using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity.Facets;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// pvp_match_grant_spells (Docs/Pvp/TEMPLATES.md "Free spells for PvP"): the grant decision PvpMatchSpellGrant on fake spell
    /// lists and a fake known-set, the setting seam, the placement (grant before the template capture, proven against the retired bind-time order) and source scans naming each call site.
    /// </summary>
    [TestClass]
    public class PvpMatchSpellGrantTests
    {
        private static readonly uint Raven = (uint)SpellId.CurseRavenFury;

        private Func<bool> savedSetting;
        private Func<string, int, IReadOnlyList<uint>> savedList;

        [TestInitialize]
        public void Setup()
        {
            savedSetting = PvpArenaHookSettings.MatchGrantSpellsSource;
            savedList = PvpArenaHookSettings.MatchGrantSpellListSource;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.MatchGrantSpellsSource = savedSetting;
            PvpArenaHookSettings.MatchGrantSpellListSource = savedList;
        }

        private static IReadOnlyList<uint> Lists(string school, int level)
        {
            if (level != 7) return Array.Empty<uint>();
            return school == "war" ? new uint[] { 101, 102, 103 } : school == "life" ? new uint[] { 201, 202 } : Array.Empty<uint>();
        }

        private static int Run(bool enabled, HashSet<uint> known, List<uint> notified = null) =>
            PvpMatchSpellGrant.Grant(enabled, Lists, id => known.Add(id), id => notified?.Add(id));

        [TestMethod]
        public void GrantsMissingSpellsOnly_AndCountEqualsTheNewOnes()
        {
            var known = new HashSet<uint> { 101, 201 };
            var notified = new List<uint>();

            var taught = Run(true, known, notified);

            Assert.AreEqual(4, taught, "102, 103, 202 and Raven Fury");
            CollectionAssert.AreEquivalent(new uint[] { 102, 103, 202, Raven }, notified);
            Assert.IsTrue(new uint[] { 101, 102, 103, 201, 202, Raven }.All(known.Contains));
        }

        [TestMethod]
        public void GrantsNothingWhenAllKnown()
        {
            var known = new HashSet<uint> { 101, 102, 103, 201, 202, Raven };
            var notified = new List<uint>();

            Assert.AreEqual(0, Run(true, known, notified));
            Assert.AreEqual(0, notified.Count);
        }

        [TestMethod]
        public void RavenFury_IsAlwaysIncluded_EvenWithEmptyProfessorLists()
        {
            var ids = PvpMatchSpellGrant.SpellIds((s, l) => Array.Empty<uint>());

            CollectionAssert.AreEqual(new[] { Raven }, ids);
            CollectionAssert.Contains(PvpMatchSpellGrant.SpellIds(Lists), Raven);
        }

        [TestMethod]
        public void ReadsLevelSevenWarAndLifeOnly()
        {
            var asked = new List<(string, int)>();

            PvpMatchSpellGrant.SpellIds((s, l) => { asked.Add((s, l)); return Array.Empty<uint>(); });

            CollectionAssert.AreEqual(new[] { ("war", 7), ("life", 7) }, asked);
        }

        [TestMethod]
        public void SettingOff_GrantsNothing()
        {
            var known = new HashSet<uint>();

            Assert.AreEqual(0, Run(false, known));
            Assert.AreEqual(0, known.Count);
        }

        [TestMethod]
        public void ProductionRead_FallsBackToTrue_WhenPropertyManagerIsUnseeded()
        {
            // PropertyManager reads throw in a unit test with no seeded key; the production read must catch that and answer the default.
            Assert.IsTrue(PvpArenaHookSettings.MatchGrantSpellsDefault);
            Assert.IsTrue(PvpArenaHookSettings.ReadMatchGrantSpells());
        }

        [TestMethod]
        public void EmptyProfessorList_WarnsOncePerSchool_AndGrantsTheRest()
        {
            var warnings = new List<string>();
            var savedSink = PvpMatchSpellGrant.WarnSink;
            PvpMatchSpellGrant.WarnSink = warnings.Add;
            PvpMatchSpellGrant.ResetWarned();

            try
            {
                Func<string, int, IReadOnlyList<uint>> warOnly = (s, l) => s == "war" ? new uint[] { 101 } : Array.Empty<uint>();

                PvpMatchSpellGrant.SpellIds(warOnly);
                PvpMatchSpellGrant.SpellIds(warOnly);

                Assert.AreEqual(1, warnings.Count, "life is empty on both calls but warned once; war is populated and never warned");
                StringAssert.Contains(warnings[0], "life professor list is empty");

                PvpMatchSpellGrant.SpellIds((s, l) => Array.Empty<uint>());
                Assert.AreEqual(2, warnings.Count, "war now empty too: its first warning");
                StringAssert.Contains(warnings[1], "war professor list is empty");

                PvpMatchSpellGrant.ResetWarned();
                PvpMatchSpellGrant.SpellIds(Lists);
                Assert.AreEqual(2, warnings.Count, "populated lists never warn");
            }
            finally
            {
                PvpMatchSpellGrant.WarnSink = savedSink;
                PvpMatchSpellGrant.ResetWarned();
            }
        }

        [TestMethod]
        public void TaughtMessage_IsGrammaticalAtOne()
        {
            StringAssert.Contains(PvpMatchSpellGrant.TaughtMessage(1), "taught 1 War and Life Magic spell for PvP");
            StringAssert.Contains(PvpMatchSpellGrant.TaughtMessage(5), "taught 5 War and Life Magic spells for PvP");
        }

        // ---- placement: the grant must be in the spellbook BEFORE the template capture ----

        private static PvpTemplateDefinition TemplateNaming(params int[] spells)
        {
            var d = new PvpTemplateDefinition { Key = "warmage", Version = 1 };
            d.Spells.AddRange(spells);
            return d;
        }

        private static PvpTemplateRestoreRecord CaptureNow(Biota biota, PvpTemplateDefinition d) =>
            PvpTemplateOverlay.Capture(biota, new ReaderWriterLockSlim(), d, Guid.NewGuid(), DateTime.UtcNow, true, new List<FacetEquipEntry>());

        [TestMethod]
        public void GrantBeforeCapture_OverlappingTemplateSpellIsNotTemplateAdded_AndSurvivesTheRestore()
        {
            var rw = new ReaderWriterLockSlim();
            var biota = new Biota { PropertiesSpellBook = new Dictionary<int, float> { { 1, 2.0f } } };
            var d = TemplateNaming(102, 999);   // 102 is a granted spell the template also names; 999 is template-only

            // queue join: the grant runs first
            var granted = PvpMatchSpellGrant.Grant(true, Lists, id => { biota.PropertiesSpellBook[(int)id] = 2.0f; return true; }, _ => { });
            Assert.IsTrue(granted > 0);

            var record = PvpTemplateOverlay.Capture(biota, rw, d, Guid.NewGuid(), DateTime.UtcNow, true, new List<FacetEquipEntry>());

            CollectionAssert.AreEqual(new[] { 999 }, record.AddedSpells, "only the template-only spell is template-added; the granted 102 is already known");

            PvpTemplateOverlay.ApplyBuild(biota, rw, d);
            PvpTemplateOverlay.RestoreBuild(biota, rw, record);

            Assert.IsTrue(biota.PropertiesSpellBook.ContainsKey(102), "a granted spell the template also names is KEPT after the exit restore");
            Assert.IsFalse(biota.PropertiesSpellBook.ContainsKey(999), "a template-only spell is still removed at exit");
        }

        [TestMethod]
        public void GrantAfterCapture_BindTimeOrder_WouldLoseTheOverlappingSpellAtExit()
        {
            // The discriminating control: the retired bind-time order (apply, THEN grant). It must fail the assertion the
            // test above passes, proving that test's outcome depends on the grant running first.
            var rw = new ReaderWriterLockSlim();
            var biota = new Biota { PropertiesSpellBook = new Dictionary<int, float> { { 1, 2.0f } } };
            var d = TemplateNaming(102, 999);

            var record = PvpTemplateOverlay.Capture(biota, rw, d, Guid.NewGuid(), DateTime.UtcNow, true, new List<FacetEquipEntry>());
            PvpTemplateOverlay.ApplyBuild(biota, rw, d);
            PvpMatchSpellGrant.Grant(true, Lists, id => { biota.PropertiesSpellBook.TryAdd((int)id, 2.0f); return true; }, _ => { });

            CollectionAssert.Contains(record.AddedSpells, 102, "captured while the player lacked it, so the exit would remove it");

            PvpTemplateOverlay.RestoreBuild(biota, rw, record);

            Assert.IsFalse(biota.PropertiesSpellBook.ContainsKey(102), "bind-time order loses the granted spell at exit - the bug the placement avoids");
        }

        [TestMethod]
        public void SettingOff_GrantsNothingBeforeCapture()
        {
            var biota = new Biota { PropertiesSpellBook = new Dictionary<int, float>() };

            Assert.AreEqual(0, PvpMatchSpellGrant.Grant(false, Lists, id => { biota.PropertiesSpellBook[(int)id] = 2.0f; return true; }, _ => { }));
            CollectionAssert.AreEquivalent(new[] { 102, (int)SpellId.CurseRavenFury }, CaptureNow(biota, TemplateNaming(102)).AddedSpells,
                "nothing granted, so both the template spell and the always-granted Raven Fury are template-added");
        }

        // ---- call sites (source scans, each BOUND to the method it names) ----

        [TestMethod]
        public void QueueJoinEntryPoint_CallsTheGrant_OnlyForAJoinThatQueued()
        {
            var src = ReadSource("Pvp", "PvpMatchManager.cs");
            var body = MethodBody(src, "public static PvpJoinResult Join(");

            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(body, @"player\.GrantPvpMatchSpells\(\)").Count);
            StringAssert.Contains(body, "if (result.Refusal == PvpJoinRefusal.None)\n                player.GrantPvpMatchSpells();");
            Assert.IsTrue(body.IndexOf("coordinator?.Join(", StringComparison.Ordinal) < body.IndexOf("player.GrantPvpMatchSpells()", StringComparison.Ordinal),
                "the grant follows the coordinator's verdict, so a refused join teaches nothing");
        }

        [TestMethod]
        public void EveryQueueJoin_GoesThroughPvpMatchManagerJoin()
        {
            // The single call site: /arena join (1v1, 2v2, duo, tugak, bg, bg group) is HandleJoinArena -> PvpMatchManager.Join.
            // If a second caller appears it must also be covered (or route through Join), so fail until this list is updated.
            var root = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(FindFile("Source", "ACE.Server", "Pvp", "PvpMatchManager.cs"))).Parent.FullName;
            var callers = System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories)
                .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar) && !f.Contains(System.IO.Path.DirectorySeparatorChar + "bin" + System.IO.Path.DirectorySeparatorChar))
                .Where(f => System.IO.File.ReadAllText(f).Contains("PvpMatchManager.Join("))
                .Select(System.IO.Path.GetFileName).ToList();

            CollectionAssert.AreEqual(new[] { "PvpArenaCommands.cs" }, callers);
        }

        [TestMethod]
        public void TemplateApply_GrantsBeforeCapture_AndAfterTheAlreadyTemplatedRefusal()
        {
            var body = MethodBody(ReadSource("WorldObjects", "Player_PvpTemplate.cs"), "internal PvpTemplateApplyResult ApplyPvpTemplateNow(");

            var refuse = body.IndexOf("if (IsPvpTemplated)", StringComparison.Ordinal);
            var grant = body.IndexOf("GrantPvpMatchSpells();", StringComparison.Ordinal);
            var capture = body.IndexOf("PvpTemplateOverlay.Capture(", StringComparison.Ordinal);

            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(body, @"GrantPvpMatchSpells\(\)").Count);
            Assert.IsTrue(refuse >= 0 && refuse < grant, "never granted while a restore record exists (mid-match)");
            Assert.IsTrue(grant < capture, "the grant runs before the spellbook is captured");
        }

        [TestMethod]
        public void MatchBind_NoLongerGrants()
        {
            var body = MethodBody(ReadSource("WorldObjects", "Player_PvpArena.cs"), "internal void EnterPvpMatchNow(");

            Assert.IsFalse(body.Contains("GrantPvpMatchSpells"), "a bind-time grant runs after the template capture and would be removed at exit");
        }

        private static string ReadSource(params string[] underServer) =>
            System.IO.File.ReadAllText(FindFile(new[] { "Source", "ACE.Server" }.Concat(underServer).ToArray())).Replace("\r\n", "\n");

        /// <summary>The text from <paramref name="signature"/> to the next method-level closing brace at 8 spaces.</summary>
        private static string MethodBody(string src, string signature)
        {
            var a = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(a >= 0, $"{signature} not found");
            var b = src.IndexOf("\n        }\n", a, StringComparison.Ordinal);
            Assert.IsTrue(b > a, $"end of {signature} not found");
            return src.Substring(a, b - a);
        }
        private static string FindFile(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, System.IO.Path.Combine(parts));
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            Assert.Fail($"Could not find {string.Join("/", parts)} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}