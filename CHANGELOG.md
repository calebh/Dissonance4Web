# Changelog

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
- A Dissonance integration for Mirror, succeeding `MirrorIgnorance`, with a startup check that the
  active transport delivers Dissonance's two channels the way Dissonance needs.
- An editor patcher for the four edits Dissonance's own source needs, with check and revert, and a
  WebGL build check that fails early when a required patch or `libopus.a` is missing.
