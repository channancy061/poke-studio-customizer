@ Poke Studio Customizer (MIT): FireRed US 1.0 entry. The hook replaced, in CB2_InitBattleInternal,
@   bl CreateNPCTrainerParty   (0x0800FF90)
@   bl SetWildMonHeldItem      (0x0800FF94)
@ with a jump here; we make both calls, run the adaptive slot, and continue at 0x0800FF98.
    .syntax unified
    .thumb
    .text
    .global ps_entry
    .thumb_func
ps_entry:
    ldr r3, =0x080112E1         @ CreateNPCTrainerParty (r0 = party, r1 = trainer: set by the game)
    bl 1f
    ldr r3, =0x080443F5         @ SetWildMonHeldItem
    bl 1f
    ldr r0, =0xDEADBEEF         @ the app writes the table's address here
    bl PokeStudio_AdaptiveSlot
    ldr r3, =0x0800FF99         @ back into the game
    bx r3
1:  bx r3
    .ltorg
