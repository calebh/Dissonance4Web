#!/usr/bin/env bash
#
# Builds libopus for WebAssembly and drops it into Runtime/Plugins/WebGL.
#
# Dissonance runs Opus through a native library on every platform. There is no
# WebAssembly build in the box, so a browser needs one built here; this is the
# only manual step in installing Dissonance 4 Web, and it is a one-off.
#
# The build deliberately uses the Emscripten that ships inside the Unity editor
# rather than a separately installed emsdk. Unity links the plugin archive into
# the player with its own Emscripten, and an archive built by a different LLVM
# version fails that link with errors that do not say so. Using Unity's own
# toolchain keeps the two in step by construction - and means nothing has to be
# installed for this beyond CMake.
#
# Usage:
#   ./Native~/build-opus-wasm.sh
#   ./Native~/build-opus-wasm.sh --unity /opt/unity/6000.6.0f1
#   ./Native~/build-opus-wasm.sh --opus-source ~/src/opus
#   ./Native~/build-opus-wasm.sh --emscripten ~/emsdk/upstream
#   ./Native~/build-opus-wasm.sh --clean

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_ROOT="$(dirname "$SCRIPT_DIR")"
PLUGIN_DIR="$PACKAGE_ROOT/Runtime/Plugins/WebGL"
WORK_DIR="$SCRIPT_DIR/build"

UNITY_PATH=""
EMSCRIPTEN=""
OPUS_SOURCE=""
OPUS_VERSION="1.5.2"
CLEAN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --unity) UNITY_PATH="$2"; shift 2 ;;
        --emscripten) EMSCRIPTEN="$2"; shift 2 ;;
        --opus-source) OPUS_SOURCE="$2"; shift 2 ;;
        --opus-version) OPUS_VERSION="$2"; shift 2 ;;
        --clean) CLEAN=1; shift ;;
        -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
        *) echo "unknown option '$1'" >&2; exit 1 ;;
    esac
done

step() { printf '\n==> %s\n' "$1"; }

require() {
    command -v "$1" >/dev/null 2>&1 || { echo "'$1' is not on PATH. $2" >&2; exit 1; }
}

find_emscripten() {
    if [ -n "$EMSCRIPTEN" ]; then
        [ -f "$EMSCRIPTEN/emscripten/emcc.py" ] || {
            echo "--emscripten '$EMSCRIPTEN' has no emscripten/emcc.py inside it" >&2
            exit 1
        }
        echo "$EMSCRIPTEN"
        return
    fi

    local suffix="Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten"
    local candidates=()

    if [ -n "$UNITY_PATH" ]; then
        candidates+=("$UNITY_PATH")
    else
        # The usual Hub locations, newest editor first.
        local roots=(
            "/opt/unity/hub/editor"
            "/opt/Unity/Hub/Editor"
            "$HOME/Unity/Hub/Editor"
            "/Applications/Unity/Hub/Editor"
        )
        local root
        for root in "${roots[@]}"; do
            [ -d "$root" ] || continue
            while IFS= read -r entry; do
                candidates+=("$entry")
            done < <(find "$root" -maxdepth 1 -mindepth 1 -type d | sort -r)
        done
    fi

    local candidate
    for candidate in "${candidates[@]:-}"; do
        if [ -f "$candidate/$suffix/emscripten/emcc.py" ]; then
            echo "$candidate/$suffix"
            return
        fi
        # macOS keeps the editor inside Unity.app.
        if [ -f "$candidate/Unity.app/Contents/$suffix/emscripten/emcc.py" ]; then
            echo "$candidate/Unity.app/Contents/$suffix"
            return
        fi
    done

    echo "Could not find Unity's Emscripten. Pass --unity <editor install> or --emscripten <emsdk directory>." >&2
    exit 1
}

