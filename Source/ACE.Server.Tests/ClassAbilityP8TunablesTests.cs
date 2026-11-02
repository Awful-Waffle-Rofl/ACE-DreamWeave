using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the registered defaults for the 2026-09-12 class ability overhaul's wave of 15 new
    /// REGISTRATION-ONLY abilities: Hunter's Mark, Pinning Shot, Opportunist, Killer Instinct, Rallying
    /// Presence, Kinetic Charge, Reflect (enum/token ReflectMagic), Adrenaline, Vengeance, Quickened
    /// Casting, Umbral Siphon, Soul Jump, Hemomancy, Spellweave, Runic Ward.
    ///
    /// This is the contract the six mechanic slices are specced against - if a slice's key does not match
    /// one pinned here, the slice mistyped it. An unregistered key does not fail cleanly: PropertyManager's
    /// GetDouble/GetLong miss the seeded cache and fall through to a live shard-config read, which throws a
    /// MySqlException that reads like unrelated infrastructure trouble rather than a missing key.
    ///
    /// None of these abilities is Implemented yet, so there is no mechanic to exercise here - only the
    /// registered numbers themselves.
    /// </summary>
    [TestClass]
    public class ClassAbilityP8TunablesTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;
        private static long L(string key) => PropertyManager.GetLong(key).Item;

        [TestMethod]
        public void HuntersMark_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.02, D("class_ability_huntersmark_percent_per_rank"), 1e-9);
            Assert.AreEqual(10.0, D("class_ability_huntersmark_duration_seconds"), 1e-9);
        }

        [TestMethod]
        public void PinningShot_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.10, D("class_ability_pinningshot_chance_base"), 1e-9);
            Assert.AreEqual(0.10, D("class_ability_pinningshot_chance_step"), 1e-9);
            Assert.AreEqual(5.0, D("class_ability_pinningshot_duration_seconds"), 1e-9);
            Assert.AreEqual(8.0, D("class_ability_pinningshot_immunity_seconds"), 1e-9);

            // the post-pin movement slow (2026-09-29): 10 seconds at a fifth speed, measured from the pin END.
            // Zero seconds, or a factor outside the open interval (0.0, 1.0), disables the slow with no
            // code-path change - see Creature.TryApplyClassAbilityMoveSlow.
            Assert.AreEqual(10.0, D("class_ability_pinningshot_slow_seconds"), 1e-9);
            Assert.AreEqual(0.2, D("class_ability_pinningshot_slow_factor"), 1e-9);
        }

        [TestMethod]
        public void Opportunist_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.02, D("class_ability_opportunist_percent_per_rank"), 1e-9);
        }

        [TestMethod]
        public void KillerInstinct_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.04, D("class_ability_killerinstinct_critdamage_base"), 1e-9);
            Assert.AreEqual(0.03, D("class_ability_killerinstinct_critdamage_step"), 1e-9);
            Assert.AreEqual(0.02, D("class_ability_killerinstinct_critchance_per_stack"), 1e-9);
            Assert.AreEqual(8.0, D("class_ability_killerinstinct_opening_duration_seconds"), 1e-9);
            Assert.AreEqual(3L, L("class_ability_killerinstinct_max_stacks"));
        }

        [TestMethod]
        public void RallyingPresence_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.01, D("class_ability_rallyingpresence_percent_per_rank"), 1e-9);
            Assert.AreEqual(0.50, D("class_ability_rallyingpresence_self_share"), 1e-9);
            Assert.AreEqual(15.0, D("class_ability_rallyingpresence_radius"), 1e-9);
        }

        [TestMethod]
        public void KineticCharge_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.20, D("class_ability_kineticcharge_percent_per_rank"), 1e-9);
            Assert.AreEqual(10.0, D("class_ability_kineticcharge_expire_seconds"), 1e-9);
            Assert.AreEqual(5L, L("class_ability_kineticcharge_charge_threshold"));
            Assert.AreEqual(2L, L("class_ability_kineticcharge_cleave_targets"));
        }

        [TestMethod]
        public void Reflect_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.06, D("class_ability_reflectmagic_chance_base"), 1e-9);
            Assert.AreEqual(0.06, D("class_ability_reflectmagic_chance_step"), 1e-9);

            // the creature-spell immunity window (2026-10-17): 0.15/0.30/0.45s by rank
            Assert.AreEqual(0.15, D("class_ability_reflectmagic_immunity_seconds_base"), 1e-9);
            Assert.AreEqual(0.15, D("class_ability_reflectmagic_immunity_seconds_step"), 1e-9);
        }

        [TestMethod]
        public void Adrenaline_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.04, D("class_ability_adrenaline_percent_per_rank"), 1e-9, "doubled from 0.02 in the 2026-10-24 Berserker pass");
            Assert.AreEqual(6.0, D("class_ability_adrenaline_window_seconds"), 1e-9);
        }

        // Vengeance_TunablesMatchSignedOffDefaults lived here until 2026-10-24: Vengeance was retired in the
        // Berserker pass and its two keys deleted (Database/Updates/Shard/2026-10-24-06-Retire-Berserker-Keys.sql).

        [TestMethod]
        public void BerserkerPass_NewTunablesMatchSignedOffDefaults()
        {
            // Berserk Rage: 5 s immunity, 90/60/30 s cooldown by rank, x1.5 outgoing while raging
            Assert.AreEqual(5.0, D("class_ability_berserkrage_duration_seconds"), 1e-9);
            Assert.AreEqual(90.0, D("class_ability_berserkrage_cooldown_seconds_r1"), 1e-9);
            Assert.AreEqual(60.0, D("class_ability_berserkrage_cooldown_seconds_r2"), 1e-9);
            Assert.AreEqual(30.0, D("class_ability_berserkrage_cooldown_seconds_r3"), 1e-9);
            Assert.AreEqual(1.5, D("class_ability_berserkrage_damage_multiplier"), 1e-9);

            // Heavy Blows: power part +25% per rank; Intimidate: 15 defense per rank
            Assert.AreEqual(0.25, D("class_ability_heavyblows_stretch_per_rank"), 1e-9);
            Assert.AreEqual(15.0, D("class_ability_intimidate_defense_per_rank"), 1e-9);

            // Whirlwind: +35% / +75% / +100% stamina by rank
            Assert.AreEqual(0.35, D("class_ability_whirlwind_stamina_surcharge_r1"), 1e-9);
            Assert.AreEqual(0.75, D("class_ability_whirlwind_stamina_surcharge_r2"), 1e-9);
            Assert.AreEqual(1.0, D("class_ability_whirlwind_stamina_surcharge_r3"), 1e-9);

            // Damage Rating: 30% / 60% / 100% of total Crit Damage Rating
            Assert.AreEqual(0.30, D("class_ability_damagerating_critdamage_conversion_r1"), 1e-9);
            Assert.AreEqual(0.60, D("class_ability_damagerating_critdamage_conversion_r2"), 1e-9);
            Assert.AreEqual(1.00, D("class_ability_damagerating_critdamage_conversion_r3"), 1e-9);

            // Bloodlust 0.015 -> 0.03
            Assert.AreEqual(0.03, D("class_ability_bloodlust_percent_per_rank"), 1e-9);
        }

        // QuickenedCasting_TunablesMatchSignedOffDefaults lived here until 2026-10-24: Quickened Casting was retired
        // in the Archmage pass and its key deleted (Database/Updates/Shard/2026-10-24-11-Retire-Archmage-Keys.sql).

        [TestMethod]
        public void ArchmagePass_NewTunablesMatchSignedOffDefaults()
        {
            // Elemental Inversion 2%/rank, Arcane Hollow 10%/rank, Volatile Wall 3/6/10%, Mana Rush cap +50%
            Assert.AreEqual(0.02, D("class_ability_elementalinversion_chance_per_rank"), 1e-9);
            Assert.AreEqual(0.10, D("class_ability_arcanehollow_ignore_per_rank"), 1e-9);
            Assert.AreEqual(0.03, D("class_ability_volatilewall_bonus_r1"), 1e-9);
            Assert.AreEqual(0.06, D("class_ability_volatilewall_bonus_r2"), 1e-9);
            Assert.AreEqual(0.10, D("class_ability_volatilewall_bonus_r3"), 1e-9);
            Assert.AreEqual(0.50, D("class_ability_netherrush_max_bonus"), 1e-9);

            // Mana Barrier cap 0.25 -> 0.30
            Assert.AreEqual(0.30, D("class_ability_manabarrier_max_share"), 1e-9);
        }

        [TestMethod]
        public void VolatileElements_TunableMatchesSignedOffDefault()
        {
            // Spellsword T2, 2026-10-30: 8/16/24% weakest-element chance at ranks 1-3
            Assert.AreEqual(0.08, D("class_ability_volatileelements_chance_per_rank"), 1e-9);
        }

        [TestMethod]
        public void UmbralSiphon_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.02, D("class_ability_umbralsiphon_percent_per_rank"), 1e-9);
        }

        [TestMethod]
        public void SoulJump_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(360.0, D("class_ability_souljump_cooldown_seconds_base"), 1e-9);
            Assert.AreEqual(-120.0, D("class_ability_souljump_cooldown_seconds_step"), 1e-9);
            Assert.AreEqual(0.10, D("class_ability_souljump_restore_fraction"), 1e-9);

            // Soul Jump's affinity direction (against Jump) is UNSETTLED - see SoulJumpAbility's doc
            // comment. Do not add an assertion for a rate pair here: none is registered for Soul Jump
            // beyond the pre-existing class_ability_affinity_souljump_rate_per_trained/_per_spec, and
            // this test does not own those (they predate this wave).
        }

        /// <summary>
        /// Guards the 2026-10-02 owner ruling that RETIRED Soul Jump's and Void Damage's own affinity
        /// rate keys outright, rather than standardizing their values onto the shared
        /// class_ability_affinity_rate_per_trained / _per_spec pair. Standardizing values would have
        /// left four redundant levers that silently drift the next time the shared pair itself moves;
        /// retiring them means both abilities inherit any future shared-pair change with no
        /// per-ability follow-up.
        ///
        /// <see cref="PropertyManager.ModifyDouble"/> returns false for a key
        /// <see cref="DefaultPropertyManager.DefaultDoubleProperties"/> does not contain - that refusal
        /// is the discriminating check here: if any of the four keys were ever re-registered, this
        /// assertion would start failing (ModifyDouble would return true) even though nothing else in
        /// this test file reads them any more.
        ///
        /// Bloodlust is deliberately EXCLUDED and untouched by this ruling - its two keys
        /// (class_ability_affinity_bloodlust_rate_per_trained/_per_spec) stay registered, pinned EQUAL
        /// to each other by design, not to the shared pair. It is now the ONLY ability with its own
        /// rate pair.
        /// </summary>
        [TestMethod]
        public void SoulJumpAndVoidDamage_AffinityRateKeysAreRetired()
        {
            Assert.IsFalse(PropertyManager.ModifyDouble("class_ability_affinity_souljump_rate_per_trained", 0.12),
                "class_ability_affinity_souljump_rate_per_trained must stay unregistered - Soul Jump reads the shared pair now");
            Assert.IsFalse(PropertyManager.ModifyDouble("class_ability_affinity_souljump_rate_per_spec", 0.17),
                "class_ability_affinity_souljump_rate_per_spec must stay unregistered - Soul Jump reads the shared pair now");
            Assert.IsFalse(PropertyManager.ModifyDouble("class_ability_affinity_voiddamage_rate_per_trained", 0.12),
                "class_ability_affinity_voiddamage_rate_per_trained must stay unregistered - Void Damage reads the shared pair now");
            Assert.IsFalse(PropertyManager.ModifyDouble("class_ability_affinity_voiddamage_rate_per_spec", 0.17),
                "class_ability_affinity_voiddamage_rate_per_spec must stay unregistered - Void Damage reads the shared pair now");
        }

        [TestMethod]
        public void Hemomancy_TunablesMatchSignedOffDefaults()
        {
            // Blood Mage buff pass rework: the heal-armed damage buff's keys. The old tick/heal/drain keys
            // are no longer read (their defaults retire in a later package).
            Assert.AreEqual(0.02, D("class_ability_hemomancy_damage_percent_per_rank"), 1e-9);
            Assert.AreEqual(0.05, D("class_ability_hemomancy_heal_threshold_fraction"), 1e-9);
            Assert.AreEqual(8.0, D("class_ability_hemomancy_duration_seconds"), 1e-9);
            Assert.AreEqual(2.0, D("class_ability_hemomancy_leech_window_seconds"), 1e-9);
        }

        [TestMethod]
        public void Spellweave_TunablesMatchSignedOffDefaults()
        {
            Assert.AreEqual(0.03, D("class_ability_spellweave_percent_per_rank"), 1e-9);
        }

        [TestMethod]
        public void RunicWard_TunablesMatchSignedOffDefaults()
        {
            // Spellsword pass 2026-10-24: gain 3/5/7% -> 3/6/9%, flat 15% cap -> per-rank 10/20/30%
            Assert.AreEqual(0.03, D("class_ability_runicward_gain_base"), 1e-9);
            Assert.AreEqual(0.03, D("class_ability_runicward_gain_step"), 1e-9);
            Assert.AreEqual(0.10, D("class_ability_runicward_cap_fraction_r1"), 1e-9);
            Assert.AreEqual(0.20, D("class_ability_runicward_cap_fraction_r2"), 1e-9);
            Assert.AreEqual(0.30, D("class_ability_runicward_cap_fraction_r3"), 1e-9);
            Assert.AreEqual(12.0, D("class_ability_runicward_duration_seconds"), 1e-9);
            Assert.AreEqual(100, PropertyManager.GetLong("class_ability_runicward_shield_level").Item);
        }
    }
}
