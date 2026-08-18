#include <stdint.h>

uint64_t sceKernelGetProcessTime(void);
uint64_t sceKernelGetProcessTimeCounter(void);
uint64_t sceKernelGetProcessTimeCounterFrequency(void);

static volatile uint64_t g_sink;

int main(void)
{
    uint64_t frequency =
        sceKernelGetProcessTimeCounterFrequency();

    if (frequency == 0) {
        __builtin_trap();
    }

    uint64_t counter1 =
        sceKernelGetProcessTimeCounter();

    uint64_t time1 =
        sceKernelGetProcessTime();

    /*
     * Do a little guest-side work so the second readings
     * should not move backwards.
     */
    volatile uint64_t value = 0;

    for (uint64_t i = 0; i < 10000; i++) {
        value += i;
    }

    uint64_t counter2 =
        sceKernelGetProcessTimeCounter();

    uint64_t time2 =
        sceKernelGetProcessTime();

    if (counter2 < counter1) {
        __builtin_trap();
    }

    if (time2 < time1) {
        __builtin_trap();
    }

    g_sink =
        frequency ^
        counter1 ^
        counter2 ^
        time1 ^
        time2 ^
        value;

    return 0;
}