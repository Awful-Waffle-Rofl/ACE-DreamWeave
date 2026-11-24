using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins /facet reset: the marker row, how TrySwitchFacet reads it, and the command parsing.
    ///
    /// No Player can be constructed here, so nothing below runs TrySwitchFacet or TryResetFacet. The pure
    /// pieces they are built from are tested directly, and the two source scans are PROXIES for the wiring
    /// (the calls are written in the right order), not proof that it runs. Nothing here reads
    /// PropertyManager, FacetTunables.DialSource or PkRulesSource, so there is no process-static state to
    /// seed or restore.
    /// </summary>
    [TestClass]
    public class FacetResetEntryTests
    {
        private static CharacterFacet RealRow(int slot = 2, string name = "Void")
        {
            return new CharacterFacet
            {
                CharacterId = 0x50000001,
                Slot = (byte)slot,
                Name = name,
                SkillsJson = FacetSnapshot.SerializeSkills(new List<FacetSkillEntry>
                {
                    new FacetSkillEntry { Skill = ACE.Entity.Enum.Skill.WarMagic, Sac = ACE.Entity.Enum.SkillAdvancementClass.Specialized, Ranks = 10, Pp = 500, InitLevel = 10 },
                }),
                AbilitiesJson = FacetSnapshot.SerializeAbilities(new Dictionary<string, int> { ["SomeAbility"] = 2 }),
                EquipJson = FacetSnapshot.SerializeEquip(new List<FacetEquipEntry> { new FacetEquipEntry { Guid = 1, Wcid = 2, Slot = 4 } }),
                AttrsJson = "{}",
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
        }

        private static CharacterFacet MarkerRow(int slot = 2, string name = "Void")
            => Player.BuildFacetResetRow(0x50000001, slot, name, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));

        // ---- IsFreshTarget ----------------------------------------------------------------------

        [TestMethod]
        public void IsFreshTarget_NullAndMarker_AreFresh_RealRowIsNot()
        {
            Assert.IsTrue(Player.IsFreshTarget(null));
            Assert.IsTrue(Player.IsFreshTarget(MarkerRow()));
            Assert.IsFalse(Player.IsFreshTarget(RealRow()), "CONTROL: a real build is never fresh");
        }

        [TestMethod]
        public void IsFreshTarget_AnEmptyBuildArray_IsARealRowNotAMarker()
        {
            var row = RealRow();
            row.SkillsJson = "[]";

            Assert.IsFalse(Player.IsFreshTarget(row));
        }

        [TestMethod]
        public void IsResetMarker_IsAnExactOrdinalCompare()
        {
            Assert.IsTrue(FacetSnapshot.IsResetMarker(FacetSnapshot.ResetMarkerSkillsJson));
            Assert.IsFalse(FacetSnapshot.IsResetMarker(null));
            Assert.IsFalse(FacetSnapshot.IsResetMarker(""));
            Assert.IsFalse(FacetSnapshot.IsResetMarker(" " + FacetSnapshot.ResetMarkerSkillsJson));
            Assert.IsFalse(FacetSnapshot.IsResetMarker("{\"FACETRESET\":1}"));
        }

        // ---- the fail-safe ----------------------------------------------------------------------

        [TestMethod]
        public void TryDeserializeSkills_OfTheMarker_ReturnsFalse()
        {
            Assert.IsFalse(FacetSnapshot.TryDeserializeSkills(FacetSnapshot.ResetMarkerSkillsJson, out var skills));
            Assert.IsNull(skills);
        }

        // ---- the fresh plan ---------------------------------------------------------------------

        [TestMethod]
        public void PlanFreshFacetIncoming_MarkerAndNull_DifferOnlyByName()
        {
            var fresh = new List<FacetSkillEntry> { new FacetSkillEntry { Skill = ACE.Entity.Enum.Skill.Salvaging } };

            var fromNull = Player.PlanFreshFacetIncoming(null, fresh);
            var fromMarker = Player.PlanFreshFacetIncoming(MarkerRow(name: "Void"), fresh);

            Assert.AreSame(fromNull.Skills, fromMarker.Skills);
            Assert.AreEqual(0, fromNull.Abilities.Count);
            Assert.AreEqual(0, fromMarker.Abilities.Count);
            Assert.AreEqual(0, fromNull.Equip.Count);
            Assert.AreEqual(0, fromMarker.Equip.Count);

            Assert.IsNull(fromNull.Name);
            Assert.AreEqual("Void", fromMarker.Name);
        }

        // ---- the marker row ---------------------------------------------------------------------

        [TestMethod]
        public void BuildFacetResetRow_SetsEveryColumn()
        {
            var now = new DateTime(2026, 3, 3, 12, 0, 0, DateTimeKind.Utc);
            var row = Player.BuildFacetResetRow(0x50000009, 3, "Kept Name", now);

            Assert.AreEqual(0x50000009u, row.CharacterId);
            Assert.AreEqual((byte)3, row.Slot);
            Assert.AreEqual("Kept Name", row.Name);
            Assert.AreEqual("{\"facetReset\":1}", row.SkillsJson);
            Assert.AreEqual(FacetSnapshot.SerializeAbilities(new Dictionary<string, int>()), row.AbilitiesJson);
            Assert.AreEqual(FacetSnapshot.SerializeEquip(new List<FacetEquipEntry>()), row.EquipJson);
            Assert.IsNull(row.AttrsJson);
            Assert.AreEqual(now, row.UpdatedAt);
        }

        [TestMethod]
        public void BuildFacetResetRow_PkSlotAndNullName_Survive()
        {
            var row = Player.BuildFacetResetRow(1, Player.PkFacetSlot, null, DateTime.UtcNow);

            Assert.AreEqual(Player.PkFacetSlot, row.Slot);
            Assert.IsNull(row.Name);
            Assert.IsTrue(FacetSnapshot.IsResetMarker(row.SkillsJson));
        }

        // ---- readers that must skip or leave the marker -------------------------------------------

        [TestMethod]
        public void SummarizeStoredFacetRows_SkipsAMarkerRow_KeepsARealOne()
        {
            var summary = Player.SummarizeStoredFacetRows(new[] { RealRow(slot: 2), MarkerRow(slot: 3) });

            Assert.IsTrue(summary.ContainsKey(2), "CONTROL: the real row is summarized");
            Assert.IsFalse(summary.ContainsKey(3), "a marker row holds no build to be warned about");
        }

        [TestMethod]
        public void TryClearStoredAbilities_LeavesAMarkerRowUntouched()
        {
            var marker = MarkerRow();
            var before = marker.UpdatedAt;
            var writes = new List<CharacterFacet>();

            var ok = ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                new List<CharacterFacet> { marker }, 1000, (row, done) => { writes.Add(row); done(true); }, out var cleared);

            Assert.IsTrue(ok);
            Assert.AreEqual(0, cleared);
            Assert.AreEqual(0, writes.Count);
            Assert.AreEqual(before, marker.UpdatedAt);
            Assert.AreEqual(FacetSnapshot.ResetMarkerSkillsJson, marker.SkillsJson);
        }

        [TestMethod]
        public void TryClearStoredAbilities_Control_ARealRowWithAbilitiesIsWritten()
        {
            var writes = new List<CharacterFacet>();

            ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                new List<CharacterFacet> { RealRow() }, 1000, (row, done) => { writes.Add(row); done(true); }, out var cleared);

            Assert.AreEqual(1, cleared);
            Assert.AreEqual(1, writes.Count);
        }

        [TestMethod]
        public void TryStripStoredAbilities_LeavesAMarkerRowUntouched()
        {
            var marker = MarkerRow();
            var writes = 0;

            var ok = ClassAbilityRepricing.TryStripStoredAbilities(
                new List<CharacterFacet> { marker }, new[] { "SomeAbility" }, 1000, (row, done) => { writes++; done(true); },
                out var strippedRows, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(0, strippedRows);
            Assert.AreEqual(0, writes);
            Assert.AreEqual(FacetSnapshot.ResetMarkerSkillsJson, marker.SkillsJson);
        }

        // ---- command parsing and text -------------------------------------------------------------

        [TestMethod]
        public void TryParseResetArgs_AcceptsTwoAndPk()
        {
            Assert.IsTrue(FacetCommands.TryParseResetArgs(new[] { "2" }, out var slot, out var refusal));
            Assert.AreEqual(2, slot);
            Assert.IsNull(refusal);

            Assert.IsTrue(FacetCommands.TryParseResetArgs(new[] { "PK" }, out slot, out _));
            Assert.AreEqual((int)Player.PkFacetSlot, slot);
        }

        [TestMethod]
        public void TryParseResetArgs_RefusesSlotOne_WithTheMainFacetLine()
        {
            Assert.IsFalse(FacetCommands.TryParseResetArgs(new[] { "1" }, out _, out var refusal));
            Assert.AreEqual("Your main facet cannot be reset.", refusal);
        }

        [TestMethod]
        public void TryParseResetArgs_RefusesTheReservedNumberNoArgAndExtraArgs_WithUsage()
        {
            foreach (var args in new[] { new[] { "100" }, new string[0], new[] { "abc" }, new[] { "2", "3" } })
            {
                Assert.IsFalse(FacetCommands.TryParseResetArgs(args, out var slot, out var refusal), string.Join(" ", args));
                Assert.AreEqual(0, slot);
                Assert.AreEqual(FacetCommands.ResetUsageLine, refusal);
            }
        }

        [TestMethod]
        public void ResetTexts_AreTheOwnerWording()
        {
            Assert.AreEqual(
                "Reset facet 2? Its skills, class abilities and remembered gear will be cleared, and the next time you turn to it you will start from a fresh build, as on a first visit. Its name is kept. Experience, items and augmentations are not affected.",
                FacetCommands.ComposeResetPrompt(2));
            Assert.AreEqual("Facet 2 has been reset. Turn to it to cut a fresh build.", FacetCommands.ComposeResetSuccess(2));
            Assert.AreEqual("The PK facet has been reset. Turn to it to cut a fresh build.", FacetCommands.ComposeResetSuccess(Player.PkFacetSlot));
            Assert.AreEqual("You are using facet 2 - turn to another facet first, then reset it.", Player.ActiveSlotResetRefusal(2));
        }

        [TestMethod]
        public void ComposeFirstVisitPrompt_AfterReset_IsPrefixed_AndOtherwiseIdentical()
        {
            var plain = FacetCommands.ComposeFirstVisitPrompt(2);
            var afterReset = FacetCommands.ComposeFirstVisitPrompt(2, afterReset: true);

            Assert.AreEqual("Facet 2 has been reset. " + plain, afterReset);

            var plainPk = FacetCommands.ComposeFirstVisitPrompt(Player.PkFacetSlot);
            Assert.AreEqual("The PK facet has been reset. " + plainPk, FacetCommands.ComposeFirstVisitPrompt(Player.PkFacetSlot, true));
        }

        [TestMethod]
        public void BareList_ShowsAMarkerRowLikeANeverCutSlot_SourceScan()
        {
            // PROXY: HandleList must route a marker row to the "not yet cut" summary rather than to
            // DeserializeSkills, which would read the marker as an empty build and print "no specialized skills".
            var text = ReadRepoFile("Source", "ACE.Server", "Command", "Handlers", "FacetCommands.cs");
            var list = text.Substring(text.IndexOf("private static void HandleList(", StringComparison.Ordinal));

            StringAssert.Contains(list, "row != null && !FacetSnapshot.IsResetMarker(row.SkillsJson)");
        }

        // ---- source scan: the order inside TrySwitchFacet -----------------------------------------

        [TestMethod]
        public void TrySwitchFacet_ChecksIsFreshTarget_BeforeTryDeserializeSkills_SourceScan()
        {
            var text = ReadRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Facets.cs");
            var start = text.IndexOf("public bool TrySwitchFacet(", StringComparison.Ordinal);

            Assert.IsTrue(start >= 0, "anchor moved: this is not evidence about behaviour");

            var body = text.Substring(start);
            var confirm = body.IndexOf("if (IsFreshTarget(targetRow) && !confirmed)", StringComparison.Ordinal);
            var branch = body.IndexOf("if (!IsFreshTarget(targetRow))", StringComparison.Ordinal);
            var deserialize = body.IndexOf("FacetSnapshot.TryDeserializeSkills(", StringComparison.Ordinal);

            Assert.IsTrue(confirm >= 0, "the first-visit confirmation must use IsFreshTarget");
            Assert.IsTrue(branch >= 0, "the stored-vs-fresh branch must use IsFreshTarget");
            Assert.IsTrue(deserialize >= 0);
            Assert.IsTrue(confirm < deserialize, "the confirmation test must come before the deserialize");
            Assert.IsTrue(branch < deserialize, "the marker branch must come before the deserialize");
            Assert.IsFalse(body.Substring(0, deserialize).Contains("targetRow == null"), "no raw null test may precede the marker-aware one");
        }

        // ---- wiring pins (source scans; PROXIES - they prove the calls are written in order, not that they run) ----

        private static string MethodBody(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"could not find `{signature}` - the anchor moved, this is not evidence about behaviour");

            var open = text.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(at, i - at + 1);
            }

            Assert.Fail($"unbalanced braces after `{signature}`");
            return null;
        }

        /// <summary>Failures of the three wiring pins against Player_Facets.cs text. Empty means all pins hold.</summary>
        private static List<string> WiringPinFailures(string playerFacets)
        {
            var failures = new List<string>();

            // TryResetFacet: the steps, in order, up to the success return.
            var reset = MethodBody(playerFacets, "public bool TryResetFacet(");
            reset = reset.Substring(0, reset.IndexOf("return true;", StringComparison.Ordinal));

            var markers = new[]
            {
                "CheckFacetResetGates(", "TryGetCharacterFacetRows(", "FacetSnapshot.IsResetMarker(existing.SkillsJson)",
                "log.Info(", "TrySaveAll(", "RemoveFacetStoredSummary(", "BeginFacetSwitchRequest(",
            };

            var last = -1;

            foreach (var marker in markers)
            {
                var at = reset.IndexOf(marker, StringComparison.Ordinal);

                if (at < 0 || at < last)
                {
                    failures.Add("order:" + marker);
                    break;
                }

                last = at;
            }

            // CheckFacetResetGates: both slot refusals live in the gate.
            var gates = MethodBody(playerFacets, "internal bool CheckFacetResetGates(");

            if (!gates.Contains("slot == 1") || !gates.Contains("MainFacetResetRefusal"))
                failures.Add("gate:slot1");

            if (!gates.Contains("slot == ActiveFacetSlot"))
                failures.Add("gate:active");

            // TrySwitchFacet: the plan reads the target row, and the prompt flag is recorded.
            var sw = MethodBody(playerFacets, "public bool TrySwitchFacet(");

            if (!sw.Contains("PlanFreshFacetIncoming(targetRow,"))
                failures.Add("switch:plan");

            if (!sw.Contains("FacetConfirmTargetWasReset ="))
                failures.Add("switch:flag");

            return failures;
        }

        [TestMethod]
        public void WiringPins_HoldOnTheRealSource()
        {
            var failures = WiringPinFailures(ReadRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Facets.cs"));

            Assert.AreEqual(0, failures.Count, string.Join(", ", failures));
        }

        [TestMethod]
        public void WiringPins_Discriminate_EachMutationFailsExactlyItsOwnPin()
        {
            // Mutations are applied to an in-memory COPY of the source text; the committed tree is never touched.
            var source = ReadRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Facets.cs");

            var cases = new (string from, string to, string expected)[]
            {
                ("log.Info($\"[FACET] {Name} (0x{Guid.Full:X8}): resetting slot", "log.Debug($\"[FACET] {Name} (0x{Guid.Full:X8}): resetting slot", "order:log.Info("),
                ("            RemoveFacetStoredSummary(slot);\r\n", "", "order:RemoveFacetStoredSummary("),
                ("            BeginFacetSwitchRequest();\r\n\r\n            refusal = null;", "            refusal = null;", "order:BeginFacetSwitchRequest("),
                ("if (!CheckFacetResetGates(slot, out refusal))", "if (false)", "order:CheckFacetResetGates("),
                ("                refusal = MainFacetResetRefusal;", "                refusal = \"\";", "gate:slot1"),
                ("            if (slot == ActiveFacetSlot)\r\n            {\r\n                refusal = ActiveSlotResetRefusal(slot);\r\n                return false;\r\n            }\r\n\r\n            return true;", "            return true;", "gate:active"),
                ("PlanFreshFacetIncoming(targetRow,", "PlanFreshFacetIncoming(null,", "switch:plan"),
                ("FacetConfirmTargetWasReset = targetRow", "FacetConfirmTargetWasRese_ = targetRow", "switch:flag"),
            };

            // The source on disk may be LF or CRLF; the mutations above are written for CRLF.
            var crlf = source.Replace("\r\n", "\n").Replace("\n", "\r\n");

            foreach (var (from, to, expected) in cases)
            {
                Assert.IsTrue(crlf.Contains(from), $"mutation target not found, so it proves nothing: {expected}");

                var failures = WiringPinFailures(crlf.Replace(from, to));

                CollectionAssert.AreEqual(new[] { expected }, failures, $"mutation for {expected}");
            }
        }

        private static string ReadRepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, Path.Combine(relative)));
        }
    }
}
