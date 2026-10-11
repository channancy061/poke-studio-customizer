#!/bin/bash
# Builds the injected adaptive-slot code for each supported ROM (needs arm-none-eabi-gcc), and proves it is
# position-independent: linked at two different addresses, the bytes must be identical.
# Usage (Linux / WSL, from this folder):  bash build.sh
set -e
cd "$(dirname "$0")"
CFLAGS="-mthumb -mcpu=arm7tdmi -Os -ffreestanding -fno-builtin -nostdlib -fno-jump-tables -fno-pic -Wall -Wextra -Werror"
build() { # game, -D addresses..., symbols...
  local game=$1; shift
  local defs="$1"; local syms="$2"
  arm-none-eabi-gcc $CFLAGS $defs -c adaptive.c -o /tmp/ps_adaptive_$game.o
  arm-none-eabi-gcc -mthumb -mcpu=arm7tdmi -c entry_$game.S -o /tmp/ps_entry_$game.o
  for base in 0x08800000 0x08900000; do
    arm-none-eabi-ld -nostdlib -Ttext=$base -e ps_entry $syms /tmp/ps_entry_$game.o /tmp/ps_adaptive_$game.o -o /tmp/ps_${game}_${base}.elf
    arm-none-eabi-objcopy -O binary -j .text /tmp/ps_${game}_${base}.elf /tmp/ps_${game}_${base}.bin
  done
  cmp /tmp/ps_${game}_0x08800000.bin /tmp/ps_${game}_0x08900000.bin || { echo "$game: NOT position-independent"; exit 1; }
  cp /tmp/ps_${game}_0x08800000.bin adaptive_$game.bin
  echo "$game: $(stat -c %s adaptive_$game.bin) bytes, position-independent"
}
build firered \
  "-DGAME_ENEMY_PARTY=0x0202402c -DGAME_PLAYER_PARTY=0x02024284 -DGAME_PLAYER_PARTY_COUNT=0x02024029 -DGAME_TRAINER_A=0x020386ae -DGAME_SPECIES_INFO=0x08254784 -DGAME_TYPE_CHART=0x0824f050" \
  "--defsym=CreateMon=0x0803da55 --defsym=GetMonData2=0x0803fbe9 --defsym=SetMonData=0x0804037d --defsym=SetMonMoveSlot=0x0803e965"
build emerald \
  "-DGAME_ENEMY_PARTY=0x02024744 -DGAME_PLAYER_PARTY=0x020244ec -DGAME_PLAYER_PARTY_COUNT=0x020244e9 -DGAME_TRAINER_A=0x02038bca -DGAME_SPECIES_INFO=0x083203cc -DGAME_TYPE_CHART=0x0831ace8" \
  "--defsym=CreateMon=0x08067b4d --defsym=GetMonData2=0x0806a519 --defsym=SetMonData=0x0806acad --defsym=SetMonMoveSlot=0x080691f5"
