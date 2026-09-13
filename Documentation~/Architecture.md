# Architecture

## What Dissonance already does, and where it stops

Dissonance's design separates cleanly into three layers, and only one of them is a problem in a
browser.

**The protocol layer** (`Dissonance.Networking`) is fully abstract. `ICommsNetwork`,
`BaseCommsNetwork`, `BaseServer` and `BaseClient` know nothing about any networking library; they
hand `ArraySegment<byte>` to two abstract methods, `SendReliable` and `SendUnreliable`, and get
packets back through `NetworkReceivedPacket`. An integration for a particular networking library
is a few hundred lines of plumbing. That is what `Dissonance4Web.Mirror` is, and it is why a
browser client and a desktop client can be in one voice session without either of them knowing
about the other's platform: they speak the same wire protocol, which has nothing to do with the
platform.

**The audio layer** is where the platform shows through. Capture and playback are pluggable -
`IMicrophoneCapture` and `IVoicePlaybackInternal` are public interfaces, exactly so a project can
substitute its own - but the pipeline between them is not, and the pipeline is the part a browser
cannot run.

**The codec** sits under both, and is the part that turns out to need the least work: Dissonance
uses Opus through a native library on every platform, and a WebAssembly build of upstream libopus
satisfies the same P/Invoke declarations the desktop build uses.

## The three replacements

### Capture: `WebMicrophoneCapture`

Implements `IMicrophoneCapture` and `IMicrophoneDeviceList`, which is Dissonance's documented
extension point: it finds the capture implementation with `GetComponent`, so a component on the
`DissonanceComms` object is all it takes.

Unity 6's Web player does have a `Microphone` class, and it is not usable for voice chat. It is
implemented over `MediaRecorder`: it records compressed chunks, decodes them only after recording
stops, and `AudioClip.GetData` refuses outright while recording is in progress
("Can't get data from microphone sound clip while recording is in progress"). That is a sound
design for record-then-play and useless for a live stream, so this goes to `getUserMedia` and an
`AudioWorkletNode` directly, through `Dissonance4WebAudio.jslib`.

The worklet batches 1024 samples (~21ms at 48kHz) and posts them to the main thread, which
appends to a one second ring buffer. `UpdateSubscribers`, called by Dissonance once a frame,
drains the ring and hands it on in 10ms blocks - the same size the preprocessing pipeline wants,
so nothing downstream has to hold a partial frame in the common case.

Opening the microphone is decoupled from Dissonance starting capture, because the two happen at
different times. Dissonance asks for capture as soon as the player joins a voice session;
`getUserMedia` is asynchronous, may put a permission prompt in front of the player, and - under
`MicrophoneAccessRequest.OnFirstTransmission` or `Manual` - is not called until later, or ever.

So `StartCapture` succeeds immediately even when the microphone is not open, returning the format
the microphone *will* have: the capture graph runs in the same AudioContext as playback, so the
context's sample rate is the microphone's. No audio arrives, and Dissonance sends nothing, which for
a player who only listens is exactly right. The alternative - returning null until the microphone
opens, Dissonance's way of being told there is no microphone - disables transmission and logs a
warning saying so, which is not news worth a warning for someone who never meant to talk.

When the browser does open the microphone, `UpdateSubscribers` returns `true` to restart the
pipeline rather than letting audio flow into the one built without it. That gives the encoder a
clean start - one started and stopped while there was no audio has not finished stopping - and
lets `StartCapture` begin with the microphone's own reported rate.

"First transmission" is judged the way Dissonance judges whether to feed its encoder: not muted,
and at least one room or player channel open. That covers push to talk, open mic, proximity
triggers and code that opens channels directly. Voice activation is the exception, because its
triggers open a channel only when they hear speech and cannot hear anything until the microphone is
open; an enabled, unmuted voice activation trigger therefore counts as intent in its own right.
Dissonance keeps its voice activation subscribers in a private list, so those triggers are found by
a scan, twice a second, only while waiting.

A refusal is reported once, with the browser's own reason, and leaves the player listening only.
`WebMicrophoneCapture.RequestAccess()` asks again, for a UI that wants to.

### Preprocessing: `WebPreprocessingPipeline`

This is the part that needs a patch, because there is no public seam here.

