#include <stdint.h>

int sceKernelDlsym(
    int module_handle,
    const char *symbol,
    void **addrp
);

typedef int (*vps5_wrapper_fn)(void);

/*
 * FreeBSD/PS5 syscall numbers.  SYS_getpid is mapped by the runtime; the
 * probe number below is deliberately outside anything the kernel defines.
 */
#define VPS5_SYS_WRITE         4
#define VPS5_SYS_GETPID        20
#define VPS5_SYS_UNSUPPORTED   0x7FFF
#define VPS5_ENOSYS            78
#define VPS5_EBADF             9
#define VPS5_STDOUT            1
#define VPS5_BAD_FD            (-1)

/*
 * The +0x0A raw entry of a libkernel syscall wrapper behaves like the shared
 * `syscall` instruction: number in RAX, arguments in RDI/RSI/RDX/R10/R8/R9,
 * carry set on failure with the errno in RAX.
 */
static long vps5_raw_syscall(
    void *gate,
    long nr,
    long a0,
    long a1,
    long a2,
    long a3,
    long a4,
    long a5,
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

int main(void)
{
    void *wrapper = 0;
    void *raw_entry = 0;
    long wrapper_pid;
    long raw_pid;
    long unsupported;
    long written;
    long write_failure;
    static const char message[] = "hle-raw-syscall: write via raw gateway\n";
    const long message_length = (long)(sizeof(message) - 1);
    int failed = -1;

    /*
     * Obtain a libkernel syscall wrapper dynamically, exactly as a PS5 CRT
     * does.  Nothing here is specific to this symbol: any wrapper the runtime
     * knows to be a syscall wrapper carries the same two entries.
     */
    if (sceKernelDlsym(0x2001, "getpid", &wrapper) != 0) {
        __builtin_trap();
    }

    if (wrapper == 0) {
        __builtin_trap();
    }

    /*
     * The wrapper entry keeps the ordinary HLE export ABI.
     */
    wrapper_pid = (long)((vps5_wrapper_fn)wrapper)();
    if (wrapper_pid <= 0) {
        __builtin_trap();
    }

    /*
     * +0x0A is the raw syscall entry a CRT reaches by skipping the 10-byte
     * wrapper prologue.
     */
    raw_entry = (void *)((unsigned char *)wrapper + 0x0A);

    /*
     * A supported syscall must succeed through the raw entry and agree with
     * the wrapper call.
     */
    raw_pid = vps5_raw_syscall(
        raw_entry, VPS5_SYS_GETPID, 0, 0, 0, 0, 0, 0, &failed);
    if (failed != 0) {
        __builtin_trap();
    }

    if (raw_pid != wrapper_pid) {
        __builtin_trap();
    }

    /*
     * A different syscall number through the very same entry: the gateway
     * dispatches on RAX, not on which wrapper the CRT happened to resolve.
     */
    failed = -1;
    written = vps5_raw_syscall(
        raw_entry,
        VPS5_SYS_WRITE,
        VPS5_STDOUT,
        (long)(long *)(void *)message,
        message_length,
        0, 0, 0,
        &failed);
    if (failed != 0) {
        __builtin_trap();
    }

    if (written != message_length) {
        __builtin_trap();
    }

    /*
     * A failing syscall must report the FreeBSD way: carry set with the errno
     * in RAX, rather than the -1/errno pair libc hands to C callers.
     */
    failed = -1;
    write_failure = vps5_raw_syscall(
        raw_entry,
        VPS5_SYS_WRITE,
        VPS5_BAD_FD,
        (long)(long *)(void *)message,
        message_length,
        0, 0, 0,
        &failed);
    if (failed != 1) {
        __builtin_trap();
    }

    if (write_failure != VPS5_EBADF) {
        __builtin_trap();
    }

    /*
     * The same entry must dispatch by number, not by which wrapper produced
     * it: an unmapped number fails deterministically with carry set and
     * ENOSYS in RAX, and never reports success.
     */
    failed = -1;
    unsupported = vps5_raw_syscall(
        raw_entry, VPS5_SYS_UNSUPPORTED, 0, 0, 0, 0, 0, 0, &failed);
    if (failed != 1) {
        __builtin_trap();
    }

    if (unsupported != VPS5_ENOSYS) {
        __builtin_trap();
    }

    return 0;
}
