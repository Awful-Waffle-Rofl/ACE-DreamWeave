using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessagePrivateUpdateSkill : GameMessage
    {
        public GameMessagePrivateUpdateSkill(WorldObject worldObject, CreatureSkill creatureSkill)
            : base(GameMessageOpcode.PrivateUpdateSkill, GameMessageGroup.UIQueue, 37)
        {
            Writer.Write(worldObject.Sequences.GetNextSequence(Sequence.SequenceType.UpdateSkill, creatureSkill.Skill));

            ushort adjustPP = 1;            // If this is not 0, it appears to trigger the initLevel to be treated as extra XP applied to the skill

            Writer.Write((uint)creatureSkill.Skill);
            Writer.Write(creatureSkill.Ranks);
            Writer.Write(adjustPP);
            Writer.Write((uint)creatureSkill.AdvancementClass);
            Writer.Write(creatureSkill.ExperienceSpent);

            Writer.Write(creatureSkill.NetworkInitLevel);     // starting point for advancement (+ Enhanced <skill> class ability bonus)
            Writer.Write(creatureSkill.PropertiesSkill.ResistanceAtLastCheck);
            Writer.Write(creatureSkill.PropertiesSkill.LastUsedTime);
        }
    }
}
