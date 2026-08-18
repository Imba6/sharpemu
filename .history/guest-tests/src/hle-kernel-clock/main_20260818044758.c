#include <stdint.h>

typedef struct {
    int64_t tv_sec;
    int64_t tv_nsec;
} vps5_timespec_t;

typedef struct {
    int64_t tv_sec;
    int64_t tv_usec;
} vps5_timeval_t;

int sceKernelClockGettime(
    int clock_id,
    vps5_timespec_t *tp
);

int sceKernelGettimeofday(
    vps5_timeval_t *tv
);

int sceKernelUsleep(
    uint64_t microseconds
);

uint64_t sceKernelGetProcessTime(void);

static volatile uint64_t g_sink;

int main(void)
{
    vps5_timespec_t realtime;
    vps5_timespec_t monotonic;
    vps5_timeval_t timeval;

    realtime.tv_sec = 0;
    realtime.tv_nsec = 0;

    monotonic.tv_sec = 0;
    monotonic.tv_nsec = 0;

    timeval.tv_sec = 0;
    timeval.tv_usec = 0;

    /*
     * CLOCK_REALTIME
     */
    if (sceKernelClockGettime(
            0,
            &realtime) != 0) {
        __builtin_trap();
    }

    if (realtime.tv_sec <= 0) {
        __builtin_trap();
    }

    if (realtime.tv_nsec < 0 ||
        realtime.tv_nsec >= 1000000000LL) {
        __builtin_trap();
    }

    /*
     * Monotonic-style clock.
     */
    if (sceKernelClockGettime(
            1,
            &monotonic) != 0) {
        __builtin_trap();
    }

    if (monotonic.tv_sec < 0) {
        __builtin_trap();
    }

    if (monotonic.tv_nsec < 0 ||
        monotonic.tv_nsec >= 1000000000LL) {
        __builtin_trap();
    }

    /*
     * gettimeofday must write guest memory too.
     */
    if (sceKernelGettimeofday(
            &timeval) != 0) {
        __builtin_trap();
    }

    if (timeval.tv_sec <= 0) {
        __builtin_trap();
    }

    if (timeval.tv_usec < 0 ||
        timeval.tv_usec >= 1000000LL) {
        __builtin_trap();
    }

    /*
     * Verify that usleep actually advances our
     * observable process clock.
     */
    uint64_t before =
        sceKernelGetProcessTime();

    if (sceKernelUsleep(2000) != 0) {
        __builtin_trap();
    }

    uint64_t after =
        sceKernelGetProcessTime();

    if (after <= before) {
        __builtin_trap();
    }

    g_sink =
        (uint64_t)realtime.tv_sec ^
        (uint64_t)realtime.tv_nsec ^
        (uint64_t)monotonic.tv_sec ^
        (uint64_t)monotonic.tv_nsec ^
        (uint64_t)timeval.tv_sec ^
        (uint64_t)timeval.tv_usec ^
        before ^
        after;

    return 0;
}