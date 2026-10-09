using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP rules settings seam: the compiled Defaults equal the registered DefaultPropertyManager defaults
    /// (the brief's exact values), the [PVP] rules log line, the /pvprules report, and the post-/modify hook.
    ///
    /// SEEDING: the only PropertyManager key read or written is pvp_damage_cap (the /modify hook test, seeded to
    /// its registered default and restored in finally). Every DialSource read goes through a swapped seam, and
    /// PropertyAdminService.AuditInGame and SettingUpdated are swapped and restored.
    /// </summary>
    [TestClass]
    public class PvpRuleTunablesTests
    {
        [TestMethod]
        public void Defaults_AreTheBriefValues()
        {
            var d = PvpRuleTunables.Defaults;

            Assert.IsTrue(d.Enabled);
            Assert.AreEqual(0L, d.DamageCap);
            Assert.AreEqual(0.5, d.DamageCapMaxHealthFraction);
            Assert.AreEqual(1.0, d.DamageRatingScale);
            Assert.AreEqual(1.0, d.CritDamageRatingScale);
            Assert.IsTrue(d.BlockAirborneConsumables);
            Assert.AreEqual(0L, d.ConsumableMinIntervalMs);
            Assert.AreEqual(300L, d.DispelVulnLockSeconds);
            Assert.AreEqual(100.0, d.CloakDamageReduction);
            Assert.IsTrue(d.CleaveEnabled);
            Assert.IsTrue(d.StripRareBuffs);
            Assert.AreEqual(1.0, d.WarMagicDamageMod);
            Assert.AreEqual(1.0, d.MeleeDamageMod);
            Assert.AreEqual(1.0, d.MissileDamageMod);
            Assert.AreEqual(1.0, d.CritDamageMod);
            Assert.AreEqual(1.0, d.MagicAbsorbMod);
            Assert.AreEqual(1.0, d.HealingMod);
            Assert.AreEqual(1.0, d.CsCritMod);
            Assert.AreEqual(1.0, d.CbCritMod);
            Assert.AreEqual(0L, d.HealthFloor);
            Assert.AreEqual(0L, d.HealthCeiling);
        }

        [TestMethod]
        public void Defaults_MatchTheRegisteredPropertyDefaults()
        {
            var described = PvpRuleTunables.Describe(PvpRuleTunables.Defaults);

            CollectionAssert.AreEqual(PvpRuleTunables.Keys.ToArray(), described.Select(r => r.Key).ToArray(), "Describe must cover every key, in Keys order");

            foreach (var (key, value) in described)
            {
                var registered = PvpRuleTunables.RegisteredDefaultText(key);
                Assert.IsNotNull(registered, $"{key} is not registered in DefaultPropertyManager");
                Assert.AreEqual(registered, value, $"{key}: compiled default and registered default disagree");
            }
        }

        [TestMethod]
        public void Keys_AreTypedAsTheBriefSays()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("pvp_rules_enabled"));
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("pvp_block_airborne_consumables"));
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("pvp_damage_cap"));
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("pvp_consumable_min_interval_ms"));
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("pvp_dispel_vuln_lock_seconds"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_damage_cap_max_health_fraction"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_damage_rating_scale"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_crit_damage_rating_scale"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_cloak_damage_reduction"));
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("pvp_cleave_enabled"));
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("pvp_strip_rare_buffs"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_war_magic_damage_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_melee_damage_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_missile_damage_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_crit_damage_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_magic_absorb_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_healing_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_cs_crit_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("pvp_cb_crit_mod"));
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("pvp_health_floor"));
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("pvp_health_ceiling"));
        }

        [TestMethod]
        public void LogLine_ListsEveryKeyValue_AndTheReason()
        {
            var line = PvpRuleTunables.FormatLogLine(PvpRuleTunables.Defaults with { DamageCap = 250 }, "boot");

            Assert.AreEqual(
                "[PVP] rules pvp_rules_enabled=True pvp_damage_cap=250 pvp_damage_cap_max_health_fraction=0.5 pvp_damage_rating_scale=1 " +
                "pvp_crit_damage_rating_scale=1 pvp_block_airborne_consumables=True pvp_consumable_min_interval_ms=0 pvp_dispel_vuln_lock_seconds=300 " +
                "pvp_cloak_damage_reduction=100 pvp_cleave_enabled=True pvp_strip_rare_buffs=True pvp_war_magic_damage_mod=1 pvp_melee_damage_mod=1 " +
                "pvp_missile_damage_mod=1 pvp_crit_damage_mod=1 pvp_magic_absorb_mod=1 pvp_healing_mod=1 " +
                "pvp_cs_crit_mod=1 pvp_cb_crit_mod=1 pvp_health_floor=0 pvp_health_ceiling=0 pvp_melee_defense_mod=1 pvp_missile_defense_mod=1 pvp_magic_defense_mod=1 reason=boot",
                line);
        }

        [TestMethod]
        public void PvpRulesReport_MarksOnlyNonDefaults_AndListsEveryChokePoint()
        {
            var report = PvpRulesCommands.BuildReport(PvpRuleTunables.Defaults with { DamageCap = 250, DamageRatingScale = 0.75 });

            StringAssert.Contains(report, "  * pvp_damage_cap = 250 (default 0)");
            StringAssert.Contains(report, "  * pvp_damage_rating_scale = 0.75 (default 1)");
            StringAssert.Contains(report, "    pvp_damage_cap_max_health_fraction = 0.5");
            StringAssert.Contains(report, "    pvp_rules_enabled = True");

            Assert.AreEqual(2, report.Split('\n').Count(l => l.StartsWith("  * ", StringComparison.Ordinal)), "exactly the two changed keys are marked");

            foreach (PvpChokePoint point in Enum.GetValues(typeof(PvpChokePoint)))
                StringAssert.Contains(report, $"{point}=");
        }

        [TestMethod]
        public void PvpRulesReport_SaysWhenTheMasterSwitchIsOff()
        {
            StringAssert.Contains(PvpRulesCommands.BuildReport(PvpRuleTunables.Defaults with { Enabled = false }), "pvp_rules_enabled is OFF");
        }

        [TestMethod]
        public void ReadDials_ThrowingOrNullSource_FallsBackToDefaults()
        {
            var saved = PvpRuleTunables.DialSource;

            try
            {
                PvpRuleTunables.DialSource = () => throw new InvalidOperationException("seam failure");
                Assert.AreEqual(PvpRuleTunables.Defaults, PvpRules.ReadDials());

                PvpRuleTunables.DialSource = () => null;
                Assert.AreEqual(PvpRuleTunables.Defaults, PvpRules.ReadDials());

                PvpRuleTunables.DialSource = null;
                Assert.AreEqual(PvpRuleTunables.Defaults, PvpRules.ReadDials());
            }
            finally
            {
                PvpRuleTunables.DialSource = saved;
            }
        }

        /// <summary>A successful /modify of any key reaches PropertyAdminService.SettingUpdated with that key; an unknown key does not.</summary>
        [TestMethod]
        public void Modify_NotifiesSettingUpdated_OnlyOnSuccess()
        {
            var priorAudit = PropertyAdminService.AuditInGame;
            var priorUpdated = PropertyAdminService.SettingUpdated;
            var notified = new System.Collections.Generic.List<string>();
            var capDefault = DefaultPropertyManager.DefaultLongProperties["pvp_damage_cap"].Item;

            try
            {
                PropertyManager.ModifyLong("pvp_damage_cap", capDefault);
                PropertyAdminService.AuditInGame = (p, m) => { };
                PropertyAdminService.SettingUpdated = key => notified.Add(key);

                var ok = PropertyAdminService.TryModify(PropertyAdminService.TypeLong, "pvp_damage_cap", "250", PropertyAdminActor.InGame(null));
                Assert.AreEqual(PropertyModifyOutcome.Updated, ok.Outcome);

                var unknown = PropertyAdminService.TryModify(PropertyAdminService.TypeLong, "pvp_not_a_real_key", "1", PropertyAdminActor.InGame(null));
                Assert.AreEqual(PropertyModifyOutcome.UnknownKey, unknown.Outcome);

                CollectionAssert.AreEqual(new[] { "pvp_damage_cap" }, notified);
            }
            finally
            {
                PropertyAdminService.AuditInGame = priorAudit;
                PropertyAdminService.SettingUpdated = priorUpdated;
                PropertyManager.ModifyLong("pvp_damage_cap", capDefault);
            }
        }

        /// <summary>
        /// The [PVP] rules log fires only for the eight lever keys: an arena pvp_ key, a non-pvp key and null never
        /// reach it. Observed through DialSource, which LogResolved reads exactly once per line it logs.
        /// </summary>
        [TestMethod]
        public void OnPropertyModified_LogsOnlyForLeverKeys_AndNeverThrows()
        {
            var saved = PvpRuleTunables.DialSource;
            var reads = 0;

            try
            {
                PvpRuleTunables.DialSource = () => { reads++; return PvpRuleTunables.Defaults; };

                PvpRuleTunables.OnPropertyModified("pvp_arena_enabled");
                PvpRuleTunables.OnPropertyModified("pvp_rating_initial");
                PvpRuleTunables.OnPropertyModified("xp_modifier");
                PvpRuleTunables.OnPropertyModified(null);
                Assert.AreEqual(0, reads, "a non-lever key must not log the [PVP] rules line");

                foreach (var key in PvpRuleTunables.Keys)
                    PvpRuleTunables.OnPropertyModified(key);
                Assert.AreEqual(PvpRuleTunables.Keys.Count, reads, "every lever key logs once");
            }
            finally
            {
                PvpRuleTunables.DialSource = saved;
            }
        }
    }
}
