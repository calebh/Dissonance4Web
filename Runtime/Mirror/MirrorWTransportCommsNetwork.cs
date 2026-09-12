using System;
using System.Collections.Generic;
using Dissonance.Extensions;
using Dissonance.Networking;
using Mirror;
using UnityEngine;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// Carries a Dissonance voice session over Mirror.
    /// </summary>
    /// <remarks>
    /// This is the successor to the <c>MirrorIgnorance</c> integration. The name
    /// changed because the old one was picked when Ignorance was the only Mirror
    /// transport offering both a reliable and an unreliable channel;
    /// MirrorWTransport now offers both over WebTransport, which is what lets a
    /// browser join the same voice session as a desktop player.
    ///
    /// Nothing here is specific to WebTransport. The integration needs a transport
    /// that delivers <see cref="ReliableChannel"/> reliably and
    /// <see cref="UnreliableChannel"/> as datagrams, and - if browsers are to join
    /// - one that works in a WebGL build. <see cref="DissonanceChannels"/> checks
    /// the first of those at startup.
    ///
    /// A browser can only ever be a client. Browsers cannot listen for
    /// WebTransport sessions, so Mirror cannot host a server in a WebGL build and
    /// neither can Dissonance. Web clients always connect to a server running
    /// elsewhere, and their voice is relayed by that server exactly as a desktop
    /// client's is.
    /// </remarks>
    [HelpURL("https://placeholder-software.co.uk/dissonance/docs/Basics/Quick-Start-MirrorIgnorance/")]
    public class MirrorWTransportCommsNetwork
        : BaseCommsNetwork<MirrorWTransportServer, MirrorWTransportClient, MirrorConn, Unit, Unit>
    {
        [SerializeField]
        [Tooltip("Mirror channel for session setup, room membership and text chat. Must be delivered reliably and in order. Defaults to Mirror's own reliable channel (0), which needs no configuration anywhere.")]
        private int _reliableChannel = DissonanceChannels.DefaultReliable;

        [SerializeField]
        [Tooltip("Mirror channel for voice. Must be delivered unreliably: retransmitting late voice is worse than dropping it. Defaults to Mirror's own unreliable channel (1), which needs no configuration anywhere.")]
        private int _unreliableChannel = DissonanceChannels.DefaultUnreliable;

        /// <summary>
        /// Mirror channel carrying session setup, room membership and text chat.
        /// Must be delivered reliably and in order.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Channels.Reliable"/>. Mirror describes channels
        /// as plain ints rather than an enum so a project can add its own, so
        /// there is no id reserved for Dissonance to claim; give it one of your own
        /// if you would rather voice was accounted for or batched separately from
        /// the rest of the game's traffic, and configure the transport to match.
        ///
        /// Read on every send, so change it before Dissonance connects. Changing
        /// it during a session would reorder traffic mid-stream.
        /// </remarks>
        public int ReliableChannel
        {
            get => _reliableChannel;
            set => SetChannel(ref _reliableChannel, value, nameof(ReliableChannel));
        }

        /// <summary>
        /// Mirror channel carrying voice. Must be delivered unreliably.
        /// </summary>
        /// <inheritdoc cref="ReliableChannel" path="/remarks"/>
        public int UnreliableChannel
        {
            get => _unreliableChannel;
            set => SetChannel(ref _unreliableChannel, value, nameof(UnreliableChannel));
        }

        private void SetChannel(ref int field, int value, string name)
        {
            if (field == value)
                return;

            if (value < 0)
                throw new ArgumentOutOfRangeException(name, $"A Mirror channel id cannot be negative, but {value} was given");

            if (Mode != NetworkMode.None)
            {
                Log.Warn(
                    $"{name} was changed to {value} while a voice session is running. Packets already in flight were sent on channel " +
                    $"{field}, so ordering across the change is not guaranteed. Set the channel before connecting."
                );
            }

            field = value;
            DissonanceChannels.ResetCheck();
        }

        /// <summary>
        /// Keeps the serialized ids sane, and makes the startup check run again
        /// after they are edited in the inspector - which writes the fields
        /// directly and so does not go through the properties above.
        /// </summary>
        private void OnValidate()
        {
            _reliableChannel = Mathf.Max(0, _reliableChannel);
            _unreliableChannel = Mathf.Max(0, _unreliableChannel);

            DissonanceChannels.ResetCheck();
        }

        /// <summary>
        /// Packets the local server sent to the local client, waiting to be
        /// delivered on the next frame.
        /// </summary>
        /// <remarks>
        /// Host mode never reaches the transport. Delivering such a packet inline
        /// would run the local client inside a local server call stack, which
        /// makes for confusing stack traces, so it is queued and handed over from
        /// <see cref="Update"/> instead.
        /// </remarks>
        // Qualified rather than imported: Mirror has a ConcurrentPool of its own.
        private readonly Datastructures.ConcurrentPool<byte[]> _loopbackBuffers = new Datastructures.ConcurrentPool<byte[]>(8, () => new byte[DissonanceNetworkMessageSerializer.BufferLength]);
        private readonly List<ArraySegment<byte>> _loopbackQueue = new List<ArraySegment<byte>>();

        protected override MirrorWTransportServer CreateServer(Unit details)
        {
            return new MirrorWTransportServer(this);
        }

        protected override MirrorWTransportClient CreateClient(Unit details)
        {
            return new MirrorWTransportClient(this);
        }

        protected override void Initialize()
        {
            // Claim the message type up front and throw the payloads away.
            // Without this, a packet that arrives before the server object exists
            // is logged by Mirror as an unknown message id, which reads like a
            // version mismatch rather than a race.
            NetworkServer.ReplaceHandler<DissonanceNetworkMessage>(DiscardServerMessage);

            base.Initialize();
        }

        protected override void Update()
        {
            if (IsInitialized)
            {
                if (IsNetworkActive())
                {
                    var server = NetworkServer.active;
                    var client = NetworkClient.active;

                    if (Mode.IsServerEnabled() != server || Mode.IsClientEnabled() != client)
                    {
                        DissonanceChannels.CheckActiveTransport(_reliableChannel, _unreliableChannel);

                        if (server && client)
                            RunAsHost(Unit.None, Unit.None);
                        else if (server)
                            RunAsDedicatedServer(Unit.None);
                        else if (client)
                            RunAsClient(Unit.None);
                    }
                }
                else if (Mode != NetworkMode.None)
                {
                    // Mirror has shut down, so the voice session goes with it.
                    Stop();

                    // Anything still queued for the local client will never be
                    // delivered now.
                    RecycleLoopbackQueue();
                    DissonanceChannels.ResetCheck();
                }

                DeliverLoopbackQueue();
            }

            base.Update();
        }

        protected override void OnDisable()
        {
            RecycleLoopbackQueue();

            base.OnDisable();
        }

        /// <summary>
        /// Whether Mirror is far enough along for Dissonance to run.
        /// </summary>
        /// <remarks>
        /// A client that has connected but is not yet "ready" cannot receive the
        /// messages Dissonance is about to send, so starting the voice session
        /// then would only lose the handshake.
        /// </remarks>
        private static bool IsNetworkActive()
        {
            var singleton = NetworkManager.singleton;
            if (ReferenceEquals(singleton, null) || !singleton.isNetworkActive)
                return false;

            if (!NetworkServer.active && !NetworkClient.active)
                return false;

            if (NetworkClient.active && (NetworkClient.connection == null || !NetworkClient.connection.isReady))
                return false;

            return true;
        }

        private void DeliverLoopbackQueue()
        {
            for (var i = 0; i < _loopbackQueue.Count; i++)
            {
                var packet = _loopbackQueue[i];

                Client?.NetworkReceivedPacket(packet);

                if (packet.Array != null)
                    _loopbackBuffers.Put(packet.Array);
            }

            _loopbackQueue.Clear();
        }

        private void RecycleLoopbackQueue()
        {
            for (var i = 0; i < _loopbackQueue.Count; i++)
            {
                var array = _loopbackQueue[i].Array;
                if (array != null)
                    _loopbackBuffers.Put(array);
            }

            _loopbackQueue.Clear();
        }

        /// <summary>
        /// Called by the server before it sends. Returns true when the packet was
        /// handled as loopback and must not go to the transport.
        /// </summary>
        internal bool PreprocessPacketToClient(ArraySegment<byte> packet, MirrorConn destination)
        {
            if (Server == null)
                throw Log.CreatePossibleBugException("server packet preprocessing ran, but this peer is not a server", "4b0b2a51-52b0-4f4e-9a4a-1f0d4b0e6c9a");

            // A dedicated server has no local client, so nothing can be loopback.
            if (Client == null)
                return false;

            if (!ReferenceEquals(NetworkClient.connection, destination.Connection))
                return false;

            _loopbackQueue.Add(packet.CopyToSegment(_loopbackBuffers.Get()));
            return true;
        }

        /// <summary>
        /// Called by the client before it sends. Returns true when the packet was
        /// handled as loopback and must not go to the transport.
        /// </summary>
        internal bool PreprocessPacketToServer(ArraySegment<byte> packet)
        {
            if (Client == null)
                throw Log.CreatePossibleBugException("client packet preprocessing ran, but this peer is not a client", "6cf3d2f4-7a01-4a18-8a5c-2a6d9f8f2b3e");

            if (Server == null)
                return false;

            // This is loopback, so the source and the destination are the same
            // connection by definition.
            Server.NetworkReceivedPacket(new MirrorConn(NetworkClient.connection), packet);
            return true;
        }

        /// <summary>
        /// Drops a Dissonance packet that arrived while no Dissonance client was
        /// listening, returning its buffer to the pool.
        /// </summary>
        internal static void DiscardClientMessage(DissonanceNetworkMessage message)
        {
            if (Logs.GetLogLevel(LogCategory.Network) <= LogLevel.Trace)
                Debug.Log("Discarding a Dissonance network message; no Dissonance peer is running to handle it");

            message.Dispose();
        }

        /// <summary>
        /// Drops a Dissonance packet that arrived while no Dissonance server was
        /// listening, returning its buffer to the pool.
        /// </summary>
        internal static void DiscardServerMessage(NetworkConnectionToClient source, DissonanceNetworkMessage message)
        {
            DiscardClientMessage(message);
        }
    }
}
