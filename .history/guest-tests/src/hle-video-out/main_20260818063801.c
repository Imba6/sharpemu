#include <stddef.h>
#include <stdint.h>

#define WIDTH  64
#define HEIGHT 64

#define VIDEOOUT_PIXEL_FORMAT_A8R8G8B8_SRGB 0x80000000u
#define VIDEOOUT_TILING_MODE_LINEAR          1u

/*
 * We intentionally keep PS ABI structures raw here.
 * The regression test checks exact byte layouts.
 */

typedef struct {
    uint8_t bytes[0x28];
} videoout_buffer_attribute_t;

typedef struct {
    uint8_t bytes[0x30];
} videoout_output_status_t;

typedef struct {
    uint8_t bytes[0x40];
} videoout_flip_status_t;


/*
 * Guest framebuffer.
 *
 * Static storage keeps it out of our small stack and gives VideoOut
 * a stable guest virtual address.
 */
static uint32_t framebuffer[WIDTH * HEIGHT];

static volatile uint64_t g_sink;


/*
 * VideoOut imports.
 */

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

int sceVideoOutGetFlipStatus(
    int handle,
    videoout_flip_status_t *status
);

int sceVideoOutGetOutputStatus(
    int handle,
    videoout_output_status_t *status
);


/*
 * Kernel import.
 */
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


static uint64_t read_u64(
    const uint8_t *p
)
{
    uint64_t value = 0;

    for (int i = 0; i < 8; i++) {
        value |=
            ((uint64_t)p[i]) << (i * 8);
    }

    return value;
}


/*
 * Volatile stores are deliberate.
 *
 * They prevent clang from replacing this loop with memset in our
 * freestanding guest and ensure that these are real guest CPU stores.
 */
static void fill_framebuffer(
    uint32_t color
)
{
    volatile uint32_t *pixels = framebuffer;

    for (size_t i = 0; i < WIDTH * HEIGHT; i++) {
        pixels[i] = color;
    }
}


int main(void)
{
    /*
     * SharpEmu currently accepts user id 0/255 for main VideoOut.
     */
    int handle = sceVideoOutOpen(
        0,      /* user id */
        0,      /* main bus */
        0,      /* index */
        0       /* param */
    );

    if (handle <= 0) {
        __builtin_trap();
    }


    /*
     * Ask VideoOut to build the PS buffer-attribute structure.
     */
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


    /*
     * Register one guest framebuffer.
     *
     * Avoid initialized local arrays so clang does not decide
     * that our freestanding binary suddenly needs memcpy.
     */
    void *addresses[1];

    addresses[0] = framebuffer;

    int group = sceVideoOutRegisterBuffers(
        handle,
        0,
        addresses,
        1,
        &attribute
    );

    if (group < 0) {
        __builtin_trap();
    }


    /*
     * Registration should update the output dimensions/state.
     */
    videoout_output_status_t output_status;

    if (sceVideoOutGetOutputStatus(
            handle,
            &output_status) != 0) {
        __builtin_trap();
    }

    uint32_t resolution_class =
        read_u32(&output_status.bytes[0x00]);

    uint32_t connected =
        read_u32(&output_status.bytes[0x04]);

    uint64_t refresh_rate =
        read_u64(&output_status.bytes[0x08]);

    if (resolution_class == 0) {
        __builtin_trap();
    }

    if (connected != 1) {
        __builtin_trap();
    }

    if (refresh_rate == 0) {
        __builtin_trap();
    }


    /*
     * INITIAL CPU FRAME
     *
     * Fill the framebuffer BEFORE write tracking is armed.
     *
     * A8R8G8B8:
     *   0xFFFF0000 = opaque red.
     */
    fill_framebuffer(
        0xFFFF0000u
    );


    /*
     * Submit framebuffer #0 exactly once.
     *
     * Vulkan should:
     *
     *   - receive the guest image address
     *   - bootstrap GuestImageResource
     *   - upload the initial RED contents
     *   - mark the image CPU-backed
     *   - arm GuestImageWriteTracker
     */
    if (sceVideoOutSubmitFlip(
            handle,
            0,
            0,
            1234) != 0) {
        __builtin_trap();
    }


    /*
     * Give the host presenter plenty of time to consume the flip,
     * materialize the GuestImageResource and arm CPU write tracking.
     */
    if (sceKernelUsleep(
            1000000) != 0) {
        __builtin_trap();
    }


    /*
     * IMPORTANT DIAGNOSTIC CONDITION:
     *
     * DO NOT WRITE TO framebuffer HERE.
     *
     * We want to know whether simply having GuestImageWriteTracker
     * armed breaks guest execution, or whether the failure happens
     * only on the first subsequent CPU store into the tracked page.
     */


    /*
     * Keep the guest alive for another second while tracking remains
     * armed and the framebuffer stays completely untouched.
     */
    if (sceKernelUsleep(
            1000000) != 0) {
        __builtin_trap();
    }


    /*
     * Verify that the original flip is still recorded correctly.
     */
    videoout_flip_status_t flip_status;

    if (sceVideoOutGetFlipStatus(
            handle,
            &flip_status) != 0) {
        __builtin_trap();
    }

    uint64_t flip_count =
        read_u64(&flip_status.bytes[0x00]);

    uint64_t current_buffer =
        read_u64(&flip_status.bytes[0x20]);

    if (flip_count == 0) {
        __builtin_trap();
    }

    if (current_buffer != 0) {
        __builtin_trap();
    }


    /*
     * Prevent useful values from being optimized away completely.
     */
    g_sink =
        (uint64_t)handle ^
        (uint64_t)group ^
        resolution_class ^
        connected ^
        refresh_rate ^
        flip_count ^
        current_buffer;


    if (sceVideoOutClose(
            handle) != 0) {
        __builtin_trap();
    }

    return 0;
}