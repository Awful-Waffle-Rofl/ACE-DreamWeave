namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// The equipment-mod catalog (v1 = 27 mods, extended to 30 alongside the Mana Barrier/Nether
    /// Bloom/Soul Tether class abilities, extended to 49 by the 2026-08-04 class-catalog reconciliation
    /// covering BloodMage, Spellsword and a Vanguard Provoke/Bellow/Shield Wall correction, reduced to 47
    /// REGISTERED rows 2026-10-02 when HeavyDraw and ShieldCheck were retired - see those members below;
    /// their ids and PropertyFloats stay reserved so the enum remains append-only). Each mod is a bounded
    /// late-game power layer applied to a piece of eligible gear from repurposed dead salvage, and each
    /// one amplifies (or stands in for) exactly one class ability - see
    /// <see cref="EquipmentModDefinition.LinkedAbility"/>.
    ///
    /// Numeric values are in-memory identifiers only: nothing persists by this id. What persists on an item
    /// is the mod's own PropertyFloat in the reserved 8100-8199 band, holding a POTENCY SCALAR in [0, 1].
    /// Append only, never renumber, never reuse - the ids are mirrored in save/report tooling.
    /// </summary>
    public enum EquipmentModId
    {
        // Archer
        Deadeye          = 1,
        EagleEye         = 2,
        LongDraw         = 3,
        HeavyDraw        = 4,   // RETIRED 2026-10-02 (dead equipment mod cleanup) - id reserved, never re-use;
                                 // the linked class ability (HeavyDraw) was retired 2026-09-12 with no
                                 // replacement, so this mod was removed from EquipmentModRegistry's roll
                                 // table rather than repurposed. No gear migration needed: pre-launch, shard
                                 // wiped the same day.
        Splitshot        = 5,
        DoubleVolley     = 6,

        // Rogue
        Venom            = 7,
        AcidProc         = 8,
        Caustic          = 9,
        Riposte          = 10,
        AttackSpeed      = 11,

        // Vanguard
        Thorns           = 12,
        ShieldCheck      = 13,  // RETIRED 2026-10-02 (dead equipment mod cleanup) - id reserved, never re-use;
                                 // the linked class ability (ShieldCheck) was retired 2026-09-12 with no
                                 // replacement, so this mod was removed from EquipmentModRegistry's roll
                                 // table rather than repurposed. No gear migration needed: pre-launch, shard
                                 // wiped the same day.
        Bulwark          = 14,

        // Berserker
        FrenziedPace     = 15,
        LingeringFury    = 16,
        SavageBlows      = 17,
        // 18 was Blood Fury until 2026-08-17. The class ability it amplified was retired in the same change
        // and the mod was REPURPOSED IN PLACE rather than retired with it: the id and its PropertyFloat (8117)
        // are unchanged, so every already-rolled item keeps a live mod and simply re-labels as Break Armor.
        // Both the old and new roles are bounded at MaxMagnitude 0.03, so no live item gained or lost budget.
        BreakArmor       = 18,
        Executioner      = 19,
        Bloodlust        = 20,

        // Archmage
        Overchannel      = 21,
        EchoCast         = 22,
        ElementalRend    = 23,
        Resonance        = 24,
        ManaBarrier      = 28,

        // Void / Summon
        VoidDamage       = 25,
        Withering        = 26,
        EmpoweredSummons = 27,
        NetherBloom      = 29,
        SoulTether       = 30,

        // Blood Mage
        BloodCharge      = 31,
        Transfusion      = 32,
        Hemorrhage       = 33,
        Deepen           = 34,
        Bloodletting     = 35,
        Sanguinate       = 36,
        BloodPrice       = 37,
        Clotting         = 38,

        // Spellsword
        Spellblade       = 39,
        Harmonics        = 40,
        Runeblade        = 41,
        Sundermark       = 42,
        Surge            = 43,
        Spellstorm       = 44,
        Cascade          = 45,
        DispellingEdge   = 46,

        // Vanguard
        Provoke          = 47,
        Bellow           = 48,
        ShieldWall       = 49,
    }
}
