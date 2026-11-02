using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Keyed like ArmorKey, but by the monster's own CombatBodyPart: a creature defender is struck on the parts its
    /// weenie's body part table lists, not on the player BodyPart bands. The two ignore-magic booleans are attacker
    /// terms and stay key axes for the same reason they do on ArmorKey.
    /// </summary>
    public readonly struct MonsterArmorKey : IEquatable<MonsterArmorKey>
    {
        public readonly CombatBodyPart BodyPart;
        public readonly DamageType DamageType;
        public readonly bool IgnoreMagicArmor;
        public readonly bool IgnoreMagicResist;

        public MonsterArmorKey(CombatBodyPart bodyPart, DamageType damageType, bool ignoreMagicArmor, bool ignoreMagicResist)
        {
            BodyPart = bodyPart;
            DamageType = damageType;
            IgnoreMagicArmor = ignoreMagicArmor;
            IgnoreMagicResist = ignoreMagicResist;
        }

        public bool Equals(MonsterArmorKey other) =>
            BodyPart == other.BodyPart && DamageType == other.DamageType && IgnoreMagicArmor == other.IgnoreMagicArmor && IgnoreMagicResist == other.IgnoreMagicResist;

        public override bool Equals(object obj) => obj is MonsterArmorKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(BodyPart, DamageType, IgnoreMagicArmor, IgnoreMagicResist);
    }

    /// <summary>
    /// The defender-side resistance terms of a creature, per damage type. The engine's creature-defender resistance
    /// is Math.Max(0, Natural * GetResistanceMod(damageType, attacker, weapon, weaponResistanceMod))
    /// (Entity\DamageEvent.cs, DoCalculateDamage; Creature_Properties.cs, GetResistanceMod(ResistanceType, ...)
    /// and GetResistanceMod(DamageType, ...)), and for a non-hollow attacker the inner term is
    /// P0 * max(V0, weaponResistanceMod) * HuntersMark. A full-hollow attacker replaces P0 * max(V0, ...) with
    /// weaponResistanceMod alone.
    /// </summary>
    public readonly struct MonsterResistanceRow
    {
        /// <summary>The weenie's own ResistX float (ResistSlash, ResistFire, ...), 1.0 when unset.</summary>
        public readonly float Natural;

        /// <summary>Protection enchantments, already floored against the creature's natural resistance (1.0).</summary>
        public readonly float P0;

        /// <summary>Vulnerability enchantments, RAW: the attacker's weapon resistance mod floors it at call time.</summary>
        public readonly float V0;

        /// <summary>
        /// Creature.GetHuntersMarkMod times Creature.GetElementalLightningMod: the separate damage-taken
        /// multipliers outside the vulnerability max.
        /// </summary>
        public readonly float HuntersMark;

        public MonsterResistanceRow(float natural, float p0, float v0, float huntersMark)
        {
            Natural = natural;
            P0 = p0;
            V0 = v0;
            HuntersMark = huntersMark;
        }
    }

    /// <summary>
    /// A flat numeric snapshot of one MONSTER's defensive posture, the counterpart of DefenderProfile for the
    /// player-attacks-monster direction. Holds no WorldObject, so it is safe to cache and read from any thread.
    /// Build with <see cref="Build"/>; apply with MonsterCombatSimulator.Analytic.
    ///
    /// Every term is read from the production function the engine's creature-defender branch calls
    /// (Entity\DamageEvent.cs, DoCalculateDamage, the "playerDefender == null" arms); what is written here is the
    /// composition, and the split of each term into the attacker-independent part stored here and the
    /// attacker-dependent part applied at call time - the same factorisation DefenderProfile makes for players.
    /// </summary>
    public class MonsterDefenderProfile
    {
        public uint WeenieClassId { get; init; }
        public string Label { get; init; }
        public int Level { get; init; }
        public uint MaxHealth { get; init; }

        /// <summary>
        /// The combat mode the profile was built in: the creature's attack stance (Melee, or Missile when it is
        /// ranged), because a creature's melee weapon defense modifier is 1.0 out of stance and a monster that is
        /// fighting is in stance. NonCombat only when entering the stance threw.
        /// </summary>
        public CombatMode Stance { get; init; }

        /// <summary>
        /// Effective defense per attack type: GetEffectiveDefenseSkill(Melee / Missile) - the evade roll's defense
        /// (DamageEvent.GetEvadeChance) - and GetEffectiveMagicDefense for Magic, the projectile resist roll's
        /// (WorldObject_Magic.cs TryResistSpell). Note the missile and magic WEAPON defense modifiers are 1.0 for
        /// every non-player: both read GetWeapon(wielder as Player) (WorldObject_Weapon.cs).
        /// </summary>
        public IReadOnlyDictionary<CombatType, uint> DefenseSkills { get; init; }

        /// <summary>
        /// Effective armor level per (part, damage type, ignore-magic-armor, ignore-magic-resist), with
        /// armorRendingMod fixed at 1.0 and stopping short of SkillFormula.CalcArmorMod - the attacker's rending is
        /// applied at call time, before the curve, which is the engine's order (Creature_BodyPart.cs,
        /// GetEffectiveArmorVsType). The row with both flags false IS that production function's return value;
        /// the hollow rows compose its terms at full intensity.
        /// </summary>
        public IReadOnlyDictionary<MonsterArmorKey, float> EffectiveArmorLevel { get; init; }

        /// <summary>
        /// The body part hit weights per quadrant, indexed by QuadrantIndex (0..11), from the same
        /// BodyPartTable the engine rolls (Creature.GetBodyParts(wcid)): one (part, weight) entry per part with a
        /// positive weight in that quadrant. A quadrant with no entries is one the engine treats as an EVADE
        /// (DamageEvent.GetBodyPart, CombatBodyPart.Undefined).
        /// </summary>
        public IReadOnlyList<IReadOnlyList<KeyValuePair<CombatBodyPart, float>>> QuadrantWeights { get; init; }

        public IReadOnlyDictionary<DamageType, MonsterResistanceRow> Resistance { get; init; }

        /// <summary>The same shield rows ProfileBuilder builds for a player, through the same composition.</summary>
        public IReadOnlyDictionary<ShieldKey, ShieldRow> Shield { get; init; }

        /// <summary>GetDamageResistRatingMod(combatType, true, null) per combat type.</summary>
        public IReadOnlyDictionary<CombatType, float> DamageResistRatingMod { get; init; }

        private static readonly DamageType[] DamageTypes =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon,
            DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Nether,
        };

        /// <summary>
        /// Walks a creature into a profile. The creature is put into its attack stance first, through the real
        /// SetCombatMode (AttackerTerms.EnterCreatureStance), and is left there.
        ///
        /// NEEDS the creature's BodyPartTable: Creature.GetBodyParts(wcid) reads the world database's cached weenie
        /// (Monster_Combat.cs) unless the table was seeded. A creature with no table yields no quadrant weights, and
        /// MonsterCombatSimulator then reports a zero hit chance - the engine evades every hit on it.
        ///
        /// Monster-side terms this does NOT capture, which the table must disclose: monster combat effect
        /// avoidance (RollMonsterEffectAvoidance, rolled before the evade) and damage absorption (wards, ImmuneEffect:
        /// AbsorbMonsterEffectDamage, TakeDamage filters), and anything in Creature.TakeDamage after the hit lands.
        /// </summary>
        public static MonsterDefenderProfile Build(Creature creature)
        {
            if (creature == null)
                throw new ArgumentNullException(nameof(creature));

            if (creature is Player)
                throw new ArgumentException("A Player defender is profiled by ProfileBuilder, not MonsterDefenderProfile.", nameof(creature));

            AttackerTerms.EnterCreatureStance(creature, creature.IsRanged ? CombatMode.Missile : CombatMode.Melee);

            var armor = new Dictionary<MonsterArmorKey, float>();
            var resistance = new Dictionary<DamageType, MonsterResistanceRow>();
            var shield = new Dictionary<ShieldKey, ShieldRow>();

            var bodyParts = creature.Biota.PropertiesBodyPart ?? new Dictionary<CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart>();

            foreach (var damageType in DamageTypes)
            {
                foreach (var kvp in bodyParts)
                {
                    var part = new Creature_BodyPart(creature, kvp);
                    var layers = part.GetArmorLayers(kvp.Key);

                    foreach (var ignoreMagicArmor in new[] { false, true })
                    {
                        foreach (var ignoreMagicResist in new[] { false, true })
                        {
                            armor[new MonsterArmorKey(kvp.Key, damageType, ignoreMagicArmor, ignoreMagicResist)] =
                                ComposeEffectiveArmorLevel(creature, part, layers, damageType, ignoreMagicArmor, ignoreMagicResist);
                        }
                    }
                }

                resistance[damageType] = BuildResistanceRow(creature, damageType);

                foreach (var ignoreMagicArmor in new[] { false, true })
                    shield[new ShieldKey(damageType, ignoreMagicArmor)] = BuildShieldRow(creature, damageType, ignoreMagicArmor);
            }

            var quadrants = new List<IReadOnlyList<KeyValuePair<CombatBodyPart, float>>>();
            var table = Creature.GetBodyParts(creature.WeenieClassId);

            for (var i = 0; i < 12; i++)
            {
                if (table == null)
                    quadrants.Add(new List<KeyValuePair<CombatBodyPart, float>>());
                else
                    quadrants.Add(table.Quadrants[i].Select(p => new KeyValuePair<CombatBodyPart, float>(p.BodyPart, p.Probability)).ToList());
            }

            return new MonsterDefenderProfile
            {
                WeenieClassId = creature.WeenieClassId,
                Label = $"{creature.Name} (wcid {creature.WeenieClassId})",
                Level = creature.Level ?? 0,
                MaxHealth = creature.Health.MaxValue,
                Stance = creature.CombatMode,
                DefenseSkills = new Dictionary<CombatType, uint>
                {
                    { CombatType.Melee, creature.GetEffectiveDefenseSkill(CombatType.Melee) },
                    { CombatType.Missile, creature.GetEffectiveDefenseSkill(CombatType.Missile) },
                    { CombatType.Magic, creature.GetEffectiveMagicDefense() },
                },
                EffectiveArmorLevel = armor,
                QuadrantWeights = quadrants,
                Resistance = resistance,
                Shield = shield,
                DamageResistRatingMod = new Dictionary<CombatType, float>
                {
                    { CombatType.Melee, creature.GetDamageResistRatingMod(CombatType.Melee, true, null) },
                    { CombatType.Missile, creature.GetDamageResistRatingMod(CombatType.Missile, true, null) },
                    { CombatType.Magic, creature.GetDamageResistRatingMod(CombatType.Magic, true, null) },
                },
            };
        }

        /// <summary>
        /// Creature_BodyPart.GetEffectiveArmorVsType at rending 1.0. The no-hollow row calls it directly (a null
        /// attacker and weapon resolve hollow to 0). The hollow rows reproduce its three terms at FULL intensity,
        /// where the production function zeroes each ignored enchantment term flatly: ignore-magic-resist drops the
        /// creature's body armor enchantment (imperil, armor self), and ignore-magic-armor drops each worn layer's
        /// armor-level and bane enchantments, which Creature_BodyPart.GetArmorMod does itself when handed
        /// intensity 1.0.
        /// </summary>
        internal static float ComposeEffectiveArmorLevel(Creature creature, Creature_BodyPart part, List<WorldObject> layers, DamageType damageType, bool ignoreMagicArmor, bool ignoreMagicResist)
        {
            if (!ignoreMagicArmor && !ignoreMagicResist)
                return part.GetEffectiveArmorVsType(damageType, layers, null, null, 1.0f);

            var effectiveAL = part.Biota.Value.BaseArmor * (float)creature.GetArmorVsType(damageType);

            if (!ignoreMagicResist)
                effectiveAL += creature.EnchantmentManager.GetBodyArmorMod();

            foreach (var layer in layers)
                effectiveAL += part.GetArmorMod(layer, damageType, ignoreMagicArmor ? 1.0 : 0.0);

            return effectiveAL;
        }

        private static MonsterResistanceRow BuildResistanceRow(Creature creature, DamageType damageType)
        {
            var protMod = creature.EnchantmentManager.GetProtectionResistanceMod(damageType);
            var vulnMod = creature.EnchantmentManager.GetVulnerabilityResistanceMod(damageType);

            // Creature_Properties.cs GetResistanceMod(DamageType, ...): protection becomes either life protection
            // or natural resistance, whichever is lower. A creature's GetNaturalResistance is 1.0 (only Player
            // overrides it); the player-only resistance augmentation does not apply to a creature
            var naturalResistMod = creature.GetNaturalResistance(damageType);

            if (protMod > naturalResistMod)
                protMod = naturalResistMod;

            // the engine multiplies Elemental Proficiency Lightning beside the mark on the same sites, so the
            // row's separate-multiplier slot carries both (1.0 x 1.0 on a creature carrying neither)
            return new MonsterResistanceRow(NaturalResist(creature, damageType), protMod, vulnMod, creature.GetHuntersMarkMod() * creature.GetElementalLightningMod());
        }

        /// <summary>
        /// The ResistX multiplier GetResistanceMod(ResistanceType, ...) applies per type (Creature_Properties.cs),
        /// "(ResistSlash ?? 1.0)" and its seven siblings.
        /// </summary>
        internal static float NaturalResist(Creature creature, DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Slash: return (float)(creature.ResistSlash ?? 1.0);
                case DamageType.Pierce: return (float)(creature.ResistPierce ?? 1.0);
                case DamageType.Bludgeon: return (float)(creature.ResistBludgeon ?? 1.0);
                case DamageType.Fire: return (float)(creature.ResistFire ?? 1.0);
                case DamageType.Cold: return (float)(creature.ResistCold ?? 1.0);
                case DamageType.Acid: return (float)(creature.ResistAcid ?? 1.0);
                case DamageType.Electric: return (float)(creature.ResistElectric ?? 1.0);
                case DamageType.Nether: return (float)(creature.ResistNether ?? 1.0);
                default: return 1.0f;
            }
        }

        /// <summary>
        /// Creature.GetShieldMod's shielded branch up to the shield-skill cap, through
        /// ProfileBuilder.ComposeShieldLevel - the same composition the player profile uses, at the non-PvP,
        /// full-intensity resolution of ignore-magic-armor. A creature never takes the Defensive Weapon terms (they are
        /// is Player-only), so no shield means 0, which CalcArmorMod turns into the engine's 1.0.
        /// </summary>
        private static ShieldRow BuildShieldRow(Creature creature, DamageType damageType, bool ignoreMagicArmor)
        {
            var shield = creature.GetEquippedShield();

            if (shield == null)
                return new ShieldRow(0.0f);

            var baseSL = (shield.GetProperty(PropertyInt.ArmorLevel) ?? 0.0f) + ACE.Server.KnotLoot.KnotTraitCombat.ShieldArmorBonus(shield, creature);
            var modSL = shield.EnchantmentManager.GetArmorMod();
            var baseRL = creature.GetResistance(shield, damageType);
            var modRL = shield.EnchantmentManager.GetArmorModVsType(damageType);

            var shieldSkill = creature.GetCreatureSkill(Skill.Shield);

            return new ShieldRow(ProfileBuilder.ComposeShieldLevel(
                baseSL, modSL, baseRL, modRL, ignoreMagicArmor,
                shieldSkill.Current,
                shieldSkill.AdvancementClass == SkillAdvancementClass.Specialized));
        }
    }
}
