## Poke Studio Customizer: test release

An early version for testers. Please read the [README](https://github.com/channancy061/poke-studio-customizer#readme)
and the [testing guide](https://github.com/channancy061/poke-studio-customizer/blob/main/docs/TESTING.md) first.

**Downloads**
- Windows: `PokeStudio-<version>-win-x64-setup.exe` (no administrator rights needed; SmartScreen may warn because the app isn't code-signed yet).
- Linux: `PokeStudio-<version>-x86_64.AppImage` (`chmod +x`, then run it).

**New in v0.1.5**
- **Story battles** (decomp projects): put a trainer in front of a gym door, cave or building so the player can't skip
  the fight. Make it a major battle and it gets its own level cap.
- **Crossover packs:** one click adds Hoenn's gym leaders, Elite Four and Wallace to a FireRed hack, or Kanto's to an
  Emerald hack. Each comes with level caps and Champion Lab teams in the trainer's type. Missing sprites are copied from
  your own other expansion project. In Emerald, saves made before adding the pack won't work: start a new game.
- **Reusable TMs:** decomp projects, and unmodified FireRed (US 1.0) / Emerald (US) ROMs.
- **Quality-of-life switches** (decomp projects): types and move effectiveness in battle, IVs/EVs on the summary screen,
  item descriptions, and skipping the battle slide-in.
- A "Working…" box shows while long tasks run, and the app no longer freezes during them.
- Built and compiled, but not played through yet: please report anything odd.

**Best supported:** pokeemerald-expansion 1.17.x and pokefirered-expansion 1.15.x projects. Every editor works, and
projects with every game-code feature switched on build into working games. GBA ROMs (Emerald, FireRed, LeafGreen,
Ruby, Sapphire) are close behind. DS, 3DS and Game Boy Color support is experimental.

**Not yet verified:** full playthroughs in an emulator. That's what this test round is for.

The app contains no game files. Use games you own, and never share ROMs or built games.
