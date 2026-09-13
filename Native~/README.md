# Building Opus for WebAssembly

Dissonance runs Opus through a native library on every platform, and ships no WebAssembly build of
it. This package includes one - `Runtime/Plugins/WebGL/libopus.a`, built with Emscripten 4.0.20,
the version Unity 6000.5 and 6000.6 bundle - and these scripts rebuild it. Rebuild it when your
Unity bundles a different Emscripten, or to move to a newer Opus.

```bash
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1
```

```bash
./Native~/build-opus-wasm.sh
```

Both download upstream libopus, build it, and replace `Runtime/Plugins/WebGL/libopus.a`. Unity
reimports it on the next domain reload. The archive's `.meta` is committed alongside it and enables
it for WebGL only; keep it that way, because the folder being named `WebGL` does not restrict
anything on its own. Commit the rebuilt archive so everyone on the same Unity picks it up.

## What it needs

**CMake and Ninja on PATH.** That is all. Emscripten, LLVM, python and node all come from inside
the Unity editor.

Ninja is required, not preferred, and the script says so and stops if it is missing. The
alternative is worse than an error. Given no generator, CMake on Windows picks the newest Visual
Studio, and a Visual Studio build compiles with the VS toolset instead of the toolchain file's
compiler - so it builds Opus with MSVC, succeeds, and produces a `libopus.a` full of x64 objects.
Unity imports that happily, and the WebGL build then fails at its link step with one
`is neither Wasm object file nor LLVM bitcode` warning per object. Forcing Ninja makes the build
use Emscripten on every host.

Install it however suits: `winget install Ninja-build.Ninja`, `choco install ninja`,
`brew install ninja`, `apt install ninja-build`, or the single executable from
[ninja-build.org](https://ninja-build.org). Visual Studio's "C++ CMake tools for Windows" component
also ships one, though not on PATH.

**Network access**, unless `-OpusSource` / `--opus-source` points at a checkout you already have:

```bash
./Native~/build-opus-wasm.sh --opus-source ~/src/opus
```

## Why the editor's Emscripten and not an emsdk

Unity links the plugin archive into the player with its own Emscripten. An archive built by a
different LLVM version fails that link, and the failure does not say so - it surfaces as undefined
symbols or bitcode version complaints from a step you did not run yourself. Taking the toolchain
from the editor keeps the two in step by construction, and has the side benefit that nothing has to
be installed.

Unity 6000.6 bundles Emscripten 4.0.20. The script reads the version out of the editor it picked
and prints it, so the archive can be matched to an editor later.

`-Emscripten` / `--emscripten` overrides it if you know Unity is using the same version:

```bash
./Native~/build-opus-wasm.sh --emscripten ~/emsdk/upstream
```

## What gets built

The configure step turns off the tools, the tests and the fixed-point build. What is left is the
encoder, the decoder and `opus_pcm_soft_clip` - everything Dissonance's P/Invokes name, and
nothing else, because this archive is linked into a page that someone has to download.

Four symbols are not in upstream libopus:

```
dissonance_opus_encoder_ctl_in    dissonance_opus_decoder_ctl_in
dissonance_opus_encoder_ctl_out   dissonance_opus_decoder_ctl_out
```

Those are Dissonance's non-variadic wrappers around `opus_encoder_ctl` and `opus_decoder_ctl`,
which exist because P/Invoke cannot describe a variadic call. Dissonance ships a patched opus
carrying them for desktop and mobile; here they are four lines of C in
`Runtime/Plugins/WebGL/dissonance_opus_shim.c`, which Unity compiles as part of the WebGL build.
That file includes no opus header on purpose, so Unity can compile it with no include path
configured.

## Rebuilding

After upgrading Unity to a version with a different Emscripten. There is no way to detect a stale
archive from the editor - `Dissonance4WebBuildCheck` can only tell you the file is missing - so if
a WebGL build starts failing at the link step after an editor upgrade, rebuild this first.

`-Clean` / `--clean` discards the download and the build directory first.

## Verifying by hand

The scripts already do this before publishing: they refuse to copy an archive unless every object in
it is WebAssembly (`llvm-readobj --file-header` reports `Format: WASM`) and every Opus entry point
Dissonance imports is defined. A failed check leaves any previously published `libopus.a` untouched.
Unity's `Dissonance4WebBuildCheck` repeats the format check on whichever `libopus.a` is enabled for
WebGL, and stops the build before it starts if that is not WebAssembly.

To check by hand, the archive should define the Opus entry points Dissonance imports. With the
editor's LLVM:

```bash
"<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/llvm/llvm-nm" \
    Runtime/Plugins/WebGL/libopus.a | grep -E " T (opus_encoder_create|opus_encode_float|opus_decoder_create|opus_decode_float|opus_pcm_soft_clip|opus_get_version_string)$"
```

Six lines means the archive is complete. The four `dissonance_opus_*` wrappers will not be in
there; they come from the .c file Unity compiles.
