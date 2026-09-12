using System;
using Dissonance.Extensions;
using JetBrains.Annotations;
using Mirror;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// A Dissonance packet, wrapped so Mirror will carry it.
    /// </summary>
    /// <remarks>
    /// Dissonance hands out packets from an internal pool and takes them back as
    /// soon as the send call returns, so the payload is copied into a buffer of
    /// our own on construction and recycled once Mirror has serialised it.
    /// </remarks>
    public struct DissonanceNetworkMessage
        : NetworkMessage, IDisposable
    {
        public ArraySegment<byte> Data;

        public DissonanceNetworkMessage(ArraySegment<byte> packet)
        {
            Data = packet.CopyToSegment(DissonanceNetworkMessageSerializer.Buffers.Get());
        }

        public void Dispose()
        {
            var array = Data.Array;
            if (array != null && array.Length == DissonanceNetworkMessageSerializer.BufferLength)
            {
                DissonanceNetworkMessageSerializer.Buffers.Put(array);
                Data = new ArraySegment<byte>(Array.Empty<byte>(), 0, 0);
            }
        }
    }

    /// <summary>
    /// Mirror finds the reader and writer for a message type by looking for
    /// extension methods with these exact shapes, so they have to live in a
    /// static class rather than on the message itself.
    /// </summary>
    public static class DissonanceNetworkMessageSerializer
    {
        /// <summary>
        /// Dissonance never emits a packet larger than its own 1024 byte send
        /// buffer, so a fixed size pool is enough and a buffer of another length
        /// can be recognised as not belonging to the pool.
        /// </summary>
        public const int BufferLength = 1024;

        // Qualified rather than imported: Mirror has a ConcurrentPool of its own.
        internal static readonly Datastructures.ConcurrentPool<byte[]> Buffers = new Datastructures.ConcurrentPool<byte[]>(16, () => new byte[BufferLength]);

        public static void Serialize([NotNull] this NetworkWriter writer, DissonanceNetworkMessage value)
        {
            writer.WriteUShort((ushort)value.Data.Count);
            writer.WriteBytes(value.Data.Array, value.Data.Offset, value.Data.Count);

            // Serialisation is the last thing Mirror does with the payload, so
            // the buffer can go back to the pool now.
            value.Dispose();
        }

        public static DissonanceNetworkMessage Deserialize([NotNull] this NetworkReader reader)
        {
            var length = reader.ReadUShort();
            if (length > BufferLength)
                throw new DissonanceException($"Received a {length} byte Dissonance packet; the limit is {BufferLength} bytes");

            var array = Buffers.Get();
            var payload = reader.ReadBytesSegment(length);
            Array.Copy(payload.Array, payload.Offset, array, 0, length);

            return new DissonanceNetworkMessage { Data = new ArraySegment<byte>(array, 0, length) };
        }
    }
}
