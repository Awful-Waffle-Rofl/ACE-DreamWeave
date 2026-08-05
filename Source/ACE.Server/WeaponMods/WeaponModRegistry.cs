using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The modifier table: one row per special, and the only place a modifier's maximum is written down.
    /// Per-modifier maximums are CONSTANTS here, never tunables - only the global weapon_mod_magnitude_scale is
    /// operator-facing.
    ///
    /// CRIT ROUTES THROUGH RATINGS, NEVER THE RETAIL PROPERTIES (HARD). Devastation and Weak Point use
    /// GearCritDamage 374 and GearCrit 372, and must NEVER be moved onto PropertyFloat.CriticalMultiplier 136 /
    /// PropertyFloat.CriticalFrequency 147. Both retail properties are consumed with Math.Max against the
    /// skill-scaled imbue bonus rather than summed (WorldObject_Weapon.cs:351-356 and :416-421), and this system
    /// preserves imbues by design, so a rolled CriticalFrequency on a CriticalStrike-imbued weapon would be
    /// silently swallowed by the larger imbue value. The two ratings are additive through Creature_Rating and
    /// stack cleanly.
    ///
    /// THOSE TWO NAMES ARE ALSO WHY THEY ARE NOT CALLED "Crushing Blow" AND "Biting Strike" (2026-07-30).
    /// Crushing Blow and Biting Strike are the RETAIL names for CriticalMultiplier and CriticalFrequency
    /// (see the doc comments on WorldObject_Weapon.CriticalFrequency), so using them here invited players to
    /// read these modifiers as the retail mechanics they deliberately avoid. Renamed to Devastation and
    /// Weak Point; the reserved PropertyFloat ids 8130 and 8131 did NOT move.
    ///
    /// THE POOL IS DAMAGE-ORIENTED ONLY (2026-07-30, repo-owner directive). Warding, Crit Ward, Resolute,
    /// Vigor and Mending were defensive or sustain rows and were removed outright, taking the table from 11
    /// entries to 6. Their PropertyFloat ids 8136-8140 are RETIRED and must never be reused - weapons on dev
    /// shards still carry records at them. See the enum comment on WeaponModId for the consequence:
    /// ClearSpecials walks THIS table, so a record at a removed id is no longer reversible.
    ///
    /// ARMOR CLEAVING IS CUT. Do not add it back. PropertyFloat.IgnoreArmor 155 is used by GetArmorCleavingMod
    /// (WorldObject_Weapon.cs:837-847) only as a null/non-null FLAG - the magnitude comes from the weapon's max
    /// spell level - so there is no magnitude axis to roll, all 248 retail weenies carrying it store values at
    /// or above 1.0, and it loses outright to the ArmorRending imbue at endgame skill
    /// (Math.Min at DamageEvent.cs:321).
    ///
    /// THREE SPECIALS IS A PERMANENT PER-WEAPON BOUND, and nothing in this system is ever player-targetable.
    /// The design's +101.1% endgame ceiling is priced on a perfect trio being a lottery rather than a grind. Any
    /// change that lets a player choose, replace or reroll a single special invalidates that assessment and
    /// needs it re-derived first.
    ///
    /// TIER B (v2) IS THE SECOND HALF OF THIS TABLE, and it is shaped differently on purpose. Every Tier B row
    /// writes NO native property: its record holds a plain FRACTION and a hand-wired hook in
    /// Player_WeaponMods.cs reads it live at combat time. See <see cref="WeaponModDefinition"/> for the storage
    /// contract and why the Tier A reversal machinery does not apply to it.
    ///
    /// THE WHOLE SYSTEM RIDES ONE GATE: weapon_mods_enabled (bool, default FALSE), and that includes Tier B.
    /// Tier B had a second tunable of its own (weapon_mod_tier_b_enabled) until 2026-07-30, when it was removed
    /// by repo-owner directive and everything it gated moved onto weapon_mods_enabled. Do NOT reintroduce a
    /// second switch: one gate is what makes "the system is off" a single, checkable fact rather than four
    /// combinations, only one of which was ever tested.
    ///
    /// While the gate is off, <see cref="Pool"/> excludes every Tier B row so none can be rolled, and each
    /// combat hook reads zero. <see cref="AllMods"/> is deliberately NOT gated - reversal, ClearSpecials and
    /// the appraisal display must keep seeing a record that was rolled while the gate was on.
    /// </summary>
    public static class WeaponModRegistry
    {
        /// <summary>
        /// The one gate for the entire weapon mod system - crafting, the roll pools and every Tier B combat
        /// effect. See the class remarks for why there is exactly one and not two.
        /// </summary>
        public static bool Enabled() => PropertyManager.GetBool("weapon_mods_enabled").Item;

        /// <summary>Inclusive lower bound of the PropertyFloat range holding Tier A applied magnitudes.</summary>
        public const int PropertyBandStart = 8130;

        /// <summary>
        /// Inclusive upper bound of the LIVE PropertyFloat range holding Tier A applied magnitudes. It came
        /// down from 8140 to 8135 on 2026-07-30 with the five-row pool cut.
        /// </summary>
        public const int PropertyBandEnd = 8135;

        /// <summary>
        /// Inclusive lower bound of the RETIRED PropertyFloat range - 8136-8140, which held Warding, Crit Ward,
        /// Resolute, Vigor and Mending until the 2026-07-30 pool cut. Nothing in this system writes here any
        /// more, and no future property may take these ids: weapons on dev shards still carry records at them.
        /// Kept as constants so the "no dead rows" harness can scan the retired range too and prove nothing
        /// writes it, rather than merely stopping at 8135 and never looking.
        /// </summary>
        public const int RetiredPropertyBandStart = 8136;

        /// <summary>Inclusive upper bound of the retired PropertyFloat range. See <see cref="RetiredPropertyBandStart"/>.</summary>
        public const int RetiredPropertyBandEnd = 8140;

        /// <summary>
        /// Inclusive lower bound of the LIVE PropertyFloat range holding Tier B applied magnitudes. It starts
        /// immediately after the retired band, so the three bands are contiguous 8130-8147 with no id belonging
        /// to none of them.
        /// </summary>
        public const int TierBPropertyBandStart = 8141;

        /// <summary>Inclusive upper bound of the live Tier B PropertyFloat range.</summary>
        public const int TierBPropertyBandEnd = 8147;

        /// <summary>
        /// Inclusive lower bound of the PropertyFloat range RESERVED for the two phase-2 Tier B entries -
        /// Sunder (8148) and Rampage (8149). Neither has an enum member or a registry row here, because neither
        /// effect exists yet: Sunder needs a new enchantment/spell row and Rampage needs per-target stack
        /// state. Kept as constants so the "no dead rows" harness can scan the reserved range too and prove
        /// nothing writes it, rather than stopping at 8147 and never looking.
        /// </summary>
        public const int TierBReservedBandStart = 8148;

        /// <summary>Inclusive upper bound of the reserved phase-2 Tier B range. See <see cref="TierBReservedBandStart"/>.</summary>
        public const int TierBReservedBandEnd = 8149;

        /// <summary>Tolerance for "this reversal landed back on the engine default", per the design.</summary>
        public const double Epsilon = 1e-9;

        /// <summary>A weapon carries exactly ten slots: imbues + specials + tinkers, always.</summary>
        public const int TotalSlots = 10;

        /// <summary>
        /// The permanent per-weapon special bound. A LITERAL CLAMP in code, not left implied by the odds table -
        /// a future tuning pass that raises weapon_mod_special_chance_3 must not be able to produce a fourth.
        /// </summary>
        public const int MaxSpecials = 3;

        private static readonly WeaponModDefinition[] mods =
        {
            new WeaponModDefinition
            {
                Id = WeaponModId.Devastation,
                // RENAMED from "Crushing Blow" on 2026-07-30. That is the RETAIL name for
                // PropertyFloat.CriticalMultiplier (WorldObject_Weapon.CriticalMultiplier carries it as a doc
                // comment), so it read as the retail mechanic rather than as this rating. The record id 8130
                // did not move.
                DisplayName = "Devastation",
                Record = PropertyFloat.WeaponModDevastation,
                NativeInt = PropertyInt.GearCritDamage,
                // RETUNED FROM 50 TO 5 on 2026-07-30, third live-play pass, by repo-owner directive, on the
                // same rating-scarcity evidence that moved the defensive rows before the pool was cut:
                // VERIFIED against ace_world across every weenie in the game, GearCritDamage 374 appears on
                // ZERO items, so a +50 grant has no peer anywhere in the item set.
                //
                // RETUNED FROM 5 TO 6 on 2026-07-30, fourth live-play pass, by repo-owner directive, on the
                // same rating-scarcity evidence as the round-three retune above.
                //
                // At MaxRoll 6 the applied magnitude is 1 at workmanship 1; 1, 2 or 3 at workmanship 5; and
                // 2 through 6 at workmanship 10 - the minimum raw value at workmanship 10 is exactly
                // 6 * 0.25 = 1.5, which rounds AWAY FROM ZERO to 2, so 1 is not reachable at workmanship 10.
                MaxRoll = 6,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0} critical damage rating",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.WeakPoint,
                // RENAMED from "Biting Strike" on 2026-07-30, for the same reason as Devastation above: it is
                // the RETAIL name for PropertyFloat.CriticalFrequency (see the doc comment at
                // WorldObject_Weapon.cs:333-335). The record id 8131 did not move.
                DisplayName = "Weak Point",
                Record = PropertyFloat.WeaponModWeakPoint,
                NativeInt = PropertyInt.GearCrit,
                // RETUNED FROM 17 TO 2 on 2026-07-30, third live-play pass, by repo-owner directive. VERIFIED
                // against ace_world: GearCrit 372 appears on ZERO weenies.
                //
                // RETUNED FROM 2 TO 3 on 2026-07-30, fourth live-play pass, by repo-owner directive, on the
                // same rating-scarcity evidence as the round-three retune above.
                //
                // At MaxRoll 3 with MinPotency 0.25 and the floor-at-1 rule in WeaponModValue.Resolve the
                // distribution is: workmanship 1 always yields 1; workmanship 5 yields 1 or 2 (a 2 only at an
                // exact potency of 1.0, where the raw value is exactly 1.5); and workmanship 10 yields 1, 2 or 3.
                MaxRoll = 3,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0} critical chance rating",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Bloodthirst,
                DisplayName = "Bloodthirst",
                Record = PropertyFloat.WeaponModBloodthirst,
                NativeInt = PropertyInt.GearDamage,
                // RETUNED FROM 25 TO 5 on 2026-07-30, third live-play pass, by repo-owner directive. VERIFIED
                // against ace_world: GearDamage 370 appears on exactly ONE weenie, at value 1.
                //
                // RETUNED AGAIN FROM 5 TO 6 on 2026-07-30, fourth live-play pass, by repo-owner directive, on
                // the same evidence. Same resulting distribution as Devastation.
                MaxRoll = 6,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0} damage rating",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Cleave,
                DisplayName = "Cleave",
                Record = PropertyFloat.WeaponModCleave,
                NativeInt = PropertyInt.Cleaving,
                MaxRoll = 1,
                // Cleaving stores TOTAL targets INCLUDING the primary: IsCleaving is a null test and
                // CleaveTargets is "value - 1" (WorldObject_Weapon.cs:47-62). So an untinkered weapon behaves as
                // 1, not 0, and +1 must land on 2 to be worth one extra target. Reversal back to 1 removes the
                // row, which restores IsCleaving == false.
                //
                // Cleave MUST keep RemoveOnDefault (the default, true). IsCleaving is a null test, so writing
                // the restored 1 explicitly would leave the weapon flagged as cleaving forever.
                NativeDefault = 1,
                NativeFloor = 1,
                Binary = true,
                MinPotency = 0,
                Classes = WeaponClass.Melee,
                DisplayFormat = "+{0:0} cleave target",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.ShieldBypass,
                DisplayName = "Shield Bypass",
                Record = PropertyFloat.WeaponModShieldBypass,
                NativeFloat = PropertyFloat.IgnoreShield,
                MaxRoll = 0.50,
                // GetIgnoreShieldMod reads "weapon?.IgnoreShield ?? 0.0f" (WorldObject_Weapon.cs:855-861), and
                // unlike IgnoreArmor it really does read the VALUE - the retail range 0.4-1.0 across 174
                // weenies is a true fraction.
                NativeDefault = 0,
                Classes = WeaponClass.Melee | WeaponClass.Missile,
                DisplayFormat = "{0:0.##}% of a shield ignored",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.SwiftFlight,
                DisplayName = "Swift Flight",
                Record = PropertyFloat.WeaponModSwiftFlight,
                NativeFloat = PropertyFloat.MaximumVelocity,
                MaxRoll = 6.0,
                // The read sites for MaximumVelocity disagree on their fallback (WeaponProfile.cs:57 uses 1.0,
                // Creature_Missile.cs:322 DefaultProjectileSpeed, :517 DefaultMaxVelocity = 20.0). The design
                // prices this modifier against missile RANGE specifically - Creature_Missile.cs:517-519 - so
                // that site's 20.0 is the default used here. In practice every missile launcher carries the
                // property, so the absent case is defensive only.
                NativeDefault = 20.0,
                // The one row that must NOT remove its native row on a reversal that lands on the default.
                // Because the read sites disagree (1.0 at WeaponProfile.cs:57, 20.0 at Creature_Missile.cs),
                // "absent" is not equivalent to 20.0, and five weenies (518, 521, 531, 537, 23109) sit at
                // exactly 20.0 - removing their row would make the appraisal panel read velocity 1.0.
                RemoveOnDefault = false,
                Classes = WeaponClass.Missile,
                DisplayFormat = "+{0:0.##} missile velocity",
            },

            /* ------------------------------------------------------------------------------------------
             * TIER B (v2). Seven rows, every one of them riding a hook that already existed.
             *
             * READ THIS BEFORE ADDING A ROW HERE. A Tier B row deliberately sets NO NativeInt and NO
             * NativeFloat. That is what makes WeaponModDefinition.IsInteger false, which is what keeps the
             * rolled magnitude FRACTIONAL rather than being rounded and then floored to 1 by the integer
             * branch in WeaponModValue.Resolve - a 4% leech would otherwise become +1 of nothing. It is also
             * what makes ApplySpecial write only the record, and what makes reversal a bare RemoveProperty.
             *
             * Consequence: a row added here is INERT until a hook in Player_WeaponMods.cs reads it. There is
             * no dispatcher - EquipmentModHookKind next door is documentation, not machinery - so every entry
             * below names its hook site in its own comment, and an entry with no hook is a slot spent on
             * nothing.
             *
             * MAGNITUDES come from Docs/WeaponMods/DESIGN.md "Tier B - v2, new combat hooks" and its "25%
             * target" subsection. The three conditional-damage entries share an axis and ADD within it, which
             * is what holds the ceiling at 25% rather than the 27.1% a cross-axis product would give.
             * ------------------------------------------------------------------------------------------ */

            new WeaponModDefinition
            {
                Id = WeaponModId.LifeLeech,
                Tier = WeaponModTier.B,
                DisplayName = "Life Leech",
                Record = PropertyFloat.WeaponModLifeLeech,
                // HOOKS (both, because a caster reaches neither through the other): the physical half in
                // Player.ApplyWeaponModOutgoingDamage, off Player_Combat.DamageTarget, which covers melee,
                // missile and multishot; the spell half in Player.ApplyWeaponModSpellHit, off
                // SpellProjectile.OnCollideObject. Spells never route through DamageTarget.
                MaxRoll = 0.04,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "{0:0.##}% of damage dealt returned as health",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.ManaLeech,
                Tier = WeaponModTier.B,
                DisplayName = "Mana Leech",
                Record = PropertyFloat.WeaponModManaLeech,
                // same two hooks as Life Leech, into Mana instead of Health
                MaxRoll = 0.04,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "{0:0.##}% of damage dealt returned as mana",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.StaminaLeech,
                Tier = WeaponModTier.B,
                DisplayName = "Stamina Leech",
                Record = PropertyFloat.WeaponModStaminaLeech,
                // same two hooks as Life Leech, into Stamina instead of Health
                MaxRoll = 0.04,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "{0:0.##}% of damage dealt returned as stamina",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Ambush,
                Tier = WeaponModTier.B,
                DisplayName = "Ambush",
                Record = PropertyFloat.WeaponModAmbush,
                // HOOKS: the physical half in Player.ApplyWeaponModOutgoingDamage (applied BEFORE the leeches
                // in that method, so they leech off the boosted number); the spell half in
                // Player.GetWeaponModSpellDamageMod, folded into SpellProjectile.CalculateDamage beside the
                // class-ability spell damage multiplier.
                //
                // CAME DOWN from 25% to 15% in the design, and the reason is the opposite of intuitive: in pack
                // farming where trash dies in one or two hits almost every hit is an opener, so its effective
                // uptime approaches 100% exactly in the AOE scenario that is already the strongest. It sits
                // level with Rampage and Sunder at 15, not above them.
                MaxRoll = 0.15,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0.##}% damage against a full-health target",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Quickening,
                Tier = WeaponModTier.B,
                DisplayName = "Quickening",
                Record = PropertyFloat.WeaponModQuickening,
                // HOOK: Player.GetWeaponModAttackSpeedMod, composed INSIDE Player.ApplyClassAbilityAttackSpeed's
                // multiplicative product and therefore inside its single ceiling clamp against
                // class_ability_attack_speed_ceiling. Applying it after the clamp would let it push past the
                // ceiling the whole attack-speed axis is governed by.
                //
                // NO CASTER ROW BY DESIGN, and the accessor agrees: the hook reads GetEquippedWeapon(), which
                // returns melee-or-missile and never the wand.
                //
                // THE ONE BUILD-DEPENDENT ENTRY IN THE POOL. The axis saturates - the 2.0 base is MaxAttackSpeed
                // (Creature_Combat.cs:504), a private static no config can raise - so this is worth the full 6%
                // on a build with no other speed source and about 4.2% on the deepest ability-only cross, which
                // already sits near 4.32 anim speed against a 4.5 ceiling.
                MaxRoll = 0.06,
                NativeDefault = 0,
                Classes = WeaponClass.Melee | WeaponClass.Missile,
                DisplayFormat = "+{0:0.##}% attack speed",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Overload,
                Tier = WeaponModTier.B,
                DisplayName = "Overload",
                Record = PropertyFloat.WeaponModOverload,
                // HOOK: Player.RollWeaponModOverload, read in Player.CalculateManaUsage (Player_Magic.cs, NOT
                // the same-named Creature_Magic overload) right after the class-ability mana surcharge.
                //
                // THE MAGNITUDE IS A PROBABILITY, not a percent bonus: 0.20 means a 20% chance the cast costs
                // nothing at all.
                MaxRoll = 0.20,
                NativeDefault = 0,
                Classes = WeaponClass.Caster,
                DisplayFormat = "{0:0.##}% chance a spell costs no mana",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.SecondWind,
                Tier = WeaponModTier.B,
                DisplayName = "Second Wind",
                Record = PropertyFloat.WeaponModSecondWind,
                // HOOK: Player.ApplyWeaponModCreatureDeath, off Creature.OnDeath beside
                // ApplyCreatureDeathClassAbilities. NetherBloomAbility is the precedent for that site.
                //
                // The magnitude is a fraction of MAXIMUM health, stamina and mana, restored on a killing blow.
                MaxRoll = 0.12,
                NativeDefault = 0,
                Classes = WeaponClass.All,
                DisplayFormat = "restores {0:0.##}% of your maximum vitals on a killing blow",
                DisplayScale = 100.0,
            },
        };

        /* ---------------------------------------------------------------------------------------------
         * REMOVED 2026-07-30, repo-owner directive: the pool is damage-oriented only.
         *
         * Five defensive / sustain rows were deleted outright rather than retuned - Warding
         * (GearDamageResist 371, PropertyFloat 8136), Crit Ward (GearCritResist 373, 8137), Resolute
         * (GearCritDamageResist 375, 8138), Vigor (GearMaxHealth 379, 8139) and Mending
         * (GearHealingBoost 376, 8140).
         *
         * DO NOT ADD THEM BACK AND DO NOT REUSE 8136-8140. The ids are registered RETIRED_DO_NOT_REUSE in
         * Source/property-registry.tsv because weapons on dev shards are carrying records at them right now.
         *
         * KNOWN CONSEQUENCE, accepted. ClearSpecials and the whole reversal path iterate AllMods, so a weapon
         * that already carries one of those five records keeps its native delta forever with no way to reverse
         * it. That is acceptable ONLY because this feature has never been enabled outside a disposable dev
         * shard, where "@weaponmodkit clean" plus fresh weapons resolves it. Shipping a modifier removal after
         * the feature went live would need a data migration: sweep ace_shard for every biota carrying a record
         * at the removed id and subtract the recorded magnitude back off its native property, exactly as
         * WeaponModTinkerSet.ReverseSpecial does in memory.
         * --------------------------------------------------------------------------------------------- */

        /// <summary>
        /// EVERY modifier, both tiers, in table order. Deliberately NOT gated on weapon_mods_enabled:
        /// reversal, <see cref="WeaponModTinkerSet.ClearSpecials"/>, <see cref="WeaponModTinkerSet.ReadSpecials"/>
        /// and the appraisal display all walk this list, and a record rolled while the gate was on must stay
        /// visible and reversible after it is turned off. <see cref="Pool"/> is where the gate lives.
        /// </summary>
        public static readonly IReadOnlyList<WeaponModDefinition> AllMods = mods;

        /// <summary>The Tier A half - the six rows that write a native property. In table order.</summary>
        public static readonly IReadOnlyList<WeaponModDefinition> TierAMods = mods.Where(m => m.Tier == WeaponModTier.A).ToArray();

        /// <summary>The Tier B half - the seven rows read live at combat time. In table order.</summary>
        public static readonly IReadOnlyList<WeaponModDefinition> TierBMods = mods.Where(m => m.Tier == WeaponModTier.B).ToArray();

        private static readonly Dictionary<WeaponModId, WeaponModDefinition> byId = mods.ToDictionary(m => m.Id);
        private static readonly Dictionary<PropertyFloat, WeaponModDefinition> byRecord = mods.ToDictionary(m => m.Record);

        private static readonly Dictionary<WeaponClass, WeaponModDefinition[]> tierAPools = new Dictionary<WeaponClass, WeaponModDefinition[]>
        {
            { WeaponClass.Melee,   mods.Where(m => m.Tier == WeaponModTier.A && m.AppliesTo(WeaponClass.Melee)).ToArray() },
            { WeaponClass.Missile, mods.Where(m => m.Tier == WeaponModTier.A && m.AppliesTo(WeaponClass.Missile)).ToArray() },
            { WeaponClass.Caster,  mods.Where(m => m.Tier == WeaponModTier.A && m.AppliesTo(WeaponClass.Caster)).ToArray() },
        };

        private static readonly Dictionary<WeaponClass, WeaponModDefinition[]> bothTierPools = new Dictionary<WeaponClass, WeaponModDefinition[]>
        {
            { WeaponClass.Melee,   mods.Where(m => m.AppliesTo(WeaponClass.Melee)).ToArray() },
            { WeaponClass.Missile, mods.Where(m => m.AppliesTo(WeaponClass.Missile)).ToArray() },
            { WeaponClass.Caster,  mods.Where(m => m.AppliesTo(WeaponClass.Caster)).ToArray() },
        };

        private static readonly WeaponModDefinition[] emptyPool = new WeaponModDefinition[0];

        /// <summary>
        /// The specials a given weapon class may roll RIGHT NOW, against the live weapon_mods_enabled gate.
        /// Pool depth is the arithmetic the design's pricing rests on, so both states are asserted in the tests
        /// rather than left to inspection:
        ///
        ///   gate OFF (the default) - melee 5, missile 5, caster 3
        ///   gate ON                - melee 11, missile 11, caster 9
        ///
        /// The gate-OFF depths describe an inert system: with weapon_mods_enabled false nothing can roll at all,
        /// because the crafting entry points refuse first. They are still pinned because this exclusion is the
        /// LAST line between a shard with the system off and live Tier B combat code, and a pool that quietly
        /// started returning Tier B rows would be invisible until it was not.
        ///
        /// THE TIER A CASTER POOL EQUALS <see cref="MaxSpecials"/> EXACTLY, and that is a known consequence
        /// rather than a bug: on the Tier A half alone, a caster that rolls three specials necessarily holds
        /// all three, so casters would have no identity lottery left, only a magnitude lottery. Do NOT add a
        /// guard for it and do NOT move the cap. With the gate on - the only state in which a caster can roll
        /// anything - the pool is 9 deep and the identity lottery is back.
        /// </summary>
        public static IReadOnlyList<WeaponModDefinition> Pool(WeaponClass weaponClass) => Pool(weaponClass, Enabled());

        /// <summary>
        /// The pool for a given weapon class at an EXPLICIT gate state. Pure - the gate arrives as an
        /// argument - so both states can be swept without touching PropertyManager.
        /// </summary>
        public static IReadOnlyList<WeaponModDefinition> Pool(WeaponClass weaponClass, bool includeTierB)
        {
            var source = includeTierB ? bothTierPools : tierAPools;

            return source.TryGetValue(weaponClass, out var pool) ? pool : emptyPool;
        }

        public static WeaponModDefinition Get(WeaponModId id) => byId[id];

        public static bool TryGet(WeaponModId id, out WeaponModDefinition definition) => byId.TryGetValue(id, out definition);

        public static bool TryGet(PropertyFloat record, out WeaponModDefinition definition) => byRecord.TryGetValue(record, out definition);
    }
}
