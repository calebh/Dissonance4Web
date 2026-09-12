# Known limitations

What a browser client does not get, and what it gets differently. All of this applies only to a
WebGL player; a desktop or mobile build runs Dissonance unchanged.

## Worth knowing before shipping

### No Unity audio processing on voice

Unity's Web player has no software audio mixer. Its audio layer is a thin wrapper over Web Audio
nodes, and it raises no DSP callback, which is why voice cannot go through an `AudioSource` at all
in a browser. Everything that hangs off an AudioSource therefore does not apply to voice:

* AudioMixer groups, and anything routed through them - ducking, sidechain compression, a
  master volume slider implemented as a mixer parameter.
* Audio effect components: reverb zones, low/high pass filters, the Dissonance echo cancellation
  filter.
* Unity spatialiser plugins, including Oculus Audio and Steam Audio.
* `AudioSource` rolloff curves, spread, doppler.

Positional voice still works, computed from the AudioListener and applied by a Web Audio gain and
stereo panner node. It approximates an AudioSource with linear rolloff between `Min Distance` and
`Max Distance` on the `WebVoicePlayback` component. It is not the same as whatever the desktop
build does, so a game that has tuned voice spatialisation carefully will hear the difference.

A master volume slider that sets `DissonanceComms.RemoteVoiceVolume` works fine in both, because
that is applied inside Dissonance's own pipeline rather than by Unity's mixer. If a project's
volume control goes through an AudioMixer, it needs a second path for web.

### Echo cancellation is the browser's

Dissonance's WebRTC echo canceller, noise suppressor and gain control are not available - there is
no WebAssembly build of `AudioPluginDissonance`. The browser's own are requested through
getUserMedia constraints instead.

This is mostly an upgrade. The browser's echo canceller runs next to the real output device, so it
cancels the whole page's audio, not only the voice Dissonance played - which is something
Dissonance's canceller cannot do without the AEC filter component in the mixer, and that component
does not work here either.

What is lost is control. `VoiceSettings`'s noise suppression level, AEC suppression level and AEC
routing mode have no effect in a browser; the browser exposes on/off and nothing more. The toggles
on the `DissonanceWebAudio` component are those on/off switches.

### Voice activity detection is an energy gate

Dissonance's VAD is part of the same native library, so a browser gets `EnergyVoiceDetector`
instead: a noise floor estimate, a threshold above it, and hysteresis so a word with a quiet middle
is not chopped in two.

It is not as discriminating as the WebRTC detector - it cannot tell speech from a door slam - but
it does not have to be as clever, because the signal reaching it has already been through the
browser's noise suppressor. `VoiceSettings.VadSensitivity` still applies and still means what it
says; the four levels map to how far above the noise floor a signal has to be.

Push-to-talk and the amplitude meter are unaffected, because neither goes through the detector.

### A browser cannot host

Browsers can only be WebTransport clients, so Mirror cannot start a server in a WebGL build and
neither can Dissonance. This is not something this package adds - it is how the transport works -
and it costs little, because a desktop host or a dedicated server relays browser voice exactly as
it relays anyone else's.

### The page must be a secure context

https, or localhost. Both `WebTransport` and `AudioWorklet` are absent otherwise, and voice will
be silent. This is a browser rule, not a Unity one.

### Audio starts suspended

A browser will not run an AudioContext until the page has seen a real user gesture. Until then
nothing is captured and nothing is played, and the log says so once. In practice Unity's own audio
needs the same gesture and this shares Unity's context, so it is resolved by the time anyone is in
a game - it matters for a menu scene that runs a voice session before the player clicks anything.
`DissonanceWebAudio.ResumeAudio()` exists for that case.

## Smaller things

### Latency

A browser client carries roughly 20ms more latency than a desktop one, in two places: the capture
worklet batches 1024 samples (~21ms at 48kHz) before handing them over, and playback keeps a
70ms buffer by default rather than relying on Unity's DSP buffer.

`TargetBufferMs` on `WebVoicePlayback` trades that against robustness. Lower is more responsive;
too low and a slow frame becomes an audible gap, because the buffer is only topped up once per
Unity frame - 33ms apart at 30fps.

### Device names are hidden until access is granted

Browsers do not reveal microphone labels to a page that has not been granted access. Before that,
`GetMicrophoneDevices` returns placeholders - "Microphone 1", "Microphone 2" - which still select
the right device but read poorly. Show a device picker after the permission prompt, not before.

### No audio diagnostics to disk

`DebugSettings`'s recording diagnostics write .wav files, which a browser has no filesystem for.
The preprocessing pipeline here does not implement them.

### The microphone is left open

`StopCapture` stops delivering samples but does not close the media stream. Closing it makes some
browsers drop the permission grant, and Dissonance restarts the capture pipeline often - on every
device change and every detected frame hitch - so re-prompting would be intolerable. The
consequence is that the browser's recording indicator stays lit while the game is running, even
while nobody is transmitting. `WebMicrophoneCapture` is where to change that if a project would
rather have the indicator behaviour.

### Opus has to be rebuilt when Unity changes

`libopus.a` is linked into the player by Unity's own Emscripten, and an archive built by a
different LLVM version fails that link. The build script uses the editor's bundled toolchain to
keep them in step, which means the archive has to be rebuilt after an editor upgrade that changes
Emscripten. `Dissonance4WebBuildCheck` catches a missing archive but cannot tell a stale one from
a current one.

### Threading

None of the browser audio path uses threads, because a WebGL player has none. Encoding and
decoding happen on the main thread, which at 50 frames a second of Opus is a small cost but is not
free the way it is when Dissonance has a thread to put it on. A page that is already dropping
frames will hear it.

If a future Unity Web build offers usable managed threads, Dissonance's own pipeline becomes
possible again and patch 2 could route to it - but the native library would still be missing, so
`WebPreprocessingPipeline` would remain the useful path.
