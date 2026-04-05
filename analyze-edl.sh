#!/bin/bash
# analyze-edl.sh — Generate analysis CSVs from commdetect EDL archives or video folders.
#
# Produces two CSV files for visual analysis:
#
#   commdetect-shows-YYYYMMDD.csv  — one row per episode (fill in show
#                                    start/end and notes after visual review)
#   commdetect-breaks-YYYYMMDD.csv — one row per detected break (fill in
#                                    actual start/end after visual review)
#
# Usage: analyze-edl.sh [options]
#
# Options:
#   --video-dir <dir>    Read EDLs from alongside video files in this folder.
#                        Use this when the EDL archive (/tmp) has been cleared
#                        by a reboot. EDL files sit next to the videos as
#                        ShowName.edl (no timestamp suffix).
#   --edl-dir <dir>      Read from the timestamped EDL archive directory
#                        (default: read edl_dir from commdetect.ini).
#                        --video-dir takes precedence if both are given.
#   --ini <file>         commdetect.ini path (default: /usr/bin/config/commdetect.ini)
#   --output-dir <dir>   Where to write CSV files (default: current directory)
#   --show <pattern>     Filter: only include shows matching this pattern (case-insensitive)
#   -h, --help           Show this help

set -euo pipefail

DEFAULT_INI="/usr/bin/config/commdetect.ini"
INI="$DEFAULT_INI"
EDL_DIR=""
VIDEO_DIR=""
OUTPUT_DIR="."
SHOW_FILTER=""

usage() {
    sed -n '2,23p' "$0" | sed 's/^# //' | sed 's/^#//'
    exit 1
}

# ── argument parsing ─────────────────────────────────────────────────────────

while [[ $# -gt 0 ]]; do
    case "$1" in
        --video-dir)  VIDEO_DIR="$2";  shift 2 ;;
        --edl-dir)    EDL_DIR="$2";    shift 2 ;;
        --ini)        INI="$2";        shift 2 ;;
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        --show)       SHOW_FILTER="$2"; shift 2 ;;
        -h|--help)    usage ;;
        *)            echo "Unknown option: $1"; usage ;;
    esac
done

# ── ini reader ───────────────────────────────────────────────────────────────

