#include <stddef.h>
#include <stdint.h>

#define WIDTH  64
#define HEIGHT 64
#define SQUARE_SIZE 8

#define USER_ID 0x10000000

#define VIDEOOUT_PIXEL_FORMAT_A8R8G8B8_SRGB 0x80000000u
#define VIDEOOUT_TILING_MODE_LINEAR          1u

#define PAD_BUTTON_UP      0x0010u
#define PAD_BUTTON_RIGHT   0x0020u
#define PAD_BUTTON_DOWN    0x0040u
#define PAD_BUTTON_LEFT    0x0080u
#define PAD_BUTTON_CIRCLE  0x2000u
#define PAD_BUTTON_CROSS   0x4000u

typedef struct {
    uint8_t bytes[0x28];
} videoout_buffer_attribute_t;

typedef struct {
    uint8_t bytes[0x78];
} pad_data_t;

static uint32_t framebuffer[WIDTH * HEIGHT]
    __attribute__((aligned(4096)));

int sceVideoOutOpen(
    int user_id,
    int bus_type,
    int index,
    const void *param
);

int sceVideoOutClose(
    int handle
);

int sceVideoOutSetBufferAttribute(
    videoout_buffer_attribute_t *attribute,
    uint32_t pixel_format,
    uint32_t tiling_mode,
    uint32_t aspect_ratio,
    uint32_t width,
    uint32_t height,
    uint32_t pitch_in_pixel
);

int sceVideoOutRegisterBuffers(
    int handle,
    int start_index,
    void * const *addresses,
    int buffer_num,
    const videoout_buffer_attribute_t *attribute
);

int sceVideoOutSubmitFlip(
    int handle,
    int buffer_index,
    int flip_mode,
    int64_t flip_arg
);

int scePadInit(void);

int scePadOpen(
    int user_id,
    int type,
    int index,
    const void *param
);

int scePadReadState(
    int handle,
    pad_data_t *data
);

int sceKernelUsleep(
    uint32_t microseconds
);

static uint32_t read_u32(
    const uint8_t *p
)
{
    return
        ((uint32_t)p[0]) |
        ((uint32_t)p[1] << 8) |
        ((uint32_t)p[2] << 16) |
        ((uint32_t)p[3] << 24);
}

static void clear_framebuffer(
    uint32_t color
)
{
    volatile uint32_t *pixels =
        (volatile uint32_t *)framebuffer;

    for (size_t i = 0; i < WIDTH * HEIGHT; i++) {
        pixels[i] = color;
    }
}

static void draw_square(
    int x,
    int y,
    uint32_t color
)
{
    volatile uint32_t *pixels =
        (volatile uint32_t *)framebuffer;

    for (int py = 0; py < SQUARE_SIZE; py++) {
        for (int px = 0; px < SQUARE_SIZE; px++) {
            pixels[(y + py) * WIDTH + (x + px)] = color;
        }
    }
}

int main(void)
{
    if (scePadInit() != 0) {
        __builtin_trap();
    }

    int pad_handle =
        scePadOpen(
            USER_ID,
            0,
            0,
            0
        );

    if (pad_handle <= 0) {
        __builtin_trap();
    }

    int video_handle =
        sceVideoOutOpen(
            0,
            0,
            0,
            0
        );

    if (video_handle <= 0) {
        __builtin_trap();
    }

    videoout_buffer_attribute_t attribute;

    if (sceVideoOutSetBufferAttribute(
            &attribute,
            VIDEOOUT_PIXEL_FORMAT_A8R8G8B8_SRGB,
            VIDEOOUT_TILING_MODE_LINEAR,
            0,
            WIDTH,
            HEIGHT,
            WIDTH) != 0) {
        __builtin_trap();
    }

    void *addresses[1];
    addresses[0] = framebuffer;

    if (sceVideoOutRegisterBuffers(
            video_handle,
            0,
            addresses,
            1,
            &attribute) < 0) {
        __builtin_trap();
    }

    int x =
        (WIDTH - SQUARE_SIZE) / 2;

    int y =
        (HEIGHT - SQUARE_SIZE) / 2;

    uint32_t square_color =
        0xFFFF0000u;

    uint32_t previous_buttons = 0;

    /*
     * Safety timeout keeps unattended runs bounded.
     * 60 seconds at roughly 60 Hz.
     */
    for (int frame = 0; frame < 3600; frame++) {
        pad_data_t pad;

        if (scePadReadState(
                pad_handle,
                &pad) != 0) {
            __builtin_trap();
        }

        uint32_t buttons =
            read_u32(
                &pad.bytes[0x00]
            );

        uint8_t left_x =
            pad.bytes[0x04];

        uint8_t left_y =
            pad.bytes[0x05];

        if ((buttons & PAD_BUTTON_LEFT) != 0 ||
            left_x < 96) {
            x--;
        }

        if ((buttons & PAD_BUTTON_RIGHT) != 0 ||
            left_x > 160) {
            x++;
        }

        if ((buttons & PAD_BUTTON_UP) != 0 ||
            left_y < 96) {
            y--;
        }

        if ((buttons & PAD_BUTTON_DOWN) != 0 ||
            left_y > 160) {
            y++;
        }

        if (x < 0) {
            x = 0;
        }

        if (y < 0) {
            y = 0;
        }

        if (x > WIDTH - SQUARE_SIZE) {
            x = WIDTH - SQUARE_SIZE;
        }

        if (y > HEIGHT - SQUARE_SIZE) {
            y = HEIGHT - SQUARE_SIZE;
        }

        if ((buttons & PAD_BUTTON_CROSS) != 0 &&
            (previous_buttons & PAD_BUTTON_CROSS) == 0) {
            square_color =
                square_color == 0xFFFF0000u
                    ? 0xFF00FF00u
                    : 0xFFFF0000u;
        }

        if ((buttons & PAD_BUTTON_CIRCLE) != 0) {
            break;
        }

        previous_buttons =
            buttons;

        clear_framebuffer(
            0xFF101018u
        );

        draw_square(
            x,
            y,
            square_color
        );

        if (sceVideoOutSubmitFlip(
                video_handle,
                0,
                0,
                frame) != 0) {
            __builtin_trap();
        }

        if (sceKernelUsleep(
                16666) != 0) {
            __builtin_trap();
        }
    }

    if (sceVideoOutClose(
            video_handle) != 0) {
        __builtin_trap();
    }

    return 0;
}
