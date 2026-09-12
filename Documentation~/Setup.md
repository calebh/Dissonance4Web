# Setup

A walkthrough from an empty project to a browser and a desktop player talking to each other.

Everything here that is not about the browser is ordinary Dissonance and ordinary
MirrorWTransport setup; those two have their own documentation and it still applies.

## 1. Packages

* **Dissonance Voice Chat**, from the asset store.
* **Mirror**, 96.0.1 or thereabouts.
* **MirrorWTransport** - `https://github.com/calebh/MirrorWTransport.git` in the package manager.
* **Dissonance 4 Web** - this package.

## 2. Patch Dissonance

**Tools > Dissonance 4 Web > Patch Dissonance For Web.**

The console reports what changed. Re-run it after any Dissonance update; **Check Patches** says
whether it is needed. `CorePatches.md` explains each edit.

## 3. Build Opus for WebAssembly

```bash
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1
```

```bash
./Native~/build-opus-wasm.sh
```

Needs CMake on PATH. Everything else - Emscripten, LLVM, python, node - comes from inside the
Unity editor, which is deliberate: see `Native~/README.md`.

The result is `Runtime/Plugins/WebGL/libopus.a`. Check the Plugin Inspector once and confirm the
platform reads WebGL and nothing else; Unity infers that from the folder name, so it should
already be right.

Rebuild it after upgrading Unity to a version with a different bundled Emscripten.

## 4. The NetworkManager

Add **Web Transport Transport** to the `NetworkManager` object and assign it to `Transport`. If the
project already has a transport, use Mirror's Multiplex Transport.

Then extend the transport's **Channels** list to **five** entries:

| Index | Channel | Delivery |
| --- | --- | --- |
| 0 | `Reliable` | Reliable |
| 1 | `Unreliable` | Unreliable |
| 2 | (your own, if any) | as you need |
| 3 | `DissonanceReliable` | **Reliable** |
| 4 | `DissonanceUnreliable` | **Unreliable** |

This is the step most likely to be missed, and the symptom does not point at it. A channel id past
the end of the list is delivered reliably, which is the safe default in general and wrong for
voice: voice would be retransmitted and head-of-line blocked, so on a lossy connection it drifts
further and further behind the game instead of dropping a word. Dissonance logs a warning at
startup when it finds this, but only for transports that can be asked.

While here, two size settings are worth a look. Dissonance never emits a packet larger than 1024
bytes, and Mirror adds a little around it, so `Unreliable Max Message Size` needs to be at least
about 1040. The default of 1024 is marginal; 1200 is a safe value that still fits a QUIC datagram.
In practice a voice packet is nearer 150 bytes.

## 5. The Dissonance object

On the game object carrying `DissonanceComms`:

* **Mirror WTransport Comms Network** - the Dissonance integration for Mirror.
* **Dissonance Web Audio** - the browser audio pipeline.

Remove `MirrorIgnoranceCommsNetwork` if the project had it; two comms networks on one object will
not work.

`Dissonance Web Audio` does nothing at all outside a WebGL player - it checks the runtime platform,
not the build target - so leave it in place for every build. One scene serves desktop and web.

Its settings:

| Setting | Meaning |
| --- | --- |
| `Web Playback Prefab` | Leave empty and a minimal one is built at runtime. Assign a prefab with a `WebVoicePlayback` component to change buffer size or positional distances, or to attach an `IAudioOutputSubscriber`. |
| `Echo Cancellation` | Ask the browser to cancel output from the captured signal. Leave on. |
| `Noise Suppression` | Ask the browser to suppress steady background noise. |
| `Auto Gain Control` | Ask the browser to normalise the input level. |
| `Request Microphone Access On Start` | Prompt as soon as the scene loads. Turn off to put the prompt behind a button; see below. |

## 6. The player prefab

Add **Mirror WTransport Player** to the player prefab, alongside its `NetworkIdentity`. That is
what gives Dissonance a position to attenuate and pan voice by, and what ties a Mirror network
object to a Dissonance player name.

If the project used `MirrorIgnorancePlayer`, replace it. Do not keep both: they would both try to
track the same player.

## 7. Certificates

WebTransport is always encrypted, so there is no plaintext mode for development. MirrorWTransport's
README covers this properly; the short version is that a development server generates a
self-signed certificate on every start and logs its SHA-256 hash, which the client has to pin in
`Client Certificate Hash`. The hash changes on every restart and on every rotation, so anything
other than an editor-to-editor test needs to fetch the current one rather than hold on to one -
which usually means the same master server that hands out the server list.

