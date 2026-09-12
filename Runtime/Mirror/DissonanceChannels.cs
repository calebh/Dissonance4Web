using System;
using System.Reflection;
using Mirror;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// Defaults and startup checks for the two Mirror channels Dissonance traffic
    /// travels on.
    /// </summary>
    /// <remarks>
    /// Dissonance wants two channels with different delivery guarantees: session
    /// setup, room membership and text chat go reliably, voice goes unreliably.
    /// Mirror describes channels as plain ints rather than an enum precisely so a
    /// project can add its own, and which ids Dissonance should use is therefore a
    /// project's decision, not this package's - see
    /// <see cref="MirrorWTransportCommsNetwork.ReliableChannel"/>.
    ///
    /// The defaults are Mirror's own two channels, which need no configuration
    /// anywhere: stock Mirror defines them and every transport already delivers
    /// them correctly. Sharing them with the rest of the game's traffic costs
    /// nothing in particular - a channel selects a delivery mode, and Mirror
    /// separates messages within one by message id - so a project only needs
    /// dedicated ids if it wants voice accounted for or batched separately.
    /// </remarks>
    public static class DissonanceChannels
    {
        /// <summary>
        /// Default channel for Dissonance's reliable traffic: Mirror's own
        /// reliable channel.
        /// </summary>
        public const int DefaultReliable = Channels.Reliable;

        /// <summary>
        /// Default channel for voice: Mirror's own unreliable channel.
        /// </summary>
        public const int DefaultUnreliable = Channels.Unreliable;

        /// <summary>
        /// Largest Dissonance packet, plus what Mirror adds around it: a varint
        /// message id, our own two byte length prefix, and the batch timestamp.
        /// </summary>
        public const int RequiredPacketSize = DissonanceNetworkMessageSerializer.BufferLength + 16;

        private static readonly Log Log = Logs.Create(LogCategory.Network, "Dissonance Channels");

        private static bool _checked;

        internal static void ResetCheck()
        {
            _checked = false;
        }

        /// <summary>
        /// Warn once about a channel configuration that will hurt voice, and do it
        /// loudly: the symptoms - voice that drifts further and further behind the
        /// game, or stops entirely on a lossy connection - do not point at a
        /// channel id on their own.
        /// </summary>
        internal static void CheckActiveTransport(int reliable, int unreliable)
        {
            if (_checked)
                return;
            _checked = true;

            if (reliable == unreliable)
            {
                Log.Error(
                    $"Dissonance is set to send both its reliable traffic and its voice on channel {reliable}. They need different " +
                    "delivery guarantees, so these have to be two different channels."
                );
                return;
            }

            var transport = Transport.active;
            if (transport == null)
            {
                Log.Warn("No active Mirror transport, so Dissonance cannot check how its channels will be delivered");
                return;
            }

            CheckDelivery(transport, reliable, unreliable);
            CheckPacketSize(transport, reliable, "reliable");
            CheckPacketSize(transport, unreliable, "unreliable");
        }

        private static void CheckPacketSize(Transport transport, int channel, string description)
        {
            var name = transport.GetType().Name;

            int limit;
            try
            {
                limit = transport.GetMaxPacketSize(channel);
            }
            catch (Exception ex)
            {
                Log.Warn($"Transport '{name}' refused to report a maximum packet size for channel {channel}: {ex.Message}");
                return;
            }

            if (limit < RequiredPacketSize)
            {
                Log.Error(
                    $"Transport '{name}' caps the Dissonance {description} channel ({channel}) at {limit} bytes, but a Dissonance packet can " +
                    $"need up to {RequiredPacketSize}. Oversized packets will be dropped. Raise the transport's message size limit for this channel."
                );
            }
        }

        /// <summary>
        /// Asks the transport whether it will deliver each channel reliably.
        /// </summary>
        /// <remarks>
        /// Mirror's <see cref="Transport"/> has no API for this, so in the general
        /// case there is nothing to call. Transports that do expose it -
        /// MirrorWTransport's <c>IsReliableChannel(int)</c> among them - are
        /// probed by name, which keeps this assembly free of a dependency on any
        /// one transport. A transport that says nothing is left alone.
        /// </remarks>
        private static void CheckDelivery(Transport transport, int reliable, int unreliable)
        {
            var name = transport.GetType().Name;

            var probe = transport.GetType().GetMethod(
                "IsReliableChannel",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(int) },
                null
            );

            if (probe == null || probe.ReturnType != typeof(bool))
            {
                Log.Debug(
                    $"Transport '{name}' does not report per channel delivery. Check by hand that channel {reliable} is reliable " +
                    $"and channel {unreliable} is unreliable."
                );
                return;
            }

            bool reliableIsReliable;
            bool unreliableIsReliable;
            try
            {
                reliableIsReliable = (bool)probe.Invoke(transport, new object[] { reliable });
                unreliableIsReliable = (bool)probe.Invoke(transport, new object[] { unreliable });
            }
            catch (Exception ex)
            {
                Log.Debug($"Transport '{name}' threw while reporting channel delivery: {ex.Message}");
                return;
            }

            if (!reliableIsReliable)
            {
                Log.Error(
                    $"Transport '{name}' delivers channel {reliable} unreliably, but Dissonance needs it reliable for session setup, rooms " +
                    "and text chat. Voice will fail to start for some players."
                );
            }

            if (unreliableIsReliable)
            {
                Log.Warn(
                    $"Transport '{name}' delivers channel {unreliable} reliably. Dissonance sends voice on it and expects datagrams, so voice " +
                    "will be retransmitted and head of line blocked, drifting further behind the longer a lossy connection lasts. Either " +
                    $"point Dissonance at a channel the transport delivers unreliably, or configure channel {unreliable} to be unreliable " +
                    $"(for MirrorWTransport, extend its Channels list to {unreliable + 1} entries and set element {unreliable} to Unreliable)."
                );
            }
        }
    }
}
