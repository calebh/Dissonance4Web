using System;
using Mirror;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// Identifies one peer of the Dissonance session by the Mirror connection it
    /// arrived on. Dissonance uses this as its opaque per-peer key, so it only
    /// has to be equatable and hashable.
    /// </summary>
    public readonly struct MirrorConn
        : IEquatable<MirrorConn>
    {
        public readonly NetworkConnection Connection;

        public MirrorConn(NetworkConnection connection)
        {
            Connection = connection;
        }

        /// <summary>
        /// The Mirror connection id, or -1 for anything that does not have one.
        /// </summary>
        /// <remarks>
        /// Only a server side connection carries an id; the base type does not
        /// declare one, because the client's single connection to the server has
        /// no need of it.
        /// </remarks>
        public int ConnectionId => Connection is NetworkConnectionToClient conn ? conn.connectionId : -1;

        public bool Equals(MirrorConn other)
        {
            if (ReferenceEquals(Connection, other.Connection))
                return true;
            if (Connection == null || other.Connection == null)
                return false;
            return Connection.Equals(other.Connection);
        }

        public override bool Equals(object obj) => obj is MirrorConn other && Equals(other);

        public override int GetHashCode() => Connection?.GetHashCode() ?? 0;

        public override string ToString() => Connection?.ToString() ?? "<null connection>";
    }
}
