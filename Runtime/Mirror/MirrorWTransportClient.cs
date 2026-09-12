using System;
using Dissonance.Networking;
using JetBrains.Annotations;
using Mirror;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// The Dissonance voice client, running inside a Mirror client.
    /// </summary>
    /// <remarks>
    /// Identical on desktop and in a browser. Everything that differs between the
    /// two - capturing the microphone, running the codec, playing audio back -
    /// sits on the other side of Dissonance's audio pipeline, not here.
    /// </remarks>
    public class MirrorWTransportClient
        : BaseClient<MirrorWTransportServer, MirrorWTransportClient, MirrorConn>
    {
        [NotNull] private readonly MirrorWTransportCommsNetwork _network;

        public MirrorWTransportClient([NotNull] MirrorWTransportCommsNetwork network)
            : base(network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        public override void Connect()
        {
            // Loopback is handled directly, so when the server is in this same
            // process the server's handler must stay installed - binding ours
            // would replace it and the session would never complete.
            if (!_network.Mode.IsServerEnabled())
                NetworkClient.ReplaceHandler<DissonanceNetworkMessage>(OnMessageReceived);
            else
                Log.Debug("Not binding the client network handler; the server is running locally");

            Connected();
        }

        public override void Disconnect()
        {
            if (!_network.Mode.IsServerEnabled())
                NetworkClient.ReplaceHandler<DissonanceNetworkMessage>(MirrorWTransportCommsNetwork.DiscardClientMessage);

            base.Disconnect();
        }

        private void OnMessageReceived(DissonanceNetworkMessage message)
        {
            using (message)
                NetworkReceivedPacket(message.Data);
        }

        protected override void ReadMessages()
        {
            // Mirror delivers messages through a handler, so there is nothing to
            // poll for here.
        }

        protected override void SendReliable(ArraySegment<byte> packet)
        {
            if (!Send(packet, _network.ReliableChannel))
                FatalError("Failed to send a reliable Dissonance packet through Mirror");
        }

        protected override void SendUnreliable(ArraySegment<byte> packet)
        {
            Send(packet, _network.UnreliableChannel);
        }

        /// <returns>false only if sending failed in a way worth killing the session over.</returns>
        private bool Send(ArraySegment<byte> packet, int channel)
        {
            if (_network.PreprocessPacketToServer(packet))
                return true;

            var connection = NetworkClient.connection;
            if (connection == null || !NetworkClient.active)
            {
                // The client is on its way out. Dropping voice here is normal, and
                // the comms network will notice Mirror has gone and stop.
                return true;
            }

            connection.Send(new DissonanceNetworkMessage(packet), channel);
            return true;
        }
    }
}