Dissonance's `BasePreprocessingPipeline` runs on a dedicated thread created with
`System.Threading.Thread`, which a WebGL player cannot do - Unity's Web platform is
single-threaded and `Thread.Start` throws. Its concrete subclass also calls into the
`AudioPluginDissonance` native library for echo cancellation, noise suppression and voice
detection, and that library has no WebAssembly build.

Both losses are smaller than they sound, because the browser already does that work. Echo
cancellation, noise suppression and gain control are requested through getUserMedia constraints,
and the browser's echo canceller is in a better position than Dissonance's: it runs next to the
output device, so it cancels the whole game's audio rather than only the voice Dissonance played.

What is left for this class to do is the part that has to match Dissonance exactly - resample the
browser's capture rate to the 48kHz, 480 sample frames the encoder expects, using Dissonance's own
resampler so the numbers come out the same - plus an energy gate standing in for the native voice
detector. It all runs inline, pushed along by each call to `ReceiveMicrophoneData` from the main
thread.

`IPreprocessingPipeline` is internal to Dissonance, so this class needs `InternalsVisibleTo`, and
`CapturePipelineManager` needs a hook to build it from. Those are two of the three required
patches; see `CorePatches.md`.

### Playback: `WebVoicePlayback`

Derives from Dissonance's public `BaseVoicePlayback`, so the jitter buffer, the Opus decoder, the
drift correction, the volume and priority rules and the session sequencing are all Dissonance's
own, unchanged. Only the last step is different.

Dissonance normally plays voice by attaching a DSP filter to an AudioSource and filling it from
`OnAudioFilterRead`. Unity's Web player has no software audio mixer - its audio layer is a thin
wrapper over Web Audio nodes, as the built `framework.js` shows: `JS_Sound_Play`,
`JS_Sound_SetPosition` and friends, and no DSP callback anywhere. `OnAudioFilterRead` is simply
never raised, so that path produces silence.

Instead, each speaker gets an `AudioWorkletNode` with a ring buffer, fed once a frame from
`Update`:

```
SpeechSession.Read(...)   ->  D4W_OutWrite  ->  postMessage  ->  worklet ring buffer
                                                                    |
                                                     GainNode -> StereoPannerNode -> output
```

How much to write is the interesting part. There is no shared memory - `SharedArrayBuffer` needs
COOP/COEP headers this does not assume - so the main thread cannot read the worklet's buffer
level. Instead the worklet reports its level along with a running total of samples it has
received, every 256 frames, and the main thread computes:

```
queued = last reported level + (samples posted - samples the worklet says it received)
```

which is exact regardless of how stale the last report is, because every posted sample is either
counted in a later report or still in flight. `Tests~/jslib-audio.test.js` checks this, including
across a reset, where an epoch number keeps a report from before the reset from resurrecting a
buffer that has been thrown away.

Going around Unity's audio costs Unity's spatialisation. Distance attenuation and stereo pan are
computed here from the AudioListener and applied by the gain and panner nodes, which approximates
an AudioSource with linear rolloff. See `KnownLimitations.md`.

## The codec

`OpusNative` in Dissonance declares fifteen P/Invokes against a library called `opus`. Upstream
libopus, compiled to WebAssembly by `Native~/build-opus-wasm.ps1`, provides all but four of them;
the four missing ones are Dissonance's non-variadic wrappers around `opus_encoder_ctl` and
`opus_decoder_ctl`, which exist because P/Invoke cannot describe a variadic call. Dissonance ships
a patched opus carrying those wrappers for desktop; here they are four lines of C in
`Runtime/Plugins/WebGL/dissonance_opus_shim.c`, which Unity compiles as part of the build.

The one wrinkle is the module name. IL2CPP resolves a `DllImport` at runtime through
`LibraryLoader`, and on Emscripten that always fails - `il2cpp`'s own source says so, and returns
an invalid handle unconditionally. Only the module name `__Internal` makes IL2CPP emit a direct
call to a statically linked symbol. Dissonance already special-cases iOS for exactly this reason,
so the patch adds WebGL to the same condition. That is the third required patch.

The upshot is that the browser runs the same managed `OpusEncoder` and `OpusDecoder` as every
other platform, with the same settings from `VoiceSettings`, so the frames it produces are frames
any Dissonance peer can decode. Nothing negotiates a codec, nothing transcodes, and the server
never decodes at all.

## The network integration

