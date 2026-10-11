# Testing guide

Thank you for testing Poke Studio Customizer! This page says what to try first and how to report what you find.

## Ground rules
- Use **games you own** and **your own** dumps. Never share ROMs, save files, built games or links to them,
  including in issues, screenshots of file listings, or chat.
- Work on a project made for testing, not on a hack you care about. The app backs everything up, but this is early software.

## What to test first (GBA)

Most testing is needed on **pokeemerald-expansion** projects. They get every feature and build into a game you can play.

### 1. Setup and building
1. Install the app, finish the setup wizard and pick mGBA as your GBA emulator.
2. Start screen → **Download pokeemerald-expansion**.
3. Build & test → follow the steps until **Build my game** works, then **Test my game**.
4. ✔ The unchanged game starts and plays normally.

### 2. Everyday editing
Change a few things, save, build, then check them in the game:
- a starter's stats, types or ability (Pokémon page; check in battle or the summary screen);
- a move's power or type (Moves);
- a level-up move at an early level (Learnsets);
- an early trainer's team (Trainers), e.g. the first route trainer or Roxanne;
- the Route 101 wild Pokémon (Wild encounters);
- the Oldale Town shop stock, plus an item that appears after badge 1 (Items & shops);
- a line of dialogue near the start (Text);
- a sprite or its colours (Graphics).

### 3. The new-game options (most important)
Game options → set several options to **Player chooses**, including difficulty, Nuzlocke (each rule), level cap,
Infinite Candy, and the Pokémon Center helper. Save and build, then start a **new game**:
- ✔ The options menu appears once, near the start, after the warning about pressing A.
- ✔ Each choice does what it says. Nuzlocke examples:
  - only the first Pokémon in an area can be caught (battles before you have Poké Balls don't count);
  - fainted Pokémon are released after the battle;
  - you can't keep the default name when nicknames are required;
  - revives can't be used;
  - both Pokémon in a wild double battle count.
- ✔ The Pokémon Center helper stands in a sensible place, and changing a nature or IVs works and costs the right amount.
- ✔ Difficulty: bosses use the Easy / Hard / Extra Hard teams.
- ✔ Adaptive boss slot (Champion Lab → Suggest adaptive slots for all bosses): a gym leader's flexible Pokémon changes
  with your team.

### 4. Bigger tools
Try Levels & caps (Champion at Lv 90), Fix evolutions, Modernize, the Randomizer (note your seed) and the Player's
Guide. Each should be one undo step, and the built game should match.

## GBA ROM mode (optional)
Open a `.gba` you own (Emerald, FireRed, LeafGreen, Ruby or Sapphire), make a few of the changes above, save, and play
with **Test my game**. LeafGreen and Sapphire haven't been tested at all yet, so reports on those are especially welcome.

## DS, 3DS and Game Boy Color (experimental)
Welcome, but expect rough edges. Please say which game and version you used.

## How to report a problem
Open an issue at https://github.com/channancy061/poke-studio-customizer/issues with:
1. **Game or project and version.** The app shows it at the top left, e.g. "pokeemerald-expansion 1.17.1" or "Pokémon Emerald".
2. **App version** (Help & about) and your operating system.
3. **Steps:** what you changed, what you expected, what happened.
4. **Screenshots** of messages; for build problems, the build log (Build & test → Show or hide the build log).
5. For in-game problems: where in the game, and whether it happens on a new save.

Logs are in `%LOCALAPPDATA%\PokeStudio\Logs` (Windows) or `~/.local/state/poke-studio/logs` (Linux). They contain file
paths from your computer, so look through them before attaching.

## Ideas and requests
Feature ideas are welcome as issues too. Please describe what you want to make in your hack, not only the button you'd
like to have.
