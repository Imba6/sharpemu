#include <stddef.h>
#include <stdint.h>

#define WIDTH  64
#define HEIGHT 64

#define VIDEOOUT_PIXEL_FORMAT_A8R8G8B8_SRGB 0x80000000u
#define VIDEOOUT_TILING_MODE_LINEAR          1u

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
 * Exactly four isolated 4 KiB pages.
 */
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

int sceVideoOutGetFlipStatus(
    int handle,
    videoout_flip_status_t *status
);

int sceVideoOutGetOutputStatus(
    int handle,
    videoout_output_status_t *status
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


static uint64_t read_u64(
    const uint8_t *p
)
{
    uint64_t value = 0;

    for (int i = 0; i < 8; i++) {
        value |= ((uint64_t)p[i]) << (i * 8);
    }

    return value;
}


static void fill_framebuffer(
    uint32_t color
)
{
    volatile uint32_t *pixels =
        (volatile uint32_t *)framebuffer;

    for (size_t i = 0; i < WIDTH * HEIGHT; i++) {
        pixels[i] = color;
    }
}


int main(void)
{
    int handle = sceVideoOutOpen(
        0,
        0,
        0,
        0
    );

    if (handle <= 0) {
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

    if (resolution_class == 0 ||
        connected != 1 ||
        refresh_rate == 0) {
        __builtin_trap();
    }


    /*
     * Frame #1: RED.
     *
     * Written before tracking is armed.
     */
    fill_framebuffer(
        0xFFFF0000u
    );

    if (sceVideoOutSubmitFlip(
            handle,
            0,
            0,
            1234) != 0) {
        __builtin_trap();
    }


    /*
     * Allow Vulkan to bootstrap the image and arm tracking.
     */
    if (sceKernelUsleep(
            1000000) != 0) {
        __builtin_trap();
    }


    /*
     * Frame #2: GREEN.
     *
     * This is the native CPU write that previously stalled.
     */
    fill_framebuffer(
        0xFF00FF00u
    );


    if (sceVideoOutSubmitFlip(
            handle,
            0,
            0,
            1235) != 0) {
        __builtin_trap();
    }


    /*
     * Keep green visible.
     */
    if (sceKernelUsleep(
            1000000) != 0) {
        __builtin_trap();
    }


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

    if (flip_count < 2) {
        __builtin_trap();
    }

    if (current_buffer != 0) {
        __builtin_trap();
    }


    if (sceVideoOutClose(
            handle) != 0) {
        __builtin_trap();
    }

    return 0;
}