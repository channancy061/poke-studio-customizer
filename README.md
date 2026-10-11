# Poke Studio Customizer

A friendly desktop app (Windows and Linux) for building your own Pokémon game from games you own: change Pokémon, moves,
trainers, wild encounters, shops, dialogue and sprites, add a new-game options menu with Nuzlocke rules and difficulty
levels, then build and play the result.

It is meant for people who want "Radical Red / Kaizo" style hacks without learning to program. Every change can be
undone, every save is backed up first, and your original game files are never changed.

> **Status: early testing (version 0.1).** The GBA side is the most complete. See [What works today](#what-works-today)
> before you start. Please report anything odd. [docs/TESTING.md](docs/TESTING.md) explains how.

> Poke Studio Customizer is a fan-made tool. It is **not affiliated with or endorsed by** Nintendo, Creatures,
> GAME FREAK or The Pokémon Company. Pokémon and all related names are trademarks of their owners.
> The app contains **no game files, graphics, music or text** from any game.

---

## Contents
- [What it can do](#what-it-can-do)
- [What works today](#what-works-today)
- [What you need](#what-you-need)
- [Install](#install)
- [Quick start: your first GBA hack](#quick-start-your-first-gba-hack)
- [Your games stay yours](#your-games-stay-yours)
- [Documentation](#documentation)
- [Reporting problems](#reporting-problems)
- [Licence and credits](#licence-and-credits)

---

## What it can do

| Area | What you can change |
|---|---|
| **Pokémon** | Base stats (with a chart), types, abilities, held items, catch rate, EXP, gender ratio, growth rate, egg groups, Pokédex text |
| **Moves** | Name, power, accuracy, PP, type, category (physical/special), priority, effect, description |
| **Learnsets** | Level-up moves (drag to reorder), TM/HM and tutor compatibility, egg moves |
| **Evolutions** | Any method the game supports, written as plain sentences. **Fix evolutions** turns trade and day/night evolutions into ones you can do alone |
| **Trainers** | Name, class, team, levels, moves, items, abilities, natures, IVs/EVs, AI. "Suggest a team" builds a competitive team that fits the gym's type |
| **Champion Lab** | Gym type themes, legendary rules, rival keeps their starter, difficulty versions (Easy / Normal / Hard / Extra Hard), a report card for every boss |
| **Wild encounters** | Every area's grass, water, fishing and rock-smash Pokémon and levels |
| **Items & shops** | Prices, shop stock, stock that grows with badges, cheap Rare Candies |
| **Text** | All dialogue, with a preview of how each line fits the text box |
| **Graphics** | Replace front/back sprites and party icons from a PNG, edit normal and shiny colours |
| **Levels & caps** | Level curve for the whole game, level caps per badge, reusable TMs, quality-of-life switches (types and move effectiveness in battle, IVs/EVs on the summary screen…) |
| **Game options** (decomp projects) | A menu shown when a new game starts: difficulty, Nuzlocke rules (each one optional), hard level cap, no EVs/IVs, no items in battle, Infinite Candy, adaptive boss slot, a Pokémon Center helper who changes natures/IVs/EVs |
| **Modernize** | Update movesets to a newer generation's |
| **Randomizer** | Wild Pokémon, trainers, abilities, types, moves, TM lists, with a shareable seed |
| **Player's Guide** | A walkthrough page for your players: every boss team, where to catch Pokémon, shops, level caps |
| **Build & test** | Builds your game and opens it in your emulator |

## What works today

"Verified" means it has been tested on real games and projects. "Built" means the result was compiled into a game file
that was produced without errors. **Nothing has been played through in an emulator yet**: that is what testers are for.

| Game type | Status |
|---|---|
| **pokeemerald-expansion** project (Emerald base, version 1.17.x) | ✅ Every editor verified on a real project. A copy with **every game-code feature switched on** (options menu, Nuzlocke, adaptive boss slot, Pokémon Center helper, Infinite Candy, level caps, badge shops, edited dialogue) **builds into a working game file**. Not yet played through. |
| **pokefirered-expansion** project (FireRed base, community port, 1.15.x) | ✅ Same as above: verified and **builds**. Not yet played through. |
| **GBA ROM** (Emerald, FireRed, LeafGreen, Ruby, Sapphire) | 🟡 Edits are saved and read back correctly on Emerald, FireRed and Ruby, including dialogue, shops, egg moves, sprites, icons and the randomizer. Not yet played in an emulator. The new-game options menu needs a decomp project. |
| DS ROM (Diamond, Pearl, Platinum, HeartGold, SoulSilver, Black, White) | 🧪 Experimental: Pokémon, moves, learnsets, evolutions, trainers, wild Pokémon, items, dialogue, egg moves, Gen 4 sprites. No shops yet. |
| 3DS (X, Y, Omega Ruby, Alpha Sapphire, decrypted `.3ds`) | 🧪 Experimental, saved as a Citra mod. No wild Pokémon or shops yet. |
| Game Boy Color (Gold, Silver, Crystal) | 🧪 Experimental. No sprites or shops yet. |

**Recommended for testers:** a pokeemerald-expansion project. It gets every feature and builds a game you can play.

## What you need

| | |
|---|---|
| Computer | Windows 10/11 (64-bit) or a 64-bit Linux (Ubuntu 22.04+, Fedora 40+…) |
| A game you own | A `.gba` / `.nds` / `.3ds` / `.gbc` file you dumped yourself, **or** a pokeemerald-expansion project (the app can download it for you) |
| An emulator | Recommended: [mGBA](https://mgba.io) for GBA and GBC, [melonDS](https://melonds.kuribo64.net) for DS, [Azahar](https://azahar-emu.org) for 3DS. VisualBoyAdvance 1.8 is too old for modern hacks |
| To **build** an expansion project on Windows | **WSL** with Ubuntu (free, from Microsoft). The app explains the steps and installs the build tools inside Ubuntu for you. Not needed for ROM editing |
| Disk space | About 2 GB for an expansion project and its builds |

## Install

**Windows:** download `PokeStudio-<version>-win-x64-setup.exe` from the
[Releases page](https://github.com/channancy061/poke-studio-customizer/releases) and run it. No administrator rights
are needed. Windows SmartScreen may warn about an unknown publisher, because the app isn't code-signed yet. Click
"More info" → "Run anyway".

**Linux:** download `PokeStudio-<version>-x86_64.AppImage`, then:
```bash
chmod +x PokeStudio-*-x86_64.AppImage
```
```bash
./PokeStudio-*-x86_64.AppImage
```
If it doesn't start on a minimal system, install FUSE 2 (`sudo apt install libfuse2`).

**From source** (developers): install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then:
```bash
dotnet run --project src/PokeStudio.App
```
See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Quick start: your first GBA hack

1. **Start the app.** A short setup asks for your theme, your emulator, the build tools and where backups go.
   Everything can be changed later in Settings.
2. **Get a project.** On the start screen choose **Download pokeemerald-expansion**, or **Open decomp project** if you
   already have one (if you downloaded it yourself as a ZIP, see [this note](docs/USER_GUIDE.md#projects-from-a-zip-download)).
3. **Change something.** For example: Pokémon → Bulbasaur → raise its Speed. Every change appears in **Changes** and can be
   undone with Ctrl+Z.
4. **Save** (Ctrl+S). The app checks your changes first and explains anything that needs fixing in plain words.
5. **Build & test → Build my game.** The first time on Windows you'll be guided through WSL and the build tools.
   The first build takes 5–10 minutes; later ones are faster.
6. **Test my game** opens the new game in your emulator.
7. To share it, press **Make patch** next to a build. Players apply the `.bps` patch to their own game, so nobody
   shares a ROM.

The [User Guide](docs/USER_GUIDE.md) walks through every screen in detail.

## Your games stay yours

- The app never includes, downloads or links to ROMs, BIOS files or keys. You provide games you own.
- **ROM files:** the app works on a copy inside the project folder; your original file is never written to.
- **Decomp projects** are edited in place, but only the files the app understands, and they are **backed up before the
  first save of every session**. Backups → Restore puts them back.
- **3DS games** are only read; changes are saved as a separate mod folder.
- Projects, backups and settings stay on your computer. The app has no accounts, ads or tracking.

## Documentation

| | |
|---|---|
| [User Guide](docs/USER_GUIDE.md) | How to use every screen, step by step |
| [Testing guide](docs/TESTING.md) | What to try first and how to report problems |
| [Developer notes](docs/DEVELOPMENT.md) | Building from source, running the tests, how the code is organised |
| [Third-party notices](THIRD-PARTY-NOTICES.md) | Every open-source component and tool, with licence links |

## Reporting problems

Open an [issue](https://github.com/channancy061/poke-studio-customizer/issues) and include:
- the game or project type and version (shown at the top left of the app),
- what you did, what you expected and what happened,
- a screenshot of any message, and the build log if a build failed (Build & test → "Show or hide the build log").

**Never attach ROMs, save files or game data**, and never ask for or share links to them.

## Licence and credits

Poke Studio Customizer is free and open source under the [MIT Licence](LICENSE).
It is built only with open-source components; the full list with licence links is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Thanks to the communities behind [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion),
[pret](https://github.com/pret) and [pokefirered-expansion](https://github.com/cawtds/pokefirered-expansion), whose
projects this app edits, and to the open-source projects listed in the notices.
