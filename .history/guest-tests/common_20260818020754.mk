PS5_PAYLOAD_SDK ?= /opt/ps5-payload-sdk

include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk

.RECIPEPREFIX := >

# Directory containing this common.mk
VPS5_GUEST_DIR := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))

TARGET ?= guest
ELF := $(TARGET).elf
GEN5_ELF := $(TARGET).gen5.elf

CRT0 ?= $(VPS5_GUEST_DIR)/runtime/crt0.S
GEN5_PATCH := $(VPS5_GUEST_DIR)/tools/mark-gen5.py
ARTIFACT_DIR := $(VPS5_GUEST_DIR)/artifacts
ARTIFACT := $(ARTIFACT_DIR)/$(GEN5_ELF)

SOURCES ?= $(CRT0) main.c

CFLAGS += \
  -Wall \
  -Wextra \
  -Werror \
  -Wno-unused-command-line-argument \
  -O0 \
  -g \
  -ffreestanding \
  -fno-builtin \
  -fno-stack-protector

LDFLAGS += \
  -nostdlib \
  -nostartfiles \
  -nodefaultlibs \
  -Wl,-e,_start

.PHONY: all clean inspect

all: $(ARTIFACT)

$(ELF): $(SOURCES)
>$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(SOURCES) $(LDLIBS)

$(GEN5_ELF): $(ELF) $(GEN5_PATCH)
>python3 $(GEN5_PATCH) $(ELF) $(GEN5_ELF)

$(ARTIFACT): $(GEN5_ELF)
>mkdir -p $(ARTIFACT_DIR)
>cp $(GEN5_ELF) $(ARTIFACT)
>@echo ""
>@echo "========================================"
>@echo " VirtualPS5 guest build complete"
>@echo " Target:   $(TARGET)"
>@echo " ELF:      $(ELF)"
>@echo " Gen5 ELF: $(GEN5_ELF)"
>@echo " Artifact: $(ARTIFACT)"
>@echo "========================================"

inspect: $(GEN5_ELF)
>@echo ""
>@echo "=== ELF HEADER ==="
>llvm-readelf-18 -h $(GEN5_ELF) | grep -E "OS/ABI|ABI Version|Entry"
>@echo ""
>@echo "=== NEEDED LIBRARIES ==="
>llvm-readelf-18 -d $(GEN5_ELF) | grep NEEDED || true
>@echo ""
>@echo "=== UNDEFINED SYMBOLS ==="
>llvm-readelf-18 -Ws $(GEN5_ELF) | grep UND || true
>@echo ""
>@echo "=== RELOCATIONS ==="
>llvm-readelf-18 -r $(GEN5_ELF)

clean:
>rm -f $(ELF) $(GEN5_ELF) $(ARTIFACT)