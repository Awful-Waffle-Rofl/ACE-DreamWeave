using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Entity
{
    /// <summary>What a caster pet may do with one of its spells.</summary>
    public enum CasterSpellKind
    {
        /// <summary>Never cast by the brain: a harmful self-targeted spell, a self-targeted strip, a portal spell, a missing spell.</summary>
        Unusable,

        /// <summary>A beneficial vital boost (Heal, Revitalize, Mana Boost): owner first, then sibling pets.</summary>
        Heal,

        /// <summary>A beneficial enchantment: cast on the owner only, never on a sibling.</summary>
        Buff,

        /// <summary>A harmful spell (war, void, debuff, harm, drain): cast only on an engaged enemy.</summary>
        Harmful,

        /// <summary>
        /// A dispel aligned Negative (removes harmful enchantments), the Other variant: cast on the owner only, and only
        /// while the owner carries a harmful enchantment of the spell's dispel school.
        /// </summary>
        Cleanse,

        /// <summary>
        /// A Cleanse's Self variant. The engine applies a Self spell to the caster, so this one is cast on the PET itself,
        /// while the pet carries a harmful enchantment of the spell's dispel school.
        /// </summary>
        CleanseSelf,

        /// <summary>
        /// A dispel aligned Positive or All, the Other variant (removes beneficial enchantments): cast on an engaged enemy
        /// only - never on the owner or a sibling, whatever its alignment - and Redundant on an enemy that carries no
        /// beneficial enchantment of the spell's dispel school.
        /// </summary>
        Strip,
    }

    /// <summary>The vital a Heal-kind spell restores.</summary>
    public enum CasterVital
    {
        Health,
        Stamina,
        Mana,
    }

    /// <summary>
    /// One spell a caster pet can cast, reduced to the facts the brain needs. Range is already resolved for
    /// this pet (its own magic skill) - see CombatPet.BuildCasterSpells.
    /// </summary>
    public readonly struct CasterSpell
    {
        public int SpellId { get; }
        public CasterSpellKind Kind { get; }

        /// <summary>The vital a Heal restores. Ignored for every other kind.</summary>
        public CasterVital Vital { get; }

        /// <summary>The spell's stacking group, for the Buff equal-or-greater check.</summary>
        public SpellCategory Category { get; }

        /// <summary>The spell's power, the number the engine stacks a category on (EnchantmentManager.Add).</summary>
        public uint Power { get; }

        /// <summary>The Harmful spell's shape, for MonsterSpellSelector.Classify.</summary>
        public MonsterSpellShape Shape { get; }

        /// <summary>Metres within which the spell may be cast at its target.</summary>
        public float Range { get; }

        /// <summary>What one cast costs this pet (Creature.CalculateManaUsage, resolved once per pet).</summary>
        public uint ManaCost { get; }

        /// <summary>A projectile spell (bolt, streak, ring, ...): needs direct line of sight to its target.</summary>
        public bool IsProjectile { get; }

        /// <summary>
        /// The school a Cleanse / CleanseSelf / Strip dispels (SelectDispel's dispel_school). None means every school.
        /// Ignored for every other kind.
        /// </summary>
        public MagicSchool DispelSchool { get; }

        /// <summary>
        /// The power window a dispel removes (SelectDispel keeps only entries whose PowerLevel lies between MinPower and
        /// MaxPower). Defaults to unbounded. Ignored for every kind but the dispels.
        /// </summary>
        public int MinPower { get; }

        public int MaxPower { get; }

        public CasterSpell(int spellId, CasterSpellKind kind, CasterVital vital = CasterVital.Health, SpellCategory category = SpellCategory.Undef,
            uint power = 0, MonsterSpellShape shape = default, float range = float.PositiveInfinity, uint manaCost = 0, bool isProjectile = false,
            MagicSchool dispelSchool = MagicSchool.None, int minPower = int.MinValue, int maxPower = int.MaxValue)
        {
            DispelSchool = dispelSchool;
            MinPower = minPower;
            MaxPower = maxPower;
            SpellId = spellId;
            Kind = kind;
            Vital = vital;
            Category = category;
            Power = power;
            Shape = shape;
            Range = range;
            ManaCost = manaCost;
            IsProjectile = isProjectile;
        }
    }

    /// <summary>The three vitals of one ally as fractions of their maxima (1.0 = full).</summary>
    public readonly struct CasterVitals
    {
        public float Health { get; }
        public float Stamina { get; }
        public float Mana { get; }

        public CasterVitals(float health, float stamina = 1.0f, float mana = 1.0f)
        {
            Health = health;
            Stamina = stamina;
            Mana = mana;
        }

        public float Get(CasterVital vital)
        {
            switch (vital)
            {
                case CasterVital.Stamina:
                    return Stamina;
                case CasterVital.Mana:
                    return Mana;
                default:
                    return Health;
            }
        }

        /// <summary>current / max, with an empty or missing maximum reading as full (nothing to restore).</summary>
        public static float Fraction(uint current, uint max) => max == 0 ? 1.0f : (float)current / max;
    }

    /// <summary>A friendly target the brain may heal: its vitals and its distance from the pet.</summary>
    public readonly struct CasterAlly
    {
        public CasterVitals Vitals { get; }
        public float Distance { get; }

        public CasterAlly(CasterVitals vitals, float distance)
        {
            Vitals = vitals;
            Distance = distance;
        }
    }

    /// <summary>
    /// Everything PetCasterBrain.Decide reads, as values. The world-touching half that fills it is
    /// CombatPet.BuildCasterContext, which is also where every cross-object read is guarded.
    ///
    /// The two delegates are LAZY on purpose: the owner's enchantment snapshot and an enemy's debuff profile
    /// both clone a registry, so Decide only asks for them once the cheaper steps above have not decided.
    /// </summary>
    public sealed class CasterContext
    {
        /// <summary>
        /// FALSE when the owner is null, dead, teleporting, in another instance or in another landblock group.
        /// Nothing else in the context is read then, and the decision is Follow.
        /// </summary>
        public bool OwnerAvailable { get; set; }

        public CasterAlly Owner { get; set; }

        /// <summary>The owner's other active combat pets the pet may heal, already filtered to the pet's group.</summary>
        public IReadOnlyList<CasterAlly> Siblings { get; set; } = Array.Empty<CasterAlly>();

        /// <summary>This pet's castable spells, in spellbook order.</summary>
        public IReadOnlyList<CasterSpell> Spells { get; set; } = Array.Empty<CasterSpell>();

        /// <summary>The owner's top-layer enchantment per spell category, or null for none. Lazy.</summary>
        public Func<IReadOnlyDictionary<SpellCategory, CarriedEnchantment>> OwnerTopLayers { get; set; }

        /// <summary>
        /// The pet's OWN top-layer enchantments, with each layer's alignment and school resolved (a CleanseSelf reads
        /// it). Lazy. The pet's registry is on its own tick thread, so this needs no thread guard.
        /// </summary>
        public Func<IReadOnlyDictionary<SpellCategory, CarriedEnchantment>> PetTopLayers { get; set; }

        /// <summary>
        /// Distance to each engaged enemy, in preference order: the pet's current target first, then the rest
        /// nearest first. Only monsters already engaged with the owner's side are ever listed.
        /// </summary>
        public IReadOnlyList<float> EnemyDistances { get; set; } = Array.Empty<float>();

        /// <summary>The debuffs standing on enemy i (an index into EnemyDistances). Lazy.</summary>
        public Func<int, VulnerabilityProfile> EnemyProfile { get; set; }

        /// <summary>
        /// Whether the pet has direct line of sight to enemy i (Creature.IsDirectVisible, the check Monster_Combat's
        /// GetMaxRange makes before a projectile spell). Lazy: asked at most once per enemy, and only when a
        /// projectile spell is weighed against it. Null means "always" (tests that do not care).
        /// </summary>
        public Func<int, bool> EnemyHasLos { get; set; }

        /// <summary>
        /// The pet's current mana. A spell costing more is never chosen, so a dry pet falls through to a cheaper
        /// spell or Follow instead of choosing a cast it cannot pay for on every tick. uint.MaxValue for a pet that
        /// does not spend mana (AiUsesMana false), and by default.
        /// </summary>
        public uint PetMana { get; set; } = uint.MaxValue;
    }

    public enum CasterAction
    {
        Follow,
        Cast,
        MoveInRange,
    }

    public enum CasterTargetKind
    {
        None,
        Owner,
        Sibling,
        Enemy,

        /// <summary>The casting pet itself (a CleanseSelf).</summary>
        Self,
    }

    /// <summary>
    /// The brain's verdict: Cast(spell, target), MoveInRange(target) or Follow. A target is named by kind plus
    /// an index into CasterContext.Siblings / EnemyDistances, so the decision stays pure and the tick maps it
    /// back to the object.
    /// </summary>
    public readonly struct CasterDecision : IEquatable<CasterDecision>
    {
        public CasterAction Action { get; }
        public int SpellId { get; }
        public CasterTargetKind Target { get; }
        public int TargetIndex { get; }

        private CasterDecision(CasterAction action, int spellId, CasterTargetKind target, int targetIndex)
        {
            Action = action;
            SpellId = spellId;
            Target = target;
            TargetIndex = targetIndex;
        }

        public static readonly CasterDecision Follow = new CasterDecision(CasterAction.Follow, 0, CasterTargetKind.None, -1);

        public static CasterDecision Cast(int spellId, CasterTargetKind target, int targetIndex = 0) =>
            new CasterDecision(CasterAction.Cast, spellId, target, targetIndex);

        /// <summary>Close on the target so <paramref name="spellId"/> can reach it.</summary>
        public static CasterDecision MoveInRange(int spellId, CasterTargetKind target, int targetIndex = 0) =>
            new CasterDecision(CasterAction.MoveInRange, spellId, target, targetIndex);

        public bool Equals(CasterDecision other) =>
            Action == other.Action && SpellId == other.SpellId && Target == other.Target && TargetIndex == other.TargetIndex;

        public override bool Equals(object obj) => obj is CasterDecision other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Action, SpellId, Target, TargetIndex);

        public override string ToString() => Action == CasterAction.Follow ? "Follow" : $"{Action}({SpellId}, {Target}[{TargetIndex}])";
    }

    /// <summary>
    /// Custom Summons Task 6: the caster pet brain. Pure - no Creature, Player, DatManager or PropertyManager -
    /// so every rule below is tested as a table (PetCasterBrainTests). CombatPet.TickCaster runs it on each
    /// monster tick and carries the decision out.
    ///
    /// Order (design doc "Casting behaviour" and "Calls I made"):
    ///   1. the owner below HealThreshold in a vital a bound heal restores: heal the owner (Health first);
    ///   2. else the lowest sibling pet below HealThreshold: heal it (out of range: move in range of it);
    ///   2b. else a bound cleanse (a Negative-aligned dispel) whose dispel school matches a harmful enchantment the owner
    ///      carries (the Other variant, owner only; out of range: move in range of the owner), or the pet itself carries
    ///      (the Self variant, cast on the pet);
    ///   3. else a bound buff the owner lacks at equal or greater power in its category: buff the owner;
    ///   4. else a bound harmful spell, or a strip (a Positive or All aligned dispel, Redundant on an enemy with nothing it can remove), that is not Redundant (and, for a projectile, has line of sight) on an engaged enemy: the current target first, then
    ///      the next engaged enemy. In range: cast. Out of range: move in range (rings close in);
    ///   5. else Follow. A caster never melees. A spell costing more than PetMana is never chosen at any step.
    /// </summary>
    public static class PetCasterBrain
    {
        /// <summary>Heal priority: a vital below this fraction of its maximum gets healed (design doc: 75%).</summary>
        public const float HealThreshold = 0.75f;

        /// <summary>The order heals are considered in: health is the heal priority, then the other two.</summary>
        private static readonly CasterVital[] HealOrder = { CasterVital.Health, CasterVital.Stamina, CasterVital.Mana };

        public static CasterDecision Decide(CasterContext ctx)
        {
            if (ctx == null || !ctx.OwnerAvailable)
                return CasterDecision.Follow;

            var spells = ctx.Spells;
            var mana = ctx.PetMana;

            // 1. the owner's heal. Out of range moves the pet to the owner rather than passing the heal over.
            foreach (var vital in HealOrder)
            {
                if (ctx.Owner.Vitals.Get(vital) >= HealThreshold)
                    continue;

                if (!TryFindHeal(spells, vital, mana, out var heal))
                    continue;

                return ctx.Owner.Distance <= heal.Range
                    ? CasterDecision.Cast(heal.SpellId, CasterTargetKind.Owner)
                    : CasterDecision.MoveInRange(heal.SpellId, CasterTargetKind.Owner);
            }

            // 2. the lowest sibling pet below the threshold. Out of range moves the pet to that sibling (ruling
            // 2026-11-02), exactly as for the owner.
            foreach (var vital in HealOrder)
            {
                if (!TryFindHeal(spells, vital, mana, out var heal))
                    continue;

                var lowest = -1;
                var lowestFraction = HealThreshold;

                for (var i = 0; i < ctx.Siblings.Count; i++)
                {
                    var fraction = ctx.Siblings[i].Vitals.Get(vital);

                    if (fraction < lowestFraction)
                    {
                        lowest = i;
                        lowestFraction = fraction;
                    }
                }

                if (lowest >= 0)
                {
                    return ctx.Siblings[lowest].Distance <= heal.Range
                        ? CasterDecision.Cast(heal.SpellId, CasterTargetKind.Sibling, lowest)
                        : CasterDecision.MoveInRange(heal.SpellId, CasterTargetKind.Sibling, lowest);
                }
            }

            // the owner's enchantment snapshot is taken once, and only when an affordable cleanse or buff is bound
            IReadOnlyDictionary<SpellCategory, CarriedEnchantment> ownerLayers = null;
            var ownerLayersRead = false;

            // 2b. cleanses: a Negative-aligned dispel, after the heals and before the buffs. The Other variant goes to
            // the owner while they carry a harmful enchantment it can remove; the Self variant goes to the pet itself
            // while IT carries one.
            foreach (var spell in spells)
            {
                if (spell.ManaCost > mana)
                    continue;

                if (spell.Kind == CasterSpellKind.CleanseSelf)
                {
                    if (CarriesDispellable(ctx.PetTopLayers?.Invoke(), false, spell.DispelSchool, spell.MinPower, spell.MaxPower))
                        return CasterDecision.Cast(spell.SpellId, CasterTargetKind.Self);

                    continue;
                }

                if (spell.Kind != CasterSpellKind.Cleanse)
                    continue;

                if (!ownerLayersRead)
                {
                    ownerLayers = ctx.OwnerTopLayers?.Invoke();
                    ownerLayersRead = true;
                }

                if (!CarriesDispellable(ownerLayers, false, spell.DispelSchool, spell.MinPower, spell.MaxPower))
                    continue;

                return ctx.Owner.Distance <= spell.Range
                    ? CasterDecision.Cast(spell.SpellId, CasterTargetKind.Owner)
                    : CasterDecision.MoveInRange(spell.SpellId, CasterTargetKind.Owner);
            }

            // 3. buffs, owner only.
            foreach (var spell in spells)
            {
                if (spell.Kind != CasterSpellKind.Buff || spell.ManaCost > mana)
                    continue;

                if (!ownerLayersRead)
                {
                    ownerLayers = ctx.OwnerTopLayers?.Invoke();
                    ownerLayersRead = true;
                }

                if (OwnerAlreadyHas(ownerLayers, spell))
                    continue;

                return ctx.Owner.Distance <= spell.Range
                    ? CasterDecision.Cast(spell.SpellId, CasterTargetKind.Owner)
                    : CasterDecision.MoveInRange(spell.SpellId, CasterTargetKind.Owner);
            }

            // 4. harmful spells on engaged enemies, in preference order
            if (!HasAffordableHarmful(spells, mana))
                return CasterDecision.Follow;

            for (var enemy = 0; enemy < ctx.EnemyDistances.Count; enemy++)
            {
                var profile = ctx.EnemyProfile?.Invoke(enemy) ?? VulnerabilityProfile.None;
                var distance = ctx.EnemyDistances[enemy];

                // line of sight, asked at most once per enemy and only for a projectile spell
                bool? hasLos = null;

                var bestInRange = -1;
                var bestInRangePriority = MonsterSpellPriority.Redundant;
                var firstOutOfRange = -1;

                for (var s = 0; s < spells.Count; s++)
                {
                    var spell = spells[s];

                    if ((spell.Kind != CasterSpellKind.Harmful && spell.Kind != CasterSpellKind.Strip) || spell.ManaCost > mana)
                        continue;

                    // a strip is wasted on an enemy with no beneficial enchantment it can remove; otherwise it weighs
                    // as a Neutral spell (a Preferred vulnerability or nuke still goes first, ties go to spellbook order)
                    var priority = spell.Kind == CasterSpellKind.Strip
                        ? (CarriesDispellable(profile.TopLayers, true, spell.DispelSchool, spell.MinPower, spell.MaxPower) ? MonsterSpellPriority.Neutral : MonsterSpellPriority.Redundant)
                        : MonsterSpellSelector.Classify(spell.Shape, profile);

                    // never waste a cast: this spell's effect is already on this enemy at equal or greater strength
                    if (priority == MonsterSpellPriority.Redundant)
                        continue;

                    // a projectile with no line of sight would only hit the wall (Monster_Combat.GetMaxRange
                    // rerolls for the same reason): unusable on this enemy
                    if (spell.IsProjectile)
                    {
                        hasLos ??= ctx.EnemyHasLos?.Invoke(enemy) ?? true;

                        if (!hasLos.Value)
                            continue;
                    }

                    if (distance <= spell.Range)
                    {
                        if (bestInRange < 0 || priority < bestInRangePriority)
                        {
                            bestInRange = s;
                            bestInRangePriority = priority;
                        }
                    }
                    else if (firstOutOfRange < 0)
                    {
                        firstOutOfRange = s;
                    }
                }

                if (bestInRange >= 0)
                    return CasterDecision.Cast(spells[bestInRange].SpellId, CasterTargetKind.Enemy, enemy);

                if (firstOutOfRange >= 0)
                    return CasterDecision.MoveInRange(spells[firstOutOfRange].SpellId, CasterTargetKind.Enemy, enemy);

                // every harmful spell is Redundant (or blind) on this enemy: try the next engaged one
            }

            return CasterDecision.Follow;
        }

        /// <summary>
        /// Whether a heal chosen earlier is still wanted when its pre-cast ends: the target's vital is still below
        /// <see cref="HealThreshold"/>. The decision is made a whole pre-cast before the spell lands, and in that
        /// time a kit, a potion, the owner's own spell or the other caster pet (Duo Summon: both see the same low
        /// owner on the same tick) may already have topped the vital up; a heal released then is wasted.
        /// </summary>
        public static bool HealStillNeeded(in CasterVitals vitals, CasterVital vital) => vitals.Get(vital) < HealThreshold;

        private static bool TryFindHeal(IReadOnlyList<CasterSpell> spells, CasterVital vital, uint mana, out CasterSpell heal)
        {
            foreach (var spell in spells)
            {
                if (spell.Kind == CasterSpellKind.Heal && spell.Vital == vital && spell.ManaCost <= mana)
                {
                    heal = spell;
                    return true;
                }
            }

            heal = default;
            return false;
        }

        private static bool HasAffordableHarmful(IReadOnlyList<CasterSpell> spells, uint mana)
        {
            foreach (var spell in spells)
            {
                if ((spell.Kind == CasterSpellKind.Harmful || spell.Kind == CasterSpellKind.Strip) && spell.ManaCost <= mana)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a cleanse chosen earlier is still wanted when its pre-cast ends: the target still carries a harmful
        /// layer the spell can remove (its school and power window). The decision is made a whole pre-cast before the
        /// spell lands, and in that time the other caster pet of a Duo pair (both see the same afflicted owner on the
        /// same tick), a potion or the owner may already have cleaned it; a cleanse released then is wasted.
        /// </summary>
        public static bool CleanseStillNeeded(IReadOnlyDictionary<SpellCategory, CarriedEnchantment> layers, MagicSchool dispelSchool, int minPower, int maxPower) =>
            CarriesDispellable(layers, false, dispelSchool, minPower, maxPower);

        /// <summary>
        /// TRUE when any top layer in <paramref name="layers"/> is one a dispel of <paramref name="dispelSchool"/> could
        /// remove, on the beneficial (<paramref name="beneficial"/> true: a strip) or harmful (false: a cleanse) side.
        /// A null snapshot (nothing carried, or not read) is FALSE.
        /// </summary>
        internal static bool CarriesDispellable(IReadOnlyDictionary<SpellCategory, CarriedEnchantment> layers, bool beneficial, MagicSchool dispelSchool,
            int minPower = int.MinValue, int maxPower = int.MaxValue)
        {
            if (layers == null)
                return false;

            foreach (var carried in layers.Values)
            {
                if (carried.IsDispellableBy(beneficial, dispelSchool, minPower, maxPower))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The buff's category already holds a top-layer entry at equal or greater power on the owner - the same
        /// comparison the engine stacks on (EnchantmentManager.Add groups by SpellCategory and the top layer is
        /// the highest PowerLevel), so a cast would be surpassed or merely refresh. An Undef category never
        /// matches.
        /// </summary>
        internal static bool OwnerAlreadyHas(IReadOnlyDictionary<SpellCategory, CarriedEnchantment> ownerLayers, in CasterSpell buff)
        {
            if (ownerLayers == null || buff.Category == SpellCategory.Undef)
                return false;

            return ownerLayers.TryGetValue(buff.Category, out var carried) && carried.PowerLevel >= buff.Power;
        }

        /// <summary>
        /// Sorts one spell into a kind from its spell-intrinsic fields. Pure so the table is testable without a
        /// dat. A self-targeted BENEFICIAL spell (Heal Self, Strength Self - what most player scrolls are) is a
        /// friendly spell like its Other twin and is classified the same way; the cast path aims it at the owner
        /// (Creature.CastSpellAt with friendly). A self-targeted HARMFUL spell is Unusable: it could only ever
        /// land on the pet itself.
        /// </summary>
        public static CasterSpellKind ClassifySpell(SpellType metaSpellType, bool beneficial, bool selfTargeted, int boost, DamageType vitalDamageType, DispelType align, out CasterVital vital)
        {
            vital = CasterVital.Health;

            // A dispel is sorted by its ALIGN, before the self-targeted-harmful refusal below: the retail Self dispels
            // (4337, 4346) are flagged SelfTargeted and NOT Beneficial (dat bitfield 0x88), the Other cleanses are Beneficial
            // (0x87) and the Other strips are not (0x83 / 0x3), so the Beneficial flag does not carry the meaning.
            // Align Negative removes harmful enchantments (SelectDispel: IsHarmful): a cleanse, which in its Self variant
            // lands on the pet. Align Positive or All removes beneficial ones: a strip, enemy-only. A Self strip would
            // strip the pet's own buffs, so it is never used.
            if (metaSpellType == SpellType.Dispel || metaSpellType == SpellType.FellowDispel)
            {
                if (align == DispelType.Negative)
                    return selfTargeted ? CasterSpellKind.CleanseSelf : CasterSpellKind.Cleanse;

                return selfTargeted ? CasterSpellKind.Unusable : CasterSpellKind.Strip;
            }

            if (selfTargeted && !beneficial)
                return CasterSpellKind.Unusable;

            switch (metaSpellType)
            {
                case SpellType.Boost:
                case SpellType.FellowBoost:

                    if (!beneficial || boost < 0)
                        return CasterSpellKind.Harmful;

                    if (!TryMapVital(vitalDamageType, out vital))
                        return CasterSpellKind.Unusable;

                    return CasterSpellKind.Heal;

                case SpellType.Enchantment:
                case SpellType.FellowEnchantment:

                    return beneficial ? CasterSpellKind.Buff : CasterSpellKind.Harmful;

                case SpellType.Projectile:
                case SpellType.LifeProjectile:
                case SpellType.EnchantmentProjectile:
                case SpellType.Transfer:

                    // a beneficial transfer spends the CASTER's own vital (refused at binding anyway)
                    return beneficial ? CasterSpellKind.Unusable : CasterSpellKind.Harmful;

                default:
                    return CasterSpellKind.Unusable;
            }
        }

        private static bool TryMapVital(DamageType damageType, out CasterVital vital)
        {
            switch (damageType)
            {
                case DamageType.Health:
                    vital = CasterVital.Health;
                    return true;
                case DamageType.Stamina:
                    vital = CasterVital.Stamina;
                    return true;
                case DamageType.Mana:
                    vital = CasterVital.Mana;
                    return true;
                default:
                    vital = CasterVital.Health;
                    return false;
            }
        }

        /// <summary>
        /// Metres a harmful spell with NO formula range at all (BaseRangeConstant 0 and BaseRangeMod 0) is cast
        /// from, so the pet closes in rather than firing from anywhere.
        ///
        /// A ring is NOT such a spell, and needs no special case: the engine's own ring reach is
        /// BaseRangeConstant (WorldObject_Tick ends a Ring projectile once it is more than
        /// Spell.BaseRangeConstant from its SpawnPos), and the formula already yields exactly that because rings
        /// carry BaseRangeMod 0. Read from C:\ACE\Dats\client_portal.dat on 2026-11-02 with a scratch DatLoader
        /// probe: Acid/Flame/Force/Lightning Ring (3805-3808) and the retail war rings 1783-1789 are 4.5 / 0,
        /// untargeted (NonComponentTargetType 0); the heavier rings are 8, 10 or 15. 4.5 - the standard ring
        /// reach - is used for the rare harmful spell with no range data (e.g. 3893 Summon Burning Haze, 0 / 0).
        /// </summary>
        public const float UnboundedHarmfulReach = 4.5f;

        /// <summary>
        /// The cast range for one spell at one magic skill: the monster formula (Creature.GetSpellMaxRange,
        /// BaseRangeConstant + skill * BaseRangeMod, capped at the outdoor radar range, 0 = unbounded). An
        /// unbounded HARMFUL spell gets <see cref="UnboundedHarmfulReach"/> so the pet closes in. An unbounded
        /// FRIENDLY spell (a Self spell aimed at the owner: Heal Self is 0 / 0 in the dat) gets the radar cap,
        /// which is what its Other twin resolves to at any caster pet's skill (Heal Other VI is 5 + 0.25 per
        /// point, so 75 m from 280 skill; caster pets have 400 or more).
        /// </summary>
        public static float ResolveRange(CasterSpellKind kind, float baseRangeConstant, float baseRangeMod, uint skill, float radarCap)
        {
            var range = Math.Min(baseRangeConstant + skill * baseRangeMod, radarCap);

            if (range != 0.0f)
                return range;

            return kind == CasterSpellKind.Harmful || kind == CasterSpellKind.Strip ? UnboundedHarmfulReach : radarCap;
        }
    }

    /// <summary>
    /// The spell-intrinsic half of a CasterSpell, memoized per spell id like MonsterSpellShapeCache (and for
    /// the same reason: new Spell(...) allocates a SpellFormula every time). The pet-specific range is applied
    /// on top by CombatPet.BuildCasterSpells.
    /// </summary>
    public readonly struct CasterSpellFacts
    {
        public CasterSpellKind Kind { get; }
        public CasterVital Vital { get; }
        public SpellCategory Category { get; }
        public uint Power { get; }
        public MonsterSpellShape Shape { get; }
        public MagicSchool School { get; }
        public float BaseRangeConstant { get; }
        public float BaseRangeMod { get; }
        public bool IsProjectile { get; }

        /// <summary>The school a dispel removes (None = every school). Meaningful for the dispel kinds only.</summary>
        public MagicSchool DispelSchool { get; }

        /// <summary>The dispel power window (SelectDispel's min_power / max_power).</summary>
        public int MinPower { get; }

        public int MaxPower { get; }

        public CasterSpellFacts(CasterSpellKind kind, CasterVital vital, SpellCategory category, uint power, MonsterSpellShape shape,
            MagicSchool school, float baseRangeConstant, float baseRangeMod, bool isProjectile, MagicSchool dispelSchool = MagicSchool.None,
            int minPower = int.MinValue, int maxPower = int.MaxValue)
        {
            DispelSchool = dispelSchool;
            MinPower = minPower;
            MaxPower = maxPower;
            IsProjectile = isProjectile;
            Kind = kind;
            Vital = vital;
            Category = category;
            Power = power;
            Shape = shape;
            School = school;
            BaseRangeConstant = baseRangeConstant;
            BaseRangeMod = baseRangeMod;
        }
    }

    public static class CasterSpellFactsCache
    {
        private static readonly ConcurrentDictionary<int, CasterSpellFacts> facts = new ConcurrentDictionary<int, CasterSpellFacts>();

        /// <summary>A spell missing from the dat or the spell table caches as Unusable (its accessors throw).</summary>
        public static CasterSpellFacts Get(int spellId)
        {
            if (facts.TryGetValue(spellId, out var cached))
                return cached;

            var spell = new Spell(spellId);

            if (spell.NotFound)
            {
                cached = default;
            }
            else
            {
                var kind = PetCasterBrain.ClassifySpell(spell.MetaSpellType, spell.IsBeneficial, spell.IsSelfTargeted, spell.Boost, spell.VitalDamageType, spell.Align, out var vital);

                cached = new CasterSpellFacts(kind, vital, spell.Category, spell.Power, MonsterSpellShapeCache.Get(spellId),
                    spell.School, spell.BaseRangeConstant, spell.BaseRangeMod, spell.IsProjectile, spell.DispelSchool, spell.MinPower, spell.MaxPower);
            }

            facts[spellId] = cached;
            return cached;
        }

        public static void Clear() => facts.Clear();
    }

    /// <summary>
    /// Resolves what a dispel judgement needs about one registry entry (VulnerabilityProfile.BuildTopLayers's resolver):
    /// the entry spell's alignment and school, memoized per spell id (new Spell(...) allocates a SpellFormula every
    /// time), and whether a creature's dispel could ever remove it - the same two filters EnchantmentManager.SelectDispel
    /// applies before alignment and school (Duration -1 item and vitae entries; the CreatureDispelExempt buffs a non-player
    /// caster cannot strip).
    /// </summary>
    public static class EnchantmentDispelFactsCache
    {
        private static readonly ConcurrentDictionary<int, (bool Beneficial, MagicSchool School)?> spells = new ConcurrentDictionary<int, (bool Beneficial, MagicSchool School)?>();

        /// <summary>
        /// The spell lookup Resolve uses: the dat, memoized. A seam so a test can run the real snapshot path without
        /// the client dat (save and restore it).
        /// </summary>
        internal static Func<int, (bool Beneficial, MagicSchool School)?> SpellInfoLookup = LookupFromDat;

        public static EnchantmentDispelFacts? Resolve(PropertiesEnchantmentRegistry entry) => ResolveWith(entry, SpellInfoLookup);

        /// <summary>The pure half of Resolve: the spell facts come from <paramref name="spellInfo"/>.</summary>
        internal static EnchantmentDispelFacts? ResolveWith(PropertiesEnchantmentRegistry entry, Func<int, (bool Beneficial, MagicSchool School)?> spellInfo)
        {
            var info = spellInfo(entry.SpellId);

            if (!info.HasValue)
                return null;

            var dispellable = entry.Duration != -1 && !EnchantmentManager.CreatureDispelExempt.Contains((uint)entry.SpellId);

            return new EnchantmentDispelFacts(info.Value.Beneficial, info.Value.School, dispellable);
        }

        private static (bool Beneficial, MagicSchool School)? LookupFromDat(int spellId)
        {
            if (!spells.TryGetValue(spellId, out var info))
            {
                var spell = new Spell(spellId);

                info = spell.NotFound ? null : (spell.IsBeneficial, spell.School);
                spells[spellId] = info;
            }

            return info;
        }

        public static void Clear() => spells.Clear();
    }
}
