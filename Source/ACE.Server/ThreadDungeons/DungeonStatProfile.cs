using System.Collections.Generic;

using ACE.Entity.Enum;

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

        public DungeonStatProfile(int level, uint health, IReadOnlyDictionary<Skill, uint> skills, uint maxBodyDamage, uint maxBaseArmor,
            int maxSpellTier = 0)
        {
            Level = level;
            Health = health;
            Skills = skills ?? EmptySkills;
            MaxBodyDamage = maxBodyDamage;
            MaxBaseArmor = maxBaseArmor;
            MaxSpellTier = maxSpellTier;
        }

        private static readonly IReadOnlyDictionary<Skill, uint> EmptySkills = new Dictionary<Skill, uint>();

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
