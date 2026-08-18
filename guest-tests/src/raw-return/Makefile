PS5_PAYLOAD_SDK ?= /opt/ps5-payload-sdk

include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk

.RECIPEPREFIX := >

ELF := raw-return.elf

all: $(ELF)

$(ELF): start.S
>$(CC) \
>  -nostdlib \
>  -nostartfiles \
>  -nodefaultlibs \
>  -Wl,-e,_start \
>  -o $@ $^

clean:
>rm -f $(ELF)