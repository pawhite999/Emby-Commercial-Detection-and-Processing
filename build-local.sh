#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUT="/tmp/commdetect-local"

echo "Building..."
/usr/bin/dotnet publish "$SCRIPT_DIR/src/CommDetect.CLI/CommDetect.CLI.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -o "$OUT"

INSTALL_DIR="/usr/bin"
CONFIG_DIR="$INSTALL_DIR/config"

echo "Installing to $INSTALL_DIR/commdetect..."
sudo cp "$OUT/commdetect" "$INSTALL_DIR/commdetect"

echo "Installing configs to $CONFIG_DIR/..."
sudo mkdir -p "$CONFIG_DIR"
sudo cp "$SCRIPT_DIR/config/"*.ini "$CONFIG_DIR/"

echo "Done."
commdetect --version
