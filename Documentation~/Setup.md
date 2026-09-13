Dissonance4Web allows projects that use Dissonance and Mirror to voice chat with each other
from web to web, other platforms (including desktop) to web, web to other platforms, and
other platforms to other platforms. In other words, the web becomes a first class citizen
in the Dissonance framework with this library.

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

The editor offers this the first time it loads the package, and again on each restart until it is
done. To run it by hand: **Tools > Dissonance 4 Web > Patch Dissonance For Web.**

Until the patches are applied `Dissonance4Web` does not compile, because it implements an interface
Dissonance keeps internal, so a fresh install starts with a handful of "inaccessible due to its
protection level" errors. They go away with the patch. The patcher is in an assembly that
references nothing, so its menu items are available whatever else in the project is broken.

**Tools > Dissonance 4 Web > Check Patches On Startup** turns the prompt off if you would rather
not be asked; the console warning stays either way.

The console reports what changed. Re-run it after any Dissonance update; **Check Patches** says
whether it is needed. `CorePatches.md` explains each edit.

## 3. Opus for WebAssembly

The package includes a prebuilt `Runtime/Plugins/WebGL/libopus.a` and its `.meta`, compiled with
Emscripten 4.0.20 - the version Unity 6000.5 and 6000.6 bundle. Check your editor's version in
`<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/emscripten/emscripten-version.txt`.
If it matches, skip to step 4.

If it does not - Unity 6000.2 and 6000.3 bundle 3.1.39 - rebuild the archive with your editor's
toolchain:

```bash
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1
```

```bash
./Native~/build-opus-wasm.sh
```

Needs CMake and Ninja on PATH. Everything else - Emscripten, LLVM, python, node - comes from
inside the Unity editor, which is deliberate: see `Native~/README.md`. Ninja is required rather
than preferred: without it CMake on Windows builds with Visual Studio, which ignores Emscripten and
produces an x64 archive the WebGL link step cannot use. The script stops if Ninja is missing, and
checks that what it built really is WebAssembly before publishing it.

The rebuilt archive replaces `Runtime/Plugins/WebGL/libopus.a`, and the committed `.meta` beside it
keeps it enabled for WebGL only. Check the Plugin Inspector once and confirm the platform reads
WebGL and nothing else. Every native plugin in this package must be WebGL-only - Unity does not
infer that from the folder name, and a plugin enabled for desktop breaks the desktop build.

Rebuild it again after moving to a Unity version with a different bundled Emscripten.

## 4. The NetworkManager

Add **Web Transport Transport** to the `NetworkManager` object and assign it to `Transport`. If the
project already has a transport, use Mirror's Multiplex Transport.

The transport's **Channels** list needs nothing done to it, because Dissonance defaults to Mirror's
own two channels and every transport already delivers those correctly. Step 5 covers giving
Dissonance channels of its own, if you want that.

One size setting is worth a look while here. Dissonance never emits a packet larger than 1024
bytes, and Mirror adds a little around it, so `Unreliable Max Message Size` needs to be at least
about 1040. The default of 1024 is marginal; 1200 is a safe value that still fits a QUIC datagram.
In practice a voice packet is nearer 150 bytes.

## 5. The Dissonance object

On the game object carrying `DissonanceComms`:

* **Mirror WTransport Comms Network** or **MirrorIgnoranceCommsNetwork** if you are multiplexing
with Ignorance. Do not use both - only one comms network is needed.
* **Dissonance Web Audio** - the browser audio pipeline.

### Channels

