# Poke Studio Customizer

> **Working title.** Choose a trademark-safe name before any public release (candidates: Sproutforge,
> Hatchlab, Pocket Atelier, Critterworks, Tamer's Toolkit). Rename in `src/PokeStudio.Core/AppInfo.cs`,
> `Directory.Build.props`, `build/windows/PokeStudio.iss` and `build/linux/poke-studio.desktop`.

A friendly desktop editor (Windows + Linux) for customising games you legally own.
It ships **no game files or assets**, never modifies your originals, and uses only open-source components (MIT-licensed).

## Status: Phase 1
Solution skeleton, plugin system, projects, undo/redo + change log, backups/restore, settings,
Avalonia shell with light/dark theming, CI on Windows + Linux, Windows installer and Linux AppImage.
Real game formats begin in Phase 2. Until then, the **Demo** module edits an invented sample
(`samples/Demo Valley.demogame.json`) so the whole pipeline can be tried.

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

## Where data lives
| | Windows | Linux |
|---|---|---|
| Settings | `%APPDATA%\PokeStudio\settings.json` | `$XDG_CONFIG_HOME/poke-studio` (default `~/.config/poke-studio`) |
| Backups | `%LOCALAPPDATA%\PokeStudio\Backups` | `$XDG_DATA_HOME/poke-studio/backups` |
| Logs | `%LOCALAPPDATA%\PokeStudio\Logs` | `$XDG_STATE_HOME/poke-studio/logs` |
| Projects | `Documents\Poke Studio` | `~/Documents/Poke Studio` |

## Ground rules
No ROMs, BIOS, keys or game assets are shipped, downloaded or linked. No DRM circumvention.
Originals are never written. No GPL code. See `THIRD-PARTY-NOTICES.md`.
