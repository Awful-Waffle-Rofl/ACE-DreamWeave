using ACE.Entity;

namespace ACE.Server.Network.GameMessages.Messages
{
    /// <summary>
    /// Opcode 0xF754 (PlayScriptId), the sibling of 0xF755 (PlayEffect).
    ///
    /// 0xF755 carries a PlayScript ENUM that the client resolves through the target's
    /// PhysicsScriptTable, which is why only a handful of scripts were ever reachable by broadcast.
    /// 0xF754 carries a raw 0x33xxxxxx script DataID instead, bypassing that table entirely. It is
    /// declared in GameMessageOpcode.cs but was never used by ACE; confirmed working live 2026-08-07.
    ///
    /// The wire format is guid + DataID. Variant 2 (with a trailing float) also rendered, which most
    /// likely means the client ignores trailing bytes rather than parsing them - so the shipping
    /// constructor sends the shorter, confirmed payload.
    /// </summary>
    public class GameMessagePlayScriptId : GameMessage
    {
        /// <summary>
        /// The confirmed-working form: object guid followed by a raw script DataID.
        /// </summary>
        public GameMessagePlayScriptId(ObjectGuid guid, uint scriptId)
            : base(GameMessageOpcode.PlayScriptId, GameMessageGroup.SmartboxQueue, 16)
        {
            Writer.WriteGuid(guid);
            Writer.Write(scriptId);
        }

        /// <summary>
        /// PROBE form. The part index a script's emitters attach to is baked into the CreateParticle
        /// hook in the dat, not sent over the wire, so an effect authored at part 0 rides a bow's limb
        /// tip instead of sitting at the grip (part index -1 = the object's own frame). Variants 4 and
        /// 5 test the long shot that the client will read an explicit part index if one is appended.
        ///
        /// Expected to do nothing - variant 2's success is evidence the client ignores trailing bytes.
        /// Cheap enough to be worth one live test rather than an assumption.
        /// </summary>
        public GameMessagePlayScriptId(ObjectGuid guid, uint scriptId, int variant, float speed, uint partIndex)
            : base(GameMessageOpcode.PlayScriptId, GameMessageGroup.SmartboxQueue, 24)
        {
            Writer.WriteGuid(guid);

            switch (variant)
            {
                default:
                case 1:                                     // confirmed working
                    Writer.Write(scriptId);
                    break;

                case 2:                                     // confirmed working - trailing float
                    Writer.Write(scriptId);
                    Writer.Write(speed);
                    break;

                case 3:                                     // speed first, to test positional parsing
                    Writer.Write(speed);
                    Writer.Write(scriptId);
                    break;

                case 4:                                     // trailing part index
                    Writer.Write(scriptId);
                    Writer.Write(partIndex);
                    break;

                case 5:                                     // speed then part index
                    Writer.Write(scriptId);
                    Writer.Write(speed);
                    Writer.Write(partIndex);
                    break;
            }
        }
    }
}
