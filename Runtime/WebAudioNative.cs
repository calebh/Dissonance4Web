using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Dissonance.Web
{
    /// <summary>
    /// State of the browser microphone, as reported by
    /// <see cref="WebAudioNative.D4W_MicState"/>.
    /// </summary>
    public enum WebMicrophoneState
    {
        /// <summary>Nothing has been requested yet.</summary>
        Idle = 0,

        /// <summary>getUserMedia is in flight, possibly waiting on the user.</summary>
        Starting = 1,

        /// <summary>Samples are arriving.</summary>
        Running = 2,

        /// <summary>The request failed, or the user refused. See <see cref="WebAudioNative.MicrophoneError"/>.</summary>
        Failed = 3
    }

    /// <summary>
    /// Bindings for <c>Dissonance4WebAudio.jslib</c>, which owns the browser side
    /// of voice capture and playback.
    /// </summary>
    /// <remarks>
    /// Polling rather than callbacks, for the same reason MirrorWTransport polls:
    /// everything should cross into managed code at a point in the frame we chose,
    /// and it avoids the dynCall compatibility dance between Emscripten versions.
    ///
    /// Outside a WebGL player every entry point is a stub. That keeps the calling
    /// code free of conditional compilation, and means the editor can load these
    /// types while a WebGL build is the active target without link errors.
    /// </remarks>
    public static class WebAudioNative
    {
        /// <summary>How many bytes of UTF-8 the string getters will write at most.</summary>
        public const int StringBufferSize = 256;

        private static readonly byte[] StringBuffer = new byte[StringBufferSize];

        /// <summary>
        /// Whether this build can talk to the browser audio plugin at all.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// The browser's reason for refusing the microphone, or an empty string.
        /// </summary>
        public static string MicrophoneError => ReadString(D4W_MicError);

        /// <summary>
        /// Label of an input device, as the browser reports it. Labels are only
        /// filled in once the user has granted microphone access.
        /// </summary>
        public static string MicrophoneDeviceName(int index)
        {
            return ReadString((buffer, capacity) => D4W_MicDeviceName(index, buffer, capacity));
        }

        private static string ReadString(Func<byte[], int, int> read)
        {
            var length = read(StringBuffer, StringBufferSize);
            if (length <= 0)
                return string.Empty;

            return System.Text.Encoding.UTF8.GetString(StringBuffer, 0, Math.Min(length, StringBufferSize));
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // capture ////////////////////////////////////////////////////////////

        /// <summary>Whether the browser exposes getUserMedia and AudioWorklet.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicSupported();

        /// <summary>
        /// Start capture. <paramref name="deviceLabel"/> is a label from
        /// <see cref="D4W_MicDeviceName"/>, or empty for the default device; the
        /// plugin resolves it to a device id. Returns immediately; watch
        /// <see cref="D4W_MicState"/>.
        /// </summary>
        [DllImport("__Internal")]
        public static extern void D4W_MicStart(string deviceLabel, int echoCancellation, int noiseSuppression, int autoGainControl);

        [DllImport("__Internal")]
        public static extern void D4W_MicStop();

        /// <summary>See <see cref="WebMicrophoneState"/>.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicState();

        /// <summary>Capture sample rate, or 0 while not running.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicSampleRate();

        /// <summary>Estimated capture latency in milliseconds.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicLatencyMs();

        /// <summary>Samples waiting to be read.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicAvailable();

        /// <summary>
        /// Copies up to <paramref name="capacity"/> samples out of the capture
        /// buffer and returns how many were written.
        /// </summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicRead(float[] destination, int capacity);

        /// <summary>Samples dropped because nothing read them in time, since the last call.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_MicTakeOverflowCount();

        /// <summary>Throw away everything captured but not yet read.</summary>
        [DllImport("__Internal")]
        public static extern void D4W_MicFlush();

        [DllImport("__Internal")]
        public static extern int D4W_MicDeviceCount();

        [DllImport("__Internal")]
        public static extern int D4W_MicDeviceName(int index, byte[] destination, int capacity);

        [DllImport("__Internal")]
        public static extern int D4W_MicError(byte[] destination, int capacity);

        // playback ///////////////////////////////////////////////////////////

        /// <summary>
        /// Prepare the output graph. Returns 1 on success. Safe to call repeatedly.
        /// </summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutInit();

        /// <summary>Output sample rate, or 0 before the graph exists.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutSampleRate();

        /// <summary>
        /// Whether the AudioContext is running. Browsers start it suspended until
        /// the page has seen a user gesture.
        /// </summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutIsRunning();

        /// <summary>Ask the browser to resume the AudioContext.</summary>
        [DllImport("__Internal")]
        public static extern void D4W_OutResume();

        /// <summary>Creates a voice output channel. Returns a handle, or 0 on failure.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutCreate();

        [DllImport("__Internal")]
        public static extern void D4W_OutDestroy(int handle);

        /// <summary>Throw away everything buffered for this channel.</summary>
        [DllImport("__Internal")]
        public static extern void D4W_OutReset(int handle);

        /// <summary>Samples buffered for this channel but not yet played.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutQueued(int handle);

        /// <summary>
        /// Hands samples to the channel. Returns how many were accepted; a short
        /// return means the channel buffer is full.
        /// </summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutWrite(int handle, float[] samples, int count);

        /// <summary>
        /// Sets volume and stereo position. <paramref name="pan"/> runs from -1
        /// (left) to 1 (right).
        /// </summary>
        [DllImport("__Internal")]
        public static extern void D4W_OutSetGain(int handle, float gain, float pan);

        /// <summary>Render quanta this channel had to fill with silence, since the last call.</summary>
        [DllImport("__Internal")]
        public static extern int D4W_OutTakeUnderrunCount(int handle);
#else
        private const string NotWebGl = "the browser audio plugin is only available in WebGL builds";

        public static int D4W_MicSupported() => 0;
        public static void D4W_MicStart(string deviceLabel, int echoCancellation, int noiseSuppression, int autoGainControl) => throw new NotSupportedException(NotWebGl);
        public static void D4W_MicStop() { }
        public static int D4W_MicState() => (int)WebMicrophoneState.Idle;
        public static int D4W_MicSampleRate() => 0;
        public static int D4W_MicLatencyMs() => 0;
        public static int D4W_MicAvailable() => 0;
        public static int D4W_MicRead(float[] destination, int capacity) => 0;
        public static int D4W_MicTakeOverflowCount() => 0;
        public static void D4W_MicFlush() { }
        public static int D4W_MicDeviceCount() => 0;
        public static int D4W_MicDeviceName(int index, byte[] destination, int capacity) => 0;
        public static int D4W_MicError(byte[] destination, int capacity) => 0;

        public static int D4W_OutInit() => 0;
        public static int D4W_OutSampleRate() => 0;
        public static int D4W_OutIsRunning() => 0;
        public static void D4W_OutResume() { }
        public static int D4W_OutCreate() => 0;
        public static void D4W_OutDestroy(int handle) { }
        public static void D4W_OutReset(int handle) { }
        public static int D4W_OutQueued(int handle) => 0;
        public static int D4W_OutWrite(int handle, float[] samples, int count) => 0;
        public static void D4W_OutSetGain(int handle, float gain, float pan) { }
        public static int D4W_OutTakeUnderrunCount(int handle) => 0;
#endif
    }
}
