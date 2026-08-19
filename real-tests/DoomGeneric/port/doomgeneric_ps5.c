// VirtualPS5 platform backend for DoomGeneric.
//
// Implements the small DG_* platform interface against the PS5 userspace ABI
// (sceVideoOut for the framebuffer, scePad for input, libkernel for timing),
// running under the VirtualPS5-compatible CRT (our crt0, no payload runtime).
//
// M1 scope: open VideoOut + Pad, present DoomGeneric's software-rendered frame
// through a LINEAR CPU scan-out buffer, and feed basic controller input. No sound.

#include <stdint.h>
#include <string.h>

#include "doomgeneric.h"
#include "doomkeys.h"

// ---------------------------------------------------------------------------
// PS5 userspace ABI prototypes (resolved by VirtualPS5 HLE at runtime). Declared
// locally so the port stays self-contained, matching the synthetic guest style.
// ---------------------------------------------------------------------------

#define VIDEOOUT_PIXEL_FORMAT_A8R8G8B8_SRGB 0x80000000u
#define VIDEOOUT_TILING_MODE_LINEAR         1u

typedef struct { uint8_t bytes[0x28]; } videoout_buffer_attribute_t;

int sceVideoOutOpen(int user_id, int bus_type, int index, const void *param);
int sceVideoOutClose(int handle);
int sceVideoOutSetBufferAttribute(videoout_buffer_attribute_t *attribute,
                                  uint32_t pixel_format, uint32_t tiling_mode,
                                  uint32_t aspect_ratio, uint32_t width,
                                  uint32_t height, uint32_t pitch_in_pixel);
int sceVideoOutRegisterBuffers(int handle, int start_index,
                               void *const *addresses, int buffer_num,
                               const videoout_buffer_attribute_t *attribute);
int sceVideoOutSubmitFlip(int handle, int buffer_index, int flip_mode,
                          int64_t flip_arg);

int scePadInit(void);
int scePadOpen(int user_id, int type, int index, const void *param);
int scePadReadState(int handle, void *data);

int sceUserServiceInitialize(const void *params);
int sceUserServiceGetInitialUser(int *user_id);

int sceKernelUsleep(uint32_t microseconds);
uint64_t sceKernelGetProcessTime(void);

// ---------------------------------------------------------------------------
// Output framebuffer
// ---------------------------------------------------------------------------

// DoomGeneric renders at 640x400, 32bpp. A8R8G8B8 stores bytes B,G,R,A, which
// matches DoomGeneric's 0x00RRGGBB pixel word in little-endian, so the frame can
// be copied straight across (forcing alpha opaque). Present the native Doom
// resolution as a linear scan-out buffer; the emulator scales it to the window.
#define FB_WIDTH  DOOMGENERIC_RESX
#define FB_HEIGHT DOOMGENERIC_RESY

static uint32_t s_framebuffer[FB_WIDTH * FB_HEIGHT] __attribute__((aligned(0x1000)));

static int s_video_handle = -1;
static int s_pad_handle = -1;
static int64_t s_flip_arg = 0;

// ---------------------------------------------------------------------------
// Input: edge-detect PS5 pad buttons into a DoomGeneric key event queue.
// ---------------------------------------------------------------------------

#define PAD_UP       0x0010u
#define PAD_RIGHT    0x0020u
#define PAD_DOWN     0x0040u
#define PAD_LEFT     0x0080u
#define PAD_OPTIONS  0x0008u
#define PAD_TRIANGLE 0x1000u
#define PAD_CIRCLE   0x2000u
#define PAD_CROSS    0x4000u
#define PAD_SQUARE   0x8000u
#define PAD_L1       0x0400u
#define PAD_R1       0x0800u

typedef struct {
    uint32_t pad_bit;
    unsigned char doom_key;
} pad_mapping_t;

static const pad_mapping_t s_pad_map[] = {
    { PAD_UP,       KEY_UPARROW    },
    { PAD_DOWN,     KEY_DOWNARROW  },
    { PAD_LEFT,     KEY_LEFTARROW  },
    { PAD_RIGHT,    KEY_RIGHTARROW },
    { PAD_CROSS,    KEY_ENTER      },
    { PAD_CIRCLE,   KEY_ESCAPE     },
    { PAD_SQUARE,   KEY_FIRE       },
    { PAD_TRIANGLE, KEY_USE        },
    { PAD_OPTIONS,  KEY_ESCAPE     },
    { PAD_L1,       KEY_STRAFE_L   },
    { PAD_R1,       KEY_STRAFE_R   },
};

#define PAD_MAP_COUNT (sizeof(s_pad_map) / sizeof(s_pad_map[0]))

