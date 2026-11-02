using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One damage source's per-hit inputs (pure): the base damage the engine starts a hit from (a body part's DVal, or an
    /// item's Damage times the DamageMod its BaseDamageMod reads), the attribute GetAttributeMod reads for it, that
    /// attribute's value, and whether the bow factor applies. <see cref="PerHit"/> is the hit before ratings, power,
    /// slayer, crit and every player-side term: BaseDamage x AttributeMod (DamageEvent.cs, the DamageBeforeMitigation
    /// product).
    /// </summary>
    public readonly struct PerHitSource
    {
        /// <param name="upliftScale">
        /// BaseDamage over the value ArchetypeUplift.DamageSourcesOf reads as this source's EFFECTIVE damage: a melee or
        /// thrown weapon's own DamageMod (the uplift reads its bare Damage, the engine hits for Damage x DamageMod), 1 for the
        /// body and for ammunition (whose effective value already carries the launcher's DamageMod). 0 or less reads as 1.
        /// </param>
        public PerHitSource(double baseDamage, PropertyAttribute attribute, uint attributeValue, bool isBow, bool isBody = false, double upliftScale = 1.0)
        {
            BaseDamage = baseDamage;
            Attribute = attribute;
            AttributeValue = attributeValue;
            IsBow = isBow;
            IsBody = isBody;
            UpliftScale = upliftScale > 0 && !double.IsNaN(upliftScale) && !double.IsInfinity(upliftScale) ? upliftScale : 1.0;
        }

        public double BaseDamage { get; }
        public PropertyAttribute Attribute { get; }
        public uint AttributeValue { get; }
        public bool IsBow { get; }

        /// <summary>True when the source is the creature's body (its best body-part DVal), false for an item.</summary>
        public bool IsBody { get; }

        /// <summary>BaseDamage over the archetype uplift's effective read of the source (see the constructor).</summary>
        public double UpliftScale { get; }

        /// <summary>The source's effective damage as ArchetypeUplift reads it (BaseDamage / UpliftScale), 0 for an empty source.</summary>
        public double UpliftEffective => IsEmpty ? 0.0 : BaseDamage / UpliftScale;

        /// <summary>True when this source deals nothing (no authored damage).</summary>
        public bool IsEmpty => !(BaseDamage > 0);

        /// <summary>BaseDamage x the engine's attribute mod, or 0 for an empty source.</summary>
        public double PerHit => IsEmpty ? 0.0 : DamageCeiling.Predict(BaseDamage, AttributeValue, IsBow);

        /// <summary>The source carried along the stat curve: damage by <paramref name="damageRatio"/>, the attribute by its own ratio.</summary>
        public PerHitSource Project(double damageRatio, double attributeRatio)
            => IsEmpty ? this : new PerHitSource(BaseDamage * damageRatio, Attribute, DungeonStatCurve.Scale(AttributeValue, attributeRatio), IsBow, IsBody, UpliftScale);

        /// <summary>The same source with another base damage and attribute value (an uplift's raise). An empty source stays empty.</summary>
        public PerHitSource With(double baseDamage, uint attributeValue)
            => IsEmpty ? this : new PerHitSource(baseDamage, Attribute, attributeValue, IsBow, IsBody, UpliftScale);
    }

    /// <summary>
    /// What the ARCHETYPE uplift reads off a creature beyond its two best sources (pure data, read off the weenie):
    ///   - <see cref="OwnDamage"/>, the uplift's own damage (ArchetypeUplift.Apply's OwnLiveDamage): the best of the body's
    ///     DVal and every wielded damage source's effective value, the body INCLUDED even while a weapon is wielded;
    ///   - <see cref="DamageRating"/>, the authored rating FloorRatings starts from;
    ///   - the best war/void projectile mean of the FINAL spell book at each spell tier (<see cref="SpellMeanAt"/>): the
    ///     authored book raised to the tier (DungeonSpellTier.Raise), then the AoE/drain strip, exactly the spawner's order.
    /// </summary>
    public sealed class ArchetypeUpliftInputs
    {
        public ArchetypeUpliftInputs(double ownDamage, int damageRating, IReadOnlyList<double> spellMeanByTier = null)
        {
            OwnDamage = ownDamage;
            DamageRating = damageRating;
            SpellMeanByTier = spellMeanByTier ?? Array.Empty<double>();
        }

        public double OwnDamage { get; }
        public int DamageRating { get; }

        /// <summary>Index 0: the authored book (no raise); index t: raised to tier t. Empty when no spell reader was given.</summary>
        public IReadOnlyList<double> SpellMeanByTier { get; }

        /// <summary>The best projectile mean of the final book when the spawner raises it to <paramref name="tier"/> (0 or less: no raise; above 8 reads 8).</summary>
        public double SpellMeanAt(int tier)
        {
            if (SpellMeanByTier.Count == 0)
                return 0.0;

            var index = Math.Clamp(tier, 0, SpellMeanByTier.Count - 1);
            return SpellMeanByTier[index];
        }
    }

    /// <summary>
    /// Reads a weenie's spell book as the spawner fields it for <see cref="ArchetypeUpliftInputs"/> (pure over its
    /// delegates): for each tier 0..8 a copy of the authored book raised to that tier, the strip applied with the same
    /// guard input (a damaging body part), and the best war/void projectile mean of what is left.
    /// </summary>
    public sealed class PerHitSpellReader
    {
        public const int MaxTier = 8;

        public PerHitSpellReader(SpellStripConfig strip, Func<int, int> projectilesOf, Func<int, double> meanOf)
        {
            Strip = strip ?? SpellStripConfig.None;
            ProjectilesOf = projectilesOf;
            MeanOf = meanOf;
        }

        public SpellStripConfig Strip { get; }
        public Func<int, int> ProjectilesOf { get; }
        public Func<int, double> MeanOf { get; }

        public IReadOnlyList<double> MeansByTier(IDictionary<int, float> authoredBook, bool hasDamagingBodyPart)
        {
            if (authoredBook == null || authoredBook.Count == 0 || MeanOf == null)
                return Array.Empty<double>();

            var means = new double[MaxTier + 1];

            for (var tier = 0; tier <= MaxTier; tier++)
            {
                var book = new Dictionary<int, float>(authoredBook);

                if (tier > 0)
                    DungeonSpellTier.Raise(book, tier);

                if (Strip.Enabled)
                    DungeonSpellFilter.Strip(book, Strip.BannedIds, ProjectilesOf ?? (_ => 0), hasDamagingBodyPart);

                means[tier] = ArchetypeUplift.BestProjectileMean(book.Keys, MeanOf);
            }

            return means;
        }
    }

    /// <summary>
    /// A weenie's best melee and best missile per-hit source, read off the authored data (pure over the weenie and a
    /// resolver for its wielded items). The median axis of the Threads damage ceiling
    /// (<see cref="DamageCeilingStandard"/>) is taken over these.
    ///
    /// MELEE: the best Wield-row melee weapon's Damage x its own DamageMod at Strength (Coordination for a finesse
    /// weapon); only a creature that wields no melee weapon reads its best body part with a positive DVal (Breath
    /// included) at Strength instead, because Monster_Melee.GetBaseDamage never reads DVal while a weapon is equipped. MISSILE: the best of
    /// every Wield-row ammunition's Damage x a wielded launcher's DamageMod (Coordination and the bow factor for a bow
    /// or crossbow, Strength otherwise; DamageMod 1 and Strength with no launcher), and every Wield-row thrown weapon's
    /// Damage x its own DamageMod at Strength. Monster_Melee.GetBaseDamage, Monster_Missile.GetMissileDamage and
    /// Creature_Combat.GetAttributeMod are the engine reads this mirrors.
    ///
    /// A weenie that carries a WieldedTreasureType rolls gear the weenie read cannot see (with or without a Wield-row weapon:
    /// a rolled weapon can out-hit the authored one), so its prediction can be several times below what it fields (the live measurement at spawn, <see cref="DamageCeiling.Apply"/>,
    /// sees the rolled items): it is flagged <see cref="GearUnknown"/> and left out of the median sample and top.
    /// </summary>
    public sealed class CreaturePerHit
    {
        public static readonly CreaturePerHit Empty = new CreaturePerHit(0, default, default);

        public CreaturePerHit(int level, PerHitSource melee, PerHitSource missile, ArchetypeUpliftInputs upliftInputs = null, bool gearUnknown = false,
            bool byArchetype = false)
        {
            Level = level;
            Melee = melee;
            Missile = missile;
            UpliftInputs = upliftInputs;
            GearUnknown = gearUnknown;
            ByArchetype = byArchetype;
        }

        /// <summary>The weenie's authored level (or the level it was projected to).</summary>
        public int Level { get; }

        public PerHitSource Melee { get; }
        public PerHitSource Missile { get; }

        /// <summary>The archetype uplift's extra inputs (null when the read carried none: own damage then falls back to the best source).</summary>
        public ArchetypeUpliftInputs UpliftInputs { get; }

        /// <summary>
        /// True when the weenie carries a WieldedTreasureType (orchestrator ruling 2026-10-26: regardless of Wield rows): the weenie read
        /// cannot see what it fights with, so the prediction is unknown (kept in the pool, left out of the median and top).
        /// </summary>
        public bool GearUnknown { get; }

        /// <summary>True when this is the archetype uplift's prediction (<see cref="UpliftedByArchetype"/> with an info).</summary>
        public bool ByArchetype { get; }

        public double MeleePerHit => Melee.PerHit;
        public double MissilePerHit => Missile.PerHit;

        /// <summary>The creature's worst hit: the larger of its melee and missile per-hit (its entry in the median sample).</summary>
        public double WorstPerHit => Math.Max(MeleePerHit, MissilePerHit);

        private CreaturePerHit Make(int level, PerHitSource melee, PerHitSource missile, bool byArchetype = false)
            => new CreaturePerHit(level, melee, missile, UpliftInputs, GearUnknown, byArchetype);

        /// <summary>This creature read at another level WITHOUT moving its damage (reach-up's level projection alone).</summary>
        public CreaturePerHit WithLevel(int level) => level == Level ? this : Make(level, Melee, Missile);

        /// <summary>
        /// This creature as a reach-up stamp to <paramref name="toLevel"/> fields it with the stat curve on (ReachUpStamp):
        /// damage by the run's curve ratio on the Damage axis, the attribute by its own axis ratio. <paramref name="curveRatio"/>
        /// is the run's (from, to) ratio exactly as the live stamp reads it - plan.StatCurve.Ratio, with the live damage rate
        /// and pivot (owner ruling 2026-10-29) - and is required, so the prediction cannot silently use another rate. Upward
        /// only; a creature with no authored level, or a level at or below its own, is returned unprojected.
        /// </summary>
        public CreaturePerHit ProjectedTo(int toLevel, Func<DungeonStatAxis, int, int, double> curveRatio)
        {
            if (curveRatio == null)
                throw new ArgumentNullException(nameof(curveRatio));

            if (Level <= 0 || toLevel <= Level)
                return this;

            double Ratio(DungeonStatAxis axis) => curveRatio(axis, Level, toLevel);

            PerHitSource P(PerHitSource s)
            {
                var axis = DungeonStatCurve.AxisOf(s.Attribute);
                return s.Project(Ratio(DungeonStatAxis.Damage), axis.HasValue ? Ratio(axis.Value) : 1.0);
            }

            return Make(toLevel, P(Melee), P(Missile));
        }

        /// <summary>
        /// The DamageRating offset the archetype uplift divides physical damage by (pure, ArchetypeUplift.FloorRatings and
        /// Apply's rule): the rating floor over the authored rating and the final book's projectile mean at the source's
        /// spell tier, then mod(total - added) / mod(total) with total = the floored rating plus
        /// <paramref name="gemDamageRating"/> (the solo plan's additive rating, StampedDamageRating with no group bonus).
        /// </summary>
        public double ArchetypeDamageRatingOffset(ArchetypeUpliftInfo info, int gemDamageRating)
        {
            if (info == null)
                return 1.0;

            var floor = ArchetypeUplift.DamageRatingFloor(info, UpliftInputs?.DamageRating ?? 0, UpliftInputs?.SpellMeanAt(info.Run.Core.SpellTier) ?? 0.0);

            return ArchetypeUplift.DamageRatingOffset((int)Math.Clamp((long)floor.DrTo + gemDamageRating, int.MinValue, int.MaxValue), floor.Added);
        }

        /// <summary>
        /// This creature as the ARCHETYPE uplift fields it at <paramref name="upliftLevel"/> (ArchetypeUplift.Apply,
        /// FloorRatings and RaiseAttributes, read through their own pure cores):
        ///   - INPUT, what the uplift measures: own damage is <see cref="ArchetypeUpliftInputs.OwnDamage"/>, the best of the
        ///     body and every wielded source's effective value (body included even with a weapon wielded, and a melee or
        ///     thrown weapon read WITHOUT its DamageMod, as DamageSourcesOf reads it); ONE target from it by
        ///     <see cref="ArchetypeUplift.AxisTarget"/>;
        ///   - each source is scaled by <see cref="ArchetypeUplift.SourceMultiplier"/> over its own effective value, times the
        ///     DamageRating offset (<see cref="ArchetypeDamageRatingOffset"/>), so a creature whose spell output needed a
        ///     rating top-up has its physical hit divided back down, never below authored;
        ///   - OUTPUT, what the engine hits for: the two sources the weenie read already chose (the body only when no melee
        ///     weapon is wielded), each at its scaled base x the raised attribute's mod.
        /// Not modelled: the integer rounding of the writes, and the Endurance guard that can skip the attribute raise (it
        /// needs the gem's health multiplier; the census counts where it would bind). A null info returns the level alone.
        /// </summary>
        public CreaturePerHit UpliftedByArchetype(int upliftLevel, ArchetypeUpliftInfo info, int gemDamageRating = 0)
        {
            if (info == null)
                return WithLevel(upliftLevel);

            var own = UpliftInputs != null && UpliftInputs.OwnDamage > 0 ? UpliftInputs.OwnDamage : Math.Max(Melee.UpliftEffective, Missile.UpliftEffective);
            var target = ArchetypeUplift.AxisTarget(own, info.Run.EffectiveDamage, info.Own.EffectiveDamage, info.OffenseRatio, info.CapRatio);
            var offset = ArchetypeDamageRatingOffset(info, gemDamageRating);

            PerHitSource Raise(PerHitSource s)
            {
                if (s.IsEmpty)
                    return s;

                var attributes = ArchetypeUplift.AttributeTargets(info, a => a == s.Attribute ? s.AttributeValue : (uint?)null);
                var attribute = attributes.TryGetValue(s.Attribute, out var raised) ? raised : s.AttributeValue;

                return s.With(s.BaseDamage * ArchetypeUplift.SourceMultiplier(s.UpliftEffective, target, offset), attribute);
            }

            return Make(upliftLevel, Raise(Melee), Raise(Missile), byArchetype: true);
        }

        /// <summary>
        /// This creature as the legacy BAND uplift fields it at <paramref name="upliftLevel"/> (BandUplift.Apply, plus the
        /// Threads attribute raise when <paramref name="raiseAttributes"/>): the body's best part raised to the standard's
        /// MaxBodyDamage (an item is never touched by it), and each carried attribute floored at the standard's median. A
        /// null or empty standard returns the level alone.
        /// </summary>
        public CreaturePerHit UpliftedByBand(int upliftLevel, DungeonBandStandard standard, bool raiseAttributes)
        {
            if (standard == null || standard.IsEmpty)
                return WithLevel(upliftLevel);

            PerHitSource Raise(PerHitSource s)
            {
                if (s.IsEmpty)
                    return s;

                var baseDamage = s.IsBody ? Math.Max(s.BaseDamage, standard.MaxBodyDamage) : s.BaseDamage;
                // BandUplift.RaiseAttributeFloors' rule: a carried (non-zero) attribute below the median is set to it.
                var median = standard.AttributeMedianFor(s.Attribute);
                var attribute = raiseAttributes && s.AttributeValue != 0 && median > s.AttributeValue ? median : s.AttributeValue;

                return s.With(baseDamage, attribute);
            }

            return Make(upliftLevel, Raise(Melee), Raise(Missile));
        }

        /// <summary>
        /// The weenie's best sources (see the class comment), plus the archetype uplift's inputs
        /// (<see cref="ArchetypeUpliftInputs"/>; the spell means only when <paramref name="spells"/> is given) and the
        /// <see cref="GearUnknown"/> flag. <see cref="Empty"/> for a null weenie.
        /// </summary>
        public static CreaturePerHit FromWeenie(Weenie weenie, Func<uint, Weenie> weenieOf, PerHitSpellReader spells = null)
        {
            if (weenie == null)
                return Empty;

            uint Attr(PropertyAttribute a)
                => weenie.PropertiesAttribute != null && weenie.PropertiesAttribute.TryGetValue(a, out var row) && row != null ? row.InitLevel + row.LevelFromCP : 0u;

            var melee = default(PerHitSource);
            var missile = default(PerHitSource);

            void Consider(ref PerHitSource best, PerHitSource candidate)
            {
                if (!candidate.IsEmpty && candidate.PerHit > best.PerHit)
                    best = candidate;
            }

            var wielded = new List<Weenie>();

            if (weenie.PropertiesCreateList != null && weenieOf != null)
            {
                foreach (var row in weenie.PropertiesCreateList)
                {
                    if (row == null || !row.DestinationType.HasFlag(DestinationType.Wield))
                        continue;

                    var item = weenieOf(row.WeenieClassId);

                    if (item != null)
                        wielded.Add(item);
                }
            }

            var maxDVal = 0;

            if (weenie.PropertiesBodyPart != null)
                foreach (var part in weenie.PropertiesBodyPart.Values)
                    if (part != null && part.DVal > maxDVal)
                        maxDVal = part.DVal;

            // The body counts only for a creature that wields no melee weapon: Monster_Melee.GetBaseDamage reads the
            // equipped weapon's damage for EVERY melee hit when one is equipped and never the body part's DVal. (The live
            // measurement keeps the body as its own source, because a monster can drop to its body after a swap.)
            if (maxDVal > 0 && !wielded.Any(i => i.WeenieType == WeenieType.MeleeWeapon))
                Consider(ref melee, new PerHitSource(maxDVal, PropertyAttribute.Strength, Attr(PropertyAttribute.Strength), false, isBody: true));

            var launchers = wielded.Where(i => i.WeenieType == WeenieType.MissileLauncher).ToList();

            // The archetype uplift's own damage reads the body even with a weapon wielded (ArchetypeUplift.Apply: the best
            // of bodyMax and every equipped source's effective value).
            var upliftOwn = (double)maxDVal;

            foreach (var item in wielded)
            {
                var damage = item.GetProperty(PropertyInt.Damage) ?? 0;

                if (damage <= 0)
                    continue;

                var ownMod = item.GetProperty(PropertyFloat.DamageMod) ?? 1.0;
                var finesse = (Skill)(item.GetProperty(PropertyInt.WeaponSkill) ?? 0) == Skill.FinesseWeapons;
                var meleeAttr = finesse ? PropertyAttribute.Coordination : PropertyAttribute.Strength;

                switch (item.WeenieType)
                {
                    case WeenieType.MeleeWeapon:
                        upliftOwn = Math.Max(upliftOwn, damage);
                        Consider(ref melee, new PerHitSource(damage * ownMod, meleeAttr, Attr(meleeAttr), false, upliftScale: ownMod));
                        break;

                    case WeenieType.Missile:
                        upliftOwn = Math.Max(upliftOwn, damage);
                        Consider(ref missile, new PerHitSource(damage * ownMod, meleeAttr, Attr(meleeAttr), false, upliftScale: ownMod));
                        break;

                    case WeenieType.Ammunition:
                        if (launchers.Count == 0)
                            Consider(ref missile, new PerHitSource(damage, PropertyAttribute.Strength, Attr(PropertyAttribute.Strength), false));

                        foreach (var launcher in launchers)
                        {
                            var style = (CombatStyle)(launcher.GetProperty(PropertyInt.DefaultCombatStyle) ?? 0);
                            var bow = style == CombatStyle.Bow || style == CombatStyle.Crossbow;
                            var launcherFinesse = (Skill)(launcher.GetProperty(PropertyInt.WeaponSkill) ?? 0) == Skill.FinesseWeapons;
                            var attr = bow || launcherFinesse ? PropertyAttribute.Coordination : PropertyAttribute.Strength;
                            var launcherMod = launcher.GetProperty(PropertyFloat.DamageMod) ?? 1.0;

                            // Ammunition sets own only with a launcher equipped (DamageSourcesOf's SetsOwn).
                            upliftOwn = Math.Max(upliftOwn, damage * ArchetypeUplift.LauncherDamageMod(launcherMod));
                            Consider(ref missile, new PerHitSource(damage * launcherMod, attr, Attr(attr), bow));
                        }

                        break;
                }
            }

            var spellMeans = spells?.MeansByTier(weenie.PropertiesSpellBook, maxDVal > 0);
            var inputs = new ArchetypeUpliftInputs(upliftOwn, weenie.GetProperty(PropertyInt.DamageRating) ?? 0, spellMeans);

            // F3 (code review 2026-10-26; widened by orchestrator ruling the same day): gear rolled from a wielded-treasure
            // table is invisible to this read, whether or not a Wield row also carries a weapon (census v3: four natural
            // A'nekshen reavers with a Wield-row weapon AND table 2120 measured x4.3-x6 live), so the prediction is unknown.
            var gearUnknown = (weenie.GetProperty(PropertyDataId.WieldedTreasureType) ?? 0) > 0;

            return new CreaturePerHit(weenie.GetProperty(PropertyInt.Level) ?? 0, melee, missile, inputs, gearUnknown);
        }
    }

    /// <summary>How the population builder fields one spawn-pool member (DungeonPopulationBuilder.TagEntry's rule).</summary>
    public enum DamageCeilingPlacement
    {
        /// <summary>Placed at its authored level: neither stamped nor uplifted.</summary>
        Natural,

        /// <summary>Reach-up STAMPED at its projected level (above the pivot with reach-up on).</summary>
        Stamped,

        /// <summary>Reached only by the band widening (projected level below the natural low edge), so UPLIFTED to the gem level.</summary>
        Uplifted,
    }

    /// <summary>One member of a gem level's spawn pool, predicted as the builder and the spawner field it.</summary>
    public sealed class DamageCeilingPoolMember
    {
        public DamageCeilingPoolMember(uint wcid, bool trash, bool elite, int authoredLevel, int projectedLevel, DamageCeilingPlacement placement, CreaturePerHit predicted)
        {
            Wcid = wcid;
            Trash = trash;
            Elite = elite;
            AuthoredLevel = authoredLevel;
            ProjectedLevel = projectedLevel;
            Placement = placement;
            Predicted = predicted ?? CreaturePerHit.Empty;
        }

        public uint Wcid { get; }

        /// <summary>In some eligible family's trash pool, and/or in some family's elite pool (a family with no elite in band fields its trash pool there).</summary>
        public bool Trash { get; }
        public bool Elite { get; }

        public int AuthoredLevel { get; }
        public int ProjectedLevel { get; }
        public DamageCeilingPlacement Placement { get; }

        /// <summary>The per-hit sources after the build-time writers that precede the ceiling (stamp or uplift).</summary>
        public CreaturePerHit Predicted { get; }

        /// <summary>The member's worst predicted hit (melee or missile): its entry in the median sample unless <see cref="GearUnknown"/>.</summary>
        public double PerHit => Predicted.WorstPerHit;

        /// <summary>Gear rolled from a wielded-treasure table the weenie read cannot see (<see cref="CreaturePerHit.GearUnknown"/>): not sampled.</summary>
        public bool GearUnknown => Predicted.GearUnknown;

        /// <summary>True when the member counts in the median sample and the top: it deals predicted damage and its gear is known.</summary>
        public bool Sampled => PerHit > 0 && !double.IsNaN(PerHit) && !double.IsInfinity(PerHit) && !GearUnknown;
    }

    /// <summary>
    /// The damage ceiling's baseline at one gem level: the shared physical median, the pool's worst ratio to it (the
    /// automatic reference ratio "top") and the pool it was measured over. <see cref="Empty"/> (median 0) is the no-op.
    /// </summary>
    public sealed class DamageCeilingBaseline
    {
        public static readonly DamageCeilingBaseline Empty = new DamageCeilingBaseline(0.0, 0.0, new List<DamageCeilingPoolMember>());

        public DamageCeilingBaseline(double median, double poolMaxRatio, IReadOnlyList<DamageCeilingPoolMember> members)
        {
            Median = median;
            PoolMaxRatio = poolMaxRatio;
            Members = members ?? new List<DamageCeilingPoolMember>();
        }

        /// <summary>The LOWER median of every sampled member's worst predicted hit (<see cref="DamageCeilingPoolMember.Sampled"/>). 0 when none.</summary>
        public double Median { get; }

        /// <summary>The largest sampled member worst-hit over <see cref="Median"/>: where the worst hitter sits, the automatic top. 0 with no median.</summary>
        public double PoolMaxRatio { get; }

        /// <summary>Every pool member, damaging or not, gear known or not, in wcid order.</summary>
        public IReadOnlyList<DamageCeilingPoolMember> Members { get; }

        /// <summary>The size of the median sample: members that deal predicted damage and whose gear is known.</summary>
        public int SampleSize => Members.Count(m => m.Sampled);

        /// <summary>Members left out of the sample because their gear rolls from a wielded-treasure table (<see cref="DamageCeilingPoolMember.GearUnknown"/>).</summary>
        public int GearUnknownCount => Members.Count(m => m.GearUnknown);

        /// <summary>
        /// The reference ratio a plan uses (dynamic_dungeons_damage_ceiling_reference_ratio): a positive configured value
        /// forces it; 0 (the shipped default, "auto") takes <see cref="PoolMaxRatio"/>, so the worst predicted hitter at this
        /// gem level lands exactly on the cap.
        /// </summary>
        public double TopFor(double configured)
            => configured > 0 && !double.IsNaN(configured) && !double.IsInfinity(configured) ? configured : PoolMaxRatio;
    }

    /// <summary>
    /// The baseline of the Threads damage ceiling (owner decisions 2026-10-26): "what actually spawns". The median is taken
    /// over the creatures the population builder can actually draw as trash or elite at the gem level, each predicted
    /// AFTER the build-time writers that precede the ceiling, and ONE shared physical median serves melee and missile.
    ///
    /// THE POOL (<see cref="SpawnPool"/>). The builder's own band construction, over EVERY family that is eligible at the
    /// gem level rather than the one a run happens to draw: the family-eligibility band (ResolveEligibilityBand), then for
    /// each eligible family its own widened trash band (ResolveFamilyTrashBand, MinFamilyPool) and elite band
    /// (ResolveFamilyEliteBand), the trash pool (PoolFor) and the elite pool (ElitePoolFor; an empty one fields the trash
    /// pool, which is already in). Distinct by wcid, in wcid order, unweighted. Over the RAW species tables, never a
    /// dungeon's fit projection: like the band standard, the baseline is a property of the gem level, not of the dungeon.
    /// The boss is not a pool member (boss normalization sets it from the band standard).
    ///
    /// THE PREDICTION (<see cref="Predict"/>). Each member is tagged as DungeonPopulationBuilder.TagEntry tags a pick: below
    /// the natural low edge it is UPLIFTED to the gem level (the caller's uplift prediction: the archetype uplift's rule, or
    /// the band uplift's); otherwise, projected above its authored level with the stat curve on, it is STAMPED there
    /// (<see cref="CreaturePerHit.ProjectedTo"/>, what ReachUpStamp does); otherwise it is read as authored.
    ///
    /// THE MEDIAN. One sample entry per member: its WORST hit, the larger of its predicted melee and missile per-hit (a
    /// member with neither is excluded, not counted as zero; so is a member whose gear rolls from a wielded-treasure table
    /// the weenie read cannot see, <see cref="CreaturePerHit.GearUnknown"/>, which stays in the pool), and the LOWER median of those. One entry per creature, so a
    /// creature that both swings and shoots is not counted twice and an archer is measured with everyone else rather than
    /// against a thin missile-only sample. Melee and missile ratios are both read against this one number.
    ///
    /// A pure function of (tables, level, dials, weenie data): no rng, no seed, no roster draw, no note, no fingerprint field.
    /// </summary>
    public static class DamageCeilingStandard
    {
        /// <summary>
        /// The trash+elite spawn pool at one gem level (see the class comment): (wcid, in a trash pool, in an elite pool),
        /// in wcid order. <paramref name="levelOf"/> is the builder's PROJECTED level read.
        /// </summary>
        public static List<(uint Wcid, bool Trash, bool Elite)> SpawnPool(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, DungeonRosterBand natural, double lowFloorRatio, int minEligibleFamilies, int minFamilyPool,
            DungeonRosterBand? eligibilityBand = null)
        {
            var roles = new SortedDictionary<uint, (bool Trash, bool Elite)>();

            if (tables == null || levelOf == null || level <= 0)
                return new List<(uint, bool, bool)>();

            var eligibility = eligibilityBand
                ?? DungeonRosterSelector.ResolveEligibilityBand(tables, level, levelOf, natural, lowFloorRatio, minEligibleFamilies);

            foreach (var family in tables.Values.Where(t => t?.Members != null).OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                if (!DungeonRosterSelector.IsEligible(family, level, levelOf, eligibility))
                    continue;

                var trashBand = DungeonRosterSelector.ResolveFamilyTrashBand(family, level, levelOf, eligibility, lowFloorRatio, minFamilyPool);
                var eliteBand = DungeonRosterSelector.ResolveFamilyEliteBand(family, level, levelOf, eligibility, lowFloorRatio);

                foreach (var wcid in DungeonRosterSelector.PoolFor(family, level, DungeonRole.Trash, levelOf, trashBand))
                    roles[wcid] = (true, roles.TryGetValue(wcid, out var have) && have.Elite);

                foreach (var wcid in DungeonRosterSelector.ElitePoolFor(family, level, levelOf, eliteBand))
                    roles[wcid] = (roles.TryGetValue(wcid, out var have) && have.Trash, true);
            }

            return roles.Select(kv => (kv.Key, kv.Value.Trash, kv.Value.Elite)).ToList();
        }

        /// <summary>
        /// One member as the builder tags it and the spawner fields it (pure): UPLIFTED below <paramref name="naturalLow"/>
        /// (through <paramref name="uplift"/>, or the level alone when that is null), STAMPED above its authored level when
        /// <paramref name="stampRatio"/> is the run's curve ratio (null = the curve does not apply), else at its projected level
        /// with its damage as authored.
        /// </summary>
        public static (DamageCeilingPlacement Placement, CreaturePerHit Predicted) Predict(uint wcid, CreaturePerHit raw, int projectedLevel, int authoredLevel,
            int naturalLow, int gemLevel, Func<DungeonStatAxis, int, int, double> stampRatio, Func<uint, CreaturePerHit, CreaturePerHit> uplift)
        {
            raw ??= CreaturePerHit.Empty;

            if (projectedLevel < naturalLow)
                return (DamageCeilingPlacement.Uplifted, uplift != null ? uplift(wcid, raw) ?? raw.WithLevel(gemLevel) : raw.WithLevel(gemLevel));

            if (projectedLevel > authoredLevel)
                return (DamageCeilingPlacement.Stamped, stampRatio != null ? raw.WithLevel(authoredLevel).ProjectedTo(projectedLevel, stampRatio) : raw.WithLevel(projectedLevel));

            return (DamageCeilingPlacement.Natural, raw.WithLevel(projectedLevel));
        }

        /// <summary>
        /// The baseline over a resolved pool (pure): every member predicted, the lower median of the damaging members'
        /// worst hits, and the largest worst-hit over that median. <see cref="DamageCeilingBaseline.Empty"/> for no pool or
        /// no per-hit read.
        /// </summary>
        public static DamageCeilingBaseline Compute(IReadOnlyList<(uint Wcid, bool Trash, bool Elite)> pool, Func<uint, CreaturePerHit> perHitOf,
            Func<uint, int> projectedLevelOf, Func<uint, int> authoredLevelOf, int naturalLow, int gemLevel, Func<DungeonStatAxis, int, int, double> stampRatio,
            Func<uint, CreaturePerHit, CreaturePerHit> uplift)
        {
            if (pool == null || pool.Count == 0 || perHitOf == null || projectedLevelOf == null || authoredLevelOf == null)
                return DamageCeilingBaseline.Empty;

            var members = new List<DamageCeilingPoolMember>(pool.Count);

            foreach (var (wcid, trash, elite) in pool.OrderBy(p => p.Wcid))
            {
                var authored = authoredLevelOf(wcid);
                var projected = projectedLevelOf(wcid);
                var (placement, predicted) = Predict(wcid, perHitOf(wcid), projected, authored, naturalLow, gemLevel, stampRatio, uplift);

                members.Add(new DamageCeilingPoolMember(wcid, trash, elite, authored, projected, placement, predicted));
            }

            var sample = members.Where(m => m.Sampled).Select(m => m.PerHit).ToList();
            var median = DungeonBandStandard.LowerMedian(sample);

            if (!(median > 0))
                return new DamageCeilingBaseline(0.0, 0.0, members);

            return new DamageCeilingBaseline(median, sample.Max() / median, members);
        }
    }

    /// <summary>What <see cref="DamageCeiling.Apply"/> did to one creature, for the spawner's Debug line and the tests.</summary>
    internal sealed class DamageCeilingResult
    {
        public double MeleeBefore { get; set; }
        public double MissileBefore { get; set; }
        public double MeleeTarget { get; set; }
        public double MissileTarget { get; set; }
        public double BodyFactor { get; set; } = 1.0;
        public int PartsScaled { get; set; }
        public List<(uint Wcid, string Name, int From, int To)> Items { get; } = new List<(uint, string, int, int)>();
        public int WithheldMelee { get; set; }
        public int WithheldMissile { get; set; }

        /// <summary>The creature's withheld group rating: the larger of its two axes (the budget a later step spends).</summary>
        public int Withheld => Math.Max(WithheldMelee, WithheldMissile);
    }

    /// <summary>
    /// The Threads damage ceiling (DAMAGE-CEILING-DESIGN.md goals 1 and 2), pure math plus the creature-facing glue.
    ///
    /// SOLO SPREAD CAP. A creature's per-hit damage P (base x attribute mod, before ratings) on each axis, melee and
    /// missile alike, is compared with the gem level's ONE shared physical median M (<see cref="DamageCeilingStandard"/>,
    /// the median of what actually spawns), and the ratio r = P / M is compressed by <see cref="Compress"/>:
    /// r^(ln cap / ln top), so the reference worst ratio top lands exactly on cap and a creature between 1 and top keeps
    /// its order on the same curve; above top it is clamped to cap. top is the pool's own worst predicted ratio unless
    /// dynamic_dungeons_damage_ceiling_reference_ratio forces one (<see cref="DamageCeilingBaseline.TopFor"/>).
    ///
    /// GROUP CLAMP. Group damage rating G (GroupScaling, carried as plan.GroupDamageRatingBonus) multiplies every hit by
    /// g = (100 + R + G) / (100 + R) on top of the creature's own rating R. The clamp lowers BASE damage so P x g never
    /// exceeds what a solo creature at the cap deals: P_final = min(M x Compress(P / M), cap x M / g). The rating stamp
    /// itself is never touched: it also feeds monster spell damage, which is out of scope.
    ///
    /// Base damage only: body-part DVal (one factor for every part, through a cloned dictionary of cloned parts, because
    /// PropertiesBodyPart is shared by reference with the cached weenie) and each non-launcher item's Damage (per
    /// instance), each with its OWN factor so every source sits on the curve. Floor rounding, so rounding can only lower a
    /// hit; a positive value never drops below 1.
    /// </summary>
    public static class DamageCeiling
    {
        /// <summary>Default of dynamic_dungeons_damage_ceiling (ships ON).</summary>
        public const bool DefaultEnabled = true;

        /// <summary>Default of dynamic_dungeons_damage_ceiling_ratio: the per-hit cap as a multiple of the median.</summary>
        public const double DefaultCap = 2.0;

        /// <summary>
        /// Default of dynamic_dungeons_damage_ceiling_reference_ratio: 0 = AUTO, the plan takes the pool's own worst
        /// predicted ratio (<see cref="DamageCeilingBaseline.PoolMaxRatio"/>) as top. A positive value forces that top.
        /// </summary>
        public const double DefaultReferenceRatio = 0.0;

        /// <summary>The cap as the code reads it: NaN, Infinity or a value below 1 reads as <see cref="DefaultCap"/>.</summary>
        public static double SanitizeCap(double cap)
            => double.IsNaN(cap) || double.IsInfinity(cap) || cap < 1.0 ? DefaultCap : cap;

        /// <summary>The configured reference ratio as the code reads it: NaN, Infinity or a negative value reads as 0 (auto).</summary>
        public static double SanitizeReferenceRatio(double value)
            => double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 ? DefaultReferenceRatio : value;

        /// <summary>The engine's attribute mod (SkillFormula.GetAttributeMod) for an attribute value.</summary>
        public static double AttributeMod(uint attribute, bool isBow)
            => SkillFormula.GetAttributeMod((int)Math.Min(attribute, int.MaxValue), isBow);

        /// <summary>One source's per-hit damage: base x attribute mod.</summary>
        public static double Predict(double baseDamage, uint attribute, bool isBow)
            => baseDamage * AttributeMod(attribute, isBow);

        /// <summary>
        /// The compressed ratio: r at or below 1 (or NaN) unchanged; above 1, r^(ln cap / ln top), and cap for r above
        /// top; with top at or below cap, a plain clamp at cap. A garbled cap reads as its default; a garbled top (NaN,
        /// Infinity, 0 or below) reads as "at or below the cap", i.e. a plain clamp.
        /// </summary>
        public static double Compress(double r, double cap, double top)
        {
            if (double.IsNaN(r) || r <= 1.0)
                return r;

            cap = SanitizeCap(cap);

            if (double.IsNaN(top) || double.IsInfinity(top) || top <= cap)
                return Math.Min(r, cap);

            if (r >= top)
                return cap;

            return Math.Min(Math.Pow(r, Math.Log(cap) / Math.Log(top)), cap);
        }

        /// <summary>The solo per-hit target: M x Compress(P / M). P itself for a non-positive P or median (no axis).</summary>
        public static double TargetPerHit(double perHit, double median, double cap, double top)
        {
            if (!(perHit > 0) || !(median > 0))
                return perHit;

            return median * Compress(perHit / median, cap, top);
        }

        /// <summary>The engine's damage rating multiplier (Creature.GetPositiveRatingMod) in double precision.</summary>
        internal static double RatingMod(long rating)
            => rating >= 0 ? (100.0 + rating) / 100.0 : 100.0 / (100.0 - rating);

        /// <summary>g = RatingMod(R + G) / RatingMod(R): how much the group bonus G multiplies a hit by. 1 for G at or below 0.</summary>
        public static double GroupFactor(int ownRating, int groupBonus)
            => groupBonus <= 0 ? 1.0 : RatingMod((long)ownRating + groupBonus) / RatingMod(ownRating);

        /// <summary>
        /// P_final = min(M x Compress(P / M), cap x M / g). P itself for a non-positive P or median. Never above P.
        /// </summary>
        public static double FinalPerHit(double perHit, double median, double cap, double top, int ownRating, int groupBonus)
        {
            if (!(perHit > 0) || !(median > 0))
                return perHit;

            var solo = TargetPerHit(perHit, median, cap, top);
            var groupCeiling = SanitizeCap(cap) * median / GroupFactor(ownRating, groupBonus);

            return Math.Min(perHit, Math.Min(solo, groupCeiling));
        }

        /// <summary>
        /// The share of the group bonus a creature whose compressed per-hit is <paramref name="compressedPerHit"/> can
        /// keep without passing the solo ceiling cap x M: clamp(floor((100 + R) x (cap x M / P' - 1)), 0, G).
        /// </summary>
        public static int AllowedGroupRating(double compressedPerHit, double median, double cap, int ownRating, int groupBonus)
        {
            if (groupBonus <= 0)
                return 0;

            if (!(compressedPerHit > 0) || !(median > 0))
                return groupBonus;

            var headroom = (100.0 + ownRating) * (SanitizeCap(cap) * median / compressedPerHit - 1.0);

            if (double.IsNaN(headroom))
                return 0;

            return (int)Math.Clamp(Math.Floor(headroom), 0, groupBonus);
        }

        /// <summary>
        /// The group rating the clamp withholds (the ability budget of a later step): G minus
        /// <see cref="AllowedGroupRating"/>. 0 on a solo plan, and 0 for an axis with no damage or no median.
        /// </summary>
        public static int WithheldRating(double compressedPerHit, double median, double cap, int ownRating, int groupBonus)
        {
            if (groupBonus <= 0 || !(compressedPerHit > 0) || !(median > 0))
                return 0;

            return groupBonus - AllowedGroupRating(compressedPerHit, median, cap, ownRating, groupBonus);
        }

        /// <summary><paramref name="value"/> x <paramref name="factor"/>, floored, never below 1 for a positive value; a factor of 1 or more leaves it.</summary>
        internal static int ScaleDown(int value, double factor)
        {
            if (value <= 0 || double.IsNaN(factor) || factor >= 1.0)
                return value;

            return (int)Math.Max(1.0, Math.Floor(value * Math.Max(0.0, factor)));
        }

        // ---- the live creature ----------------------------------------------------------------------------

        /// <summary>
        /// One live damage source: an item (null for the body) and its per-hit, on the missile axis or not. <see cref="Counted"/>
        /// is false for a source the engine does not hit with in the creature's current loadout (the body while a melee weapon is
        /// equipped): it is still scaled on its own factor, but it does not set the axis maximum.
        /// </summary>
        internal readonly struct LiveSource
        {
            public LiveSource(WorldObject item, double perHit, bool missile, bool counted = true)
            {
                Item = item;
                PerHit = perHit;
                Missile = missile;
                Counted = counted;
            }

            public WorldObject Item { get; }
            public double PerHit { get; }
            public bool Missile { get; }
            public bool Counted { get; }
        }

        /// <summary>
        /// True when the creature has a melee weapon equipped: an equipped MeleeWeapon, or an item wielded in the melee or
        /// two-handed slot. Monster_Melee.GetBaseDamage then hits with the weapon INSTEAD of the attack body part's DVal, so the
        /// body is not a melee source the engine uses. Read off EquippedObjects (not ParentLocation, which
        /// GetEquippedMeleeWeapon also checks) so it holds before EnterWorld.
        /// </summary>
        internal static bool HasEquippedMeleeWeapon(Creature creature)
            => creature?.EquippedObjects != null && creature.EquippedObjects.Values.Any(e => e != null
                && (e.WeenieType == WeenieType.MeleeWeapon || e.CurrentWieldedLocation == EquipMask.MeleeWeapon || e.CurrentWieldedLocation == EquipMask.TwoHanded));

        private static uint AttributeOf(Creature creature, PropertyAttribute attribute)
            => creature.Attributes.TryGetValue(attribute, out var a) && a != null ? a.Current : 0u;

        /// <summary>
        /// Every damage source the creature can hit with, measured live: the body (best part with a positive DVal, Breath
        /// included), every melee weapon and every thrown weapon in EquippedObjects AND Inventory (monsters swap weapons
        /// mid-fight), and every ammunition with the best launcher it can be fired from. Launchers themselves deal no
        /// damage and are never sources.
        /// </summary>
        internal static List<LiveSource> MeasureSources(Creature creature)
        {
            var sources = new List<LiveSource>();

            if (creature == null)
                return sources;

            var parts = creature.Biota?.PropertiesBodyPart;
            var maxDVal = 0;

            if (parts != null)
                foreach (var part in parts.Values)
                    if (part != null && part.DVal > maxDVal)
                        maxDVal = part.DVal;

            // The body counts toward the melee maximum only while no melee weapon is equipped (Monster_Melee.GetBaseDamage
            // uses the weapon instead of the body part); it is still listed, so Apply scales the parts on their own factor.
            if (maxDVal > 0)
                sources.Add(new LiveSource(null, Predict(maxDVal, AttributeOf(creature, PropertyAttribute.Strength), false), false,
                    counted: !HasEquippedMeleeWeapon(creature)));

            var items = creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(i => i != null).Distinct().ToList();
            var launchers = items.Where(i => i.WeenieType == WeenieType.MissileLauncher).ToList();

            foreach (var item in items)
            {
                var damage = item.GetProperty(PropertyInt.Damage) ?? 0;

                if (damage <= 0)
                    continue;

                var ownMod = item.GetProperty(PropertyFloat.DamageMod) ?? 1.0;
                var attr = item.WeaponSkill == Skill.FinesseWeapons ? PropertyAttribute.Coordination : PropertyAttribute.Strength;

                switch (item.WeenieType)
                {
                    case WeenieType.MeleeWeapon:
                        sources.Add(new LiveSource(item, Predict(damage * ownMod, AttributeOf(creature, attr), false), false));
                        break;

                    case WeenieType.Missile:
                        sources.Add(new LiveSource(item, Predict(damage * ownMod, AttributeOf(creature, attr), false), true));
                        break;

                    case WeenieType.Ammunition:
                        var best = launchers.Count == 0 ? Predict(damage, AttributeOf(creature, PropertyAttribute.Strength), false) : 0.0;

                        foreach (var launcher in launchers)
                        {
                            var bow = launcher.IsBow;
                            var launcherAttr = bow || launcher.WeaponSkill == Skill.FinesseWeapons ? PropertyAttribute.Coordination : PropertyAttribute.Strength;
                            best = Math.Max(best, Predict(damage * (launcher.GetProperty(PropertyFloat.DamageMod) ?? 1.0), AttributeOf(creature, launcherAttr), bow));
                        }

                        sources.Add(new LiveSource(item, best, true));
                        break;
                }
            }

            return sources;
        }

        /// <summary>The creature's live per-hit maximum on each axis (0 for an axis it has no source on).</summary>
        internal static (double Melee, double Missile) Measure(Creature creature)
        {
            var sources = MeasureSources(creature);

            return (sources.Where(s => s.Counted && !s.Missile).Select(s => s.PerHit).DefaultIfEmpty(0).Max(),
                    sources.Where(s => s.Counted && s.Missile).Select(s => s.PerHit).DefaultIfEmpty(0).Max());
        }

        /// <summary>
        /// Applies the ceiling to a live creature before EnterWorld. <paramref name="ownRating"/> is R (the creature's
        /// stamped DamageRating minus the group bonus), <paramref name="groupBonus"/> is G. Melee and missile sources are both
        /// read against the ONE shared physical <paramref name="median"/>, each source on its own factor. A median of 0 is a no-op.
        /// </summary>
        internal static DamageCeilingResult Apply(Creature creature, double median, double cap, double top,
            int ownRating, int groupBonus)
        {
            var result = new DamageCeilingResult();

            if (creature == null || !(median > 0))
                return result;

            cap = SanitizeCap(cap);

            var sources = MeasureSources(creature);

            double AxisMax(bool missile) => sources.Where(s => s.Counted && s.Missile == missile).Select(s => s.PerHit).DefaultIfEmpty(0).Max();

            result.MeleeBefore = AxisMax(false);
            result.MissileBefore = AxisMax(true);
            result.MeleeTarget = FinalPerHit(result.MeleeBefore, median, cap, top, ownRating, groupBonus);
            result.MissileTarget = FinalPerHit(result.MissileBefore, median, cap, top, ownRating, groupBonus);
            result.WithheldMelee = WithheldRating(TargetPerHit(result.MeleeBefore, median, cap, top), median, cap, ownRating, groupBonus);
            result.WithheldMissile = WithheldRating(TargetPerHit(result.MissileBefore, median, cap, top), median, cap, ownRating, groupBonus);

            foreach (var source in sources)
            {
                if (!(source.PerHit > 0))
                    continue;

                var factor = FinalPerHit(source.PerHit, median, cap, top, ownRating, groupBonus) / source.PerHit;

                if (!(factor < 1.0))
                    continue;

                if (source.Item == null)
                {
                    result.BodyFactor = factor;
                    result.PartsScaled = ScaleBodyParts(creature.Biota, factor);
                    continue;
                }

                var from = source.Item.GetProperty(PropertyInt.Damage) ?? 0;
                var to = ScaleDown(from, factor);

                if (to == from)
                    continue;

                source.Item.SetProperty(PropertyInt.Damage, to);
                result.Items.Add((source.Item.WeenieClassId, source.Item.Name, from, to));
            }

            return result;
        }

        /// <summary>
        /// Every part's positive DVal x <paramref name="factor"/>, floored, never below 1. CLONES the dictionary and every
        /// part first (PropertiesBodyPart is shared by reference with the cached weenie; BandUplift.ApplyBodyPartUplift
        /// and ReachUpStamp.ScaleBodyParts document the trap). Nothing is cloned when nothing would change. Returns how
        /// many parts changed.
        /// </summary>
        internal static int ScaleBodyParts(Biota biota, double factor)
        {
            var parts = biota?.PropertiesBodyPart;

            if (parts == null || parts.Count == 0 || !(factor < 1.0))
                return 0;

            var changed = 0;
            var fresh = new Dictionary<CombatBodyPart, PropertiesBodyPart>(parts.Count);

            foreach (var kvp in parts)
            {
                var part = kvp.Value?.Clone();

                if (part != null)
                {
                    var dval = ScaleDown(part.DVal, factor);

                    if (dval != part.DVal)
                        changed++;

                    part.DVal = dval;
                }

                fresh[kvp.Key] = part;
            }

            if (changed > 0)
                biota.PropertiesBodyPart = fresh;

            return changed;
        }
    }
}
