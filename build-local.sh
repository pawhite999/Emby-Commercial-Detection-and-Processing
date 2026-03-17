#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUT="/tmp/commdetect-local"

echo "Building..."
/usr/bin/dotnet publish "$SCRIPT_DIR/src/CommDetect.CLI/CommDetect.CLI.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -o "$OUT"

echo "Installing to /usr/bin/commdetect..."
sudo cp "$OUT/commdetect" /usr/bin/commdetect

echo "Done."
commdetect --version
