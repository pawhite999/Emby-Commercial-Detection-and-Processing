#!/bin/bash
# =============================================================================
# build-all.sh — Cross-platform build script for CommDetect
# =============================================================================
# Builds self-contained, single-file executables for all supported platforms.
# Each output is a standalone binary that includes the .NET runtime — the only
# external dependency at runtime is FFmpeg.
#
# Usage:
#   chmod +x build-all.sh
#   ./build-all.sh              # Build all platforms
#   ./build-all.sh linux-x64    # Build a single platform
#   ./build-all.sh --clean      # Clean previous builds then build all
#
# Requirements:
#   - .NET 8 SDK (install from https://dotnet.microsoft.com/download)
#
# Output:
#   dist/<rid>/commdetect[.exe]  — one self-contained executable per platform
# =============================================================================

set -euo pipefail

# ── Configuration ────────────────────────────────────────────────────────────
PROJECT="src/CommDetect.CLI/CommDetect.CLI.csproj"
CONFIG="Release"
OUTPUT_BASE="dist"

# All supported Runtime Identifiers (RIDs)
ALL_RIDS=(
    "win-x64"
    "win-arm64"
    "linux-x64"
    "linux-arm64"
    "linux-musl-x64"       # Alpine Linux / Docker
    "osx-x64"              # macOS Intel
    "osx-arm64"            # macOS Apple Silicon
)

# ── Color output helpers ─────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

info()    { echo -e "${CYAN}[INFO]${NC}  $*"; }
success() { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
fail()    { echo -e "${RED}[FAIL]${NC}  $*"; }

# ── Preflight checks ────────────────────────────────────────────────────────
check_prerequisites() {
    if ! command -v dotnet &> /dev/null; then
        fail "dotnet SDK not found. Install from https://dotnet.microsoft.com/download"
        exit 1
    fi

    DOTNET_VERSION=$(dotnet --version)
    info "Using .NET SDK ${DOTNET_VERSION}"

    if [ ! -f "$PROJECT" ]; then
        fail "Project file not found: ${PROJECT}"
        fail "Make sure you run this script from the repository root (where CommDetect.sln lives)."
        exit 1
    fi
}

# ── Clean previous builds ───────────────────────────────────────────────────
clean_builds() {
    if [ -d "$OUTPUT_BASE" ]; then
        info "Cleaning previous builds in ${OUTPUT_BASE}/..."
        rm -rf "$OUTPUT_BASE"
    fi
    # Also clean intermediate build artifacts
    dotnet clean "$PROJECT" -c "$CONFIG" --nologo -v quiet 2>/dev/null || true
}

# ── Build for a single RID ──────────────────────────────────────────────────
build_rid() {
    local rid="$1"
    local output_dir="${OUTPUT_BASE}/${rid}"

    info "Building for ${rid}..."

    if dotnet publish "$PROJECT" \
        -c "$CONFIG" \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -o "$output_dir" \
        --nologo \
        -v quiet; then

        # Show the produced binary
        local binary
        if [[ "$rid" == win-* ]]; then
            binary=$(find "$output_dir" -maxdepth 1 -name "*.exe" | head -1)
        else
            binary=$(find "$output_dir" -maxdepth 1 -type f ! -name "*.pdb" ! -name "*.json" ! -name "*.xml" | head -1)
        fi

        if [ -n "$binary" ]; then
            local size
            size=$(du -h "$binary" | cut -f1)
            success "${rid}  →  $(basename "$binary")  (${size})"
        else
            success "${rid}  →  built"
        fi
        return 0
    else
        fail "${rid}  →  BUILD FAILED"
        return 1
    fi
}

# ── Main ─────────────────────────────────────────────────────────────────────
main() {
    echo ""
    echo "============================================="
    echo "  CommDetect — Cross-Platform Build"
    echo "============================================="
    echo ""

    check_prerequisites

    local rids_to_build=()
    local do_clean=false

    # Parse arguments
    for arg in "$@"; do
        case "$arg" in
            --clean)
                do_clean=true
                ;;
            --help|-h)
                echo "Usage: $0 [--clean] [rid1 rid2 ...]"
                echo ""
                echo "Options:"
                echo "  --clean       Remove previous build output before building"
                echo "  --help, -h    Show this help message"
                echo ""
                echo "Supported RIDs:"
                for rid in "${ALL_RIDS[@]}"; do
                    echo "  $rid"
                done
                echo ""
                echo "Examples:"
                echo "  $0                        # Build all platforms"
                echo "  $0 linux-x64 osx-arm64    # Build specific platforms"
                echo "  $0 --clean win-x64        # Clean then build Windows x64"
                exit 0
                ;;
            *)
                rids_to_build+=("$arg")
                ;;
        esac
    done

    # Default: build all RIDs if none specified
    if [ ${#rids_to_build[@]} -eq 0 ]; then
        rids_to_build=("${ALL_RIDS[@]}")
    fi

    if $do_clean; then
        clean_builds
    fi

    # Restore packages once before building
    info "Restoring NuGet packages..."
    dotnet restore "$PROJECT" --nologo -v quiet

    echo ""
    info "Building ${#rids_to_build[@]} platform(s)..."
    echo ""

    local succeeded=0
    local failed=0
    local failed_rids=()

    for rid in "${rids_to_build[@]}"; do
        if build_rid "$rid"; then
            succeeded=$(( succeeded + 1 ))
        else
            failed=$(( failed + 1 ))
            failed_rids+=("$rid")
        fi
    done

    # ── Summary ──────────────────────────────────────────────────────────
    echo ""
    echo "============================================="
    echo "  Build Summary"
    echo "============================================="
    success "Succeeded: ${succeeded}"
    if [ "$failed" -gt 0 ]; then
        fail "Failed:    ${failed}  (${failed_rids[*]})"
    fi
    echo ""

    if [ "$succeeded" -gt 0 ]; then
        info "Output directory: ${OUTPUT_BASE}/"
        echo ""
        info "Platform binaries:"
        for rid in "${rids_to_build[@]}"; do
            local dir="${OUTPUT_BASE}/${rid}"
            if [ -d "$dir" ]; then
                echo "  ${rid}/"
                find "$dir" -maxdepth 1 -type f \( -name "commdetect*" -o -name "CommDetect*" \) \
                    ! -name "*.pdb" ! -name "*.json" ! -name "*.xml" 2>/dev/null | \
                    while read -r f; do
                        echo "    └── $(basename "$f")  ($(du -h "$f" | cut -f1))"
                    done
            fi
        done
        echo ""
    fi

    # Exit with failure if any builds failed
    [ "$failed" -eq 0 ]
}

main "$@"
