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
    /// THE TWO MAGNITUDE LEVERS, and which one to reach for. weapon_mod_magnitude_scale moves EVERY row at
    /// once and is a live shard config double, so it needs no deploy. A single row is retuned by editing its
    /// <see cref="WeaponModDefinition.MaxRoll"/> below, which is a code change and therefore a deploy - a
    /// deliberate trade, since a per-row tunable for eighteen rows would be eighteen more operator-facing
    /// knobs to keep consistent with the design doc.
    ///
    /// EITHER LEVER IS RETROACTIVE FOR TIER B AND NEITHER IS FOR TIER A (2026-08-07). A Tier B row stores the
    /// roll fraction and re-derives its magnitude from this table on every read, so halving a MaxRoll here
    /// halves that modifier on every weapon already carrying it. A Tier A row stored its magnitude at roll
    /// time and keeps it until rerolled. See WeaponModDefinition's storage contract for why the asymmetry is
    /// required rather than incidental.
    ///
    /// A retune is deliberately not a one-file change: the magnitudes are pinned in a test table too
    /// (WeaponModTierBTests for the v2 rows, WeaponModCatalogV3Tests for the v3 rows), so a value cannot move
    /// as a refactor artifact.
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
    /// THE POOL WAS DAMAGE-ORIENTED ONLY FROM 2026-07-30 UNTIL 2026-08-17, WHEN THAT DIRECTIVE WAS SUPERSEDED.
    /// The original 2026-07-30 repo-owner directive removed Warding, Crit Ward, Resolute, Vigor and Mending as
    /// defensive/sustain rows, taking the table from 11 entries to 6. Their PropertyFloat ids 8136-8140 are
    /// RETIRED and must never be reused - weapons on dev shards still carry records at them. See the enum
    /// comment on WeaponModId for the consequence: ClearSpecials walks THIS table, so a record at a removed id
    /// is no longer reversible. ON 2026-08-17, by a NEW repo-owner directive, that restriction was lifted:
    /// utility rows are back in the pool, in a new form - see "CATALOG V4" below.
    ///
    /// CLEAVE WAS REMOVED ON THE SAME TERMS (2026-08-07, repo-owner directive), taking Tier A from 6 rows to
    /// 5 and the melee pool from 15 to 14. Its WeaponModId 4 and its PropertyFloat 8133 are both RETIRED.
    /// The consequence above applies to it in full and is the reason 8133 can never be reused: a dev-shard
    /// weapon that rolled Cleave keeps a stale 8133 record AND the PropertyInt.Cleaving native that record
    /// was the bookkeeping for, and ClearSpecials can no longer reverse either one.
    ///
    /// ARMOR CLEAVING IS CUT. Do not add it back. PropertyFloat.IgnoreArmor 155 is used by GetArmorCleavingMod
    /// (WorldObject_Weapon.cs:837-847) only as a null/non-null FLAG - the magnitude comes from the weapon's max
    /// spell level - so there is no magnitude axis to roll, all 248 retail weenies carrying it store values at
    /// or above 1.0, and it loses outright to the ArmorRending imbue at endgame skill
    /// (Math.Min at DamageEvent.cs:321).
    ///
    /// <see cref="MaxSpecials"/> IS A PERMANENT PER-WEAPON BOUND, and nothing in this system is ever
    /// player-targetable. The design's endgame ceiling is priced on a perfect set being a lottery rather than a
    /// grind. Any change that lets a player choose, replace or reroll a single special invalidates that
    /// assessment and needs it re-derived first. The bound itself moved from 3 to 4 on 2026-08-06; the
    /// re-derivation of the ceiling that goes with it belongs to the magnitude pass, not to this structural
    /// change, and until that pass lands the fourth special is unreachable because
    /// weapon_mod_special_chance_4 defaults to 0.
    ///
    /// TIER B (v2) IS THE SECOND HALF OF THIS TABLE, and it is shaped differently on purpose. Every Tier B row
    /// writes NO native property: its record holds a plain FRACTION and a hand-wired hook in
    /// Player_WeaponMods.cs reads it live at combat time. See <see cref="WeaponModDefinition"/> for the storage
    /// contract and why the Tier A reversal machinery does not apply to it.
    ///
    /// THE WHOLE SYSTEM RIDES ONE GATE: weapon_mods_enabled (bool; see PropertyManager for the current
    /// default, which flipped to TRUE for the 2026-08 open beta), and that includes Tier B.
    /// Tier B had a second tunable of its own (weapon_mod_tier_b_enabled) until 2026-07-30, when it was removed
    /// by repo-owner directive and everything it gated moved onto weapon_mods_enabled. Do NOT reintroduce a
    /// second switch: one gate is what makes "the system is off" a single, checkable fact rather than four
    /// combinations, only one of which was ever tested.
    ///
    /// PER-WIELDER SUPPRESSION IS NOT A SECOND SWITCH. Since 2026-09-25 (owner ruling: no weapon mods on the PK
    /// facet) a player whose weapon mods are suppressed (Player.WeaponModSuppressed) reads 0 from every Tier B
    /// hook and has every Tier A amount taken back off at the native's read sites. It is a condition on the
    /// wielder, evaluated per read, that changes nothing on the item and nothing about the system - with the
    /// gate on, everyone else is unaffected. See WeaponModSuppression.
    ///
    /// While the gate is off, <see cref="Pool"/> excludes every Tier B row so none can be rolled, and each
    /// combat hook reads zero. <see cref="AllMods"/> is deliberately NOT gated - reversal, ClearSpecials and
    /// the appraisal display must keep seeing a record that was rolled while the gate was on.
    ///
    /// CATALOG V4 (2026-08-17, repo-owner directive): FIVE ROWS RETIRED, NINE ROWS ADDED. Swift Flight
    /// (WeaponModId 6, PropertyFloat 8135, Tier A) and Life Leech/Mana Leech/Stamina Leech/Overload
    /// (WeaponModId 12-14 and 17, PropertyFloat 8141-8143 and 8146, all Tier B) were removed outright, on the
    /// same terms as every prior retirement in this file: the ids are RETIRED_DO_NOT_REUSE in
    /// Source/property-registry.tsv, weapons on dev shards may still carry stale records at them, and
    /// ClearSpecials can no longer reverse those records. In their place, nine new Tier B utility rows -
    /// Efficiency, Recovery, Mana Well, Cleanse, Longevity, Siphon, Quick Refresh, Arcane Defender and Panic
    /// Reload, PropertyFloat 8027-8035 (<see cref="TierBV4BandStart"/>-<see cref="TierBV4BandEnd"/>) - were
    /// registered.
    ///
    /// ALL NINE ARE NOW HOOKED (2026-08-17, three follow-up commits d66bfa995/0fa5fdb8e/ba0218211 on top of the
    /// registration commit d904ba982). Each row's comment below names its ACTUAL hook site, not a future one.
    /// Summary: Efficiency discounts attack stamina (Player_Combat.GetAttackStamina) and spell mana
    /// (Player_Magic.CalculateManaUsage) via WeaponModCombat.CostMultiplier; Recovery multiplies into
    /// Creature.VitalHeartBeat's regeneration tick; Mana Well and Cleanse both hang off
    /// Player.ApplyWeaponModCreatureDeath, alongside Second Wind, via their own
    /// ApplyWeaponModManaWell/ApplyWeaponModCleanse methods; Longevity extends duration at both
    /// EnchantmentManager duration sites (Add's refresh path and BuildEntry); Siphon rolls off both
    /// Player.ApplyWeaponModOutgoingDamage and Player.ApplyWeaponModSpellHit through the shared
    /// TryWeaponModSiphon helper; Quick Refresh composes Player.GetWeaponModCastSpeedMod
    /// (Player_WeaponMods_Casting.cs) into Player_Magic's castSpeedMultiplier at both cast entry points; Arcane
    /// Defender adds a no-shield branch to Creature.GetShieldMod; and Panic Reload composes
    /// Player.GetWeaponModPanicReloadMod into GetWeaponModAttackSpeedMod(includeConditional: true), which only
    /// Creature_Combat.GetAnimSpeed calls.
    ///
    /// Creature_Vitals.cs, Creature_Combat.cs and EnchantmentManager.cs each newly diverge from upstream
    /// 08471633e as a direct result - each file's own class-remarks doc comment records it (see PR #493 for
    /// the flagged maintenance cost at the next upstream merge). This replaces the "registers them only, hooks
    /// are a separate follow-up phase" note this block carried immediately after the registration commit.
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
        /// down from 8140 to 8135 on 2026-07-30 with the five-row pool cut, and from 8135 to 8134 on
        /// 2026-08-17 when Swift Flight was retired in the catalog v4 pass.
        /// </summary>
        public const int PropertyBandEnd = 8134;

        /// <summary>
        /// Inclusive lower bound of the RETIRED PropertyFloat range - 8135-8140. 8136-8140 held Warding, Crit
        /// Ward, Resolute, Vigor and Mending until the 2026-07-30 pool cut; 8135 held Swift Flight until it was
        /// retired in the 2026-08-17 catalog v4 pass and folded into this same contiguous retired range so the
        /// live/retired boundary stays adjacent (<see cref="PropertyBandEnd"/> + 1 == this). Nothing in this
        /// system writes here any more, and no future property may take these ids: weapons on dev shards still
        /// carry records at them. Kept as constants so the "no dead rows" harness can scan the retired range
        /// too and prove nothing writes it, rather than merely stopping at the live band and never looking.
        /// </summary>
        public const int RetiredPropertyBandStart = 8135;

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

        /// <summary>
        /// Inclusive lower bound of the v3 TIER B CATALOG EXPANSION band (2026-08-06) - Heft, Tension,
        /// Leverage, Attunement, Focus and Execution. Deliberately a SECOND, disjoint band rather than an
        /// extension of <see cref="TierBPropertyBandEnd"/>: the original 8130-8149 system was filled edge to
        /// edge (6 Tier A active + 5 retired + 7 Tier B active + 2 Tier B reserved = 20 ids), so this
        /// expansion sits at 8021-8026, right after the multishot prototype pair. A Tier B record is now
        /// valid in EITHER <see cref="TierBPropertyBandStart"/>-<see cref="TierBPropertyBandEnd"/> or this
        /// band, and a Tier A record in neither.
        ///
        /// ALL SIX ARE TIER B, AND THE FIRST FOUR WERE TIER A FOR PART OF ONE DAY. The original v3 draft
        /// shipped Heft, Tension, Leverage and Attunement as Tier A rows writing PropertyInt.Damage,
        /// PropertyFloat.DamageMod (twice) and PropertyFloat.ElementalDamageMod. Every one of those natives
        /// is ALREADY owned by a layer 1 tinker material - Iron writes Damage, Mahogany writes DamageMod,
        /// Green Garnet writes ElementalDamageMod (WeaponTinkerMaterial.cs) - which the structural invariant
        /// in WeaponModInteractionTests.Registry_NoSpecialSharesANativePropertyWithALayerOneMaterial exists
        /// specifically to refuse: <see cref="WeaponModManager.ApplyReroll"/> reverses the whole layer 1
        /// composition and THEN clears the specials, so a shared native would be subtracted twice from a
        /// value only one of them contributed. Focus and Execution were blocked in that same draft by the
        /// crit invariant below. Tier B resolves both at once, because a Tier B row writes no native at all.
        /// </summary>
        public const int TierBExpansionBandStart = 8021;

        /// <summary>Inclusive upper bound of the v3 Tier B expansion band. See <see cref="TierBExpansionBandStart"/>.</summary>
        public const int TierBExpansionBandEnd = 8026;

        /// <summary>
        /// Inclusive lower bound of the v4 TIER B CATALOG EXPANSION band (2026-08-17) - Efficiency, Recovery,
        /// Mana Well, Cleanse, Longevity, Siphon, Quick Refresh, Arcane Defender and Panic Reload. A THIRD,
        /// disjoint band, immediately after the v3 expansion band above (8021-8026), taking the next free ids
        /// 8027-8035. All nine rows are hooked (2026-08-17 follow-up commits d66bfa995/0fa5fdb8e/ba0218211) -
        /// see the class remarks above and each row's own comment for its hook site. A Tier B record is valid
        /// in ANY of
        /// <see cref="TierBPropertyBandStart"/>-<see cref="TierBPropertyBandEnd"/>,
        /// <see cref="TierBExpansionBandStart"/>-<see cref="TierBExpansionBandEnd"/>, or this band.
        /// </summary>
        public const int TierBV4BandStart = 8027;

        /// <summary>Inclusive upper bound of the v4 Tier B expansion band. See <see cref="TierBV4BandStart"/>.</summary>
        public const int TierBV4BandEnd = 8035;

        /// <summary>Tolerance for "this reversal landed back on the engine default", per the design.</summary>
        public const double Epsilon = 1e-9;

        /// <summary>
        /// The TINKER budget: a weapon carries exactly ten slots, and since the 2026-08-06 decoupling they are
        /// shared by imbues and layer 1 tinkers ONLY.
        ///
        ///     10 = reservedSlots + tinkerCount
        ///
        /// SPECIALS ARE NO LONGER PART OF THIS SUM. Before the decoupling the identity was
        /// "10 = reserved + specials + tinkers", which made every special cost a tinker and therefore made an
        /// inert special strictly WORSE than no special at all. Specials are now bounded only by
        /// <see cref="MaxSpecials"/> and consume no slot here. See <see cref="WeaponModTinkerSet"/> for the
        /// consequences on the tinker log and on PropertyInt.NumTimesTinkered.
        /// </summary>
        public const int TotalSlots = 10;

        /// <summary>
        /// The permanent per-weapon special bound. A LITERAL CLAMP in code, not left implied by the odds table -
        /// a future tuning pass that raises weapon_mod_special_chance_4 must not be able to produce a fifth.
        ///
        /// RAISED FROM 3 TO 4 on 2026-08-06, together with the slot decoupling above. The two changes go
        /// together on purpose: a fourth special was not affordable while each one ate a tinker slot. THE ONLY
        /// ENFORCEMENT POINT IS <see cref="WeaponModRoller.ClampSpecialCount"/> (the reroll's draw). Before the
        /// same-day Amethyst rework, two more existed - WeaponModManager.ResolveRefusal's SwapAtSpecialCap arm
        /// and ApplySwap's own defence-in-depth check - because the swap USED to be able to ADD a special
        /// beyond what the weapon held. Amethyst is now a special-only reroll: it removes one held special and
        /// replaces it, so the count it operates on can never increase past what the weapon already had, and
        /// there is nothing left for a cap check to guard against on that path.
        ///
        /// NOTE the cap is NOT a promise that a draw of that size can be satisfied. The Tier A caster pool is
        /// exactly 3 deep (Devastation, Weak Point, Bloodthirst), so with weapon_mods_enabled OFF a caster
        /// asking for 4 distinct specials gets 3. Neither the 2026-08-06 v3 expansion nor the 2026-08-17 v4
        /// expansion changes this: every row in both is Tier B (see TierBExpansionBandStart and
        /// TierBV4BandStart), so none of them widen the Tier A-only pool the gate-off state draws from. With
        /// the gate ON the caster pool is 16 deep (3 Tier A + 2 Tier B v2 + 3 Tier B v3 + 8 Tier B v4),
        /// comfortably above the cap. That is harmless now that the tinker count no longer derives from the
        /// special count - a short draw simply yields fewer specials and leaves the ten tinker slots untouched.
        /// </summary>
        public const int MaxSpecials = 4;

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
                AffectsSingleTargetDamage = true,
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
                AffectsSingleTargetDamage = true,
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
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0} damage rating",
            },
            // WeaponModId.Cleave (id 4, PropertyFloat.WeaponModCleave 8133) sat here until 2026-08-07, when it
            // was removed from the catalog outright by repo-owner directive. Both ids are retired and must
            // never be reused - see WeaponModId's remarks and Source/property-registry.tsv. It was the only
            // row that ever set Binary, NativeFloor or an explicit MinPotency, and the only Tier A row that
            // wrote PropertyInt.Cleaving; that machinery is deliberately left in WeaponModDefinition rather
            // than deleted with it, because it is the general rule and not Cleave's private arrangement.
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
                // FALSE: conditional on the DEFENDER carrying a shield, which most of the monster set does not.
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.Melee | WeaponClass.Missile,
                DisplayFormat = "{0:0.##}% of a shield ignored",
                DisplayScale = 100.0,
            },
            // WeaponModId.SwiftFlight (id 6, PropertyFloat.WeaponModSwiftFlight 8135) sat here until
            // 2026-08-17, when it was retired in the catalog v4 pass by repo-owner directive. Both ids are
            // retired and must never be reused - see WeaponModId's remarks and Source/property-registry.tsv.

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

            // WeaponModId.LifeLeech/ManaLeech/StaminaLeech (ids 12-14, PropertyFloat 8141-8143) sat here
            // until 2026-08-17, when they were retired in the catalog v4 pass by repo-owner directive. All
            // three ids are retired and must never be reused - see WeaponModId's remarks and
            // Source/property-registry.tsv.
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
                //
                // RETUNED FROM 0.15 TO 0.30 on 2026-08-06 as part of the v3 magnitude pass - see
                // Docs/WeaponMods/DESIGN.md for the benchmark this was tuned against.
                MaxRoll = 0.30,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
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
                // RETUNED FROM 0.06 TO 0.24 on 2026-08-06 as part of the v3 magnitude pass - see
                // Docs/WeaponMods/DESIGN.md for the benchmark this was tuned against.
                MaxRoll = 0.24,
                NativeDefault = 0,
                // TRUE: more attacks per second is more damage on the SAME target, so it belongs in the
                // damage-relevant subset even though it writes no damage number of its own.
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.Melee | WeaponClass.Missile,
                DisplayFormat = "+{0:0.##}% attack speed",
                DisplayScale = 100.0,
            },
            // WeaponModId.Overload (id 17, PropertyFloat.WeaponModOverload 8146) sat here until 2026-08-17,
            // when it was retired in the catalog v4 pass by repo-owner directive. Both ids are retired and
            // must never be reused - see WeaponModId's remarks and Source/property-registry.tsv.
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
                // FALSE: sustain, and it pays out only once the target is already dead.
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.All,
                DisplayFormat = "restores {0:0.##}% of your maximum vitals on a killing blow",
                DisplayScale = 100.0,
            },

            /* ------------------------------------------------------------------------------------------
             * TIER B, V3 EXPANSION (2026-08-06). Six rows, ORIGINALLY DRAFTED AS TIER A THE SAME DAY and
             * reworked after an adversarial review found two structural collisions - see the PropertyFloat.cs
             * 8021-8026 comment block and WeaponModId.cs class remarks for the full account. In short:
             * Heft/Tension/Leverage/Attunement would each have written a native property a layer 1 tinker
             * material also owns, and Focus/Execution would have written the retail crit properties this
             * system routes around via the Gear* ratings. Tier B sidesteps both - no native property, no
             * collision.
             *
             * UNLIKE v2, NONE OF THESE SIX READS THROUGH Player_WeaponMods.cs. Each hooks INSIDE an existing
             * formula rather than composing additively at an outer call site, so each reads
             * WeaponModCombat.ReadWeaponOnly(weapon, ...) directly off the weapon parameter already in scope
             * at its hook site - no Player accessor needed, and the hook fires for a monster wielder exactly
             * as harmlessly as for a player one (ReadWeaponOnly returns 0 on a weapon with no record).
             *
             *   Heft       - Source/ACE.Server/Entity/BaseDamageMod.cs, the (BaseDamage, Creature, WorldObject)
             *                constructor: added into DamageBonus, alongside the existing
             *                weapon.EnchantmentManager.GetDamageBonus() term.
             *   Tension    - same constructor, added into DamageMod alongside weapon.GetProperty(DamageMod).
             *   Leverage   - same site as Tension; the two are mutually exclusive by class (missile vs melee)
             *                at ROLL time, so reading both unconditionally here is harmless.
             *   Attunement - WorldObject_Weapon.GetCasterElementalDamageModifier, added into the
             *                wielderEnchantments + weaponEnchantments sum before the PvP halving.
             *   Focus      - WorldObject_Weapon.GetWeaponCriticalChance AND GetWeaponMagicCritFrequency
             *                (physical and magic crit chance), added AFTER the Math.Max against the Critical
             *                Strike imbue, alongside the existing GetCritRating() * 0.01f term. Wired into
             *                BOTH functions - not just the one the original spec named - because Focus is
             *                Caster-eligible and GetWeaponCriticalChance is never reached by a spell cast;
             *                leaving GetWeaponMagicCritFrequency unwired would make Focus silently dead on
             *                every caster who rolls it, the same class of bug the Tier A rework itself was
             *                triggered by.
             *   Execution  - Entity/DamageEvent.cs, multiplies CriticalDamageMod by (1 + magnitude) right
             *                after GetWeaponCritDamageMod returns (physical crit damage); AND
             *                WorldObject_Weapon's weaponCritDamageMod in SpellProjectile.cs, at both call
             *                sites (life magic and war/void), for the same Caster-eligibility reason as Focus.
             *
             * THIS IS WHY BaseDamageMod.cs AND WorldObject_Weapon.cs NOW DIVERGE FROM UPSTREAM 08471633e -
             * BaseDamageMod.cs for the first time, WorldObject_Weapon.cs on top of its pre-existing multi-shot
             * prototype divergence. DamageEvent.cs and SpellProjectile.cs also diverge. See PR #493 for the
             * flagged maintenance cost at the next upstream merge.
             * ------------------------------------------------------------------------------------------ */

            new WeaponModDefinition
            {
                Id = WeaponModId.Heft,
                Tier = WeaponModTier.B,
                DisplayName = "Heft",
                Record = PropertyFloat.WeaponModHeft,
                // HOOK: BaseDamageMod's (BaseDamage, Creature, WorldObject) constructor, added into
                // DamageBonus. DamageBonus sits INSIDE the "(BaseDamage.MaxDamage + DamageBonus +
                // ElementalBonus) * DamageMod" bracket, so on a missile weapon it multiplies with Tension's
                // DamageMod bonus rather than adding alongside it.
                MaxRoll = 22,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.Melee | WeaponClass.Missile,
                DisplayFormat = "+{0:0.##} damage",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Tension,
                Tier = WeaponModTier.B,
                DisplayName = "Tension",
                Record = PropertyFloat.WeaponModTension,
                // HOOK: same BaseDamageMod constructor as Heft, added into DamageMod. DamageMod multiplies
                // the whole (base + bonus + elemental) bracket, so this stacks multiplicatively with Heft.
                MaxRoll = 0.50,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.Missile,
                DisplayFormat = "+{0:0.##} damage mod",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Leverage,
                Tier = WeaponModTier.B,
                DisplayName = "Leverage",
                Record = PropertyFloat.WeaponModLeverage,
                // Same hook as Tension, on the melee side of the pool instead of missile.
                MaxRoll = 0.30,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.Melee,
                DisplayFormat = "+{0:0.##} damage mod",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Attunement,
                Tier = WeaponModTier.B,
                DisplayName = "Attunement",
                Record = PropertyFloat.WeaponModAttunement,
                // HOOK: WorldObject_Weapon.GetCasterElementalDamageModifier, added into the enchantment sum
                // before the PvP halving.
                MaxRoll = 0.28,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.Caster,
                DisplayFormat = "+{0:0.##} elemental damage mod",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Focus,
                Tier = WeaponModTier.B,
                DisplayName = "Focus",
                Record = PropertyFloat.WeaponModFocus,
                // HOOKS: WorldObject_Weapon.GetWeaponCriticalChance (physical) and GetWeaponMagicCritFrequency
                // (magic), both added AFTER their Math.Max against the Critical Strike imbue, alongside the
                // existing GetCritRating() * 0.01f term. Never writes CriticalFrequency itself, so the crit
                // routes-through-ratings invariant is untouched.
                MaxRoll = 0.25,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0.##}% critical chance",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Execution,
                Tier = WeaponModTier.B,
                DisplayName = "Execution",
                Record = PropertyFloat.WeaponModExecution,
                // HOOKS: Entity/DamageEvent.cs multiplies CriticalDamageMod by (1 + magnitude) right after
                // GetWeaponCritDamageMod returns (physical); SpellProjectile.cs multiplies weaponCritDamageMod
                // the same way at both its call sites (life magic and war/void). A MULTIPLIER, deliberately -
                // GearCritDamage is meant to layer ON TOP of a weapon-side effect, and routing this through the
                // rating pool instead would leave nothing for it to multiply. Never writes CriticalMultiplier
                // itself.
                //
                // 0.50 rather than 1.00 BECAUSE it is a multiplier. The catalog was tuned against an additive
                // reading (CriticalDamageMod = 2 + magnitude); multiplying instead gives 2 * (1 + magnitude),
                // which is exactly twice as strong. 0.50 reproduces the tuned physical curve digit for digit -
                // missile p50/p90/p99/p99.9 = 0.884/1.051/1.259/1.446, melee 0.846/1.027/1.254/1.457 - where
                // 1.00 pushed missile p99.9 to 1.75 and the theoretical best to 2.37x the quest benchmark.
                // Caster lands slightly lower than its tuned figure because its crit base is 1.0 against the
                // physical 2.0, so one MaxRoll cannot preserve both; that pulls the one over-target class
                // toward its target. See tools/weapon-mod-sim (branch tools/weapon-mod-sim).
                MaxRoll = 0.50,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0.##}% critical damage",
                DisplayScale = 100.0,
            },

            /* ------------------------------------------------------------------------------------------
             * TIER B, V4 EXPANSION (2026-08-17). Nine utility rows, ALL HOOKED as of the three follow-up
             * commits d66bfa995/0fa5fdb8e/ba0218211. Each row's comment names its ACTUAL hook site
             * (Type.Method), matching the summary in the class remarks above. See the class remarks above for
             * why utility rows are back after the 2026-07-30 damage-oriented-only directive was superseded.
             * ------------------------------------------------------------------------------------------ */

            new WeaponModDefinition
            {
                Id = WeaponModId.Efficiency,
                Tier = WeaponModTier.B,
                DisplayName = "Efficiency",
                Record = PropertyFloat.WeaponModEfficiency,
                // HOOK: Player_Combat.GetAttackStamina multiplies staminaCost by
                // WeaponModCombat.CostMultiplier(GetWeaponOnlyModValue(Efficiency)); Player_Magic.CalculateManaUsage
                // multiplies manaUsed by WeaponModCombat.CostMultiplier(GetCasterOnlyModValue(Efficiency)), the
                // former Overload branch.
                MaxRoll = 0.50,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.All,
                DisplayFormat = "-{0:0.##}% attack stamina or spell mana cost",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Recovery,
                Tier = WeaponModTier.B,
                DisplayName = "Recovery",
                Record = PropertyFloat.WeaponModRecovery,
                // HOOK: Creature_Vitals.VitalHeartBeat(CreatureVital) multiplies recoveryMod, from
                // WeaponModCombat.RegenerationMultiplier, into the same currentTick product as stanceMod - so
                // Recovery is still zeroed by the 0.5 combat stance mod like every other regen term.
                MaxRoll = 0.50,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.All,
                DisplayFormat = "+{0:0.##}% natural regeneration",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.ManaWell,
                Tier = WeaponModTier.B,
                DisplayName = "Mana Well",
                Record = PropertyFloat.WeaponModManaWell,
                // HOOK: Player.ApplyWeaponModManaWell, called from ApplyWeaponModCreatureDeath alongside Second
                // Wind and Cleanse. Pours MaxRoll mana points into every equipped item with headroom, clamped
                // per item - never a fraction of that item's pool.
                // FLAT, NOT A PERCENTAGE (repo-owner correction, 2026-08-17; revised same day from 30 to 5):
                // MaxRoll is mana POINTS restored to EACH equipped item on a killing blow, not a fraction of
                // that item's mana pool. No DisplayScale, unlike every other Tier B v4 row.
                MaxRoll = 5,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.All,
                DisplayFormat = "restores {0:0} mana to every equipped item on a killing blow",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Cleanse,
                Tier = WeaponModTier.B,
                DisplayName = "Cleanse",
                Record = PropertyFloat.WeaponModCleanse,
                // HOOK: Player.ApplyWeaponModCleanse, called from ApplyWeaponModCreatureDeath alongside Second
                // Wind and Mana Well - a sibling, not conditional on Second Wind. Rolls the magnitude as a
                // probability and strips one HARMFUL enchantment (no EnchantmentTypeFlags.Beneficial) off the
                // wielder via DispellingEdgeAbility.SelectDispellable.
                MaxRoll = 1.00,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.All,
                DisplayFormat = "{0:0.##}% chance a killing blow removes a harmful enchantment",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Longevity,
                Tier = WeaponModTier.B,
                DisplayName = "Longevity",
                Record = PropertyFloat.WeaponModLongevity,
                // HOOK: EnchantmentManager.Add's refresh path AND BuildEntry, both immediately after the
                // pre-existing AugmentationIncreasedSpellDuration term - both sites are required, or Longevity
                // would be dead on either a fresh cast or a re-cast. Self-cast only.
                MaxRoll = 0.25,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.Caster,
                DisplayFormat = "+{0:0.##}% duration on your self-cast beneficial enchantments",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.Siphon,
                Tier = WeaponModTier.B,
                DisplayName = "Siphon",
                Record = PropertyFloat.WeaponModSiphon,
                // HOOK: Player.ApplyWeaponModOutgoingDamage (physical) and Player.ApplyWeaponModSpellHit (spell)
                // both funnel into the shared Player.TryWeaponModSiphon helper, which strips one BENEFICIAL
                // enchantment off the target via DispellingEdgeAbility.SelectDispellable and re-applies it to
                // the wielder at the spell's own full duration.
                MaxRoll = 0.10,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.All,
                DisplayFormat = "{0:0.##}% chance on hit to steal a beneficial enchantment",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.QuickRefresh,
                Tier = WeaponModTier.B,
                DisplayName = "Quick Refresh",
                Record = PropertyFloat.WeaponModQuickRefresh,
                // HOOK: Player.GetWeaponModCastSpeedMod (Player_WeaponMods_Casting.cs), composed multiplicatively
                // into Player_Magic's castSpeedMultiplier alongside ApplyClassAbilityCastSpeed at both cast
                // entry points. Self-cast beneficial enchantments only; NO CEILING BY DESIGN - self-buffs only
                // cut downtime between casts, so there is nothing for a cap to protect.
                MaxRoll = 1.00,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.Caster,
                DisplayFormat = "+{0:0.##}% cast speed on self-cast beneficial enchantments",
                DisplayScale = 100.0,
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.ArcaneDefender,
                Tier = WeaponModTier.B,
                DisplayName = "Arcane Defender",
                Record = PropertyFloat.WeaponModArcaneDefender,
                // HOOK: Creature_Combat.GetShieldMod, a new no-shield branch treating the wand as a virtual
                // shield of MaxRoll armor level (CalcArmorMod(25) = 0.7273), skipping the Shield-skill cap that
                // would otherwise zero it for a caster. Frontal (180 degree cone) physical only, via
                // WeaponModCombat.ArcaneShieldMod. Does NOT enable Shield Block or Thorns - both gate directly
                // on GetEquippedShield() != null and never read GetShieldMod.
                // MaxRoll corrected from 50 to 40, then revised again to 25 (repo-owner correction, 2026-08-17).
                MaxRoll = 25,
                NativeDefault = 0,
                AffectsSingleTargetDamage = false,
                Classes = WeaponClass.Caster,
                DisplayFormat = "deflects frontal blows as a {0:0} armor level shield",
            },
            new WeaponModDefinition
            {
                Id = WeaponModId.PanicReload,
                Tier = WeaponModTier.B,
                DisplayName = "Panic Reload",
                Record = PropertyFloat.WeaponModPanicReload,
                // HOOK: Player.GetWeaponModPanicReloadMod, composed inside
                // GetWeaponModAttackSpeedMod(includeConditional: true) - only Creature_Combat.GetAnimSpeed
                // passes true, so the /classabilities readout (which uses the parameterless overload) stays
                // unconditional and does not change with position. Fires while a hostile is within 6 m cylinder
                // distance (WeaponModCombat.IsPanicReloadThreat / PanicReloadRangeSq).
                MaxRoll = 0.10,
                NativeDefault = 0,
                AffectsSingleTargetDamage = true,
                Classes = WeaponClass.Missile,
                DisplayFormat = "+{0:0.##}% attack speed with a hostile within 6 m",
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

        /// <summary>
        /// The Tier A half - four v1 rows (Devastation, Weak Point, Bloodthirst, Shield Bypass), each writing
        /// a native property. Cleave and Swift Flight are retired and no longer appear here. In table order.
        /// </summary>
        public static readonly IReadOnlyList<WeaponModDefinition> TierAMods = mods.Where(m => m.Tier == WeaponModTier.A).ToArray();

        /// <summary>
        /// The Tier B half - three surviving v2 rows (Ambush, Quickening, Second Wind; the three leeches and
        /// Overload are retired) plus the six v3-expansion rows (2026-08-06) plus the nine v4-expansion rows
        /// (2026-08-17, all hooked), all read live at combat time and none writing a native property.
        /// In table order.
        /// </summary>
        public static readonly IReadOnlyList<WeaponModDefinition> TierBMods = mods.Where(m => m.Tier == WeaponModTier.B).ToArray();

        private static readonly Dictionary<WeaponModId, WeaponModDefinition> byId = mods.ToDictionary(m => m.Id);
        private static readonly Dictionary<PropertyFloat, WeaponModDefinition> byRecord = mods.ToDictionary(m => m.Record);

        // Tier A rows keyed by the NATIVE they add into, for the per-wielder suppression reads (WeaponModSuppression),
        // which run on every rating read of a suppressed player. ToDictionary throws on a duplicate key, which is the
        // same invariant WeaponModInteractionTests pins: no two specials share a native.
        private static readonly Dictionary<PropertyInt, WeaponModDefinition> tierAByNativeInt = mods.Where(m => m.Tier == WeaponModTier.A && m.NativeInt != null).ToDictionary(m => m.NativeInt.Value);
        private static readonly Dictionary<PropertyFloat, WeaponModDefinition> tierAByNativeFloat = mods.Where(m => m.Tier == WeaponModTier.A && m.NativeFloat != null).ToDictionary(m => m.NativeFloat.Value);

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

        private static readonly Dictionary<WeaponClass, WeaponModDefinition[]> tierADamagePools = new Dictionary<WeaponClass, WeaponModDefinition[]>
        {
            { WeaponClass.Melee,   mods.Where(m => m.Tier == WeaponModTier.A && m.AffectsSingleTargetDamage && m.AppliesTo(WeaponClass.Melee)).ToArray() },
            { WeaponClass.Missile, mods.Where(m => m.Tier == WeaponModTier.A && m.AffectsSingleTargetDamage && m.AppliesTo(WeaponClass.Missile)).ToArray() },
            { WeaponClass.Caster,  mods.Where(m => m.Tier == WeaponModTier.A && m.AffectsSingleTargetDamage && m.AppliesTo(WeaponClass.Caster)).ToArray() },
        };

        private static readonly Dictionary<WeaponClass, WeaponModDefinition[]> bothTierDamagePools = new Dictionary<WeaponClass, WeaponModDefinition[]>
        {
            { WeaponClass.Melee,   mods.Where(m => m.AffectsSingleTargetDamage && m.AppliesTo(WeaponClass.Melee)).ToArray() },
            { WeaponClass.Missile, mods.Where(m => m.AffectsSingleTargetDamage && m.AppliesTo(WeaponClass.Missile)).ToArray() },
            { WeaponClass.Caster,  mods.Where(m => m.AffectsSingleTargetDamage && m.AppliesTo(WeaponClass.Caster)).ToArray() },
        };

        private static readonly WeaponModDefinition[] emptyPool = new WeaponModDefinition[0];

        /// <summary>
        /// The specials a given weapon class may roll RIGHT NOW, against the live weapon_mods_enabled gate.
        /// Pool depth is the arithmetic the design's pricing rests on, so both states are asserted in the tests
        /// rather than left to inspection:
        ///
        ///   gate OFF (the default) - melee 4, missile 4, caster 3
        ///   gate ON                - melee 16, missile 17, caster 16
        ///
        /// UNCHANGED AT THE GATE-OFF DEPTHS BY EVERY TIER B EXPANSION SO FAR (v3 2026-08-06, v4 2026-08-17):
        /// every row each expansion adds is Tier B (see TierBExpansionBandStart, TierBV4BandStart), so none
        /// widens the Tier A-only pool the gate-off state draws from. The missile gate-OFF depth went from 5
        /// to 4 on 2026-08-17 specifically because Swift Flight (missile-only Tier A) was retired in the v4
        /// catalog pass, not because of anything the v4 additions themselves do.
        ///
        /// The gate-OFF depths describe an inert system: with weapon_mods_enabled false nothing can roll at all,
        /// because the crafting entry points refuse first. They are still pinned because this exclusion is the
        /// LAST line between a shard with the system off and live Tier B combat code, and a pool that quietly
        /// started returning Tier B rows would be invisible until it was not.
        ///
        /// THE TIER A CASTER POOL IS SHALLOWER THAN <see cref="MaxSpecials"/> (3 against 4) - a caster draw of
        /// 4 with the gate off comes back with 3. With the gate on the caster pool is 16 deep, comfortably
        /// above the cap.
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

        /// <summary>
        /// The DAMAGE-RELEVANT subset of <see cref="Pool(WeaponClass)"/>: every row in the class pool whose
        /// <see cref="WeaponModDefinition.AffectsSingleTargetDamage"/> is true, against the live gate.
        ///
        /// This is what weapon_mod_guarantee_damage_special draws the FIRST special from, so that a set is never
        /// entirely utility. It is a strict subset of the class pool, so a caller may treat a definition that
        /// came out of here as if it had come out of <see cref="Pool(WeaponClass)"/> - the distinctness and
        /// exclusion rules are unchanged.
        ///
        /// IT CAN BE EMPTY and callers MUST handle that rather than assume the depths below. The depths are
        /// melee 3 / missile 3 / caster 3 with the gate off (Tier A only, unchanged since the retired rows
        /// were never damage-relevant), and melee 9 / missile 10 / caster 7 with it on: the six v3 rows are
        /// all damage-relevant (adding 4/4/3 on top of surviving v2's 2/2/1), and of the nine v4 rows only
        /// Panic Reload is (adding 0/1/0, missile only) - but the subset is a per-row flag, not a promise: a
        /// future class whose rows are all utility would empty it, and <see cref="WeaponModRoller.RollSpecial"/>
        /// falls back to the full pool in that case.
        /// </summary>
        public static IReadOnlyList<WeaponModDefinition> DamagePool(WeaponClass weaponClass) => DamagePool(weaponClass, Enabled());

        /// <summary>The damage-relevant subset at an EXPLICIT gate state. Pure - see <see cref="Pool(WeaponClass, bool)"/>.</summary>
        public static IReadOnlyList<WeaponModDefinition> DamagePool(WeaponClass weaponClass, bool includeTierB)
        {
            var source = includeTierB ? bothTierDamagePools : tierADamagePools;

            return source.TryGetValue(weaponClass, out var pool) ? pool : emptyPool;
        }

        public static WeaponModDefinition Get(WeaponModId id) => byId[id];

        public static bool TryGet(WeaponModId id, out WeaponModDefinition definition) => byId.TryGetValue(id, out definition);

        public static bool TryGet(PropertyFloat record, out WeaponModDefinition definition) => byRecord.TryGetValue(record, out definition);

        /// <summary>The Tier A row that adds into the integer native <paramref name="native"/>, if any. Keyed by the NATIVE, not the record.</summary>
        public static bool TryGetTierAByNative(PropertyInt native, out WeaponModDefinition definition) => tierAByNativeInt.TryGetValue(native, out definition);

        /// <summary>The Tier A row that adds into the float native <paramref name="native"/>, if any. Keyed by the NATIVE, not the record - contrast <see cref="TryGet(PropertyFloat, out WeaponModDefinition)"/>.</summary>
        public static bool TryGetTierAByNativeFloat(PropertyFloat native, out WeaponModDefinition definition) => tierAByNativeFloat.TryGetValue(native, out definition);
    }
}
