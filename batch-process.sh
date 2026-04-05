#!/bin/bash
# batch-process.sh — Run commdetect process on all video files in a folder.
#
# Usage: batch-process.sh [options] <video-folder>
#
# Options:
#   --config <file>    Pass a per-show profile to commdetect (e.g. config/colbert.ini)
#   --sort episode     Sort by S##E## episode number instead of alphabetically
#   --dry-run          List files that would be processed without running commdetect
#   --force            Pass --force to commdetect (overwrite existing EDL/output files)
#   -h, --help         Show this help

set -euo pipefail

COMMDETECT="${COMMDETECT:-commdetect}"
SORT_MODE="alpha"
CONFIG_FILE=""
DRY_RUN=false
FORCE=""

EXTENSIONS=(ts mpg mpeg mp4 mkv avi wtv m4v)

usage() {
    sed -n '2,12p' "$0" | sed 's/^# //' | sed 's/^#//'
    exit 1
}

# ── argument parsing ─────────────────────────────────────────────────────────

FOLDER=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --config)   CONFIG_FILE="$2"; shift 2 ;;
        --sort)     SORT_MODE="$2";   shift 2 ;;
        --dry-run)  DRY_RUN=true;     shift   ;;
        --force)    FORCE="--force";  shift   ;;
        -h|--help)  usage ;;
        -*)         echo "Unknown option: $1"; usage ;;
        *)          FOLDER="$1";      shift   ;;
    esac
done

[[ -z "$FOLDER" ]] && echo "Error: video folder required." && usage
[[ ! -d "$FOLDER" ]] && echo "Error: '$FOLDER' is not a directory." && exit 1

if [[ -n "$CONFIG_FILE" && ! -f "$CONFIG_FILE" ]]; then
    echo "Error: config file not found: $CONFIG_FILE"
    exit 1
fi

# ── collect video files ──────────────────────────────────────────────────────

# Build find expression: ( -iname "*.ts" -o -iname "*.mkv" ... )
FIND_NAMES=()
for ext in "${EXTENSIONS[@]}"; do
    FIND_NAMES+=(-o -iname "*.$ext")
done
FIND_NAMES=("${FIND_NAMES[@]:1}")   # drop leading -o

mapfile -d '' RAW_FILES < <(
    find "$FOLDER" -maxdepth 1 -type f \( "${FIND_NAMES[@]}" \) -print0 2>/dev/null
)

if [[ ${#RAW_FILES[@]} -eq 0 ]]; then
    echo "No video files found in '$FOLDER'."
    exit 0
fi

# ── sort ─────────────────────────────────────────────────────────────────────

episode_key() {
    local base
    base=$(basename "$1")
    if [[ "$base" =~ [Ss]([0-9]+)[Ee]([0-9]+) ]]; then
        printf "%04d%04d\t%s\n" "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "$1"
    else
        printf "99999999\t%s\n" "$1"
    fi
}

if [[ "$SORT_MODE" == "episode" ]]; then
    mapfile -t FILES < <(
        for f in "${RAW_FILES[@]}"; do episode_key "$f"; done \
            | sort -k1,1n \
            | cut -f2-
    )
else
    mapfile -t FILES < <(printf '%s\0' "${RAW_FILES[@]}" | sort -z | tr '\0' '\n')
fi

TOTAL=${#FILES[@]}

# ── dry run ──────────────────────────────────────────────────────────────────

if $DRY_RUN; then
    echo "Dry run — $TOTAL file(s) in '$FOLDER' (sort: $SORT_MODE):"
    for f in "${FILES[@]}"; do
        echo "  $(basename "$f")"
    done
    exit 0
fi

# ── process ──────────────────────────────────────────────────────────────────

echo "Processing $TOTAL file(s) in '$FOLDER' (sort: $SORT_MODE)"
[[ -n "$CONFIG_FILE" ]] && echo "Config: $CONFIG_FILE"
echo ""

PASS=0
FAIL=0
declare -a FAILED_FILES=()

for i in "${!FILES[@]}"; do
    f="${FILES[$i]}"
    num=$((i + 1))
    echo "[$num/$TOTAL] $(basename "$f")"

    CMD=("$COMMDETECT" process --verbose)
    [[ -n "$CONFIG_FILE" ]] && CMD+=(--config "$CONFIG_FILE")
    [[ -n "$FORCE"       ]] && CMD+=("$FORCE")
    CMD+=("$f")

    if "${CMD[@]}"; then
        PASS=$((PASS + 1))
    else
        FAIL=$((FAIL + 1))
        FAILED_FILES+=("$(basename "$f")")
    fi
    echo ""
done

# ── summary ──────────────────────────────────────────────────────────────────

echo "========================================"
echo "Done: $PASS succeeded, $FAIL failed"
if [[ ${#FAILED_FILES[@]} -gt 0 ]]; then
    echo "Failed files:"
    for f in "${FAILED_FILES[@]}"; do
        echo "  $f"
    done
fi
