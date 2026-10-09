using System;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Task 8: the per-character telemetry projection (dungeon_run_participant / dungeon_run_group) and the
    /// per-character XP/luminance counters that feed it. Everything here is reachable without a world - the
    /// harness cannot construct a live Player - so these tests drive the pure run primitives
    /// (AddMemberXp/AddMemberLum, SnapshotRoster) and DungeonRunTelemetry.BuildRow directly, the same seam
    /// DungeonRunTelemetryTests already covers for the run-level columns.
    /// </summary>
    [TestClass]
    public class DungeonRunParticipantTelemetryTests
    {
        private static DungeonEntryDef NewDungeon() => new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };

        private static DungeonGemSpec NewSpec() => new DungeonGemSpec("filos_doom", 185, 6, "olthoi", 42, new (string, double)[0], 0, 0);

        private static ThreadDungeonRun NewSoloRun()
        {
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, NewSpec(), NewDungeon(),
                DateTime.UtcNow, TimeSpan.FromMinutes(180), ownerLevelAtStart: 180);
        }

        /// <summary>Owner guid 0x50000001, a higher-guid member and a lower-guid member, to pin roster order (invariant 4).</summary>
        private static ThreadDungeonRun NewGroupRun(out RosterSeat owner, out RosterSeat memberLow, out RosterSeat memberHigh)
        {
            owner = new RosterSeat(0x50000001u, "Owner", 7, 180);
            memberLow = new RosterSeat(0x50000002u, "Low", 8, 150);
            memberHigh = new RosterSeat(0x50000009u, "High", 9, 160);

            var seats = new[] { owner, memberHigh, memberLow }; // deliberately out of guid order; the ctor sorts non-owners
            var group = GroupScaling.Compute(3, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare,
                GroupScaling.DefaultGroupMaxMonsters, GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap,
                GroupScaling.DefaultRewardBonusPerMember, GroupScaling.DefaultRewardBonusCap, sharePlateau: 0);

            return new ThreadDungeonRun(0x80001235u, seats, 0x80000099u, NewSpec(), NewDungeon(),
                DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        // ---- solo (legacy ctor) ----

        [TestMethod]
        public void A_legacy_ctor_run_gives_one_owner_participant_row_and_a_null_group()
        {
            var run = NewSoloRun();

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 181);

            Assert.AreEqual(1, row.Participants.Count, "solo writes exactly one participant row (the owner)");
            Assert.IsNull(row.Group, "dungeon_run_group gets no row for a solo run");

            var owner = row.Participants[0];
            Assert.AreEqual(0x50000001u, owner.CharacterId);
            Assert.AreEqual("Tester", owner.Name);
            Assert.IsTrue(owner.IsOwner);
            Assert.AreEqual(180, owner.LevelStart);
            Assert.AreEqual(0, owner.PilesReceived);
            Assert.AreEqual(0, owner.PilesForfeited);
        }

        /// <summary>
        /// The owner's LevelEnd falls back to the level BuildRow already uses (charLevel) when the roster
        /// snapshot never recorded one (SetMemberLevelEnd was never called - the emitter's job, not this
        /// task's), so the participant row is never silently worse than the run-level column it already had.
        /// </summary>
        [TestMethod]
        public void Owner_LevelEnd_falls_back_to_charLevel_when_the_snapshot_has_zero()
        {
            var run = NewSoloRun();

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 199);

            Assert.AreEqual(199, row.Participants.Single().LevelEnd);
        }

        [TestMethod]
        public void Name_is_clipped_to_64_characters()
        {
            var owner = new RosterSeat(0x50000001u, new string('N', 100), 7, 180);
            var run = new ThreadDungeonRun(0x80001236u, new[] { owner }, 0x80000099u, NewSpec(), NewDungeon(),
                DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupScaling.Solo);

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 180);

            Assert.AreEqual(64, row.Participants.Single().Name.Length);
            Assert.AreEqual(new string('N', 64), row.Participants.Single().Name);
        }

        // ---- group ----

        [TestMethod]
        public void A_group_run_gives_per_member_XP_and_a_populated_group_row()
        {
            var run = NewGroupRun(out var owner, out var memberLow, out var memberHigh);

            run.AddMemberXp(owner.Guid, 1000);
            run.AddMemberXp(memberLow.Guid, 400);
            run.AddMemberXp(memberHigh.Guid, 250);
            run.AddMemberLum(memberLow.Guid, 30);

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 181);

            Assert.AreEqual(3, row.Participants.Count);
            Assert.AreEqual(1000L, row.Participants.Single(p => p.CharacterId == owner.Guid).XpGained);
            Assert.AreEqual(400L, row.Participants.Single(p => p.CharacterId == memberLow.Guid).XpGained);
            Assert.AreEqual(250L, row.Participants.Single(p => p.CharacterId == memberHigh.Guid).XpGained);
            Assert.AreEqual(30L, row.Participants.Single(p => p.CharacterId == memberLow.Guid).LumGained);

            Assert.IsNotNull(row.Group);
            Assert.AreEqual(run.Group.RosterSize, row.Group.RosterSize);
            Assert.AreEqual(run.Group.Effort, row.Group.Effort, 1e-9);
            Assert.AreEqual(run.GroupCountActual, row.Group.CountMult, 1e-9);
            Assert.AreEqual(run.Group.CountTarget, row.Group.CountMultTarget, 1e-9);
            Assert.AreEqual(run.GroupHealthMult, row.Group.HealthMult, 1e-9);
            Assert.AreEqual(run.Group.DamageRatingBonus, row.Group.DamageRatingBonus);
            Assert.AreEqual(run.Group.RewardBonus, row.Group.RewardBonus, 1e-9);
            Assert.AreEqual(run.Group.ShareTotal, row.Group.ShareTotal, 1e-9);
        }

        /// <summary>Invariant 4: owner first, then non-owner members by ascending character guid.</summary>
        [TestMethod]
        public void Participant_rows_are_in_roster_order()
        {
            var run = NewGroupRun(out var owner, out var memberLow, out var memberHigh);

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 181);

            CollectionAssert.AreEqual(
                new[] { owner.Guid, memberLow.Guid, memberHigh.Guid },
                row.Participants.Select(p => p.CharacterId).ToList());
        }

        /// <summary>
        /// AddMemberXp/AddMemberLum credit the SAME amount BankDungeonEarnings already banks onto the run
        /// total (run.AddXp/AddLum); they do not add a second grant. A solo run's single member must
        /// therefore see exactly the run total, never double it - this is the arithmetic the task brief calls
        /// out against BankDungeonEarnings.
        /// </summary>
        [TestMethod]
        public void Member_XP_tracks_the_run_total_without_doubling()
        {
            var run = NewSoloRun();

            // This is the exact pairing BankDungeonEarnings performs per grant: one AddXp for the run total,
            // one AddMemberXp for the same amount for the earning character.
            run.AddXp(500);
            run.AddMemberXp(run.OwnerGuid, 500);
            run.AddXp(250);
            run.AddMemberXp(run.OwnerGuid, 250);

            Assert.AreEqual(750L, run.XpEarned, "run total is unaffected by the new per-member counter");

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 181);

            Assert.AreEqual(750L, row.XpGained, "dungeon_run.xp_gained stays the run total (R25)");
            Assert.AreEqual(750L, row.Participants.Single().XpGained, "the sole member's share equals the run total, not double it");
        }

        /// <summary>AddMemberXp/AddMemberLum are a no-op for a guid that is not on the roster.</summary>
        [TestMethod]
        public void AddMemberXp_is_a_noop_for_a_non_member()
        {
            var run = NewSoloRun();

            run.AddMemberXp(0xDEADBEEFu, 999);
            run.AddMemberLum(0xDEADBEEFu, 999);

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(60), charLevel: 181);

            Assert.AreEqual(0L, row.Participants.Single().XpGained);
            Assert.AreEqual(0L, row.Participants.Single().LumGained);
        }
    }
}