**Mirror WTransport Comms Network** has two settings, and the defaults are the ones most projects
want:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Reliable Channel` | 0 (`Channels.Reliable`) | Session setup, room membership, text chat. Must be reliable and ordered. |
| `Unreliable Channel` | 1 (`Channels.Unreliable`) | Voice. Must be datagrams. |

Mirror describes channels as plain ints rather than an enum so that a project can add its own,
which means there is no id reserved for Dissonance to claim - so it shares Mirror's two by default.
That costs nothing in particular: a channel id selects a delivery mode, and Mirror keeps messages
within a channel apart by message id. It also means there is nothing to configure, on any
transport.

Give Dissonance ids of its own if you would rather voice was batched and accounted for separately
from the rest of the game's traffic. Two things then have to line up:

* The transport has to know about the new ids. MirrorWTransport's **Channels** list is indexed by
  channel id, and **an id past the end of that list is delivered reliably** - safe in general, and
  wrong for voice, because voice would be retransmitted and head-of-line blocked and would drift
  further and further behind the game on a lossy connection. So extend the list to cover the
  highest id you use.
* The ids must not collide with any other channel the project defines.

The comms network inspector spells out what your transport needs for whatever ids you set, and
Dissonance re-checks it at startup - for transports that report their channel delivery, which
MirrorWTransport does.

From code, the same two settings are `ReliableChannel` and `UnreliableChannel` on the component,
and `DissonanceChannels.DefaultReliable` / `DefaultUnreliable` are the defaults. Set them before
Dissonance connects.

### Web audio

`Dissonance Web Audio` does nothing at all outside a WebGL player - it checks the runtime platform,
not the build target - so leave it in place for every build. One scene serves desktop and web.

Its settings:

| Setting | Meaning |
| --- | --- |
| `Web Playback Prefab` | Leave empty and a minimal one is built at runtime. Assign a prefab with a `WebVoicePlayback` component to change buffer size or positional distances, or to attach an `IAudioOutputSubscriber`. |
| `Echo Cancellation` | Ask the browser to cancel output from the captured signal. Leave on. |
| `Noise Suppression` | Ask the browser to suppress steady background noise. |
| `Auto Gain Control` | Ask the browser to normalise the input level. |
| `Microphone Access` | When to ask the player for the microphone: `On Start`, `On First Transmission` (listen-only players are never prompted), or `Manual`. See [Choosing when to ask for the microphone](#choosing-when-to-ask-for-the-microphone). |

## 6. The player prefab

Add **Mirror WTransport Player** to the player prefab, alongside its `NetworkIdentity`. That is
what gives Dissonance a position to attenuate and pan voice by, and what ties a Mirror network
object to a Dissonance player name.

If this prefab already uses a `MirrorIgnorancePlayer`, choose one to use. They are mostly identical.
Do not keep both: they would both try to track the same player.

## 7. Running it

Build for WebGL and serve it over https or from localhost. Unity's own **Build And Run** serves
from localhost, which counts as secure.

Expected console output on a browser client, with `Microphone Access` left at `On Start`. When the
browser already remembers a grant for the page:

```
[Dissonance4Web] audio worklet ready at 48000Hz
[Dissonance4Web] microphone running at 48000Hz
Started browser microphone capture: 48000Hz, 31ms latency
```

On a first visit, the voice session usually starts while the permission prompt is still up. Capture
starts anyway and restarts once when the player answers:

```
[Dissonance4Web] audio worklet ready at 48000Hz
Waiting for the browser to grant microphone access; voice will be sent once it does
[Dissonance4Web] microphone running at 48000Hz
The browser opened the microphone; restarting voice capture
Started browser microphone capture: 48000Hz, 31ms latency
```

With `On First Transmission`, nothing is requested until the player first tries to talk:

```
[Dissonance4Web] audio worklet ready at 48000Hz
Voice capture is ready; the microphone will be opened when this player first transmits
Requesting microphone access: this player started transmitting
[Dissonance4Web] microphone running at 48000Hz
The browser opened the microphone; restarting voice capture
Started browser microphone capture: 48000Hz, 31ms latency
```

A player who never talks stops at the second line, and hears everyone regardless.

## Choosing when to ask for the microphone

`Microphone Access` on `Dissonance Web Audio` decides when the browser's permission prompt appears.
Every setting lets the player hear voice chat; they differ only in when the player is asked to join
in.

| Setting | The prompt appears | Suits |
| --- | --- | --- |
| `On Start` (default) | As the scene loads. | Games where nearly everyone talks. Voice works the first time the player presses the key. |
| `On First Transmission` | The first time the player tries to send voice. | Games where many players only listen. They never see the prompt. |
| `Manual` | When your code calls `RequestMicrophoneAccess()`. | A settings screen or an "enable voice" button of your own. |

"Tries to send voice" means the same thing it means to Dissonance: the player is not muted and a
broadcast trigger has opened a channel - pressing push to talk, standing in an open channel or a
proximity trigger's range, or code opening a channel directly. Voice activation is the exception;
see below.

`On First Transmission` costs one thing: **the first press of push to talk sends nothing.** That
press brings up the prompt, and voice starts once the player has answered. The microphone then
stays open for the session, so it happens once.

### Voice activation and `On First Transmission`

A voice activation trigger opens its channel when it hears the player speak - and it cannot hear
anything until the microphone is open. Waiting for it would wait forever, so an enabled, unmuted
voice activation trigger counts as the player trying to talk, and the prompt appears as soon as the
voice session starts. That is the same as `On Start`.

To keep listen-only players unprompted in a voice activated game, start the trigger muted and unmute
it when the player chooses to speak:

```csharp
public class SpeakToggle : MonoBehaviour
{
    public VoiceBroadcastTrigger Trigger; // Mode: Voice Activation, starting muted

