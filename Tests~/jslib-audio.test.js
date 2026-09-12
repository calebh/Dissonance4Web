// Tests for the browser half of Dissonance 4 Web.
//
// Runs the real Dissonance4WebAudio.jslib, and the real AudioWorklet processors
// out of its worklet source string, against a stub Web Audio API. What is being
// checked is the part that is easy to get subtly wrong and hard to notice in a
// game: the two ring buffers, and the queue accounting that spans the main thread
// and the audio thread without a shared clock.
//
//   node Tests~/jslib-audio.test.js
//
// See Tests~/README.md.

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");

const RENDER_QUANTUM = 128;

// ---------------------------------------------------------------------------
// test harness
// ---------------------------------------------------------------------------

let passed = 0;
const failures = [];

function test(name, body) {
    try {
        body();
        passed++;
        console.log("  ok   " + name);
    } catch (error) {
        failures.push({ name: name, error: error });
        console.log("  FAIL " + name + "\n         " + error.message);
    }
}

function assert(condition, message) {
    if (!condition) throw new Error(message || "assertion failed");
}

function assertEqual(actual, expected, message) {
    if (actual !== expected) {
        throw new Error((message ? message + ": " : "") + "expected " + expected + ", got " + actual);
    }
}

function assertClose(actual, expected, tolerance, message) {
    if (Math.abs(actual - expected) > tolerance) {
        throw new Error((message ? message + ": " : "") + "expected " + expected + " +/- " + tolerance + ", got " + actual);
    }
}

/// Lets the promise chains inside the plugin run.
function settle() {
    return new Promise(function (resolve) { setImmediate(resolve); });
}

function ramp(count, start) {
    const array = new Float32Array(count);
    for (let i = 0; i < count; i++) array[i] = (start || 0) + i;
    return array;
}

function readString(plugin, length) {
    return Buffer.from(plugin.sandbox.HEAPU8.subarray(0, length)).toString("utf8");
}

// ---------------------------------------------------------------------------
// stub browser
// ---------------------------------------------------------------------------

/// Deferred port messages. postMessage is asynchronous in a browser, and keeping
/// it asynchronous here is the point: it is what the queue accounting has to cope
/// with. The test decides when delivery happens.
const pending = [];

function flushMessages() {
    while (pending.length > 0) {
        const next = pending.shift();
        const handler = next.port.onmessage;
        if (handler) handler({ data: next.data });
    }
}

/// A connected pair of MessagePorts, as a MessageChannel would give.
function createPortPair() {
    const a = { onmessage: null };
    const b = { onmessage: null };

    function deliverTo(target) {
        return function (data) {
            // Structured clone semantics: a typed array arrives as a copy. The
            // capture processor relies on that, because it reuses one chunk
            // buffer for every message it sends.
            const isTypedArray = data && typeof data.length === "number" && data.buffer instanceof ArrayBuffer;
            pending.push({ port: target, data: isTypedArray ? data.slice() : data });
        };
    }

    a.postMessage = deliverTo(b);
    b.postMessage = deliverTo(a);

    return { node: a, processor: b };
}

/// Evaluates the worklet source and hands back its registered processors.
///
/// The real AudioWorklet gives a processor its port before the subclass
/// constructor runs, which is why the processors can set port.onmessage in their
/// own constructors. The stub base class does the same, reading a port parked by
/// the node stub immediately beforehand.
function loadWorklet(source) {
    const registry = {};
    const state = { port: null };

    const context = {
        Math: Math,
        Float32Array: Float32Array,
        console: console,
        AudioWorkletProcessor: function AudioWorkletProcessor() {
            this.port = state.port;
        },
        registerProcessor: function (name, ctor) {
            registry[name] = ctor;
        }
    };

    vm.createContext(context);
    vm.runInContext(source, context, { filename: "worklet.js" });

    return {
        registry: registry,
        create: function (name, options, port) {
            const Processor = registry[name];
            if (!Processor) throw new Error("the worklet registered no processor called " + name);

            state.port = port;
            try {
                return new Processor(options);
            } finally {
                state.port = null;
            }
        }
    };
}

