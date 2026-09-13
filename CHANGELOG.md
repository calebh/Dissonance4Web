# Changelog

## 0.2.0

- **`MicrophoneAccessRequest`**: when a browser player is asked for microphone access, set by
  `Microphone Access` on `DissonanceWebAudio`. `OnStart` (the default, and the previous
  behaviour), `OnFirstTransmission` (the first time the player tries to send voice, so players who
  only listen never see the prompt), or `Manual`. Replaces `RequestMicrophoneAccessOnStart`, which
  is not migrated: a scene that had it on keeps the same behaviour, but one that had it off now
  asks `OnStart` and should be set to `Manual`.
- `Manual` now really waits for `RequestMicrophoneAccess()`. Turning off
  `RequestMicrophoneAccessOnStart` used to defer the prompt only until the player joined a voice
  session, because starting capture requested access regardless.
- Joining a voice session before the microphone is open no longer logs Dissonance's "local voice
  transmission will be disabled" warning, or forces a capture reset once access is granted.
  Capture starts straight away with the AudioContext's format and restarts once when the
  microphone opens.
- A microphone that fails to open is reported as "did not open" rather than "refused", since the
  same state covers a missing or unplugged device.

## 0.1.0

First release.

- Browser voice capture through getUserMedia and an AudioWorklet, replacing Unity's
  `Microphone` class, which records through MediaRecorder and cannot hand over samples while
  recording.
- A single threaded preprocessing pipeline for the browser, leaving echo cancellation, noise
  suppression and gain control to the browser and resampling with Dissonance's own resampler so
  frames match every other platform.
- Browser voice playback through an AudioWorklet, replacing the `OnAudioFilterRead` path that a
  Unity Web player never raises. Positional voice approximated with Web Audio gain and panner
  nodes.
- Opus as WebAssembly, built from upstream libopus by the editor's own Emscripten, so a browser
  and a desktop player exchange Opus frames with no transcoding anywhere in the path.
- A Dissonance integration for Mirror, succeeding `MirrorIgnorance`. The two channel ids are
  settings on the component rather than constants, because Mirror leaves channel ids to the
  project; they default to Mirror's own two, which need no transport configuration. A startup
  check reports a transport that will not deliver them the way Dissonance needs.
- An editor patcher for the four edits Dissonance's own source needs, with check and revert, and a
  WebGL build check that fails early when a required patch or `libopus.a` is missing. Both live in
  an assembly that references nothing, so they work in a project where the unpatched package does
  not compile - which is every project until the patch is applied.
- A prompt on first load offering to apply the patches, so a fresh install does not present a wall
  of "inaccessible due to its protection level" errors with no explanation.
