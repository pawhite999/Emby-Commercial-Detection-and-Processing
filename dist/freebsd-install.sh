#!/bin/sh
# CommDetect FreeBSD Installer
# Run this script inside your FreeBSD jail after completing the host-side prerequisites.
#
# Host prerequisites (run on the TrueNAS/FreeBSD host BEFORE running this script):
#   1. kldload linux64
#   2. sysrc linux_enable="YES"
#      NOTE (TrueNAS): sysrc may not fire before jails start. For reliability, also add
#      a Pre Init script in TrueNAS UI: System → Init/Shutdown Scripts → Add
#        Type: Command  |  Command: kldload linux64  |  When: Pre Init
#   3. Install linux_base-rl9 in the jail first (pkg install linux_base-rl9),
#      then mount from the host into the jail's filesystem:
#        mount -t linprocfs linprocfs <jailroot>/compat/linux/proc
#        mount -t linsysfs linsysfs <jailroot>/compat/linux/sys
#      Where <jailroot> is e.g. /mnt/Whitvol/iocage/jails/<jailname>/root
#   4. For persistence, add those lines to the host's /etc/fstab
#
# Note: The iocage fstab approach may not activate mounts reliably on TrueNAS.
#       Use the host-mount approach above instead.
# Note: 'mount | grep linux' inside the jail shows nothing even when working — this is normal.

set -e

GITHUB_REPO="pawhite999/Emby-Commercial-Detection-and-Processing"
BINARY_NAME="commdetect-freebsd13"
INSTALL_DIR="/usr/local/bin"
LOG_DIR="/var/log/commdetect"
TEMP_DIR="/var/tmp/commdetect"

# ── helpers ──────────────────────────────────────────────────────────────────

die() { echo "Error: $1" >&2; exit 1; }

confirm() {
    printf "%s [y/N]: " "$1"
    read answer
    case "$answer" in
        [yY]*) return 0 ;;
        *) return 1 ;;
    esac
}

# ── checks ───────────────────────────────────────────────────────────────────

[ "$(id -u)" -eq 0 ] || die "This script must be run as root"

# Verify Linux compat is active
kldstat | grep -q linux || die "Linux compat kernel module not loaded. On the HOST run: kldload linux64"

# Verify linprocfs is mounted
mount | grep -q linprocfs || die "linprocfs not mounted. See README for iocage fstab setup."

echo ""
echo "=== CommDetect FreeBSD Installer ==="
echo ""

# ── step 1: dependencies ─────────────────────────────────────────────────────

echo "Installing dependencies..."
echo "  linux_base-rl9 (Rocky Linux 9 — required for .NET runtime)"
echo "  ffmpeg          (video analysis + ffprobe)"
echo "  mkvtoolnix      (mkvmerge for MKV output)"
echo ""
pkg install -y linux_base-rl9 ffmpeg mkvtoolnix

# ── step 2: fetch binary ─────────────────────────────────────────────────────

