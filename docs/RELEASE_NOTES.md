## Poke Studio Customizer: test release

An early version for testers. Please read the [README](https://github.com/channancy061/poke-studio-customizer#readme)
and the [testing guide](https://github.com/channancy061/poke-studio-customizer/blob/main/docs/TESTING.md) first.

**Downloads**
- Windows: `PokeStudio-<version>-win-x64-setup.exe` (no administrator rights needed; SmartScreen may warn because the app isn't code-signed yet).
- Linux: `PokeStudio-<version>-x86_64.AppImage` (`chmod +x`, then run it).

**New in v0.1.3**
- Unmodified Pokémon FireRed (US 1.0) and Emerald (US) ROMs can now have adaptive boss slots: when the battle starts,
  one Pokémon is swapped for the candidate that best counters the player's team. Tested in an emulator on both games.
- Builds say when they're done, and the Player's Guide is saved next to the built game.
- The Player's Guide lists abilities, moves, natures and items, includes every gym leader, explains adaptive bosses
  and has sections you can open and close.

**Best supported:** pokeemerald-expansion 1.17.x and pokefirered-expansion 1.15.x projects. Every editor works, and
projects with every game-code feature switched on build into working games. GBA ROMs (Emerald, FireRed, LeafGreen,
Ruby, Sapphire) are close behind. DS, 3DS and Game Boy Color support is experimental.

**Not yet verified:** full playthroughs in an emulator. That's what this test round is for.

The app contains no game files. Use games you own, and never share ROMs or built games.
