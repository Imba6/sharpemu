#include <stdint.h>

int sceKernelDlsym(
    int module_handle,
    const char *symbol,
    void **addrp
);

typedef int (*vps5_dlsym_fn)(
    int module_handle,
    const char *symbol,
    void **addrp
);

typedef uint64_t (*vps5_probe_fn)(void);

#define VPS5_DLSYM_PROBE_MAGIC 0x56505335444C5359ULL

/*
 * Global on purpose: the dynamic lookup below must find this symbol in
 * the guest image and return an address we can call back into.
 */
uint64_t vps5_dlsym_probe(void)
{
    return VPS5_DLSYM_PROBE_MAGIC;
}

int main(void)
{
    void *dlsym_addr = 0;
    void *probe_addr = 0;
    void *missing_addr = 0;

    /*
     * Mirror the LinkDev bootstrap shape: sceKernelDlsym must be able to
     * resolve itself through the libkernel module handle.
     */
    if (sceKernelDlsym(0x2001, "sceKernelDlsym", &dlsym_addr) != 0) {
        __builtin_trap();
    }

    if (dlsym_addr == 0) {
        __builtin_trap();
    }

    /*
     * The returned pointer must itself perform dynamic lookups.
     */
    if (((vps5_dlsym_fn)dlsym_addr)(
            0x2001,
            "vps5_dlsym_probe",
            &probe_addr) != 0) {
        __builtin_trap();
    }

    if (probe_addr == 0) {
        __builtin_trap();
    }

    /*
     * The resolved address must be callable and reach the right function.
     */
    if (((vps5_probe_fn)probe_addr)() != VPS5_DLSYM_PROBE_MAGIC) {
        __builtin_trap();
    }

    /*
     * Unknown symbols must still fail without touching the output slot.
     */
    if (((vps5_dlsym_fn)dlsym_addr)(
            0x2001,
            "vps5_dlsym_missing_symbol",
            &missing_addr) == 0) {
        __builtin_trap();
    }

    if (missing_addr != 0) {
        __builtin_trap();
    }

    return 0;
}