function makeStream() {
    const track = {
        enabled: true,
        listeners: {},
        stop: function () { },
        addEventListener: function (name, handler) {
            track.listeners[name] = handler;
        },
        /// Fires the "ended" event a browser raises when the device goes away.
        end: function () {
            if (track.listeners.ended) track.listeners.ended();
        }
    };

    return {
        track: track,
        getTracks: function () { return [track]; },
        getAudioTracks: function () { return [track]; }
    };
}

function makeContext(sampleRate) {
    return {
        sampleRate: sampleRate,
        state: "running",
        currentTime: 0,
        baseLatency: 0.01,
        destination: {},
        audioWorklet: {
            addModule: function () { return Promise.resolve(); }
        },
        createGain: function () {
            return {
                gain: { value: 1, setTargetAtTime: function (v) { this.value = v; } },
                connect: function () { },
                disconnect: function () { }
            };
        },
        createStereoPanner: function () {
            return {
                pan: { value: 0, setTargetAtTime: function (v) { this.value = v; } },
                connect: function () { },
                disconnect: function () { }
            };
        },
        createMediaStreamSource: function () {
            return { connect: function () { }, disconnect: function () { } };
        }
    };
}

/// Loads the jslib into a sandbox and returns its exports plus the handles the
/// tests need to drive the audio thread by hand.
function loadPlugin(options) {
    options = options || {};

    const sampleRate = options.sampleRate || 48000;
    const heap = new ArrayBuffer(4 * 1024 * 1024);

    const jslibPath = path.join(__dirname, "..", "Runtime", "Plugins", "WebGL", "Dissonance4WebAudio.jslib");
    const source = fs.readFileSync(jslibPath, "utf8");

    const audioContext = makeContext(sampleRate);
    const library = { library: {} };
    const processors = { capture: null, playback: [] };

    let worklet = null;

    const sandbox = {
        console: console,
        Math: Math,
        Promise: Promise,
        setTimeout: setTimeout,
        Float32Array: Float32Array,
        Uint8Array: Uint8Array,
        ArrayBuffer: ArrayBuffer,
        TextEncoder: TextEncoder,
        String: String,
        Object: Object,
        Error: Error,

        HEAPF32: new Float32Array(heap),
        HEAPU8: new Uint8Array(heap),

        LibraryManager: library,
        mergeInto: function (target, extra) {
            for (const key in extra) target[key] = extra[key];
        },
        autoAddDeps: function () { },
        UTF8ToString: function (pointer) {
            if (!pointer) return "";

            let end = pointer;
            while (sandbox.HEAPU8[end] !== 0) end++;

            return Buffer.from(sandbox.HEAPU8.subarray(pointer, end)).toString("utf8");
        },

        window: {},
        document: { addEventListener: function () { } },
        WEBAudio: { audioContext: audioContext },

        URL: {
            createObjectURL: function (blob) {
                worklet = loadWorklet(blob.parts.join(""));
                return "blob:worklet";
            },
            revokeObjectURL: function () { }
        },
        Blob: function (parts) {
            this.parts = parts;
        },

        navigator: {
            mediaDevices: {
                getUserMedia: options.getUserMedia || function () {
                    return Promise.resolve(makeStream());
                },
                enumerateDevices: function () {
                    return Promise.resolve(options.devices || []);
                },
                addEventListener: function () { }
            }
        }
    };

    sandbox.AudioWorkletNode = function (context, name, nodeOptions) {
        assert(worklet, "a node was created before the worklet module loaded");

        const ports = createPortPair();
        const processor = worklet.create(name, nodeOptions, ports.processor);

        this.port = ports.node;
        this.connect = function () { };
        this.disconnect = function () { };
        this.processor = processor;

        if (name === "d4w-capture") processors.capture = processor;
        if (name === "d4w-playback") processors.playback.push(processor);
    };

    vm.createContext(sandbox);
    vm.runInContext(source, sandbox, { filename: "Dissonance4WebAudio.jslib" });

    // Emscripten turns a "$D4W" library member into a module scope variable named
    // D4W, which is how the exported functions reach the shared state. Nothing
    // does that here, so do it by hand.
    sandbox.D4W = library.library.$D4W;

    return {
        api: library.library,
        state: library.library.$D4W,
        sandbox: sandbox,
        audioContext: audioContext,
        processors: processors,
        flush: flushMessages,

        /// Runs one render quantum on a playback processor and returns its output.
        renderPlayback: function (processor, frames) {
            frames = frames || RENDER_QUANTUM;

            const output = [new Float32Array(frames)];
            processor.process([], [output], {});
            audioContext.currentTime += frames / sampleRate;

            return output[0];
        },

        /// Feeds one quantum of microphone input through the capture processor.
        renderCapture: function (processor, samples) {
            processor.process([[samples]], [[new Float32Array(samples.length)]], {});
        }
    };
}

