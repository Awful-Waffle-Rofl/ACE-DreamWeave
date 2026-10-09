using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The pure rules behind Player_PvpArena.cs (PR C1): the per-binding masks, the one combined-predicate
    /// shape (PK facet rule OR arena mask), the in-match death waivers, and the facade's intent shapes. Reads no
    /// PropertyManager key, so it needs no seeding.
    /// </summary>
    [TestClass]
    public class PvpPlayerRulesTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500), new PvpParticipant(2, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), "arena_2v2", teams, Now);
        }

        private static PvpPlayerBinding Binding(PvpMatch match, int team = 0, bool ca = true, bool eq = true, bool wm = true, bool pickup = true, bool turn = true) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, ca, eq, wm, pickup, turn);

        // ================= InMatch and the masks =================

        [TestMethod]
        public void NoBinding_IsNotInAMatch_AndEveryMaskIsOff()
        {
            Assert.IsFalse(PvpPlayerRules.InMatch(null));
            Assert.IsFalse(PvpPlayerRules.ClassAbilityMask(null));
            Assert.IsFalse(PvpPlayerRules.EquipmentModMask(null));
            Assert.IsFalse(PvpPlayerRules.WeaponModMask(null));
            Assert.IsFalse(PvpPlayerRules.PickupBoonMask(null));
            Assert.IsFalse(PvpPlayerRules.TurnSpeedMask(null));
        }

        /// <summary>A binding with no match is not "in a match", exactly as PvpArenaGate.IsBound decides - even with every flag set.</summary>
        [TestMethod]
        public void BindingWithoutAMatch_IsNotInAMatch_EvenWithFlagsSet()
        {
            var orphan = new PvpPlayerBinding(null, 0, PvpMatchState.Live, true, true, true, true, true);

            Assert.IsFalse(PvpPlayerRules.InMatch(orphan));
            Assert.IsFalse(PvpPlayerRules.ClassAbilityMask(orphan));
            Assert.IsFalse(PvpPlayerRules.WeaponModMask(orphan));
        }

        /// <summary>Each mask reads its OWN flag: turning one flag off leaves every other mask on.</summary>
        [TestMethod]
        public void EachMask_ReadsOnlyItsOwnFlag()
        {
            var match = NewMatch();

            Assert.IsFalse(PvpPlayerRules.ClassAbilityMask(Binding(match, ca: false)));
            Assert.IsTrue(PvpPlayerRules.EquipmentModMask(Binding(match, ca: false)));

            Assert.IsFalse(PvpPlayerRules.EquipmentModMask(Binding(match, eq: false)));
            Assert.IsTrue(PvpPlayerRules.WeaponModMask(Binding(match, eq: false)));

            Assert.IsFalse(PvpPlayerRules.WeaponModMask(Binding(match, wm: false)));
            Assert.IsTrue(PvpPlayerRules.PickupBoonMask(Binding(match, wm: false)));

            Assert.IsFalse(PvpPlayerRules.PickupBoonMask(Binding(match, pickup: false)));
            Assert.IsTrue(PvpPlayerRules.TurnSpeedMask(Binding(match, pickup: false)));

            Assert.IsFalse(PvpPlayerRules.TurnSpeedMask(Binding(match, turn: false)));
            Assert.IsTrue(PvpPlayerRules.ClassAbilityMask(Binding(match, turn: false)));
        }

        // ================= the combined-predicate shape =================

        /// <summary>
        /// PvpPlayerRules.Suppressed is the body of ClassAbilitySuppressed, EquipmentModSuppressed,
        /// PickupBoonSuppressed and TurnSpeedSuppressed. All four rows, so dropping a term or turning the OR into
        /// an AND fails one.
        /// </summary>
        [TestMethod]
        public void Suppressed_IsFacetRuleOrArenaMask()
        {
            Assert.IsFalse(PvpPlayerRules.Suppressed(pkFacetRuleActive: false, arenaMaskActive: false));
            Assert.IsTrue(PvpPlayerRules.Suppressed(pkFacetRuleActive: true, arenaMaskActive: false));
            Assert.IsTrue(PvpPlayerRules.Suppressed(pkFacetRuleActive: false, arenaMaskActive: true));
            Assert.IsTrue(PvpPlayerRules.Suppressed(pkFacetRuleActive: true, arenaMaskActive: true));
        }

        [TestMethod]
        public void IsTeammate_OnlySameMatchSameTeam()
        {
            var match = NewMatch();
            var other = NewMatch();

            Assert.IsTrue(PvpPlayerRules.IsTeammate(Binding(match, 0), Binding(match, 0)));
            Assert.IsFalse(PvpPlayerRules.IsTeammate(Binding(match, 0), Binding(match, 1)), "opponents");
            Assert.IsFalse(PvpPlayerRules.IsTeammate(Binding(match, 0), Binding(other, 0)), "same index, different match");
            Assert.IsFalse(PvpPlayerRules.IsTeammate(Binding(match, 0), null), "one side unbound");
        }

        // ================= in-match death waivers (H5) =================

        /// <summary>
        /// The four waivers the death sites read (Player.Die vitae and purge, the dieChain respite, and both
        /// CalculateDeathItems paths). A match death with the shipped waiver waives vitae, item loss and the
        /// respite, and keeps enchantments.
        /// </summary>
        [TestMethod]
        public void MatchDeath_WaivesVitaeItemsRespite_AndKeepsEnchantmentsByDefault()
        {
            var waiver = PvpDeathWaiver.ForInMatchDeath(deathKeepsEnchantments: true);

            Assert.IsTrue(PvpPlayerRules.WaivesVitae(true, waiver));
            Assert.IsTrue(PvpPlayerRules.WaivesItemLoss(true, waiver));
            Assert.IsTrue(PvpPlayerRules.WaivesRespite(true, waiver));
            Assert.IsTrue(PvpPlayerRules.WaivesEnchantmentPurge(true, waiver));
        }

        /// <summary>pvp_arena_death_keeps_enchantments off: the purge runs, and nothing else changes.</summary>
        [TestMethod]
        public void MatchDeath_WithKeepsEnchantmentsOff_PurgesButStillWaivesTheRest()
        {
            var waiver = PvpDeathWaiver.ForInMatchDeath(deathKeepsEnchantments: false);

            Assert.IsFalse(PvpPlayerRules.WaivesEnchantmentPurge(true, waiver));
            Assert.IsTrue(PvpPlayerRules.WaivesVitae(true, waiver));
            Assert.IsTrue(PvpPlayerRules.WaivesItemLoss(true, waiver));
            Assert.IsTrue(PvpPlayerRules.WaivesRespite(true, waiver));
        }

        /// <summary>INERT: an ordinary death (latch false) waives nothing, whatever waiver object is lying around.</summary>
        [TestMethod]
        public void OrdinaryDeath_WaivesNothing()
        {
            var waiver = PvpDeathWaiver.ForInMatchDeath(deathKeepsEnchantments: true);

            foreach (var w in new[] { waiver, null })
            {
                Assert.IsFalse(PvpPlayerRules.WaivesVitae(false, w));
                Assert.IsFalse(PvpPlayerRules.WaivesItemLoss(false, w));
                Assert.IsFalse(PvpPlayerRules.WaivesRespite(false, w));
                Assert.IsFalse(PvpPlayerRules.WaivesEnchantmentPurge(false, w));
            }
        }

        /// <summary>A latched match death with no waiver object never turns into a penalty.</summary>
        [TestMethod]
        public void MatchDeath_WithMissingWaiver_StillWaives()
        {
            Assert.IsTrue(PvpPlayerRules.WaivesVitae(true, null));
            Assert.IsTrue(PvpPlayerRules.WaivesItemLoss(true, null));
            Assert.IsTrue(PvpPlayerRules.WaivesRespite(true, null));
            Assert.IsTrue(PvpPlayerRules.WaivesEnchantmentPurge(true, null));
        }

        // ================= the facade =================

        [TestMethod]
        public void Facade_IntentShapes_AndFifoQueue()
        {
            PvpMatchManager.ClearForTests();

            try
            {
                var matchId = Guid.NewGuid();

                var forfeit = PvpMatchManager.LogoutForfeit(0x50000001, matchId, Now);
                Assert.AreEqual(PvpIntentKind.Forfeit, forfeit.Kind);
                Assert.AreEqual(ParticipantExit.ForfeitLogout, forfeit.ExitReason);
                Assert.AreEqual(0x50000001u, forfeit.CharacterId);
                Assert.AreEqual(matchId, forfeit.MatchId);

                var death = PvpMatchManager.Death(0x50000002, matchId, Now);
                Assert.AreEqual(PvpIntentKind.Death, death.Kind);
                Assert.AreEqual(ParticipantExit.Died, death.ExitReason);

                PvpMatchManager.Report(null);
                Assert.AreEqual(0, PvpMatchManager.PendingCount, "a null intent must be ignored");

                PvpMatchManager.Report(forfeit);
                PvpMatchManager.Report(death);

                Assert.IsTrue(PvpMatchManager.TryDequeue(out var first));
                Assert.AreSame(forfeit, first);
                Assert.IsTrue(PvpMatchManager.TryDequeue(out var second));
                Assert.AreSame(death, second);
                Assert.IsFalse(PvpMatchManager.TryDequeue(out _));
            }
            finally
            {
                PvpMatchManager.ClearForTests();
            }
        }

        // ================= EnterPvpMatch: which enchantments are removed on entry =================

        private const uint Self = 0x50000001;
        private const uint OtherPlayer = 0x50000002;
        private const uint Monster = 0x80000123;

        private static ACE.Entity.Models.PropertiesEnchantmentRegistry Entry(uint caster, bool beneficial = false, double duration = 60, int spellId = 1234) =>
            new ACE.Entity.Models.PropertiesEnchantmentRegistry
            {
                CasterObjectId = caster,
                Duration = duration,
                SpellId = spellId,
                StatModType = beneficial ? ACE.Entity.Enum.EnchantmentTypeFlags.Beneficial : ACE.Entity.Enum.EnchantmentTypeFlags.Undef
            };

        /// <summary>The one case removed: a timed harmful enchantment whose caster is a different player.</summary>
        [TestMethod]
        public void IsHarmFromAnotherPlayer_AnotherPlayersHarmfulEnchantment_IsRemoved()
        {
            Assert.IsTrue(ObjectGuid_IsPlayer(OtherPlayer), "fixture: OtherPlayer must be in the player guid range");
            Assert.IsTrue(PvpPlayerRules.IsHarmFromAnotherPlayer(Entry(OtherPlayer), Self));
        }

        [TestMethod]
        public void IsHarmFromAnotherPlayer_EverythingElse_IsKept()
        {
            Assert.IsFalse(ObjectGuid_IsPlayer(Monster), "fixture: Monster must be outside the player guid range");

            Assert.IsFalse(PvpPlayerRules.IsHarmFromAnotherPlayer(null, Self), "null entry");
            Assert.IsFalse(PvpPlayerRules.IsHarmFromAnotherPlayer(Entry(Self), Self), "self-cast (and vitae, which is stamped with the player's own guid)");
            Assert.IsFalse(PvpPlayerRules.IsHarmFromAnotherPlayer(Entry(Monster), Self), "monster-cast");
            Assert.IsFalse(PvpPlayerRules.IsHarmFromAnotherPlayer(Entry(OtherPlayer, beneficial: true), Self), "another player's buff");
            Assert.IsFalse(PvpPlayerRules.IsHarmFromAnotherPlayer(Entry(OtherPlayer, duration: -1), Self), "an item aura (Duration -1)");
            Assert.IsFalse(PvpPlayerRules.IsHarmFromAnotherPlayer(Entry(OtherPlayer, spellId: short.MaxValue + 1), Self), "a cooldown entry");
        }

        private static bool ObjectGuid_IsPlayer(uint guid) => ACE.Entity.ObjectGuid.IsPlayer(guid);

        // ================= RefusesFacetSwitchForArena (Player.CheckFacetGates' arena gate) =================

        /// <summary>
        /// Every arena status from joining to the end of the match refuses a facet switch, so none can be in flight
        /// when the template apply captures the build. InMatch is the case the old (PK-facet) rule missed: dispatched
        /// but not yet bound, where CheckFacetGates' own IsInPvpMatch gate does not answer.
        /// </summary>
        [TestMethod]
        public void RefusesFacetSwitchForArena_QueuedOfferedOrInMatch_Refuses()
        {
            Assert.IsTrue(PvpPlayerRules.RefusesFacetSwitchForArena(PvpStatusKind.Queued));
            Assert.IsTrue(PvpPlayerRules.RefusesFacetSwitchForArena(PvpStatusKind.AwaitingAccept));
            Assert.IsTrue(PvpPlayerRules.RefusesFacetSwitchForArena(PvpStatusKind.InMatch));
        }

        /// <summary>Idle has nothing to protect.</summary>
        [TestMethod]
        public void RefusesFacetSwitchForArena_Idle_Allows()
        {
            Assert.IsFalse(PvpPlayerRules.RefusesFacetSwitchForArena(PvpStatusKind.Idle));
        }

        /// <summary>The refusal no longer talks about the PK facet, which the arena no longer requires.</summary>
        [TestMethod]
        public void QueuedFacetSwitchRefused_DoesNotMentionThePkFacet()
        {
            StringAssert.DoesNotMatch(PvpArenaText.QueuedFacetSwitchRefused, new System.Text.RegularExpressions.Regex("PK"));
        }
    }
}
