#include <stddef.h>
#include <stdint.h>

int sceRandomGetRandomNumber(uint8_t *buffer, size_t size);

static volatile uint8_t g_random_sink;

int main(void)
{
    uint8_t buffer[16] = {0};

    int result = sceRandomGetRandomNumber(
        buffer,
        sizeof(buffer)
    );

    if (result != 0) {
        __builtin_trap();
    }

    uint8_t combined = 0;

    for (size_t i = 0; i < sizeof(buffer); i++) {
        combined |= buffer[i];
    }

    /*
     * Practically impossible for 16 random bytes to all be zero.
     * More importantly, this checks that HLE actually wrote into
     * guest memory.
     */
    if (combined == 0) {
        __builtin_trap();
    }

    g_random_sink = combined;

    return 0;
}