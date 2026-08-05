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
    /// The weapon-modifier catalog: Tier A (v1, 6 entries) plus Tier B (v2, 7 entries). A Tier A entry sets a
    /// NATIVE property the server already reads, so it needed no combat code; a Tier B entry is read live at
    /// combat time by hand-wired hooks in Player_WeaponMods.cs.
    ///
    /// Numeric values are in-memory identifiers only: nothing persists by this id. What persists on a weapon
    /// is the modifier's own PropertyFloat - 8130-8135 for Tier A, 8141-8147 for Tier B - holding the APPLIED
    /// MAGNITUDE (deliberately unlike EquipmentMods, which stores a potency scalar - see WeaponModDefinition
    /// for why). Append only, never renumber, never reuse.
    ///
    /// IDS 7-11 ARE RETIRED. They were Warding, CritWard, Resolute, Vigor and Mending until 2026-07-30, when
    /// the pool was cut to damage-oriented modifiers only. Their reserved PropertyFloats 8136-8140 are retired
    /// with them (RETIRED_DO_NOT_REUSE in Source/property-registry.tsv). Nothing persists by the ids in THIS
    /// enum, so retiring them is a readability rule rather than a data one - but reusing 7-11 for something
    /// else would make every comment and test message about the cut read as a lie. Tier B therefore starts at
    /// 12, skipping the retired block entirely.
    ///
    /// SUNDER AND RAMPAGE ARE DELIBERATELY ABSENT. Both are phase 2: Sunder needs a new enchantment/spell row
    /// to carry its debuff and Rampage needs genuinely new per-target stack state. Their PropertyFloats 8148
    /// and 8149 are reserved in the registry, but adding an enum member here before the effect exists would
    /// put an inert entry into every roll pool.
    /// </summary>
    public enum WeaponModId
    {
        // ---- Tier A (v1): each writes a native property ----
        Devastation  = 1,
        WeakPoint    = 2,
        Bloodthirst  = 3,
        Cleave       = 4,
        ShieldBypass = 5,
        SwiftFlight  = 6,

        // ---- Tier B (v2): each is read live at combat time, none writes a native property ----
        LifeLeech    = 12,
        ManaLeech    = 13,
        StaminaLeech = 14,
        Ambush       = 15,
        Quickening   = 16,
        Overload     = 17,
        SecondWind   = 18,
    }
}
