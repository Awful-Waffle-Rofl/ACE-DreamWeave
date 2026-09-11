using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.EquipmentMods;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Tables;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Exports the LIVE weapon-mod / equipment-mod / gear-rating registries to tools/salvage-sim/catalog.json
    /// and fails if the committed file has drifted from it. Modeled directly on
    /// ClassAbilityPlannerCatalogTests - see that file's remarks for why an export like this has to run as a
    /// test rather than a script (the registries are constructed static state with no source text a regex
    /// could parse reliably), and see tools/salvage-sim/CATALOG-CONTRACT.md for the authoritative shape.
    ///
    /// TUNABLES ARE READ, NEVER TYPED. Every value under "tunables" comes from
    /// <see cref="DefaultPropertyManager"/>'s default tables rather than being restated here, so a retune of a
    /// weapon_mod_* / equipment_mod_* default shows up as a catalog diff. The default tables are plain public
    /// static dictionaries, so they are readable with no database and no PropertyManager.Initialize call -
    /// which is why this reads them directly instead of going through PropertyManager.GetDouble/GetBool (those
    /// resolve through the loaded, DB-backed set).
    ///
    /// GEAR RATING TABLES ARE PRIVATE STATIC FIELDS on <see cref="GearRatingChance"/>, with no public
    /// accessor. Read via reflection rather than retyping the numbers, so a retune there also shows up as a
    /// catalog diff instead of trusting a hand-copied constant to stay in sync.
    ///
    /// REGENERATING. Set SALVAGE_SIM_REGEN=1 and run this test; it overwrites catalog.json and reports
    /// Inconclusive instead of asserting. See tools/salvage-sim/README.md.
    ///
    /// PROVENANCE IS EXCLUDED FROM THE COMPARISON. "generatedFrom" records the sha and date of the run that
    /// produced the file. It cannot participate in the drift check, because committing the file changes HEAD
    /// and would make the very next run disagree with itself. The block's SHAPE is still asserted.
    /// </summary>
    [TestClass]
    public class SalvageSimCatalogTests
    {
        private const string CatalogRelativePath = "tools/salvage-sim/catalog.json";

        private const string RegenEnvironmentVariable = "SALVAGE_SIM_REGEN";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,

            // Display formats carry "+", "%" and the odd apostrophe; the default encoder escapes them and
            // makes the committed file unreadable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        [TestMethod]
        public void SalvageSimCatalog_MatchesTheLiveRegistry()
        {
            var path = CatalogPath();

            var generated = BuildCatalog(withProvenance: true);

            if (string.Equals(Environment.GetEnvironmentVariable(RegenEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                File.WriteAllText(path, Render(generated), new UTF8Encoding(false));

                Assert.Inconclusive($"{RegenEnvironmentVariable}=1: rewrote {path}. Unset it and re-run to validate, then commit the file.");
                return;
            }

            Assert.IsTrue(File.Exists(path),
                $"{CatalogRelativePath} does not exist. Regenerate it with {RegenEnvironmentVariable}=1 and commit it.");

            var committedText = File.ReadAllText(path);

            JsonNode committed;

            try
            {
                committed = JsonNode.Parse(committedText);
            }
            catch (JsonException ex)
            {
                Assert.Fail($"{CatalogRelativePath} is not valid JSON: {ex.Message}");
                return;
            }

            AssertProvenanceShape(committed);

            var expectedText = Render(StripProvenance(generated));
            var actualText = Render(StripProvenance(committed));

            if (string.Equals(expectedText, actualText, StringComparison.Ordinal))
                return;

            Assert.Fail(
                $"{CatalogRelativePath} has drifted from the live registry.{Environment.NewLine}" +
                $"Regenerate with {RegenEnvironmentVariable}=1 and commit the result.{Environment.NewLine}{Environment.NewLine}" +
                DescribeDifference(actualText, expectedText));
        }

        /// <summary>
        /// The tunables block reproduces the weapon_mod_* / equipment_mod_* defaults. Pinned separately from
        /// the file comparison so a retune reads as "a tunable changed", not as "the catalog is stale" - and so
        /// the keys the page depends on cannot be renamed out from under it silently.
        /// </summary>
        [TestMethod]
        public void SalvageSimCatalog_TunablesComeFromThePropertyDefaults()
        {
            var tunables = BuildTunables();

            var chances = tunables["weaponModSpecialChance"].AsArray().Select(n => (double)n).ToArray();

            CollectionAssert.AreEqual(
                new[]
                {
                    Double("weapon_mod_special_chance_1"),
                    Double("weapon_mod_special_chance_2"),
                    Double("weapon_mod_special_chance_3"),
                    Double("weapon_mod_special_chance_4"),
                },
                chances);

            Assert.AreEqual(Double("weapon_mod_magnitude_scale"), (double)tunables["weaponModMagnitudeScale"]);
            Assert.AreEqual(Bool("weapon_mod_guarantee_damage_special"), (bool)tunables["weaponModGuaranteeDamageSpecial"]);
            Assert.AreEqual(Double("equipment_mod_potency_scale"), (double)tunables["equipmentModPotencyScale"]);
            Assert.AreEqual(WeaponModRegistry.MaxSpecials, (int)tunables["maxSpecials"]);
        }

        /// <summary>Every live WeaponModRegistry.AllMods row appears exactly once, and nothing else does.</summary>
        [TestMethod]
        public void SalvageSimCatalog_WeaponModsMatchTheRegistry()
        {
            var rows = BuildCatalog(withProvenance: false)["weaponMods"].AsArray()
                .Select(r => (string)r["id"])
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var live = WeaponModRegistry.AllMods
                .Select(m => m.Id.ToString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(live, rows);
        }

        /// <summary>Every live EquipmentModRegistry.AllMods row appears exactly once, and nothing else does.</summary>
        [TestMethod]
        public void SalvageSimCatalog_EquipmentModsMatchTheRegistry()
        {
            var rows = BuildCatalog(withProvenance: false)["equipmentMods"].AsArray()
                .Select(r => (string)r["id"])
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var live = EquipmentModRegistry.AllMods
                .Select(m => m.Id.ToString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(live, rows);
        }

        // ---------------------------------------------------------------------------------------------
        // catalog construction
        // ---------------------------------------------------------------------------------------------

        private static JsonObject BuildCatalog(bool withProvenance)
        {
            var root = new JsonObject();

            if (withProvenance)
            {
                root["generatedFrom"] = new JsonObject
                {
                    ["sha"] = HeadSha(),
                    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                };
            }

            root["tunables"] = BuildTunables();
            root["qualityTiers"] = BuildQualityTiers();
            root["weaponMods"] = BuildWeaponMods();
            root["equipmentMods"] = BuildEquipmentMods();
            root["gearRatings"] = BuildGearRatings();

            return root;
        }

        private static JsonObject BuildTunables() => new JsonObject
        {
            ["weaponModSpecialChance"] = new JsonArray(
                Double("weapon_mod_special_chance_1"),
                Double("weapon_mod_special_chance_2"),
                Double("weapon_mod_special_chance_3"),
                Double("weapon_mod_special_chance_4")),
            ["weaponModMagnitudeScale"] = Double("weapon_mod_magnitude_scale"),
            ["weaponModGuaranteeDamageSpecial"] = Bool("weapon_mod_guarantee_damage_special"),
            ["equipmentModPotencyScale"] = Double("equipment_mod_potency_scale"),
            ["maxSpecials"] = WeaponModRegistry.MaxSpecials,
        };

        /// <summary>
        /// WeaponQualityTiers.Ladder is private, but every value it holds is reachable through the class's
        /// public static methods, so this walks the enum rather than reflecting into the private array.
        /// Ordered highest threshold first, per the contract.
        /// </summary>
        private static JsonArray BuildQualityTiers()
        {
            var array = new JsonArray();

            var tiers = ((WeaponQualityTier[])Enum.GetValues(typeof(WeaponQualityTier)))
                .Where(t => t != WeaponQualityTier.None)
                .Select(t => new
                {
                    Tier = t,
                    Name = WeaponQualityTiers.NameFor(t),
                    Threshold = WeaponQualityTiers.ThresholdFor(t),
                })
                .OrderByDescending(t => t.Threshold);

            foreach (var tier in tiers)
            {
                array.Add(new JsonObject
                {
                    ["tier"] = tier.Name,
                    ["threshold"] = tier.Threshold,
                });
            }

            return array;
        }

        private static JsonArray BuildWeaponMods()
        {
            var array = new JsonArray();

            foreach (var mod in WeaponModRegistry.AllMods)
            {
                var classes = new JsonArray();

                foreach (WeaponClass flag in Enum.GetValues(typeof(WeaponClass)))
                {
                    if (flag == WeaponClass.None || flag == WeaponClass.All)
                        continue;

                    if ((mod.Classes & flag) != 0)
                        classes.Add(flag.ToString());
                }

                array.Add(new JsonObject
                {
                    ["id"] = mod.Id.ToString(),
                    ["displayName"] = mod.DisplayName,
                    ["tier"] = mod.Tier.ToString(),
                    ["maxRoll"] = mod.MaxRoll,
                    ["minPotency"] = mod.MinPotency,
                    ["isInteger"] = mod.IsInteger,
                    ["binary"] = mod.Binary,
                    ["affectsDamage"] = mod.AffectsSingleTargetDamage,
                    ["classes"] = classes,
                    ["displayFormat"] = mod.DisplayFormat,
                    ["displayScale"] = mod.DisplayScale,
                });
            }

            return array;
        }

        private static JsonArray BuildEquipmentMods()
        {
            var array = new JsonArray();

            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                array.Add(new JsonObject
                {
                    ["id"] = mod.Id.ToString(),
                    ["displayName"] = mod.DisplayName,
                    ["maxMagnitude"] = mod.MaxMagnitude,
                    ["minPotency"] = mod.MinPotency,
                    ["stackCap"] = mod.StackCap,
                    ["standalone"] = mod.Standalone,
                    ["hookKind"] = mod.HookKind.ToString(),
                    ["displayFormat"] = mod.DisplayFormat,
                    ["displayScale"] = mod.DisplayScale,
                });
            }

            return array;
        }

        private static JsonObject BuildGearRatings() => new JsonObject
        {
            ["tierGate"] = 8,
            ["anyRatingChance"] = ReadBoolChanceTable("RatingChance", true),
            ["armor"] = ReadIntChanceTable("ArmorRating"),
            ["clothingJewelry"] = ReadIntChanceTable("ClothingJewelryRating"),
        };

        // ---------------------------------------------------------------------------------------------
        // GearRatingChance reflection - its tables are private static fields with no public accessor
        // ---------------------------------------------------------------------------------------------

        private static double ReadBoolChanceTable(string fieldName, bool wantedResult)
        {
            var table = (System.Collections.IEnumerable)GearRatingChanceField(fieldName);

            foreach (var entry in table)
            {
                var (result, chance) = ReadEntry<bool>(entry);

                if (result == wantedResult)
                    return CleanFloat(chance);
            }

            Assert.Fail($"GearRatingChance.{fieldName} has no entry for {wantedResult}.");
            return 0.0;
        }

        private static JsonArray ReadIntChanceTable(string fieldName)
        {
            var table = (System.Collections.IEnumerable)GearRatingChanceField(fieldName);

            var array = new JsonArray();

            foreach (var entry in table)
            {
                var (result, chance) = ReadEntry<int>(entry);

                array.Add(new JsonArray(result, CleanFloat(chance)));
            }

            return array;
        }

        private static (T result, float chance) ReadEntry<T>(object entry)
        {
            // Each entry is a boxed ValueTuple<T, float> (ChanceTable<T> : List<(T result, float chance)>).
            // The tuple's public fields are literally named Item1 / Item2.
            var type = entry.GetType();

            var result = (T)type.GetField("Item1").GetValue(entry);
            var chance = (float)type.GetField("Item2").GetValue(entry);

            return (result, chance);
        }

        /// <summary>
        /// A float widened straight to double carries garbage past its ~7 significant digits (0.95f becomes
        /// 0.949999988079071), which would commit as noise and make every future diff unreadable. Round-tripping
        /// through decimal recovers the float's actual shortest decimal representation.
        /// </summary>
        private static double CleanFloat(float value) => (double)(decimal)value;

        private static object GearRatingChanceField(string fieldName)
        {
            var field = typeof(GearRatingChance).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(field,
                $"GearRatingChance no longer has a private static field named {fieldName} - it was renamed, and the salvage-sim catalog depends on it.");

            var value = field.GetValue(null);

            Assert.IsNotNull(value, $"GearRatingChance.{fieldName} is null.");

            return value;
        }

        // ---------------------------------------------------------------------------------------------
        // property default lookups
        // ---------------------------------------------------------------------------------------------

        private static double Double(string key)
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key),
                $"{key} is not a known double property default - it was renamed or removed, and the salvage-sim catalog depends on it.");

            return DefaultPropertyManager.DefaultDoubleProperties[key].Item;
        }

        private static bool Bool(string key)
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(key),
                $"{key} is not a known bool property default - it was renamed or removed, and the salvage-sim catalog depends on it.");

            return DefaultPropertyManager.DefaultBooleanProperties[key].Item;
        }

        // ---------------------------------------------------------------------------------------------
        // rendering / comparison
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Serializes with a fixed newline. System.Text.Json's indented writer defaults to
        /// Environment.NewLine, which would make the file differ between a Windows and a Linux run for no
        /// reason - so the newline is normalized to \n here, and the file is committed that way.
        /// </summary>
        private static string Render(JsonNode node) =>
            node.ToJsonString(WriteOptions).Replace("\r\n", "\n") + "\n";

        private static JsonNode StripProvenance(JsonNode node)
        {
            var clone = JsonNode.Parse(node.ToJsonString());
            clone.AsObject().Remove("generatedFrom");
            return clone;
        }

        private static void AssertProvenanceShape(JsonNode committed)
        {
            var provenance = committed["generatedFrom"];

            Assert.IsNotNull(provenance, "catalog.json is missing its generatedFrom block.");

            var sha = (string)provenance["sha"];
            var date = (string)provenance["date"];

            Assert.IsFalse(string.IsNullOrWhiteSpace(sha), "generatedFrom.sha is empty.");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(date ?? string.Empty, @"^\d{4}-\d{2}-\d{2}$"),
                $"generatedFrom.date must be yyyy-mm-dd, found '{date}'.");
        }

        /// <summary>
        /// Prints the first differing lines with line numbers plus the surrounding context, so a failing run
        /// says WHAT drifted rather than only THAT something did.
        /// </summary>
        private static string DescribeDifference(string actual, string expected)
        {
            var actualLines = actual.Split('\n');
            var expectedLines = expected.Split('\n');

            var report = new StringBuilder();
            var shown = 0;

            for (var i = 0; i < Math.Max(actualLines.Length, expectedLines.Length) && shown < 20; i++)
            {
                var a = i < actualLines.Length ? actualLines[i] : "<end of file>";
                var e = i < expectedLines.Length ? expectedLines[i] : "<end of file>";

                if (string.Equals(a, e, StringComparison.Ordinal))
                    continue;

                report.AppendLine($"line {i + 1}:");
                report.AppendLine($"  committed: {a.Trim()}");
                report.AppendLine($"  live     : {e.Trim()}");
                shown++;
            }

            if (shown == 0)
                report.AppendLine("(no line-level difference found - the files differ only in trailing whitespace)");
            else if (actualLines.Length != expectedLines.Length)
                report.AppendLine($"(committed has {actualLines.Length} lines, live registry produces {expectedLines.Length})");

            return report.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // paths
        // ---------------------------------------------------------------------------------------------

        internal static string CatalogPath() =>
            Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "tools", "salvage-sim", "catalog.json");

        /// <summary>
        /// HEAD's sha, for the provenance block only. Shelling out to git is acceptable here because it runs
        /// on the regeneration path alone; a checkout with no git available still validates, it just cannot
        /// re-stamp provenance.
        /// </summary>
        private static string HeadSha()
        {
            try
            {
                var info = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = ClassAbilityAffinityDeclarationTests.RepoRoot(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(info))
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(15000);

                    return string.IsNullOrWhiteSpace(output) ? "unknown" : output;
                }
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
