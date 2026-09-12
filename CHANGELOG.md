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