    // Wire this to a "speak" toggle.
    public void SetSpeaking(bool speaking)
    {
        // Unmuting is what counts as trying to talk, so the prompt follows within
        // half a second of the first time this turns speaking on.
        Trigger.IsMuted = !speaking;
    }
}
```

### Behind a button

A browser prompt that appears the instant a page loads is often refused, and a refusal sticks for
the session. To ask at a moment of your choosing, set `Microphone Access` to `Manual` and call:

```csharp
public class VoiceOptIn : MonoBehaviour
{
    public DissonanceWebAudio WebAudio;

    // Wire this to a button.
    public void EnableVoice()
    {
        // Browsers only start audio from a real user gesture, so this is the place
        // to resume it - and a prompt that follows the player's own click at least
        // comes with context.
        WebAudio.ResumeAudio();
        WebAudio.RequestMicrophoneAccess();
    }
}
```

`MicrophoneAccess` can also be changed at runtime, up until access has been requested. A game that
lets players opt in to voice without wanting a separate button can start at `Manual` and switch to
`OnFirstTransmission` when they opt in, so the prompt then waits for their first attempt to talk.

### What happened

`DissonanceWebAudio.Microphone.IsAccessRequested` is false for a player who has not been asked -
worth showing as a "listening only" indicator. `.State` reports the rest - `Starting`, `Running` or
`Failed` - and `.Error` carries the browser's own reason when the microphone did not open, which is
what to show the player. `RequestMicrophoneAccess()` after a refusal asks again, and a grant then
restarts capture on its own.

## A microphone picker

`DissonanceComms.GetMicrophoneDevices` returns nothing in a Web player; it is compiled around
Unity's `Microphone` class. Use `DissonanceWebAudio.MicrophoneDevices` instead - a static
counterpart of `Microphone.devices` that returns the browser's microphones in a Web player and
`Microphone.devices` everywhere else, so one picker serves both:

```csharp
var devices = DissonanceWebAudio.MicrophoneDevices;

