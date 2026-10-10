MeadowTown_Mart_MapScripts::
	.byte 0

MeadowTown_Mart_EventScript_Clerk::
	lock
	faceplayer
	message gText_HowMayIServeYou
	waitmessage
	pokemart MeadowTown_Mart_Pokemart
	msgbox gText_PleaseComeAgain, MSGBOX_DEFAULT
	release
	end

	.align 2
MeadowTown_Mart_Pokemart:
	.2byte ITEM_ORAN_BERRY
	.2byte ITEM_SUN_STONE
	pokemartlistend

MeadowTown_Mart_Text_Welcome:
	.string "Welcome to the meadow!\n"
	.string "We sell berries and stones.\p"
	.string "Have a nice day!$"
