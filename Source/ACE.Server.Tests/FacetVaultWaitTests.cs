using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the facet switch's wait for a cold account vault: the pre-mutation gate
    /// (<see cref="Player.DecideFacetVaultGate"/>) and the command layer's poll
    /// (<see cref="FacetCommands.DecideVaultPoll"/>).
    ///
    /// The bug being guarded against: the gear restore runs after the switch commits, and on a cold vault
    /// its lookup was the call that STARTED the load, so it could never see the vault ready. Every
    /// vault-held item was reported "could not be checked", left behind, and then forgotten on the next
    /// switch away.
    ///
    /// WHAT THIS DOES NOT COVER, stated plainly: the live sequence. No Player can be constructed here, so
    /// nothing below runs TrySwitchFacet, the ActionChain, or a real AccountVaultStore load. The source
    /// guards at the bottom are PROXIES for the wiring - they prove the calls are written in the right
    /// place, not that they run.
    /// </summary>
    [TestClass]
    public class FacetVaultWaitTests
    {
        private static FacetEquipEntry Entry(uint guid, uint wcid) => new FacetEquipEntry { Guid = guid, Wcid = wcid, Slot = 1 };

        /// <summary>A probe that records how often it was consulted, so a test can prove it was NOT.</summary>
        private sealed class CountingProbe
        {
            private readonly Player.FacetVaultStatus status;

            public CountingProbe(Player.FacetVaultStatus status) => this.status = status;

            public int Calls { get; private set; }

            public Player.FacetVaultStatus Probe()
            {
                Calls++;
                return status;
            }
        }

        private static readonly Player.FacetVaultStatus Loading = Player.FacetVaultStatus.NotReady(AccountVaultStore.StillLoadingMessage);

        // ---- the gate ------------------------------------------------------------------------------

        [TestMethod]
        public void Gate_NoRememberedGear_ProceedsWithoutProbingTheVault()
        {
            var probe = new CountingProbe(Loading);

            var outcome = Player.DecideFacetVaultGate(new List<FacetEquipEntry>(), new HashSet<uint>(), probe.Probe, out var refusal);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Proceed, outcome);
            Assert.IsNull(refusal);
            Assert.AreEqual(0, probe.Calls, "a switch with nothing to restore must not start a vault load it does not need");
        }

        /// <summary>
        /// CATCHES: the local check being dropped or inverted, which would make every switch wait on the
        /// vault. The local set at the call site includes EquippedObjects (pinned by a source guard below),
        /// so an item worn right now is in this set and must not trigger a wait.
        /// </summary>
        [TestMethod]
        public void Gate_EveryEntryLocal_ProceedsWithoutProbingTheVault()
        {
            var probe = new CountingProbe(Loading);
            var entries = new List<FacetEquipEntry> { Entry(0x80000001, 100), Entry(0x80000002, 200) };
            var local = new HashSet<uint> { 0x80000001, 0x80000002 };

            var outcome = Player.DecideFacetVaultGate(entries, local, probe.Probe, out _);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Proceed, outcome);
            Assert.AreEqual(0, probe.Calls);
        }

        /// <summary>
        /// A pristine item collapsed into a ledger stack loses its biota, so its remembered guid is local
        /// nowhere and it resolves by wcid from the vault ledger. It still needs the vault, so a loading
        /// vault must be waited for. CATCHES: a "guid not found anywhere means gone" shortcut.
        /// </summary>
        [TestMethod]
        public void Gate_LedgerOnlyEntry_StillLoading_AwaitsTheVault()
        {
            var probe = new CountingProbe(Loading);
            var entries = new List<FacetEquipEntry> { Entry(0x80000009, 3000) };

            var outcome = Player.DecideFacetVaultGate(entries, new HashSet<uint> { 0x80000001 }, probe.Probe, out var refusal);

            Assert.AreEqual(Player.FacetVaultGateOutcome.AwaitVault, outcome);
            Assert.IsNull(refusal, "the command layer owns the wait wording; the gate must not supply a refusal on this path");
            Assert.AreEqual(1, probe.Calls);
        }

        [TestMethod]
        public void Gate_OneLocalOneVaultEntry_StillLoading_AwaitsTheVault()
        {
            var probe = new CountingProbe(Loading);
            var entries = new List<FacetEquipEntry> { Entry(0x80000001, 100), Entry(0x80000002, 200) };

            var outcome = Player.DecideFacetVaultGate(entries, new HashSet<uint> { 0x80000001 }, probe.Probe, out _);

            Assert.AreEqual(Player.FacetVaultGateOutcome.AwaitVault, outcome);
        }

        [TestMethod]
        public void Gate_VaultEntry_StoreReady_Proceeds()
        {
            var outcome = Player.DecideFacetVaultGate(
                new List<FacetEquipEntry> { Entry(0x80000002, 200) }, new HashSet<uint>(), () => Player.FacetVaultStatus.ReadyStore, out var refusal);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Proceed, outcome);
            Assert.IsNull(refusal);
        }

        /// <summary>No resolvable account: the existing degrade (the restore reports what it could not check).</summary>
        [TestMethod]
        public void Gate_VaultEntry_NoStore_Proceeds()
        {
            var outcome = Player.DecideFacetVaultGate(
                new List<FacetEquipEntry> { Entry(0x80000002, 200) }, new HashSet<uint>(), () => Player.FacetVaultStatus.NoStore, out var refusal);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Proceed, outcome);
            Assert.IsNull(refusal);
        }

        /// <summary>
        /// Only "still loading" is worth waiting for. CATCHES: every not-ready reason being treated as a
        /// wait, which would burn the full budget on an index read that already failed.
        /// </summary>
        [TestMethod]
        public void Gate_VaultEntry_Unavailable_RefusesWithTheReason_DoesNotWait()
        {
            var outcome = Player.DecideFacetVaultGate(
                new List<FacetEquipEntry> { Entry(0x80000002, 200) }, new HashSet<uint>(),
                () => Player.FacetVaultStatus.NotReady(AccountVaultStore.UnavailableMessage), out var refusal);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Refuse, outcome);
            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, AccountVaultStore.UnavailableMessage);
            StringAssert.Contains(refusal, "not changed");
        }

        [TestMethod]
        public void Gate_VaultEntry_UnrecognizedReason_Refuses()
        {
            const string reason = "Some future vault refusal.";

            var outcome = Player.DecideFacetVaultGate(
                new List<FacetEquipEntry> { Entry(0x80000002, 200) }, new HashSet<uint>(),
                () => Player.FacetVaultStatus.NotReady(reason), out var refusal);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Refuse, outcome);
            StringAssert.Contains(refusal, reason);
        }

        /// <summary>A null entry resolves to NotFound whatever the vault holds, so it is no reason to wait.</summary>
        [TestMethod]
        public void Gate_OnlyNullEntries_ProceedsWithoutProbing()
        {
            var probe = new CountingProbe(Loading);

            var outcome = Player.DecideFacetVaultGate(new List<FacetEquipEntry> { null }, new HashSet<uint>(), probe.Probe, out _);

            Assert.AreEqual(Player.FacetVaultGateOutcome.Proceed, outcome);
            Assert.AreEqual(0, probe.Calls);
        }

        // ---- the poll ------------------------------------------------------------------------------

        [TestMethod]
        public void Poll_PlayerLoggedOut_AbandonsWithoutProbing()
        {
            var probe = new CountingProbe(Player.FacetVaultStatus.ReadyStore);

            var action = FacetCommands.DecideVaultPoll(false, 3, 3, probe.Probe, 1.0);

            Assert.AreEqual(FacetCommands.VaultPollAction.Abandon, action);
            Assert.AreEqual(0, probe.Calls, "probing creates a store and starts a load - pointless for a player who has gone");
        }

        /// <summary>
        /// One pending switch per character. CATCHES: the generation check being dropped, which would let
        /// a superseded request resume and switch the character to the facet the player moved away from.
        /// </summary>
        [TestMethod]
        public void Poll_SupersededRequest_AbandonsWithoutProbing()
        {
            var probe = new CountingProbe(Player.FacetVaultStatus.ReadyStore);

            var action = FacetCommands.DecideVaultPoll(true, 3, 4, probe.Probe, 1.0);

            Assert.AreEqual(FacetCommands.VaultPollAction.Abandon, action);
            Assert.AreEqual(0, probe.Calls);
        }

        /// <summary>
        /// The poll's online test. CATCHES: an identity check that is dropped (a relogged session holds a
        /// NEW Player, and a finished logoff nulls Session.Player), and either logout flag being ignored
        /// (the character is still on its session during the logout animation and the PK logout delay).
        /// Plain objects stand in for Player, which this assembly cannot construct.
        /// </summary>
        [TestMethod]
        public void PollOnline_RequiresTheSameInstanceOnTheSession_AndNoLogoutUnderWay()
        {
            var captured = new object();

            Assert.IsTrue(FacetCommands.IsPollPlayerOnline(captured, captured, false, false), "the same instance, not logging out, is online");

            Assert.IsFalse(FacetCommands.IsPollPlayerOnline(null, captured, false, false), "logoff finished: Session.Player is null");
            Assert.IsFalse(FacetCommands.IsPollPlayerOnline(new object(), captured, false, false), "relogged: the session holds a different Player");
            Assert.IsFalse(FacetCommands.IsPollPlayerOnline(captured, captured, true, false), "IsLoggingOut: the logout animation is under way");
            Assert.IsFalse(FacetCommands.IsPollPlayerOnline(captured, captured, false, true), "PKLogout: a delayed player killer logout is pending");
            Assert.IsFalse(FacetCommands.IsPollPlayerOnline(null, null, false, false), "no captured player is never online");
        }

        [TestMethod]
        public void Poll_StoreReady_Resumes()
        {
            Assert.AreEqual(FacetCommands.VaultPollAction.Resume,
                FacetCommands.DecideVaultPoll(true, 3, 3, () => Player.FacetVaultStatus.ReadyStore, 1.0));
        }

        /// <summary>A non-loading failure resumes so the re-entered switch's own gate refuses with the real reason.</summary>
        [TestMethod]
        public void Poll_NonLoadingFailure_Resumes_EvenPastTheBudget()
        {
            Assert.AreEqual(FacetCommands.VaultPollAction.Resume,
                FacetCommands.DecideVaultPoll(true, 3, 3, () => Player.FacetVaultStatus.NotReady(AccountVaultStore.UnavailableMessage), 25.0));
        }

        [TestMethod]
        public void Poll_NoStore_Resumes()
        {
            Assert.AreEqual(FacetCommands.VaultPollAction.Resume,
                FacetCommands.DecideVaultPoll(true, 3, 3, () => Player.FacetVaultStatus.NoStore, 1.0));
        }

        [TestMethod]
        public void Poll_StillLoading_WithinBudget_KeepsWaiting()
        {
            Assert.AreEqual(FacetCommands.VaultPollAction.KeepWaiting,
                FacetCommands.DecideVaultPoll(true, 3, 3, () => Loading, FacetCommands.VaultMaxWaitSeconds - FacetCommands.VaultPollSeconds));
        }

        [TestMethod]
        public void Poll_StillLoading_AtTheBudget_GivesUp()
        {
            Assert.AreEqual(FacetCommands.VaultPollAction.GiveUp,
                FacetCommands.DecideVaultPoll(true, 3, 3, () => Loading, FacetCommands.VaultMaxWaitSeconds));
        }

        /// <summary>
        /// Drives the poll loop exactly as ScheduleVaultPoll does (waited += poll interval, then decide) on
        /// a vault that never finishes loading. The total must be the 20 s budget, reached in 40 ticks.
        /// </summary>
        [TestMethod]
        public void Poll_NeverLoads_GivesUpAfterExactlyTheBudget()
        {
            var waited = 0.0;
            var ticks = 0;
            FacetCommands.VaultPollAction action;

            do
            {
                waited += FacetCommands.VaultPollSeconds;
                ticks++;
                action = FacetCommands.DecideVaultPoll(true, 1, 1, () => Loading, waited);
            }
            while (action == FacetCommands.VaultPollAction.KeepWaiting && ticks < 1000);

            Assert.AreEqual(FacetCommands.VaultPollAction.GiveUp, action);
            Assert.AreEqual(20.0, waited, "the spec'd total wait");
            Assert.AreEqual(40, ticks, "20 s at the spec'd 0.5 s interval");
        }

        /// <summary>
        /// The clock is carried across a re-entered switch, not restarted. A request that resumed at 12 s
        /// and found the vault loading again has 8 s left, not 20. CATCHES: HandleSwitch passing 0 instead
        /// of the carried wait on the resume path (pinned at source below) or the expiry test using a
        /// per-leg budget.
        /// </summary>
        [TestMethod]
        public void Wait_CarriedAcrossReentry_ExpiresAtTheTotalNotPerLeg()
        {
            Assert.IsFalse(FacetCommands.HasVaultWaitExpired(12.0));
            Assert.IsFalse(FacetCommands.HasVaultWaitExpired(19.5));
            Assert.IsTrue(FacetCommands.HasVaultWaitExpired(20.0));
            Assert.IsTrue(FacetCommands.HasVaultWaitExpired(35.0));

            var waited = 12.0;
            var ticks = 0;

            while (FacetCommands.DecideVaultPoll(true, 1, 1, () => Loading, waited += FacetCommands.VaultPollSeconds) == FacetCommands.VaultPollAction.KeepWaiting)
                ticks++;

            Assert.AreEqual(15, ticks, "from 12 s carried, 16 more ticks reach 20 s: 15 keep-waiting then the give-up");
        }

        [TestMethod]
        public void PlayerLines_AreAsciiOnly()
        {
            foreach (var line in new[] { FacetCommands.VaultLoadingLine, FacetCommands.VaultTimeoutLine })
            {
                foreach (var c in line)
                    Assert.IsTrue(c < 128, $"non-ASCII character U+{(int)c:X4} in player-facing line: {line}");
            }

            StringAssert.Contains(FacetCommands.VaultTimeoutLine, "not changed");
        }

        // ---- source guards (PROXIES for the wiring) ------------------------------------------------

        private static string ReadServerFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", Path.Combine(relative)));
        }

        private static string MethodBody(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"could not find `{signature}` - this test's anchor has moved, it is not evidence about behaviour");

            var open = text.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after `{signature}`");
            return null;
        }

        /// <summary>
        /// CATCHES: the gate being dropped from TrySwitchFacet, moved AFTER the first mutation (the outgoing
        /// row save), moved ahead of the last read-only refusal (a pool/attribute refusal would then only
        /// be reported after a wait), or its local set losing EquippedObjects. PROXY: order in source.
        /// </summary>
        [TestMethod]
        public void Wiring_TrySwitchFacet_GatesAfterTheLastRefusalAndBeforeTheFirstMutation()
        {
            var body = MethodBody(ReadServerFile("WorldObjects", "Player_Facets.cs"), "public bool TrySwitchFacet(");

            var lastRefusal = body.IndexOf("if (attributeShortfall > 0)", StringComparison.Ordinal);
            var localSet = body.IndexOf("GetAllPossessions(Inventory.Values, EquippedObjects.Values)", StringComparison.Ordinal);
            var gate = body.IndexOf("DecideFacetVaultGate(", StringComparison.Ordinal);
            var firstMutation = body.IndexOf("SaveFacetRowAsync(outgoingFacetRow)", StringComparison.Ordinal);

            Assert.IsTrue(lastRefusal >= 0, "fixture: the attribute shortfall refusal anchor has moved");
            Assert.IsTrue(firstMutation >= 0, "fixture: the outgoing row save anchor has moved");
            Assert.IsTrue(gate >= 0, "TrySwitchFacet must call DecideFacetVaultGate");
            Assert.IsTrue(localSet >= 0 && localSet < gate, "the gate's local set must include EquippedObjects and be built before the gate");
            Assert.IsTrue(lastRefusal < gate, "the gate must come after the last read-only refusal");
            Assert.IsTrue(gate < firstMutation, "the gate must come before the first mutation");
            StringAssert.Contains(body, "awaitingVault = true;");
        }

        /// <summary>
        /// CATCHES: a poll that re-runs TrySwitchFacet every tick (a blocking shard read on the world thread
        /// twice a second), a resume that restarts the clock, and a typed switch that no longer supersedes a
        /// pending one. PROXY: text in source.
        /// </summary>
        [TestMethod]
        public void Wiring_FacetCommands_PollChecksReadinessOnly_CarriesTheClock_TypedSwitchSupersedes()
        {
            var text = ReadServerFile("Command", "Handlers", "FacetCommands.cs");

            var poll = MethodBody(text, "private static void ScheduleVaultPoll(");
            Assert.IsFalse(poll.Contains("TrySwitchFacet("), "the poll must check vault readiness only, never re-run the switch per tick");
            StringAssert.Contains(poll, "HandleSwitch(session, player, targetSlot, confirmed, trim, requestGeneration, waited)",
                "the resume must carry the request's generation and elapsed wait");

            // Session.Network is never nulled on disconnect (Session.DropSession only releases its
            // resources), so an online test built on it never fires and a detached Player's switch resumes.
            StringAssert.Contains(poll, "IsPollPlayerOnline(player.Session?.Player, player, player.IsLoggingOut, player.PKLogout)",
                "the poll must test Session.Player identity plus both logout flags");
            Assert.IsFalse(poll.Contains("Session?.Network"), "the poll's online test must not read Session.Network");

            var handleFacet = MethodBody(text, "public static void HandleFacet(");
            StringAssert.Contains(handleFacet, "player.BeginFacetSwitchRequest()", "a typed switch must supersede any pending one");
        }

        /// <summary>
        /// The remembered worn set is rewritten from what is worn when the player LEAVES a facet, so a piece
        /// that did not come back on is forgotten then unless the player equips it. The switch summary has
        /// to say so whenever anything is missing.
        ///
        /// CATCHES: a note shown when everything came back, a note missing when even one piece did not, and
        /// non-ASCII text in a player-facing line.
        /// </summary>
        [TestMethod]
        public void UnrestoredGearNote_OnlyWhenSomethingDidNotComeBack()
        {
            Assert.IsNull(Player.ComposeUnrestoredGearNote(0, 0), "nothing remembered, no note");
            Assert.IsNull(Player.ComposeUnrestoredGearNote(12, 12), "everything worn again, no note");

            var note = Player.ComposeUnrestoredGearNote(12, 11);

            Assert.IsNotNull(note, "one missing piece must produce the note");
            StringAssert.Contains(note, "forgotten");
            StringAssert.Contains(note, "unless you equip it first");

            foreach (var c in note)
                Assert.IsTrue(c >= 0x20 && c < 0x7F, $"player-facing text must be printable ASCII, found U+{(int)c:X4}");

            var wiring = MethodBody(ReadServerFile("WorldObjects", "Player_Facets.cs"), "public bool TrySwitchFacet(");
            StringAssert.Contains(wiring, "ComposeUnrestoredGearNote(incomingEquip.Count, EquippedObjects.Count)",
                "the switch summary must count what is worn after the restore against what was remembered");
            StringAssert.Contains(wiring, "summary.Append(\" \" + unrestoredGearNote)",
                "the note must reach the summary the player reads, not just be computed");
        }
    }
}