Two things that bite here specifically:

* The page hosting the WebGL build must be a **secure context**: https, or localhost. Without it
  neither `WebTransport` nor `AudioWorklet` exists, and voice will be silent with an error in the
  browser console.
* WebTransport needs **UDP** open on the server port, not TCP.

## 8. Running it

Build for WebGL and serve it over https or from localhost. Unity's own **Build And Run** serves
from localhost, which counts as secure.

Expected console output on a browser client, in order:

```
[Dissonance4Web] audio worklet ready at 48000Hz
Requesting browser microphone access for device '<default>'
[Dissonance4Web] microphone running at 48000Hz
Started browser microphone capture: 48000Hz, 31ms latency
```

On the first visit, the permission prompt takes longer than Dissonance's first attempt to start
capture, so a line about waiting for access appears in between. That is normal:

```
Waiting for the browser to grant microphone access; voice capture will start once it does
Failed to start microphone capture; local voice transmission will be disabled.
[Dissonance4Web] microphone running at 48000Hz
The browser granted microphone access; restarting voice capture
Started browser microphone capture: 48000Hz, 31ms latency
```

The "will be disabled" line is Dissonance's, and it is retracted by the next two.

## Putting the permission prompt behind a button

A browser prompt that appears the instant a page loads is usually refused, and a refusal sticks for
the session. To ask at a better moment, turn off `Request Microphone Access On Start` and call:

```csharp
public class VoiceOptIn : MonoBehaviour
{
    public DissonanceWebAudio WebAudio;

    // Wire this to a button.
    public void EnableVoice()
    {
        // Browsers only start audio from a real user gesture, and only allow the
        // microphone prompt to be useful in the same circumstances.
        WebAudio.ResumeAudio();
        WebAudio.RequestMicrophoneAccess();
    }
}
```

`DissonanceWebAudio.Microphone.State` reports what happened - `Starting`, `Running` or `Failed` -
and `.Error` carries the browser's own reason for a refusal, which is what to show the player.
`WebMicrophoneCapture.Retry()` asks again, and forces the capture pipeline to restart so a second
grant takes effect.

## A microphone picker

`DissonanceComms.GetMicrophoneDevices` returns nothing in a Web player; it is compiled around
Unity's `Microphone` class. Use the component instead:

```csharp
var devices = new List<string>();
webAudio.GetMicrophoneDevices(devices);

// Assign the chosen one the usual way.
comms.MicrophoneName = devices[index];
```

Browsers hide device labels until access has been granted once. Before that the list contains
placeholder names - "Microphone 1", "Microphone 2" - which still select the right device, so a
picker shown before the prompt works but reads poorly. Showing it after is better.

## Troubleshooting

**Voice works desktop-to-desktop but not to or from the browser.** Check the transport's Channels
list has five entries (step 4). Then check the browser console for
`[Dissonance4Web] audio worklet ready`; without it the page is not a secure context.

**The browser console says the AudioWorklet module could not be loaded.** The page is not on https
or localhost. `AudioWorklet` does not exist in an insecure context.

**"The browser has suspended audio, so no voice will be captured or heard."** The page has not seen
a click or a key press yet. Browsers start audio suspended. Call
`DissonanceWebAudio.ResumeAudio()` from a user gesture, or just wait for the player to click
something.

**Voice from the browser is choppy on the desktop side.** Look at
`Unreliable Max Message Size` and at the Channels list; a Dissonance packet dropped for being
oversized is logged by the transport.

**Voice into the browser is choppy.** Raise `Target Buffer Ms` on the `WebVoicePlayback` component
(assign a `Web Playback Prefab` to get at it). The default 70ms covers a 30fps frame with room to
spare, but a page competing with a heavy main thread may want more. The playback component logs
underruns at debug level.

**The WebGL build fails at the link step with undefined `opus_*` symbols.** `libopus.a` is missing
or was built with a different Emscripten. Re-run the build script. `Dissonance4WebBuildCheck`
normally catches the missing case before the build starts.

**A `DissonanceComms` error about no preprocessing pipeline being available.** The
`DissonanceWebAudio` component is not on the same game object as `DissonanceComms`.
