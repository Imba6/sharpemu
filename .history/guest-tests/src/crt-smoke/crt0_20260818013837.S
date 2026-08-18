.intel_syntax noprefix

.global _start
.type _start, @function

.extern main

_start:
    push rbp
    mov rbp, rsp

    call main

    pop rbp
    ret