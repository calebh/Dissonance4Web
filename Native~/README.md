# Building Opus for WebAssembly

Dissonance runs Opus through a native library on every platform. There is no WebAssembly build in
the box, so a browser needs one built here. This is the only manual step in installing
Dissonance 4 Web, and it is a one-off.

```bash
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1
```

```bash
./Native~/build-opus-wasm.sh
```

Both download upstream libopus, build it, and drop `libopus.a` into `Runtime/Plugins/WebGL/`.
Unity imports it on the next domain reload and infers the WebGL platform from the folder name.

## What it needs

**CMake on PATH.** That is all. Emscripten, LLVM, python and node all come from inside the Unity
editor.

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

The archive should define the Opus entry points Dissonance imports. With the editor's LLVM:

```bash
"<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/llvm/llvm-nm" \
    Runtime/Plugins/WebGL/libopus.a | grep -E " T (opus_encoder_create|opus_encode_float|opus_decoder_create|opus_decode_float|opus_pcm_soft_clip|opus_get_version_string)$"
```

Six lines means the archive is complete. The four `dissonance_opus_*` wrappers will not be in
there; they come from the .c file Unity compiles.
