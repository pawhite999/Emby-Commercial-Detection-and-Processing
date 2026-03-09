#!/usr/bin/env bash
set -euo pipefail

REPO="pawhite999/Emby-Commercial-Detection-and-Processing"
BINARY="commdetect"
GITHUB_API="https://api.github.com/repos/${REPO}/releases/latest"

# ── Detect OS ────────────────────────────────────────────────────────────────
case "$(uname -s)" in
    Linux*)  OS="linux"  ;;
    Darwin*) OS="osx"    ;;
    *)
        echo "Unsupported OS: $(uname -s)" >&2
        exit 1
        ;;
esac

# ── Detect architecture ───────────────────────────────────────────────────────
case "$(uname -m)" in
    x86_64)        ARCH="x64"   ;;
    aarch64|arm64) ARCH="arm64" ;;
    *)
        echo "Unsupported architecture: $(uname -m)" >&2
        exit 1
        ;;
esac

# ── Alpine Linux uses musl libc ───────────────────────────────────────────────
if [ "$OS" = "linux" ] && [ -f /etc/alpine-release ]; then
    PLATFORM="linux-musl-x64"
else
    PLATFORM="${OS}-${ARCH}"
fi

ASSET_NAME="${BINARY}-${PLATFORM}"

echo "Detected platform: ${PLATFORM}"
echo "Fetching latest release info..."

# ── Get download URL ──────────────────────────────────────────────────────────
DOWNLOAD_URL=$(curl -fsSL "${GITHUB_API}" \
    | grep -o "\"browser_download_url\": *\"[^\"]*/${ASSET_NAME}\"" \
    | grep -o "https://[^\"]*")

if [ -z "$DOWNLOAD_URL" ]; then
    echo "Error: no release asset found for platform '${PLATFORM}'." >&2
    echo "Available assets:" >&2
    curl -fsSL "${GITHUB_API}" \
        | grep "browser_download_url" \
        | grep -o '"https://[^"]*"' >&2
    exit 1
fi

# ── Choose install directory ──────────────────────────────────────────────────
# Default: ~/.local/bin (no sudo required).
# Pass --system to install to /usr/local/bin instead (requires sudo).
INSTALL_DIR="$HOME/.local/bin"
for arg in "$@"; do
    if [ "$arg" = "--system" ]; then
        INSTALL_DIR="/usr/local/bin"
    fi
done
mkdir -p "$INSTALL_DIR"

INSTALL_PATH="${INSTALL_DIR}/${BINARY}"

# ── Download ──────────────────────────────────────────────────────────────────
echo "Downloading ${ASSET_NAME}..."
curl -fsSL --progress-bar -o "${INSTALL_PATH}" "${DOWNLOAD_URL}"
chmod +x "${INSTALL_PATH}"

echo ""
echo "Installed: ${INSTALL_PATH}"

# ── PATH check ────────────────────────────────────────────────────────────────
if ! echo "$PATH" | tr ':' '\n' | grep -qx "${INSTALL_DIR}"; then
    echo ""
    echo "Note: ${INSTALL_DIR} is not in your PATH."
    echo "Add it permanently by running:"
    echo ""
    echo "  echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.bashrc && source ~/.bashrc"
fi

echo ""
echo "Run 'commdetect --help' to get started."
