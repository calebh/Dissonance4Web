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
it asks Mirror for channel 3 reliably and channel 4 unreliably and does not care how - but a
browser needs a transport that can actually deliver both, which today means WebTransport.

The second one is gated on Mirror's `MIRROR` define, so a project on another networking library
gets the browser audio half and no compile errors from the half it has no use for.

See `Documentation~/Architecture.md` for what each part does and why.

## Requirements

* Unity 6000.0 or newer.
* Dissonance Voice Chat, and three small patches to its source - applied by a menu item, see below.
* Mirror, for the Mirror integration. Developed against 96.0.1.
* MirrorWTransport, for browser clients.
* CMake, once, to build Opus for WebAssembly. Everything else that build needs ships inside Unity.
* A browser with WebTransport and AudioWorklet, served from a secure context (https, or localhost).

## Installing

**1. Install the package.** Add it through the package manager with this repository's URL, or drop
the folder under `Packages/` in your project.

**2. Patch Dissonance.** Run **Tools > Dissonance 4 Web > Patch Dissonance For Web**.

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

**3. Build Opus for WebAssembly.**

```bash
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1
```

```bash
./Native~/build-opus-wasm.sh
```

This drops `libopus.a` into `Runtime/Plugins/WebGL/`. It uses the Emscripten that ships inside the
Unity editor rather than a separate emsdk, so the archive matches the toolchain Unity will link it
with - a mismatch there fails the build with errors that do not say so. See `Native~/README.md`.

**4. Set up the scene.**

* On the `NetworkManager` object, add **Web Transport Transport** and assign it to `Transport`.
* Extend the transport's **Channels** list to five entries and set them to
  `Reliable, Unreliable, Unreliable, Reliable, Unreliable`. Mirror reserves channel 3 and 4 for
  Dissonance, and a channel past the end of that list is delivered reliably - which leaves voice
  retransmitted and head-of-line blocked. Dissonance logs a warning at startup if it finds this
  wrong.
* On the object carrying `DissonanceComms`, add **Mirror WTransport Comms Network** and
  **Dissonance Web Audio**.
* On the player prefab, add **Mirror WTransport Player** for positional voice.

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
  -> Mirror channel 4
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
