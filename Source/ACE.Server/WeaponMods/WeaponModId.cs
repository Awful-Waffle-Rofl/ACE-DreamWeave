namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Which half of the catalog a modifier belongs to. The distinction is NOT cosmetic - it decides how the
    /// modifier is stored, how it is reversed, and whether it is gated.
    ///
    /// <see cref="A"/> - v1. Writes a NATIVE property the server already reads. Its record holds the APPLIED
    /// MAGNITUDE in the native property's own units, and reversal subtracts that number back off, because the
    /// item may already have carried a loot-generated value at that property.
    ///
    /// <see cref="B"/> - v2. Writes NO native property at all: every effect is read live off the equipped
    /// weapon at combat time. Its record holds the applied magnitude as a FRACTION (0.04 = 4%), reversal is a
    /// bare RemoveProperty with no arithmetic and nothing to restore, and the whole tier rides the system's one
    /// gate, weapon_mods_enabled, exactly as Tier A does.
    /// </summary>
    public enum WeaponModTier
    {
        A = 1,
        B = 2,
    }

    /// <summary>
    /// The weapon-modifier catalog: Tier A v1 (5 entries, after Cleave was retired on 2026-08-07) plus Tier B
    /// v2 (7 entries) plus the v3 Tier B expansion (6 entries). A Tier A entry sets a NATIVE property the
    /// server already reads, so it needed no combat code; a Tier B entry is read live at combat time by
    /// hand-wired hooks - v2's in
    /// Player_WeaponMods.cs, v3's in BaseDamageMod.cs, WorldObject_Weapon.cs, DamageEvent.cs and
    /// SpellProjectile.cs (see WeaponModRegistry.cs class remarks for why v3 is wired differently from v2).
    ///
    /// Numeric values are in-memory identifiers only: nothing persists by this id. What persists on a weapon
    /// is the modifier's own PropertyFloat - 8130-8135 less the retired 8133 for Tier A v1, 8141-8147 for
    /// Tier B v2, 8021-8026 for
    /// the v3 Tier B expansion - holding the APPLIED MAGNITUDE (deliberately unlike EquipmentMods, which
    /// stores a potency scalar - see WeaponModDefinition for why). Append only, never renumber, never reuse.
    ///
    /// IDS 7-11 ARE RETIRED. They were Warding, CritWard, Resolute, Vigor and Mending until 2026-07-30, when
    /// the pool was cut to damage-oriented modifiers only. Their reserved PropertyFloats 8136-8140 are retired
    /// with them (RETIRED_DO_NOT_REUSE in Source/property-registry.tsv). Nothing persists by the ids in THIS
    /// enum, so retiring them is a readability rule rather than a data one - but reusing 7-11 for something
    /// else would make every comment and test message about the cut read as a lie. Tier B therefore starts at
    /// 12, skipping the retired block entirely.
    ///
    /// ID 4 IS RETIRED ON THE SAME TERMS. It was Cleave until 2026-08-07, when the modifier was removed from
    /// the catalog outright; its PropertyFloat 8133 is retired with it (RETIRED_DO_NOT_REUSE in
    /// Source/property-registry.tsv, guarded by WaveChallengePropertyTests). Tier A therefore runs 1, 2, 3,
    /// 5, 6 with a hole at 4, exactly as the numbering rule requires - append only, never renumber, never
    /// reuse. Weapons on dev shards that rolled Cleave keep an orphaned PropertyInt.Cleaving native and a
    /// stale 8133 record; nothing reverses either, because the row that knew how to is gone.
    ///
    /// SUNDER AND RAMPAGE ARE DELIBERATELY ABSENT. Both are phase 2: Sunder needs a new enchantment/spell row
    /// to carry its debuff and Rampage needs genuinely new per-target stack state. Their PropertyFloats 8148
    /// and 8149 are reserved in the registry, but adding an enum member here before the effect exists would
    /// put an inert entry into every roll pool.
    ///
    /// THE V3 EXPANSION (2026-08-06) STARTS AT 19, after v2's 18, rather than filling in after 6 - the
    /// numbering is append-only and does not care which tier a later addition belongs to.
    ///
    /// V3 WAS ORIGINALLY DRAFTED AS TIER A (writing native properties directly) AND REWORKED TO TIER B THE
    /// SAME DAY, after an adversarial review found two structural collisions: Heft/Tension/Leverage/Attunement
    /// would each have shared a native property with an existing layer 1 tinker material (Iron, Mahogany,
    /// Mahogany, Green Garnet respectively), which the pre-existing
    /// Registry_NoSpecialSharesANativePropertyWithALayerOneMaterial test exists specifically to catch; and
    /// Focus/Execution, proposed to write PropertyFloat.CriticalFrequency 147 / CriticalMultiplier 136
    /// directly, would have collided with the HARD "crit routes through ratings, never the retail properties"
    /// invariant a few lines below. Routing all six through Tier B - reading their magnitude live rather than
    /// writing a native property at all - resolves both collisions at once.
    ///
    /// SWIFT FLIGHT (6), LIFELEECH/MANALEECH/STAMINALEECH (12-14) AND OVERLOAD (17) ARE RETIRED, 2026-08-17.
    /// Catalog v4 cut them on the repo-owner directive to bring utility rows back in a new form; their
    /// PropertyFloats (8135, 8141-8143, 8146) are retired with them (RETIRED_DO_NOT_REUSE in
    /// Source/property-registry.tsv). Ids are deleted rather than renamed so nothing can write them again -
    /// Tier A now runs 1, 2, 3, 5 with holes at 4 and 6; Tier B v2 now runs 15, 16, 18 with holes at 12-14
    /// and 17.
    ///
    /// V4 (2026-08-17) ADDS NINE TIER B UTILITY ROWS AT 25-33, ALL NINE NOW HOOKED (2026-08-17). See
    /// WeaponModRegistry.cs's class remarks for the one-row-per-modifier table and each row's hook site.
    /// </summary>
    public enum WeaponModId
    {
        // ---- Tier A (v1): each writes a native property ----
        Devastation  = 1,
        WeakPoint    = 2,
        Bloodthirst  = 3,
        // 4 was Cleave, retired 2026-08-07 - see the remarks above. Do not reuse it.
        ShieldBypass = 5,
        // 6 was SwiftFlight, retired 2026-08-17 - see the remarks above. Do not reuse it.

        // ---- Tier B (v2): each is read live at combat time, none writes a native property ----
        // 12-14 were LifeLeech, ManaLeech, StaminaLeech, retired 2026-08-17 - see the remarks above. Do not reuse them.
        Ambush       = 15,
        Quickening   = 16,
        // 17 was Overload, retired 2026-08-17 - see the remarks above. Do not reuse it.
        SecondWind   = 18,

        // ---- Tier B, v3 expansion (2026-08-06): read live, same as v2, but wired at different call sites
        // (see WeaponModRegistry.cs class remarks) because each hooks INSIDE an existing formula rather than
        // composing additively at an outer call site ----
        Heft         = 19,
        Tension      = 20,
        Leverage     = 21,
        Attunement   = 22,
        Focus        = 23,
        Execution    = 24,

        // ---- Tier B, v4 expansion (2026-08-17): registered but INERT this pass; hooks land in a later phase ----
        Efficiency       = 25,
        Recovery         = 26,
        ManaWell         = 27,
        Cleanse          = 28,
        Longevity        = 29,
        Siphon           = 30,
        QuickRefresh     = 31,
        ArcaneDefender   = 32,
        PanicReload      = 33,
    }
}
