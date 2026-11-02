using System;

using ACE.Common.Cryptography;

namespace ACE.Server.Network
{
    public class ServerPacketFragment : PacketFragment
    {
        /// <summary>
        /// Set only on a message's LAST piece: that message's outbound ring index, so the packet's sequence can be noted
        /// on the ring entry when the packet is stamped. 0 otherwise. Diagnostics only; never serialized.
        /// </summary>
        public long RingIndex;

        public ServerPacketFragment(byte[] data)
        {
            Data = data;
        }

        /// <summary>
        /// Returns the Hash32 of the payload added to buffer
        /// </summary>
        public uint PackAndReturnHash32(byte[] buffer, ref int offset)
        {
            Header.Size = (ushort)(PacketFragmentHeader.HeaderSize + Data.Length);

            var headerHash32 = Header.PackAndReturnHash32(buffer, ref offset);

            Buffer.BlockCopy(Data, 0, buffer, offset, Data.Length);
            offset += Data.Length;

            return headerHash32 + Hash32.Calculate(Data, Data.Length);
        }
    }
}
