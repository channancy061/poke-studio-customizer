# Poke Studio Customizer

> **Working title.** Choose a trademark-safe name before any public release (candidates: Sproutforge,
> Hatchlab, Pocket Atelier, Critterworks, Tamer's Toolkit). Rename in `src/PokeStudio.Core/AppInfo.cs`,
> `Directory.Build.props`, `build/windows/PokeStudio.iss` and `build/linux/poke-studio.desktop`.

A friendly desktop editor (Windows + Linux) for customising games you legally own.
It ships **no game files or assets**, never modifies your originals, and uses only open-source components (MIT-licensed).

## Status: Phase 7 (Champion Lab) done
- **Phase 1:** solution skeleton, plugin system, projects, undo/redo + change log, backups/restore, settings,
  Avalonia shell with light/dark theming, CI on Windows + Linux, Windows installer and Linux AppImage.
- **Phase 2:** edit a **pokeemerald-expansion 1.17.1** project (Emerald-based, Gen 1–9 Pokémon):
  Pokémon editor (stats + radar chart, types, abilities, held items, catch rate, EXP, gender, growth,
  egg groups, category, Pokédex text), learnset editor (level-up list with reordering, TM/HM/tutor
  compatibility, egg moves), evolution builder (all methods + conditions, plain-English sentences),
  checks before saving with one-click fixes. The app can **download pokeemerald-expansion** from its
  official GitHub release, or open a copy you already have.
  - Edits go straight into the project's source files, changing only the edited values (layout and
    comments untouched). Only the data files the module declares can be written, and they're backed
    up before the first save of each session.
  - Values that depend on the project's settings (e.g. `P_UPDATED_STATS`) are edited in the branch
    your settings use; parts split across settings are shown locked, with the reason.
