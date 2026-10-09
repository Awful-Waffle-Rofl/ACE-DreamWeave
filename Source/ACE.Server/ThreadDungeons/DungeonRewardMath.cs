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
        /// The monsterEffectKinds a target: "boss" modifier row is allowed to carry, enforced at load time by
        /// ThreadDungeonStore next to its Target validation. Each of HealthMult, DamageRating and
        /// DamageResistRating has a dedicated Boss* field on DungeonSpawnPlan and a spawner branch that reads
        /// it (DungeonPopulationBuilder's three forBoss: true call sites); "none" is included too because no
        /// Effects consumer ever matches kind "none" against anything, so a target: "boss" row of that kind
        /// contributes nothing to Effects either way - it is how a BOSS-ONLY reward multiplier (pure XP/lum
        /// bonus, no monster effect) is authored, and that axis already reads Target directly in XpMultiplier/
        /// BossXpMultiplier/LumMultiplier's own split, independent of this set.
        ///
        /// A NEW monsterEffectKind must earn its own Boss* plan field and spawner branch BEFORE it joins this
        /// set. Until then, a target: "boss" row of that kind would reach nobody: Effects (forBoss: false)
        /// correctly excludes it from the pack, and no forBoss: true call site exists to give it to the boss.
        /// </summary>
        public static readonly IReadOnlySet<string> BossEligibleMonsterEffectKinds =
            new HashSet<string> { HealthMult, DamageRating, DamageResistRating, "none" };

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
        /// Writes PropertyBool.IgnoreMagicArmor (66) and PropertyBool.IgnoreMagicResist (65), both true, plus
        /// PropertyFloat.HollowIntensity (9012) carrying the rolled fraction in [0.01, 1.0]. The flags are the
        /// trigger; the float is what makes the effect fractional rather than all-or-nothing (owner ruling
        /// 2026-09-17) - see ACE.Server.Entity.HollowMath, which every combat-math site now reads through
        /// (armor/banes/life-armor/shield impen and banes, protections/vulnerabilities via GetResistanceMod,
        /// damage-over-time ticks, hotspots, and HARM spells where the caster is the attacker
        /// (WorldObject_Magic.cs:571)). War/life spell PROJECTILES are not hollow - the projectile object is
        /// the attacker there - unless the projectile itself carries the flag.
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

        /// <summary>
        /// <paramref name="levelScale"/> is the modifier level curve's s (<see cref="DungeonModifierLevelScale"/>):
        /// each modifier's FACTOR is pulled toward 1.0 as 1 + s x (factor - 1) BEFORE it joins the product,
        /// and the cap applies to the scaled product. 1.0 (the default) leaves every factor exactly as it was.
        /// </summary>
        private static double Product(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap,
            Func<ModifierDef, string> kindOf, Func<ModifierDef, double> baseOf, Func<ModifierDef, double> slopeOf,
            Func<ModifierDef, bool> include, double levelScale = 1.0)
        {
            var product = 1.0;
            foreach (var (id, magnitude) in spec.Modifiers)
            {
                if (!modifiers.TryGetValue(id, out var def)) continue;
                if (include != null && !include(def)) continue;
                var factor = kindOf(def) == "linear" ? baseOf(def) + slopeOf(def) * magnitude : baseOf(def);
                if (double.IsNaN(factor) || factor <= 0) continue;
                product *= DungeonModifierLevelScale.ScaleRewardFactor(factor, levelScale);
            }
            return Math.Min(product, cap);
        }

        /// <summary>Non-boss modifiers only; every kill.</summary>
        public static double XpMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap, double levelScale = 1.0)
            => Product(spec, modifiers, cap, d => d.RewardXpKind, d => d.RewardXpBase, d => d.RewardXpSlope, d => d.Target != "boss", levelScale);

        /// <summary>Boss-target modifiers only. Multiplied on TOP of XpMultiplier for a boss kill.</summary>
        public static double BossXpMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap, double levelScale = 1.0)
            => Product(spec, modifiers, cap, d => d.RewardXpKind, d => d.RewardXpBase, d => d.RewardXpSlope, d => d.Target == "boss", levelScale);

        /// <summary>Luminance uses the XP kind ("linear"/"constant") with the lum base/slope fields.</summary>
        public static double LumMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, double cap, double levelScale = 1.0)
            => Product(spec, modifiers, cap, d => d.RewardLumSlope != 0 ? "linear" : "constant", d => d.RewardLumBase, d => d.RewardLumSlope, null, levelScale);

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

        /// <summary>
        /// The gem's modifiers plus the caller's extras, filtered by <see cref="ModifierDef.Target"/>: a gem
        /// modifier whose Target is "boss" (case-insensitive) is a BOSS-ONLY effect and is skipped unless
        /// <paramref name="forBoss"/> is true. Extra modifier ids are always included regardless of
        /// <paramref name="forBoss"/> - they are the boss row's own modifiers, never a pack's, so there is no
        /// target to filter on.
        ///
        /// Every magnitude yielded here - the gem's own AND the extras - is already passed through
        /// <see cref="DungeonModifierLevelScale.ScaleMagnitude"/> with <paramref name="levelScale"/>, so the
        /// four consumers below (health, ignore-shield, hollow, ratings) cannot forget it. The extras are
        /// scaled too on purpose: a boss row's own modifiers (boss_guarded on a promoted boss) are modifier
        /// magnitudes like any other, and leaving them full-strength would make a level-50 boss carry a
        /// level-185 bonus. 1.0 yields the raw magnitudes exactly.
        /// </summary>
        private static IEnumerable<(ModifierDef Def, double Magnitude)> Effects(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds, bool forBoss, double levelScale)
        {
            foreach (var (id, magnitude) in spec.Modifiers)
                if (modifiers.TryGetValue(id, out var def) && (forBoss || !string.Equals(def.Target, "boss", StringComparison.OrdinalIgnoreCase)))
                    yield return (def, DungeonModifierLevelScale.ScaleMagnitude(def.MonsterEffectKind, magnitude, levelScale));

            foreach (var id in extraModifierIds ?? Enumerable.Empty<string>())
                if (modifiers.TryGetValue(id, out var def))
                    yield return (def, DungeonModifierLevelScale.ScaleMagnitude(def.MonsterEffectKind, def.MinMagnitude, levelScale));
        }

        public static double HealthMultiplier(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds, bool forBoss = false, double levelScale = 1.0)
        {
            var product = 1.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds, forBoss, levelScale))
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
        public static double IgnoreShieldFraction(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds, bool forBoss = false, double levelScale = 1.0)
        {
            var best = 0.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds, forBoss, levelScale))
                if (def.MonsterEffectKind == IgnoreShield && !double.IsNaN(magnitude) && magnitude > 0)
                    best = Math.Max(best, Math.Clamp(magnitude, 0.0, 1.0));
            return best;
        }

        /// <summary>
        /// The intensity the run's hollow effect rolls, in [0, 1] - 0 meaning the axis is untouched.
        ///
        /// The MAXIMUM across the gem's <see cref="Hollow"/> modifiers, never a sum, on the same reasoning as
        /// <see cref="IgnoreShieldFraction"/>: the value is consumed as a single best fraction
        /// (ACE.Server.Entity.HollowMath.Resolve takes Math.Max against the weapon's own intensity), so two
        /// partial hollow modifiers are not additive.
        /// </summary>
        public static double HollowIntensity(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds, bool forBoss = false, double levelScale = 1.0)
        {
            var best = 0.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds, forBoss, levelScale))
                if (def.MonsterEffectKind == Hollow && !double.IsNaN(magnitude) && magnitude > 0)
                    best = Math.Max(best, Math.Clamp(magnitude, 0.0, 1.0));
            return best;
        }

        public static int RatingTotal(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, IEnumerable<string> extraModifierIds, string kind, bool forBoss = false, double levelScale = 1.0)
        {
            var total = 0.0;
            foreach (var (def, magnitude) in Effects(spec, modifiers, extraModifierIds, forBoss, levelScale))
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
        /// The band term of a NORMALIZED boss's base health (dynamic_dungeons_boss_normalize): R x T, where T is
        /// DungeonHealthCurve's target for the gem level and R is dynamic_dungeons_boss_health_band_ratio. 0 when
        /// either is non-positive or garbled - R = 0 is the documented "use the pack term only", and T = 0 is the
        /// curve's own "no curve".
        /// </summary>
        public static double BossHealthBandTerm(double curveTarget, double bandRatio)
        {
            if (!IsFinitePositive(curveTarget) || !IsFinitePositive(bandRatio))
                return 0.0;

            return curveTarget * bandRatio;
        }

        /// <summary>
        /// The pack term of a NORMALIZED boss's base health: M x PoolMaxBase, M being
        /// dynamic_dungeons_boss_health_pack_margin after <see cref="SanitizePackMargin"/>.
        /// </summary>
        public static double BossHealthPackTerm(uint poolMaxBase, double packMargin)
        {
            var m = SanitizePackMargin(packMargin);

            return m > 0 ? poolMaxBase * m : 0.0;
        }

        /// <summary>
        /// The pack margin as it is applied: NaN, Infinity or a non-positive value reads as 0 ("use the band term
        /// only"), and a positive value BELOW 1.0 reads as 1.0 - the pack term exists to make the boss outrank
        /// its pack, and a margin under 1 would let it undercut the toughest member instead.
        /// </summary>
        public static double SanitizePackMargin(double packMargin)
        {
            if (!IsFinitePositive(packMargin))
                return 0.0;

            return Math.Max(1.0, packMargin);
        }

        /// <summary>
        /// A NORMALIZED boss's health target: round(<paramref name="bossBase"/> x <paramref name="bossMult"/>),
        /// never below <paramref name="observedMaxNonBoss"/> + 1.
        ///
        /// The plan-time formula already outranks every non-boss creature whenever the pack margin is above 1
        /// (bossBase &gt;= M x PoolMaxBase and bossMult &gt;= the pack's multiplier), so on ordinary data the
        /// guard never binds and the boss stays a pure function of (gem, family, dungeon). It exists for the
        /// three cases the plan cannot see, and makes the owner's "boss strictly outranks every elite and
        /// trash" true by construction rather than by margin:
        ///   - LiveHealthOf undercounts an odd authored Endurance by 1 hp (see DungeonSpawnPlan.PoolMaxBase);
        ///   - WorldEventSpawner.ApplyHealth cannot drive a CP-heavy creature DOWN past its CP-parked health,
        ///     so a non-boss can spawn above its own target;
        ///   - an admin running the pack margin at exactly 1.0.
        ///
        /// Not a product with the observed maximum and not a ratio of it - the legacy BossHealthTarget's floor
        /// of 4x the observed pack is what made the boss depend on the random draw, which this replaces.
        /// A garbled or non-positive <paramref name="bossMult"/> reads as 1.0. Saturates at int.MaxValue.
        /// </summary>
        public static int NormalizedBossHealthTarget(double bossBase, double bossMult, uint observedMaxNonBoss)
        {
            var mult = IsFinitePositive(bossMult) ? bossMult : 1.0;
            var fromBase = IsFinitePositive(bossBase) ? bossBase * mult : 0.0;
            var guard = observedMaxNonBoss > 0 ? (double)observedMaxNonBoss + 1.0 : 0.0;

            return (int)Math.Clamp(Math.Round(Math.Max(fromBase, guard)), 1.0, int.MaxValue);
        }

        private static bool IsFinitePositive(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

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
        /// Applies dynamic_dungeons_modifier_reward_scale (s, in [0, 1]) to one of a gem's rolled
        /// reward-PRODUCT axes - <see cref="XpMultiplier"/>, <see cref="BossXpMultiplier"/>,
        /// <see cref="LumMultiplier"/>, or the already-capped <see cref="LootQuantityMultiplier"/> - shrinking
        /// the axis toward neutral (1.0): effective = 1 + s * (product - 1), the same form as
        /// <see cref="ApplyMultiplicativeScale"/> and, deliberately, the SAME function underneath it - this is
        /// not a second formula, only a documented alias for it at this call site.
        ///
        /// NO FLOOR on <paramref name="product"/>, and this is deliberate, not an oversight: for any s in
        /// [0, 1], effective is a WEIGHTED AVERAGE of product and 1.0 (effective = product * s + 1 * (1 - s)),
        /// so it always sits between product and 1.0 inclusive - it can never overshoot past 1.0 on either
        /// side, whether product is above or below 1.0. A product below 1.0 is not hypothetical: XpMultiplier/
        /// LumMultiplier have no lower floor of their own, so a cap tunable set below 1.0 already produces one
        /// today (see ThreadDungeonGemHandler.ComposeLongDesc's reward-block comment, point 4, and
        /// DungeonGemRewardLinesTests.A_cap_below_one_is_shown_because_the_run_really_pays_it) - flooring the
        /// product here would silently erase that existing, tested behaviour at s == 1.0 (the no-op default),
        /// which is exactly the regression a first draft of this method introduced and a full test run caught.
        /// Only NaN is special-cased, to 1.0 (neutral), so a garbled upstream product cannot propagate.
        /// </summary>
        public static double ApplyModifierRewardScale(double s, double product)
        {
            var safeProduct = double.IsNaN(product) ? 1.0 : product;
            return ApplyMultiplicativeScale(s, safeProduct);
        }

        /// <summary>
        /// Applies a reward-scale ratio to an ADDITIVE axis - one whose neutral (unscaled) value is 0.0, such
        /// as the loot-quality bonus or the salvage-affinity per-kill chance: effective = ratio * raw.
        /// </summary>
        public static double ApplyAdditiveScale(double ratio, double raw) => ratio * raw;

        /// <summary>
        /// The first gem modifier of <paramref name="kind"/>'s magnitude, or <paramref name="fallback"/> when
        /// the gem carries none. <paramref name="levelScale"/> (<see cref="DungeonModifierLevelScale"/>) is
        /// applied to a FOUND magnitude by its kind's transform - count_mult and run_speed_mult toward 1.0,
        /// elite_share toward DungeonPopulationBuilder.DefaultEliteShare - and never to the fallback, which
        /// already is the "no modifier" value. 1.0 (the default) returns the raw magnitude exactly.
        /// </summary>
        public static double RunValue(DungeonGemSpec spec, IReadOnlyDictionary<string, ModifierDef> modifiers, string kind, double fallback, double levelScale = 1.0)
        {
            foreach (var (id, magnitude) in spec.Modifiers)
                if (modifiers.TryGetValue(id, out var def) && def.MonsterEffectKind == kind)
                    return DungeonModifierLevelScale.ScaleMagnitude(kind, magnitude, levelScale);
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
