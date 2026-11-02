using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ExecuteEffect: scales a landed hit's damage up when the defender is at or below a health threshold.
    /// Mutates DamageEvent.Damage synchronously (the same shape ExecutionerAbility itself uses), so it is
    /// fully exercisable through the real dispatch site with no ActionChain involved.
    /// </summary>
    [TestClass]
    public class MonsterEffectExecuteTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_damage_rider_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_damage_rider_cap"].Item);
        }

        private static readonly ExecuteEffect Effect = new ExecuteEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"execute {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("bonus=0.5"), out _), "hp= defaults to 0.25");

            Assert.IsFalse(Effect.Validate(Spec("bonus=0"), out var error), "bonus= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("bonus=0.5 hp=0"), out _), "hp= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("bonus=0.5 hp=1.5"), out _), "hp= must be <= 1");
        }

        [TestMethod]
        public void Validate_RejectsABonusAuthoredAsAFlatAmount()
        {
            // bonus= is a MULTIPLIER of the landed hit (extra = damage * bonus), so 40 would be a 41x hit.
            Assert.IsFalse(Effect.Validate(Spec("bonus=40"), out var error), "bonus=40 reads as +4000%");
            StringAssert.Contains(error, "MULTIPLIER");
            Assert.IsFalse(Effect.Validate(Spec("hp=0.2 bonus=2.5"), out _), "just over the maximum");

            Assert.IsTrue(Effect.Validate(Spec("bonus=0.4"), out _), "0.4 = +40%");
            Assert.IsTrue(Effect.Validate(Spec($"bonus={ExecuteEffect.MaxBonus}"), out _), "the maximum itself is accepted");
        }

        /// <summary>
        /// Every shipped weenie that authors an execute record must parse and validate. The 2026-10-26 bug
        /// shipped 12 high-band creatures with bonus=30..60 (a flat-amount reading of a multiplier) because
        /// nothing ever ran the handler's Validate over the content. No database, no dats: it reads the .sql text.
        /// </summary>
        [TestMethod]
        public void Content_EveryShippedExecuteRecordParsesAndValidates()
        {
            var weeniesDir = Path.Combine(FindContentDir(), "sql", "weenies");
            var effectsString = new Regex(@"\b9015\s*,\s*'((?:[^']|'')*)'", RegexOptions.Compiled);

            var executeRecords = 0;
            var problems = new System.Collections.Generic.List<string>();

            foreach (var file in Directory.EnumerateFiles(weeniesDir, "*.sql"))
            {
                foreach (Match match in effectsString.Matches(File.ReadAllText(file)))
                {
                    var raw = match.Groups[1].Value.Replace("\u0027\u0027", "\u0027");

                    if (raw.IndexOf("execute", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    MonsterEffectParser.Parse(raw, out var specs, out var errors);

                    foreach (var error in errors.Where(e => e.IndexOf("execute", StringComparison.OrdinalIgnoreCase) >= 0))
                        problems.Add($"{Path.GetFileName(file)}: parse: {error}");

                    foreach (var spec in specs.Where(s => s.Kind == "execute"))
                    {
                        executeRecords++;

                        if (!Effect.Validate(spec, out var error))
                            problems.Add($"{Path.GetFileName(file)}: execute rejected: {error}");
                    }
                }
            }

            Assert.IsTrue(executeRecords >= 30, $"the scan found only {executeRecords} execute records - the regex or the path is wrong, not the content");
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static string FindContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content");

                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.sql", SearchOption.AllDirectories).Any())
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        [TestMethod]
        public void Magnitude_ScalesDamageWhenTheDefenderIsBelowTheThreshold()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 100);
            defender.Health.Current = 20;   // 20% <= the default 25% hp= threshold

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("bonus=0.5"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(15.0f, damageEvent.Damage, 0.001f, "10 * (1 + 0.5) = 15");
        }

        [TestMethod]
        public void CapClamp_TheAddedDamageIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 3.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 100);
            defender.Health.Current = 20;

            // bonus=2.0 (the maximum Validate accepts) on a 10-damage hit would add 20 without the cap
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("bonus=2.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(13.0f, damageEvent.Damage, 0.001f, "10 + the 3.0-capped addition, not 10 + 20");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheScaling()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 100);
            defender.Health.Current = 20;

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("bonus=0.5"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(10.0f, damageEvent.Damage, 0.001f, "the master switch must stop the scaling entirely");
        }
    }
}
