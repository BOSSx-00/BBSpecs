#!/usr/bin/env bash
#
# Builds the downloadable BBSpecs app for Linux.
#
# Produces a single self-contained binary in dist/ plus a .desktop launcher and
# an installer. Nobody needs the .NET runtime installed: one file, mark it
# executable, run it.
#
# Usage:  ./build/build-linux.sh [Release|Debug] [linux-x64|linux-arm64]

set -euo pipefail

CONFIGURATION="${1:-Release}"
RUNTIME="${2:-linux-x64}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/BBSpecs/BBSpecs.csproj"
STAGING="$ROOT/build/staging/$RUNTIME"
DIST="$ROOT/dist"

echo "BBSpecs - building for $RUNTIME"

# The version lives in the .csproj, so read it back rather than duplicating it.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"
echo "  version  $VERSION"

rm -rf "$STAGING"
mkdir -p "$DIST"

echo "  publishing..."
dotnet publish "$PROJECT" \
    --configuration "$CONFIGURATION" \
    --runtime "$RUNTIME" \
    --output "$STAGING" \
    -p:BBSpecsTarget=net9.0 \
    --nologo \
    --verbosity minimal

BIN="$STAGING/BBSpecs"
[ -f "$BIN" ] || { echo "Expected $BIN but it wasn't produced." >&2; exit 1; }

TARGET="$DIST/BBSpecs-$VERSION-$RUNTIME"
cp "$BIN" "$TARGET"
chmod +x "$TARGET"

# Icon for desktop launchers.
cp "$ROOT/img/bbspecs_logo.png" "$DIST/bbspecs.png"

# A .desktop entry so it appears in the applications menu. pkexec asks for the
# password using the desktop's own dialog, which is what lets BBSpecs read the
# readings that need root.
cat > "$DIST/bbspecs.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=BBSpecs
GenericName=Hardware Monitor
Comment=See what's inside your PC and how it's running
Exec=/usr/local/bin/bbspecs
Icon=bbspecs
Terminal=false
Categories=System;Monitor;
Keywords=hardware;system;cpu;gpu;temperature;specs;
DESKTOP

cat > "$DIST/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Installs BBSpecs for every user on this machine.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$(find "$HERE" -maxdepth 1 -name 'BBSpecs-*-linux-*' -type f | head -1)"
[ -n "$BIN" ] || { echo "No BBSpecs binary found next to this script." >&2; exit 1; }

echo "Installing BBSpecs..."
sudo install -Dm755 "$BIN" /usr/local/bin/bbspecs
sudo install -Dm644 "$HERE/bbspecs.png" /usr/share/icons/hicolor/512x512/apps/bbspecs.png
sudo install -Dm644 "$HERE/bbspecs.desktop" /usr/share/applications/bbspecs.desktop
sudo gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true

echo
echo "Done. Launch it from your applications menu, or run: bbspecs"
echo
echo "BBSpecs works as a normal user, but temperatures, the memory slot layout"
echo "and drive health need root. Either start it from the menu and use the"
echo "'Restart with full access' button, or run: sudo -E bbspecs"
INSTALL
chmod +x "$DIST/install.sh"

SIZE="$(du -h "$TARGET" | cut -f1)"
echo
echo "  Done: $TARGET ($SIZE)"
echo "  Also wrote install.sh, bbspecs.desktop and bbspecs.png to dist/"
echo
echo "  BBSpecs needs a system webview. On most distributions:"
echo "    Debian/Ubuntu  sudo apt install libwebkit2gtk-4.1-0"
echo "    Fedora         sudo dnf install webkit2gtk4.1"
echo "    Arch           sudo pacman -S webkit2gtk-4.1"