#define KEY_QUEUE_SIZE 32
typedef struct { int pressed; unsigned char key; } key_event_t;
static key_event_t s_key_queue[KEY_QUEUE_SIZE];
static unsigned int s_key_write = 0;
static unsigned int s_key_read = 0;
static uint32_t s_prev_buttons = 0;

static void enqueue_key(int pressed, unsigned char key)
{
    unsigned int next = (s_key_write + 1) % KEY_QUEUE_SIZE;
    if (next == s_key_read) {
        return; // queue full: drop oldest-safe (avoid overwriting unread)
    }
    s_key_queue[s_key_write].pressed = pressed;
    s_key_queue[s_key_write].key = key;
    s_key_write = next;
}

// Poll the pad once and turn button transitions into key press/release events.
static void poll_input(void)
{
    if (s_pad_handle <= 0) {
        return;
    }

    uint8_t data[0x78];
    memset(data, 0, sizeof(data));
    if (scePadReadState(s_pad_handle, data) != 0) {
        return;
    }

    uint32_t buttons = (uint32_t)data[0] | ((uint32_t)data[1] << 8) |
                       ((uint32_t)data[2] << 16) | ((uint32_t)data[3] << 24);
    uint32_t changed = buttons ^ s_prev_buttons;
    if (changed != 0) {
        for (unsigned int i = 0; i < PAD_MAP_COUNT; i++) {
            uint32_t bit = s_pad_map[i].pad_bit;
            if (changed & bit) {
                enqueue_key((buttons & bit) ? 1 : 0, s_pad_map[i].doom_key);
            }
        }
    }
    s_prev_buttons = buttons;
}

// ---------------------------------------------------------------------------
// DoomGeneric platform interface
// ---------------------------------------------------------------------------

void DG_Init(void)
{
    // Applications launched by the system may find the user service already
    // started; the return code reports that without blocking the launch.
    int user_id = 0;
    sceUserServiceInitialize(0);
    sceUserServiceGetInitialUser(&user_id);

    s_video_handle = sceVideoOutOpen(0, 0, 0, 0);

    if (s_video_handle > 0) {
        videoout_buffer_attribute_t attribute;
        sceVideoOutSetBufferAttribute(&attribute,
                                      VIDEOOUT_PIXEL_FORMAT_A8R8G8B8_SRGB,
                                      VIDEOOUT_TILING_MODE_LINEAR,
                                      0, FB_WIDTH, FB_HEIGHT, FB_WIDTH);
        void *addresses[1];
        addresses[0] = s_framebuffer;
        sceVideoOutRegisterBuffers(s_video_handle, 0, addresses, 1, &attribute);
    }

    scePadInit();
    s_pad_handle = scePadOpen(0x10000000, 0, 0, 0);
}

void DG_DrawFrame(void)
{
    // Copy the completed Doom frame into the scan-out buffer, forcing alpha
    // opaque, then present it.
    if (DG_ScreenBuffer != 0) {
        const uint32_t *src = DG_ScreenBuffer;
        uint32_t *dst = s_framebuffer;
        for (int i = 0; i < FB_WIDTH * FB_HEIGHT; i++) {
            dst[i] = src[i] | 0xFF000000u;
        }
    }

    if (s_video_handle > 0) {
        sceVideoOutSubmitFlip(s_video_handle, 0, 0, s_flip_arg++);
    }

    poll_input();
}

void DG_SleepMs(uint32_t ms)
{
    sceKernelUsleep(ms * 1000u);
}

uint32_t DG_GetTicksMs(void)
{
    return (uint32_t)(sceKernelGetProcessTime() / 1000ull);
}

int DG_GetKey(int *pressed, unsigned char *key)
{
    if (s_key_read == s_key_write) {
        return 0;
    }

    *pressed = s_key_queue[s_key_read].pressed;
    *key = s_key_queue[s_key_read].key;
    s_key_read = (s_key_read + 1) % KEY_QUEUE_SIZE;
    return 1;
}

void DG_SetWindowTitle(const char *title)
{
    (void)title;
}

// The VirtualPS5 CRT (crt0) enters main without a usable argc/argv, so build the
// argument vector here. The WAD is looked up under the application root
// (/app0 maps to the eboot.bin directory); the human places a freely
// redistributable WAD (doom1.wad / freedoom) there.
int main(void)
{
    static char arg0[] = "doom";
    static char arg1[] = "-iwad";
    static char arg2[] = "/app0/doom1.wad";
    static char *argv[] = { arg0, arg1, arg2, 0 };

    doomgeneric_Create(3, argv);

    // D_DoomMain normally runs the loop and never returns; keep ticking if it does.
    for (;;) {
        doomgeneric_Tick();
    }

    return 0;
}
