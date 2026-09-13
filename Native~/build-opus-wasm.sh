#!/usr/bin/env bash
#
# Builds libopus for WebAssembly and drops it into Runtime/Plugins/WebGL.
#
# Dissonance runs Opus through a native library on every platform, and ships no
# WebAssembly build of it. This package commits one; run this to rebuild it when
# your Unity bundles a different Emscripten, or to move to a newer Opus.
#
# The build deliberately uses the Emscripten that ships inside the Unity editor
# rather than a separately installed emsdk. Unity links the plugin archive into
# the player with its own Emscripten, and an archive built by a different LLVM
# version fails that link with errors that do not say so. Using Unity's own
# toolchain keeps the two in step by construction - and means nothing has to be
# installed for this beyond CMake and Ninja.
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

# Every Opus entry point Dissonance's OpusNative class P/Invokes, plus the two
# ctl functions dissonance_opus_shim.c forwards to. An archive missing any of
# these links into a player that fails the moment voice starts.
REQUIRED_SYMBOLS="
    opus_get_version_string
    opus_encoder_create
    opus_encoder_destroy
    opus_encode_float
    opus_encoder_ctl
    opus_decoder_create
    opus_decoder_destroy
    opus_decode_float
    opus_decoder_ctl
    opus_pcm_soft_clip
"

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

llvm_tool() {
    local candidate
    for candidate in "$EM_ROOT/llvm/$1" "$EM_ROOT/llvm/$1.exe"; do
        [ -f "$candidate" ] && { echo "$candidate"; return; }
    done
    echo "Emscripten's LLVM has no $1 under $EM_ROOT/llvm" >&2
    exit 1
}

# The check that would have caught a build which quietly used the wrong
# compiler. Every member of the archive has to be a WebAssembly object: a native
# object with a .a name makes Unity's link step warn "neither Wasm object file
# nor LLVM bitcode" once per member, and then fail on the symbols it could not use.
verify_archive() {
    local archive="$1" readobj nm formats foreign defined missing="" symbol count

    readobj="$(llvm_tool llvm-readobj)"
    nm="$(llvm_tool llvm-nm)"

    formats="$("$readobj" --file-header "$archive" | sed -n 's/^Format:[[:space:]]*//p' | tr -d '\r')"
    [ -n "$formats" ] || { echo "$archive contains no object files" >&2; exit 1; }

    foreign="$(printf '%s\n' "$formats" | grep -v '^WASM$' | sort -u | paste -sd, - || true)"
    if [ -n "$foreign" ]; then
        echo "$archive is not WebAssembly: its objects are $foreign. CMake compiled with something other than Emscripten; see Native~/README.md." >&2
        exit 1
    fi

    # stderr dropped: llvm-nm notes "no symbols" for Opus's empty debug.c object.
    defined="$("$nm" --defined-only "$archive" 2>/dev/null | awk '{ print $NF }' | tr -d '\r')"
    # A here-string rather than a pipe: under pipefail, grep -q exiting on its first
    # match can SIGPIPE the writer and make a symbol that is present look missing.
    for symbol in $REQUIRED_SYMBOLS; do
        grep -qx "$symbol" <<< "$defined" || missing="$missing $symbol"
    done
    if [ -n "$missing" ]; then
        echo "$archive does not define symbols Dissonance imports:$missing" >&2
        exit 1
    fi

    count="$(printf '%s\n' "$formats" | wc -l | tr -d ' ')"
    echo "    $count WebAssembly objects; all symbols Dissonance imports are defined"
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
require ninja "Install Ninja (brew install ninja, apt install ninja-build, or choco install ninja on Windows) and add it to PATH. It is required: without it CMake on Windows falls back to Visual Studio, which compiles Opus for x64 instead of WebAssembly. See Native~/README.md."
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

# CMake will not change generator in an existing build directory, and one
# configured before this script chose Ninja may have recorded another - on
# Windows, Visual Studio, whose output is MSVC objects. Start it over.
if [ -f "$BUILD_DIR/CMakeCache.txt" ]; then
    RECORDED="$(grep -m1 '^CMAKE_GENERATOR:INTERNAL=' "$BUILD_DIR/CMakeCache.txt" | cut -d= -f2- | tr -d '\r')"
    if [ -n "$RECORDED" ] && [ "$RECORDED" != "Ninja" ]; then
        echo "    discarding $BUILD_DIR, which was configured for '$RECORDED'"
        rm -rf "$BUILD_DIR"
    fi
fi

# Ninja, explicitly. Given no -G, CMake on Windows picks the newest Visual
# Studio, and a Visual Studio build compiles with the VS toolset rather than the
# toolchain file's compiler - so it builds Opus with MSVC, without complaint, and
# names the result libopus.a. Ninja uses the toolchain's compiler everywhere, so
# the build is the same on every host.
#
# Only the encoder, decoder and the soft clipper are wanted. Everything else -
# the tools, the tests - is turned off so the archive stays small; it is linked
# into every page load.
cmake -G Ninja -S "$OPUS" -B "$BUILD_DIR" \
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
cmake --build "$BUILD_DIR" --parallel

step "Verifying"
ARCHIVE="$(find "$BUILD_DIR" -name libopus.a -type f | head -n 1)"
[ -n "$ARCHIVE" ] || {
    echo "the build finished but produced no libopus.a under $BUILD_DIR" >&2
    exit 1
}

# Before publishing, so an archive Unity cannot link never replaces one it can.
verify_archive "$ARCHIVE"

step "Publishing"
mkdir -p "$PLUGIN_DIR"
cp -f "$ARCHIVE" "$PLUGIN_DIR/libopus.a"

echo "    $PLUGIN_DIR/libopus.a ($(du -h "$PLUGIN_DIR/libopus.a" | cut -f1), built with Emscripten $EM_VERSION)"

cat <<MESSAGE

Done. Unity will import the archive on the next domain reload.
Check the Plugin Inspector once: the platform should be WebGL and nothing else.

Rebuild this after upgrading Unity to a version with a different Emscripten.
MESSAGE
