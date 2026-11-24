using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Adapter;
using ACE.Entity.Enum.Properties;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// STOP gate for the Balance Trials archetype pipeline: trial characters are built by copying a real one with
    /// DoCopyChar, so every fork item power (equipment mods, jewelry aspects, cloak Mantle, ...) must survive that copy.
    /// Those powers are plain biota property rows, and DoCopyChar moves every player and item biota through
    /// BiotaConverter (EF -> entity -> EF), editing only the identity links in between. So the carry question splits
    /// into two pins: the converter round trip keeps every fork-allocated property id (driven by
    /// Source/property-registry.tsv, so a newly reserved id is covered automatically), and DoCopyChar's item loops
    /// remove nothing but identity links (source pin on the method body, comments stripped, whitespace ignored).
    ///
    /// This class reads no PropertyManager key, so there is nothing to seed or restore.
    /// </summary>
    [TestClass]
    public class CopyCharItemPropertyCarryTests
    {
        private const string Registry = "Source/property-registry.tsv";
        private const string AdminCommands = "Source/ACE.Server/Command/Handlers/AdminCommands.cs";

        private static readonly HashSet<string> CarriedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "PropertyInt", "PropertyInt64", "PropertyFloat", "PropertyBool", "PropertyString", "PropertyDataId",
        };

        [TestMethod]
        public void Round_trip_preserves_every_fork_property_id()
        {
            var checkedCount = 0;
            var skipped = new List<string>();
            var failures = new List<string>();

            foreach (var line in File.ReadAllLines(RepoFile(Registry)))
            {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var cols = line.Split('\t');
                if (cols.Length < 3 || cols[0] == "type")
                    continue;

                var type = cols[0];
                var idText = cols[1];
                var name = cols[2];

                if (!CarriedTypes.Contains(type))
                {
                    skipped.Add($"{type} {idText} (type not carried by this check)");
                    continue;
                }

                if (!ushort.TryParse(idText, out var id))
                {
                    skipped.Add($"{type} {idText} (range row, not a single id)");
                    continue;
                }

                if (name == "RETIRED_DO_NOT_REUSE")
                {
                    skipped.Add($"{type} {id} (RETIRED_DO_NOT_REUSE)");
                    continue;
                }

                if (!Enum.IsDefined(EnumFor(type), id))
                {
                    skipped.Add($"{type} {id} {name} (no enum member)");
                    continue;
                }

                var error = RoundTripOne(type, id);
                if (error != null)
                    failures.Add($"{type} {id} {name}: {error}");

                checkedCount++;
            }

            Console.WriteLine($"checked {checkedCount} registry rows, skipped {skipped.Count}");
            foreach (var s in skipped)
                Console.WriteLine($"  skipped: {s}");

            Assert.AreEqual(0, failures.Count, "properties lost or changed by the EF -> entity -> EF round trip:\n" + string.Join("\n", failures));
            Assert.IsTrue(checkedCount >= 100, $"only {checkedCount} registry rows were checked; the registry parse has degenerated");
        }

        [TestMethod]
        public void DoCopyChar_item_loops_remove_only_identity_links()
        {
            var body = MethodBody(AdminCommands, "internal static void DoCopyChar(");

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "PropertiesIID.Remove(PropertyInstanceId.Owner)",
                "PropertiesIID.Remove(PropertyInstanceId.Wielder)",
                "PropertiesIID.Remove(PropertyInstanceId.Container)",
                "PropertiesIID.Remove(PropertyInstanceId.AllowedActivator)",
                "PropertiesIID.Remove(PropertyInstanceId.AllowedWielder)",
                "PropertiesString.Remove(PropertyString.CraftsmanName)",
            };

            // Every .Remove( in the body, whatever its receiver, so a removal through an alias is caught too.
            var removeCount = Regex.Matches(body, @"\.Remove\(").Count;
            var propertyRemoves = Regex.Matches(body, @"Properties\w+\??\.Remove\([^)]*\)").Select(m => m.Value.Replace("?.", ".")).ToList();

            Assert.AreEqual(removeCount, propertyRemoves.Count, "DoCopyChar has a .Remove( call whose receiver is not a Properties* collection; review it and extend this pin deliberately");

            // positive control: the identity-link swaps this pin exists to allow must still be found
            Assert.IsTrue(propertyRemoves.Count >= 12, $"expected at least 12 identity-link removals in DoCopyChar, found {propertyRemoves.Count}; the body match has degenerated");

            var offenders = propertyRemoves.Where(r => !allowed.Contains(r)).ToList();
            Assert.AreEqual(0, offenders.Count, "DoCopyChar removes a non-identity property, so a copied character would lose it:\n" + string.Join("\n", offenders));

            // Bulk strips. The only clears DoCopyChar may make are the two on the new PLAYER biota (allegiance and
            // house permissions); any other clear, any RemoveAll/RemoveWhere, and any assignment to a Properties*
            // member (= new ..., = null) could wipe item powers wholesale.
            var clears = Regex.Matches(body, @"([\w.?\[\]]+)\.Clear\(\)").Select(m => m.Groups[1].Value.Replace("?.", ".")).OrderBy(r => r, StringComparer.Ordinal).ToList();
            var allowedClears = new List<string> { "newPlayerBiota.HousePermissions", "newPlayerBiota.PropertiesAllegiance" };
            CollectionAssert.AreEqual(allowedClears, clears, "DoCopyChar .Clear() receivers changed; found: " + string.Join(", ", clears));

            var bulkRemoves = Regex.Matches(body, @"[\w.?\[\]]+\.Remove(All|Where)\(").Select(m => m.Value).ToList();
            Assert.AreEqual(0, bulkRemoves.Count, "DoCopyChar bulk-removes from a collection:\n" + string.Join("\n", bulkRemoves));

            var assignments = Regex.Matches(body, @"[\w.?]*Properties\w*=(?![=>])").Select(m => m.Value).ToList();
            Assert.AreEqual(0, assignments.Count, "DoCopyChar assigns a Properties* member, which can replace or null a whole property collection:\n" + string.Join("\n", assignments));
        }

        [TestMethod]
        public void Round_trip_preserves_item_spellbook_and_enchantments()
        {
            // Item cantrips on donor gear live in the spellbook; active item spells in the enchantment registry.
            var biota = new ShardBiota { Id = 0x50000002, WeenieClassId = 1, WeenieType = 1 };

            biota.BiotaPropertiesSpellBook.Add(new ACE.Database.Models.Shard.BiotaPropertiesSpellBook { ObjectId = biota.Id, Spell = 2101, Probability = 0.375f });
            biota.BiotaPropertiesSpellBook.Add(new ACE.Database.Models.Shard.BiotaPropertiesSpellBook { ObjectId = biota.Id, Spell = 6045, Probability = 0.625f });

            var original = new ACE.Database.Models.Shard.BiotaPropertiesEnchantmentRegistry
            {
                ObjectId = biota.Id,
                EnchantmentCategory = 3,
                SpellId = 2101,
                LayerId = 2,
                HasSpellSetId = true,
                SpellCategory = 17,
                PowerLevel = 400,
                StartTime = 0,
                Duration = -1, // permanent
                CasterObjectId = 0x50000002,
                DegradeModifier = 0.5f,
                DegradeLimit = -666f,
                LastTimeDegraded = 12.5,
                StatModType = 36865,
                StatModKey = 9,
                StatModValue = 7.25f,
                SpellSetId = 31,
            };
            biota.BiotaPropertiesEnchantmentRegistry.Add(original);

            var result = RoundTrip(biota);

            var book = result.BiotaPropertiesSpellBook.OrderBy(r => r.Spell).Select(r => $"{r.Spell}:{r.Probability}").ToList();
            CollectionAssert.AreEqual(new List<string> { "2101:0.375", "6045:0.625" }, book, "spellbook after round trip: " + string.Join(", ", book));

            Assert.AreEqual(1, result.BiotaPropertiesEnchantmentRegistry.Count, "enchantment registry row count after round trip");
            var e = result.BiotaPropertiesEnchantmentRegistry.Single();
            Assert.AreEqual(original.EnchantmentCategory, e.EnchantmentCategory, "EnchantmentCategory");
            Assert.AreEqual(original.SpellId, e.SpellId, "SpellId");
            Assert.AreEqual(original.LayerId, e.LayerId, "LayerId");
            Assert.AreEqual(original.HasSpellSetId, e.HasSpellSetId, "HasSpellSetId");
            Assert.AreEqual(original.SpellCategory, e.SpellCategory, "SpellCategory");
            Assert.AreEqual(original.PowerLevel, e.PowerLevel, "PowerLevel");
            Assert.AreEqual(original.StartTime, e.StartTime, "StartTime");
            Assert.AreEqual(original.Duration, e.Duration, "Duration");
            Assert.AreEqual(original.CasterObjectId, e.CasterObjectId, "CasterObjectId");
            Assert.AreEqual(original.DegradeModifier, e.DegradeModifier, "DegradeModifier");
            Assert.AreEqual(original.DegradeLimit, e.DegradeLimit, "DegradeLimit");
            Assert.AreEqual(original.LastTimeDegraded, e.LastTimeDegraded, "LastTimeDegraded");
            Assert.AreEqual(original.StatModType, e.StatModType, "StatModType");
            Assert.AreEqual(original.StatModKey, e.StatModKey, "StatModKey");
            Assert.AreEqual(original.StatModValue, e.StatModValue, "StatModValue");
            Assert.AreEqual(original.SpellSetId, e.SpellSetId, "SpellSetId");
        }

        // ---------------- round trip ----------------

        private static Type EnumFor(string type)
        {
            switch (type)
            {
                case "PropertyInt": return typeof(PropertyInt);
                case "PropertyInt64": return typeof(PropertyInt64);
                case "PropertyFloat": return typeof(PropertyFloat);
                case "PropertyBool": return typeof(PropertyBool);
                case "PropertyString": return typeof(PropertyString);
                case "PropertyDataId": return typeof(PropertyDataId);
                default: throw new ArgumentException(type);
            }
        }

        /// <summary>
        /// Builds an EF biota holding exactly one row of the given type and id, runs it through the same conversions
        /// DoCopyChar applies (EF -> entity -> EF), and returns null when the row survives with its value, else why not.
        /// Values are id-derived and never the type default, so a dropped row cannot pass as a default.
        /// </summary>
        private static string RoundTripOne(string type, ushort id)
        {
            var biota = new ShardBiota { Id = 0x50000001, WeenieClassId = 1, WeenieType = 1 };

            switch (type)
            {
                case "PropertyInt":
                {
                    var value = 100000 + id;
                    biota.BiotaPropertiesInt.Add(new ACE.Database.Models.Shard.BiotaPropertiesInt { ObjectId = biota.Id, Type = id, Value = value });
                    var rows = RoundTrip(biota).BiotaPropertiesInt.Where(r => r.Type == id).ToList();
                    return rows.Count != 1 ? $"{rows.Count} rows after round trip" : rows[0].Value != value ? $"value {rows[0].Value} != {value}" : null;
                }
                case "PropertyInt64":
                {
                    var value = 5_000_000_000L + id;
                    biota.BiotaPropertiesInt64.Add(new ACE.Database.Models.Shard.BiotaPropertiesInt64 { ObjectId = biota.Id, Type = id, Value = value });
                    var rows = RoundTrip(biota).BiotaPropertiesInt64.Where(r => r.Type == id).ToList();
                    return rows.Count != 1 ? $"{rows.Count} rows after round trip" : rows[0].Value != value ? $"value {rows[0].Value} != {value}" : null;
                }
                case "PropertyFloat":
                {
                    var value = 1.25 + id;
                    biota.BiotaPropertiesFloat.Add(new ACE.Database.Models.Shard.BiotaPropertiesFloat { ObjectId = biota.Id, Type = id, Value = value });
                    var rows = RoundTrip(biota).BiotaPropertiesFloat.Where(r => r.Type == id).ToList();
                    return rows.Count != 1 ? $"{rows.Count} rows after round trip" : rows[0].Value != value ? $"value {rows[0].Value} != {value}" : null;
                }
                case "PropertyBool":
                {
                    biota.BiotaPropertiesBool.Add(new ACE.Database.Models.Shard.BiotaPropertiesBool { ObjectId = biota.Id, Type = id, Value = true });
                    var rows = RoundTrip(biota).BiotaPropertiesBool.Where(r => r.Type == id).ToList();
                    return rows.Count != 1 ? $"{rows.Count} rows after round trip" : !rows[0].Value ? "value false != true" : null;
                }
                case "PropertyString":
                {
                    var value = $"fork-carry-{id}";
                    biota.BiotaPropertiesString.Add(new ACE.Database.Models.Shard.BiotaPropertiesString { ObjectId = biota.Id, Type = id, Value = value });
                    var rows = RoundTrip(biota).BiotaPropertiesString.Where(r => r.Type == id).ToList();
                    return rows.Count != 1 ? $"{rows.Count} rows after round trip" : rows[0].Value != value ? $"value '{rows[0].Value}' != '{value}'" : null;
                }
                case "PropertyDataId":
                {
                    var value = 0x06000000u + id;
                    biota.BiotaPropertiesDID.Add(new ACE.Database.Models.Shard.BiotaPropertiesDID { ObjectId = biota.Id, Type = id, Value = value });
                    var rows = RoundTrip(biota).BiotaPropertiesDID.Where(r => r.Type == id).ToList();
                    return rows.Count != 1 ? $"{rows.Count} rows after round trip" : rows[0].Value != value ? $"value {rows[0].Value} != {value}" : null;
                }
                default:
                    throw new ArgumentException(type);
            }
        }

        private static ShardBiota RoundTrip(ShardBiota biota)
        {
            var entity = BiotaConverter.ConvertToEntityBiota(biota);
            return BiotaConverter.ConvertFromEntityBiota(entity);
        }

        // ---------------- source helpers (pattern from BankHopeslayerMarksTests) ----------------

        private static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            throw new FileNotFoundException($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);

            return sb.ToString();
        }

        /// <summary>Removes // and /* */ comments, leaving string and char literals alone.</summary>
        private static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);

            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];

                if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && src[i - 1] == '@';
                    sb.Append(c);
                    i++;

                    while (i < src.Length)
                    {
                        sb.Append(src[i]);

                        if (!verbatim && src[i] == '\\' && i + 1 < src.Length)
                        {
                            i++;
                            sb.Append(src[i]);
                        }
                        else if (src[i] == c)
                        {
                            if (verbatim && i + 1 < src.Length && src[i + 1] == '"')
                            {
                                i++;
                                sb.Append(src[i]);
                            }
                            else
                                break;
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n')
                        i++;

                    sb.Append('\n');
                    continue;
                }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                        i++;

                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The brace-matched body of the one method whose signature starts with <paramref name="signature"/>, comments
        /// stripped and whitespace-squashed. Internal so CopyCharOptionsWiringTests reads DoCopyChar the same way.
        /// </summary>
        internal static string MethodBody(string relativePath, string signature)
        {
            var src = StripComments(File.ReadAllText(RepoFile(relativePath)));

            var needle = Squash(signature);
            var map = new List<int>(src.Length);
            var squashed = new StringBuilder(src.Length);

            for (var i = 0; i < src.Length; i++)
            {
                if (char.IsWhiteSpace(src[i]))
                    continue;

                squashed.Append(src[i]);
                map.Add(i);
            }

            var squashedText = squashed.ToString();
            var at = squashedText.IndexOf(needle, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"signature not found in {relativePath}: {signature}");
            Assert.AreEqual(at, squashedText.LastIndexOf(needle, StringComparison.Ordinal), $"signature is ambiguous in {relativePath}: {signature}");

            var open = src.IndexOf('{', map[at + needle.Length - 1]);
            Assert.IsTrue(open >= 0, "no body found");

            var depth = 0;
            for (var i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return Squash(src.Substring(open, i - open + 1));
            }

            Assert.Fail($"unterminated body for {signature}");
            return null;
        }
    }
}
