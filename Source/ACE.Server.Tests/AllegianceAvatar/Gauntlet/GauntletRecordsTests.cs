using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    [TestClass]
    public class GauntletRecordsTests
    {
        private static readonly DateTime T0 = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        private List<AllegianceGauntletRecord> persisted;
        private List<string> broadcasts;
        private List<(uint Monarch, string Text)> allegianceLines;
        private Dictionary<uint, (string, string)> names;

        [TestInitialize]
        public void Setup()
        {
            persisted = new();
            broadcasts = new();
            allegianceLines = new();
            names = new();

            GauntletRecords.Loader = () => new List<AllegianceGauntletRecord>();
            GauntletRecords.Persist = row => persisted.Add(row);
            GauntletRecords.Broadcast = text => broadcasts.Add(text);
            GauntletRecords.ToAllegiance = (m, t) => allegianceLines.Add((m, t));
            GauntletRecords.NameSource = id => names.TryGetValue(id, out var n) ? n : (null, null);
            GauntletRecords.Initialize();
        }

        [TestCleanup]
        public void Cleanup() => GauntletRecords.ResetForTests();

        private static AllegianceGauntletRecord Row(uint id, uint centi, DateTime? at, ushort fought = 5) =>
            new() { AvatarId = id, BestCenti = centi, BestParticipants = fought, BestAt = at };

        [TestMethod]
        public void Order_ScoreDesc_ThenEarlierBestAt()
        {
            var ordered = GauntletRecords.Order(new[]
            {
                Row(1, 500, T0.AddHours(2)),
                Row(2, 900, T0.AddHours(5)),
                Row(3, 500, T0.AddHours(1)),
                Row(4, 900, T0.AddHours(3)),
            }).Select(r => r.AvatarId).ToArray();

            CollectionAssert.AreEqual(new uint[] { 4, 2, 3, 1 }, ordered);

            // The same order through the loaded cache, which also drops an Avatar with no banked score.
            GauntletRecords.Loader = () => new List<AllegianceGauntletRecord>
            {
                Row(1, 500, T0.AddHours(2)), Row(2, 900, T0.AddHours(5)), Row(3, 500, T0.AddHours(1)), Row(4, 900, T0.AddHours(3)), Row(5, 0, null),
            };
            GauntletRecords.Initialize();

            CollectionAssert.AreEqual(new uint[] { 4, 2, 3, 1 }, GauntletRecords.Board().Select(r => r.AvatarId).ToArray());
        }

        [TestMethod]
        public void Announce_StrictlyAboveRunStartSnapshot_Once()
        {
            GauntletRecords.Bank(1, 1000, 10, 1, T0);
            var snapshot = GauntletRecords.SnapshotTop();
            Assert.AreEqual(1000L, snapshot);

            // A rival sets a higher record mid-run: the snapshot is the value captured at Active(1), not the live top.
            GauntletRecords.Bank(2, 1200, 8, 2, T0);

            // Tie with the snapshot: no announcement.
            Assert.IsFalse(GauntletRecords.TryAnnounce(10, 3, 333, 1000, snapshot, 6));
            Assert.AreEqual(0, broadcasts.Count);

            // Strictly above the snapshot announces, even though the live top has since moved past it.
            Assert.IsTrue(GauntletRecords.TryAnnounce(11, 3, 333, 1101, snapshot, 6));
            Assert.AreEqual(1, broadcasts.Count);

            // At most once per run.
            Assert.IsFalse(GauntletRecords.TryAnnounce(11, 3, 333, 1101, snapshot, 6));
            Assert.AreEqual(1, broadcasts.Count);
            Assert.AreEqual(2, allegianceLines.Count(l => l.Monarch == 333), "one result line per run, none on the repeat");

            // A zero score never announces against an empty board.
            Assert.IsFalse(GauntletRecords.TryAnnounce(12, 4, 444, 0, 0, 0));
        }

        [TestMethod]
        public void Announcement_AndResultLine_ExactAsciiText()
        {
            names[9] = ("Dawn Guard", "Aria");
            GauntletRecords.TryAnnounce(1, 9, 99, 1347, 0, 24);

            Assert.AreEqual("[Allegiance Gauntlet] Dawn Guard has fought through wave 13.47 - a new record!", broadcasts.Single());
            Assert.AreEqual("Your Allegiance Gauntlet ended at wave 13.47. 24 fought.", allegianceLines.Single().Text);
            Assert.IsTrue(broadcasts.Concat(allegianceLines.Select(l => l.Text)).All(t => t.All(c => c < 128)));
        }

        [TestMethod]
        public void Row_FallsBackToMonarchName()
        {
            Assert.AreEqual("Dawn Guard", GauntletRecords.ChooseLabel("Dawn Guard", "Aria"));
            Assert.AreEqual("Aria's allegiance", GauntletRecords.ChooseLabel(null, "Aria"));
            Assert.AreEqual("Aria's allegiance", GauntletRecords.ChooseLabel("  ", "Aria"));
            Assert.AreEqual("a former allegiance", GauntletRecords.ChooseLabel(null, null));

            names[1] = (null, "Aria");
            GauntletRecords.Bank(1, 500, 3, 1, T0);
            GauntletRecords.Bank(2, 400, 3, 2, T0);

            var board = GauntletRecords.Board();
            Assert.AreEqual("Aria's allegiance", board[0].Name);
            Assert.AreEqual("a former allegiance", board[1].Name);
        }

        [TestMethod]
        public void Board_RendersParticipantCount()
        {
            names[1] = ("Dawn Guard", null);
            GauntletRecords.Bank(1, 1347, 24, 1, T0);

            var text = GauntletRecords.RenderBoard(GauntletRecords.Board());

            StringAssert.Contains(text, "1. Dawn Guard - Wave 13.47 (24 fought)");
            Assert.AreEqual("Dawn Guard - Wave 13.47 (24 fought)", GauntletRecords.RowText("Dawn Guard", 1347, 24));
            StringAssert.Contains(GauntletRecords.RenderBoard(new List<GauntletBoardRow>()), "No Allegiance Gauntlet runs");
        }

        [TestMethod]
        public void Bank_OnlyImprovesBest_TieKeepsEarlier_PersistsAbsolute()
        {
            Assert.IsTrue(GauntletRecords.Bank(1, 700, 5, 10, T0));
            Assert.IsFalse(GauntletRecords.Bank(1, 700, 9, 11, T0.AddHours(1)), "a tie keeps the earlier best");
            Assert.IsFalse(GauntletRecords.Bank(1, 300, 9, 12, T0.AddHours(2)));
            Assert.IsTrue(GauntletRecords.Bank(1, 800, 6, 13, T0.AddHours(3)));

            Assert.AreEqual(2, persisted.Count);
            Assert.AreEqual(800u, persisted[1].BestCenti);
            Assert.AreEqual((ushort)6, persisted[1].BestParticipants);
            Assert.AreEqual(13u, persisted[1].BestRunId);
        }

        [TestMethod]
        public void Bank_CarriesCooldownFromNoteRunCommitted()
        {
            GauntletRecords.NoteRunCommitted(1, T0);
            GauntletRecords.Bank(1, 500, 4, 1, T0.AddMinutes(30));

            var last = persisted.Last();
            Assert.AreEqual(T0, last.LastRunAt);
            Assert.AreEqual(1u, last.RunCount);
            Assert.AreEqual(500u, last.BestCenti);
        }

        [TestMethod]
        public void FailedLoad_RefusesBank_SoAStoredBestIsNeverOverwritten()
        {
            GauntletRecords.Loader = () => null;
            GauntletRecords.Initialize();

            Assert.IsFalse(GauntletRecords.Bank(1, 500, 4, 1, T0));
            Assert.AreEqual(0, persisted.Count);
        }
    }
}
