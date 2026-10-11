@ Poke Studio Customizer (MIT): Emerald US entry. The hook replaced, in CB2_InitBattleInternal,
@   bl CreateNPCTrainerParty   (0x080369C4)   r0 = party, r1 = trainer A, r2 = 1 (set by the game)
@   ldr r0, [r5, #0]           (0x080369C8)   r5 = &gBattleTypeFlags
@   movs r1, #128              (0x080369CA)
@ with a jump here; we make the call, run the adaptive slot, redo the two instructions, and continue at 0x080369CC.
    .syntax unified
    .thumb
    .text
    .global ps_entry
    .thumb_func
ps_entry:
    ldr r3, =0x080385E9         @ CreateNPCTrainerParty
    bl 1f
    ldr r0, =0xDEADBEEF         @ the app writes the table's address here
    bl PokeStudio_AdaptiveSlot  @ keeps r4–r7 (the game still needs r4 and r5)
    ldr r0, [r5, #0]
    movs r1, #128
    ldr r3, =0x080369CD         @ back into the game
    bx r3
1:  bx r3
    .ltorg