// ---------------------------------------------------------------------------

async function playbackTests() {
    console.log("\nplayback");

    const plugin = loadPlugin();

    test("output init reports not ready while the worklet module loads", function () {
        assertEqual(plugin.api.D4W_OutInit(), 0);
        assertEqual(plugin.api.D4W_OutCreate(), 0, "a channel cannot be created yet");
    });

    await settle();

    test("output reports the context sample rate once the module is ready", function () {
        assertEqual(plugin.api.D4W_OutInit(), 1);
        assertEqual(plugin.api.D4W_OutSampleRate(), 48000);
        assertEqual(plugin.api.D4W_OutIsRunning(), 1);
    });

    const handle = plugin.api.D4W_OutCreate();

    test("creating a channel returns a handle and instantiates a processor", function () {
        assert(handle > 0, "expected a non zero handle");
        assertEqual(plugin.processors.playback.length, 1);
        assertEqual(plugin.api.D4W_OutQueued(handle), 0);
    });

    test("written samples count as queued before the worklet has seen them", function () {
        plugin.sandbox.HEAPF32.set(ramp(480, 1), 0);

        assertEqual(plugin.api.D4W_OutWrite(handle, 0, 480), 480);

        // Nothing delivered and nothing rendered, so all 480 are in flight. This
        // is the case a level-only estimate would get wrong.
        assertEqual(plugin.api.D4W_OutQueued(handle), 480);
    });

    test("samples come back out in the order they went in", function () {
        plugin.flush();

        const processor = plugin.processors.playback[0];

        const first = plugin.renderPlayback(processor, 128);
        for (let i = 0; i < 128; i++)
            assertEqual(first[i], i + 1, "sample " + i);

        const second = plugin.renderPlayback(processor, 128);
        for (let i = 0; i < 128; i++)
            assertEqual(second[i], 128 + i + 1, "sample " + (128 + i));
    });

    test("the queue estimate follows what the worklet has played", function () {
        plugin.flush();
        assertEqual(plugin.api.D4W_OutQueued(handle), 480 - 256);
    });

    test("an empty buffer produces silence, not stale audio", function () {
        const processor = plugin.processors.playback[0];

        // Drain the remaining 224 samples, then render past the end.
        plugin.renderPlayback(processor, 128);
        plugin.renderPlayback(processor, 128);
        plugin.flush();

        assertEqual(plugin.api.D4W_OutQueued(handle), 0);

        const out = plugin.renderPlayback(processor, 64);
        for (let i = 0; i < out.length; i++)
            assertEqual(out[i], 0, "sample " + i);
    });

    test("underruns are counted and reported once", function () {
        const processor = plugin.processors.playback[0];

        plugin.renderPlayback(processor, 128);
        plugin.renderPlayback(processor, 128);
        plugin.flush();

        const underruns = plugin.api.D4W_OutTakeUnderrunCount(handle);
        assert(underruns >= 1, "expected at least one underrun, got " + underruns);
        assertEqual(plugin.api.D4W_OutTakeUnderrunCount(handle), 0, "the same underruns should not be reported twice");
    });

    test("a write is refused once the channel buffer is full", function () {
        const capacity = plugin.state.channels[handle].capacity;
        const chunk = 4096;

        plugin.sandbox.HEAPF32.fill(0.5, 0, chunk);

        let accepted = 0;
        for (let i = 0; i < 100; i++) {
            const took = plugin.api.D4W_OutWrite(handle, 0, chunk);
            accepted += took;
            if (took < chunk) break;
        }

        assertEqual(accepted, capacity, "should accept exactly the channel capacity and no more");
        assertEqual(plugin.api.D4W_OutWrite(handle, 0, chunk), 0, "a full channel should accept nothing");
    });

    test("reset empties the channel, and a report from before it is ignored", function () {
        plugin.api.D4W_OutReset(handle);
        assertEqual(plugin.api.D4W_OutQueued(handle), 0);

        // Everything queued before the reset now arrives: the stale level report
        // must not resurrect a buffer that has been thrown away.
        plugin.flush();
        assertEqual(plugin.api.D4W_OutQueued(handle), 0);
    });

    test("a reset channel plays what is written after it, from the start", function () {
        plugin.sandbox.HEAPF32.set(ramp(256, 100), 0);
        assertEqual(plugin.api.D4W_OutWrite(handle, 0, 256), 256);

        plugin.flush();

        const out = plugin.renderPlayback(plugin.processors.playback[0], 128);
        for (let i = 0; i < 128; i++)
            assertEqual(out[i], 100 + i, "sample " + i);
    });

    test("gain and pan reach the channel nodes", function () {
        plugin.api.D4W_OutSetGain(handle, 0.25, -0.5);

        const channel = plugin.state.channels[handle];
        assertClose(channel.gain.gain.value, 0.25, 0.0001, "gain");
        assertClose(channel.panner.pan.value, -0.5, 0.0001, "pan");
    });

    test("two channels keep their audio apart", function () {
        const a = plugin.api.D4W_OutCreate();
        const b = plugin.api.D4W_OutCreate();

        plugin.sandbox.HEAPF32.set(ramp(128, 1), 0);
        plugin.api.D4W_OutWrite(a, 0, 128);

        plugin.sandbox.HEAPF32.set(ramp(128, 1000), 0);
        plugin.api.D4W_OutWrite(b, 0, 128);

        plugin.flush();

        const all = plugin.processors.playback;
        const outA = plugin.renderPlayback(all[all.length - 2], 128);
        const outB = plugin.renderPlayback(all[all.length - 1], 128);

        assertEqual(outA[0], 1, "channel a");
        assertEqual(outB[0], 1000, "channel b");
    });

    test("destroying a channel forgets it, and its handle goes inert", function () {
        plugin.api.D4W_OutDestroy(handle);

        assertEqual(plugin.state.channels[handle], undefined);
        assertEqual(plugin.api.D4W_OutQueued(handle), 0);
        assertEqual(plugin.api.D4W_OutWrite(handle, 0, 16), 0);
        assertEqual(plugin.api.D4W_OutTakeUnderrunCount(handle), 0);
    });
}