get_opus_source() {
    if [ -n "$OPUS_SOURCE" ]; then
        [ -f "$OPUS_SOURCE/CMakeLists.txt" ] || {
            echo "--opus-source '$OPUS_SOURCE' has no CMakeLists.txt in it" >&2
            exit 1
        }
        echo "$OPUS_SOURCE"
        return
    fi

    local extracted="$WORK_DIR/opus-$OPUS_VERSION"
    if [ -f "$extracted/CMakeLists.txt" ]; then
        echo "$extracted"
        return
    fi

    local archive="$WORK_DIR/opus-$OPUS_VERSION.tar.gz"
    if [ ! -f "$archive" ]; then
        local url="https://downloads.xiph.org/releases/opus/opus-$OPUS_VERSION.tar.gz"
        echo "    downloading $url" >&2
        if command -v curl >/dev/null 2>&1; then
            curl -fsSL "$url" -o "$archive"
        else
            require wget "Install curl or wget, or pass --opus-source."
            wget -q "$url" -O "$archive"
        fi
    fi

    echo "    extracting" >&2
    tar -xzf "$archive" -C "$WORK_DIR"

    [ -f "$extracted/CMakeLists.txt" ] || {
        echo "extracted archive does not contain opus-$OPUS_VERSION/CMakeLists.txt" >&2
        exit 1
    }

    echo "$extracted"
}

# ---------------------------------------------------------------------------

require cmake "Install CMake and add it to PATH."
require tar "Install tar, or pass --opus-source with an existing checkout."

if [ "$CLEAN" = "1" ] && [ -d "$WORK_DIR" ]; then
    step "Removing $WORK_DIR"
    rm -rf "$WORK_DIR"
fi

mkdir -p "$WORK_DIR"

step "Locating Emscripten"
EM_ROOT="$(find_emscripten)"
EM_VERSION="$(tr -d '"' < "$EM_ROOT/emscripten/emscripten-version.txt" 2>/dev/null || echo unknown)"
echo "    $EM_ROOT"
echo "    version $EM_VERSION"

# The emcc launcher finds its interpreter through EMSDK_PYTHON and its
# configuration through EM_CONFIG. Setting both makes Unity's bundled toolchain
# self contained, so this runs on a machine with no emsdk and no python.
export EM_CONFIG="$EM_ROOT/.emscripten"
export PATH="$EM_ROOT/emscripten:$PATH"

if [ -x "$EM_ROOT/python/bin/python3" ]; then
    export EMSDK_PYTHON="$EM_ROOT/python/bin/python3"
elif [ -x "$EM_ROOT/python/python3" ]; then
    export EMSDK_PYTHON="$EM_ROOT/python/python3"
else
    require python3 "A standalone Emscripten needs python3 on PATH."
fi

step "Locating libopus source"
OPUS="$(get_opus_source)"
echo "    $OPUS"

step "Configuring"
BUILD_DIR="$WORK_DIR/opus-wasm"

# Only the encoder, decoder and the soft clipper are wanted. Everything else -
# the tools, the tests - is turned off so the archive stays small; it is linked
# into every page load.
cmake -S "$OPUS" -B "$BUILD_DIR" \
    -DCMAKE_TOOLCHAIN_FILE="$EM_ROOT/emscripten/cmake/Modules/Platform/Emscripten.cmake" \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=OFF \
    -DOPUS_BUILD_SHARED_LIBRARY=OFF \
    -DOPUS_BUILD_PROGRAMS=OFF \
    -DOPUS_BUILD_TESTING=OFF \
    -DBUILD_TESTING=OFF \
    -DOPUS_STACK_PROTECTOR=OFF \
    -DOPUS_FIXED_POINT=OFF

step "Building"
cmake --build "$BUILD_DIR" --config Release --parallel

step "Publishing"
ARCHIVE="$(find "$BUILD_DIR" -name libopus.a -type f | head -n 1)"
[ -n "$ARCHIVE" ] || {
    echo "the build finished but produced no libopus.a under $BUILD_DIR" >&2
    exit 1
}

mkdir -p "$PLUGIN_DIR"
cp -f "$ARCHIVE" "$PLUGIN_DIR/libopus.a"

echo "    $PLUGIN_DIR/libopus.a ($(du -h "$PLUGIN_DIR/libopus.a" | cut -f1), built with Emscripten $EM_VERSION)"

cat <<MESSAGE

Done. Unity will import the archive on the next domain reload.
Check the Plugin Inspector once: the platform should be WebGL and nothing else.

Rebuild this after upgrading Unity to a version with a different Emscripten.
MESSAGE