get_ini() {
    local section="$1" key="$2" default="$3"
    local in_section=false
    [[ ! -f "$INI" ]] && echo "$default" && return
    while IFS= read -r line; do
        line="${line%%#*}"           # strip inline comments
        line="${line%%\;*}"
        line="${line#"${line%%[![:space:]]*}"}"  # ltrim
        line="${line%"${line##*[![:space:]]}"}"  # rtrim
        if [[ "$line" =~ ^\[(.+)\]$ ]]; then
            [[ "${BASH_REMATCH[1]}" == "$section" ]] && in_section=true || in_section=false
        elif $in_section && [[ "$line" == "$key="* ]]; then
            local val="${line#*=}"
            val="${val#"${val%%[![:space:]]*}"}"
            echo "$val"
            return
        fi
    done < "$INI"
    echo "$default"
}

mkdir -p "$OUTPUT_DIR"

# ── time formatter (seconds → M:SS or H:MM:SS) ───────────────────────────────

fmt_time() {
    local raw=$((10#${1%.*}))    # strip decimal, force base-10
    local h=$((raw / 3600))
    local m=$(( (raw % 3600) / 60 ))
    local s=$((raw % 60))
    if [[ $h -gt 0 ]]; then
        printf "%d:%02d:%02d" "$h" "$m" "$s"
    else
        printf "%d:%02d" "$m" "$s"
    fi
}

# ── collect EDL files ────────────────────────────────────────────────────────

declare -A LATEST_EDL   # show_name → edl_path
SOURCE_DESC=""

if [[ -n "$VIDEO_DIR" ]]; then
    # ── alongside mode: ShowName.edl sits next to each video, no timestamp ──
    [[ ! -d "$VIDEO_DIR" ]] && echo "Error: video directory not found: $VIDEO_DIR" && exit 1
    SOURCE_DESC="$VIDEO_DIR (alongside videos)"

    while IFS= read -r edl; do
        show=$(basename "${edl%.edl}")

        if [[ -n "$SHOW_FILTER" ]]; then
            [[ "${show,,}" != *"${SHOW_FILTER,,}"* ]] && continue
        fi

        LATEST_EDL["$show"]="$edl"
    done < <(find "$VIDEO_DIR" -maxdepth 1 -name "*.edl" | sort)

else
    # ── no explicit flag: prefer video_dir from ini, fall back to archive ───
    if [[ -z "$EDL_DIR" ]]; then
        INI_VIDEO_DIR=$(get_ini "Logging" "video_dir" "")
        if [[ -n "$INI_VIDEO_DIR" && -d "$INI_VIDEO_DIR" ]]; then
            VIDEO_DIR="$INI_VIDEO_DIR"
        fi
    fi

    if [[ -n "$VIDEO_DIR" ]]; then
        # redirect into alongside mode (duplicate of the if-branch above)
        SOURCE_DESC="$VIDEO_DIR (alongside videos)"
        while IFS= read -r edl; do
            show=$(basename "${edl%.edl}")
            if [[ -n "$SHOW_FILTER" ]]; then
                [[ "${show,,}" != *"${SHOW_FILTER,,}"* ]] && continue
            fi
            LATEST_EDL["$show"]="$edl"
        done < <(find "$VIDEO_DIR" -maxdepth 1 -name "*.edl" | sort)
    else
    # ── archive mode: Show Name_YYYYMMDD_HHMMSS.edl, pick most recent ───────
    [[ -z "$EDL_DIR" ]] && EDL_DIR=$(get_ini "Logging" "edl_dir" "/tmp/logs/edl")
    [[ ! -d "$EDL_DIR" ]] && echo "Error: EDL archive not found: $EDL_DIR" \
        && echo "Try --video-dir ~/Videos to read EDLs from alongside the video files." \
        && exit 1
    SOURCE_DESC="$EDL_DIR (archive)"

    declare -A LATEST_TS
    while IFS= read -r edl; do
        base=$(basename "${edl%.edl}")
        [[ ${#base} -le 16 ]] && continue
        show="${base:0:${#base}-16}"    # strip _YYYYMMDD_HHMMSS
        ts="${base: -15}"              # YYYYMMDD_HHMMSS

        if [[ -n "$SHOW_FILTER" ]]; then
            [[ "${show,,}" != *"${SHOW_FILTER,,}"* ]] && continue
        fi

        if [[ -z "${LATEST_TS[$show]+x}" ]] || [[ "$ts" > "${LATEST_TS[$show]}" ]]; then
            LATEST_TS["$show"]="$ts"
            LATEST_EDL["$show"]="$edl"
        fi
    done < <(find "$EDL_DIR" -maxdepth 1 -name "*.edl" | sort)
    fi  # end inner if [[ -n "$VIDEO_DIR" ]] (ini-provided vs archive)
fi  # end outer if [[ -n "$VIDEO_DIR" ]] (explicit flag)

if [[ ${#LATEST_EDL[@]} -eq 0 ]]; then
    echo "No EDL files found."
    [[ -n "$SHOW_FILTER" ]] && echo "(filter: '$SHOW_FILTER')"
    exit 0
fi

# Sort episodes: S##E## order when present, alphabetical otherwise
episode_key() {
    local show="$1"
    if [[ "$show" =~ [Ss]([0-9]+)[Ee]([0-9]+) ]]; then
        # Extract the series title prefix (before S##E##) for primary sort,
        # then season + episode for secondary sort.
        local prefix="${show%%[Ss][0-9]*}"
        printf "%s|%04d|%04d\t%s\n" "$prefix" "$((10#${BASH_REMATCH[1]}))" "$((10#${BASH_REMATCH[2]}))" "$show"
    else
        printf "%s|9999|9999\t%s\n" "$show" "$show"
    fi
}

mapfile -t SHOWS < <(
    for show in "${!LATEST_EDL[@]}"; do episode_key "$show"; done \
        | sort \
        | cut -f2-
)

# ── output paths ─────────────────────────────────────────────────────────────

DATE=$(date +%Y%m%d)
SHOWS_CSV="$OUTPUT_DIR/commdetect-shows-$DATE.csv"
BREAKS_CSV="$OUTPUT_DIR/commdetect-breaks-$DATE.csv"

# ── write shows CSV ──────────────────────────────────────────────────────────

{
    echo "Show,Det_Breaks,Show_Start_Actual,Show_End_Actual,Notes"
    for show in "${SHOWS[@]}"; do
        edl="${LATEST_EDL[$show]}"
        count=$(grep -cE '^[0-9]' "$edl" 2>/dev/null || true)
        # Escape any quotes in show name
        show_escaped="${show//\"/\"\"}"
        printf '"%s",%s,,,\n' "$show_escaped" "$count"
    done
} > "$SHOWS_CSV"

# ── write breaks CSV ─────────────────────────────────────────────────────────

{
    echo "Show,Break_Num,Det_Start,Det_End,Actual_Start,Actual_End,Notes"
    for show in "${SHOWS[@]}"; do
        edl="${LATEST_EDL[$show]}"
        show_escaped="${show//\"/\"\"}"
        break_num=1
        while IFS=' ' read -r start end _rest; do
            [[ -z "$start" || ! "$start" =~ ^[0-9] ]] && continue
            det_start=$(fmt_time "$start")
            det_end=$(fmt_time "$end")
            printf '"%s",%d,%s,%s,,\n' "$show_escaped" "$break_num" "$det_start" "$det_end"
            break_num=$((break_num + 1))
        done < "$edl"
    done
} > "$BREAKS_CSV"

# ── summary ──────────────────────────────────────────────────────────────────

echo "Generated from: $SOURCE_DESC"
echo ""
echo "  $SHOWS_CSV"
echo "     $(wc -l < "$SHOWS_CSV") shows (fill in Show_Start_Actual, Show_End_Actual, Notes)"
echo ""
echo "  $BREAKS_CSV"
echo "     $(( $(wc -l < "$BREAKS_CSV") - 1 )) breaks (fill in Actual_Start, Actual_End, Notes)"
echo ""
echo "Open both files in LibreOffice Calc for side-by-side analysis."