// Assign the chosen one the usual way.
comms.MicrophoneName = devices[index];
```

In a browser this is the browser's own list, not Unity's web `Microphone.devices`: these are the
names Dissonance 4 Web's capture resolves, and reading them needs no
`Application.RequestUserAuthorization`. The browser lists devices asynchronously, so a read in the
first frame can be empty; after that the list keeps itself up to date as devices are plugged in and
removed.

Browsers hide device labels until access has been granted once. Before that the list contains
placeholder names - "Microphone 1", "Microphone 2" - which still select the right device, so a
picker shown before the prompt works but reads poorly. Showing it after is better.

## Troubleshooting

**Voice works desktop-to-desktop but not to or from the browser.** Check the browser console for
`[Dissonance4Web] audio worklet ready`; without it the page is not a secure context. This is an
audio problem rather than a networking one - the browser is connected, or Mirror itself would not
be working either.

**The browser console says the AudioWorklet module could not be loaded.** The page is not on https
or localhost. `AudioWorklet` does not exist in an insecure context.

**"The browser has suspended audio, so no voice will be captured or heard."** The page has not seen
a click or a key press yet. Browsers start audio suspended. Call
`DissonanceWebAudio.ResumeAudio()` from a user gesture, or just wait for the player to click
something.

**Voice from the browser is choppy on the desktop side.** Look at
`Unreliable Max Message Size`; a Dissonance packet dropped for being oversized is logged by the
transport.

**Voice latency grows the longer a lossy connection lasts, instead of dropping words.** Voice is
being delivered reliably. If `Unreliable Channel` has been changed from the default, the transport
needs a matching entry in its channel list - the comms network inspector says which - because an id
past the end of that list is delivered reliably. Dissonance logs a warning about this at startup.

**Voice into the browser is choppy.** Raise `Target Buffer Ms` on the `WebVoicePlayback` component
(assign a `Web Playback Prefab` to get at it). The default 70ms covers a 30fps frame with room to
spare, but a page competing with a heavy main thread may want more. The playback component logs
underruns at debug level.

**The WebGL build fails at the link step with undefined `opus_*` symbols.** `libopus.a` is missing
or was built with a different Emscripten. Re-run the build script. `Dissonance4WebBuildCheck`
normally catches the missing case before the build starts.

**The link step warns `archive member 'opus.dir\Release\....obj' is neither Wasm object file nor
LLVM bitcode`, once per Opus source file.** `libopus.a` was compiled for Windows, not WebAssembly.
That happens when CMake uses its Visual Studio generator, which ignores the Emscripten toolchain.
The build script forces Ninja and checks the result before publishing it, so re-run it with Ninja
on PATH; it discards a build directory left configured for Visual Studio by itself.
`Dissonance4WebBuildCheck` now inspects the archive's contents too, and stops a WebGL build before
it starts if the archive is not WebAssembly.

**A desktop build fails with `unresolved external symbol opus_encoder_ctl referenced in function
dissonance_opus_encoder_ctl_in`.** `dissonance_opus_shim.c` has been compiled into a non-WebGL
player. Unity compiles a C source plugin into every IL2CPP player its importer allows, and the
folder being named `WebGL` does not restrict that; on desktop there is no static
`opus_encoder_ctl` to link against, because desktop Opus lives in `opus.dll`. The package ships
`.meta` files enabling its plugins for WebGL only, and the shim's body only compiles under
Emscripten as a backstop, so this means an old copy of the package or a `.meta` that was
regenerated. Update the package, then select `Runtime/Plugins/WebGL/dissonance_opus_shim.c` and
confirm the Plugin Inspector shows WebGL and nothing else; if it does not, right-click it and
choose **Reimport**.

**A `DissonanceComms` error about no preprocessing pipeline being available.** The
`DissonanceWebAudio` component is not on the same game object as `DissonanceComms`.

**"inaccessible due to its protection level" errors, and no Tools > Dissonance 4 Web menu.** The
menu is in `Dissonance4Web.Patcher.Editor`, which references nothing and so should compile whatever
else is broken. If it is genuinely absent, Unity has not imported the package's Editor folder at
all - check the Console for an assembly definition error, and that
`Editor/Patcher/Dissonance4Web.Patcher.Editor.asmdef` came through the install.