async function captureTests() {
    console.log("\ncapture");

    const plugin = loadPlugin();
    plugin.api.D4W_OutInit();
    await settle();

    test("capture is supported when getUserMedia and AudioWorklet are present", function () {
        assertEqual(plugin.api.D4W_MicSupported(), 1);
    });

    plugin.api.D4W_MicStart(0, 1, 1, 1);

    test("capture reports Starting while the browser decides", function () {
        assertEqual(plugin.api.D4W_MicState(), 1);
    });

    await settle();
    await settle();

    test("capture reaches Running once the stream arrives", function () {
        assertEqual(plugin.api.D4W_MicState(), 2);
        assertEqual(plugin.api.D4W_MicSampleRate(), 48000);
        assert(plugin.processors.capture, "expected a capture processor");
    });

    test("latency is reported as the context latency plus one batch", function () {
        // 10ms of context latency, plus 1024 samples at 48kHz.
        assertEqual(plugin.api.D4W_MicLatencyMs(), 31);
    });

    test("nothing is available before any audio has been captured", function () {
        assertEqual(plugin.api.D4W_MicAvailable(), 0);
        assertEqual(plugin.api.D4W_MicRead(0, 1024), 0);
    });

    test("a partial batch is not delivered early", function () {
        const processor = plugin.processors.capture;

        // The worklet batches 1024 samples; four quanta is half of that.
        for (let i = 0; i < 4; i++)
            plugin.renderCapture(processor, ramp(RENDER_QUANTUM, i * RENDER_QUANTUM));

        plugin.flush();
        assertEqual(plugin.api.D4W_MicAvailable(), 0);
    });

    test("a full batch arrives intact and in order", function () {
        const processor = plugin.processors.capture;

        for (let i = 4; i < 8; i++)
            plugin.renderCapture(processor, ramp(RENDER_QUANTUM, i * RENDER_QUANTUM));

        plugin.flush();
        assertEqual(plugin.api.D4W_MicAvailable(), 1024);
        assertEqual(plugin.api.D4W_MicRead(0, 1024), 1024);

        for (let i = 0; i < 1024; i++)
            assertEqual(plugin.sandbox.HEAPF32[i], i, "sample " + i);

        assertEqual(plugin.api.D4W_MicAvailable(), 0);
    });

    test("a short read leaves the rest behind, in order", function () {
        const processor = plugin.processors.capture;

        for (let i = 0; i < 8; i++)
            plugin.renderCapture(processor, ramp(RENDER_QUANTUM, 500 + i * RENDER_QUANTUM));

        plugin.flush();

        assertEqual(plugin.api.D4W_MicRead(0, 100), 100);
        for (let i = 0; i < 100; i++)
            assertEqual(plugin.sandbox.HEAPF32[i], 500 + i, "sample " + i);

        assertEqual(plugin.api.D4W_MicAvailable(), 924);
        assertEqual(plugin.api.D4W_MicRead(0, 924), 924);

        for (let i = 0; i < 924; i++)
            assertEqual(plugin.sandbox.HEAPF32[i], 600 + i, "sample " + i);
    });

    test("a read that wraps the end of the ring buffer stays in order", function () {
        const processor = plugin.processors.capture;
        const ringLength = plugin.state.micRing.length;

        // Park the heads near the end of the ring so the next batch has to wrap.
        plugin.api.D4W_MicFlush();
        plugin.state.micRead = ringLength - 300;
        plugin.state.micWrite = ringLength - 300;

        for (let i = 0; i < 8; i++)
            plugin.renderCapture(processor, ramp(RENDER_QUANTUM, 7000 + i * RENDER_QUANTUM));

        plugin.flush();

        assert(plugin.state.micWrite < plugin.state.micRead, "the ring should have wrapped");
        assertEqual(plugin.api.D4W_MicAvailable(), 1024);
        assertEqual(plugin.api.D4W_MicRead(0, 1024), 1024);

        for (let i = 0; i < 1024; i++)
            assertEqual(plugin.sandbox.HEAPF32[i], 7000 + i, "sample " + i);
    });

    test("overflow keeps the newest audio and is reported once", function () {
        const processor = plugin.processors.capture;
        const ringLength = plugin.state.micRing.length;

        plugin.api.D4W_MicFlush();
        plugin.api.D4W_MicTakeOverflowCount();

        // Two rings' worth with nothing reading.
        const batches = Math.ceil((ringLength * 2) / 1024);
        for (let batch = 0; batch < batches; batch++) {
            for (let i = 0; i < 8; i++)
                plugin.renderCapture(processor, ramp(RENDER_QUANTUM, batch * 1024 + i * RENDER_QUANTUM));
        }

        plugin.flush();

        const overflow = plugin.api.D4W_MicTakeOverflowCount();
        assert(overflow > 0, "expected an overflow to be reported");
        assertEqual(plugin.api.D4W_MicTakeOverflowCount(), 0, "the same overflow should not be reported twice");
        assertEqual(plugin.api.D4W_MicAvailable(), ringLength, "the ring should be exactly full");

        // The newest audio is what survives, so the last sample written is the
        // last sample readable.
        const total = batches * 1024;
        assertEqual(plugin.api.D4W_MicRead(0, ringLength), ringLength);
        assertEqual(plugin.sandbox.HEAPF32[ringLength - 1], total - 1, "the newest sample should be kept");
    });

    test("flush discards captured audio", function () {
        plugin.api.D4W_MicFlush();
        assertEqual(plugin.api.D4W_MicAvailable(), 0);
    });

    test("stopping capture returns to Idle", function () {
        plugin.api.D4W_MicStop();
        assertEqual(plugin.api.D4W_MicState(), 0);
        assertEqual(plugin.api.D4W_MicSampleRate(), 0);
    });
}

