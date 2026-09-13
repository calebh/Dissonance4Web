# Dissonance 4 Web

Browser support for [Dissonance Voice Chat](https://placeholder-software.co.uk/dissonance/),
plus a Mirror integration that runs over [MirrorWTransport](https://github.com/calebh/MirrorWTransport).

A WebGL player and a desktop player join the same Dissonance voice session and talk to each
other. Nothing transcodes anywhere in the path: the browser runs the same Opus encoder the
desktop build does, compiled to WebAssembly, and the server relays the frames it is given without
looking inside them.

| Role | Capture | Codec | Playback | Network |
| --- | --- | --- | --- | --- |
| Desktop / mobile | Dissonance, unchanged | Opus (native) | Dissonance, unchanged | Mirror + any transport |
| Browser (WebGL) | getUserMedia + AudioWorklet | Opus (WebAssembly) | AudioWorklet | Mirror + MirrorWTransport |
| Server | — | — (never decodes) | — | Mirror + MirrorWTransport |

## What this is made of

Two assemblies, split along the line Dissonance itself draws.

**`Dissonance4Web`** is the browser audio pipeline, and knows nothing about any networking
library. It is three replacement parts for the three pieces of Dissonance's audio path that have
no browser equivalent - capture, preprocessing, playback - and it works with any
`ICommsNetwork`, Mirror's or otherwise.

**`Dissonance4Web.Mirror`** is the Dissonance integration for Mirror: the successor to
Dissonance's own `MirrorIgnorance` integration, which was named when Ignorance was the only
Mirror transport offering both a reliable and an unreliable channel. It is transport agnostic -
it asks Mirror for one channel delivered reliably and another delivered as datagrams, and does not
care how - but a browser needs a transport that can actually deliver both, which today means
WebTransport.

The second one is gated on Mirror's `MIRROR` define, so a project on another networking library
gets the browser audio half and no compile errors from the half it has no use for.

See `Documentation~/Architecture.md` for what each part does and why.

## Requirements

* Unity 6000.0 or newer.
* Dissonance Voice Chat, and three small patches to its source - applied by a menu item, see below.
* Mirror, for the Mirror integration. Developed against 96.0.1.
* MirrorWTransport, for browser clients.
* CMake and [Ninja](https://ninja-build.org) on PATH, only if you need to rebuild Opus for
  WebAssembly - see step 3. Everything else that build needs ships inside Unity. Ninja is not
  optional: without it CMake on Windows falls back to Visual Studio, which quietly compiles Opus
  for x64 instead.
* A browser with WebTransport and AudioWorklet, served from a secure context (https, or localhost).

## Installing

**1. Install the package.** Add it through the package manager with this repository's URL, or drop
the folder under `Packages/` in your project.

**2. Patch Dissonance.** The editor offers to do this the first time it loads the package. If you
declined, or want to run it again: **Tools > Dissonance 4 Web > Patch Dissonance For Web**.

Until it is applied, `Dissonance4Web` does not compile - it implements an interface Dissonance
keeps internal - so expect "inaccessible due to its protection level" errors up to that point. The
patcher lives in an assembly of its own that references nothing, so the menu is there regardless.

Three edits to Dissonance's own source are unavoidable, and the patcher reports exactly what it
changed. The short version: Dissonance's preprocessing pipeline runs on a thread a WebGL player
cannot create, and the class that builds it is internal, so there has to be a way to substitute
one; and IL2CPP only links a native function directly when the `DllImport` names the module
`__Internal`, which is the same reason Dissonance already special-cases iOS. A fourth, optional
edit stops a harmless dependency error being logged on every start.

The edits are additive, anchored to exact text so a Dissonance version this was not written
against is reported rather than mangled, and reversible with **Revert Patches**.
`Documentation~/CorePatches.md` lists them in full, with the reasoning, so they can be applied by
hand or re-applied after a Dissonance update.

**3. Check the Opus build matches your Unity.** The package includes a prebuilt
`Runtime/Plugins/WebGL/libopus.a`, compiled with Emscripten 4.0.20 - the version Unity 6000.5 and
6000.6 bundle. Your editor's version is in
`<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/emscripten/emscripten-version.txt`.
If it matches, there is nothing to do. If it does not - Unity 6000.2 and 6000.3 bundle 3.1.39 -
rebuild the archive with your editor's toolchain, because an archive from a different Emscripten
can fail the WebGL link step with errors that do not say so:

```bash
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1
```

```bash
./Native~/build-opus-wasm.sh
```

The scripts use the Emscripten inside the Unity editor rather than a separate emsdk, so the rebuilt
archive matches the toolchain Unity will link it with. See `Native~/README.md`.

**4. Set up the scene.**

* On the `NetworkManager` object, add **Web Transport Transport** and assign it to `Transport`.
* On the object carrying `DissonanceComms`, add **Dissonance Web Audio**. If you do not already have a
**Mirror Ignorance Comms Network**, add **Mirror WTransport Comms Network**. You only need one of these.
In my game I use a multiplex transport to support both of these transports, so I only have the Ignorance Comms.
* On the player prefab, add **Mirror WTransport Player** for positional voice.

That is all the channel setup there is, because the comms network defaults to Mirror's own two
channels - reliable (0) for session setup and text, unreliable (1) for voice - and every transport
already delivers those correctly. **Reliable Channel** and **Unreliable Channel** on the comms
network give Dissonance ids of its own instead, if you would rather voice was batched and accounted
for separately; the inspector then says what your transport needs, and Dissonance checks it at
startup.

`Dissonance Web Audio` does nothing outside a WebGL player, so leave it in the scene for every
build target; one scene serves desktop and web.

Full walkthrough, including the certificate handling WebTransport needs:
`Documentation~/Setup.md`.

## How voice gets from a browser to a desktop player

```
browser                                  server                         desktop
-------                                  ------                         -------
getUserMedia
  -> AudioWorklet  (capture)
  -> resample to 48kHz, 480 frames
  -> Opus encode   (WebAssembly)
  -> Dissonance packet
  -> Mirror unreliable channel
  -> WebTransport datagram  ----------->  relayed by                                
                                          Dissonance's                              
                                          routing table   -----------> Opus decode (native)
                                          (never decoded)              -> jitter buffer
                                                                       -> OnAudioFilterRead
```

and the other way round, with the last two steps replaced by an AudioWorklet.

The server is the same Dissonance server it always was. It reads the routing header, works out
who is listening, and forwards the encoded audio untouched - so it does not matter to the server
which kind of client a frame came from, and there is no transcoding, no resampling and no decode
anywhere in the relay.

## What a browser does differently

Three parts of Dissonance's audio path are replaced, and one thing is genuinely lost. In short:
Unity's Web audio layer has no software mixer, so voice cannot go through an AudioSource, which
means Unity's spatialisation, mixer groups and audio effects do not apply to voice in a browser.
Distance attenuation and stereo panning are computed from the AudioListener and applied by Web
Audio nodes instead - close to an AudioSource with linear rolloff, and not the same as whatever
rolloff curve or spatialiser plugin the desktop build uses.

Echo cancellation, noise suppression and gain control are the browser's rather than Dissonance's,
requested through getUserMedia constraints. That is an upgrade rather than a loss: the browser's
echo canceller sits next to the real output device, so it cancels the whole game's audio and not
just voice.

Voice activity detection is an energy gate rather than Dissonance's WebRTC detector, which has no
WebAssembly build.

`Documentation~/KnownLimitations.md` has the complete list, including the ones that are worth
knowing before shipping.

## Testing

The browser half has a test suite that runs the real plugin and the real AudioWorklet processors
against a stub Web Audio API, covering the two ring buffers and the queue accounting that spans
the main thread and the audio thread:

```bash
node Tests~/jslib-audio.test.js
```

See `Tests~/README.md`.

## Licence

MIT, see `LICENSE`.

Dissonance Voice Chat is a commercial asset and is not included here; this package patches and
extends an installation you already have. Opus is BSD licensed and built from upstream source by
the script in `Native~`.
