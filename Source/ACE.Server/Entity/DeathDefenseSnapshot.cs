namespace ACE.Server.Entity
{
    /// <summary>
    /// A player's protective and debuff state at the hit that killed them (player_death_event). Built by
    /// DeathTelemetry.BuildDefenseSnapshot from the victim's enchantment registry at the killing hit, and held on the
    /// killing <see cref="DamageHistoryEntry"/> until Player.OnDeath writes the row. Plain values only: no live object.
    ///
    /// The element fields are null (not false) when the hit's damage type has no single resistance element, or (for
    /// the two category flags) when the element has no life-magic category, which is the case for Nether.
    /// </summary>
    public sealed class DeathDefenseSnapshot
    {
        /// <summary>The element the element columns were read for: slash, pierce, bludgeon, cold, fire, acid, electric, nether, or none.</summary>
        public string Element;

        /// <summary>Any enchantment in the element's Vulnerability category. Null when the element has no category.</summary>
        public bool? HadVulnerability;
        public uint? VulnerabilitySpellId;

        /// <summary>Any ArmorLowering (Imperil) enchantment. Always read, whatever the attack kind.</summary>
        public bool HadImperil;
        public uint? ImperilSpellId;

        /// <summary>Any enchantment in the element's Protection or rare Protection category. Null when the element has no category.</summary>
        public bool? HadElementProtection;
        public uint? ElementProtectionSpellId;

        /// <summary>Any ArmorRaising (Armor Self) or rare ArmorRaising enchantment. Always read, whatever the attack kind.</summary>
        public bool HadArmorSelf;
        public uint? ArmorSelfSpellId;

        /// <summary>
        /// EnchantmentManager.GetProtectionResistanceMod(element): the enchantment protection factor
        /// Creature.GetResistanceMod reads before it folds in natural resistance, augmentations, hollow and rending.
        /// Null when the element is none.
        /// </summary>
        public float? ProtResistMod;

        /// <summary>EnchantmentManager.GetVulnerabilityResistanceMod(element), the vulnerability factor beside it. Null when the element is none.</summary>
        public float? VulnResistMod;

        /// <summary>EnchantmentManager.GetBodyArmorMod(): the net additive body armor (Armor Self minus Imperil) the armor math adds.</summary>
        public int? BodyArmorMod;
    }
}
