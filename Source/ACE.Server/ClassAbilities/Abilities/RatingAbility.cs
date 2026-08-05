using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// A passive class ability that adds a flat amount (+3/6/10 by rank) into one of ACE's existing rating
    /// pools - the same additive pools gear ratings and LumAug auras feed, so it stacks cleanly within
    /// its axis. Like the Enhanced-stat family it needs no combat hook: the bonus is read at the matching
    /// GetXRating() choke point in Creature_Rating via Player.GetClassAbilityRating. Deliberately unscaled
    /// (ratings stay clean and predictable - SKILL-TABLES-PREVIEW cross-class notes).
    /// </summary>
    public class RatingAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        /// <summary>Rating granted at each rank (0 = unlearned): +3/6/10, matching the rating family in the tables.</summary>
        public static readonly int[] RatingBonus = { 0, 3, 6, 10 };

        public static int BonusForRank(int rank) => RatingBonus[Math.Clamp(rank, 0, EnhancedStatAbility.MaxTier)];

        /// <summary>
        /// Mirrors BonusForRank() above - a flat addition into the rating pool, so Unit is "" (flat). No
        /// affinity rider and no gear mod exists for this family in the shipped registry, and there is no
        /// cap.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (double)BonusForRank(rank);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = skill,
                Unit = "",
                Label = "rating",
                Per = null,
                CapNote = null,
            };
        }

        public ClassAbilityDefinition Definition { get; }

        private RatingAbility(ClassAbilityDefinition definition)
        {
            Definition = definition;
        }

        private static readonly int[] RatingCost = { 2, 3, 4 };

        private static RatingAbility Make(ClassAbilityId id, ClassAbilityClass abilityClass, int tier, string name,
            string displayName, string effect)
        {
            var definition = new ClassAbilityDefinition
            {
                Id = id,
                AbilityClass = abilityClass,
                Tier = tier,
                Name = name,
                DisplayName = displayName,
                Description = $"Adds +3, then +6, then +10 at ranks 1-3 to your {effect}.",
                MaxRank = EnhancedStatAbility.MaxTier,
                CostPerRank = RatingCost,
                Implemented = true,
                Category = "Rating",
            };
            return new RatingAbility(definition);
        }

        public static IEnumerable<RatingAbility> GenerateAll()
        {
            // CLIENT DISPLAY - three of these pools are INVISIBLE on the player's own panel, and no server
            // change can alter that. Do not tell a player they can read one of these ranks off their
            // character. VERIFIED 2026-08-03 against acclient.exe (the ToD client this server targets):
            //
            // The player/creature assess panel - the same function that draws Head/Chest/Groin armor levels,
            // allegiance, deaths and titles - fetches THIRTEEN rating PropertyInts (307, 308, 313, 314, 315,
            // 316, 323, 350, 351, 381, 382, 386, 387) and then formats exactly FIVE two-value lines from
            // them, so ten of the thirteen are consumed and three are dropped on the floor:
            //   "Dmg/CritDmg" + "Rating: %d/%d"   DamageRating 307      / CritDamageRating 314
            //   "Dmg/CritDmg" + "Resist: %d/%d"   DamageResistRating 308 / CritDamageResistRating 316
            //   "PK Dmg/Res"  + "Rating: %d/%d"   PKDamageRating 381    / PKDamageResistRating 382
            //   "Overpower %" + "+%d/-%d"         Overpower 386         / OverpowerResist 387
            //   "DoT/Life:"   + "Resist: %d/%d"   DotResistRating 350   / LifeResistRating 351
            // (the label-to-id pairings are read off the labels and the fetch order; the VERIFIED part is
            // the thirteen-id read set and the five format strings.)
            //
            // The three with no line are CritRating (313), CritResistRating (315) and HealingBoostRating
            // (323). 313 and 315 are not merely unread - they are read as VISIBILITY GATES only: a nonzero
            // CritRating makes the first line appear, but the two numbers that line prints are 307 and 314
            // (0x004B5E7B gates on 313, 0x004B5EF8/0x004B5EFC load 314 and 307 into the format call).
            // HealingBoostRating 323 is fetched into a stack slot that is never read again - dead client
            // code, so the client evidently intended a Heal Boost line and never wired it. A whole-.text
            // scan for immediate loads of each id finds exactly two sites per id, one in each of the two
            // creature renderers, so there is no third one; and no label string for them exists in the
            // binary.
            //
            // There are two creature renderers, not two copies of one: 0x004B4690 draws monsters/NPCs and
            // 0x004B4EF0 draws players (the latter adds Society, Fellowship, Allegiance, Monarch/Patron,
            // Deaths, Time in Dereth, Chess Rank, Enlightenment). Both drop the same three ids.
            //
            // WHY THE GEAR* IDS CANNOT BE BORROWED. This is the part that looks like a way out and is not.
            // The "Ratings: ... Crit %d ... Crit Resist %d ... Heal Boost %d" block a player sees on GEAR is
            // a THIRD renderer (0x004B7ED0, whose rating section is 0x004AF400) reading only the Gear* ids
            // 370-379, 383, 384, 388, 389 - which include GearCrit 372, GearCritResist 373 and
            // GearHealingBoost 376, the exact 1:1 partners of the three missing pools. The blocker is not
            // which ids that renderer reads. It is that it never runs for a player. The assess panel owns
            // all three renderers and invokes exactly ONE per appraisal, through a single virtual call at
            // 0x004ADFC7, after this selection at 0x004ADEC5-0x004ADF3D:
            //     if      (!qualities.GetCreatureProfile(&cp))          -> ITEM    renderer 0x004B7ED0
            //     else if (qualities.GetStringStat(Template 5))         -> PLAYER  renderer 0x004B4EF0
            //     else if (qualities.GetIntStat(CharacterTitleId 261))  -> PLAYER  renderer 0x004B4EF0
            //     else                                                 -> MONSTER renderer 0x004B4690
            // AppraiseInfo.BuildCreature always constructs a CreatureProfile for every Creature, players
            // included, so the first test can never fail for a player and the Gear* block is unreachable
            // regardless of which ids the packet carries. Putting GearCrit / GearCritResist /
            // GearHealingBoost into a creature appraisal is a silent no-op, not a display.
            //
            // ACE is already doing its part: AppraiseInfo.AddRatings sends all of these on a self-assess
            // (which always succeeds - Player.Examine forces chance to 1.0 when player == this). The client
            // receives HealingBoostRating and discards it. The effect is still real and measurable in play -
            // for Heal Boost at rank 3, a healing kit used on yourself heals 10% more
            // (Healer.GetHealAmount -> target.GetHealingRatingMod).
            yield return Make(ClassAbilityId.CritRating, ClassAbilityClass.Archer, 3, "crit_rating", "Crit Rating",
                "Critical Hit Rating (higher critical hit chance)");
            yield return Make(ClassAbilityId.CritDamageRating, ClassAbilityClass.Rogue, 2, "crit_damage_rating", "Crit Damage Rating",
                "Critical Damage Rating (harder critical hits)");
            yield return Make(ClassAbilityId.DamageResistRating, ClassAbilityClass.Vanguard, 2, "damage_resist_rating", "Damage Resist Rating",
                "Damage Resistance Rating (less damage taken)");
            yield return Make(ClassAbilityId.CritResistRating, ClassAbilityClass.Vanguard, 3, "crit_resist_rating", "Crit Resist Rating",
                "Critical Resistance Rating (lower chance of being critically hit)");
            yield return Make(ClassAbilityId.DamageRating, ClassAbilityClass.Berserker, 3, "damage_rating", "Damage Rating",
                "Damage Rating (all damage you deal)");
            // Blood Mage T2. The rating pool is HealingBoostRating, homed here from the unhomed pool - the
            // life/healing class is where DESIGN.md always meant it to go (BLOOD-MAGE-DESIGN.md sec 3).
            // Read at Creature.GetHealingBoostRating, like every other member of this family.
            //
            // Direction check, because the name reads ambiguous: this pool is consumed RECEIVER-side. Every
            // call site of GetHealingRatingMod reads it off the creature BEING healed - Healer.cs:292 uses
            // target.GetHealingRatingMod(), Creature_Properties' HealthBoost/StaminaBoost/ManaBoost
            // resistance branches are the target's own, Food.cs is the eater's, and EnchantmentManager's
            // heal-over-time tick multiplies by the enchanted creature's. So a rank makes healing the owner
            // RECEIVES stronger; it does not amplify heals they cast on someone else.
            yield return Make(ClassAbilityId.HealBoostRating, ClassAbilityClass.BloodMage, 2, "heal_boost_rating", "Heal Boost Rating",
                "Healing Boost Rating (healing you receive is stronger)");
        }
    }
}
