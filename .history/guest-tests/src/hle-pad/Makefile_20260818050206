#include <stdint.h>

#define USER_ID 0x10000000

typedef struct {
    uint8_t bytes[0x78];
} pad_data_t;

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

static volatile uint64_t g_sink;

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

int main(void)
{
    /*
     * Initialize pad subsystem.
     */
    if (scePadInit() != 0) {
        __builtin_trap();
    }

    /*
     * Open primary user's standard controller.
     */
    int handle =
        scePadOpen(
            USER_ID,
            0,
            0,
            0
        );

    if (handle <= 0) {
        __builtin_trap();
    }

    pad_data_t data;

    if (scePadReadState(
            handle,
            &data) != 0) {
        __builtin_trap();
    }

    /*
     * No buttons pressed.
     */
    if (data.bytes[0x00] != 0 ||
        data.bytes[0x01] != 0 ||
        data.bytes[0x02] != 0 ||
        data.bytes[0x03] != 0) {
        __builtin_trap();
    }

    /*
     * Neutral analog sticks.
     */
    if (data.bytes[0x04] != 128 ||
        data.bytes[0x05] != 128 ||
        data.bytes[0x06] != 128 ||
        data.bytes[0x07] != 128) {
        __builtin_trap();
    }

    /*
     * Neutral triggers.
     */
    if (data.bytes[0x08] != 0 ||
        data.bytes[0x09] != 0) {
        __builtin_trap();
    }

    /*
     * Connected.
     */
    if (data.bytes[0x4C] != 1) {
        __builtin_trap();
    }

    if (data.bytes[0x68] != 1) {
        __builtin_trap();
    }

    uint64_t timestamp =
        read_u64(
            &data.bytes[0x50]
        );

    g_sink =
        (uint64_t)handle ^
        timestamp ^
        data.bytes[0x04] ^
        data.bytes[0x4C];

    return 0;
}