- **Phase 3:** first-launch **setup wizard** (theme, emulators, build tools, backups; run again from
  Settings), a 4-step **guided tour** (replay from Help & about), **Build & test** page:
  - **Build my game** saves, backs up and runs the project's own build, with progress, Stop, and
    errors translated into plain English. The last 5 builds are kept as dated copies in the project's
    `builds/` folder (configurable).
  - On Windows, pokeemerald-expansion builds inside **WSL** (its tools don't run on plain Windows).
    The app never installs WSL itself: without it, Build explains why and shows the steps; everything
    else works. Once WSL + Ubuntu exist, the app can install the GBA build tools inside Ubuntu.
  - **Test my game** opens the latest build (or any game file) in the emulator you choose. The app
    suggests mGBA / melonDS / Azahar, recognises VBA, VBA-M, DeSmuME, Citra and Lime3DS too, finds
    ones already on your computer, and warns that VisualBoyAdvance 1.8 is too old for modern hacks.
    No emulator is bundled.
- **Phase 4:**
  - **Move editor** (pokeemerald-expansion): name, description, type, category, power, accuracy,
    PP, priority, effect, target, contact, with plain-English hints and checks before saving.
    Values that depend on a battle setting (`B_UPDATED_MOVE_DATA >= GEN_6 ? 90 : 95`) are edited in
    the branch your settings use; the other branch is kept. Pokémon fields now work the same way.
  - **Modernize**: pick a generation (Gen 9 = latest … Gen 1) and preview, per Pokémon, which
    level-up moves change; optionally base stats, types and abilities too. Untick any Pokémon, then
    apply as one undo step. Uses the per-generation data the project already ships.
  - **Global search** (Ctrl+K or the search bar): jump to any Pokémon, move or tool.
  - **Drag and drop**: drop a game file or project folder on the hub to open it; drag level-up
    moves by their handle to reorder them.
- **Phase 5:**
  - **Trainer editor** (pokeemerald-expansion `src/data/trainers.party`): name, class, picture,
    gender, music, single/double battle, battle items, and the team: Pokémon, level, held item,
    ability, nature, IVs, nickname, shiny, four moves (or "learned automatically").
    Only changed trainers are rewritten, and within them only changed lines and Pokémon; comments stay.
    Names are written the way the project's trainerproc tool expects ("Aroma Lady", "Skill Swap"),
    falling back to constants when a name wouldn't convert back (forms, aliases).
  - **Change levels**: + N levels or a percentage, for one trainer, the filtered list or everyone,
    as one undo step. **Copy team** from another trainer.
  - **Enemy AI**: presets (Random, Beginner, Basic, Smart, Expert) built from the project's own
    AI_FLAG_* names, per trainer or for groups (every trainer / bosses / everyone else); individual
    behaviours with the descriptions from `battle_ai.h`.
  - Checks before saving: team size, levels, unknown Pokémon/moves/items, Pokémon switched off in
    settings, double battle with one Pokémon, EV limits; warnings for unusual abilities and moves
    a Pokémon can't normally learn. Trainers that pick from a random pool are shown locked.
- **Phase 6:**
  - **Wild encounters** (`wild_encounters.json`, written back in its exact layout): every place's grass,
    surfing, fishing (per rod) and rock-smash slots with each slot's chance in plain words; change all
    wild levels by N; replace a Pokémon everywhere (optionally on trainers' teams too).
  - **Items & shops:** item name, price, description, effect strength (`items.h`). Every shop's stock
    (`data/maps/*/scripts.inc`), ready-made lists (competitive held items, battle berries, X items,
    training items, healing, balls, TMs), **stock unlocked by badges** (the app writes clearly marked
    extra shop lists and `goto_if_set FLAG_BADGExx_GET` lines; removing them restores the file), and
    **Rare Candies in every shop** at a price you choose.
  - **Text:** search all dialogue; edit with real line breaks (a blank line = new text box); each line
    is measured with the project's own font (`src/fonts.c` + `charmap.txt`) against the 216-pixel box.
  - **Levels & caps:** set milestone levels (Gym 1 … Champion, from `src/caps.c`), with presets for a
    Lv 70–100 Champion; every trainer and wild Pokémon follows the curve, optionally evolving trainers'
    Pokémon; edit the caps and `include/config/caps.h` (caps on/off, hard or soft XP cap, Rare Candy
    cap, more XP below the cap).
  - **Physical/Special split** (`B_PHYSICAL_SPECIAL_SPLIT`): switch between the Gen 4+ rule and the
    Gen 1–3 type rule; Modernize offers it when moving between generations; the move editor explains
    which category a move really uses.
- **Phase 7 (first half) — Champion Lab:**
  - **Groups:** trainers are sorted into Regular / Gyms 1–5 / Gyms 6–8 / Rival / Elite Four / Champion /
    Post-game from their class, code name and level (rematches go to Post-game); move any trainer.
  - **Type themes** per trainer (strict, or 1–2 off-type Pokémon allowed), suggested from current teams;
    varied second types.
  - **Legendary rules** per group × difficulty using the project's own `isRestrictedLegendary`,
    `isSubLegendary`, `isMythical`, `isUltraBeast`, `isParadox`, Mega/Primal flags (editable as tickboxes in
    the Pokémon editor): limits per category, "one restricted OR the sub-legendaries", max Megas, Species
    and Item Clause, story locks (not before a milestone), fair play (no more legendaries than the player
    can catch by then). Defaults as agreed; all editable.
  - **Rival keeps their starter:** starters read from the project (Emerald `starter_choose.c`, FireRed
    `field_specials.c`); the rival naming convention is detected (Emerald: suffix = player's pick; FireRed:
    suffix = the rival's own); flags fights without the line, or unevolved more than 3 levels past evolution.
  - **Scouting:** what the player can catch before each milestone (areas at or below the cap, plus
    evolutions by then).
  - Rule breaks are "Check this" warnings with "Take me there"; settings live in the Poke Studio project
    (`data/champion-lab.json`), never in the game files, and every change is undoable.
- **Phase 7 (second half):**
  - **Suggest a team** (trainer editor; rule-based, no AI model): fits the theme, legendary rules, story locks,
    Species/Item Clause and level; picks Pokémon that patch the team's weaknesses against what the player can
    have; moves (STAB, coverage, a matching setup or utility move), nature, ability, item and EVs by role and
    difficulty; the strongest last as the ace; the rival's starter stays. Every pick is explained; "Try another".
    **Official Pokémon only** (National Dex 1–1025 from the project's `natDexNum`, no novelty/battle-only
    forms) is on by default.
  - **Report card:** speed and two-hit-KO tables against the player's possible Pokémon at the level cap
    (main-series damage formula), shared weaknesses, a 1–10 score and suggested fixes.
  - **Difficulty versions:** Easy / Hard / Extra Hard blocks generated for every important trainer from its
    Normal team (never smaller than the original); `DIFFICULTY_EXTRA_HARD` is added to
    `include/constants/difficulty.h` when needed. The in-game menu to pick it comes in Phase 8.
  - **Enemy AI:** Champion and Double Ace presets; AI tuning sliders for `include/config/ai.h` (game-wide).
- **FireRed base:** projects made from **pokefirered-expansion** (community port, cawtds/pokefirered-expansion,
  expansion 1.15.x) open with the same editors. Differences handled: the ROM is `pokefirered.gba`, the text
  box is 26 tiles (208 px, read from `src/menu.c`), shops live in `src/data/shops.json` (stages; edited in
  place; badge stock adds stages plus eight marked `PokeStudio_HasBadgeN` checks in `src/shop.c`; shops
  that already change with the story keep their stages; shared lists are edited once).
- Only text you change is checked against the text box (the original game has lines meant for wider windows).
- The **Demo** module still edits an invented sample (`samples/Demo Valley.demogame.json`).

## Layout
```
src/PokeStudio.Core           platform-neutral engine (no UI / OS APIs)
  Modules/   IGameModule, ModuleCapabilities, FeatureSupport, ModuleRegistry
  Models/    Species, Move, Learnset, EvolutionMethod, Trainer, TrainerPokemon, AiProfile, SpriteAsset, ModelAsset, Encounter
  Projects/  Project (project.json + source/ + data/ + history.db), ProjectService, ProjectSession
  Changes/   UndoRedoStack (with drag-merging), ChangeLog (SQLite)
  Backups/   BackupService (timestamped snapshots, SHA-256, pin, prune, verified restore)
  Settings/  AppSettings, SettingsService (atomic writes, damaged-file recovery)
  Platform/  AppPaths (Windows AppData / Linux XDG), PathRules (case-sensitive archive names)
  Safety/    SafeFileWriter (confined, atomic, never touches originals)
src/PokeStudio.Modules.Expansion  pokeemerald-expansion project mode (reader/writer, downloader)
src/PokeStudio.Modules.Demo   sample module used to exercise everything
src/PokeStudio.App            Avalonia 11 + CommunityToolkit.Mvvm (theme from the Pixel Lab design handoff)
tests/                        xUnit (Core) + Avalonia.Headless (UI, renders screenshots)
build/                        publish.ps1 + Inno Setup, build-appimage.sh, license-audit.py
.github/workflows             ci.yml (Windows + Ubuntu matrix), release.yml (installer + AppImage on tag)
```

## Requirements
- .NET SDK 10 (see `global.json`). Users need nothing — releases are self-contained.
- Windows installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).
- AppImage: any x86-64 Linux with .NET 10 SDK and `curl`.
- Optional: set `AVALONIA_TELEMETRY_OPTOUT=1` to switch off Avalonia's build-time telemetry (CI does).

## Run and verify — Windows
```powershell
dotnet test                                   # Core + UI tests (screenshots in tests/PokeStudio.App.Tests/bin/*/net10.0/TestResults/screens)
dotnet run --project src/PokeStudio.App       # run from source
./build/publish.ps1 -Version 0.1.0            # artifacts/publish/win-x64/PokeStudio.exe (+ installer if Inno Setup is installed)
python build/license-audit.py                 # all dependencies open-source?
```
Install `artifacts/installer/PokeStudio-0.1.0-win-x64-setup.exe` (no admin needed), launch from the Start menu,
then uninstall from Settings › Apps. Your projects and settings are kept.

## Run and verify — Linux (Ubuntu 22.04 / 24.04, Fedora 40+)
```bash
dotnet test
dotnet run --project src/PokeStudio.App
bash build/linux/build-appimage.sh 0.1.0
chmod +x artifacts/appimage/PokeStudio-0.1.0-x86_64.AppImage
./artifacts/appimage/PokeStudio-0.1.0-x86_64.AppImage
```
If the AppImage won't start on a minimal distro: `sudo apt install libfuse2` (or run with `--appimage-extract-and-run`).
CI builds the AppImage on every `v*` tag (`release.yml`), so you can also download it from the run's artifacts.

## Manual checklist (both OSes)
1. First launch shows the hub with the empty state.
2. **Try the demo game** → workspace opens on Pokémon; the menu shows Moves/Trainers/Enemy AI **locked with a reason** and "6 tools hidden for this game".
3. Drag Leafling's **Speed** slider → one entry "Leafling · Speed 45 → …" in Changes; status bar shows "1 unsaved change".
4. **Ctrl+Z / Ctrl+Y** undo and redo; the undo button tooltip names the change.
5. Type a 13-letter name → plain-English "too long for this game" message, no change recorded.
6. **Ctrl+S** saves; close and reopen from Recent projects → value persisted.
7. Backups › **Back up now**, change a stat, save, **Restore…** → value returns; a "Before restore" backup appears.
8. Settings › Light / Dark / Match my computer, Text size Large → whole UI scales; restart keeps settings.
9. Tab through the window: every control shows a focus ring.
10. Help & about lists only open-source licences and the legal note.
11. Delete `settings.json` and start the app → the setup wizard opens; "Search again" lists emulators
    you already have; "Use this" picks one; VBA 1.8 shows a warning; Finish closes it for good.
12. Open a project → the guided tour runs once; Help & about › Replay the guided tour shows it again.
13. Build & test without WSL → "Not ready to build yet" with the WSL steps; Build button disabled.
14. With an emulator set and a build in `builds/`, **Test my game** opens it in the emulator.
15. Moves › Thunderbolt › Power 85 → save → only that line changes in `src/data/moves_info.h`.
16. Modernize › Gen 3 › Show what would change → untick one → Apply → one entry in Changes; Ctrl+Z undoes all.
17. Ctrl+K, type "char", Enter → that Pokémon opens. Drag a project folder onto the hub → it opens.
18. Trainers › Hiker Sawyer › level 25 → save → only that line changes in `src/data/trainers.party`.
19. Trainers › Change levels… › Percent 120 › Every trainer › Apply → one entry in Changes.
20. Enemy AI › Expert › Gym Leaders… › Apply → bosses use the new AI; Ctrl+Z undoes it.
21. Wild encounters › Route 101 › first slot level 4 → save → two lines change in `wild_encounters.json`.
22. Items & shops › Shops › Oldale Mart › Competitive held items after badge 4 → save → extra shop lists
    appear at the end of the map's `scripts.inc`; build the game and check the shop after badge 4.
23. Levels & caps › Champion at Lv 90 › Apply the curve → trainers, wild levels and caps all move; one undo.

## Where data lives
| | Windows | Linux |
|---|---|---|
| Settings | `%APPDATA%\PokeStudio\settings.json` | `$XDG_CONFIG_HOME/poke-studio` (default `~/.config/poke-studio`) |
| Backups | `%LOCALAPPDATA%\PokeStudio\Backups` | `$XDG_DATA_HOME/poke-studio/backups` |
| Logs | `%LOCALAPPDATA%\PokeStudio\Logs` | `$XDG_STATE_HOME/poke-studio/logs` |
| Projects | `Documents\Poke Studio` | `~/Documents/Poke Studio` |

## Ground rules
No ROMs, BIOS or keys are shipped, downloaded or linked, and no game assets are bundled. No DRM
circumvention. Game files are never written (decomp projects are edited in place, with backups).
No GPL code. See `THIRD-PARTY-NOTICES.md`.

Exception (owner's decision, 2026-10-10): at the user's request the app may download
**pokeemerald-expansion** from its official GitHub repository. That project contains assets extracted
from the original games and has no open-source licence; it is downloaded to the user's computer only,
never bundled with this app.

## Optional: test against real projects
```bash
POKESTUDIO_EXPANSION_DIR=/path/to/pokeemerald-expansion dotnet test --filter Real_project
POKESTUDIO_FIRERED_DIR=/path/to/pokefirered-expansion dotnet test --filter FireRedRealTests
```
The folder is copied first; the test checks that saving without changes keeps every file identical
and that one edit changes exactly one line.