echo ""
echo "Fetching latest CommDetect binary..."
LATEST_TAG=$(fetch -qo - "https://api.github.com/repos/${GITHUB_REPO}/releases/latest" \
    | grep '"tag_name"' | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')

[ -n "$LATEST_TAG" ] || die "Could not determine latest release tag. Check your internet connection."

echo "  Latest release: ${LATEST_TAG}"
BINARY_URL="https://github.com/${GITHUB_REPO}/releases/download/${LATEST_TAG}/${BINARY_NAME}"
fetch -o "/tmp/${BINARY_NAME}" "$BINARY_URL" || die "Failed to download binary from ${BINARY_URL}"
install -m 755 "/tmp/${BINARY_NAME}" "${INSTALL_DIR}/commdetect"
rm -f "/tmp/${BINARY_NAME}"
echo "  Installed: ${INSTALL_DIR}/commdetect"

# ── step 3: config files ─────────────────────────────────────────────────────

echo ""
echo "Fetching config files..."
RAW_BASE="https://raw.githubusercontent.com/${GITHUB_REPO}/main/config"

fetch -o "${INSTALL_DIR}/commdetect.ini" "${RAW_BASE}/commdetect.ini" \
    || die "Failed to fetch commdetect.ini"
echo "  ${INSTALL_DIR}/commdetect.ini"

fetch -o "${INSTALL_DIR}/comprocess.ini" "${RAW_BASE}/comprocess.ini" \
    || die "Failed to fetch comprocess.ini"
echo "  ${INSTALL_DIR}/comprocess.ini"

# ── step 4: directories ───────────────────────────────────────────────────────

echo ""
echo "Creating directories..."
mkdir -p "${LOG_DIR}/log" "${LOG_DIR}/edl" "${TEMP_DIR}"
chmod 777 "${LOG_DIR}/log" "${LOG_DIR}/edl" "${TEMP_DIR}"
echo "  ${LOG_DIR}/log  (run logs)"
echo "  ${LOG_DIR}/edl  (EDL archive)"
echo "  ${TEMP_DIR}  (temp files during commercial cutting)"

# ── step 5: patch config files ───────────────────────────────────────────────

echo ""
echo "Configuring commdetect.ini and comprocess.ini..."

# Log directories (persistent, emby-writable)
sed -i '' "s|^log_dir=.*|log_dir=${LOG_DIR}/log|" "${INSTALL_DIR}/commdetect.ini"
sed -i '' "s|^edl_dir=.*|edl_dir=${LOG_DIR}/edl|" "${INSTALL_DIR}/commdetect.ini"

# Temp directory (persistent)
sed -i '' "s|^temp_dir=.*|temp_dir=${TEMP_DIR}|" "${INSTALL_DIR}/comprocess.ini"

# ── step 6: padding ───────────────────────────────────────────────────────────

echo ""
echo "DVR padding settings:"
echo "  These should match the pre/post padding configured in your Emby Live TV settings."
echo "  If recorded shows are cut too short at the start or end, adjust these values."
echo ""
printf "Pre-recording padding in seconds (default 120): "
read PRE_PAD
PRE_PAD=${PRE_PAD:-120}
printf "Post-recording padding in seconds (default 120): "
read POST_PAD
POST_PAD=${POST_PAD:-120}

sed -i '' "s|^skip_start_seconds=.*|skip_start_seconds=${PRE_PAD}|" "${INSTALL_DIR}/commdetect.ini"
sed -i '' "s|^skip_end_seconds=.*|skip_end_seconds=${POST_PAD}|" "${INSTALL_DIR}/commdetect.ini"

# ── step 7: emby integration (optional) ──────────────────────────────────────

echo ""
echo "Emby API integration (optional):"
echo "  Allows CommDetect to query your Emby server for recording metadata."
echo "  EPG data may improve boundary detection accuracy."
echo "  API key: Emby Dashboard → Advanced → API Keys"
echo ""
if confirm "Configure Emby API integration?"; then
    printf "Emby server URL (e.g. http://192.168.1.97:8096): "
    read EMBY_URL
    printf "Emby API key: "
    read EMBY_KEY
    if [ -n "$EMBY_URL" ] && [ -n "$EMBY_KEY" ]; then
        sed -i '' "s|^server_url=.*|server_url=${EMBY_URL}|" "${INSTALL_DIR}/commdetect.ini"
        sed -i '' "s|^api_key=.*|api_key=${EMBY_KEY}|" "${INSTALL_DIR}/commdetect.ini"
        echo "  Emby integration configured."
    else
        echo "  Skipped (incomplete input)."
    fi
else
    echo "  Skipped. Edit ${INSTALL_DIR}/commdetect.ini later to add Emby credentials."
fi

# ── done ──────────────────────────────────────────────────────────────────────

echo ""
echo "=== Installation complete ==="
echo ""
echo "Test your installation:"
echo "  commdetect process \"/path/to/recording.ts\" --verbose"
echo ""
echo "Emby post-processor settings:"
echo "  Application: ${INSTALL_DIR}/commdetect"
echo "  Arguments:   process \"{path}\""
echo ""
echo "Log files will be written to: ${LOG_DIR}/log"
echo "EDL archive:                  ${LOG_DIR}/edl"
echo ""
echo "To adjust detection settings, edit: ${INSTALL_DIR}/commdetect.ini"
echo "To adjust processing settings, edit: ${INSTALL_DIR}/comprocess.ini"
