# Poke Studio Customizer: User Guide

This guide explains how to use every part of the app, from opening your first game to handing a finished hack to your
players. You don't need to know how to program. Words that might be new are explained the first time they appear.

## Contents
1. [Words you'll see](#1-words-youll-see)
2. [Installing and first launch](#2-installing-and-first-launch)
3. [Starting a project](#3-starting-a-project)
4. [The main window](#4-the-main-window)
5. [Saving, undo, checks and backups](#5-saving-undo-checks-and-backups)
6. [Editing your game](#6-editing-your-game)
7. [Tools for building a harder, better game](#7-tools-for-building-a-harder-better-game)
8. [Building and testing (decomp projects)](#8-building-and-testing-decomp-projects)
9. [ROM mode: editing a .gba, .nds, .3ds or .gbc file](#9-rom-mode-editing-a-gba-nds-3ds-or-gbc-file)
10. [Sharing your hack](#10-sharing-your-hack)
11. [Settings](#11-settings)
12. [Troubleshooting](#12-troubleshooting)
13. [Where your files are](#13-where-your-files-are)

---

## 1. Words you'll see

| Word | Meaning |
|---|---|
| **ROM** | The game file itself (`.gba`, `.nds`, `.3ds`, `.gbc`), dumped from a cartridge you own. |
| **Decomp project** | A folder with a game rebuilt as readable source code. The app edits that code and then *builds* it into a new game file. **pokeemerald-expansion** (Emerald base, all Pokémon up to Gen 9) is the one this app supports best; **pokefirered-expansion** is a FireRed-based version. |
| **Build** | Turning a decomp project into a playable `.gba` file. |
| **Emulator** | A program that plays the game on your computer, e.g. mGBA. |
| **WSL** | *Windows Subsystem for Linux*, Microsoft's free way to run Linux tools on Windows. pokeemerald-expansion can only be built with Linux tools, so Windows users need it for building (not for editing). |
| **Project** | Poke Studio's folder for one game you're working on: your settings, backups, history and (for ROMs) the working copy. |
| **Seed** | The "recipe number" of a randomizer run. The same seed with the same choices always gives the same result. |

## 2. Installing and first launch

1. Install the app (see [Install](../README.md#install) in the README).
2. The first time it starts, a four-step **setup** asks:
   - **Look:** light, dark, or match your computer.
   - **Emulators:** the app searches your computer for emulators it knows and suggests one per system
     (mGBA for GBA/GBC, melonDS for DS, Azahar for 3DS). Pick **Use this**, or **Change…** to choose one yourself.
     VisualBoyAdvance 1.8 shows a warning: it is too old for many modern hacks.
   - **Build tools:** only needed to build decomp projects. On Windows this checks for WSL and Ubuntu. You can skip
     this and come back later from Build & test.
   - **Backups:** where backups go and how many to keep. A different drive is safest.
3. When you open your first project, a short **guided tour** shows the main parts of the window. Replay it any time
   from **Help & about**.

You can run the setup again from **Settings → Run setup again…**.

## 3. Starting a project

The start screen offers:

| Button | Use it for |
|---|---|
| **Open game files** | A ROM you own (`.gba`, `.nds`, `.3ds`, `.gbc`). The app makes a project around a **copy**; your original is never changed. |
| **Open decomp project** | A pokeemerald-expansion or pokefirered-expansion folder already on your computer. |
| **Download pokeemerald-expansion** | Downloads version 1.17.1 from its official GitHub page into a folder you choose, then opens it. |
| **Open project folder** | A Poke Studio project you made before (also listed under **Recent projects**). |
| **Try the demo game** | An invented sample game to explore safely. |

You can also **drag** a game file or folder onto the start screen.

### Which should I choose?
- **For the full experience** (options menu, Nuzlocke rules, adaptive bosses, Pokémon Center helper, badge shops):
  a **decomp project**. Download pokeemerald-expansion from the start screen.
- **For quick changes to a game you already have:** open the ROM. Most editors work. The options menu and other
  new game code need a decomp project, because they add new code to the game.

### Projects from a ZIP download
pokeemerald-expansion's build checks that the project came with its **git history**, the record of its versions that
`git clone` brings along. A project downloaded as a ZIP doesn't have it, and the build stops with
"no git history found". When the app downloads the project for you it handles this automatically. If you downloaded
a ZIP yourself, either get it with git instead:
```bash
git clone https://github.com/rh-hideout/pokeemerald-expansion.git
```
or create an empty file named `.histignore` in the project folder. The app's build page explains this if it happens.

## 4. The main window

- **Left menu:** every editor and tool. Items that this game type can't do yet are shown **locked**, with the reason;
  things it can never do are hidden. The count of hidden tools is shown at the bottom.
- **Top bar:**
  - **Search** (Ctrl+K): type a Pokémon, move, trainer or tool and press Enter to jump there.
  - **Undo / Redo** (Ctrl+Z / Ctrl+Y): the tooltip names the change.
  - **Changes:** every unsaved change, in order; click one to go to it or undo just that one.
  - **Save** (Ctrl+S), **Build**, **Test my game**.
- **Status bar:** how many unsaved changes you have and when the last backup was made.
- **Overview:** what this game type lets you edit, where your project and original are, and what's locked and why.

## 5. Saving, undo, checks and backups

- **Undo everything.** Every change, including big ones like "Apply the level curve" or "Randomize", is one undo step.
- **Checks before saving.** When you save, the app checks your changes:
  - **Must fix** items stop the save. They explain the problem in plain words, and many have a **Fix it for me**
    button.
  - **Check this** items are warnings, e.g. a trainer's Pokémon knows a move it can't normally learn. You can save anyway.
  - Problems that were already in the original game aren't listed. A note says how many were left out.
- **Backups.** Before the first save of every session the app backs up the files it's about to change.
  **Backups → Back up now** makes one any time; **Restore…** brings one back (and backs up what you have first).
  Pinned backups are never deleted automatically.
- **History.** Every change is also recorded in the project's history, so you can see what you did and when.

## 6. Editing your game

### Pokémon
Pick a Pokémon from the list (search at the top). You can change:
- **Base stats** with sliders; the chart shows the old values as a dashed line.
- **Types** (pick "None" for a single type), **abilities** (two plus a hidden ability where the game has them),
  **held items** (common 50% / rare 5%), **catch rate**, **EXP given**, **gender ratio**, **growth speed**,
  **egg groups**, **category** and **Pokédex entry**.
- **Legendary tags** (decomp projects): restricted legendary, sub-legendary, mythical, Ultra Beast, Paradox, Mega.
  The Champion Lab uses them for its rules.

Some fields depend on a game setting; the app edits the version your settings use and says so. A few Pokémon (for
example Unown's forms) are built from a shared template in the source code; they're shown **read-only**, with the
reason.

### Moves
Name, type, category (physical / special / status), power, accuracy, PP, priority, effect, target, contact and
description. Hints explain each field. Values that depend on a battle setting are edited in the version you use.

### Learnsets
- **Level-up moves:** add, remove, change levels, and drag rows (or Alt+↑/↓) to reorder. Level 0 = learned on evolving.
  **Sort by level** tidies the list.
- **TM, HM & tutor moves:** tick what this Pokémon can be taught.
- **Egg moves:** what it can be born knowing. ROM games have limits (e.g. 10 per Pokémon in GBA games); the check explains them.

### Evolutions
Each evolution is shown as a sentence ("Evolves into Ivysaur at level 16"). **Add an evolution**, pick **how it
evolves** and the target, and add **extra conditions** (time of day, held item, friendship, gender…). Only methods
the game supports are offered.

### Trainers
- **Team:** Pokémon, level, held item, ability, nature, IVs, EVs, nickname, shiny, and four moves
  ("learned automatically" picks the moves it would know at that level).
- **Name, class, picture, music, single/double battle, items** the trainer uses in battle.
- **Enemy AI:** presets from Random to Expert (and Champion / Double Ace), or individual behaviours.
- **Suggest a team** builds a team that fits the trainer's type theme and your rules; every choice is explained, and
  **Try another** gives a different one. **Copy team** copies another trainer's team.
- **Change levels** raises or lowers levels by a number or a percentage, for one trainer, the list or everyone.

### Wild encounters
Every area's grass, water, fishing (per rod) and rock-smash Pokémon, with each slot's chance in plain words.
**Change levels or replace a Pokémon…** raises all wild levels, or swaps one Pokémon for another everywhere
(optionally on trainers' teams too).

### Items & shops
- **Items:** names, prices, descriptions and effect strength.
- **Shops:** what each shop sells, with ready-made lists (competitive held items, battle berries, balls, healing, TMs).
- **Stock unlocked by badges** (decomp projects): items that appear after a chosen badge.
- **No grinding:** adds Rare Candies to every shop at a price you choose (level caps still apply).

### Text
Search all dialogue by words, place or label. Edit with real line breaks; leave an empty line to start a new text box.
The preview shows how wide each line is against the game's text box, measured with the game's own font where
possible. Codes in braces such as `{PLAYER}` are filled in by the game; keep them.

### Graphics
Pick a Pokémon and a picture (front, back or party icon), then normal or shiny.
- **Colours:** type a new `#RRGGBB` into a swatch. Colour 0 is the see-through background. The game rounds colours slightly.
- **Replace from PNG…:** draw in any paint program and import. The picture is fitted to the game's size and 16-colour
  limit for you. For two-frame sprites a single frame is repeated, and icons are matched to their shared palette.
- **Export PNG** saves the current picture so you can edit it.

## 7. Tools for building a harder, better game

### Champion Lab
- **Groups & themes:** trainers are sorted into regular trainers, gyms, rival, Elite Four, Champion and post-game.
  Give each boss a **type theme** (strict, or 1–2 off-type Pokémon allowed).
- **Rules:** legendary limits per group and difficulty, Species / Item Clause, story locks, "fair play" (bosses don't
  use more legendaries than you could own by then), and **rivals keep their starter**.
- **Scouting:** what the player can catch before each boss.
- **Report card:** for each boss, which of the player's possible Pokémon it beats and loses to, with a 1–10 score and
  suggestions.
- **Difficulty versions:** Easy, Hard and Extra Hard teams for every important trainer, made from the Normal team.
- **Suggest adaptive slots for all bosses** (decomp projects): see Game options below.

### Game options (decomp projects)
A menu the player sees once when starting a new game. For each option you choose **not added**, **player chooses**,
or **always on/off**:
- **Difficulty:** Easy / Normal / Hard / Extra Hard (uses the difficulty versions).
- **Nuzlocke**, each rule separately: only the first Pokémon met in each area can be caught (counting starts once you have Poké Balls); fainted Pokémon are
  released; duplicates clause (whole evolution families, plus an Eevee clause); shiny clause; nicknames required (the
  species name doesn't count); no revives; respect the level cap.
- **Hard level cap**, **no EVs / perfect IVs**, **no items in battle**, **Infinite Candy** (a reusable Rare Candy).
- **Adaptive boss slot:** gym leaders, the rival and the Elite Four (never the Champion) keep one flexible team member,
  chosen at the start of the battle to counter your current team.
- **Pokémon Center helper:** an extra person in every Pokémon Center who changes natures, gives perfect IVs and sets
  EVs, each optional and with a price you set.

Players are warned at the start: *"Stop pressing A now! Yes is picked first. Press B to answer No."*

### Levels & caps
Set how strong the big fights are (Gym 1 … Champion); every trainer and wild Pokémon in between follows the curve.
Set **level caps** per badge, choose whether Rare Candies stop at the cap, and whether Pokémon below the cap get extra
experience. Also switches between the modern **physical/special split** and the Gen 1–3 type rule.

**Reusable TMs:** TMs aren't used up when a Pokémon learns a move, as in Gen 5 and later. This works in decomp projects
and in unmodified FireRed (US 1.0) and Emerald (US) ROMs.

**Quality of life** (decomp projects), each a switch:
- show the opponent's types in battle;
- show how well each move will work;
- IVs and EVs on the summary screen;
- item descriptions when found;
- skip the battle slide-in.

### Story battles (decomp projects)
Add a battle the player can't skip. Pick a **map** and the **entrance** to guard: a gym door, a cave mouth, a building.
The trainer stands a few tiles away and looks at the tile in front of the entrance, so everyone who goes in is spotted
and has to fight. You can also guard the tile just outside an exit, for example Victory Road's way out. The app only
offers spots where the trainer can see the entrance without blocking a path.

Give the trainer a name, class, battle sprite and look. Tick **Major battle** to give the fight its own level cap,
just before the battle you choose (for example just before Gym 1), so the level curve and the caps follow it. A team is
suggested at that level. Edit it on the **Trainers** page or redesign it in **Champion Lab** like any other trainer. The
battle uses the game's own "trainer spots you" rule and its "already beaten" flag, so it needs no new flags or
variables. **Remove** takes the trainer, their script and their level cap out again, and Ctrl+Z undoes it.

**Crossover packs.** On the same page, one click adds another region's whole challenge to your story:
- **Hoenn challengers in Kanto** (FireRed): each Hoenn gym leader guards the matching Kanto gym (Roxanne at Pewter
  Gym, and so on), Sidney, Phoebe, Drake and Glacia guard Mt. Moon, Pokémon Tower, Silph Co. and the Pokémon Mansion,
  and Wallace waits just outside Victory Road.
- **Kanto challengers in Hoenn** (Emerald): Brock to Giovanni guard Hoenn's gyms, Lorelei, Bruno, Agatha and Lance
  guard the cable car, the Weather Institute, Mt. Pyre and Sky Pillar, and Blue waits just outside Victory Road.

Every battle is a major battle with its own level cap and a Champion Lab team in the trainer's type. Each
challenger fights with a **field effect for the whole battle** that suits their type: Erika's rain weakens Fire moves,
Flannery's sun (or, in FireRed, a sea of fire on your side) weakens Water, and so on. After the first win they give
you a **TM of their type**. You can change both for every battle on the page. The Player's Guide tells players about
the effect and the reward. The Champion always has at least one legendary and one mythical Pokémon (a Champion Lab
rule you can switch off). FireRed's expansion is older and has no weather effects, so its challengers use terrains,
hazards and Tailwind instead. FireRed has no
Hoenn overworld sprites and no sprites for Juan or Champion Wallace, so the app asks for **your own**
pokeemerald-expansion folder and copies them in when you save. Emerald has room for only 9 more trainers. With 13, the
space for "beaten" flags grows, so save files made before can't be used with the new version: start a new game to
test. One Undo takes the whole pack out again.

### Fix evolutions
Turns trade and day/night evolutions into ones anyone can do alone: a level, a stone, or (where the game supports it)
levelling up while holding an item. You choose per evolution; clashes (Espeon/Umbreon) are handled.

### Modernize
Give every Pokémon the moveset from a newer (or older) generation. You see every change first, can untick any
Pokémon, and one Undo puts it all back. Optionally also stats, types and abilities.

### Randomizer
Pick a **seed** (or **New seed**), then what to shuffle:
- **Wild Pokémon:** any, or similar strength. In each area a Pokémon is always replaced by the same one, and there are
  no legendaries unless you allow them.
- **Trainers' teams:** similar strength, optionally keeping each Pokémon's type (gym themes stay).
- **Abilities** (game-breaking ones like Wonder Guard are never handed out), **types** (a Pokémon and its evolutions
  share them), **level-up moves** (the first is always a weak attack), **TM compatibility**.
- **Only official Pokémon.**

**Randomize** applies it as one step; Ctrl+Z takes it all back. Share the seed and your choices so others can make the
same game.

### Player's Guide
Creates `guide/players-guide.html` (open it in any browser, prints well) and a Markdown copy. It lists your
new-game options, Nuzlocke rules, level caps, every important battle in story order with where it is, the team and
what to bring, difficulty versions, adaptive slots shown as "?" with their candidates, where to catch each Pokémon,
shops and special evolutions. Make it again after changes.

## 8. Building and testing (decomp projects)

1. Open **Build & test**. The page checks your build tools first.
2. **On Windows** you need WSL with Ubuntu. If it's missing, the page shows the steps. In short, in PowerShell as
   administrator:
   ```powershell
   wsl --install -d Ubuntu
   ```
   restart, finish Ubuntu's first start (choose a Linux username and password), then click **Check again**.
   The page can then install the GBA build tools inside Ubuntu for you; you'll be asked for your Linux password.
   On Linux, it lists the packages to install.
3. **Build my game** saves, backs up (if set in Settings) and builds. You'll see progress, a **Stop** button and, if
   something goes wrong, the problem in plain English (open **Show or hide the build log** for the details).
   The first build takes 5–10 minutes.
4. Each finished game is kept in the project's `builds` folder with the date in its name (the last 5 by default).
   **Show in folder** opens it.
5. **Test my game** opens the newest build in your emulator. **Test another file…** picks any game file.

## 9. ROM mode: editing a .gba, .nds, .3ds or .gbc file

- The app finds the game's data by its contents, so most versions and light hacks work. Their places are remembered in
  the project.
- When something grows (a longer move list, a bigger team, longer dialogue), it moves to the ROM's free space and the
  game is pointed to it.
- **GBA ROMs:** Pokémon, moves, learnsets (level-up, TM/HM, egg moves), evolutions (only methods these games know),
  trainers, wild Pokémon, items and shops, dialogue, sprites and icons, randomizer.
- **Adaptive boss slots in GBA ROMs:** unmodified **FireRed (US 1.0)** and **Emerald (US)** ROMs can get them too: the app
  adds a small piece of its own code to the ROM when you save. In a ROM the slot is always on (there's no new-game menu).
  Other versions and already-hacked ROMs show a note instead.
- **DS / 3DS / GBC ROMs:** experimental. See the Overview page for what each game supports. 3DS changes are saved as a
  mod folder that Citra loads automatically.
- **Saving** writes your changes into the project's copy of the ROM. There is nothing to compile.
- **Build my game** saves and puts a dated copy of your edited game in **Recent builds** (the project's `builds`
  folder), so you have a game file to play or keep, the same as with decomp projects.
- **Test my game** plays your newest build, or the edited copy if you haven't built yet.

## 10. Sharing your hack

- **Never share ROMs or built game files publicly.** They contain the original game.
- The usual way to share a ROM hack is a **patch**: a file with only your changes, which players apply to their own
  game. On **Build my game**, press **Make patch** next to a build. A `.bps` file is saved next to that build.
  - **ROM projects** make the patch from the ROM you started the project with.
  - **Decomp projects** ask you to pick your unmodified game: Emerald (US) for pokeemerald-expansion, FireRed (US 1.0)
    for pokefirered-expansion.
  - Anything that's also in the player's own game, even if it moved, is taken from their copy, so the patch holds only
    your hack. Patches of expansion projects are still large (often 15–20 MB) because the expansion adds a lot of new
    Pokémon, moves and graphics.
- Players apply the patch to **their own unmodified game** with [Floating IPS (Flips)](https://github.com/Alcaro/Flips)
  or an online patcher such as [Rom Patcher JS](https://www.marcrobledo.com/RomPatcher.js/). If their game is a
  different version, the patcher says so instead of making a broken game.
- Give your players the **Player's Guide**, and share your randomizer seed if you used one.

## 11. Settings

- **Appearance:** light / dark / match my computer, text size, reduce motion.
- **Emulators:** one per system, used by Test my game.
- **Projects & backups:** where new projects and backups go, how many backups and builds to keep, back up before every build.
- **Advanced mode:** shows internal names, raw values and file locations next to the friendly fields.
- **Run setup again…**

## 12. Troubleshooting

| Problem | What to do |
|---|---|
| "Not ready to build yet" | Follow the steps on the Build & test page (WSL + Ubuntu on Windows, then install the build tools). |
| Build stops with "no git history" | See [Projects from a ZIP download](#projects-from-a-zip-download). |
| Build stops with another error | Read the plain-English explanation; open the build log; restore a backup if a change broke it; report it (see [TESTING.md](TESTING.md)). |
| A field is grey / locked | Hover over it or read the note: it explains why (a game limit, a shared value, or not supported for this game yet). |
| "Must fix" when saving | Each item says what's wrong and often offers **Fix it for me**. **Take me there** opens the right page. |
| The game looks wrong in the emulator | Restore the last backup (Backups → Restore…), then try the change again step by step and report what you found. |
| WSL install fails | Install WSL from [Microsoft's WSL releases page](https://github.com/microsoft/WSL/releases), restart, then `wsl --install -d Ubuntu`. Make sure `%TEMP%` exists. |
| Windows warns about an unknown publisher | The app isn't code-signed yet: choose "More info" → "Run anyway". |

## 13. Where your files are

| | Windows | Linux |
|---|---|---|
| Projects | `Documents\Poke Studio` | `~/Documents/Poke Studio` |
| Settings | `%APPDATA%\PokeStudio\settings.json` | `~/.config/poke-studio` |
| Backups | `%LOCALAPPDATA%\PokeStudio\Backups` | `~/.local/share/poke-studio/backups` |
| Logs | `%LOCALAPPDATA%\PokeStudio\Logs` | `~/.local/state/poke-studio/logs` |

Inside a project folder:

| | |
|---|---|
| `project.json` | The project's details |
| `source/` | The working copy of your ROM (ROM projects) |
| `data/` | Your settings for the tools (Champion Lab, game options) and remembered table locations |
| `builds/` | Finished games (decomp projects) |
| `guide/` | The Player's Guide |
| `graphics-export/` | Exported pictures |
| `history.db` | The change history |
