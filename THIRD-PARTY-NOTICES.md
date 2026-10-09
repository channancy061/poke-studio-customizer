# Third-party notices

Poke Studio Customizer is MIT-licensed and uses **only open-source components**.
`build/license-audit.py` checks every NuGet dependency (direct and transitive) on each CI run and
fails the build if anything is not under an approved licence
(MIT, Apache-2.0, BSD-2/3-Clause, ISC, OFL-1.1, Zlib, MS-PL, Unlicense).

No GPL code is used or copied. No game files, sprites, models, music, text or other
Nintendo / Game Freak / Creatures assets are included.

## Shipped with the app
| Component | Licence |
|---|---|
| Avalonia UI 11 (+ Desktop, Skia, Win32, X11, FreeDesktop, Native, Fluent/Simple themes, ColorPicker, Remote.Protocol) | MIT |
| ANGLE (Avalonia.Angle.Windows.Natives) | BSD-3-Clause |
| SkiaSharp, HarfBuzzSharp | MIT |
| MicroCom.Runtime, Tmds.DBus.Protocol | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.DependencyInjection | MIT |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw | Apache-2.0 |
| SQLite (via e_sqlite3) | Public domain |
| .NET runtime (self-contained builds) | MIT |
| Figtree font | SIL OFL 1.1 — `src/PokeStudio.App/Assets/Fonts/Figtree-OFL.txt` |
| Silkscreen font | SIL OFL 1.1 — `src/PokeStudio.App/Assets/Fonts/Silkscreen-OFL.txt` |
| JetBrains Mono font | SIL OFL 1.1 — `src/PokeStudio.App/Assets/Fonts/JetBrainsMono-OFL.txt` |
| Icons | Original drawings (style inspired by Lucide, ISC) |
| Logo and app icon | Original (this project) |

## Development and test only (not shipped)
xUnit (Apache-2.0), Microsoft.NET.Test.Sdk / TestPlatform / CodeCoverage (MIT),
Avalonia.Headless(.XUnit), Avalonia.Diagnostics (MIT), Avalonia.BuildServices (MIT, build-time;
opt out of its telemetry with `AVALONIA_TELEMETRY_OPTOUT=1`, which CI sets).

## Tools the user installs separately (never bundled)
devkitARM, make, emulators such as mGBA / melonDS / Azahar — each under its own licence.
Inno Setup (used to build the Windows installer) and appimagetool (used to build the AppImage) are
build tools only and are not shipped.
