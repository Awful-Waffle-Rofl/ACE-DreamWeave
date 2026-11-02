using System;

using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Network
{
    /// <summary>
    /// Pulls at most two identifying values out of an outbound message's bytes for the outbound ring
    /// (<see cref="OutboundRing"/>). Never throws and never allocates: an unknown opcode or a short buffer yields zeros.
    /// Every offset below is pinned by OutboundMessageDecodeTests against a real constructed message.
    /// <para>Layouts (offset 0 is the u32 opcode):</para>
    /// <para>- object messages (create, update, objdesc, delete, motion, position, vector, pickup): guid at 4;
    /// a create's aux is its SetupId, captured on the message.</para>
    /// <para>- PlayEffect, PlayScriptId, Sound, SetState, ParentEvent: guid at 4, aux = u32 at 8 (script, sound,
    /// state, or the child's guid).</para>
    /// <para>- 0x02CD-0x02E9 property updates, after a one-byte sequence at 4: odd opcodes are private (property id
    /// at 5), even are public (guid at 5, property id at 9), except PublicUpdatePropertyString, which writes the
    /// property id at 5 and the guid at 9.</para>
    /// <para>- GameEvent: guid 4 (the player, not reported), sequence 8, event type 12, first body u32 at 16.</para>
    /// </summary>
    public static class OutboundMessageDecode
    {
        public static void Decode(GameMessage message, out uint guid, out uint aux, out ushort eventType)
        {
            guid = 0;
            aux = 0;
            eventType = 0;

            if (message?.Data == null || !message.Data.TryGetBuffer(out var segment) || segment.Array == null)
                return;

            var length = (int)Math.Min(message.Data.Length, segment.Count);

            Decode((ushort)message.Opcode, segment.Array, segment.Offset, length, out guid, out aux, out eventType);

            if (message.Opcode == GameMessageOpcode.ObjectCreate && message is GameMessageCreateObject create)
                aux = create.SetupId;
        }

        /// <summary>The buffer-only decode: <paramref name="length"/> bytes of message starting at <paramref name="offset"/>.</summary>
        public static void Decode(ushort opcode, byte[] buffer, int offset, int length, out uint guid, out uint aux, out ushort eventType)
        {
            guid = 0;
            aux = 0;
            eventType = 0;

            if (buffer == null || offset < 0 || length < 0 || offset > buffer.Length || length > buffer.Length - offset)
                return;

            switch (opcode)
            {
                case (ushort)GameMessageOpcode.ObjectCreate:
                case (ushort)GameMessageOpcode.UpdateObject:
                case (ushort)GameMessageOpcode.ObjDescEvent:
                case (ushort)GameMessageOpcode.ObjectDelete:
                case (ushort)GameMessageOpcode.Motion:
                case (ushort)GameMessageOpcode.UpdatePosition:
                case (ushort)GameMessageOpcode.VectorUpdate:
                case (ushort)GameMessageOpcode.PickupEvent:
                    guid = U32(buffer, offset, length, 4);
                    return;

                case (ushort)GameMessageOpcode.PlayEffect:
                case (ushort)GameMessageOpcode.PlayScriptId:
                case (ushort)GameMessageOpcode.Sound:
                case (ushort)GameMessageOpcode.SetState:
                case (ushort)GameMessageOpcode.ParentEvent:
                    guid = U32(buffer, offset, length, 4);
                    aux = U32(buffer, offset, length, 8);
                    return;

                case (ushort)GameMessageOpcode.PublicUpdatePropertyString:
                    aux = U32(buffer, offset, length, 5);
                    guid = U32(buffer, offset, length, 9);
                    return;

                case (ushort)GameMessageOpcode.GameEvent:
                    eventType = (ushort)U32(buffer, offset, length, 12);
                    aux = U32(buffer, offset, length, 16);
                    return;
            }

            if (opcode >= (ushort)GameMessageOpcode.PrivateUpdatePropertyInt && opcode <= (ushort)GameMessageOpcode.PrivateUpdateAttribute2ndLevel)
            {
                if ((opcode & 1) != 0)
                    aux = U32(buffer, offset, length, 5);
                else
                {
                    guid = U32(buffer, offset, length, 5);
                    aux = U32(buffer, offset, length, 9);
                }
            }
        }

        private static uint U32(byte[] buffer, int offset, int length, int at)
        {
            if (at + 4 > length)
                return 0;

            var i = offset + at;
            return (uint)(buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16) | (buffer[i + 3] << 24));
        }
    }
}
