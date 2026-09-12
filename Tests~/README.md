# Tests

```bash
node Tests~/jslib-audio.test.js
```

Any recent node will do. The editor ships one, if that is convenient:

```bash
"<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/node/node" Tests~/jslib-audio.test.js
```

## What is covered

`jslib-audio.test.js` runs the real `Dissonance4WebAudio.jslib`, and the real AudioWorklet
processors out of its worklet source string, against a stub Web Audio API. The processors are
instantiated the way a browser instantiates them - with a live pair of message ports, and with
`postMessage` deferred rather than synchronous - so what is under test is the actual code that
ships, not a re-implementation of it.

It covers the parts that are easy to get subtly wrong and hard to notice in a game:

**The playback queue estimate.** There is no shared memory between the main thread and the audio
thread, so the main thread cannot read the worklet's buffer level directly. It computes
`level + (posted - received)` from the worklet's periodic report, which has to stay exact no matter
how stale that report is. The tests check it with samples in flight, after rendering, across a
reset, and at the capacity limit.

**Both ring buffers.** Order preserved, wrap-around handled, partial reads leaving the remainder in
place, overflow keeping the newest audio and being reported exactly once, and silence rather than
stale audio on an underrun.

**Channel isolation.** Two speakers' audio not bleeding into each other.

**The capture state machine.** Idle, Starting while the browser decides, Running, and Failed with
the browser's own reason - including a refused permission prompt and a browser with no
`AudioWorklet` at all, both of which have to end in a reported failure rather than a hang.

**The string protocol.** Device names are written to the heap without a null terminator, because
the C# side decodes exactly the returned byte count; a name longer than the buffer is truncated
rather than overflowing it.

## What is not covered

The C# side has no test assembly. Its behaviour is checked by compiling it - against a patched
Dissonance core, in both the editor and the WebGL player configuration, which is what catches the
conditional compilation - and then by running it.

Nothing here tests the round trip through a real browser, a real Opus encoder or a real network.
That needs a build; `Documentation~/Setup.md` has the console output to expect from one.
