# Third-party notices

Poke Studio Customizer is released under the [MIT Licence](LICENSE) and uses **only open-source components**.
`build/license-audit.py` checks every NuGet dependency (direct and transitive) on each CI run and fails the build if
anything is not under an approved licence (MIT, Apache-2.0, BSD-2/3-Clause, ISC, OFL-1.1, Zlib, MS-PL, Unlicense).

No GPL code is used or copied into this app. No game files, sprites, models, music, text or other Nintendo /
Creatures / GAME FREAK / The Pokémon Company assets are included.

## 1. Shipped with the app

| Component | Licence | Project |
|---|---|---|
| Avalonia UI 11 (+ Desktop, Skia, Win32, X11, FreeDesktop, Native, Fluent/Simple themes, ColorPicker, Remote.Protocol) | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md) | https://github.com/AvaloniaUI/Avalonia |
| ANGLE (Avalonia.Angle.Windows.Natives) | [BSD-3-Clause](https://chromium.googlesource.com/angle/angle/+/refs/heads/main/LICENSE) | https://chromium.googlesource.com/angle/angle |
| SkiaSharp, HarfBuzzSharp | [MIT](https://github.com/mono/SkiaSharp/blob/main/LICENSE.md) | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | [MIT](https://github.com/kekekeks/MicroCom/blob/master/LICENSE) | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | [MIT](https://github.com/tmds/Tmds.DBus/blob/main/LICENSE) | https://github.com/tmds/Tmds.DBus |
| CommunityToolkit.Mvvm | [MIT](https://github.com/CommunityToolkit/dotnet/blob/main/License.md) | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite | [MIT](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) | https://github.com/dotnet/efcore |
| SQLitePCLRaw | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT) | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (via e_sqlite3) | [Public domain](https://sqlite.org/copyright.html) | https://sqlite.org |
| .NET runtime (self-contained builds) | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | https://dotnet.microsoft.com |
| Figtree font | [SIL OFL 1.1](https://openfontlicense.org) (`src/PokeStudio.App/Assets/Fonts/Figtree-OFL.txt`) | https://github.com/erikdkennedy/figtree |
| Silkscreen font | [SIL OFL 1.1](https://openfontlicense.org) (`src/PokeStudio.App/Assets/Fonts/Silkscreen-OFL.txt`) | https://fonts.google.com/specimen/Silkscreen |
| JetBrains Mono font | [SIL OFL 1.1](https://openfontlicense.org) (`src/PokeStudio.App/Assets/Fonts/JetBrainsMono-OFL.txt`) | https://github.com/JetBrains/JetBrainsMono |
| Icons | Original drawings, style inspired by [Lucide](https://lucide.dev) ([ISC](https://lucide.dev/license)) | — |
| Logo and app icon | Original (this project) | — |

## 2. Development, testing and release (not shipped)

| Tool | Licence | Used for |
|---|---|---|
| [.NET SDK 10](https://dotnet.microsoft.com/download) | [MIT](https://github.com/dotnet/sdk/blob/main/LICENSE.TXT) | Building the app |
| [Git](https://git-scm.com) | [GPL-2.0](https://git-scm.com/about/free-and-open-source) | Version control for this repository (and `git clone` of decomp projects) |
| [GitHub Actions](https://github.com/features/actions): [checkout](https://github.com/actions/checkout), [setup-dotnet](https://github.com/actions/setup-dotnet), [upload-artifact](https://github.com/actions/upload-artifact), [download-artifact](https://github.com/actions/download-artifact) | MIT | Automatic builds and tests |
| [GitHub CLI](https://cli.github.com) | [MIT](https://github.com/cli/cli/blob/trunk/LICENSE) | Publishing releases from CI |
| [xUnit](https://xunit.net) | [Apache-2.0](https://github.com/xunit/xunit/blob/main/LICENSE) | Tests |
| [Microsoft.NET.Test.Sdk / VSTest](https://github.com/microsoft/vstest) | [MIT](https://github.com/microsoft/vstest/blob/main/LICENSE) | Tests |
| Avalonia.Headless(.XUnit), Avalonia.Diagnostics, Avalonia.BuildServices | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md) | UI tests, debugging, build. Opt out of Avalonia's build-time telemetry with `AVALONIA_TELEMETRY_OPTOUT=1` (CI sets it) |
| [Python](https://www.python.org) | [PSF Licence](https://docs.python.org/3/license.html) | `build/license-audit.py` |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | [Inno Setup Licence](https://jrsoftware.org/files/is/license.txt) | Building the Windows installer |
| [Chocolatey](https://chocolatey.org) | [Apache-2.0](https://github.com/chocolatey/choco/blob/develop/LICENSE) | Installing Inno Setup in CI |
| [appimagetool](https://github.com/AppImage/appimagetool) | [MIT](https://github.com/AppImage/appimagetool/blob/main/LICENSE) | Building the Linux AppImage |
| [GNU Arm Embedded Toolchain](https://developer.arm.com/downloads/-/gnu-rm) (`gcc-arm-none-eabi`) | [GPL-3.0-or-later with the GCC Runtime Library Exception](https://www.gnu.org/licenses/gcc-exception-3.1.html) | Compiling the app's own adaptive-slot code for GBA ROMs (`src/PokeStudio.Modules.Gen3Rom/Inject`) |
| [agbcc](https://github.com/pret/agbcc) | [GPL-2.0](https://github.com/pret/agbcc/blob/master/LICENSE) | Developer tool only: rebuilding the original games from the [pret](https://github.com/pret) decompilations to confirm the addresses of game routines the injected code calls. Never shipped |

## 3. Downloaded only when you ask (never bundled)

| Project | Notes |
|---|---|
| [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion) (release expansion/1.17.1) | Downloaded from its official GitHub release into a folder you pick. It is based on the [pret pokeemerald](https://github.com/pret/pokeemerald) decompilation, contains material from the original games and has **no open-source licence**: use it only with games you own. Nothing from it is included in this app. |

## 4. Projects the app can edit (you provide them)

| Project | Notes |
|---|---|
| [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion) | See above |
| [pokefirered-expansion](https://github.com/cawtds/pokefirered-expansion) | Community port of pokeemerald-expansion to [pret pokefirered](https://github.com/pret/pokefirered); same notes as above |

## 5. Tools you install separately (never bundled)

Building decomp projects and playing your game use free tools you install yourself, each under its own licence:

| Tool | Licence | Used for |
|---|---|---|
| [Windows Subsystem for Linux (WSL)](https://github.com/microsoft/WSL) | [MIT](https://github.com/microsoft/WSL/blob/master/LICENSE) | Running the Linux build tools on Windows |
| [Ubuntu](https://ubuntu.com) | [Mostly GPL and other free licences](https://ubuntu.com/legal/open-source-licences) | The Linux system inside WSL |
| [GNU Make](https://www.gnu.org/software/make/) | [GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html) | Running the project's build |
| [GCC](https://gcc.gnu.org) (`build-essential`, `gcc-arm-none-eabi`) | [GPL-3.0-or-later with the GCC Runtime Library Exception](https://www.gnu.org/licenses/gcc-exception-3.1.html) | Compiling the game |
| [GNU Binutils](https://www.gnu.org/software/binutils/) (`binutils-arm-none-eabi`) | [GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html) | Assembling and linking the game |
| [Newlib](https://sourceware.org/newlib/) (`libnewlib-arm-none-eabi`) | [BSD-style and other free licences](https://sourceware.org/newlib/COPYING.NEWLIB) | C library for the GBA |
| [libpng](http://www.libpng.org/pub/png/libpng.html) (`libpng-dev`) | [libpng licence](http://www.libpng.org/pub/png/src/libpng-LICENSE.txt) | The project's graphics tools |
| [zlib](https://zlib.net) | [zlib licence](https://zlib.net/zlib_license.html) | Used by libpng |
| [pkg-config](https://www.freedesktop.org/wiki/Software/pkg-config/) | [GPL-2.0-or-later](https://gitlab.freedesktop.org/pkg-config/pkg-config/-/blob/master/COPYING) | Finding libpng during the build |
| [Python 3](https://www.python.org) | [PSF Licence](https://docs.python.org/3/license.html) | The project's build scripts |
| [Git](https://git-scm.com) | [GPL-2.0](https://git-scm.com/about/free-and-open-source) | Getting decomp projects with their history |
| [mGBA](https://mgba.io) | [MPL-2.0](https://github.com/mgba-emu/mgba/blob/master/LICENSE) | Playing GBA / GBC games (suggested) |
| [VisualBoyAdvance-M](https://github.com/visualboyadvance-m/visualboyadvance-m) | [GPL-2.0](https://github.com/visualboyadvance-m/visualboyadvance-m/blob/master/doc/gpl.txt) | Playing GBA / GBC games |
| [melonDS](https://melonds.kuribo64.net) | [GPL-3.0](https://github.com/melonDS-emu/melonDS/blob/master/LICENSE) | Playing DS games (suggested) |
| [DeSmuME](https://desmume.org) | [GPL-2.0](https://github.com/TASEmulators/desmume/blob/master/license.txt) | Playing DS games |
| [Azahar](https://azahar-emu.org) | [GPL-2.0](https://github.com/azahar-emu/azahar/blob/master/license.txt) | Playing 3DS games (suggested) |
| [Citra](https://github.com/citra-emu) / [Lime3DS](https://github.com/Lime3DS) | GPL-2.0 | Playing 3DS games (the app writes mods to Citra's mod folder) |
| [Floating IPS (Flips)](https://github.com/Alcaro/Flips) | [GPL-3.0](https://github.com/Alcaro/Flips/blob/master/COPYING.gpl3) | Applying patches (for your players; the app makes BPS patches itself) |

The app only starts these programs (for building and testing); it doesn't include or modify them.

## 6. File formats

The app makes **BPS patches** with its own code (`src/PokeStudio.Core/Build/Bps.cs`, MIT), written from the format's
public description by its author, byuu (Near). No code from Flips or other patchers is used.

## 7. Code the app writes into your project or ROM

When you use Game options, badge shops and similar tools on a decomp project, the app writes new code into that
project (`src/poke_studio.c`, `include/constants/poke_studio.h`, `data/scripts/poke_studio_options.inc` and short
marked lines in existing files). That generated code is part of this app and is covered by the MIT Licence, so you can
ship it in your hack.

For unmodified Pokémon FireRed (US 1.0) and Emerald (US) ROMs, the adaptive boss slot is added as a small piece of
compiled code written by this project (`src/PokeStudio.Modules.Gen3Rom/Inject/adaptive.c`, MIT) plus a table of
candidates. It calls the game's own routines at fixed addresses; those addresses were confirmed by rebuilding the
original games from the pret decompilations on the developer's computer. No code from the games or the decompilations
is included.
