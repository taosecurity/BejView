#!/bin/sh
# Installs a single-file Linux app for the current user only (no sudo): the program in ~/.local/bin, its .desktop
# entry in ~/.local/share/applications and its icons in ~/.local/share/icons/hicolor, so KDE Plasma, GNOME and other
# desktops show its name and icon in the launcher and task manager. Run it from the unpacked folder:
#   ./install.sh              install (or update)
#   ./install.sh --uninstall  remove what it installed (settings in ~/.config are kept)
# Generic: only the three lines below differ between apps (BejView, SquareSnap). APP_ID must match the .desktop file
# name and the app's Wayland app_id, which is how KWin and the portals match a window to its entry.
APP_ID=bejview
APP_NAME=BejView
BINARY=bejview

set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
BIN_DIR="${XDG_BIN_HOME:-$HOME/.local/bin}"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS_DIR="$DATA_DIR/applications"
ICONS_DIR="$DATA_DIR/icons/hicolor"

refresh() {
    # Each is optional: whichever the desktop has picks up the change at once; otherwise it shows after a re-login.
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$APPS_DIR" 2>/dev/null || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$ICONS_DIR" 2>/dev/null || true
    command -v kbuildsycoca6 >/dev/null 2>&1 && kbuildsycoca6 --noincremental >/dev/null 2>&1 || true
    command -v kbuildsycoca5 >/dev/null 2>&1 && kbuildsycoca5 --noincremental >/dev/null 2>&1 || true
}

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$BIN_DIR/$BINARY" "$APPS_DIR/$APP_ID.desktop"
    find "$ICONS_DIR" -path "*/apps/$APP_ID.png" -delete 2>/dev/null || true
    find "$ICONS_DIR" -path "*/apps/$APP_ID.svg" -delete 2>/dev/null || true
    refresh
    echo "$APP_NAME removed. Its settings and log in ~/.config are kept; delete them by hand if you want."
    exit 0
fi
if [ $# -gt 0 ]; then
    echo "usage: ./install.sh [--uninstall]" >&2
    exit 2
fi

for f in "$BINARY" "$APP_ID.desktop"; do
    if [ ! -f "$HERE/$f" ]; then
        echo "install.sh: $f is missing next to install.sh ($HERE). Unpack the whole archive and run it from there." >&2
        exit 3
    fi
done

mkdir -p "$BIN_DIR" "$APPS_DIR"
install -m 755 "$HERE/$BINARY" "$BIN_DIR/$BINARY"
# The menu entry runs the program by its full path, so it works even when ~/.local/bin isn't on PATH.
sed "s|^Exec=$BINARY|Exec=\"$BIN_DIR/$BINARY\"|" "$HERE/$APP_ID.desktop" > "$APPS_DIR/$APP_ID.desktop"
chmod 644 "$APPS_DIR/$APP_ID.desktop"
if [ -d "$HERE/icons/hicolor" ]; then
    (cd "$HERE/icons/hicolor" && find . -type f -name "$APP_ID.*") | while read -r icon; do
        mkdir -p "$ICONS_DIR/$(dirname "$icon")"
        install -m 644 "$HERE/icons/hicolor/$icon" "$ICONS_DIR/$icon"
    done
fi
refresh

echo "$APP_NAME installed:"
echo "  program:    $BIN_DIR/$BINARY"
echo "  menu entry: $APPS_DIR/$APP_ID.desktop"
echo "  icons:      $ICONS_DIR/*/apps/$APP_ID.png"
case ":$PATH:" in
    *":$BIN_DIR:"*) echo "Run it from the app menu or type: $BINARY" ;;
    *) echo "Run it from the app menu. To type '$BINARY' in a terminal, add $BIN_DIR to PATH (most desktops do once it exists: log out and in)." ;;
esac
echo "To remove it again: ./install.sh --uninstall (from this folder)"
