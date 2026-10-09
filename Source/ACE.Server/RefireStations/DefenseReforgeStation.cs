using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// The Defense Requirement Reforge: a Marketplace NPC (wcid 1002752, PropertyBool.DefenseRequirementReforge).
    /// A player GIVES an item that demands Melee, Missile or Magic Defense to wield; it asks a yes/no
    /// confirmation to pay 5 Trade Notes (250,000, wcid 20630) to reforge that demand - down by as much as ten,
    /// up by as much as five, but never above what it was forged with. The item is never taken from the player.
    ///
    /// SLOT SCAN: a wield requirement can live in any of the item's four slots (WieldRequirements/
    /// WieldSkillType/WieldDifficulty and the ...2/3/4 triples, PropertyInt.cs). This station scans all four IN
    /// ORDER and operates on the FIRST slot whose requirement type is Skill or RawSkill and whose skill is one
    /// of the three Defense skills (owner-accepted: an item with more than one Defense requirement across
    /// multiple slots only has its first one reforged).
    ///
    /// CEILING is recorded the first time this station touches an item: PropertyInt.DefenseWieldOriginal (9045)
    /// is set to that slot's WieldDifficulty at that moment and never overwritten again, mirroring
    /// ArcaneAlignmentStation's ArcaneLoreOriginal.
    /// </summary>
    public static class DefenseReforgeStation
    {
        public const int ReforgeCostNotes = 5;

        private const string InsufficientFundsMessage = "You need 5 Trade Notes (250,000), in your pack or as 1,250,000 banked pyreals, to use the Defense Requirement Reforge.";

        private const string StackedRefusal = "The Defense Requirement Reforge cannot work on a stack - split it first.";

        /// <summary>
        /// The four wield-requirement slots, in scan order, as (requirement type property, skill type property,
        /// difficulty property) triples.
        /// </summary>
        private static readonly (PropertyInt Req, PropertyInt SkillType, PropertyInt Difficulty)[] Slots =
        {
            (PropertyInt.WieldRequirements, PropertyInt.WieldSkillType, PropertyInt.WieldDifficulty),
            (PropertyInt.WieldRequirements2, PropertyInt.WieldSkillType2, PropertyInt.WieldDifficulty2),
            (PropertyInt.WieldRequirements3, PropertyInt.WieldSkillType3, PropertyInt.WieldDifficulty3),
            (PropertyInt.WieldRequirements4, PropertyInt.WieldSkillType4, PropertyInt.WieldDifficulty4),
        };

        // ---------------- pure helpers ----------------

        public static bool IsDefenseSkill(Skill skill) =>
            skill == Skill.MeleeDefense || skill == Skill.MissileDefense || skill == Skill.MagicDefense;

        /// <summary>
        /// Pure slot finder over (requirement type, skill type) tuples for the item's four wield-requirement
        /// slots, in order. Returns the index (0-3) of the FIRST slot whose requirement type is Skill or
        /// RawSkill and whose skill is a Defense skill, or null if none qualifies (e.g. a Str/Level-only item,
        /// or an item with no wield requirement at all).
        /// </summary>
        public static int? FindDefenseSlot(IReadOnlyList<(WieldRequirement ReqType, int? SkillType)> slots)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                var (reqType, skillType) = slots[i];

                if ((reqType != WieldRequirement.Skill && reqType != WieldRequirement.RawSkill) || !skillType.HasValue)
                    continue;

                if (IsDefenseSkill((Skill)skillType.Value))
                    return i;
            }

            return null;
        }

        // ---------------- WorldObject-level slot access ----------------

        private static int? FindDefenseSlotOnItem(WorldObject item)
        {
            var slotTuples = Slots
                .Select(slot => ((WieldRequirement)(item.GetProperty(slot.Req) ?? 0), item.GetProperty(slot.SkillType)))
                .ToList();

            return FindDefenseSlot(slotTuples);
        }

        // ---------------- eligibility (needs a live Player/WorldObject) ----------------

        public static WeenieError VerifyEligible(Player player, WorldObject item, out string refusal)
        {
            refusal = null;

            if (item == null)
            {
                refusal = "The Defense Requirement Reforge needs a real item handed to it.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(item.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                refusal = "The Defense Requirement Reforge only works on a piece in your pack, not one you are wearing.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (RefireStationCommon.IsStacked(item.StackSize))
            {
                refusal = StackedRefusal;
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (FindDefenseSlotOnItem(item) == null)
            {
                refusal = "The Defense Requirement Reforge cannot reforge that: it has no Melee, Missile or Magic Defense requirement.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.IsBusy)
            {
                refusal = "You are too busy for that right now.";
                return WeenieError.YoureTooBusy;
            }

            return WeenieError.None;
        }

        // ---------------- entry point (Player_Inventory.GiveObjectToNPC) ----------------

        public static void HandleGive(Player player, WorldObject station, WorldObject item)
        {
            RefireStationCommon.HandleGive(
                player, station, item,
                enabledPropertyName: null,
                costNotes: ReforgeCostNotes,
                insufficientFundsMessage: InsufficientFundsMessage,
                verify: VerifyEligible,
                buildPrompt: BuildPrompt,
                apply: Apply);
        }

        private static string BuildPrompt(Player player, WorldObject item)
        {
            var slotIndex = FindDefenseSlotOnItem(item).Value;
            var slot = Slots[slotIndex];

            var skillName = ((Skill)item.GetProperty(slot.SkillType).Value).ToSentence();
            var current = item.GetProperty(slot.Difficulty).Value;
            var ceiling = PeekCeiling(item, slot, current);

            return $"Reforge the {skillName} requirement of {item.Name} (currently {current}) for 5 Trade Notes (250,000)? " +
                   $"Result is random (-10 to +5), never above {ceiling}.";
        }

        private static string Apply(Player player, WorldObject item)
        {
            var slotIndex = FindDefenseSlotOnItem(item).Value;
            var slot = Slots[slotIndex];

            var skillName = ((Skill)item.GetProperty(slot.SkillType).Value).ToSentence();
            var current = item.GetProperty(slot.Difficulty).Value;
            var ceiling = RecordCeilingIfFirstUse(item, slot, current);

            var delta = RefireStationCommon.RollDelta();
            var updated = RefireStationCommon.ClampDelta(current, delta, ceiling);

            item.SetProperty(slot.Difficulty, updated);
            item.ChangesDetected = true;
            item.SaveBiotaToDatabase();

            RefireStationCommon.UpdateObj(player, item);

            return $"The Defense Requirement Reforge reworks the {item.Name}. {skillName} {current} -> {updated}. 5 Trade Notes (250,000) spent.";
        }

        /// <summary>
        /// Read-only preview of the ceiling for the confirmation prompt - the recorded DefenseWieldOriginal if
        /// this item was reforged before, otherwise its current slot WieldDifficulty. Deliberately does NOT
        /// write the property (see <see cref="RecordCeilingIfFirstUse"/>), so a cancelled confirmation leaves
        /// the item completely untouched.
        /// </summary>
        private static int PeekCeiling(WorldObject item, (PropertyInt Req, PropertyInt SkillType, PropertyInt Difficulty) slot, int current)
        {
            return item.GetProperty(PropertyInt.DefenseWieldOriginal) ?? current;
        }

        /// <summary>
        /// Reads PropertyInt.DefenseWieldOriginal off the item if present; otherwise this is the item's FIRST
        /// reforge, so records the found slot's current WieldDifficulty as the ceiling. Only called from Apply,
        /// after the charge has already succeeded.
        /// </summary>
        private static int RecordCeilingIfFirstUse(WorldObject item, (PropertyInt Req, PropertyInt SkillType, PropertyInt Difficulty) slot, int current)
        {
            var recorded = item.GetProperty(PropertyInt.DefenseWieldOriginal);

            if (recorded.HasValue)
                return recorded.Value;

            item.SetProperty(PropertyInt.DefenseWieldOriginal, current);

            return current;
        }
    }
}
