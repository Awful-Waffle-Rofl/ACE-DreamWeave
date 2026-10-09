using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The pure crystal damage-permission decision (Docs/Pvp/ATTACK-DEFEND.md "Who may damage it"): ONLY a harmful action by a player
    /// bound to the same match, Live and not respawning, on a team other than the defending one is allowed.
    /// </summary>
    [TestClass]
    public class BattlegroundObjectiveGateTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch NewMatch() =>
            new PvpMatch(Guid.NewGuid(), BattlegroundModes.AttackDefendModeKey, new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            }, Now);

        private static BattlegroundObjectiveTag Tag(PvpMatch m, int index = 0) => new BattlegroundObjectiveTag(m.MatchId, index, DefendingTeam: 1);

        private static PvpPlayerBinding Binding(PvpMatch m, int team, PvpMatchState state = PvpMatchState.Live, bool respawning = false) =>
            new PvpPlayerBinding(m, team, state, true, true, true, true, true, respawning: respawning);

        [TestMethod]
        public void NoTag_IsNotACrystal()
        {
            var m = NewMatch();

            Assert.AreEqual(ObjectiveGateReason.NoTag, BattlegroundObjectiveGate.Evaluate(null, Binding(m, 0), harmful: true));
        }

        [TestMethod]
        public void UnboundActor_IsRefused()
        {
            var m = NewMatch();

            Assert.AreEqual(ObjectiveGateReason.ActorUnbound, BattlegroundObjectiveGate.Evaluate(Tag(m), null, harmful: true));
            Assert.AreEqual(ObjectiveGateReason.ActorUnbound, BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(null, 0), harmful: true), "a binding that carries no match is unbound too");
        }

        [TestMethod]
        public void ActorInAnotherMatch_IsRefused()
        {
            var mine = NewMatch();
            var other = NewMatch();

            Assert.AreEqual(ObjectiveGateReason.OtherMatch, BattlegroundObjectiveGate.Evaluate(Tag(mine), Binding(other, 0), harmful: true));
        }

        [TestMethod]
        public void MatchNotLive_IsRefused()
        {
            var m = NewMatch();

            foreach (var state in new[] { PvpMatchState.Staging, PvpMatchState.Countdown, PvpMatchState.Resolving, PvpMatchState.Closed })
                Assert.AreEqual(ObjectiveGateReason.MatchNotLive, BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(m, 0, state), harmful: true), state.ToString());
        }

        [TestMethod]
        public void RespawningActor_IsRefused()
        {
            var m = NewMatch();

            Assert.AreEqual(ObjectiveGateReason.ActorRespawning, BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(m, 0, respawning: true), harmful: true));
        }

        [TestMethod]
        public void DefendingTeam_IsRefused_HarmfulOrNot()
        {
            var m = NewMatch();

            Assert.AreEqual(ObjectiveGateReason.OwnTeam, BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(m, 1), harmful: true));
            Assert.AreEqual(ObjectiveGateReason.OwnTeam, BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(m, 1), harmful: false), "a defender's heal is refused as the own-team case");
        }

        [TestMethod]
        public void AttackingTeam_Harmful_IsAllowed()
        {
            var m = NewMatch();
            var reason = BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(m, 0), harmful: true);

            Assert.AreEqual(ObjectiveGateReason.Allowed, reason);
            Assert.IsTrue(BattlegroundObjectiveGate.IsAllowed(reason));
        }

        [TestMethod]
        public void AttackingTeam_Beneficial_IsRefused()
        {
            var m = NewMatch();
            var reason = BattlegroundObjectiveGate.Evaluate(Tag(m), Binding(m, 0), harmful: false);

            Assert.AreEqual(ObjectiveGateReason.NotHarmful, reason);
            Assert.IsFalse(BattlegroundObjectiveGate.IsAllowed(reason));
        }

        /// <summary>Only the defending team index is "own": a crystal defended by team 0 is attackable by team 1.</summary>
        [TestMethod]
        public void TheDefendingTeamIsTheTagsNotAConstant()
        {
            var m = NewMatch();
            var tag = new BattlegroundObjectiveTag(m.MatchId, 0, DefendingTeam: 0);

            Assert.AreEqual(ObjectiveGateReason.OwnTeam, BattlegroundObjectiveGate.Evaluate(tag, Binding(m, 0), harmful: true));
            Assert.AreEqual(ObjectiveGateReason.Allowed, BattlegroundObjectiveGate.Evaluate(tag, Binding(m, 1), harmful: true));
        }

        [TestMethod]
        public void RefusalText_OwnTeamHasItsOwnLine_EveryOtherRefusalTheGenericOne()
        {
            Assert.AreEqual("[Battleground] You cannot damage your own team's crystal.", BattlegroundText.CrystalRefusal(ObjectiveGateReason.OwnTeam));
            Assert.AreEqual("You cannot damage your own team's crystal.", BattlegroundText.RefusalOwnCrystalBody);

            foreach (var reason in new[] { ObjectiveGateReason.ActorUnbound, ObjectiveGateReason.OtherMatch, ObjectiveGateReason.MatchNotLive, ObjectiveGateReason.ActorRespawning, ObjectiveGateReason.NotHarmful })
                Assert.AreEqual("[Battleground] You cannot damage that crystal.", BattlegroundText.CrystalRefusal(reason), reason.ToString());

            Assert.AreEqual("You cannot damage that crystal.", BattlegroundText.RefusalCrystalBody);
            Assert.IsNull(BattlegroundText.CrystalRefusal(ObjectiveGateReason.Allowed));
            Assert.IsNull(BattlegroundText.CrystalRefusal(ObjectiveGateReason.NoTag));
        }

        /// <summary>Every reason except Allowed must be reachable and must refuse: a new reason cannot slip through as an allow.</summary>
        [TestMethod]
        public void OnlyAllowedAllows()
        {
            foreach (ObjectiveGateReason reason in Enum.GetValues(typeof(ObjectiveGateReason)))
                Assert.AreEqual(reason == ObjectiveGateReason.Allowed, BattlegroundObjectiveGate.IsAllowed(reason), reason.ToString());
        }
    }
}
