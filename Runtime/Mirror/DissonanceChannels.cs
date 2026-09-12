using System;
using System.Reflection;
using Mirror;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// The Mirror channel ids Dissonance traffic travels on, and a startup check
    /// that the active transport really delivers them the way Dissonance needs.
    /// </summary>
    /// <remarks>
    /// Dissonance wants two channels with different delivery guarantees: session
    /// setup, room membership and text chat go reliably, voice goes unreliably.
    /// Mirror exposes channels as plain ints so a project can add its own; the
    /// two ids used here are the ones Mirror reserves for Dissonance.
    /// </remarks>
    public static class DissonanceChannels
    {
        public const int Reliable = Channels.DissonanceReliable;
        public const int Unreliable = Channels.DissonanceUnreliable;

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
        /// Warn once about a transport configuration that will hurt voice, and do
        /// it loudly: the symptoms - voice that drifts further and further behind
        /// the game, or stops entirely on a lossy connection - do not point at
        /// the transport's channel list on their own.
        /// </summary>
        internal static void CheckActiveTransport()
        {
            if (_checked)
                return;
            _checked = true;

            var transport = Transport.active;
            if (transport == null)
            {
                Log.Warn("No active Mirror transport, so Dissonance cannot check how its channels will be delivered");
                return;
            }

            CheckDelivery(transport);
            CheckPacketSize(transport, Reliable, "reliable");
            CheckPacketSize(transport, Unreliable, "unreliable");
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
        private static void CheckDelivery(Transport transport)
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
                    $"Transport '{name}' does not report per channel delivery. Check by hand that channel {Reliable} is reliable " +
                    $"and channel {Unreliable} is unreliable."
                );
                return;
            }

            bool reliableIsReliable;
            bool unreliableIsReliable;
            try
            {
                reliableIsReliable = (bool)probe.Invoke(transport, new object[] { Reliable });
                unreliableIsReliable = (bool)probe.Invoke(transport, new object[] { Unreliable });
            }
            catch (Exception ex)
            {
                Log.Debug($"Transport '{name}' threw while reporting channel delivery: {ex.Message}");
                return;
            }

            if (!reliableIsReliable)
            {
                Log.Error(
                    $"Transport '{name}' delivers channel {Reliable} unreliably, but Dissonance needs it reliable for session setup, rooms " +
                    "and text chat. Voice will fail to start for some players."
                );
            }

            if (unreliableIsReliable)
            {
                Log.Warn(
                    $"Transport '{name}' delivers channel {Unreliable} reliably. Dissonance sends voice on it and expects datagrams, so voice " +
                    "will be retransmitted and head of line blocked, drifting further behind the longer a lossy connection lasts. Configure " +
                    $"the transport so channel {Unreliable} is unreliable (for MirrorWTransport, extend the Channels list to {Unreliable + 1} " +
                    $"entries and set element {Unreliable} to Unreliable)."
                );
            }
        }
    }
}
