namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// Shipped defaults for every Knot loot setting. DefaultPropertyManager rows and the table and trait registries
    /// reference these, so a default is written once. Nothing here reads the property store (only
    /// <see cref="KnotLootTunables"/> may).
    /// </summary>
    public static class KnotLootDefaults
    {
        // ---- Grael table ----

        public const bool GraelEnabled = true;

        public const long GraelWeightElementalInfusion = 1;
        public const long GraelWeightArmorInfusion = 1;
        public const long GraelWeightCripplingInfusion = 1;

        public const long GraelWeightMirrorglassOrb = 40;
        public const long GraelWeightShieldOfTheRose = 30;
        public const long GraelWeightFrugalGauntlets = 20;
        public const long GraelWeightGlassTurtleBracelet = 10;

        public const long GraelMmdMin = 25;
        public const long GraelMmdMax = 50;
        public const long GraelNoteMin = 25;
        public const long GraelNoteMax = 50;

        // ---- Knot level window (shared shape; Grael defaults) ----

        public const long GraelWindowLowLevel = 200;
        public const long GraelWindowHighLevel = 400;
        public const double GraelWindowLowStart = 0.0;
        public const double GraelWindowHighStart = 0.70;
        public const double GraelWindowWidth = 0.30;

        // ---- Matron table ----

        public const bool MatronEnabled = true;

        public const long MatronWeightElementalInfusion = 1;
        public const long MatronWeightArmorInfusion = 1;
        public const long MatronWeightCripplingInfusion = 1;

        public const long MatronWeightVoidclaw = 1;
        public const long MatronWeightSpines = 1;
        public const long MatronWeightTorc = 1;
        public const long MatronWeightHiveStingerBaton = 1;
        public const long MatronWeightHiveNetherBaton = 1;

        /// <summary>Chance the independent Spines group drops (knot_loot_matron_chance_spines).</summary>
        public const double MatronChanceSpines = 1.0;

        public const long MatronMmdMin = 25;
        public const long MatronMmdMax = 50;
        public const long MatronNoteMin = 25;
        public const long MatronNoteMax = 50;

        public const long MatronWindowLowLevel = 200;
        public const long MatronWindowHighLevel = 400;
        public const double MatronWindowLowStart = 0.0;
        public const double MatronWindowHighStart = 0.70;
        public const double MatronWindowWidth = 0.30;

        // ---- Upgraded Knot cloak (UpgradedCloak) ----

        // Max level window: t = clamp((L - LowLevel) / (HighLevel - LowLevel), 0, 1);
        // lo = MinLevelBase + round-half-up(MinLevelSpan * t), hi = MaxLevelBase + round-half-up(MaxLevelSpan * t).
        public const long UpgradedCloakLowLevel = 200;
        public const long UpgradedCloakHighLevel = 400;
        public const int UpgradedCloakMinLevelBase = 5;
        public const int UpgradedCloakMinLevelSpan = 3;
        public const int UpgradedCloakMaxLevelBase = 6;
        public const int UpgradedCloakMaxLevelSpan = 4;

        /// <summary>The summed set level the cloak's set bonus keeps scaling to (PropertyInt.SetBonusMaxLevel).</summary>
        public const int UpgradedCloakSetBonusMaxLevel = 10;

        /// <summary>Cloaked in Skill magnitude on the upgraded cloak (the spell's own value is 20).</summary>
        public const double UpgradedCloakCloakedInSkillValue = 40;

        /// <summary>Damage reduction proc amount on the upgraded cloak (retail 200).</summary>
        public const int UpgradedCloakDamageReduction = 400;

        // ---- Traits (ranges are positive magnitudes) ----

        public const bool CastSpeedEnabled = true;
        public const double CastSpeedMin = 1.0;
        public const double CastSpeedMax = 2.0;

        public const bool ShieldTradeEnabled = true;
        public const double ShieldTradeArmorMin = 125.0;
        public const double ShieldTradeArmorMax = 500.0;
        public const double ShieldTradeHealthMin = 25.0;
        public const double ShieldTradeHealthMax = 100.0;

        public const bool FrugalHandEnabled = true;
        public const double FrugalHandMin = 0.1;
        public const double FrugalHandMax = 0.3;

        public const bool WristTradeEnabled = true;
        public const double WristTradeGainMin = 5.0;
        public const double WristTradeGainMax = 10.0;
        public const double WristTradeCostMin = 10.0;
        public const double WristTradeCostMax = 20.0;

        // Overpowering Intent: fraction X; the caster's War/Void skill counts as (1 + X) times its value for the
        // target's magic resist check only, against non-player targets.
        public const bool OverpoweringIntentEnabled = true;
        public const double OverpoweringIntentMin = 0.05;
        public const double OverpoweringIntentMax = 0.10;
    }
}