async function deviceLossTests() {
    console.log("\ndevice loss");

    const stream = makeStream();
    const plugin = loadPlugin({
        getUserMedia: function () { return Promise.resolve(stream); }
    });

    plugin.api.D4W_OutInit();
    await settle();

    plugin.api.D4W_MicStart(0, 1, 1, 1);
    await settle();
    await settle();

    test("capture is running before the device goes away", function () {
        assertEqual(plugin.api.D4W_MicState(), 2);
    });

    test("an ended track is reported as a failure rather than silence", function () {
        stream.track.end();

        assertEqual(plugin.api.D4W_MicState(), 3);

        const written = plugin.api.D4W_MicError(0, 256);
        const text = readString(plugin, written);
        assert(text.indexOf("disconnected") >= 0, "unexpected message: " + text);
    });
}

async function captureFailureTests() {
    console.log("\ncapture failures");

    const plugin = loadPlugin({
        getUserMedia: function () {
            const error = new Error("Permission denied");
            error.name = "NotAllowedError";
            return Promise.reject(error);
        }
    });

    plugin.api.D4W_OutInit();
    await settle();

    plugin.api.D4W_MicStart(0, 1, 1, 1);
    await settle();
    await settle();

    test("a refused permission prompt ends in Failed with a readable reason", function () {
        assertEqual(plugin.api.D4W_MicState(), 3);

        const written = plugin.api.D4W_MicError(0, 256);
        assert(written > 0, "expected an error message");

        const text = readString(plugin, written);
        assert(text.indexOf("refused") >= 0, "unexpected message: " + text);
    });

    test("reading from a failed microphone yields nothing rather than throwing", function () {
        assertEqual(plugin.api.D4W_MicAvailable(), 0);
        assertEqual(plugin.api.D4W_MicRead(0, 256), 0);
    });
}

