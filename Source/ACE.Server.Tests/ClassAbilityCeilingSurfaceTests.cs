using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Command.Handlers;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the CONSUMERS of the ceiling metadata: what /abilities info, /abilities list, the token appraisal, the descriptions feed
    /// and the panel model actually render or serialize. A declaration with no surface reading it is the bug these catch.
    /// Values come from a DictionaryTunableValues, never PropertyManager, so no static state is read or leaked.
    /// </summary>
    [TestClass]
    public class ClassAbilityCeilingSurfaceTests
    {
        [TestInitialize]
        public void Init() => DefaultPropertyManager.LoadDefaultProperties();

        private static ITunableValues Dict(string key, double value) =>
            new DictionaryTunableValues(new Dictionary<string, double> { [key] = value });

        private static ClassAbilityDefinition Def(ClassAbilityId id) => ClassAbilityRegistry.Abilities[id];

        private static ClassAbilityDefinition ByName(string displayName) =>
            ClassAbilityRegistry.Abilities.Values.Single(d => d.DisplayName == displayName);

        private sealed class OfflineSubject : IClassAbilityPanelSubject
        {
            public uint CharacterGuid => 0x50000B01;
            public string Name => "Subject";
            public int Level => 275;
            public string Revision => "rev:1";
            public int PointsAvailable => 0;
            public int PointsEarned => 0;
            public IReadOnlyDictionary<ClassAbilityId, int> Ranks { get; set; } = new Dictionary<ClassAbilityId, int>();
            public Func<ClassAbilityDefinition, int, ClassAbilityReadout?> ReadoutFn { get; set; }
            public bool CanRaise(ClassAbilityDefinition ability, out string refusal) { refusal = "no"; return false; }
            public bool CanLower(ClassAbilityDefinition ability, out string refusal) { refusal = "no"; return false; }
            public ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank) => ReadoutFn?.Invoke(ability, rank);
        }

        private static readonly ClassAbilityPanelTierRequirements Req = new ClassAbilityPanelTierRequirements(3, 5, 8, 15);

        [TestMethod]
        public void InfoLines_TokenAppraisal_Feed_Panel_AllCarryParryAvoidanceCap()
        {
            var v = Dict("class_ability_avoidance_cap", 0.42);
            var parry = Def(ClassAbilityId.Parry);

            var info = string.Join("\n", ClassAbilityCommands.BuildInfoLines(parry, 1, v));
            StringAssert.Contains(info, "42%");

            var token = ClassAbilityTokenAppraisal.LongDesc("committed text", (int)parry.Id, 1, v);
            StringAssert.Contains(token, "42%");
            StringAssert.Contains(token, "Limits:");

            var feedParry = ClassAbilityDescriptionFeed.Build(v).Abilities.Single(a => a.Id == parry.Name);
            Assert.AreEqual(42.0, feedParry.Ceilings.Single(c => c.Id == "avoidance").Value);

            var snap = ClassAbilityPanelModel.Build(new OfflineSubject(), online: false, writeRefusal: null, Req, v);
            var row = snap.Abilities.Single(a => a.Id == parry.Name);
            Assert.AreEqual("avoidance", row.Ceilings[0].Id);
            Assert.AreEqual(42.0, row.Ceilings[0].Max);
            Assert.AreEqual("%", row.Ceilings[0].Unit);

            // The wire carries the same number.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snap, MarketApiHost.Json));
            var wire = doc.RootElement.GetProperty("abilities").EnumerateArray().Single(a => a.GetProperty("id").GetString() == parry.Name);
            Assert.AreEqual(42.0, wire.GetProperty("ceilings")[0].GetProperty("max").GetDouble());
        }

        [TestMethod]
        public void Feed_Ceilings_AreOptionalAndKeyFree()
        {
            var feed = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Defaults);
            var json = JsonSerializer.Serialize(feed, MarketApiHost.Json);

            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("class_ability_"));

            using var doc = JsonDocument.Parse(json);
            var abilities = doc.RootElement.GetProperty("abilities").EnumerateArray().ToList();
            var withCeilings = abilities.Where(a => a.TryGetProperty("ceilings", out _)).ToList();
            Assert.IsTrue(withCeilings.Count >= 30, $"declared ceilings reach the feed ({withCeilings.Count})");

            // An ability with none omits the key (or at worst emits []), and the same for rates.
            var none = ClassAbilityRegistry.Abilities.Values.First(d => d.Ceilings == null || d.Ceilings.Count == 0);
            var noneRow = abilities.Single(a => a.GetProperty("id").GetString() == none.Name);
            Assert.IsFalse(noneRow.TryGetProperty("ceilings", out var emitted) && emitted.GetArrayLength() > 0);

            // single lowercase-word / snake keys only, as the feed's contract says
            foreach (var c in withCeilings.SelectMany(a => a.GetProperty("ceilings").EnumerateArray()))
                CollectionAssert.IsSubsetOf(c.EnumerateObject().Select(p => p.Name).ToList(),
                    new[] { "id", "bounds", "label", "value", "unit", "text", "mod", "scales" });
        }

        [TestMethod]
        public void AcidProc_Feed_GearCeiling_Is30_9_WithModAcidProc()
        {
            var acid = ByName("Acid Proc");
            var gear = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Defaults).Abilities.Single(a => a.Id == acid.Name)
                .Ceilings.Single(c => c.Id == "gear");

            Assert.AreEqual(30.9, gear.Value.Value, 1e-9);
            Assert.AreEqual("%", gear.Unit);
            Assert.AreEqual("gear", gear.Bounds);
            Assert.AreEqual(EquipmentModId.AcidProc.ToString(), gear.Mod);
            StringAssert.Contains(gear.Text, "30.9%");
        }

        [TestMethod]
        public void Feed_Rates_ReportModelAndText_ForSharedAndUncapped()
        {
            var feed = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Defaults);

            var volley = feed.Abilities.Single(a => a.Id == ByName("Double Volley").Name);
            Assert.AreEqual("uncapped", volley.Rates.Model);
            StringAssert.EndsWith(volley.Rates.Text, "No limit on what affinity adds.");

            var parry = feed.Abilities.Single(a => a.Id == ByName("Parry").Name);
            Assert.AreEqual(parry.Affinity.Count > 0 ? "shared" : null, parry.Rates?.Model);

            var noAffinity = feed.Abilities.First(a => a.Affinity.Count == 0);
            Assert.IsNull(noAffinity.Rates, "an ability with no affinity rider has no rates");
        }

        [TestMethod]
        public void InfoLines_OrderIsDescriptionAffinityCeilingsRate_ForParry()
        {
            var lines = ClassAbilityCommands.BuildInfoLines(Def(ClassAbilityId.Parry), 1, ClassAbilityText.Defaults);
            var affinity = lines.FindIndex(l => l.TrimStart().StartsWith("Affinity:", StringComparison.Ordinal));
            var limit = lines.FindIndex(l => l.StartsWith("  Limit:", StringComparison.Ordinal));
            var rate = lines.FindIndex(l => l.Contains("adds") && l.Contains("per 100 skill"));

            Assert.IsTrue(affinity > 1, "description then affinity");
            Assert.AreEqual(affinity + 1, limit);
            Assert.IsTrue(rate > limit);
            Assert.IsFalse(lines[rate].EndsWith(".."), "the rate line carries its own terminator, if any");
        }

        [TestMethod]
        public void InfoLines_Exsanguinate_StaticCeiling_SaysTopRank_AndRankVaryingCeilingsToo()
        {
            var exs = string.Join("\n", ClassAbilityCommands.BuildInfoLines(ByName("Exsanguinate"), 1, ClassAbilityText.Defaults));
            StringAssert.Contains(exs, "max blood charges");
            StringAssert.Contains(exs, "at top rank");

            // a flat tunable ceiling is not a top-rank figure
            var parry = string.Join("\n", ClassAbilityCommands.BuildInfoLines(Def(ClassAbilityId.Parry), 1, ClassAbilityText.Defaults));
            Assert.IsFalse(parry.Contains("at top rank"));

            // a per-rank tunable is, for a reader with no rank
            var ward = string.Join("\n", ClassAbilityCommands.BuildInfoLines(ByName("Runic Ward"), 0, ClassAbilityText.Defaults));
            StringAssert.Contains(ward, "at top rank");
        }

        [TestMethod]
        public void InfoLines_OwnedRank_ShowsOwnRankValues_RunicWardRank2Is20Percent()
        {
            var def = ByName("Runic Ward");
            var rank2 = ClassAbilityCommands.BuildInfoLines(def, 2, ClassAbilityText.Defaults);
            Assert.IsTrue(rank2.Any(l => l.StartsWith("  Limit: ", StringComparison.Ordinal) && l.Contains("20%")), string.Join("\n", rank2));
            Assert.IsFalse(rank2.Any(l => l.Contains("at top rank")));

            // Exsanguinate's figure is another ability's top rank whatever the reader owns
            var exs = string.Join("\n", ClassAbilityCommands.BuildInfoLines(ByName("Exsanguinate"), 2, ClassAbilityText.Defaults));
            StringAssert.Contains(exs, "at top rank");
        }

        // ---- wire contract guards ----

        private static string RepoFile(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (System.IO.File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            throw new InvalidOperationException("could not find " + string.Join("/", parts) + " walking up from " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// No YAML parser is referenced by any test project, so this is the lightweight guard: a plain (unquoted) scalar whose text
        /// contains ": " is a YAML ScannerError that broke the contract once (rates.description, 2026-11-23).
        /// </summary>
        [TestMethod]
        public void MarketApiYaml_NoPlainScalarDescriptionContainsColonSpace()
        {
            var bad = new List<string>();
            var lines = System.IO.File.ReadAllLines(RepoFile("Docs", "Market", "market-api-v1.yaml"));
            for (var i = 0; i < lines.Length; i++)
            {
                var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"^\s*(?:- )?description:\s+(\S.*)$");
                if (!m.Success)
                    continue;

                var value = m.Groups[1].Value;
                if ("'\">|[{&*!".IndexOf(value[0]) >= 0)
                    continue;

                if (value.Contains(": ") || value.Contains(" #"))
                    bad.Add($"{i + 1}: {lines[i].Trim()}");
            }

            Assert.AreEqual(0, bad.Count, "quote these or use >-:\n" + string.Join("\n", bad));
        }

        /// <summary>The panel service must build both snapshots from the LIVE tunables (a bare Build falls back to code defaults).</summary>
        [TestMethod]
        public void PanelService_BothBuildCalls_PassLiveTunables()
        {
            var text = System.IO.File.ReadAllText(RepoFile("Source", "ACE.Server", "Managers", "Market", "ClassAbilityPanelService.cs"));
            var calls = System.Text.RegularExpressions.Regex.Matches(text, @"ClassAbilityPanelModel\.Build\([^;]*;");
            Assert.AreEqual(2, calls.Count);
            foreach (System.Text.RegularExpressions.Match call in calls)
                StringAssert.Contains(call.Value, "ClassAbilityText.Live");
        }

        [TestMethod]
        public void TokenAppraisal_AddsSentenceOnlyWhereCeilingsExist_AndNeverChangesTokenLongDesc()
        {
            foreach (var def in ClassAbilityRegistry.Abilities.Values)
            {
                var pinned = ClassAbilityText.TokenLongDesc(def, 1, ClassAbilityText.Defaults);
                var rendered = ClassAbilityTokenAppraisal.LongDesc("committed", (int)def.Id, 1, ClassAbilityText.Defaults);
                var sentence = ClassAbilityCeilingText.Sentence(def, ClassAbilityText.Defaults);
                Assert.AreEqual(sentence == null ? pinned : pinned + " " + sentence, rendered, def.Name);
            }
        }

        // ---- /abilities list ----

        private static ClassAbilityReadout SyntheticReadout(ClassAbilityDefinition def, bool binding)
        {
            var v = ClassAbilityText.Defaults;
            var rank = def.MaxRank > 0 ? def.MaxRank : 1;
            return new ClassAbilityReadout
            {
                HasValue = true,
                Unit = "%",
                Label = "x",
                Ceilings = def.Ceilings.Select(c => ClassAbilityCeilings.Read(def, c.Id, binding ? 1e9 : 0.5, rank, v)).ToList(),
            };
        }

        [TestMethod]
        public void ListEntryLines_EveryRegisteredAbility_OneCompactLimitsLine_Within255()
        {
            var withCeilings = ClassAbilityRegistry.Abilities.Values.Where(d => d.Ceilings is { Count: > 0 }).ToList();
            Assert.IsTrue(withCeilings.Count >= 30);

            foreach (var def in withCeilings)
                foreach (var binding in new[] { false, true })
                {
                    var lines = ClassAbilityCommands.ListEntryLines(def, def.MaxRank, SyntheticReadout(def, binding));
                    Assert.AreEqual(2, lines.Count, $"{def.Name}: the readout line plus exactly one limits line");
                    Assert.IsTrue(lines.All(l => l.Length <= 255), $"{def.Name}: {lines.Max(l => l.Length)}");
                    StringAssert.StartsWith(lines[1], "  limits: ");
                }
        }

        [TestMethod]
        public void ListEntryLines_NoCeilingReadings_IsTheReadoutLineAlone()
        {
            var def = Def(ClassAbilityId.Parry);
            var lines = ClassAbilityCommands.ListEntryLines(def, 1, new ClassAbilityReadout { HasValue = true, Unit = "%", Label = "x" });
            Assert.AreEqual(1, lines.Count);
        }

        [TestMethod]
        public void CompactReadingLine_ParryShape()
        {
            var parry = Def(ClassAbilityId.Parry);
            var v = ClassAbilityText.Defaults;
            var line = ClassAbilityCeilingText.CompactReadingLine(parry, new ClassAbilityReadout
            {
                Ceilings = new[] { ClassAbilityCeilings.Read(parry, "avoidance", 47, 1, v) },
            });
            Assert.AreEqual("  limits: combined block and parry chance 47/50%", line);
        }

        [TestMethod]
        public void CompactReadingLine_DropsTrailingReadings_RatherThanExceedTheLimit()
        {
            var parry = Def(ClassAbilityId.Parry);
            var r = ClassAbilityCeilings.Read(parry, "avoidance", 47, 1, ClassAbilityText.Defaults);
            var line = ClassAbilityCeilingText.CompactReadingLine(parry, new ClassAbilityReadout { Ceilings = new[] { r, r, r } }, maxLength: 80);
            Assert.IsTrue(line.Length <= 80);
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(line, "47/50").Count);
        }

        [TestMethod]
        public void CascadeGeneration_StructuralLevelReading_NeverPrintsAtLimit_EvenWithAZeroCap()
        {
            var cascade = ByName("Cascade");
            var zeroCap = Dict("class_ability_cascade_max_generation", 0);
            var reading = ClassAbilityCeilings.Read(cascade, "generation", 0.0, 1, zeroCap);
            Assert.IsTrue(reading.Binding, "a zero cap is a real bound, so the reading is binding...");

            var readout = new ClassAbilityReadout { Ceilings = new[] { reading } };
            StringAssert.DoesNotMatch(ClassAbilityCeilingText.ReadingLine(cascade, readout), new System.Text.RegularExpressions.Regex("at limit"));
            StringAssert.DoesNotMatch(ClassAbilityCeilingText.CompactReadingLine(cascade, readout), new System.Text.RegularExpressions.Regex("at limit"));

            // ...but a non-Level ceiling at its bound still says so.
            var parry = Def(ClassAbilityId.Parry);
            var atCap = new ClassAbilityReadout { Ceilings = new[] { ClassAbilityCeilings.Read(parry, "avoidance", 55, 1, ClassAbilityText.Defaults) } };
            StringAssert.Contains(ClassAbilityCeilingText.CompactReadingLine(parry, atCap), "(at limit)");
        }

        [TestMethod]
        public void AttackSpeed_ReadingLabelsReadNaturally()
        {
            var def = ByName("Attack Speed");
            var r = ClassAbilityCeilings.Read(def, "attackspeed", 1.2, 1, ClassAbilityText.Defaults);
            var line = ClassAbilityCeilingText.CompactReadingLine(def, new ClassAbilityReadout { Ceilings = new[] { r } });
            StringAssert.Contains(line, "attack speed 1.2/");
            StringAssert.Contains(line, "x");
        }

        // ---- panel ----

        [TestMethod]
        public void Panel_OnlineReadout_CarriesAffinityGearAndCeilingReadings_OfflineCarriesStaticOnly()
        {
            var parry = Def(ClassAbilityId.Parry);
            var v = ClassAbilityText.Defaults;
            var subject = new OfflineSubject
            {
                Ranks = new Dictionary<ClassAbilityId, int> { [parry.Id] = 1 },
                ReadoutFn = (d, rank) => d.Id != parry.Id ? null : new ClassAbilityReadout
                {
                    HasValue = true, Skill = 10, Affinity = 3, Gear = 2, Effective = 15, Unit = "%", Label = "parry",
                    Ceilings = new[] { ClassAbilityCeilings.Read(parry, "avoidance", 55, 1, v) },
                },
            };

            var online = ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req, v).Abilities.Single(a => a.Id == parry.Name);
            Assert.AreEqual(3.0, online.Readout.Affinity);
            Assert.AreEqual(2.0, online.Readout.Gear);
            var reading = online.Readout.Ceilings.Single();
            Assert.AreEqual("avoidance", reading.Id);
            Assert.AreEqual(55.0, reading.Current);
            Assert.AreEqual(50.0, reading.Max);
            Assert.IsTrue(reading.Binding);
            Assert.AreEqual(50.0, online.Ceilings[0].Max, "static ceilings ride along online too");

            var offline = ClassAbilityPanelModel.Build(subject, online: false, writeRefusal: null, Req, v).Abilities.Single(a => a.Id == parry.Name);
            Assert.IsNull(offline.Readout);
            Assert.AreEqual(50.0, offline.Ceilings[0].Max);
        }

        [TestMethod]
        public void Panel_UncappedReading_AndUncappedCeiling_SerializeMaxAsAbsent()
        {
            var parry = Def(ClassAbilityId.Parry);
            var zero = Dict("class_ability_avoidance_cap", 0);
            var reading = ClassAbilityCeilings.Read(parry, "avoidance", 55, 1, zero);

            var row = ClassAbilityPanelModel.ToReadoutRow(new ClassAbilityReadout { HasValue = true, Unit = "%", Ceilings = new[] { reading } }, parry);
            Assert.IsNull(row.Ceilings.Single().Max);
            Assert.IsNull(ClassAbilityPanelModel.CeilingRows(parry, zero)[0].Max);
        }
    }
}
