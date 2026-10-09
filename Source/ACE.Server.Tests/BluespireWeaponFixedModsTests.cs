using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pin for the FIXED weapon mods on the eight Bluespire dungeon-ladder vendor weapons
    /// (wcids 1005450-1005457, owner ruling 2026-09-25, RoZ round 19):
    ///
    ///   all eight                    Second Wind
    ///   melee 1005454-1005457        + Heft
    ///   missile 1005450              + Tension
    ///   casters 1005451-1005453      Second Wind only
    ///
    /// each at a perfect roll - the value a potency 1.0 roll produces at workmanship 10, which the appraisal
    /// panel reads as [100%]. All three are Tier B rows, so the authored record is the roll FRACTION 1.0 and no
    /// native property is written.
    ///
    /// NOTHING HERE IS HAND-TYPED FROM THE SQL. Every weapon is built from a parse of its own
    /// Content/sql/weenies unit (the int, float, bool and string tables, plus the weenie type), so a wrong or
    /// missing authored row fails these tests rather than being mirrored by a second copy of the same value.
    /// The expectations below are the OWNER RULING (which mods) and the REGISTRY (what a perfect roll is
    /// worth), never the SQL.
    /// </summary>
    [TestClass]
    public class BluespireWeaponFixedModsTests
    {
        private static uint nextGuid = 0x7E3A0000;   // static guid range, clear of GuidManager

        /// <summary>The owner ruling: which mods each weapon carries. Order is irrelevant.</summary>
        private static readonly Dictionary<int, WeaponModId[]> Ruling = new Dictionary<int, WeaponModId[]>
        {
            { 1005450, new[] { WeaponModId.SecondWind, WeaponModId.Tension } }, // Tourmaline-Bound Elari Bow
            { 1005451, new[] { WeaponModId.SecondWind } },                      // Virindi Implant (Red)
            { 1005452, new[] { WeaponModId.SecondWind } },                      // Virindi Implant (Purple)
            { 1005453, new[] { WeaponModId.SecondWind } },                      // The Tourmaline-Bound Awakener
            { 1005454, new[] { WeaponModId.SecondWind, WeaponModId.Heft } },    // Waaika
            { 1005455, new[] { WeaponModId.SecondWind, WeaponModId.Heft } },    // Tewhate
            { 1005456, new[] { WeaponModId.SecondWind, WeaponModId.Heft } },    // Hoeroa
            { 1005457, new[] { WeaponModId.SecondWind, WeaponModId.Heft } },    // Taiaha
        };

        /// <summary>The weapon class each wcid must classify as, so eligibility is checked against the real item.</summary>
        private static readonly Dictionary<int, WeaponClass> ExpectedClass = new Dictionary<int, WeaponClass>
        {
            { 1005450, WeaponClass.Missile },
            { 1005451, WeaponClass.Caster },
            { 1005452, WeaponClass.Caster },
            { 1005453, WeaponClass.Caster },
            { 1005454, WeaponClass.Melee },
            { 1005455, WeaponClass.Melee },
            { 1005456, WeaponClass.Melee },
            { 1005457, WeaponClass.Melee },
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds every server tunable this class reads (weapon_mods_enabled, weapon_mod_magnitude_scale, the
            // special-count chances the reroll draws against) from hardcoded defaults - no database
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Runs a body with the weapon mod gate ON and the magnitude scale at 1.0 (its shipped default), restoring
        /// both whatever happens, so this class neither depends on nor leaks tunable state.
        /// </summary>
        private static void WithModsLive(Action body)
        {
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
            }
        }

        // ---------------- the authored set ----------------

        /// <summary>
        /// Each weapon carries exactly the ruled mods - no more, no fewer - every one stored as the Tier B
        /// fraction 1.0, and every one eligible for the weapon's actual class (so no authored mod is one the roll
        /// path could never have produced on that item).
        /// </summary>
        [TestMethod]
        public void EachWeapon_CarriesExactlyTheRuledModsAtFractionOne()
        {
            var problems = new List<string>();

            foreach (var wcid in Ruling.Keys)
            {
                var weapon = BuildFromSql(wcid, out var name);
                var weaponClass = WeaponClassifier.Classify(weapon);

                if (weaponClass != ExpectedClass[wcid])
                    problems.Add($"{wcid} ({name}): classifies as {weaponClass}, expected {ExpectedClass[wcid]}.");

                var held = WeaponModRegistry.AllMods.Where(d => weapon.GetProperty(d.Record) != null).Select(d => d.Id).OrderBy(i => i).ToList();
                var ruled = Ruling[wcid].OrderBy(i => i).ToList();

                if (!held.SequenceEqual(ruled))
                    problems.Add($"{wcid} ({name}): carries [{string.Join(", ", held)}], ruling is [{string.Join(", ", ruled)}].");

                foreach (var id in Ruling[wcid])
                {
                    var definition = WeaponModRegistry.Get(id);

                    if (definition.Tier != WeaponModTier.B)
                        problems.Add($"{wcid} ({name}): {id} is Tier {definition.Tier}; this test assumes Tier B fraction storage.");

                    if (!definition.AppliesTo(weaponClass))
                        problems.Add($"{wcid} ({name}): {id} is not eligible for class {weaponClass}.");

                    var record = weapon.GetProperty(definition.Record);

                    if (record == null || Math.Abs(record.Value - 1.0) > 1e-12)
                        problems.Add($"{wcid} ({name}): {id} record (PropertyFloat {(int)definition.Record}) is {record?.ToString(CultureInfo.InvariantCulture) ?? "absent"}, expected the perfect-roll fraction 1.0.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The appraisal panel's lines, exactly: each ruled mod at [100%] and its perfect-roll magnitude, and no
        /// tinker line (these weapons are not managed by the tinker half of the system).
        /// </summary>
        [TestMethod]
        public void Appraisal_ShowsEachModAtOneHundredPercent()
        {
            WithModsLive(() =>
            {
                var problems = new List<string>();

                foreach (var wcid in Ruling.Keys)
                {
                    var weapon = BuildFromSql(wcid, out var name);
                    var lines = WeaponModDisplay.GetAppraisalLines(weapon);

                    var expected = WeaponModRegistry.AllMods
                        .Where(d => Ruling[wcid].Contains(d.Id))
                        .Select(d => $"- {d.DisplayName} [100%]: {d.Format(WeaponModValue.MaxMagnitude(d, 1.0))}")
                        .ToList();

                    if (!lines.SequenceEqual(expected))
                        problems.Add($"{wcid} ({name}): appraisal lines [{string.Join(" | ", lines)}], expected [{string.Join(" | ", expected)}].");

                    foreach (var id in Ruling[wcid])
                    {
                        var definition = WeaponModRegistry.Get(id);
                        var magnitude = WeaponModTinkerSet.ReadMagnitude(weapon, definition, 1.0);
                        var percent = WeaponModDisplay.IntensityPercent(definition, magnitude, 1.0);

                        if (percent != 100)
                            problems.Add($"{wcid} ({name}): {id} reads [{percent}%], expected [100%].");

                        if (!WeaponModDisplay.Describe(definition, magnitude, 1.0).Contains("[100%]"))
                            problems.Add($"{wcid} ({name}): Describe for {id} does not show [100%].");
                    }

                    // two mods at 100 each is 200 - below the 300 Exceptional threshold, so no quality aura
                    if (WeaponQualityTiers.Evaluate(weapon) != WeaponQualityTier.None)
                        problems.Add($"{wcid} ({name}): earns quality tier {WeaponQualityTiers.Evaluate(weapon)}, expected None.");
                }

                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            });
        }

        // ---------------- the combat read path ----------------

        /// <summary>
        /// The combat path returns the perfect-roll magnitudes: Second Wind 0.12 through ReadWeaponOnly (the
        /// accessor Player.GetKillingWeaponModValue resolves through), Heft +22 into DamageBonus and Tension +0.5
        /// into DamageMod through ApplyBaseDamageMods, the same call DamageEvent.GetBaseDamage makes. The
        /// BaseDamageMod is seeded from the weapon's own authored Damage/DamageVariance/DamageMod rows exactly as
        /// its (BaseDamage, Creature, WorldObject) constructor would, minus enchantments.
        /// </summary>
        [TestMethod]
        public void CombatReadPath_ReturnsPerfectRollMagnitudes()
        {
            WithModsLive(() =>
            {
                var problems = new List<string>();

                foreach (var wcid in Ruling.Keys)
                {
                    var weapon = BuildFromSql(wcid, out var name);
                    var ruled = Ruling[wcid];

                    var secondWind = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.SecondWind);

                    if (Math.Abs(secondWind - 0.12) > 1e-9)
                        problems.Add($"{wcid} ({name}): Second Wind reads {secondWind}, expected 0.12.");

                    var authoredDamageMod = (float)(weapon.GetProperty(PropertyFloat.DamageMod) ?? 1.0);
                    var mod = new BaseDamageMod(new BaseDamage(weapon.GetProperty(PropertyInt.Damage) ?? 0, (float)(weapon.GetProperty(PropertyFloat.DamageVariance) ?? 0.0)))
                    {
                        DamageMod = authoredDamageMod,
                    };

                    WeaponModCombat.ApplyBaseDamageMods(mod, weapon);

                    var expectedBonus = ruled.Contains(WeaponModId.Heft) ? 22.0f : 0.0f;
                    var expectedDamageMod = authoredDamageMod + (ruled.Contains(WeaponModId.Tension) ? 0.5f : 0.0f);

                    if (Math.Abs(mod.DamageBonus - expectedBonus) > 1e-5)
                        problems.Add($"{wcid} ({name}): DamageBonus after ApplyBaseDamageMods is {mod.DamageBonus}, expected {expectedBonus}.");

                    if (Math.Abs(mod.DamageMod - expectedDamageMod) > 1e-5)
                        problems.Add($"{wcid} ({name}): DamageMod after ApplyBaseDamageMods is {mod.DamageMod}, expected {expectedDamageMod}.");
                }

                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            });
        }

        /// <summary>
        /// Control for the combat test: with the master gate OFF the same weapons read nothing, so the positive
        /// numbers above come from the gate-on path and not from some other source.
        /// </summary>
        [TestMethod]
        public void CombatReadPath_IsInertWithTheGateOff()
        {
            var prior = PropertyManager.GetBool("weapon_mods_enabled").Item;
            PropertyManager.ModifyBool("weapon_mods_enabled", false);

            try
            {
                foreach (var wcid in Ruling.Keys)
                {
                    var weapon = BuildFromSql(wcid, out var name);

                    Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.SecondWind), $"{wcid} ({name})");
                    Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Heft), $"{wcid} ({name})");
                    Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Tension), $"{wcid} ({name})");
                }
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", prior);
            }
        }

        // ---------------- reroll behaviour ----------------

        /// <summary>
        /// A Tourmaline or Amethyst is actually usable on these weapons (no refusal), and clearing the authored
        /// set - step 1 of every Tourmaline reroll - removes every mod record and leaves every other property on
        /// the item exactly as authored.
        /// </summary>
        [TestMethod]
        public void TourmalineClear_RemovesTheAuthoredModsAndLeavesEveryNativeAsAuthored()
        {
            WithModsLive(() =>
            {
                foreach (var wcid in Ruling.Keys)
                {
                    var weapon = BuildFromSql(wcid, out var name);
                    var authored = SnapshotNonModProperties(weapon);

                    Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, weapon), $"{wcid} ({name}): Tourmaline refused");
                    Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, weapon), $"{wcid} ({name}): Amethyst refused");

                    WeaponModTinkerSet.ClearSpecials(weapon);

                    Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon), $"{wcid} ({name}): a mod record survived the clear");
                    AssertSameProperties(authored, SnapshotNonModProperties(weapon), $"{wcid} ({name}) after ClearSpecials");
                }
            });
        }

        /// <summary>
        /// The full Tourmaline reroll (and an Amethyst swap), repeated: whatever fresh set each draws - including
        /// Tier A rows that DO write a native - clearing it afterwards lands every native back on the authored
        /// value. That is the "no corruption" guarantee: the authored mods leave nothing behind for the reversal
        /// arithmetic to get wrong.
        /// </summary>
        [TestMethod]
        public void TourmalineReroll_ThenClear_ReturnsEveryNativeToAuthored()
        {
            WithModsLive(() =>
            {
                foreach (var wcid in Ruling.Keys)
                {
                    for (var i = 0; i < 25; i++)
                    {
                        var weapon = BuildFromSql(wcid, out var name);
                        var authored = SnapshotNonModProperties(weapon);
                        var weaponClass = WeaponClassifier.Classify(weapon);
                        var workmanship = weapon.Workmanship ?? 0.0f;

                        Assert.AreEqual(10.0f, workmanship, $"{wcid} ({name}): workmanship");

                        var lines = i % 2 == 0
                            ? WeaponModManager.ApplyReroll(weapon, weaponClass, workmanship)
                            : WeaponModManager.ApplySwap(weapon, weaponClass, workmanship);

                        Assert.IsNotNull(lines, $"{wcid} ({name}): reroll/swap bailed out");
                        Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) <= WeaponModRegistry.MaxSpecials, $"{wcid} ({name}): special count above the cap");

                        WeaponModTinkerSet.ClearSpecials(weapon);

                        AssertSameProperties(authored, SnapshotNonModProperties(weapon), $"{wcid} ({name}) iteration {i}");
                    }
                }
            });
        }

        // ---------------- building a weapon from its SQL ----------------

        private static WorldObject BuildFromSql(int wcid, out string name)
        {
            var file = FindWeenieFile(wcid);
            Assert.IsNotNull(file, $"{wcid}: no weenie SQL found");

            var text = File.ReadAllText(file);

            var typeMatch = Regex.Match(text, @"INSERT\s+INTO\s+`weenie`\s*\([^)]*\)\s*VALUES\s*\(\s*(\d+)\s*,\s*'[^']*'\s*,\s*(\d+)\s*,", RegexOptions.IgnoreCase);
            Assert.IsTrue(typeMatch.Success, $"{wcid}: could not parse the weenie row");
            Assert.AreEqual(wcid, int.Parse(typeMatch.Groups[1].Value), $"{wcid}: weenie row names another id");

            var weenie = new Weenie
            {
                WeenieClassId = (uint)wcid,
                WeenieType = (WeenieType)int.Parse(typeMatch.Groups[2].Value),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesString = new Dictionary<PropertyString, string>(),
            };

            foreach (var (type, value) in TableRows(text, "weenie_properties_int", wcid))
                weenie.PropertiesInt[(PropertyInt)type] = int.Parse(value, CultureInfo.InvariantCulture);

            foreach (var (type, value) in TableRows(text, "weenie_properties_float", wcid))
                weenie.PropertiesFloat[(PropertyFloat)type] = double.Parse(value, CultureInfo.InvariantCulture);

            foreach (var (type, value) in TableRows(text, "weenie_properties_bool", wcid))
                weenie.PropertiesBool[(PropertyBool)type] = bool.Parse(value);

            foreach (var (type, value) in TableRows(text, "weenie_properties_string", wcid))
                weenie.PropertiesString[(PropertyString)type] = value.Substring(1, value.Length - 2).Replace("''", "'");

            Assert.IsTrue(weenie.PropertiesInt.Count > 0 && weenie.PropertiesFloat.Count > 0, $"{wcid}: parsed no int/float rows");

            name = weenie.PropertiesString.TryGetValue(PropertyString.Name, out var n) ? n : Path.GetFileName(file);

            var weapon = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            Assert.IsNotNull(weapon, $"{wcid}: WorldObjectFactory returned null for WeenieType {weenie.WeenieType}");

            return weapon;
        }

        /// <summary>
        /// (type, raw value) for every row of one property table for one object id. Comments are stripped first,
        /// and string literals are matched whole so a comma or parenthesis inside a LongDesc cannot split a row.
        /// </summary>
        private static IEnumerable<(int Type, string Value)> TableRows(string text, string table, int wcid)
        {
            var blocks = Regex.Matches(text, @"INSERT\s+INTO\s+`" + table + @"`\s*\([^)]*\)\s*VALUES(.*?);\s*(\r?\n|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var body = Regex.Replace(block.Groups[1].Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*('(?:[^']|'')*'|[^)\s]+)\s*\)"))
                {
                    if (int.Parse(row.Groups[1].Value) != wcid)
                        continue;

                    yield return (int.Parse(row.Groups[2].Value), row.Groups[3].Value);
                }
            }
        }

        private static string FindWeenieFile(int wcid)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    return Directory.EnumerateFiles(weenies, wcid + " *.sql", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .FirstOrDefault();
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        // ---------------- property snapshots ----------------

        private static readonly HashSet<PropertyFloat> ModRecords = new HashSet<PropertyFloat>(WeaponModRegistry.AllMods.Select(d => d.Record));

        private static Dictionary<string, string> SnapshotNonModProperties(WorldObject weapon)
        {
            var snapshot = new Dictionary<string, string>();
            var biota = weapon.Biota;

            if (biota.PropertiesInt != null)
                foreach (var kvp in biota.PropertiesInt)
                    snapshot[$"int {kvp.Key}"] = kvp.Value.ToString(CultureInfo.InvariantCulture);

            if (biota.PropertiesInt64 != null)
                foreach (var kvp in biota.PropertiesInt64)
                    snapshot[$"int64 {kvp.Key}"] = kvp.Value.ToString(CultureInfo.InvariantCulture);

            if (biota.PropertiesFloat != null)
                foreach (var kvp in biota.PropertiesFloat.Where(k => !ModRecords.Contains(k.Key)))
                    snapshot[$"float {kvp.Key}"] = kvp.Value.ToString("R", CultureInfo.InvariantCulture);

            if (biota.PropertiesBool != null)
                foreach (var kvp in biota.PropertiesBool)
                    snapshot[$"bool {kvp.Key}"] = kvp.Value.ToString();

            if (biota.PropertiesString != null)
                foreach (var kvp in biota.PropertiesString)
                    snapshot[$"string {kvp.Key}"] = kvp.Value;

            return snapshot;
        }

        private static void AssertSameProperties(Dictionary<string, string> expected, Dictionary<string, string> actual, string context)
        {
            var problems = new List<string>();

            foreach (var kvp in expected)
            {
                if (!actual.TryGetValue(kvp.Key, out var value))
                    problems.Add($"{kvp.Key} was {kvp.Value}, now absent");
                else if (value != kvp.Value)
                    problems.Add($"{kvp.Key} was {kvp.Value}, now {value}");
            }

            foreach (var kvp in actual.Where(k => !expected.ContainsKey(k.Key)))
                problems.Add($"{kvp.Key} = {kvp.Value} appeared");

            Assert.AreEqual(0, problems.Count, $"{context}: {string.Join("; ", problems)}");
        }
    }
}