`Dissonance4Web.Mirror` is a rewrite of Dissonance's `MirrorIgnorance` integration under a name
that says what it is for. The structure is the same, because that structure is right:

* `MirrorWTransportCommsNetwork` watches Mirror's state and starts the voice session as a host, a
  dedicated server or a client to match. It also short-circuits host mode loopback, queueing a
  packet from the local server for delivery to the local client on the next frame rather than
  running the client inside a server call stack.
* `MirrorWTransportServer` is the relay. It installs a Mirror message handler, hands packets to
  Dissonance's routing table and sends the results back out. It polls for disconnections, because
  Mirror only reports those to the `NetworkManager` and *assigns* rather than adds to
  `NetworkServer.OnDisconnectedEvent`, so subscribing would either clobber the NetworkManager or
  be clobbered by it.
* `MirrorWTransportClient` sends to the server and receives from it. It is identical on desktop
  and in a browser.
* `MirrorWTransportPlayer` ties a Mirror network object to a Dissonance player name with a SyncVar,
  so positional voice works and a client joining a game in progress has the right names for
  everyone already spawned.

### Keeping the two halves separable

The audio assembly references Dissonance and nothing else. The Mirror assembly is gated on
Mirror's own `MIRROR` define through `defineConstraints`, so a project using another networking
library gets the browser audio pipeline and none of the Mirror code - it simply is not compiled.
Writing the integration for that other library means the same few hundred lines of plumbing
against `BaseCommsNetwork`, `BaseServer` and `BaseClient`, with nothing browser specific in it;
`Dissonance4Web.Mirror` is a working example of the shape.

Five assemblies in total: `Dissonance4Web`, `Dissonance4Web.Mirror`, an editor assembly for each,
and `Dissonance4Web.Patcher.Editor`.

That last one references nothing - not Dissonance, not the rest of this package - and that is
deliberate. It carries the core patcher, and the patcher has to be usable in a project where
`Dissonance4Web` does not compile, because *not compiling* is exactly the state an unpatched
install is in. An editor assembly that referenced the broken one would be skipped along with it,
which would leave the Tools menu missing precisely when it is needed. The build check lives there
too, for the same reason: a check that disappears whenever the build is going to fail is no check
at all.

### Channels are the project's to choose

Mirror describes channels as plain ints rather than an enum, with a comment saying why: so that a
project can add its own. That cuts both ways - there is no id reserved for Dissonance, and a
package has no business claiming one, because whichever id it picked might already be a project's
own channel.

So the two ids are settings on `MirrorWTransportCommsNetwork` -`ReliableChannel` and
`UnreliableChannel`, serialized and drawn in the inspector - and they default to
`Channels.Reliable` and `Channels.Unreliable`, Mirror's own two. Sharing those with the rest of the
game's traffic costs nothing in particular: a channel id selects a delivery mode, and Mirror keeps
messages within a channel apart by message id. What it buys is that the defaults need no
configuration on any transport, and work against stock Mirror.

A project that wants voice batched and accounted for separately points them at ids of its own and
extends the transport's channel list to match. The inspector spells out what that list needs, since
the consequence of getting it wrong is subtle: MirrorWTransport delivers an id past the end of its
list *reliably*, which is the right default in general and wrong for voice, and shows up as voice
drifting further behind the game the longer a lossy connection lasts rather than as anything that
looks like a misconfiguration.

### How transport agnostic is it, really

The integration names no transport. It asks Mirror to deliver one channel reliably and another as
datagrams, and Mirror's `Transport` base class has no API to ask a transport whether it will.
`DissonanceChannels` therefore probes for an `IsReliableChannel(int)` method by name - which
MirrorWTransport has - and warns when the answer is wrong. A transport that does not expose one is
left alone with a debug line saying so. So: transport agnostic in what it requires, and able to
check only with transports that will answer.

What is *not* agnostic is the practical set of transports a browser can use. A WebGL client needs
a transport that offers an unreliable channel in a browser, and WebSockets cannot; that is what
MirrorWTransport is for, and why this package is named after it.

### Why the server does not need to be special

A browser cannot host. Browsers can only be WebTransport clients, so Mirror cannot start a server
in a WebGL build and neither can Dissonance. That is not a limitation this package adds - it
follows from the transport - and it costs nothing, because the server does not need to be a
browser: a desktop host or a dedicated server relays browser voice exactly as it relays anyone
else's.
