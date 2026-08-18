#include <stddef.h>
#include <stdint.h>

typedef struct {
    uint8_t bytes[12];
} system_status_t;

int sceSystemServiceGetStatus(
    system_status_t *status
);

int sceSystemServiceParamGetInt(
    int parameter_id,
    int *value
);

int sceSystemServiceParamGetString(
    int parameter_id,
    char *buffer,
    size_t size
);

static volatile uint64_t g_sink;

int main(void)
{
    system_status_t status;

    if (sceSystemServiceGetStatus(
            &status) != 0) {
        __builtin_trap();
    }

    /*
     * Current VirtualPS5 baseline status.
     */
    if (status.bytes[6] != 1) {
        __builtin_trap();
    }

    int value = -1;

    /*
     * Parameter 4 currently maps to 180.
     */
    if (sceSystemServiceParamGetInt(
            4,
            &value) != 0) {
        __builtin_trap();
    }

    if (value != 180) {
        __builtin_trap();
    }

    int flag = -1;

    if (sceSystemServiceParamGetInt(
            1,
            &flag) != 0) {
        __builtin_trap();
    }

    if (flag != 1) {
        __builtin_trap();
    }

    char text[32];

    if (sceSystemServiceParamGetString(
            0,
            text,
            sizeof(text)) != 0) {
        __builtin_trap();
    }

    const char *expected = "VirtualPS5";

    size_t i = 0;

    while (expected[i] != '\0') {
        if (text[i] != expected[i]) {
            __builtin_trap();
        }

        i++;
    }

    if (text[i] != '\0') {
        __builtin_trap();
    }

    g_sink =
        status.bytes[6] ^
        (uint64_t)value ^
        (uint64_t)flag ^
        (uint64_t)text[0];

    return 0;
}