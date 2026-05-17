#!/bin/bash
# Build DirStat.app from the Swift sources. Uses the system swiftc — no Xcode
# project required (just the command-line tools, which ship with macOS).
#
# Usage:  ./build.sh              # builds for the host architecture
#         ./build.sh universal    # builds a fat binary (arm64 + x86_64)
set -e
cd "$(dirname "$0")"

APP_NAME="DirStat"
BUILD_DIR="build"
APP_DIR="${BUILD_DIR}/${APP_NAME}.app"
BIN_DIR="${APP_DIR}/Contents/MacOS"
RES_DIR="${APP_DIR}/Contents/Resources"

if ! command -v swiftc >/dev/null 2>&1; then
    echo "error: swiftc not found. Install Xcode Command Line Tools:" >&2
    echo "       xcode-select --install" >&2
    exit 1
fi

rm -rf "${APP_DIR}"
mkdir -p "${BIN_DIR}" "${RES_DIR}"

# Common swiftc flags.
COMMON_FLAGS=(
    -O
    -swift-version 5
    -parse-as-library
    -module-name DirStat
)

case "${1:-}" in
    universal)
        echo "Building universal binary (arm64 + x86_64)..."
        # Build per-arch, then lipo.
        TMP="${BUILD_DIR}/tmp"
        mkdir -p "${TMP}"
        for arch in arm64 x86_64; do
            echo "  -> $arch"
            swiftc "${COMMON_FLAGS[@]}" \
                -target ${arch}-apple-macos12 \
                -o "${TMP}/${APP_NAME}-${arch}" \
                Sources/*.swift
        done
        lipo -create -output "${BIN_DIR}/${APP_NAME}" \
            "${TMP}/${APP_NAME}-arm64" \
            "${TMP}/${APP_NAME}-x86_64"
        rm -rf "${TMP}"
        ;;
    *)
        echo "Building for host architecture..."
        swiftc "${COMMON_FLAGS[@]}" \
            -o "${BIN_DIR}/${APP_NAME}" \
            Sources/*.swift
        ;;
esac

cp Resources/Info.plist "${APP_DIR}/Contents/Info.plist"

echo ""
echo "Built ${APP_DIR}"
echo "Run with:    open ${APP_DIR}"
echo "Or:          ${BIN_DIR}/${APP_NAME} [path]"
