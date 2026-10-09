namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// One resolved, immutable snapshot of every PvP balance lever (Docs/Pvp/DESIGN.md "PvP rules (levers)").
    /// Built by <see cref="PvpRuleTunables.DialSource"/>; read ONLY after an interaction has been classified as
    /// PvP (<see cref="PvpClassifier.Classify"/>) or, for the arena-only levers (the consumable rules and
    /// pvp_healing_mod), after the actor or healed player is confirmed bound to a Live arena match
    /// (<see cref="PvpPlayerRules.InLiveMatch"/>), so a PvE path never reads a lever. A PK timer alone never
    /// qualifies.
    /// </summary>
    /// <param name="Enabled">pvp_rules_enabled - master switch. False: every lever hands its input back unchanged.</param>
    /// <param name="DamageCap">pvp_damage_cap - absolute per-hit ceiling; 0 or less = off.</param>
    /// <param name="DamageCapMaxHealthFraction">pvp_damage_cap_max_health_fraction - per-hit ceiling as a fraction of the defender's max health; 0 or less = off.</param>
    /// <param name="DamageRatingScale">pvp_damage_rating_scale - multiplier on damage / damage resist rating (choke points R1, R2).</param>
    /// <param name="CritDamageRatingScale">pvp_crit_damage_rating_scale - multiplier on crit damage / crit damage resist rating (R1, R2).</param>
    /// <param name="BlockAirborneConsumables">pvp_block_airborne_consumables - arena only: refuse consumable completion while airborne and in a Live arena match (H1, H2).</param>
    /// <param name="ConsumableMinIntervalMs">pvp_consumable_min_interval_ms - arena only: minimum ms between consumable uses while in a Live arena match; 0 = off (H2).</param>
    /// <param name="DispelVulnLockSeconds">pvp_dispel_vuln_lock_seconds - window after the target's last PK attack during which a vulnerability cast by ANOTHER player cannot be dispelled or cleansed; 0 = off (D1, D2).</param>
    /// <param name="CloakDamageReduction">pvp_cloak_damage_reduction - the flat amount of damage a cloak proc mitigates on a PvP hit. Ported from Doctide `pvp_cloak_max_dmg_mitigation`. Defaults 100 (CLK1).</param>
    /// <param name="CleaveEnabled">pvp_cleave_enabled - whether a cleaving melee strike may hit player targets. False skips player candidates. Ported from Doctide `disable_pvp_cleave` (inverted). Defaults true (CLV1).</param>
    /// <param name="StripRareBuffs">pvp_strip_rare_buffs - strips rare-gem buffs from a player on every successful PvP hit. Ported from Doctide `dispel_rares_pvp`. Defaults true (RB1).</param>
    /// <param name="WarMagicDamageMod">pvp_war_magic_damage_mod - multiplier on a PvP war magic projectile hit (Spell.School == WarMagic only; never void or life), before the damage cap. Defaults 1.0 (M2).</param>
    /// <param name="MeleeDamageMod">pvp_melee_damage_mod - multiplier on a PvP melee hit (CombatType.Melee), before the damage cap. Defaults 1.0 (M1).</param>
    /// <param name="MissileDamageMod">pvp_missile_damage_mod - multiplier on a PvP missile hit (CombatType.Missile: bow, crossbow, atlatl, thrown), before the damage cap. Defaults 1.0 (M1).</param>
    /// <param name="CritDamageMod">pvp_crit_damage_mod - multiplier on the WHOLE of a PvP critical hit (melee, missile, and war/void/life projectile crits), before the damage cap. Distinct from pvp_crit_damage_rating_scale, which scales rating terms only. Defaults 1.0 (M1, M2).</param>
    /// <param name="MagicAbsorbMod">pvp_magic_absorb_mod - multiplier on the magic absorb REDUCTION fraction on a PvP projectile hit: absorbMod = 1 - (1 - absorbMod) * m, clamped to [0, 1], after the retail PvP 0.72. Defaults 1.0 (AB1).</param>
    /// <param name="HealingMod">pvp_healing_mod - ARENA ONLY: multiplier on Health heals RECEIVED by a player in a Live arena match (heal spells and gems, Health kits, Health food and potions, heal-over-time ticks, each recipient's Drain / Transfer Health gain). Open-world PK heals are never scaled. Defaults 1.0 (HL1-HL5F).</param>
    /// <param name="CsCritMod">pvp_cs_crit_mod - multiplier on the Critical Strike imbue's crit CHANCE bonus above the unimbued baseline on a PvP hit: base + (cs - base) x m. 0 = the imbue adds nothing. Defaults 1.0 (CS1, CS2).</param>
    /// <param name="CbCritMod">pvp_cb_crit_mod - multiplier on the Crippling Blow imbue's crit-damage multiplier above 1.0 on a PvP hit: 1 + (cb - 1) x m. Never the tinkered CriticalMultiplier. Defaults 1.0 (CB1).</param>
    /// <param name="HealthFloor">pvp_health_floor - effective-HP normalization lower bound on the defender's max health; 0 or less = off. Defaults 0 (M1, M2, N3, N4).</param>
    /// <param name="HealthCeiling">pvp_health_ceiling - effective-HP normalization upper bound on the defender's max health; 0 or less = off. Defaults 0 (M1, M2, N3, N4).</param>
    /// <param name="MeleeDefenseMod">pvp_melee_defense_mod - multiplier on a PvP DEFENDER's effective melee defense skill in the evade roll only (DamageEvent.GetEvadeChance, CombatType.Melee). 1.0 = identity. Defaults 1.0 (DF1).</param>
    /// <param name="MissileDefenseMod">pvp_missile_defense_mod - multiplier on a PvP DEFENDER's effective missile defense skill in the evade roll only (DamageEvent.GetEvadeChance, CombatType.Missile). 1.0 = identity. Defaults 1.0 (DF1).</param>
    /// <param name="MagicDefenseMod">pvp_magic_defense_mod - multiplier on a PvP target's effective magic defense in the spell resist roll only (WorldObject.TryResistSpell). 1.0 = identity. Defaults 1.0 (DF2).</param>
    public sealed record PvpRuleDials(
        bool Enabled,
        long DamageCap,
        double DamageCapMaxHealthFraction,
        double DamageRatingScale,
        double CritDamageRatingScale,
        bool BlockAirborneConsumables,
        long ConsumableMinIntervalMs,
        long DispelVulnLockSeconds,
        double CloakDamageReduction,
        bool CleaveEnabled,
        bool StripRareBuffs,
        double WarMagicDamageMod,
        double MeleeDamageMod,
        double MissileDamageMod,
        double CritDamageMod,
        double MagicAbsorbMod,
        double HealingMod,
        double CsCritMod,
        double CbCritMod,
        long HealthFloor,
        long HealthCeiling,
        double MeleeDefenseMod = 1.0,
        double MissileDefenseMod = 1.0,
        double MagicDefenseMod = 1.0);
}
