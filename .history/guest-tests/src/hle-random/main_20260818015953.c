#include <stddef.h>
#include <stdint.h>

int sceRandomGetRandomNumber(uint8_t *buffer, size_t size);

static volatile uint8_t g_random_sink;

int main(void)
{
    uint8_t buffer[16];

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
     * Якщо HLE нічого не записав, buffer фактично некоректний для
     * цього тесту. Ймовірність отримати 16 справжніх нульових
     * random bytes ≈ 1 / 2^128.
     */
    if (combined == 0) {
        __builtin_trap();
    }

    g_random_sink = combined;

    return 0;
}