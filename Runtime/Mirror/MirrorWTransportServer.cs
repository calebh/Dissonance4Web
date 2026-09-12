using System;
using System.Collections.Generic;
using Dissonance.Networking;
using Dissonance.Networking.Server;
using JetBrains.Annotations;
using Mirror;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// The Dissonance voice server, running inside a Mirror server.
    /// </summary>
    /// <remarks>
    /// The server never looks inside a voice packet. It reads the Dissonance
    /// routing header, works out which peers are listening, and forwards the
    /// encoded audio untouched - so there is no decode, no re-encode and no
    /// resample anywhere in the relay, whether the speaker is a browser or a
    /// desktop player.
    /// </remarks>
    public class MirrorWTransportServer
        : BaseServer<MirrorWTransportServer, MirrorWTransportClient, MirrorConn>
    {
        [NotNull] private readonly MirrorWTransportCommsNetwork _network;

        /// <summary>
        /// Connections of the remote peers Dissonance knows about, polled for
        /// disconnection.
        /// </summary>
        private readonly List<NetworkConnectionToClient> _peers = new List<NetworkConnectionToClient>();

        public MirrorWTransportServer([NotNull] MirrorWTransportCommsNetwork network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        public override void Connect()
        {
            NetworkServer.ReplaceHandler<DissonanceNetworkMessage>(OnMessageReceived);

            base.Connect();
        }

        public override void Disconnect()
        {
            base.Disconnect();

            NetworkServer.ReplaceHandler<DissonanceNetworkMessage>(MirrorWTransportCommsNetwork.DiscardServerMessage);

            _peers.Clear();
        }

        private void OnMessageReceived(NetworkConnectionToClient source, DissonanceNetworkMessage message)
        {
            using (message)
                NetworkReceivedPacket(new MirrorConn(source), message.Data);
        }

        protected override void ReadMessages()
        {
            // Mirror delivers messages through a handler, so there is nothing to
            // poll for here.
        }

        protected override void AddClient([NotNull] ClientInfo<MirrorConn> client)
        {
            base.AddClient(client);

            // The local player of a host has no transport connection to watch, and
            // will be torn down with the rest of the session instead.
            if (client.PlayerName == _network.PlayerName)
                return;

            if (client.Connection.Connection is NetworkConnectionToClient conn && !_peers.Contains(conn))
                _peers.Add(conn);
        }

        public override ServerState Update()
        {
            // Mirror only reports disconnections to the NetworkManager, and it
            // assigns NetworkServer.OnDisconnectedEvent rather than adding to it,
            // so subscribing would either be clobbered by the NetworkManager or
            // clobber it. Polling is the only way to notice without making this
            // component a NetworkManager of its own.
            for (var i = _peers.Count - 1; i >= 0; i--)
            {
                var conn = _peers[i];
                if (IsConnected(conn))
                    continue;

                _peers.RemoveAt(i);
                ClientDisconnected(new MirrorConn(conn));
            }

            return base.Update();
        }

        private static bool IsConnected(NetworkConnection connection)
        {
            if (connection == null)
                return false;

            // Only a server side connection can be looked up in the server's
            // connection table. Anything else (the local client's connection to
            // the server, for instance) is not ours to track.
            if (!(connection is NetworkConnectionToClient conn))
                return false;

            return conn.isReady && NetworkServer.connections.ContainsKey(conn.connectionId);
        }

        protected override void SendReliable(MirrorConn connection, ArraySegment<byte> packet)
        {
            if (!Send(packet, connection, _network.ReliableChannel))
                FatalError("Failed to send a reliable Dissonance packet through Mirror");
        }

        protected override void SendUnreliable(MirrorConn connection, ArraySegment<byte> packet)
        {
            Send(packet, connection, _network.UnreliableChannel);
        }

        /// <returns>false only if sending failed in a way worth killing the session over.</returns>
        private bool Send(ArraySegment<byte> packet, MirrorConn connection, int channel)
        {
            if (_network.PreprocessPacketToClient(packet, connection))
                return true;

            if (connection.Connection == null)
            {
                Log.Error("Cannot send a Dissonance packet to a null destination");
                return false;
            }

            // Sending to a peer that has just gone away is not a failure. It is
            // easily caused by a race with the disconnection poll above, and a
            // packet to a peer that no longer exists is no loss.
            if (!IsConnected(connection.Connection))
                return true;

            connection.Connection.Send(new DissonanceNetworkMessage(packet), channel);
            return true;
        }
    }
}
