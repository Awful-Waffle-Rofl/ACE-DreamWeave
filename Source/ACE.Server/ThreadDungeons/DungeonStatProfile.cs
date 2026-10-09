using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Everything the band-standard uplift needs to know about one weenie's authored combat statistics, read
    /// once per distinct wcid through a single delegate (<c>Func&lt;uint, DungeonStatProfile&gt;</c>).
    ///
    /// ONE delegate rather than one per axis. The pure builder already took <c>levelOf</c> and
    /// <c>healthOf</c>; the uplift needs three more axes (per-skill InitLevel, body-part damage, body-part
    /// armour), and a parameter per axis would grow the builder's signature every time an axis is added and
    /// force every unit test to stub several lambdas that all read the same weenie anyway.
    ///
    /// <see cref="Level"/> and <see cref="Health"/> are carried here as well even though the builder still
    /// takes <c>levelOf</c>/<c>healthOf</c> separately, and that redundancy is deliberate: those two are read
    /// for EVERY roster wcid on every run (1544 members across the shipped species tables), while a full
    /// profile is only ever needed for the far smaller uplift sample, so building a skills dictionary for all
    /// 1544 would be pure waste. ThreadDungeonSpawner resolves all three delegates from the SAME cached
    /// weenie, so they cannot disagree.
    ///
    /// Immutable, and deliberately a plain sealed class rather than a C# record: a record's generated
    /// equality would compare <see cref="Skills"/> by REFERENCE while reading like value equality, and
    /// nothing here is ever compared. <see cref="DungeonRosterPick"/> in this same namespace is shaped the
    /// same way for the same reason.
    ///
    /// A ZERO on any axis means "no authored data", never a genuine zero - the same convention
    /// ThreadDungeonSpawner.LiveHealthOf established for health, and the one
    /// <see cref="DungeonBandStandard"/> relies on when it excludes a member from its sample.
    /// </summary>
    public sealed class DungeonStatProfile
    {
        /// <summary>PropertyInt.Level, or 0 when the weenie is missing or carries none.</summary>
        public int Level { get; }

        /// <summary>Authored max health, by ThreadDungeonSpawner.HealthFromAttributes. 0 means "no data".</summary>
        public uint Health { get; }

        /// <summary>
        /// Authored InitLevel per skill, for every skill row the weenie carries. Never null - an empty
        /// dictionary is used instead, so callers never null-check. A row whose InitLevel is 0 is kept here
        /// (this type reports what the weenie says); DungeonBandStandard is what excludes it from a sample.
        /// </summary>
        public IReadOnlyDictionary<Skill, uint> Skills { get; }

        /// <summary>
        /// The largest PropertiesBodyPart.DVal across the weenie's body parts - the monster's best melee
        /// max damage, which is what Monster_Melee.GetBaseDamage reads off the chosen attack part. 0 means
        /// "no body-part damage data".
        /// </summary>
        public uint MaxBodyDamage { get; }

        /// <summary>
        /// The largest PropertiesBodyPart.BaseArmor across the weenie's body parts - the value
        /// Creature_BodyPart.GetEffectiveArmorVsType multiplies by the creature's armour-vs-type resistance.
        /// 0 means "no body-part armour data".
        /// </summary>
        public uint MaxBaseArmor { get; }

        /// <summary>
        /// The highest <see cref="DungeonSpellTier.TierOf"/> across the weenie's spell book - the tier a
        /// band-standard uplift raises a caster's spells TOWARD (see <see cref="DungeonBandStandard.SpellTier"/>).
        /// 0 means "no tierable spell" - either the weenie carries no spell book at all, or every spell it
        /// carries is untierable (a non-standard progression, e.g. a cantrip).
        /// </summary>
        public int MaxSpellTier { get; }

        /// <summary>
        /// PropertyFloat.HealthRate, the base the creature's health vital regenerates from
        /// (CreatureVital caches it into RegenRate at construction). 0 means "no authored data", on the same
        /// convention as every other axis here; a genuinely authored 0 is indistinguishable from an absent row
        /// and is treated identically, which is also what CreatureVital does (<c>?? 0</c>).
        ///
        /// Read only by boss normalization (DungeonBandStandard.HealthRate), and deliberately NOT counted by
        /// <see cref="HasData"/>: a member with a regen rate and nothing else says nothing about the band's
        /// combat standard, and admitting it would let such profiles satisfy DungeonBandStandard.MinSample.
        /// </summary>
        public double HealthRate { get; }

        /// <summary>
        /// <paramref name="healthRate"/> is appended AFTER the existing optional parameter, never inserted
        /// before it, so every positional caller keeps binding exactly as it did.
        /// </summary>
        public DungeonStatProfile(int level, uint health, IReadOnlyDictionary<Skill, uint> skills, uint maxBodyDamage, uint maxBaseArmor,
            int maxSpellTier = 0, double healthRate = 0.0, IReadOnlyDictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint> attributes = null)
        {
            Level = level;
            Health = health;
            Skills = skills ?? EmptySkills;
            MaxBodyDamage = maxBodyDamage;
            MaxBaseArmor = maxBaseArmor;
            MaxSpellTier = maxSpellTier;
            HealthRate = double.IsNaN(healthRate) || double.IsInfinity(healthRate) || healthRate < 0 ? 0.0 : healthRate;
            Attributes = attributes ?? EmptyAttributes;
        }

        /// <summary>
        /// Authored base per primary attribute (InitLevel + LevelFromCP, i.e. CreatureAttribute.Base for a
        /// non-player), for every attribute row the weenie carries. Never null. Read only by boss normalization
        /// (DungeonBandStandard.AttributeMedians): a skill's Current is AttributeFormula + InitLevel + Ranks
        /// (CreatureSkill.cs:170-179), so a boss can only land on its band's skill standard if its attributes do too.
        /// Like <see cref="HealthRate"/> it is NOT counted by <see cref="HasData"/>, so it cannot change which
        /// members satisfy DungeonBandStandard.MinSample.
        /// </summary>
        public IReadOnlyDictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint> Attributes { get; }

        /// <summary>
        /// This profile with <see cref="Level"/> replaced and every other axis shared by reference (all of them are
        /// immutable). The Threads reach-up projection reads a creature at its projected level, and the band
        /// standard's sample filters on this field rather than on the builder's levelOf delegate, so a projected
        /// sample needs a projected profile. Returns this same instance when the level is unchanged.
        /// </summary>
        public DungeonStatProfile WithLevel(int level)
            => level == Level ? this : new DungeonStatProfile(level, Health, Skills, MaxBodyDamage, MaxBaseArmor, MaxSpellTier, HealthRate, Attributes);

        private static readonly IReadOnlyDictionary<Skill, uint> EmptySkills = new Dictionary<Skill, uint>();

        private static readonly IReadOnlyDictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint> EmptyAttributes =
            new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint>();

        /// <summary>
        /// The "nothing known about this wcid" profile, returned for a weenie that is missing from the world
        /// cache. Every axis reads 0, which every consumer already treats as "no authored data".
        /// </summary>
        public static readonly DungeonStatProfile Empty = new DungeonStatProfile(0, 0, null, 0, 0);

        /// <summary>
        /// True when this profile contributes at least one usable data point to a band sample. A member with
        /// no skills, no body-part damage and no body-part armour tells the standard nothing, so counting it
        /// toward <see cref="DungeonBandStandard.MinSample"/> would let a sample of empty profiles satisfy the
        /// threshold and produce a standard of all zeroes - which is a silent no-op rather than a visible
        /// failure, and so the worse of the two outcomes.
        /// </summary>
        public bool HasData => MaxBodyDamage > 0 || MaxBaseArmor > 0 || HasAnySkill;

        /// <summary>
        /// CreatureVital.GetMaxValue's formula (StartingValue + Ranks + attr) applied to a weenie's authored
        /// MaxHealth/Endurance rows: maxHealthInitLevel plus maxHealthLevelFromCP plus
        /// floor(enduranceInitLevel / 2). Lives here (moved from ThreadDungeonSpawner, which keeps a delegating
        /// stub) so World Events can reuse the SAME arithmetic without depending on the Threads spawner.
        /// </summary>
        public static uint HealthFromAttributes(uint maxHealthInitLevel, uint maxHealthLevelFromCP, uint enduranceInitLevel)
        {
            return maxHealthInitLevel + maxHealthLevelFromCP + enduranceInitLevel / 2;
        }

        /// <summary>
        /// The weenie-to-health extraction, in ONE place (moved from ThreadDungeonSpawner.HealthOfWeenie).
        /// Both attribute dictionaries can be null - ACE.Entity.Models.Weenie initializes only populated
        /// collections - so a missing weenie or a missing MaxHealth entry returns 0, which BandMedianHealth
        /// reads as "no data". Takes the weenie so it is unit-testable without a world database.
        /// </summary>
        public static uint HealthOf(ACE.Entity.Models.Weenie weenie)
        {
            if (weenie == null)
                return 0;

            if (weenie.PropertiesAttribute2nd == null
                || !weenie.PropertiesAttribute2nd.TryGetValue(ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth, out var maxHealth))
                return 0;

            var endurance = 0u;
            if (weenie.PropertiesAttribute != null
                && weenie.PropertiesAttribute.TryGetValue(ACE.Entity.Enum.Properties.PropertyAttribute.Endurance, out var enduranceAttr))
                endurance = enduranceAttr.InitLevel;

            return HealthFromAttributes(maxHealth.InitLevel, maxHealth.LevelFromCP, endurance);
        }

        /// <summary>
        /// Pure core of the band-standard profile resolver (moved from ThreadDungeonSpawner.ProfileOfWeenie),
        /// taking the weenie so it is unit-testable without a world database.
        ///
        /// Every collection on ACE.Entity.Models.Weenie can be null, so each is checked before it is walked,
        /// and a weenie missing one simply reports 0 on that axis ("no authored data").
        ///
        /// Body-part damage is the MAXIMUM DVal across the parts rather than a mean: Monster_Melee.GetBaseDamage
        /// reads whichever single part the attack animation selected, and GetAttackPart already filters to
        /// parts with a nonzero DVal, so the largest is what the creature can actually hit for. Armour is the
        /// maximum for the parallel reason.
        /// </summary>
        public static DungeonStatProfile FromWeenie(ACE.Entity.Models.Weenie weenie)
        {
            if (weenie == null)
                return Empty;

            var level = weenie.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level) ?? 0;

            var skills = new Dictionary<Skill, uint>();

            if (weenie.PropertiesSkill != null)
            {
                foreach (var kvp in weenie.PropertiesSkill)
                {
                    if (kvp.Value != null)
                        skills[kvp.Key] = kvp.Value.InitLevel;
                }
            }

            var maxDamage = 0;
            var maxArmor = 0;

            if (weenie.PropertiesBodyPart != null)
            {
                foreach (var kvp in weenie.PropertiesBodyPart)
                {
                    if (kvp.Value == null)
                        continue;

                    if (kvp.Value.DVal > maxDamage) maxDamage = kvp.Value.DVal;
                    if (kvp.Value.BaseArmor > maxArmor) maxArmor = kvp.Value.BaseArmor;
                }
            }

            // Attribute bases for boss normalization's attribute medians. InitLevel + LevelFromCP is
            // CreatureAttribute.Base for a non-player (CreatureAttribute.cs:102-106).
            var attributes = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint>();

            if (weenie.PropertiesAttribute != null)
            {
                foreach (var kvp in weenie.PropertiesAttribute)
                {
                    if (kvp.Value != null)
                        attributes[kvp.Key] = kvp.Value.InitLevel + kvp.Value.LevelFromCP;
                }
            }

            var maxSpellTier = 0;

            if (weenie.PropertiesSpellBook != null)
            {
                foreach (var id in weenie.PropertiesSpellBook.Keys)
                {
                    var tier = DungeonSpellTier.TierOf(id);

                    if (tier > maxSpellTier)
                        maxSpellTier = tier;
                }
            }

            // Clamped at 0: a negative authored DVal or BaseArmor would be nonsense data, and a negative
            // reaching a uint cast wraps to something enormous rather than failing. HealthRate is read with
            // the same "absent is 0" rule CreatureVital applies when it caches the rate (CreatureVital.cs:38).
            return new DungeonStatProfile(level, HealthOf(weenie), skills,
                (uint)Math.Max(0, maxDamage), (uint)Math.Max(0, maxArmor), maxSpellTier,
                weenie.GetProperty(ACE.Entity.Enum.Properties.PropertyFloat.HealthRate) ?? 0.0, attributes);
        }

        private bool HasAnySkill
        {
            get
            {
                foreach (var kvp in Skills)
                {
                    if (kvp.Value > 0)
                        return true;
                }

                return false;
            }
        }
    }
}
