#include <stdint.h>

int sceKernelDlsym(int module_handle, const char *symbol, void **addrp);

/* FreeBSD sysctl MIB constants (sys/sysctl.h). */
#define CTL_KERN        1
#define CTL_HW          6
#define HW_PAGESIZE     7

#define SYS___SYSCTL    202

/* FreeBSD errno subset. */
#define VPS5_EPERM      1
#define VPS5_ENOENT     2
#define VPS5_ENOMEM     12
#define VPS5_EFAULT     14

#define ORBIS_PAGE_SIZE 0x4000

/*
 * Raw syscall veneer entry (+0x0A of a libkernel syscall wrapper): number in
 * RAX, arguments in RDI/RSI/RDX/R10/R8/R9, carry set on failure with errno in
 * RAX.
 */
static long vps5_raw_syscall(
    void *gate, long nr,
    long a0, long a1, long a2, long a3, long a4, long a5,
    int *failed)
{
    long result;
    unsigned char carry;
    register long r10 __asm__("r10") = a3;
    register long r8 __asm__("r8") = a4;
    register long r9 __asm__("r9") = a5;

    __asm__ volatile(
        "call *%[gate]\n\t"
        "setc %[carry]"
        : "=a"(result), [carry] "=q"(carry)
        : "a"(nr),
          "D"(a0), "S"(a1), "d"(a2), "r"(r10), "r"(r8), "r"(r9),
          [gate] "m"(gate)
        : "rcx", "r11", "memory", "cc");

    *failed = carry;
    return result;
}

static long sysctl_raw(
    void *gate, const int *name, unsigned int namelen,
    void *oldp, unsigned long *oldlenp, int *failed)
{
    return vps5_raw_syscall(
        gate, SYS___SYSCTL,
        (long)name, (long)namelen, (long)oldp, (long)oldlenp, 0, 0,
        failed);
}

int main(void)
{
    void *wrapper = 0;
    void *gate;
    int failed;
    long rc;

    /* Obtain the shared raw-syscall entry the way a PS5 CRT does. */
    if (sceKernelDlsym(0x2001, "getpid", &wrapper) != 0 || wrapper == 0) {
        __builtin_trap();
    }
    gate = (void *)((unsigned char *)wrapper + 0x0A);

    /* 1. A known supported FreeBSD MIB: hw.pagesize reads a 4-byte int. */
    {
        int mib[2] = { CTL_HW, HW_PAGESIZE };
        unsigned long len = sizeof(int);
        int value = 0;

        failed = -1;
        rc = sysctl_raw(gate, mib, 2, &value, &len, &failed);
        if (failed != 0 || rc != 0) {
            __builtin_trap();
        }
        if (len != sizeof(int) || value != ORBIS_PAGE_SIZE) {
            __builtin_trap();
        }
    }

    /* 2. Size query: oldp == NULL reports the size without copying. */
    {
        int mib[2] = { CTL_HW, HW_PAGESIZE };
        unsigned long len = 0xdead;

        failed = -1;
        rc = sysctl_raw(gate, mib, 2, 0, &len, &failed);
        if (failed != 0 || rc != 0) {
            __builtin_trap();
        }
        if (len != sizeof(int)) {
            __builtin_trap();
        }
    }

    /* 3. Buffer too small: report the needed size and fail with ENOMEM. */
    {
        int mib[2] = { CTL_HW, HW_PAGESIZE };
        unsigned long len = 2;
        char small = 0;

        failed = -1;
        rc = sysctl_raw(gate, mib, 2, &small, &len, &failed);
        if (failed != 1 || rc != VPS5_ENOMEM) {
            __builtin_trap();
        }
        if (len != sizeof(int)) {
            __builtin_trap();
        }
    }

    /* 4. Unknown MIB fails with ENOENT and never fakes success. */
    {
        int mib[2] = { CTL_HW, 99 };
        unsigned long len = sizeof(int);
        int value = 0x5a;

        failed = -1;
        rc = sysctl_raw(gate, mib, 2, &value, &len, &failed);
        if (failed != 1 || rc != VPS5_ENOENT) {
            __builtin_trap();
        }
    }

    /* 4b. newp == NULL means "read", so a nonzero newlen must be ignored (real
     *     callers leave garbage in newlen's high bits). */
    {
        int mib[2] = { CTL_HW, HW_PAGESIZE };
        unsigned long len = sizeof(int);
        int value = 0;

        failed = -1;
        rc = vps5_raw_syscall(
            gate, SYS___SYSCTL,
            (long)mib, 2, (long)&value, (long)&len,
            0, 0x00007fff00000000L, &failed);
        if (failed != 0 || rc != 0 || value != ORBIS_PAGE_SIZE) {
            __builtin_trap();
        }
    }

    /* 5. The firmware-version OID {1,46} is deliberately unsupported. */
    {
        int mib[2] = { CTL_KERN, 46 };
        unsigned long len = sizeof(long);
        long value = 0;

        failed = -1;
        rc = sysctl_raw(gate, mib, 2, &value, &len, &failed);
        if (failed != 1 || rc != VPS5_ENOENT) {
            __builtin_trap();
        }
    }

    /* 6. Guest pointer validation: an unmapped name pointer is EFAULT. */
    {
        unsigned long len = sizeof(int);
        int value = 0;

        failed = -1;
        rc = sysctl_raw(
            gate, (const int *)(uintptr_t)0x0000424200000000ULL, 2,
            &value, &len, &failed);
        if (failed != 1 || rc != VPS5_EFAULT) {
            __builtin_trap();
        }
    }

    /* 7. Zero namelen is invalid input, not an empty success. */
    {
        int mib[1] = { CTL_HW };
        unsigned long len = sizeof(int);
        int value = 0;

        failed = -1;
        rc = sysctl_raw(gate, mib, 0, &value, &len, &failed);
        if (failed != 1) {
            __builtin_trap();
        }
    }

    return 0;
}