async function deviceTests() {
    console.log("\ndevices");

    const plugin = loadPlugin({
        devices: [
            { kind: "audioinput", deviceId: "aaa", label: "Headset" },
            { kind: "audiooutput", deviceId: "out", label: "Speakers" },
            { kind: "audioinput", deviceId: "bbb", label: "" }
        ]
    });

    plugin.api.D4W_OutInit();
    await settle();
    await settle();

    test("only input devices are listed", function () {
        assertEqual(plugin.api.D4W_MicDeviceCount(), 2);
    });

    test("a name is written without a terminator, as the C# side expects", function () {
        const written = plugin.api.D4W_MicDeviceName(0, 0, 256);
        assertEqual(written, "Headset".length);
        assertEqual(readString(plugin, written), "Headset");
    });

    test("a device with no label still gets a usable name", function () {
        const written = plugin.api.D4W_MicDeviceName(1, 0, 256);
        assertEqual(readString(plugin, written), "Microphone 2");
    });

    test("a name longer than the buffer is truncated rather than overflowing", function () {
        plugin.state.devices = [{ id: "x", label: "A".repeat(500) }];

        assertEqual(plugin.api.D4W_MicDeviceName(0, 0, 16), 16);
        assertEqual(plugin.sandbox.HEAPU8[16], 0, "nothing should be written past the capacity");
    });

    test("an out of range device index writes nothing", function () {
        assertEqual(plugin.api.D4W_MicDeviceName(99, 0, 256), 0);
        assertEqual(plugin.api.D4W_MicDeviceName(-1, 0, 256), 0);
    });
}

async function unsupportedBrowserTests() {
    console.log("\nunsupported browser");

    const plugin = loadPlugin();
    delete plugin.sandbox.AudioWorkletNode;

    test("capture reports unsupported without AudioWorklet", function () {
        assertEqual(plugin.api.D4W_MicSupported(), 0);
    });

    test("starting capture without AudioWorklet fails rather than hanging", function () {
        plugin.api.D4W_MicStart(0, 1, 1, 1);
        assertEqual(plugin.api.D4W_MicState(), 3);

        const written = plugin.api.D4W_MicError(0, 256);
        assert(written > 0, "expected an error message");
    });
}

async function main() {
    await playbackTests();
    await captureTests();
    await captureFailureTests();
    await deviceLossTests();
    await deviceTests();
    await unsupportedBrowserTests();

    console.log("");

    if (failures.length > 0) {
        console.log(failures.length + " failed, " + passed + " passed");
        process.exit(1);
    }

    console.log(passed + " passed");
}

main().catch(function (error) {
    console.error(error);
    process.exit(1);
});
