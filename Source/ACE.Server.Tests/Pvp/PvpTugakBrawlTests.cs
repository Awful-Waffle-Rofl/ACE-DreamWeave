using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The FFA arena mode is shown to players as "Tugak Brawl" (renamed 2026-10-06) and typed as "tugak", with "ffa" kept as
    /// an alias. The internal mode key ("ffa"), the "arena_ffa" ladder and stored template mode lists are unchanged.
    /// </summary>
    [TestClass]
    public class PvpTugakBrawlTests
    {
        private static readonly Regex OldName = new Regex(@"\b(ffa|free[- ]for[- ]all)\b", RegexOptions.IgnoreCase);

        // ---------------- command words: tugak is primary, ffa stays an alias, both mean the same mode ----------------

        [DataTestMethod]
        [DataRow("tugak")]
        [DataRow("TUGAK")]
        [DataRow(" Tugak ")]
        [DataRow("ffa")]
        [DataRow("FFA")]
        public void ArenaJoin_BothWords_ParseToTheSameInternalMode(string word)
        {
            var r = PvpArenaCommands.ParseJoinArena(new[] { word, "duo" });

            Assert.AreEqual(PvpArenaCommands.JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual(ArenaMapCatalog.FfaKey, r.ModeKey);
            Assert.AreEqual("ffa", r.ModeKey, "the internal key is unchanged");
        }

        [DataTestMethod]
        [DataRow("tugak")]
        [DataRow("ffa")]
        public void ArenaTop_BothWords_ParseToTheSameInternalMode_AndTheSameLadder(string word)
        {
            var r = PvpArenaCommands.ParseTop(new[] { word, "5" });

            Assert.AreEqual(PvpArenaCommands.TopParseOutcome.Ok, r.Outcome);
            Assert.AreEqual(ArenaMapCatalog.FfaKey, r.ModeKey);
            Assert.AreEqual("arena_ffa", PvpArenaCommands.LadderForMode(r.ModeKey), "the persisted ladder key is unchanged");
        }

        [TestMethod]
        public void CanonicalModeWord_MapsTugakToFfa_AndLeavesEverythingElseAlone()
        {
            Assert.AreEqual("ffa", ArenaMapCatalog.CanonicalModeWord("tugak"));
            Assert.AreEqual("ffa", ArenaMapCatalog.CanonicalModeWord("ffa"));
            Assert.AreEqual("1v1", ArenaMapCatalog.CanonicalModeWord(" 1V1 "));
            Assert.AreEqual("bg", ArenaMapCatalog.CanonicalModeWord("bg"));
            Assert.AreEqual("all", ArenaMapCatalog.CanonicalModeWord("all"));
            Assert.AreEqual("tugak", ArenaMapCatalog.JoinWord("ffa"));
            Assert.AreEqual("2v2", ArenaMapCatalog.JoinWord("2v2"));
            CollectionAssert.Contains(PvpArenaAdminCommands.ClearableModes, ArenaMapCatalog.CanonicalModeWord("tugak"), "/arenaadmin clearqueue tugak resolves to a clearable mode");
        }

        [TestMethod]
        public void TemplateModes_AcceptBothWords_StoreTheInternalKey_AndShowTugak()
        {
            Assert.AreEqual("ffa", PvpTemplateCatalog.CanonicalModeKey("tugak"));
            Assert.AreEqual("ffa", PvpTemplateCatalog.CanonicalModeKey("ffa"));
            CollectionAssert.AreEqual(new[] { "1v1", "ffa" }, PvpTemplateCatalog.ParseModes("tugak, 1v1").ToList(), "a stored column may carry either word");
            Assert.AreEqual("1v1,ffa,bg_koth", PvpTemplateCatalog.NormalizeModes("tugak,1v1,koth", out var e1), e1);
            Assert.AreEqual("1v1,ffa,bg_koth", PvpTemplateCatalog.NormalizeModes("ffa,1v1,koth", out var e2), e2, "the old word still works and stores the same value");
            StringAssert.Contains(PvpTemplateCatalog.ModeList(), "tugak");
            Assert.IsFalse(OldName.IsMatch(PvpTemplateCatalog.ModeList()), PvpTemplateCatalog.ModeList());
        }

        // ---------------- display ----------------

        [TestMethod]
        public void DisplayLabels_SayTugakBrawl()
        {
            Assert.AreEqual("Tugak Brawl", PvpArenaText.ModeLabel(ArenaMapCatalog.FfaKey));
            Assert.AreEqual("Tugak Brawl", PvpArenaText.CrierModeLabel(ArenaMapCatalog.FfaKey));
            StringAssert.Contains(PvpArenaText.Fill(PvpArenaText.FfaLobbyProgress, ("count", 3), ("needed", 5)), "Tugak Brawl");
            StringAssert.Contains(PvpArenaText.StatusIdle, "/arena join tugak");
        }

        // ---------------- the scan: no player-facing string still says FFA / Free-for-All ----------------

        private static IEnumerable<(string Where, string Text)> ConstStrings(Type type)
        {
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                yield return ($"{type.Name}.{f.Name}", (string)f.GetRawConstantValue());
        }

        private static IEnumerable<(string Where, string Text)> HandlerHelp(Type type)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                foreach (var a in m.GetCustomAttributes<CommandHandlerAttribute>())
                {
                    yield return ($"{type.Name}.{m.Name} description", a.Description);
                    yield return ($"{type.Name}.{m.Name} usage", a.Usage);
                }
        }

        [TestMethod]
        public void NoPlayerFacingString_StillSaysFfaOrFreeForAll()
        {
            var texts = new List<(string Where, string Text)>();

            foreach (var t in new[] { typeof(PvpArenaText), typeof(PvpArenaCommands), typeof(PvpArenaAdminCommands), typeof(PvpTemplateAdminCommands), typeof(BattlegroundText) })
            {
                texts.AddRange(ConstStrings(t));
                texts.AddRange(HandlerHelp(t));
            }

            texts.AddRange(HandlerHelp(typeof(PlayerCommands)).Where(x => x.Where.Contains("HandleTop")));

            // computed text: labels, the Crier lines for the FFA queue, the rating line, the admin mode list and its error
            texts.Add(("ModeLabel", PvpArenaText.ModeLabel(ArenaMapCatalog.FfaKey)));
            texts.Add(("CrierModeLabel", PvpArenaText.CrierModeLabel(ArenaMapCatalog.FfaKey)));
            texts.Add(("ModeList", PvpTemplateCatalog.ModeList()));
            PvpTemplateCatalog.NormalizeModes("nonsense", out var modesError);
            texts.Add(("NormalizeModes error", modesError));
            texts.Add(("RatingLineText", PvpArenaCommands.RatingLineText("Alice", null, null, null)));

            var crier = new PvpArenaCrier();
            var dials = PvpTunables.Defaults with { CrierEnabled = true, CrierAnnounceOnJoin = false, CrierIntervalSeconds = 10 };
            var t0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var queues = new[] { new CrierQueueSnapshot(ArenaMapCatalog.FfaKey, true, 2, 3) };
            crier.Tick(t0, dials, queues);
            foreach (var line in crier.Tick(t0.AddSeconds(10), dials, queues))
                texts.Add(("Crier periodic line", line));

            Assert.IsTrue(texts.Count > 100, "the scan must actually reach the text tables (found " + texts.Count + ")");

            var offenders = texts.Where(x => x.Text != null && OldName.IsMatch(x.Text)).Select(x => $"{x.Where}: {x.Text}").ToList();

            Assert.AreEqual(0, offenders.Count, "player-facing text still names the mode FFA / Free-for-All:\n" + string.Join("\n", offenders));
        }

        // ---------------- /top, admin echo ----------------

        [DataTestMethod]
        [DataRow("tugak", true)]
        [DataRow("TUGAK", true)]
        [DataRow("ffa", true)]
        [DataRow("1v1", true)]
        [DataRow("2v2", true)]
        [DataRow("bg", true)]
        [DataRow("dps", false)]
        [DataRow("", false)]
        [DataRow("all", false)]
        [DataRow("koth", false)]
        public void TopLadderWord_AcceptsBothFfaWords_AndOnlyLadders(string word, bool expected)
        {
            Assert.AreEqual(expected, PvpArenaCommands.IsLadderWord(word));
        }

        /// <summary>/top's ladder arm is exactly the helper (the old per-word case labels are gone), so removing the tugak handling fails here or above.</summary>
        [TestMethod]
        public void TopCommand_RoutesItsLadderArmThroughTheAliasAwareHelper()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir);

            var src = File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "Command", "Handlers", "PlayerCommands.cs"));

            StringAssert.Contains(src, "case var ladder when PvpArenaCommands.IsLadderWord(ladder):");
            Assert.IsFalse(src.Contains("case \"ffa\":"), "no per-word ladder label may bypass the helper");
        }

        private static PvpTemplateRecord Row(string modes) => new PvpTemplateRecord
        {
            TemplateKey = "duelist",
            DisplayName = "Duelist",
            Version = 1,
            Enabled = true,
            Modes = modes,
            DefinitionJson = ACE.Server.Pvp.Templates.PvpTemplateJson.SerializeDefinition(new ACE.Server.Pvp.Templates.PvpTemplateDefinition { Key = "duelist", Version = 1, DisplayName = "Duelist" }),
            SnapshotAt = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
            SnapshotBy = "test",
            SourceCharacterName = "Src",
        };

        [TestMethod]
        public void AdminEcho_ShowsTugak_NotTheInternalKey()
        {
            var row = Row("1v1,ffa,bg_koth");

            var list = PvpTemplateAdminCommands.ListText(new[] { row }, PvpTemplateStoreStatus.Ok);
            StringAssert.Contains(list, "modes 1v1,tugak,bg_koth");
            Assert.IsFalse(OldName.IsMatch(list), list);

            var inspect = PvpTemplateAdminCommands.InspectText(row);
            StringAssert.Contains(inspect, "modes 1v1,tugak,bg_koth");
            Assert.IsFalse(OldName.IsMatch(inspect), inspect);

            Assert.AreEqual("1v1,tugak", PvpTemplateCatalog.ModesDisplay("1v1,ffa"), "the /pvptemplate modes confirmation");
            Assert.AreEqual("none", PvpTemplateCatalog.ModesDisplay(""));
            StringAssert.Contains(PvpTemplateAdminCommands.WriteResultText("duelist", PvpTemplateStoreStatus.Ok, $"is now offered in: {PvpTemplateCatalog.ModesDisplay("ffa")}"), "tugak");
        }    }
}
