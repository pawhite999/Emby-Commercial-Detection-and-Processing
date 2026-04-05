#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUT="/tmp/commdetect-freebsd"

# Target host — set EMBY_JAIL to override, e.g.:
#   EMBY_JAIL=user@192.168.1.34 ./build-freebsd.sh
EMBY_JAIL="${EMBY_JAIL:-}"
REMOTE_BIN="/usr/local/bin"
REMOTE_CONFIG="/usr/local/bin/config"

echo "Building for freebsd-x64..."
/usr/bin/dotnet publish "$SCRIPT_DIR/src/CommDetect.CLI/CommDetect.CLI.csproj" \
    -c Release -r freebsd-x64 --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=true \
    -o "$OUT"

echo "Build complete: $OUT/commdetect"

if [ -n "$EMBY_JAIL" ]; then
    echo "Deploying to $EMBY_JAIL..."
    scp "$OUT/commdetect" "$EMBY_JAIL:$REMOTE_BIN/commdetect"
    ssh "$EMBY_JAIL" "mkdir -p $REMOTE_CONFIG"
    scp "$SCRIPT_DIR/config/"*.ini "$EMBY_JAIL:$REMOTE_CONFIG/"
    ssh "$EMBY_JAIL" "$REMOTE_BIN/commdetect --version"
    echo "Done."
else
    echo ""
    echo "To deploy, set EMBY_JAIL and re-run:"
    echo "  EMBY_JAIL=user@192.168.1.34 ./build-freebsd.sh"
    echo ""
    echo "Or deploy manually:"
    echo "  scp $OUT/commdetect user@jail:$REMOTE_BIN/"
    echo "  scp config/*.ini   user@jail:$REMOTE_CONFIG/"
fi
