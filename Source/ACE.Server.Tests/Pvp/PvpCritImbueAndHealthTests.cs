using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP crit imbue levers (pvp_cs_crit_mod at CS1/CS2, pvp_cb_crit_mod at CB1) and effective-HP normalization
    /// (pvp_health_floor / pvp_health_ceiling at M1/M2 inside ApplyDamageMods, and N3/N4 before C3/C4), per
    /// Docs/Pvp/DESIGN.md "PvP rules (levers)".
    ///
    /// Exercised through PvpRules' pure functions and choke-point entries on reflection-seeded Players. The weapon
    /// crit functions need a wielded, imbued weapon and a CreatureSkill, which this test host cannot build, and Harm /
    /// Drain need a dat Spell - so which call site passes which point, and that each sits before its Math.Max or cap,
    /// is a SOURCE-ORDER PLACEMENT CHECK (matched on CODE only, comments stripped), not a live computation.
    ///
    /// SEEDING: no PropertyManager key is read - the dials come from a swapped PvpRuleTunables.DialSource and the
    /// defender's max health from a swapped PvpRules.MaxHealthSource. Every seam (DialSource, MaxHealthSource,
    /// Observer, PvpClassifier.ArenaScopeSource) is saved and restored per test.
    /// </summary>
    [TestClass]
    public class PvpCritImbueAndHealthTests
    {
        private Func<PvpRuleDials> savedDialSource;
        private Func<Creature, double> savedMaxHealth;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<Player, Player, bool> savedArenaScope;

        private List<(PvpChokePoint Point, double Before, double After)> observed;
        private int dialReads;
        private int maxHealthReads;
        private double defenderMaxHealth;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedMaxHealth = PvpRules.MaxHealthSource;
            savedObserver = PvpRules.Observer;
            savedArenaScope = PvpClassifier.ArenaScopeSource;

            observed = new List<(PvpChokePoint, double, double)>();
            dialReads = 0;
            maxHealthReads = 0;
            defenderMaxHealth = 1000;

            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            PvpRules.MaxHealthSource = c => { maxHealthReads++; return defenderMaxHealth; };
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
            Use(PvpRuleTunables.Defaults);
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.MaxHealthSource = savedMaxHealth;
            PvpRules.Observer = savedObserver;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        // ================= fixtures =================

        private void Use(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => { dialReads++; return dials; };

        private static PvpRuleDials With(double cs = 1.0, double cb = 1.0, long floor = 0, long ceiling = 0, bool enabled = true) =>
            PvpRuleTunables.Defaults with
            {
                Enabled = enabled,
                DamageCapMaxHealthFraction = 0,   // keep the cap out of the way of the normalization numbers
                CsCritMod = cs,
                CbCritMod = cb,
                HealthFloor = floor,
                HealthCeiling = ceiling,
            };

        private static Player SeededPlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            player.PlayerKillerStatus = PlayerKillerStatus.PK;

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        // ================= pure =================

        [TestMethod]
        public void ScaleCriticalStrike_Pure()
        {
            Assert.AreEqual(0.3, PvpRules.ScaleCriticalStrike(0.1, 0.3, 1.0), 1e-12, "identity");
            Assert.AreEqual(0.1, PvpRules.ScaleCriticalStrike(0.1, 0.3, 0.0), 1e-12, "0: the imbue adds nothing above base");
            Assert.AreEqual(0.2, PvpRules.ScaleCriticalStrike(0.1, 0.3, 0.5), 1e-12, "half the bonus above base");
            Assert.AreEqual(0.5, PvpRules.ScaleCriticalStrike(0.1, 0.3, 2.0), 1e-12, "twice the bonus above base");

            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
                Assert.AreEqual(0.3, PvpRules.ScaleCriticalStrike(0.1, 0.3, bad), 1e-12, $"{bad} is the identity");
        }

        [TestMethod]
        public void ScaleCripplingBlow_Pure()
        {
            Assert.AreEqual(4.0, PvpRules.ScaleCripplingBlow(4.0, 1.0), 1e-12, "identity");
            Assert.AreEqual(1.0, PvpRules.ScaleCripplingBlow(4.0, 0.0), 1e-12, "0: the imbue adds nothing above 1.0");
            Assert.AreEqual(2.5, PvpRules.ScaleCripplingBlow(4.0, 0.5), 1e-12, "half the bonus above 1.0");
            Assert.AreEqual(7.0, PvpRules.ScaleCripplingBlow(4.0, 2.0), 1e-12);

            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1.0 })
                Assert.AreEqual(4.0, PvpRules.ScaleCripplingBlow(4.0, bad), 1e-12, $"{bad} is the identity");
        }

        [TestMethod]
        public void HealthNormalizationFactor_Pure()
        {
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(500, 0, 0), "both bounds off");
            Assert.AreEqual(0.5, PvpRules.HealthNormalizationFactor(500, 1000, 0), 1e-12, "low H under a floor takes proportionally less");
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(1500, 1000, 0), 1e-12, "above the floor with no ceiling: unchanged");
            Assert.AreEqual(2.0, PvpRules.HealthNormalizationFactor(2000, 0, 1000), 1e-12, "high H over a ceiling takes proportionally more");
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(800, 0, 1000), 1e-12, "under the ceiling with no floor: unchanged");
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(700, 500, 1000), 1e-12, "inside [floor, ceiling]: unchanged");
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(100, 2000, 1000), 1e-12, "floor > ceiling is invalid: the identity");
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(0, 1000, 0), 1e-12, "no known max health: the identity");
            Assert.AreEqual(1.0, PvpRules.HealthNormalizationFactor(100, -5, -5), 1e-12, "negative bounds are off");

            Assert.IsTrue(PvpRules.HealthBoundsInvalid(2000, 1000));
            Assert.IsFalse(PvpRules.HealthBoundsInvalid(2000, 0), "a single bound is never invalid");
            Assert.IsFalse(PvpRules.HealthBoundsInvalid(1000, 1000));
        }

        // ================= crit imbue entries =================

        [TestMethod]
        public void CriticalStrike_PvpPair_IsScaled_AndReported()
        {
            Use(With(cs: 0.0));

            Assert.AreEqual(0.1f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, SeededPlayer(), SeededPlayer(), 0.1f, 0.3f), 1e-6f, "m = 0: base rate");
            Assert.AreEqual(0.05f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS2, SeededPlayer(), SeededPlayer(), 0.05f, 0.25f), 1e-6f);

            Assert.AreEqual(2, observed.Count);
            Assert.AreEqual(PvpChokePoint.CS1, observed[0].Point);
            Assert.AreEqual(PvpChokePoint.CS2, observed[1].Point);
        }

        [TestMethod]
        public void CripplingBlow_PvpPair_IsScaled_AndReported()
        {
            Use(With(cb: 0.5));

            Assert.AreEqual(2.5f, PvpRules.ApplyCripplingBlowMod(SeededPlayer(), SeededPlayer(), 4.0f), 1e-6f);
            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.CB1, observed[0].Point);
        }

        [TestMethod]
        public void CritImbues_NullTargetOrSelf_AreUnchanged_AndReadNothing()
        {
            Use(With(cs: 0.0, cb: 0.0));

            var player = SeededPlayer();

            Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, player, null, 0.1f, 0.3f), "a report command's null target");
            Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS2, null, null, 0.1f, 0.3f));
            Assert.AreEqual(4.0f, PvpRules.ApplyCripplingBlowMod(player, null, 4.0f));
            Assert.AreEqual(4.0f, PvpRules.ApplyCripplingBlowMod(player, player, 4.0f), "self is not PvP");

            Assert.AreEqual(0, dialReads);
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void CritImbues_DefaultsAndBadValuesAndMasterSwitch_AreTheIdentity()
        {
            Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, SeededPlayer(), SeededPlayer(), 0.1f, 0.3f));
            Assert.AreEqual(4.0f, PvpRules.ApplyCripplingBlowMod(SeededPlayer(), SeededPlayer(), 4.0f));

            Use(With(cs: double.NaN, cb: -2.0));
            Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, SeededPlayer(), SeededPlayer(), 0.1f, 0.3f));
            Assert.AreEqual(4.0f, PvpRules.ApplyCripplingBlowMod(SeededPlayer(), SeededPlayer(), 4.0f));

            Use(With(cs: 0.0, cb: 0.0, enabled: false));
            Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, SeededPlayer(), SeededPlayer(), 0.1f, 0.3f));
            Assert.AreEqual(4.0f, PvpRules.ApplyCripplingBlowMod(SeededPlayer(), SeededPlayer(), 4.0f));

            Assert.AreEqual(0, observed.Count);
        }

        // ================= health normalization =================

        /// <summary>At the defaults (both bounds 0) the dials are read once per PvP hit (the gate) and NOTHING else - not the defender's max health.</summary>
        [TestMethod]
        public void HealthNormalization_Defaults_ReadNothingBeyondTheGate()
        {
            Assert.AreEqual(800.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, SeededPlayer(), SeededPlayer(), 800.0f));
            Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, SeededPlayer(), SeededPlayer(), 800.0f, PvpDamageKind.Melee, false, default));

            Assert.AreEqual(2, dialReads, "one dial read per PvP hit - the gate");
            Assert.AreEqual(0, maxHealthReads, "no bound set: the defender's max health is never read");
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void HealthFloor_LowHealthDefender_TakesProportionallyLess_AtEverySite()
        {
            Use(With(floor: 1000));
            defenderMaxHealth = 500;

            Assert.AreEqual(400.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, SeededPlayer(), SeededPlayer(), 800.0f), 1e-4f, "N3 Harm");
            Assert.AreEqual(400u, PvpRules.ApplyHealthNormalization(PvpChokePoint.N4, SeededPlayer(), SeededPlayer(), 800u), "N4 Drain");
            Assert.AreEqual(400.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, SeededPlayer(), SeededPlayer(), 800.0f, PvpDamageKind.Melee, false, default), 1e-4f, "M1 (C1)");
            Assert.AreEqual(400.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, SeededPlayer(), SeededPlayer(), 800.0f, PvpDamageKind.Other, false, default), 1e-4f, "M2 (C2), void/life too");

            CollectionAssert.AreEqual(new[] { PvpChokePoint.N3, PvpChokePoint.N4, PvpChokePoint.M1, PvpChokePoint.M2 }, observed.Select(o => o.Point).ToArray());
        }

        [TestMethod]
        public void HealthCeiling_HighHealthDefender_TakesProportionallyMore()
        {
            Use(With(ceiling: 1000));
            defenderMaxHealth = 2000;

            Assert.AreEqual(1600.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, SeededPlayer(), SeededPlayer(), 800.0f), 1e-3f);
            Assert.AreEqual(1600.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, SeededPlayer(), SeededPlayer(), 800.0f, PvpDamageKind.Missile, false, default), 1e-3f);
        }

        [TestMethod]
        public void HealthNormalization_ComposesWithTheKindMod()
        {
            Use(With(ceiling: 1000) with { MeleeDamageMod = 0.5 });
            defenderMaxHealth = 2000;

            Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, SeededPlayer(), SeededPlayer(), 800.0f, PvpDamageKind.Melee, false, default), 1e-3f, "0.5 x 2.0");
        }

        [TestMethod]
        public void HealthNormalization_InvalidPair_IsTheIdentity_AndReadsNoMaxHealth()
        {
            Use(With(floor: 2000, ceiling: 1000));
            defenderMaxHealth = 100;

            for (var i = 0; i < 3; i++)
                Assert.AreEqual(800.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, SeededPlayer(), SeededPlayer(), 800.0f));

            Assert.AreEqual(0, maxHealthReads, "an invalid pair never reads max health");
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void HealthNormalization_NonPvp_ReadsNothing_AndMasterSwitchOff_IsUnchanged()
        {
            Use(With(floor: 1000));
            defenderMaxHealth = 500;

            var player = SeededPlayer();

            Assert.AreEqual(800.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, player, player, 800.0f), "self-Harm is not PvP");
            Assert.AreEqual(800u, PvpRules.ApplyHealthNormalization(PvpChokePoint.N4, null, player, 800u));
            Assert.AreEqual(0, dialReads);
            Assert.AreEqual(0, maxHealthReads);

            Use(With(floor: 1000, enabled: false));
            Assert.AreEqual(800.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, SeededPlayer(), SeededPlayer(), 800.0f));
            Assert.AreEqual(0, maxHealthReads);
        }

        // ================= call-site pins (source order; placement checks, not live computations) =================

        [TestMethod]
        public void CritImbueSites_ArePinned_BeforeTheirMathMax()
        {
            const string file = "ACE.Server/WorldObjects/WorldObject_Weapon.cs";
            const string cs1 = "criticalStrikeBonus = PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, wielder, target, critRate, criticalStrikeBonus);";
            const string cs2 = "criticalStrikeMod = PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS2, wielder, target, critRate, criticalStrikeMod);";
            const string cb1 = "cripplingBlowMod = PvpRules.ApplyCripplingBlowMod(wielder, target, cripplingBlowMod);";

            AssertOrder(file, "var criticalStrikeBonus = GetCriticalStrikeMod(skill);", cs1);
            AssertOrder(file, cs1, "critRate = Math.Max(critRate, criticalStrikeBonus);");

            // CS2 after the call that applies the retail PvP halving, before the Math.Max
            AssertOrder(file, "var criticalStrikeMod = GetCriticalStrikeMod(skill, isPvP);", cs2);
            AssertOrder(file, cs2, "critRate = Math.Max(critRate, criticalStrikeMod);");

            AssertOrder(file, "var cripplingBlowMod = GetCripplingBlowMod(skill);", cb1);
            AssertOrder(file, cb1, "critDamageMod = Math.Max(critDamageMod, cripplingBlowMod);");

            // Crushing Blow (the tinkered CriticalMultiplier) is never routed through the CB lever
            var lines = ReadCode(file);
            Assert.AreEqual(0, lines.Count(l => l.Contains("CriticalMultiplier") && l.Contains("ApplyCripplingBlowMod")));
            Assert.AreEqual(1, lines.Count(l => l.Contains("ApplyCripplingBlowMod(")), "exactly one CB1 site");
            Assert.AreEqual(2, lines.Count(l => l.Contains("ApplyCriticalStrikeMod(")), "exactly two CS sites");
        }

        private static string CodeOnly(string raw)
        {
            var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
            return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
        }

        private static string[] ReadCode(string file)
        {
            var path = Path.Combine(FindSourceRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");
            return File.ReadAllLines(path).Select(CodeOnly).ToArray();
        }

        private static void AssertOrder(string file, string first, string second)
        {
            var lines = ReadCode(file);

            var a = Enumerable.Range(0, lines.Length).Where(i => lines[i] == first).ToList();
            var b = Enumerable.Range(0, lines.Length).Where(i => lines[i] == second).ToList();

            Assert.AreEqual(1, a.Count, $"{file}: expected exactly one code line `{first}`");
            Assert.AreEqual(1, b.Count, $"{file}: expected exactly one code line `{second}`");
            Assert.IsTrue(a[0] < b[0], $"{file}: `{first}` (line {a[0] + 1}) must come before `{second}` (line {b[0] + 1})");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
