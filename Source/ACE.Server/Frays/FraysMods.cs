using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Everything that changes one camper's numbers at their fire: their boons, perks and
    /// Snarl Pact, the week's affixes, Fray-only proof set reduction and a Dream Surge. Built once a second by the fire's
    /// tick and hung on the player (<see cref="Player.FraysMods"/>); every combat, Heat, Ember and XP hook reads it.
    /// Null on the player means "not camping": every hook then leaves the numbers alone.
    /// </summary>
    public sealed class FraysCampMods
    {
        public FraysFire Fire;

        // ---- the player's side ----
        public double DamageDealt = 1, DamageTaken = 1;
        public double HeatGain = 1, Embers = 1, DecayMult = 1;
        /// <summary>The key bar's fill (the Sky Banner).</summary>
        public double KeyBar = 1;
        public double PackDamage = 1, GildedDamage = 1, SnarlDamage = 1;   // GildedDamage: Gilded creatures
        public double ManaPer5s;

        /// <summary>Class boons: each one multiplies one class ability's own strength or proc chance while camping.</summary>
        public readonly Dictionary<ACE.Server.ClassAbilities.ClassAbilityId, double> AbilityBoost = new Dictionary<ACE.Server.ClassAbilities.ClassAbilityId, double>();

        /// <summary>Blood Mage AoE boons (FraysBloodBoons): Crimson Splash's extra creatures, and Hecatomb's Reach.</summary>
        public int BloodSplash;
        public bool HecatombReach;

        // proof sets: Fray-only reduction against one element
        public readonly Dictionary<DamageType, double> ProofReduction = new Dictionary<DamageType, double>();

        // ---- the camp's side (monsters, loot, the Snarl) ----
        public double MonsterHealth = 1, MonsterDamage = 1, PackHealth = 1, GildedHealth = 1;
        public int PackDelta;
        /// <summary>Bellows: each pack's chance of one more creature is this times its size, so creatures an hour rise by
        /// about this share at any Heat; not in the rest scaling.</summary>
        public double PackShare;
        public bool NoDecay;
        public int DeathDropLevels = 2;
        public double SnarlBarSpeed = 1;
        /// <summary>The Snarl bar speed every source together gives, capped at 1 + snarl_speed_cap (perk, boons and Ember Stoke share it).</summary>
        public double SnarlSpeed => FraysRules.CapSnarlSpeed(SnarlBarSpeed, FraysSettings.Number(FraysSettings.SnarlSpeedCap));

        public string SurgeName;

        // long-term systems (FraysLongTerm.Apply)
        public double HealPer10s;

        // ---- the Frays bonus pools (every Frays source is sized so maxing them all lands on the cap) ----
        // Bonuses add (+10% and +5% is +15%), then the total is held to the cap. Gear (proof sets, the Slayer Stone),
        // class abilities and ratings sit outside both pools. A Dream Surge (unreachable) still
        // multiplies DamageDealt / DamageTaken outside them.

        /// <summary>Frays damage on every target: Searing, Ascension, the striker companion, the shrine's Fury, Emberblessed, Lucid Night.</summary>
        public double BonusDamage;
        /// <summary>Boon damage: on every target, and on one kind of target. A hit's boon part is held to boon_bonus_cap.</summary>
        public double BoonDamage, BoonPack, BoonGilded, BoonSnarl;
        /// <summary>Frays damage reduction: Ash Guard, the warder companion, the shrine's Ward.</summary>
        public double BonusReduction;
        /// <summary>Boon reduction (Ember Skin on every hit, Iron Ember on physical), held to boon_bonus_cap.</summary>
        public double BoonReduction, BoonPhysicalReduction;

        /// <summary>The damage pool for a hit on a creature with <paramref name="tag"/> (null: no target kind), held to frays_damage_bonus_cap.</summary>
        public double DamageBonusAgainst(FraysCreatureTag tag)
        {
            var kind = tag == null ? 0 : (tag.Snarl || tag.SnarlAdd) ? BoonSnarl : tag.Rich ? BoonGilded : BoonPack;
            return DamagePool(BonusDamage, BoonDamage + kind);
        }

        /// <summary>Pure: the damage pool from its Frays part and its boon part (each boon part held to boon_bonus_cap first).</summary>
        public static double DamagePool(double frays, double boons) =>
            Math.Clamp(Math.Max(0, frays) + Math.Clamp(boons, 0, FraysSettings.Number(FraysSettings.BoonBonusCap)), 0, FraysSettings.Number(FraysSettings.DamageBonusCap));

        /// <summary>The reduction pool for a hit (physical or not), held to damage_reduction_cap.</summary>
        public double ReductionPool(bool physical) =>
            Math.Clamp(Math.Max(0, BonusReduction) + Math.Clamp(BoonReduction + (physical ? BoonPhysicalReduction : 0), 0, FraysSettings.Number(FraysSettings.BoonBonusCap)),
                0, FraysSettings.Number(FraysSettings.DamageReductionCap));

        /// <summary>The pools before their caps, for the readout: damage (best target kind) and reduction (physical).</summary>
        public (double Damage, double Reduction) PoolsBeforeCap() =>
            (Math.Max(0, BonusDamage) + Math.Min(Math.Max(BoonDamage + Math.Max(BoonPack, Math.Max(BoonGilded, BoonSnarl)), 0), FraysSettings.Number(FraysSettings.BoonBonusCap)),
             Math.Max(0, BonusReduction) + Math.Min(Math.Max(0, BoonReduction + BoonPhysicalReduction), FraysSettings.Number(FraysSettings.BoonBonusCap)));

        /// <summary>Builds the modifiers. <paramref name="player"/> may be null (tests): no gear bonuses then.</summary>
        public static FraysCampMods Build(Player player, SnarlPact snarl, FraysAffix shift, string surge)
        {
            var m = new FraysCampMods();

            // affixes
            switch (shift)
            {
                case FraysAffix.HeavyThread: m.MonsterHealth *= 1.4; break;
                case FraysAffix.BrittleDream: m.MonsterHealth *= 0.7; m.MonsterDamage *= 1.15; break;
                case FraysAffix.UnspooledSwarm: m.PackDelta += 1; break;
                case FraysAffix.TangledKings: m.GildedHealth *= 1.5; m.PackDelta -= 1; break;
                case FraysAffix.FrayedMasses: m.PackHealth *= 0.8; m.GildedHealth *= 1.4; break;
                case FraysAffix.Teeming: m.PackDelta += 2; break;
                case FraysAffix.Champions: m.GildedHealth *= 2; break;
            }

            // A Dream Surge: one short boon at a time.
            m.SurgeName = surge;

            switch (surge)
            {
                case "Ember Gust": m.Embers *= 1.25; break;
                case "Keen Dream": m.DamageDealt *= 1.15; break;
                case "Soft Dream": m.DamageTaken *= 0.85; break;
                case "Quick Dream": m.HeatGain *= 1.25; break;
            }

            if (player != null)
                AddProofSets(player, m);

            return m;
        }

        public static readonly string[] Surges = { "Ember Gust", "Keen Dream", "Soft Dream", "Quick Dream" };

        private static readonly (EquipmentSet Set, DamageType Element)[] ProofSets =
        {
            (EquipmentSet.Flameproof, DamageType.Fire),
            (EquipmentSet.Acidproof, DamageType.Acid),
            (EquipmentSet.Coldproof, DamageType.Cold),
            (EquipmentSet.Lightningproof, DamageType.Electric),
        };

        /// <summary>Fray-only proof set reduction: 10 / 15 / 20 / 25% at 2 / 3 / 4 / 5+ pieces, against that element only.</summary>
        public static double ProofFor(int pieces) => pieces >= 5 ? 0.25 : pieces == 4 ? 0.20 : pieces == 3 ? 0.15 : pieces == 2 ? 0.10 : 0;

        private static void AddProofSets(Player player, FraysCampMods m)
        {
            foreach (var (set, element) in ProofSets)
            {
                var pieces = player.EquippedObjects.Values.Count(o => o.EquipmentSetId == set);
                var r = ProofFor(pieces);

                if (r > 0)
                    m.ProofReduction[element] = r;
            }
        }

        // ---- applying them ----

        /// <summary>
        /// A hit from the player (or their pet) on one of their fire's creatures: the boons' target kinds and the
        /// week's resist affixes. <paramref name="weapon"/> may be null (spells from a wand use the wand).
        /// </summary>
        public double OutgoingMult(Creature target, DamageType type, WorldObject weapon, bool fromPet)
        {
            var tag = target?.FraysTag;
            var mult = DamageDealt * (1 + DamageBonusAgainst(tag));

            // A target kind's multiplier (a boon's cost), outside the pool.
            if (tag != null)
            {
                if (tag.Snarl || tag.SnarlAdd)
                    mult *= SnarlDamage;
                else if (tag.Rich)
                    mult *= GildedDamage;
                else
                    mult *= PackDamage;
            }

            return mult;
        }

        /// <summary>
        /// A hit on the player from their own fire's creature, after the Heat multiplier: the reduction pool and
        /// proof sets.
        /// </summary>
        public double IncomingMult(DamageType type, int creaturesOnYou)
        {
            var physical = (type & DamageType.Physical) != 0;

            // The Frays pool (Ash Guard, warder, Ward, boons) is held to damage_reduction_cap (35%). Proof sets are gear:
            // outside it.
            var mult = DamageTaken * (1 - ReductionPool(physical));

            if (!physical)
                mult *= 1 - ProofOf(type);

            return mult;
        }

        /// <summary>The whole reduction a hit gets (the capped pool, then proof sets on top), 0..1.</summary>
        public double ReductionBeforeCap(bool physical, int creaturesOnYou = 0)
        {
            var mult = DamageTaken * (1 - ReductionPool(physical)) * (physical ? 1 : 1 - ProofReduction.Values.DefaultIfEmpty(0).Max());

            return 1 - mult;
        }

        private double ProofOf(DamageType type)
        {
            foreach (var kv in ProofReduction)
            {
                if ((type & kv.Key) != 0)
                    return kv.Value;
            }

            return 0;
        }
    }
}
