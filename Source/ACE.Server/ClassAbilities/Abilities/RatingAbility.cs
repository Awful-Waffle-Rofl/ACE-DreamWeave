using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;
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
        /// The rank that grants exactly <paramref name="bonus"/> (the inverse of <see cref="BonusForRank"/>),
        /// or 0 for any value the table does not produce - including 0 itself, which is what a suppressed or
        /// unlearned ability reports.
        /// </summary>
        public static int RankForBonus(int bonus)
        {
            if (bonus <= 0)
                return 0;

            for (var rank = 1; rank < RatingBonus.Length; rank++)
            {
                if (RatingBonus[rank] == bonus)
                    return rank;
            }

            return 0;
        }

        /// <summary>
        /// OPTIONAL SECONDARY CONVERSION: the tunable-key prefix (the rank number 1-3 is appended) for the
        /// fraction of this entry's OWN granted rating that ALSO counts as Damage Rating, or null for none.
        /// Today only Heal Boost Rating carries one. Converted from the granted rating alone - never from gear,
        /// enchantments or aug ratings in the same pool - and read through Player.GetClassAbilityRating, so
        /// PK/PvP suppression (which zeroes the granted rating) zeroes the conversion with it.
        /// </summary>
        public string DamageConversionTunablePrefix { get; }

        /// <summary>
        /// Converted Damage Rating from a granted rating and a fraction, rounded to the nearest whole point
        /// with halves away from zero (0.30*3 -> 1, 0.60*6 -> 4, 1.00*10 -> 10 at the defaults). 0 when either
        /// input is not positive. Pure.
        /// </summary>
        public static int ConvertedRating(int grantedRating, double fraction)
        {
            if (grantedRating <= 0 || fraction <= 0.0)
                return 0;

            return (int)Math.Round(grantedRating * fraction, MidpointRounding.AwayFromZero);
        }

        /// <summary>The tunable-key prefix (rank 1-3 appended) for Heal Boost Rating's Damage Rating conversion.</summary>
        public const string HealBoostDamageConversionPrefix = "class_ability_healboostrating_damage_conversion_r";

        /// <summary>The conversion fraction at a rank (0 at rank 0, or when this entry has no conversion).</summary>
        public double DamageConversionFraction(int rank)
        {
            if (DamageConversionTunablePrefix == null || rank <= 0)
                return 0.0;

            var clamped = Math.Clamp(rank, 1, EnhancedStatAbility.MaxTier);

            return PropertyManager.GetDouble(DamageConversionTunablePrefix + clamped).Item;
        }

        /// <summary>
        /// Damage Rating converted from this entry's granted rating as GetClassAbilityRating reported it (so a
        /// suppressed player's 0 converts to 0). The rank is recovered from the granted value.
        /// </summary>
        public int DamageRatingConversionForGranted(int grantedRating)
        {
            if (DamageConversionTunablePrefix == null)
                return 0;

            return ConvertedRating(grantedRating, DamageConversionFraction(RankForBonus(grantedRating)));
        }

        /// <summary>
        /// Heal Boost Rating's conversion into Damage Rating for the rating the player is CURRENTLY granted,
        /// read at Creature.GetDamageRating. 0 when the entry is missing or carries no conversion.
        /// </summary>
        public static int HealBoostDamageRatingConversion(int grantedHealBoostRating)
        {
            return ClassAbilityRegistry.GetHandler(ClassAbilityId.HealBoostRating) is RatingAbility hbr
                ? hbr.DamageRatingConversionForGranted(grantedHealBoostRating)
                : 0;
        }

        /// <summary>
        /// The tunable-key prefix (rank 1-3 appended) for Damage Rating's CRIT DAMAGE conversion: the fraction
        /// of the player's TOTAL Critical Damage Rating (Creature.GetCritDamageRating - gear, enchantments,
        /// augs, lum augs and the Crit Damage Rating class ability alike) that Damage Rating adds to Damage
        /// Rating. Berserker pass 2026-10-24 (owner ruling), 30%/60%/100% by rank at the defaults - the same
        /// ladder and the same rounding as Heal Boost Rating's conversion above.
        /// </summary>
        public const string DamageRatingCritDamageConversionPrefix = "class_ability_damagerating_critdamage_conversion_r";

        /// <summary>
        /// Damage Rating's crit-damage conversion for the player's CURRENT grant and total Crit Damage Rating,
        /// read at Creature.GetDamageRating. The rank is recovered from <paramref name="grantedDamageRating"/>
        /// exactly as GetClassAbilityRating reported it, so PK-facet / PvP-arena suppression (which zeroes the
        /// grant) zeroes the conversion with it - the ability stays PvE-only by the same gate as its own +3/6/10.
        /// A negative total crit damage rating converts to 0, never to a penalty. Rounded half away from zero.
        /// </summary>
        public static int DamageRatingCritDamageConversion(int grantedDamageRating, int totalCritDamageRating)
        {
            var rank = RankForBonus(grantedDamageRating);

            if (rank <= 0 || totalCritDamageRating <= 0)
                return 0;

            var fraction = PropertyManager.GetDouble(DamageRatingCritDamageConversionPrefix + Math.Clamp(rank, 1, EnhancedStatAbility.MaxTier)).Item;

            return ConvertedRating(totalCritDamageRating, fraction);
        }

        /// <summary>
        /// The tunable-key prefix (rank 1-3 appended) for Crit Resist Rating's DAMAGE conversion: the fraction
        /// of the player's TOTAL Critical Resist Rating (Creature.GetCritResistRating - gear, augs, enchantments
        /// and the Crit Resist Rating class grant alike) that also counts as Damage Resist Rating. Vanguard pass
        /// 2026-11-06 (owner ruling), retargeted from Damage Rating to Damage Resist Rating 2026-11-08, 30%/60%/100% by rank at the defaults - the same ladder and rounding as
        /// <see cref="DamageRatingCritDamageConversion"/>.
        /// </summary>
        public const string CritResistRatingDamageConversionPrefix = "class_ability_critresistrating_damage_conversion_r";

        /// <summary>
        /// Crit Resist Rating's damage conversion for the player's CURRENT grant and total Critical Resist
        /// Rating, read at Creature.GetDamageResistRating. The rank is recovered from
        /// <paramref name="grantedCritResistRating"/> exactly as GetClassAbilityRating reported it, so PK-facet /
        /// PvP-arena suppression (which zeroes the grant) zeroes the conversion with it. A negative total
        /// converts to 0, never to a penalty. Rounded half away from zero.
        /// </summary>
        public static int CritResistRatingDamageConversion(int grantedCritResistRating, int totalCritResistRating)
        {
            var rank = RankForBonus(grantedCritResistRating);

            if (rank <= 0 || totalCritResistRating <= 0)
                return 0;

            var fraction = PropertyManager.GetDouble(CritResistRatingDamageConversionPrefix + Math.Clamp(rank, 1, EnhancedStatAbility.MaxTier)).Item;

            return ConvertedRating(totalCritResistRating, fraction);
        }

        /// <summary>
        /// The tunable-key prefix (rank 1-3 appended) for Damage Resist Rating's THORNS conversion: the
        /// fraction of the player's total Damage Resist Rating that scales a Thorns reflect, as a multiplier
        /// of 1 + fraction x totalDRR / 100. 0.50 / 1.00 / 1.50 by rank at the defaults.
        /// </summary>
        public const string DamageResistRatingThornsConversionPrefix = "class_ability_damageresistrating_thorns_conversion_r";

        /// <summary>
        /// The multiplier a Thorns reflect takes from Damage Resist Rating: 1 + fraction x total / 100. The
        /// rank is recovered from <paramref name="grantedDamageResistRating"/> as GetClassAbilityRating reported
        /// it, so suppression (grant 0) yields exactly 1.0. A non-positive total yields 1.0, never below it.
        /// </summary>
        public static double DamageResistRatingThornsMultiplier(int grantedDamageResistRating, int totalDamageResistRating)
        {
            var rank = RankForBonus(grantedDamageResistRating);

            if (rank <= 0 || totalDamageResistRating <= 0)
                return 1.0;

            var fraction = PropertyManager.GetDouble(DamageResistRatingThornsConversionPrefix + Math.Clamp(rank, 1, EnhancedStatAbility.MaxTier)).Item;

            if (fraction <= 0.0)
                return 1.0;

            return 1.0 + fraction * totalDamageResistRating / 100.0;
        }

        /// <summary>
        /// Mirrors BonusForRank() above - a flat addition into the rating pool, so Unit is "" (flat). No
        /// affinity rider and no gear mod exists for this family in the shipped registry, and there is no
        /// cap. An entry with a damage conversion reports it as a Secondary, mirroring
        /// <see cref="DamageRatingConversionForGranted"/> on this rank's own grant.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var granted = BonusForRank(rank);
            var skill = (double)granted;

            List<ClassAbilityReadout> secondary = null;

            if (DamageConversionTunablePrefix != null)
            {
                var converted = (double)ConvertedRating(granted, DamageConversionFraction(rank));

                secondary = new List<ClassAbilityReadout>
                {
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = converted,
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = converted,
                        Unit = "",
                        Label = "dmg rating",
                        Per = null,
                        CapNote = null,
                    },
                };
            }

            // Damage Rating (Berserker T3, 2026-10-24): its crit damage conversion as a Secondary, mirroring
            // DamageRatingCritDamageConversion on THIS rank's grant and the player's live total Crit Damage
            // Rating (0 with no player, so the segment then reads 0).
            if (Definition.Id == ClassAbilityId.DamageRating)
            {
                var converted = (double)DamageRatingCritDamageConversion(granted, player?.GetCritDamageRating() ?? 0);

                secondary = new List<ClassAbilityReadout>
                {
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = converted,
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = converted,
                        Unit = "",
                        Label = "dmg rating from crit dmg",
                        Per = null,
                        CapNote = null,
                    },
                };
            }

            // Crit Resist Rating (Vanguard, 2026-11-06): its crit resist conversion as a Secondary, on THIS
            // rank's grant and the player's live total Critical Resist Rating (0 with no player).
            if (Definition.Id == ClassAbilityId.CritResistRating)
            {
                var converted = (double)CritResistRatingDamageConversion(granted, player?.GetCritResistRating() ?? 0);

                secondary = new List<ClassAbilityReadout>
                {
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = converted,
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = converted,
                        Unit = "",
                        Label = "dmg resist rating from crit resist",
                        Per = null,
                        CapNote = null,
                    },
                };
            }

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
                Secondary = secondary,
            };
        }

        public ClassAbilityDefinition Definition { get; }

        private RatingAbility(ClassAbilityDefinition definition, string damageConversionTunablePrefix)
        {
            Definition = definition;
            DamageConversionTunablePrefix = damageConversionTunablePrefix;
        }

        /// <summary>
        /// The rank's grant ("+3" / "+6" / "+10"): the {fn:grant} formula, which reads <see cref="RatingBonus"/> (the table
        /// the grant is applied from), with an optional per-rank rider appended after it.
        /// </summary>
        private static string[] RankTails(Func<int, string> rider = null) =>
            Enumerable.Range(1, EnhancedStatAbility.MaxTier)
                .Select(rank => "+{fn:grant|int}" + (rider == null ? string.Empty : rider(rank)))
                .ToArray();

        /// <summary>The {fn:grant} formula every rating tail uses: a rank's grant, straight from <see cref="BonusForRank"/>.</summary>
        private static readonly TextFormula GrantFormula = TextFormula.Constant(rank => BonusForRank(rank));

        /// <summary>The structural wording of the Thorns multiplier, 1 + fraction x DRR / 100, as the one literal a tail may type.</summary>
        private static (string Literal, string Reason)[] ThornsFormulaLiterals() =>
            new[] { ("x(1 + ", "structural wording of the Thorns multiplier formula, 1 + fraction x DRR / 100 (the owner's format)") };

        private static IReadOnlyDictionary<string, TextFormula> WithGrant(IReadOnlyDictionary<string, TextFormula> formulas)
        {
            var all = new Dictionary<string, TextFormula>(formulas ?? new Dictionary<string, TextFormula>()) { ["grant"] = GrantFormula };
            return all;
        }

        private static RatingAbility Make(ClassAbilityId id, ClassAbilityClass abilityClass, int tier, string name,
            string displayName, string description, string[] tails, int[] cost, string damageConversionTunablePrefix = null,
            IReadOnlyDictionary<string, TextFormula> textFormulas = null, (string Literal, string Reason)[] allowedLiterals = null)
        {
            var definition = new ClassAbilityDefinition
            {
                Id = id,
                AbilityClass = abilityClass,
                Tier = tier,
                Name = name,
                DisplayName = displayName,
                DescriptionTemplate = description,
                RankTailTemplate = tails,
                TextFormulas = tails == null ? textFormulas : WithGrant(textFormulas),
                AllowedLiterals = allowedLiterals,
                MaxRank = EnhancedStatAbility.MaxTier,
                CostPerRank = cost,
                Implemented = true,
                Category = "Rating",
            };
            return new RatingAbility(definition, damageConversionTunablePrefix);
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
                // premium reprice 2026-09-13: off the 1/2/3 escalator onto flat 2/rank. Archer pass
                // (reprice wave 5): back down to flat 1/rank.
                "Raises your Critical Hit Rating, so you land critical hits more often.", RankTails(), new[] { 1, 1, 1 });
            yield return Make(ClassAbilityId.CritDamageRating, ClassAbilityClass.Rogue, 3, "crit_damage_rating", "Crit Damage Rating",
                "Raises your Critical Damage Rating, so your critical hits land harder.", RankTails(), new[] { 1, 1, 1 });
            yield return Make(ClassAbilityId.DamageResistRating, ClassAbilityClass.Vanguard, 3, "damage_resist_rating", "Damage Resist Rating",
                // Vanguard pass 2026-11-06 (owner ruling): scales the Thorns reflect, read in Player.ApplyThornsReflect.
                // The rider is 1 + fraction x DRR / 100, so "per DRR" is the fraction as a percentage; the tail renders
                // it as the raw fraction (0.5 -> "0.5%" per DRR point) from class_ability_damageresistrating_thorns_conversion_r (read in DamageResistRatingThornsMultiplier).
                "Raises your Damage Resistance Rating, so you take less damage. It also boosts Thorns damage for each point of Damage Resist Rating you have. " +
                "Does nothing without Thorns or the Thorns gear mod.",
                RankTails(rank => $", Thorns x(1 + {{ranks:{DamageResistRatingThornsConversionPrefix}|num}}% per DRR)"),
                new[] { 1, 1, 1 },
                allowedLiterals: ThornsFormulaLiterals());
            yield return Make(ClassAbilityId.CritResistRating, ClassAbilityClass.Vanguard, 2, "crit_resist_rating", "Crit Resist Rating",
                // premium reprice 2026-09-13: 1/rank -> 2/rank, REVERSED 2026-11-06 (Vanguard pass, owner ruling):
                // back to 1/rank, with a damage conversion added (ClassAbilityRepricing wave 7 keeps owners' ranks
                // and returns the difference).
                // The mechanic (owner ruling, PR #2011): the fraction of the player's total Critical Resist Rating that also
                // counts as Damage Resist Rating, class_ability_critresistrating_damage_conversion_r, applied in GetDamageResistRating.
                "Raises your Critical Resistance Rating, so you are critically hit less often. It also adds part of your Critical Resist Rating to your Damage Resist Rating.",
                RankTails(rank => $", +{{ranks:{CritResistRatingDamageConversionPrefix}|pct}}% of it as DRR"),
                new[] { 1, 1, 1 });
            yield return Make(ClassAbilityId.DamageRating, ClassAbilityClass.Berserker, 3, "damage_rating", "Damage Rating",
                // premium reprice 2026-09-13: 1/rank -> 2/rank, REVERSED 2026-10-24 (Berserker pass, owner
                // ruling): back to 1/rank, with a crit damage conversion added (ClassAbilityRepricing wave 3
                // keeps owners' ranks and returns the difference).
                // The conversion fraction is class_ability_damagerating_critdamage_conversion_r (DamageRatingCritDamageConversion).
                "Raises your Damage Rating, which boosts all damage you deal. It also adds part of your total Critical Damage Rating to your Damage Rating.",
                RankTails(rank => $", plus {{ranks:{DamageRatingCritDamageConversionPrefix}|pct}}% of Critical Damage Rating"),
                new[] { 1, 1, 1 });
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
                "Raises your Healing Boost Rating, so healing you receive is stronger. It also adds part of that rating to your Damage Rating.",
                // The Damage Rating rider is ConvertedRating(grant, fraction) with the class_ability_healboostrating_damage_conversion_r
                // fraction (3 x 0.30 -> 1, 6 x 0.60 -> 4, 10 x 1.00 -> 10 at the defaults), computed by the formula below.
                RankTails(rank => " (+{fn:damage_rating_gain|int} Damage Rating)"),
                new[] { 1, 1, 1 },
                // Blood Mage buff pass: the ability's OWN granted rating also counts as Damage Rating at
                // 30%/60%/100% by rank (read at Creature.GetDamageRating). No affinity - generated rating
                // entries stay unscaled.
                damageConversionTunablePrefix: HealBoostDamageConversionPrefix,
                textFormulas: new Dictionary<string, TextFormula>
                {
                    // The same ConvertedRating the mechanic applies (DamageRatingConversionForGranted), at this rank's grant and fraction.
                    ["damage_rating_gain"] = new TextFormula(
                        (values, rank) => ConvertedRating(BonusForRank(rank), values.GetDouble(HealBoostDamageConversionPrefix + Math.Clamp(rank, 1, EnhancedStatAbility.MaxTier))),
                        Enumerable.Range(1, EnhancedStatAbility.MaxTier).Select(r => HealBoostDamageConversionPrefix + r).ToArray()),
                });
        }
    }
}
