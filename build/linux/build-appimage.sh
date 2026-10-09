#!/usr/bin/env bash
# Linux: self-contained single-file publish + AppImage.
# Usage (from the repo root):  ./build/linux/build-appimage.sh [version]
# Needs: .NET 10 SDK, curl. Downloads appimagetool (MIT) into artifacts/tools on first run.
set -euo pipefail
VERSION="${1:-0.1.0}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ARTIFACTS="$ROOT/artifacts"
PUBLISH="$ARTIFACTS/publish/linux-x64"
APPDIR="$ARTIFACTS/AppDir"
export AVALONIA_TELEMETRY_OPTOUT=1

dotnet publish "$ROOT/src/PokeStudio.App/PokeStudio.App.csproj" -c Release -r linux-x64 -p:Version="$VERSION" -o "$PUBLISH"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons"
cp -r "$PUBLISH/." "$APPDIR/usr/bin/"
cp -r "$ROOT/build/linux/icons/hicolor" "$APPDIR/usr/share/icons/"
cp "$ROOT/build/linux/poke-studio.desktop" "$APPDIR/usr/share/applications/"
cp "$ROOT/build/linux/poke-studio.desktop" "$APPDIR/"
cp "$ROOT/build/linux/icons/hicolor/256x256/apps/poke-studio.png" "$APPDIR/poke-studio.png"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$APPDIR/usr/bin/"
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/PokeStudio" "$@"
EOF
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/PokeStudio"

TOOL="$ARTIFACTS/tools/appimagetool-x86_64.AppImage"
if [ ! -x "$TOOL" ]; then
  mkdir -p "$(dirname "$TOOL")"
  curl -fsSL -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$TOOL"
fi

mkdir -p "$ARTIFACTS/appimage"
# Extract-and-run so FUSE isn't required (CI containers, WSL).
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$TOOL" "$APPDIR" "$ARTIFACTS/appimage/PokeStudio-$VERSION-x86_64.AppImage"
echo "AppImage: $ARTIFACTS/appimage/PokeStudio-$VERSION-x86_64.AppImage"
