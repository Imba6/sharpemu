PS5_PAYLOAD_SDK ?= /opt/ps5-payload-sdk

include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk

.RECIPEPREFIX := >

ELF := crt-smoke.elf

CFLAGS := \
  -Wall \
  -Wextra \
  -Werror \
  -Wno-unused-command-line-argument \
  -O0 \
  -g \
  -ffreestanding \
  -fno-builtin \
  -fno-stack-protector

LDFLAGS := \
  -nostdlib \
  -nostartfiles \
  -nodefaultlibs \
  -Wl,-e,_start

all: $(ELF)

$(ELF): crt0.S main.c
>$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $^

clean:
>rm -f $(ELF) crt-smoke.gen5.elf