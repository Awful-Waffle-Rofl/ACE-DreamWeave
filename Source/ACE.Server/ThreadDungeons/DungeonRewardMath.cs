using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.World;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Reward and mutation arithmetic for a run, pure (PLAN 7, TECH-DESIGN 3.5). Every cap is a parameter
    /// so the tunables are read once by the caller (ThreadDungeonSpawner) and never inside the math.
    /// </summary>
    public static class DungeonRewardMath
    {
        public const string HealthMult = "health_mult";
        public const string DamageRating = "damage_rating";
        public const string CritRating = "crit_rating";
        public const string CritDamageRating = "crit_damage_rating";
        public const string DamageResistRating = "damage_resist_rating";
        public const string CountMult = "count_mult";
        public const string EliteShare = "elite_share";
        public const string RunSpeedMult = "run_speed_mult";
        public const string SalvageAffinity = "salvage_affinity";

        /// <summary>
        /// Writes PropertyFloat.IgnoreShield (151) on the spawned creature; the magnitude is the FRACTION of
        /// the defender's shield the monster ignores. Verified against the combat path on 2026-09-07:
        /// WorldObject_Weapon.GetIgnoreShieldMod reads <c>creatureMod = IgnoreShield ?? 0.0f</c> and takes
        /// Math.Max against the wielded weapon's, so a monster needs no weapon for it to bite, and it is
        /// consumed as <c>attacker.GetIgnoreShieldMod(weapon)</c> from inside the DEFENDER's
        /// Creature_Combat.GetShieldMod - the monster-attacks-player direction.
        ///
        /// KNOWN ASYMMETRY, deliberate: GetShieldMod returns 1.0 immediately when the defender has no shield
        /// equipped, so this is worth exactly nothing against a two-hander or bow build. That is why the
        /// shipped shield_hollow row's reward sits at the low end - a conditional effect must not carry an
        /// unconditional premium.
        /// </summary>
        public const string IgnoreShield = "ignore_shield";

        /// <summary>
        /// Writes PropertyBool.IgnoreMagicArmor (66) and PropertyBool.IgnoreMagicResist (65), both true.
        /// There is NO partial form and the magnitude carries nothing: they are booleans, and the
        /// monster-attacks-player path zeroes the enchantment contribution outright rather than scaling it
        /// (Creature_Combat.cs:735, <c>modSL = attacker is Player ? IgnoreMagicArmorScaled(modSL) : 0</c> -
        /// the scaling branch is PvP-only). Monster_Melee.cs:429-430 reads the monster's OWN flags, so it
        /// works from the monster side. Full hollow only.
        /// </summary>
        public const string Hollow = "hollow";

        /// <summary>
        /// The one kind whose MAGNITUDE IS the lootQuantityMult factor, rather than the row's fixed
        /// <see cref="ModifierDef.LootQuantityMult"/> field. That field cannot be rolled - it is per-row, not
        /// per-gem - so a modifier whose whole point is a ROLLED loot multiplier needs the magnitude to carry
        /// it. <see cref="LootQuantityMultiplier"/> is the single place that substitution happens; nothing
        /// else reads it, so a row of this kind contributes to no creature knob at all (which is also why
        /// DungeonModifierCategories files it as a bonus).
        /// </summary>
        public const string LootQuantity = "loot_quantity";

        /// <summary>
        /// The largest long that survives a round trip through double (2^63 - 1024), used as the upper clamp
        /// bound in <see cref="XpForKill"/>.
        ///
        /// long.MaxValue is the wrong bound to clamp a double against. Widening 9,223,372,036,854,775,807 to
        /// double rounds it UP to 2^63, which is one past the largest representable long, so the bound the
        /// clamp actually enforces is a value the destination type cannot hold and the cast that follows is
        /// left to decide what happens.
        ///
        /// MEASURED, not assumed: with the bound reverted to long.MaxValue this method returns long.MaxValue
        /// rather than long.MinValue, because .NET Core 3.0 and later specify FLOAT-TO-INTEGER CONVERSION AS
        /// SATURATING. So the classic negative-wrap this guard is usually written against does not occur on
        /// this runtime, and the practical difference today is one ULP at a bound nothing can reach. What the
        /// explicit bound buys is that the saturation point is stated here instead of being delegated to a
        /// runtime rule that differed on .NET Framework and Mono, and that the clamp's stated maximum is a
        /// value the return type can actually represent.
        ///
        /// Unreachable with the shipped dials in any case (scale ceiling 20, xp cap 3, RoleRate at most 2, and
        /// the roster's largest XpOverride is 4,000,000).
        /// </summary>
        public const double MaxRepresentableLong = 9223372036854774784.0;

        public static long LadderXp(IReadOnlyList<XpLadderRungDef> ladder, int level)
        {
            if (ladder == null || ladder.Count == 0)
                return 0;
            if (level <= ladder[0].Level)
                return ladder[0].Xp;
            var last = ladder[ladder.Count - 1];
            if (level >= last.Level)
                return last.Xp;

            for (var i = 1; i < ladder.Count; i++)
            {
                var hi = ladder[i];
                if (level > hi.Level) continue;
                var lo = ladder[i - 1];
                var t = (double)(level - lo.Level) / (hi.Level - lo.Level);
                var logXp = Math.Log(lo.Xp) + t * (Math.Log(hi.Xp) - Math.Log(lo.Xp));
                return (long)Math.Round(Math.Exp(logXp));
            }
            return last.Xp;
        }

        public static double RoleRate(DungeonRole role)
        {
            switch (role)
            {
                case DungeonRole.Elite: return 1.5;
                case DungeonRole.Boss: return 2.0;
                default: return 1.0;
            }
        }

        private static double Product(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap,
            Func<ModifierDef, string> kindOf, Func<ModifierDef, double> baseOf, Func<ModifierDef, double> slopeOf,
            Func<ModifierDef, bool> include)
        {
            var product = 1.0;
            foreach (var (id, magnitude) in spec.Modifiers)
            {
                if (!modifiers.TryGetValue(id, out var def)) continue;
                if (include != null && !include(def)) continue;
                var factor = kindOf(def) == "linear" ? baseOf(def) + slopeOf(def) * magnitude : baseOf(def);
                if (double.IsNaN(factor) || factor <= 0) continue;
                product *= factor;
            }
            return Math.Min(product, cap);
        }

        /// <summary>Non-boss modifiers only; every kill.</summary>
        public static double XpMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap)
            => Product(spec, modifiers, cap, d => d.RewardXpKind, d => d.RewardXpBase, d => d.RewardXpSlope, d => d.Target != "boss");

        /// <summary>Boss-target modifiers only. Multiplied on TOP of XpMultiplier for a boss kill.</summary>
        public static double BossXpMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap)
            => Product(spec, modifiers, cap, d => d.RewardXpKind, d => d.RewardXpBase, d => d.RewardXpSlope, d => d.Target == "boss");

        /// <summary>Luminance uses the XP kind ("linear"/"constant") with the lum base/slope fields.</summary>
        public static double LumMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap)
            => Product(spec, modifiers, cap, d => d.RewardLumSlope != 0 ? "linear" : "constant", d => d.RewardLumBase, d => d.RewardLumSlope, null);

        /// <summary>
        /// XP one kill awards, off a base value the CALLER resolved.
        ///
        /// <paramref name="rewardScale"/> is the owner's headline rate (dynamic_dungeons_xp_scale, 2.0 by
        /// default) and it is its OWN term here, deliberately not folded into
        /// <paramref name="xpMultiplier"/>. <see cref="Product"/> ends in Math.Min(product, cap): a scalar
        /// folded in as a synthetic factor would be clipped by dynamic_dungeons_xp_mult_cap, and a plain gem
        /// and a four-modifier gem would then pay exactly the same. It is REQUIRED rather than defaulted for
        /// the same reason - a reward multiplier that silently defaults is a reward multiplier that silently
        /// goes missing.
        ///
        /// <paramref name="baseXp"/> is the drawn creature's own retail worth (see
        /// DungeonPopulationBuilder.BaseXp), so the baseline is exactly 1.0x retail and the scale is
        /// measurable against it.
        /// </summary>
        public static long XpForKill(long baseXp, DungeonRole role, double rewardScale, double xpMultiplier, double bossXpMultiplier)
        {
            var scale = double.IsNaN(rewardScale) || rewardScale < 0 ? 0.0 : rewardScale;

            // Zero is the documented way to disable the axis (see ThreadDungeonSpawner.SanitizeDoubleDial),
            // so it must pay literally nothing rather than being floored to 1 by the Math.Max below.
            if (scale == 0.0)
                return 0;

            var boss = role == DungeonRole.Boss ? bossXpMultiplier : 1.0;
            var xp = baseXp * RoleRate(role) * xpMultiplier * boss * scale;

            // NaN survives Math.Clamp (both of its comparisons are false against NaN) and the cast that
            // follows is then unspecified, so it is caught here rather than left to the floor below. It takes
            // a 0 * Infinity somewhere in the product, which the dial sanitizers make unreachable today.
            if (double.IsNaN(xp))
                return 1;

            return Math.Max(1, (long)Math.Clamp(Math.Round(xp), 0.0, MaxRepresentableLong));
        }

        /// <summary>
        /// Fallback resolver for a caller that has no per-creature base value to hand - the ladder rung at
        /// <paramref name="level"/> stands in for it. The production path resolves the creature's own
        /// XpOverride first and only falls back to this; see DungeonPopulationBuilder.BaseXp.
        /// </summary>
        public static long XpForKill(IReadOnlyList<XpLadderRungDef> ladder, int level, DungeonRole role, double rewardScale, double xpMultiplier, double bossXpMultiplier)
            => XpForKill(LadderXp(ladder, level), role, rewardScale, xpMultiplier, bossXpMultiplier);

        private static IEnumerable<(ModifierDef Def, double Magnitude)> Effects(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds)
        {
            foreach (var (id, magnitude) in spec.Modifiers)
                if (modifiers.TryGetValue(id, out var def))
                    yield return (def, magnitude);

            foreach (var id in extraModifierIds ?? Enumerable.Empty<string>())
                if (modifiers.TryGetValue(id, out var def))
                    yield return (def, def.MinMagnitude);
        }

        public static double HealthMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds)
        {
            var product = 1.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds))
                if (def.MonsterEffectKind == HealthMult && magnitude > 0)
                    product *= magnitude;
            return product;
        }

        /// <summary>
        /// The fraction of the defender's shield the run's creatures ignore, in [0, 1].
        ///
        /// The MAXIMUM across the gem's <see cref="IgnoreShield"/> modifiers, never a sum and never a
        /// product, because that is how the value is consumed: GetIgnoreShieldMod takes
        /// <c>Math.Max(creatureMod, weaponMod)</c> and returns <c>1 - max</c>, so two half-shares are not one
        /// whole. 0 means the axis is untouched and the spawner writes nothing.
        /// </summary>
        public static double IgnoreShieldFraction(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds)
        {
            var best = 0.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds))
                if (def.MonsterEffectKind == IgnoreShield && !double.IsNaN(magnitude) && magnitude > 0)
                    best = Math.Max(best, Math.Clamp(magnitude, 0.0, 1.0));
            return best;
        }

        /// <summary>
        /// Whether the run's creatures ignore magic armor and magic resistance outright. Binary by nature -
        /// see <see cref="Hollow"/> - so the magnitude is not read at all and a second hollow modifier adds
        /// nothing.
        /// </summary>
        public static bool IsHollow(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds)
        {
            foreach (var (def, _) in Effects(spec, modifiers, extraModifierIds))
                if (def.MonsterEffectKind == Hollow)
                    return true;
            return false;
        }

        public static int RatingTotal(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds, string kind)
        {
            var total = 0.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds))
                if (def.MonsterEffectKind == kind)
                    total += magnitude;
            return (int)Math.Round(total);
        }

        /// <summary>
        /// Invariant I2's arithmetic: the health a run's boss must end up with.
        ///
        /// The MAXIMUM of three terms, never a product of them:
        ///   1. <paramref name="bossBaseMax"/>, the boss weenie's own Health.MaxValue - the uplift may only
        ///      ever RAISE a boss, never weaken one the content author already made tough;
        ///   2. that base times <paramref name="bossMult"/>, what the gem's and the boss row's own modifiers
        ///      alone would have given it, so a boss_guarded gem is never worse off for this floor existing;
        ///   3. <paramref name="observedMaxNonBoss"/> times <paramref name="ratio"/>, the floor that carries
        ///      the owner's rule that a boss always outranks its own pack.
        ///
        /// Taking the max rather than multiplying is what keeps this idempotent and bounded: boss_guarded
        /// alone reaches 3.0x and a trash hardy reaches 2.0x, so stacking an unconditional 4x on top would
        /// land a modded level-275 boss near 24x its base health and risk a fight that cannot finish inside
        /// the run's TTL.
        ///
        /// <paramref name="observedMaxNonBoss"/> is OBSERVED, not predicted: a creature's real
        /// Health.MaxValue folds a dat-driven attribute formula the pure builder cannot evaluate, and the
        /// species files' health field is an informational snapshot rather than a runtime value. 0 (nothing
        /// non-boss actually entered the world) simply drops term 3.
        ///
        /// Saturating. A silly ratio cannot wrap the result negative; it clamps at int.MaxValue.
        /// </summary>
        public static int BossHealthTarget(uint bossBaseMax, double bossMult, uint observedMaxNonBoss, double ratio)
        {
            var fromModifiers = double.IsNaN(bossMult) || bossMult <= 0 ? bossBaseMax : bossBaseMax * bossMult;
            var fromFloor = double.IsNaN(ratio) || ratio <= 0 ? 0.0 : (double)observedMaxNonBoss * ratio;

            var target = Math.Max(bossBaseMax, Math.Max(fromModifiers, fromFloor));

            return (int)Math.Clamp(Math.Round(target), 1.0, int.MaxValue);
        }

        /// <summary>
        /// The gem-level reward-scale ratio: clamp((level / anchor) ^ exponent, floor, cap). <paramref name="cap"/>
        /// of 0 or less means uncapped, so growth above the anchor is unbounded until an admin sets an
        /// explicit safety valve.
        ///
        /// Defensively re-sanitizes its own inputs the same way <see cref="XpForKill"/> re-sanitizes
        /// <c>rewardScale</c> - the caller (ThreadDungeonSpawner) already sanitizes the live tunables before
        /// they reach here, but a limits struct built directly in code (every unit test) has no such caller,
        /// so a garbage value must not propagate a NaN into the arithmetic below.
        /// </summary>
        public static double RewardScaleRatio(int level, double anchor, double exponent, double floor, double cap)
        {
            var safeAnchor = double.IsNaN(anchor) || double.IsInfinity(anchor) || anchor <= 0
                ? DungeonPopulationLimits.DefaultRewardScaleAnchor
                : anchor;

            var safeExponent = double.IsNaN(exponent) || double.IsInfinity(exponent)
                ? DungeonPopulationLimits.DefaultRewardScaleExponent
                : exponent;

            var safeFloor = double.IsNaN(floor) || double.IsInfinity(floor) || floor < 0
                ? DungeonPopulationLimits.DefaultRewardScaleFloor
                : floor;

            var raw = Math.Pow(level / safeAnchor, safeExponent);
            var ratio = double.IsNaN(raw) ? safeFloor : Math.Max(raw, safeFloor);

            if (!double.IsNaN(cap) && !double.IsInfinity(cap) && cap > 0)
                ratio = Math.Min(ratio, cap);

            return ratio;
        }

        /// <summary>
        /// Applies a reward-scale ratio to a MULTIPLICATIVE axis - one whose neutral (unscaled) value is 1.0,
        /// such as an XP or luminance scalar or a loot-quantity multiplier: effective = 1 + ratio * (raw - 1).
        ///
        /// Never <c>ratio * raw</c>. At a low ratio a 2.0x scalar folded that way would become LESS than 1.0
        /// - i.e. a low-level Thread would pay less than not running one at all, which inverts the intent of
        /// a reward-scale curve. This form leaves a ratio of 1.0 (at or above the anchor, uncapped) exactly
        /// equal to <paramref name="raw"/>, and a ratio of 0.0 exactly equal to the neutral value 1.0.
        /// </summary>
        public static double ApplyMultiplicativeScale(double ratio, double raw) => 1.0 + ratio * (raw - 1.0);

        /// <summary>
        /// Applies a reward-scale ratio to an ADDITIVE axis - one whose neutral (unscaled) value is 0.0, such
        /// as the loot-quality bonus or the salvage-affinity per-kill chance: effective = ratio * raw.
        /// </summary>
        public static double ApplyAdditiveScale(double ratio, double raw) => ratio * raw;

        public static double RunValue(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, string kind, double fallback)
        {
            foreach (var (id, magnitude) in spec.Modifiers)
                if (modifiers.TryGetValue(id, out var def) && def.MonsterEffectKind == kind)
                    return magnitude;
            return fallback;
        }

        /// <summary>
        /// The salvage affinities a gem carries: for each "salvage_affinity" modifier on it, the material,
        /// the ordinary weenie the injected item is made from, and the PER-KILL probability of injecting one.
        ///
        /// This method is the ONE place a magnitude in percent becomes a probability in [0, 1]. Not at the
        /// JSON boundary, where the number a designer types and the number the appraisal panel prints would
        /// then disagree, and emphatically not twice.
        ///
        /// Deterministic: the list follows the gem's own modifier order, so two calls for the same gem return
        /// the same list in the same order. The roll itself is NOT taken here - it happens per kill, off
        /// ThreadSafeRandom at the death path, so the gem's seeded Random is untouched and the population plan
        /// stays bit-identical to a gem without any affinity modifier.
        /// </summary>
        public static IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> SalvageAffinities(
            DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var affinities = new List<(int, uint, double)>();

            foreach (var (id, magnitude) in spec.Modifiers)
            {
                if (!modifiers.TryGetValue(id, out var def)) continue;
                if (def.MonsterEffectKind != SalvageAffinity) continue;

                // A row that survived store validation always has a non-zero wcid; re-checked here so a
                // hand-built store in a test cannot inject wcid 0 into the death path.
                if (def.SalvageBaseWcid == 0) continue;

                var chance = double.IsNaN(magnitude) ? 0.0 : Math.Clamp(magnitude, 0.0, 100.0) / 100.0;

                if (chance <= 0.0) continue;

                affinities.Add((def.SalvageMaterial, def.SalvageBaseWcid, chance));
            }

            return affinities;
        }

        public static double LootQualityBonus(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var total = 0.0;
            foreach (var (id, _) in spec.Modifiers)
                if (modifiers.TryGetValue(id, out var def))
                    total += def.LootQualityBonus;
            return total;
        }

        /// <summary>
        /// How many times as much loot a kill drops, as the PRODUCT of the gem's lootQuantityMult values,
        /// clamped to [1, cap]. Never below 1: a modifier cannot make a run drop less than the base profile,
        /// so a nonsensical row (0, negative, NaN) is skipped rather than shrinking the reward.
        ///
        /// A <see cref="LootQuantity"/>-kind row contributes its ROLLED MAGNITUDE here instead of its
        /// lootQuantityMult field, and this is the only place that substitution happens. It is an
        /// either/or, never both: lootQuantityMult defaults to 1.0 on such a row, so reading both would be
        /// harmless today but would silently double-count the moment someone set the field as well.
        /// </summary>
        public static double LootQuantityMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap)
        {
            var product = 1.0;
            foreach (var (id, magnitude) in spec.Modifiers)
            {
                if (!modifiers.TryGetValue(id, out var def)) continue;
                var factor = def.MonsterEffectKind == LootQuantity ? magnitude : def.LootQuantityMult;
                if (double.IsNaN(factor) || factor <= 0) continue;
                product *= factor;
            }
            return Math.Clamp(product, 1.0, Math.Max(1.0, cap));
        }

        /// <summary>
        /// In-memory TreasureDeath profile for a run's creatures. Id 0 / TreasureType 0 mark it as never
        /// coming from the world DB; Creature.DeathTreasureOverride (Task 7) hands it to the loot factory in
        /// place of the weenie's DeathTreasureType. Gear ratings only roll at Tier 8, so tierCap is the lever
        /// that keeps them out of the economy (PLAN 7.2).
        /// </summary>
        /// <param name="lootQuantityMult">
        /// From <see cref="LootQuantityMultiplier"/>, already clamped. It scales the MAXIMUM item counts only,
        /// rounding up, and never the minimums: a gem raises a run's loot ceiling rather than its floor, so a
        /// modified run can still roll a thin kill. 1.0 leaves the profile exactly as it was.
        /// </param>
        public static TreasureDeath BuildProfile(int tier, double lootQualityMod, double qualityCap, int tierCap,
            double lootQuantityMult = 1.0)
        {
            var q = double.IsNaN(lootQuantityMult) ? 1.0 : Math.Max(1.0, lootQuantityMult);
            int Scale(int baseMax) => (int)Math.Clamp(Math.Ceiling(baseMax * q), baseMax, 100);

            return new TreasureDeath
            {
                Id = 0,
                TreasureType = 0,
                Tier = Math.Clamp(Math.Min(tier, tierCap), 1, 8),
                LootQualityMod = (float)Math.Clamp(Math.Min(lootQualityMod, qualityCap), 0, 1),
                UnknownChances = 21,
                ItemChance = 100,
                ItemMinAmount = 1,
                ItemMaxAmount = Scale(2),
                ItemTreasureTypeSelectionChances = 8,
                MagicItemChance = 100,
                MagicItemMinAmount = 1,
                MagicItemMaxAmount = Scale(2),
                MagicItemTreasureTypeSelectionChances = 8,
                MundaneItemChance = 100,
                MundaneItemMinAmount = 1,
                MundaneItemMaxAmount = Scale(1),
                MundaneItemTypeSelectionChances = 7,
            };
        }
    }
}
