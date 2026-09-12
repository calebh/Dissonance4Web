// Browser half of Dissonance 4 Web: microphone capture and voice playback.
//
// Capture:  getUserMedia -> MediaStreamSource -> AudioWorklet -> ring buffer
//           that C# drains once a frame.
// Playback: one AudioWorklet per speaker, fed from C# once a frame, through a
//           gain and a stereo panner to the output.
//
// Everything crosses into C# by polling, for the same reason MirrorWTransport
// polls: audio callbacks fire on the browser's audio thread at times Unity knows
// nothing about, and Dissonance wants its samples handed over from Update.
//
// Written in ES5 style (var, function, no arrow functions, no template literals)
// because Emscripten embeds this verbatim and may run it through a minifier
// targeting ES5. The AudioWorklet code has to use `class`, so it lives in a
// string, which no minifier parses, and is loaded from a blob URL.

var Dissonance4WebAudioLibrary = {

    $D4W: {
        // ---- capture state ----
        MIC_IDLE: 0,
        MIC_STARTING: 1,
        MIC_RUNNING: 2,
        MIC_FAILED: 3,

        /// Samples the capture worklet batches up before posting. ~21ms at 48kHz.
        CAPTURE_CHUNK: 1024,

        micState: 0,
        micStream: null,
        micSource: null,
        micNode: null,
        micSink: null,
        micError: "",
        micRate: 0,
        micGeneration: 0,

        micRing: null,
        micRead: 0,
        micWrite: 0,
        micCount: 0,
        micOverflow: 0,

        devices: [],
        devicesWatched: false,

        // ---- playback state ----
        channels: {},
        nextHandle: 1,

        /// Per channel buffer ceiling, in seconds. Well above any sane jitter
        /// buffer; it exists so a stalled reader cannot grow the buffer without
        /// limit and turn a hitch into permanent latency.
        OUTPUT_CAPACITY_SECONDS: 0.5,

        // ---- shared audio graph ----
        context: null,
        workletState: 0, // 0 none, 1 loading, 2 ready, 3 failed
        resumeHooked: false,

        // -----------------------------------------------------------------
        // the worklet
        // -----------------------------------------------------------------

        WorkletSource: [
            "class D4WCaptureProcessor extends AudioWorkletProcessor {",
            "  constructor(options) {",
            "    super();",
            "    var size = (options && options.processorOptions && options.processorOptions.chunk) || 1024;",
            "    this.chunk = new Float32Array(size);",
            "    this.filled = 0;",
            "  }",
            "  process(inputs) {",
            "    var input = inputs[0];",
            "    if (!input || input.length === 0) return true;",
            "    var channel = input[0];",
            "    if (!channel) return true;",
            "    var offset = 0;",
            "    while (offset < channel.length) {",
            "      var take = Math.min(this.chunk.length - this.filled, channel.length - offset);",
            "      this.chunk.set(channel.subarray(offset, offset + take), this.filled);",
            "      this.filled += take;",
            "      offset += take;",
            "      if (this.filled === this.chunk.length) {",
            // postMessage structured-clones the array, so reusing this.chunk is
            // safe and capture allocates nothing per quantum.
            "        this.port.postMessage(this.chunk);",
            "        this.filled = 0;",
            "      }",
            "    }",
            "    return true;",
            "  }",
            "}",
            "",
            "class D4WPlaybackProcessor extends AudioWorkletProcessor {",
            "  constructor(options) {",
            "    super();",
            "    var capacity = (options && options.processorOptions && options.processorOptions.capacity) || 24000;",
            "    this.buffer = new Float32Array(capacity);",
            "    this.read = 0;",
            "    this.write = 0;",
            "    this.count = 0;",
            "    this.received = 0;",
            "    this.underruns = 0;",
            "    this.sinceReport = 0;",
            "    this.reportEvery = 256;",
            "    this.epoch = 0;",
            "    var self = this;",
            "    this.port.onmessage = function (event) {",
            "      var data = event.data;",
            // A reset carries an epoch, echoed back in every later report, so the
            // main thread can tell a report from before the reset from one after.
            "      if (data && data.reset !== undefined) {",
            "        self.read = 0;",
            "        self.write = 0;",
            "        self.count = 0;",
            "        self.received = 0;",
            "        self.epoch = data.reset;",
            "        return;",
            "      }",
            "      self.push(data);",
            "    };",
            "  }",
            "  push(samples) {",
            "    if (!samples || !samples.length) return;",
            "    var capacity = this.buffer.length;",
            "    for (var i = 0; i < samples.length; i++) {",
            // C# checks for room before posting, so a full buffer here means the
            // two disagreed. Overwriting the oldest sample keeps latency bounded.
            "      this.buffer[this.write] = samples[i];",
            "      this.write = (this.write + 1) % capacity;",
            "      if (this.count < capacity) this.count++;",
            "      else this.read = (this.read + 1) % capacity;",
            "    }",
            "    this.received += samples.length;",
            "  }",
            "  process(inputs, outputs) {",
            "    var out = outputs[0];",
            "    if (!out || out.length === 0) return true;",
            "    var target = out[0];",
            "    var frames = target.length;",
            "    var capacity = this.buffer.length;",
            "    if (this.count === 0) this.underruns++;",
            "    for (var i = 0; i < frames; i++) {",
            "      var value = 0;",
            "      if (this.count > 0) {",
            "        value = this.buffer[this.read];",
            "        this.read = (this.read + 1) % capacity;",
            "        this.count--;",
            "      }",
            "      for (var c = 0; c < out.length; c++) out[c][i] = value;",
            "    }",
            "    this.sinceReport += frames;",
            "    if (this.sinceReport >= this.reportEvery) {",
            "      this.sinceReport = 0;",
            "      this.port.postMessage({ level: this.count, received: this.received, underruns: this.underruns, epoch: this.epoch });",
            "    }",
            "    return true;",
            "  }",
            "}",
            "",
            "registerProcessor('d4w-capture', D4WCaptureProcessor);",
            "registerProcessor('d4w-playback', D4WPlaybackProcessor);"
        ].join("\n"),

        // -----------------------------------------------------------------
        // helpers
        // -----------------------------------------------------------------

        Log: function (message) {
            console.log("[Dissonance4Web] " + message);
        },

        Warn: function (message) {
            console.warn("[Dissonance4Web] " + message);
        },

        Error: function (message) {
            console.error("[Dissonance4Web] " + message);
        },

        Supported: function () {
            return typeof AudioWorkletNode !== "undefined"
                && typeof navigator !== "undefined"
                && !!navigator.mediaDevices
                && !!navigator.mediaDevices.getUserMedia;
        },

        /// Unity's own AudioContext if there is one, so game audio and voice share
        /// a clock, an output device and the browser's autoplay unlock. A context
        /// of our own is the fallback for a build with Unity audio disabled.
        GetContext: function () {
            if (D4W.context) return D4W.context;

            if (typeof WEBAudio !== "undefined" && WEBAudio && WEBAudio.audioContext) {
                D4W.context = WEBAudio.audioContext;
                return D4W.context;
            }

            var Ctor = window.AudioContext || window.webkitAudioContext;
            if (!Ctor) {
                D4W.Error("this browser has no Web Audio API, so voice chat cannot run");
                return null;
            }

            try {
                D4W.context = new Ctor();
            } catch (e) {
                D4W.Error("could not create an AudioContext: " + e);
                return null;
            }

            return D4W.context;
        },

        /// Browsers keep an AudioContext suspended until the page has seen a real
        /// user gesture. Unity unlocks its own context this way too; when we are
        /// borrowing that context this is redundant but harmless.
        HookResume: function () {
            if (D4W.resumeHooked) return;
            D4W.resumeHooked = true;

            var resume = function () {
                var context = D4W.context;
                if (context && context.state === "suspended") {
                    context.resume().catch(function () { /* another gesture will come */ });
                }
            };

            var events = ["pointerdown", "touchend", "keydown"];
            for (var i = 0; i < events.length; i++)
                document.addEventListener(events[i], resume, true);
        },

        /// Loads the worklet module. Returns true once processors can be created.
        EnsureWorklet: function () {
            if (D4W.workletState === 2) return true;
            if (D4W.workletState === 1 || D4W.workletState === 3) return false;

            var context = D4W.GetContext();
            if (!context) {
                D4W.workletState = 3;
                return false;
            }

            if (!context.audioWorklet) {
                D4W.workletState = 3;
                D4W.Error(
                    "this browser does not expose AudioWorklet. It is only available in a secure context, so serve the page over " +
                    "https or from localhost."
                );
                return false;
            }

            D4W.workletState = 1;
            D4W.HookResume();

            // Warm the device list now, so a device picker shown a frame later has
            // something to show.
            D4W.RefreshDevices();

            var url;
            try {
                url = URL.createObjectURL(new Blob([D4W.WorkletSource], { type: "application/javascript" }));
            } catch (e) {
                D4W.workletState = 3;
                D4W.Error("could not build the audio worklet module: " + e);
                return false;
            }

            context.audioWorklet.addModule(url).then(function () {
                D4W.workletState = 2;
                URL.revokeObjectURL(url);
                D4W.Log("audio worklet ready at " + context.sampleRate + "Hz");
            }).catch(function (e) {
                D4W.workletState = 3;
                URL.revokeObjectURL(url);
                D4W.Error("could not load the audio worklet module: " + e);
            });

            return false;
        },

        WriteString: function (text, pointer, capacity) {
            if (!text || capacity <= 0) return 0;

            var bytes = new TextEncoder().encode(text);
            var length = Math.min(bytes.length, capacity);
            if (length > 0) HEAPU8.set(bytes.subarray(0, length), pointer);

            return length;
        },

        // -----------------------------------------------------------------
        // capture
        // -----------------------------------------------------------------

        RefreshDevices: function () {
            if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) return;

            if (!D4W.devicesWatched) {
                D4W.devicesWatched = true;
                if (navigator.mediaDevices.addEventListener) {
                    navigator.mediaDevices.addEventListener("devicechange", function () {
                        D4W.RefreshDevices();
                    });
                }
            }

            navigator.mediaDevices.enumerateDevices().then(function (list) {
                var found = [];
                for (var i = 0; i < list.length; i++) {
                    if (list[i].kind !== "audioinput") continue;

                    // Labels are empty until the user has granted access once, so
                    // fall back to something a device picker can still show.
                    found.push({
                        id: list[i].deviceId,
                        label: list[i].label || ("Microphone " + (found.length + 1))
                    });
                }
                D4W.devices = found;
            }).catch(function (e) {
                D4W.Warn("could not enumerate audio input devices: " + e);
            });
        },

        DeviceIdForLabel: function (label) {
            if (!label) return null;

            for (var i = 0; i < D4W.devices.length; i++) {
                if (D4W.devices[i].label === label)
                    return D4W.devices[i].id;
            }

            return null;
        },

        StopCapture: function () {
            // Bump the generation so a getUserMedia promise still in flight knows
            // its stream is no longer wanted.
            D4W.micGeneration++;

            if (D4W.micNode) {
                try { D4W.micNode.port.onmessage = null; } catch (e) { /* closing */ }
                try { D4W.micNode.disconnect(); } catch (e) { /* closing */ }
                D4W.micNode = null;
            }

            if (D4W.micSink) {
                try { D4W.micSink.disconnect(); } catch (e) { /* closing */ }
                D4W.micSink = null;
            }

            if (D4W.micSource) {
                try { D4W.micSource.disconnect(); } catch (e) { /* closing */ }
                D4W.micSource = null;
            }

            if (D4W.micStream) {
                var tracks = D4W.micStream.getTracks();
                for (var i = 0; i < tracks.length; i++) {
                    try { tracks[i].stop(); } catch (e) { /* closing */ }
                }
                D4W.micStream = null;
            }

            D4W.micRate = 0;
            D4W.micState = D4W.MIC_IDLE;
            D4W.FlushCapture();
        },

        FlushCapture: function () {
            D4W.micRead = 0;
            D4W.micWrite = 0;
            D4W.micCount = 0;
        },

        FailCapture: function (message) {
            D4W.micError = message;
            D4W.micState = D4W.MIC_FAILED;
            D4W.Error(message);
        },

        OnCaptureSamples: function (samples) {
            var ring = D4W.micRing;
            if (!ring) return;

            var capacity = ring.length;
            for (var i = 0; i < samples.length; i++) {
                ring[D4W.micWrite] = samples[i];
                D4W.micWrite = (D4W.micWrite + 1) % capacity;

                if (D4W.micCount < capacity) {
                    D4W.micCount++;
                } else {
                    // Nothing is reading. Keep the newest audio and count the loss;
                    // C# reports it so a stall shows up in the log as dropped
                    // samples rather than as unexplained gaps in someone's voice.
                    D4W.micRead = (D4W.micRead + 1) % capacity;
                    D4W.micOverflow++;
                }
            }
        },

        BuildCaptureGraph: function (stream, generation) {
            if (generation !== D4W.micGeneration) {
                // Capture was stopped or restarted while the browser was still
                // deciding. This stream belongs to nobody.
                var tracks = stream.getTracks();
                for (var i = 0; i < tracks.length; i++) {
                    try { tracks[i].stop(); } catch (e) { /* discarding */ }
                }
                return;
            }

            var context = D4W.GetContext();
            if (!context) {
                D4W.FailCapture("no AudioContext is available for capture");
                return;
            }

            try {
                D4W.micStream = stream;
                D4W.micSource = context.createMediaStreamSource(stream);

                D4W.micNode = new AudioWorkletNode(context, "d4w-capture", {
                    numberOfInputs: 1,
                    numberOfOutputs: 1,
                    outputChannelCount: [1],
                    channelCount: 1,
                    channelCountMode: "explicit",
                    channelInterpretation: "speakers",
                    processorOptions: { chunk: D4W.CAPTURE_CHUNK }
                });

                D4W.micNode.port.onmessage = function (event) {
                    D4W.OnCaptureSamples(event.data);
                };

                // A worklet is only guaranteed to be pulled while it has a path to
                // the destination, so the (silent) output goes there through a
                // muted gain rather than being left dangling.
                D4W.micSink = context.createGain();
                D4W.micSink.gain.value = 0;

                D4W.micSource.connect(D4W.micNode);
                D4W.micNode.connect(D4W.micSink);
                D4W.micSink.connect(context.destination);

                // One second of slack, which is far more than a frame hitch needs
                // and enough that a garbage collection pause loses nothing.
                D4W.micRing = new Float32Array(Math.max(4096, Math.ceil(context.sampleRate)));
                D4W.FlushCapture();

                // A track ends when the device goes away - an unplugged headset, or
                // a browser revoking access. Nothing else would notice: the graph
                // stays connected and simply delivers silence forever.
                var tracks = stream.getAudioTracks ? stream.getAudioTracks() : [];
                for (var t = 0; t < tracks.length; t++) {
                    tracks[t].addEventListener("ended", function () {
                        if (generation !== D4W.micGeneration) return;
                        D4W.FailCapture("the microphone was disconnected");
                    });
                }

                D4W.micRate = context.sampleRate;
                D4W.micError = "";
                D4W.micState = D4W.MIC_RUNNING;

                D4W.Log("microphone running at " + D4W.micRate + "Hz");

                // Labels are only readable now that access has been granted.
                D4W.RefreshDevices();
            } catch (e) {
                D4W.StopCapture();
                D4W.FailCapture("could not build the capture graph: " + e);
            }
        },

        // -----------------------------------------------------------------
        // playback
        // -----------------------------------------------------------------

        DestroyChannel: function (handle) {
            var channel = D4W.channels[handle];
            if (!channel) return;

            try { channel.node.port.onmessage = null; } catch (e) { /* closing */ }
            try { channel.node.disconnect(); } catch (e) { /* closing */ }
            try { channel.gain.disconnect(); } catch (e) { /* closing */ }
            try { channel.panner.disconnect(); } catch (e) { /* closing */ }

            delete D4W.channels[handle];
        },

        QueuedFor: function (channel) {
            // What the worklet last reported, plus everything posted since. The
            // worklet's running total of samples received is what makes the two
            // halves add up without a shared clock.
            var pending = channel.posted - channel.received;
            if (pending < 0) pending = 0;

            return channel.level + pending;
        }
    },

    // ---------------------------------------------------------------------
    // capture entry points
    // ---------------------------------------------------------------------

    D4W_MicSupported: function () {
        return D4W.Supported() ? 1 : 0;
    },

    D4W_MicStart: function (labelPointer, echoCancellation, noiseSuppression, autoGainControl) {
        if (!D4W.Supported()) {
            D4W.FailCapture("this browser does not support getUserMedia with AudioWorklet");
            return;
        }

        if (D4W.micState === D4W.MIC_STARTING || D4W.micState === D4W.MIC_RUNNING)
            return;

        // The worklet may still be loading here. That is fine: the permission
        // prompt takes longer than the module does, and the promise below waits
        // for it before building the graph.
        if (!D4W.EnsureWorklet() && D4W.workletState === 3) {
            D4W.FailCapture("the audio worklet module could not be loaded, so capture cannot start");
            return;
        }

        var label = labelPointer ? UTF8ToString(labelPointer) : "";
        var deviceId = D4W.DeviceIdForLabel(label);

        if (label && !deviceId)
            D4W.Warn("no audio input device is called '" + label + "'; using the default device");

        var audio = {
            echoCancellation: !!echoCancellation,
            noiseSuppression: !!noiseSuppression,
            autoGainControl: !!autoGainControl,
            channelCount: { ideal: 1 }
        };

        if (deviceId) audio.deviceId = { exact: deviceId };

        D4W.micGeneration++;
        var generation = D4W.micGeneration;

        D4W.micError = "";
        D4W.micState = D4W.MIC_STARTING;

        navigator.mediaDevices.getUserMedia({ audio: audio, video: false }).then(function (stream) {
            // The worklet may still have been loading when the prompt went up.
            if (!D4W.EnsureWorklet()) {
                var wait = function () {
                    if (D4W.workletState === 2) {
                        D4W.BuildCaptureGraph(stream, generation);
                    } else if (D4W.workletState === 3) {
                        D4W.FailCapture("the audio worklet module could not be loaded, so capture cannot start");
                    } else {
                        setTimeout(wait, 50);
                    }
                };
                wait();
                return;
            }

            D4W.BuildCaptureGraph(stream, generation);
        }).catch(function (e) {
            if (generation !== D4W.micGeneration) return;

            var name = e && e.name ? e.name : "Error";
            var reason = e && e.message ? e.message : String(e);

            if (name === "NotAllowedError")
                D4W.FailCapture("the user refused microphone access");
            else if (name === "NotFoundError")
                D4W.FailCapture("no microphone is connected");
            else
                D4W.FailCapture("could not open the microphone (" + name + "): " + reason);
        });
    },

    D4W_MicStop: function () {
        D4W.StopCapture();
    },

    D4W_MicState: function () {
        return D4W.micState;
    },

    D4W_MicSampleRate: function () {
        return D4W.micRate | 0;
    },

    D4W_MicLatencyMs: function () {
        if (D4W.micState !== D4W.MIC_RUNNING || !D4W.micRate) return 0;

        var context = D4W.context;
        var base = context && context.baseLatency ? context.baseLatency : 0;

        // The worklet batches a chunk before posting, so on average half of one
        // is waiting at any moment; the whole chunk is the worst case and the
        // number this feeds is a latency budget, not a measurement.
        var batching = D4W.CAPTURE_CHUNK / D4W.micRate;

        return Math.round((base + batching) * 1000);
    },

    D4W_MicAvailable: function () {
        return D4W.micCount | 0;
    },

    D4W_MicRead: function (destination, capacity) {
        if (!D4W.micRing || capacity <= 0) return 0;

        var count = Math.min(D4W.micCount, capacity);
        if (count <= 0) return 0;

        var ring = D4W.micRing;
        var ringCapacity = ring.length;
        var out = destination >> 2;

        // Two copies at most: up to the end of the ring, then the wrap.
        var first = Math.min(count, ringCapacity - D4W.micRead);
        HEAPF32.set(ring.subarray(D4W.micRead, D4W.micRead + first), out);

        if (count > first)
            HEAPF32.set(ring.subarray(0, count - first), out + first);

        D4W.micRead = (D4W.micRead + count) % ringCapacity;
        D4W.micCount -= count;

        return count;
    },

    D4W_MicTakeOverflowCount: function () {
        var overflow = D4W.micOverflow;
        D4W.micOverflow = 0;
        return overflow;
    },

    D4W_MicFlush: function () {
        D4W.FlushCapture();
    },

    D4W_MicDeviceCount: function () {
        // Kick off the first enumeration lazily, so a project that never shows a
        // device picker never asks the browser for the list.
        if (!D4W.devicesWatched) D4W.RefreshDevices();

        return D4W.devices.length;
    },

    D4W_MicDeviceName: function (index, destination, capacity) {
        if (index < 0 || index >= D4W.devices.length) return 0;

        return D4W.WriteString(D4W.devices[index].label, destination, capacity);
    },

    D4W_MicError: function (destination, capacity) {
        return D4W.WriteString(D4W.micError, destination, capacity);
    },

    // ---------------------------------------------------------------------
    // playback entry points
    // ---------------------------------------------------------------------

    D4W_OutInit: function () {
        if (!D4W.GetContext()) return 0;

        return D4W.EnsureWorklet() ? 1 : 0;
    },

    D4W_OutSampleRate: function () {
        var context = D4W.context;
        return context ? (context.sampleRate | 0) : 0;
    },

    D4W_OutIsRunning: function () {
        var context = D4W.context;
        return context && context.state === "running" ? 1 : 0;
    },

    D4W_OutResume: function () {
        var context = D4W.context;
        if (context && context.state === "suspended") {
            context.resume().catch(function (e) {
                D4W.Warn("could not resume the AudioContext; a user gesture is usually required: " + e);
            });
        }
    },

    D4W_OutCreate: function () {
        var context = D4W.GetContext();
        if (!context || !D4W.EnsureWorklet()) return 0;

        var capacity = Math.max(4096, Math.ceil(context.sampleRate * D4W.OUTPUT_CAPACITY_SECONDS));

        var channel = {
            node: null,
            gain: null,
            panner: null,
            capacity: capacity,
            posted: 0,
            received: 0,
            level: 0,
            epoch: 0,
            underruns: 0,
            reportedUnderruns: 0
        };

        try {
            channel.node = new AudioWorkletNode(context, "d4w-playback", {
                numberOfInputs: 0,
                numberOfOutputs: 1,
                outputChannelCount: [1],
                processorOptions: { capacity: capacity }
            });

            channel.gain = context.createGain();
            channel.panner = context.createStereoPanner
                ? context.createStereoPanner()
                : null;

            channel.node.connect(channel.gain);

            if (channel.panner) {
                channel.gain.connect(channel.panner);
                channel.panner.connect(context.destination);
            } else {
                // Very old Safari. Voice still plays, just without panning.
                channel.gain.connect(context.destination);
            }

            channel.node.port.onmessage = function (event) {
                var report = event.data;

                // A report from before the last reset describes a buffer that no
                // longer exists, and applying it would make the queue estimate
                // jump until the next report arrived.
                if (report.epoch !== channel.epoch) return;

                channel.level = report.level;
                channel.received = report.received;
                channel.underruns = report.underruns;
            };
        } catch (e) {
            D4W.Error("could not create a voice output channel: " + e);
            return 0;
        }

        var handle = D4W.nextHandle++;
        D4W.channels[handle] = channel;

        return handle;
    },

    D4W_OutDestroy: function (handle) {
        D4W.DestroyChannel(handle);
    },

    D4W_OutReset: function (handle) {
        var channel = D4W.channels[handle];
        if (!channel) return;

        // The worklet owns the buffered samples, so the only way to drop them is
        // to ask. Both sides clear their counters and move to a new epoch, so the
        // queue estimate stays consistent across the reset.
        channel.epoch++;

        try {
            channel.node.port.postMessage({ reset: channel.epoch });
        } catch (e) {
            /* the node is going away anyway */
        }

        channel.posted = 0;
        channel.received = 0;
        channel.level = 0;
    },

    D4W_OutQueued: function (handle) {
        var channel = D4W.channels[handle];
        if (!channel) return 0;

        return D4W.QueuedFor(channel) | 0;
    },

    D4W_OutWrite: function (handle, samples, count) {
        var channel = D4W.channels[handle];
        if (!channel || count <= 0) return 0;

        var room = channel.capacity - D4W.QueuedFor(channel);
        var take = Math.min(count, room);
        if (take <= 0) return 0;

        var start = samples >> 2;
        var copy = HEAPF32.slice(start, start + take);

        try {
            // Transferring hands the browser the buffer outright, which avoids the
            // structured clone copying it again.
            channel.node.port.postMessage(copy, [copy.buffer]);
        } catch (e) {
            D4W.Warn("could not deliver samples to a voice output channel: " + e);
            return 0;
        }

        channel.posted += take;

        return take;
    },

    D4W_OutSetGain: function (handle, gain, pan) {
        var channel = D4W.channels[handle];
        if (!channel) return;

        var context = D4W.context;
        var now = context ? context.currentTime : 0;

        // Ramp rather than jump. A gain step of any size is an audible click, and
        // this is updated every frame as players move.
        try {
            channel.gain.gain.setTargetAtTime(gain, now, 0.02);
        } catch (e) {
            channel.gain.gain.value = gain;
        }

        if (channel.panner) {
            try {
                channel.panner.pan.setTargetAtTime(pan, now, 0.02);
            } catch (e) {
                channel.panner.pan.value = pan;
            }
        }
    },

    D4W_OutTakeUnderrunCount: function (handle) {
        var channel = D4W.channels[handle];
        if (!channel) return 0;

        var delta = channel.underruns - channel.reportedUnderruns;
        channel.reportedUnderruns = channel.underruns;

        return delta > 0 ? delta : 0;
    }
};

autoAddDeps(Dissonance4WebAudioLibrary, "$D4W");
mergeInto(LibraryManager.library, Dissonance4WebAudioLibrary);
