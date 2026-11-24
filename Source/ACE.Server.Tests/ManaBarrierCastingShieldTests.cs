using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Mana Barrier casting shield (owner ruling 2026-11-23): a flat frontal shield armor level while the
    /// Archmage is casting. The pure decision and sums are pinned directly; the CONSUMER (Creature.GetShieldMod
    /// feeding the term into both shield paths, Player.GetManaBarrierShieldLevel gathering the inputs) is pinned
    /// by source scan, because Player's static initializer cannot run under the unit test host.
    /// </summary>
    [TestClass]
    public class ManaBarrierCastingShieldTests
    {
        private const string Key = "class_ability_manabarrier_shield_level";

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find property-registry.tsv by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(SourceRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

        private static string Method(string text, string signature)
        {
            var i = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, "signature not found: " + signature);
            var end = text.IndexOf("\n        }\n", i, StringComparison.Ordinal);
            Assert.IsTrue(end > i, "end of method not found: " + signature);
            return text.Substring(i, end - i);
        }

        [TestMethod]
        public void LevelFor_LearnedCastingPvE_IsTheTunable()
        {
            Assert.AreEqual(100.0, ManaBarrierShieldMath.LevelFor(true, 1, true, true, 100), 1e-12);
            Assert.AreEqual(150.0, ManaBarrierShieldMath.LevelFor(true, 2, true, true, 150), 1e-12);
        }

        [TestMethod]
        public void LevelFor_NotCasting_IsZero()
            => Assert.AreEqual(0.0, ManaBarrierShieldMath.LevelFor(true, 3, false, true, 100), 1e-12);

        [TestMethod]
        public void LevelFor_NotLearned_IsZero()
            => Assert.AreEqual(0.0, ManaBarrierShieldMath.LevelFor(true, 0, true, true, 100), 1e-12);

        [TestMethod]
        public void LevelFor_PvpOrSelfAttacker_IsZero()
            => Assert.AreEqual(0.0, ManaBarrierShieldMath.LevelFor(true, 3, true, false, 100), 1e-12, "a player (PvP) or self is not an enemy creature");

        [TestMethod]
        public void LevelFor_ClassAbilitiesDisabled_IsZero()
            => Assert.AreEqual(0.0, ManaBarrierShieldMath.LevelFor(false, 3, true, true, 100), 1e-12);

        [TestMethod]
        public void LevelFor_IsFlatAcrossRanks()
        {
            var r1 = ManaBarrierShieldMath.LevelFor(true, 1, true, true, 100);
            var r2 = ManaBarrierShieldMath.LevelFor(true, 2, true, true, 100);
            var r3 = ManaBarrierShieldMath.LevelFor(true, 3, true, true, 100);

            Assert.AreEqual(100.0, r1, 1e-12);
            Assert.AreEqual(r1, r2, 1e-12);
            Assert.AreEqual(r1, r3, 1e-12);
        }

        [TestMethod]
        public void LevelFor_NegativeTunable_IsZero()
            => Assert.AreEqual(0.0, ManaBarrierShieldMath.LevelFor(true, 1, true, true, -50), 1e-12);

        [TestMethod]
        public void VirtualShieldLevel_SumsAllThreeTerms_AndIgnoresNegativeCasting()
        {
            Assert.AreEqual(100.0, ManaBarrierShieldMath.VirtualShieldLevel(0, 0, 100), 1e-12, "casting shield alone is a virtual shield");
            Assert.AreEqual(250.0, ManaBarrierShieldMath.VirtualShieldLevel(50, 100, 100), 1e-12);
            Assert.AreEqual(150.0, ManaBarrierShieldMath.VirtualShieldLevel(50, 100, -40), 1e-12);
        }

        [TestMethod]
        public void AfterShieldCap_IncludesTheCastingTerm_AfterTheCap()
        {
            // capped shield 80 + defensive weapon 20 + ward 100 + casting 100
            Assert.AreEqual(300.0, DefensiveWeaponMath.AfterShieldCap(80, 20, 100, 100), 1e-12);
            Assert.AreEqual(200.0, DefensiveWeaponMath.AfterShieldCap(80, 20, 100), 1e-12, "omitted casting term defaults to 0");
            Assert.AreEqual(200.0, DefensiveWeaponMath.AfterShieldCap(80, 20, 100, -5), 1e-12);
        }

        [TestMethod]
        public void TunableDefault_Is100()
        {
            // LoadDefaultProperties ran in ClassInitialize; nothing here modifies the key first
            Assert.AreEqual(100L, PropertyManager.GetLong(Key).Item);
        }

        // ---- consumer pins (source scan) -----------------------------------------------------------

        [TestMethod]
        public void GetShieldMod_FeedsTheTermIntoBothShieldPaths()
        {
            var text = Read("ACE.Server", "WorldObjects", "Creature_Combat.cs");
            var body = Method(text, "public float GetShieldMod(");

            // no-shield path: the virtual level is the three-term sum
            Assert.IsTrue(Regex.IsMatch(body, @"barrierPlayer\.GetManaBarrierShieldLevel\(attacker\)"), "no-shield path must read the casting level");
            Assert.IsTrue(Regex.IsMatch(body, @"var virtualShieldLevel = ManaBarrierShieldMath\.VirtualShieldLevel\(defensiveWeapon, wardShieldLevel, manaBarrierShieldLevel\);"),
                "no-shield path must sum the casting level into the virtual shield");

            // equipped-shield path: added after the cap through AfterShieldCap
            Assert.IsTrue(Regex.IsMatch(body, @"DefensiveWeaponMath\.AfterShieldCap\([^;]*wardShieldPlayer\.GetManaBarrierShieldLevel\(attacker\)\)"),
                "equipped-shield path must pass the casting level to AfterShieldCap");
        }

        [TestMethod]
        public void SpellAbsorbPath_DoesNotReadTheCastingShield()
        {
            var spell = Read("ACE.Server", "WorldObjects", "SpellProjectile.cs");
            var m = Regex.Match(spell, @"GetAbsorbMod\(");
            Assert.IsTrue(m.Success);

            Assert.IsFalse(spell.Contains("GetManaBarrierShieldLevel"), "physical only: the spell path must not read the casting shield");
            Assert.IsFalse(spell.Contains("GetShieldMod("), "GetAbsorbMod must not route through GetShieldMod");
        }

        [TestMethod]
        public void GetManaBarrierShieldLevel_GathersTheGates()
        {
            var text = Read("ACE.Server", "WorldObjects", "Player_ClassAbilityBuffs.cs");
            var body = Method(text, "public double GetManaBarrierShieldLevel(");

            StringAssert.Contains(body, "MagicState?.IsCasting");
            StringAssert.Contains(body, "class_abilities_enabled");
            StringAssert.Contains(body, "TryGetClassAbility(ClassAbilityId.ManaBarrier");
            StringAssert.Contains(body, "ClassAbilityAttackerFilter.EnemyCreature(attacker, this)");
            // the LevelFor call's actual arguments, in order: enabled, rank, casting, enemy, tunable
            Assert.IsTrue(Regex.IsMatch(body,
                @"ManaBarrierShieldMath\.LevelFor\(enabled, rank, MagicState\.IsCasting, enemyCreature,\s*PropertyManager\.GetLong\(""class_ability_manabarrier_shield_level""\)\.Item\)"),
                "LevelFor must receive (enabled, rank, MagicState.IsCasting, enemyCreature, tunable)");

            // rank is fed from TryGetClassAbility, enabled from the class_abilities_enabled bool, enemy from the EnemyCreature filter
            Assert.IsTrue(Regex.IsMatch(body, @"TryGetClassAbility\(ClassAbilityId\.ManaBarrier, out rank\)"), "rank must come from TryGetClassAbility");
            Assert.IsTrue(Regex.IsMatch(body, @"var enabled = PropertyManager\.GetBool\(""class_abilities_enabled""\)\.Item;"), "enabled must come from class_abilities_enabled");
            Assert.IsTrue(Regex.IsMatch(body, @"var enemyCreature = ClassAbilityAttackerFilter\.EnemyCreature\(attacker, this\) != null;"), "enemy must come from the EnemyCreature filter");
            StringAssert.Contains(body, Key);
            Assert.IsFalse(body.Contains("EquipmentModId"), "the gear mod must not add to the casting shield");
        }
    }
}
