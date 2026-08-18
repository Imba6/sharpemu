.intel_syntax noprefix

.global _start
.type _start, @function

.extern main

_start:
    push rbp
    mov rbp, rsp

    call main

    # SharpEmu expects guest entrypoint to return 0.
    xor eax, eax

    pop rbp
    ret