using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The facet vault deposit's pure parts: which outgoing items go (<see cref="Player.SelectFacetDepositCandidates"/>),
    /// what the player is told (<see cref="Player.ComposeFacetVaultDepositReport"/>), the /facet vault parse and
    /// wording, and source pins for the wiring no unit test can run (no Player can be built here). The pins are
    /// PROXIES: they prove the calls are written in the right place, not that they run.
    /// </summary>
    [TestClass]
    public class FacetVaultDepositSelectTests
    {
        private static FacetEquipEntry Entry(WorldObject item) => new FacetEquipEntry { Guid = item.Guid.Full, Wcid = item.WeenieClassId, Slot = 1 };

        private static WorldObject Gear(uint wcid) => FakeVaultWorld.MakeStack(wcid, 1, 1);

        private static Player.FacetDepositSelection Select(IEnumerable<WorldObject> outgoing, IEnumerable<WorldObject> wornNow, IEnumerable<WorldObject> pool,
                                                           IEnumerable<WorldObject> incomingRemembered = null)
        {
            return Player.SelectFacetDepositCandidates(outgoing.Select(Entry).ToList(),
                new HashSet<uint>((wornNow ?? Enumerable.Empty<WorldObject>()).Select(w => w.Guid.Full)),
                new HashSet<uint>((incomingRemembered ?? Enumerable.Empty<WorldObject>()).Select(w => w.Guid.Full)), pool, IssuedOnly);
        }

        /// <summary>The non-templated player's arm of PvpTemplateBlocked(Vault, item): the issued-item lock alone.</summary>
        private static bool IssuedOnly(WorldObject item) => ACE.Server.Pvp.Templates.PvpTemplate.IsIssued(item);

        private static Player.FacetDepositKeepReason ReasonFor(Player.FacetDepositSelection selection, WorldObject item) =>
            selection.Kept.Single(k => k.Guid == item.Guid.Full).Reason;

        private static PetDevice MakePetDevice()
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 8001,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.Value, 100 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Test Essence" },
                },
            };

            return new PetDevice(weenie, new ObjectGuid(FakeVaultWorld.NextGuid()));
        }

        // ------------------------------------------------------------------ selection

        [TestMethod]
        public void Select_PlainGearInThePool_IsDeposited_InOutgoingOrder()
        {
            var a = Gear(9501);
            var b = Gear(9502);
            var c = Gear(9503);

            // The pool is in a different order from the worn set: the result must follow the worn set.
            var selection = Select(new[] { a, b, c }, null, new[] { c, a, b });

            CollectionAssert.AreEqual(new[] { a, b, c }, selection.Deposit);
            Assert.AreEqual(0, selection.Kept.Count);
        }

        /// <summary>CATCHES: a shared item being deposited and then withdrawn straight back by the restore.</summary>
        [TestMethod]
        public void Select_SharedWithTheIncomingFacet_IsKeptAndNotReported()
        {
            var shared = Gear(9511);
            var other = Gear(9512);

            var selection = Select(new[] { shared, other }, new[] { shared }, new[] { shared, other });

            CollectionAssert.AreEqual(new[] { other }, selection.Deposit);
            Assert.AreEqual(Player.FacetDepositKeepReason.WornAgain, ReasonFor(selection, shared));
            Assert.IsFalse(selection.Kept.Single().IsReported, "a piece the new facet wears is not the deposit's to report");
        }

        /// <summary>
        /// Orchestrator ruling 2026-11-21. CATCHES: an item the incoming facet remembers but failed to re-equip (a wield
        /// requirement no longer met) being sent to the vault, where the player cannot put it on.
        /// </summary>
        [TestMethod]
        public void Select_InTheIncomingSetButNotWorn_IsKeptAndReported()
        {
            var failedReequip = Gear(9515); failedReequip.Name = "Heavy Axe";
            var other = Gear(9516);

            // Worn-now is EMPTY: the re-equip failed, so only the incoming remembered set can keep it.
            var selection = Select(new[] { failedReequip, other }, null, new[] { failedReequip, other }, incomingRemembered: new[] { failedReequip });

            CollectionAssert.AreEqual(new[] { other }, selection.Deposit);
            Assert.AreEqual(Player.FacetDepositKeepReason.IncomingFacetItem, ReasonFor(selection, failedReequip));

            var kept = selection.Kept.Single();
            Assert.IsTrue(kept.IsReported, "the player must be told why it stayed");
            Assert.AreEqual("incoming facet item", Player.FacetDepositKeepLabel(kept));

            var text = string.Join(" ", Player.ComposeFacetVaultDepositReport(null, selection.Kept, null, null));
            StringAssert.Contains(text, "Kept in your pack: Heavy Axe (incoming facet item).");
        }

        /// <summary>An incoming item that DID come back on is worn-again (silent), not "incoming facet item".</summary>
        [TestMethod]
        public void Select_InTheIncomingSetAndWorn_IsWornAgainAndSilent()
        {
            var shared = Gear(9517);

            var selection = Select(new[] { shared }, new[] { shared }, new[] { shared }, incomingRemembered: new[] { shared });

            Assert.AreEqual(Player.FacetDepositKeepReason.WornAgain, ReasonFor(selection, shared));
            Assert.IsFalse(selection.Kept.Single().IsReported);
        }

        [TestMethod]
        public void Select_NotInTheMainPack_IsKept()
        {
            var moved = Gear(9521);

            var selection = Select(new[] { moved }, null, Array.Empty<WorldObject>());

            Assert.AreEqual(0, selection.Deposit.Count);
            Assert.AreEqual(Player.FacetDepositKeepReason.NotInPool, ReasonFor(selection, moved));
        }

        [TestMethod]
        public void Select_Attuned_IsKept()
        {
            var attuned = Gear(9531);
            attuned.Attuned = AttunedStatus.Attuned;

            var sticky = Gear(9532);
            sticky.Attuned = AttunedStatus.Sticky;

            var selection = Select(new[] { attuned, sticky }, null, new[] { attuned, sticky });

            Assert.AreEqual(0, selection.Deposit.Count);
            Assert.AreEqual(Player.FacetDepositKeepReason.NotTransferable, ReasonFor(selection, attuned));
            Assert.AreEqual(Player.FacetDepositKeepReason.NotTransferable, ReasonFor(selection, sticky), "Sticky is the stronger tier and must not slip through");
            Assert.AreEqual("attuned", Player.FacetDepositKeepLabel(selection.Kept[0]));
        }

        [TestMethod]
        public void Select_AttunedInsideAContainer_IsKept()
        {
            var pack = FakeVaultWorld.MakeContainer(4);
            var attuned = Gear(9541);
            attuned.Attuned = AttunedStatus.Attuned;
            Assert.IsTrue(pack.TryAddToInventory(attuned), "fixture: could not put the attuned item in the pack");

            var selection = Select(new[] { (WorldObject)pack }, null, new[] { (WorldObject)pack });

            Assert.AreEqual(0, selection.Deposit.Count, "a container holding an attuned item never goes to the vault");
        }

        [TestMethod]
        public void Select_TemplateGateRefusal_IsKept()
        {
            var issued = Gear(9551);
            issued.SetProperty(PropertyBool.PvpTemplateIssued, true);

            var selection = Select(new[] { issued }, null, new[] { issued });

            Assert.AreEqual(0, selection.Deposit.Count);
            Assert.AreEqual(Player.FacetDepositKeepReason.Issued, ReasonFor(selection, issued));
        }

        /// <summary>CATCHES: a stack auto-deposited, which a ledger restore would hand back as a single unit.</summary>
        [TestMethod]
        public void Select_StackOfMoreThanOne_IsKept_ButASingleStackableGoes()
        {
            var arrows = FakeVaultWorld.MakeStack(9561, 500, 1000);
            var single = FakeVaultWorld.MakeStack(9562, 1, 1000);

            var selection = Select(new[] { arrows, single }, null, new[] { arrows, single });

            CollectionAssert.AreEqual(new[] { (WorldObject)single }, selection.Deposit);
            Assert.AreEqual(Player.FacetDepositKeepReason.Stack, ReasonFor(selection, arrows));
        }

        [TestMethod]
        public void Select_Timed_IsKept()
        {
            var timed = Gear(9571);
            timed.Lifespan = 3600;

            var selection = Select(new[] { timed }, null, new[] { timed });

            Assert.AreEqual(Player.FacetDepositKeepReason.Timed, ReasonFor(selection, timed));
        }

        [TestMethod]
        public void Select_PackSlotItem_IsKept()
        {
            var pack = FakeVaultWorld.MakeContainer(4);

            var selection = Select(new[] { (WorldObject)pack }, null, new[] { (WorldObject)pack });

            Assert.AreEqual(Player.FacetDepositKeepReason.PackSlot, ReasonFor(selection, pack));
        }

        [TestMethod]
        public void Select_PetDeviceWithItsPetOut_IsKept_WithoutThePetItGoes()
        {
            var out_ = MakePetDevice();
            out_.Pet = 0x80000001;

            var home = MakePetDevice();

            var selection = Select(new WorldObject[] { out_, home }, null, new WorldObject[] { out_, home });

            CollectionAssert.AreEqual(new[] { (WorldObject)home }, selection.Deposit);
            Assert.AreEqual(Player.FacetDepositKeepReason.NotTransferable, ReasonFor(selection, out_));
            Assert.AreEqual("its pet is out", Player.FacetDepositKeepLabel(selection.Kept.Single()));
        }

        [TestMethod]
        public void Select_NullEntriesAndRepeatedGuids_AreSkipped()
        {
            var a = Gear(9581);

            var entries = new List<FacetEquipEntry> { null, Entry(a), Entry(a) };

            var selection = Player.SelectFacetDepositCandidates(entries, new HashSet<uint>(), new HashSet<uint>(), new[] { a }, IssuedOnly);

            CollectionAssert.AreEqual(new[] { a }, selection.Deposit);
        }

        // ------------------------------------------------------------------ report

        private static VaultPackDepositItemResult R(WorldObject item, VaultPackDepositStatus status, string reason = null) =>
            new VaultPackDepositItemResult(item, status, VaultDepositCommit.None, reason);

        [TestMethod]
        public void Report_NamesEveryItemOnce_StoredKeptAndStaff()
        {
            var stored = Gear(9601); stored.Name = "Sword";
            var full = Gear(9602); full.Name = "Shield";
            var lost = Gear(9603); lost.Name = "Helm";
            var arrows = FakeVaultWorld.MakeStack(9604, 50, 100); arrows.Name = "Arrows";

            var result = new VaultPackDepositResult(new[]
            {
                R(stored, VaultPackDepositStatus.Deposited),
                R(full, VaultPackDepositStatus.HandedBack, "Your vault is full: 10 of 10 entries. Withdraw something before storing more."),
                R(lost, VaultPackDepositStatus.Withheld, AccountVaultStore.UnavailableMessage),
            }, false, null);

            var kept = new List<Player.FacetDepositKept> { new Player.FacetDepositKept(arrows.Guid.Full, arrows, Player.FacetDepositKeepReason.Stack) };

            var lines = Player.ComposeFacetVaultDepositReport(result, kept, null, null);
            var text = string.Join(" ", lines);

            StringAssert.Contains(text, "Stored in your vault: Sword.");
            StringAssert.Contains(text, "Shield (vault full)");
            StringAssert.Contains(text, "Arrows (a stack)");
            StringAssert.Contains(text, "Helm");
            StringAssert.Contains(text, "contact staff");

            foreach (var name in new[] { "Sword", "Shield", "Helm", "Arrows" })
                Assert.AreEqual(1, CountOf(text, name), $"{name} must be named exactly once: {text}");

            AssertPrintableAscii(text);
        }

        [TestMethod]
        public void Report_WornAgainItems_AreNotMentioned()
        {
            var shared = Gear(9611); shared.Name = "Shared Ring";

            var kept = new List<Player.FacetDepositKept> { new Player.FacetDepositKept(shared.Guid.Full, null, Player.FacetDepositKeepReason.WornAgain) };

            Assert.AreEqual(0, Player.ComposeFacetVaultDepositReport(null, kept, null, null).Count);
        }

        [TestMethod]
        public void Report_VaultUnavailable_SaysTheGearStayedInThePack()
        {
            var a = Gear(9621); a.Name = "Bracers";

            var lines = Player.ComposeFacetVaultDepositReport(null, null, new[] { a }, AccountVaultStore.UnavailableMessage);
            var text = string.Join(" ", lines);

            StringAssert.Contains(text, "stayed in your pack");
            StringAssert.Contains(text, "Bracers");
            AssertPrintableAscii(text);
        }

        private static int CountOf(string text, string word)
        {
            var n = 0;

            for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal))
                n++;

            return n;
        }

        private static void AssertPrintableAscii(string text)
        {
            foreach (var c in text)
                Assert.IsTrue(c >= 0x20 && c < 0x7F, $"player-facing text must be printable ASCII, found U+{(int)c:X4}: {text}");
        }

        // ------------------------------------------------------------------ /facet vault

        [TestMethod]
        public void ParseVaultArgs_AcceptsOnOffAndNothing_RefusesTheRest()
        {
            Assert.AreEqual(FacetCommands.VaultToggleRequest.Show, FacetCommands.ParseVaultArgs(Array.Empty<string>()));
            Assert.AreEqual(FacetCommands.VaultToggleRequest.On, FacetCommands.ParseVaultArgs(new[] { "ON" }));
            Assert.AreEqual(FacetCommands.VaultToggleRequest.Off, FacetCommands.ParseVaultArgs(new[] { "off" }));
            Assert.AreEqual(FacetCommands.VaultToggleRequest.Invalid, FacetCommands.ParseVaultArgs(new[] { "yes" }));
            Assert.AreEqual(FacetCommands.VaultToggleRequest.Invalid, FacetCommands.ParseVaultArgs(new[] { "on", "now" }));
        }

        [TestMethod]
        public void VaultToggleLine_TurningOn_WarnsAboutGrantees_AndNamesTheKillSwitch()
        {
            var on = FacetCommands.ComposeVaultToggleLine(FacetCommands.VaultToggleRequest.On, true, true);

            StringAssert.Contains(on, "now on");
            StringAssert.Contains(on, FacetCommands.VaultGranteeWarning, "turning it on must say grantees with withdraw rights can see and take the gear");
            Assert.IsFalse(on.Contains("switched off on this server"), "no kill-switch note while the server has it on");

            var killed = FacetCommands.ComposeVaultToggleLine(FacetCommands.VaultToggleRequest.On, true, false);
            StringAssert.Contains(killed, "switched off on this server");

            StringAssert.Contains(FacetCommands.ComposeVaultToggleLine(FacetCommands.VaultToggleRequest.Show, false, true), "is off");
            StringAssert.Contains(FacetCommands.ComposeVaultToggleLine(FacetCommands.VaultToggleRequest.Show, true, true), "is on");
            Assert.AreEqual(FacetCommands.VaultUsageLine, FacetCommands.ComposeVaultToggleLine(FacetCommands.VaultToggleRequest.Invalid, false, true));

            foreach (var request in new[] { FacetCommands.VaultToggleRequest.On, FacetCommands.VaultToggleRequest.Off, FacetCommands.VaultToggleRequest.Show })
                AssertPrintableAscii(FacetCommands.ComposeVaultToggleLine(request, true, false));
        }

        // ------------------------------------------------------------------ source pins (PROXIES for the wiring)

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
        /// CATCHES: the deposit moved ahead of the commit (above ActiveFacetSlot = targetSlot or the saves) or ahead
        /// of the restore (a shared item would be deposited and withdrawn straight back), the toggle or the kill
        /// switch dropped from the condition, the deposit called twice, and the gate not told about the deposit.
        /// </summary>
        [TestMethod]
        public void Wiring_TrySwitchFacet_DepositsAfterTheCommitAndTheRestore_OnlyWhenBothSwitchesAreOn()
        {
            var body = MethodBody(ReadServerFile("WorldObjects", "Player_Facets.cs"), "public bool TrySwitchFacet(");

            const string wanted = "var facetVaultDepositWanted = FacetVaultDeposit && FacetTunables.VaultDepositEnabledSource();";
            const string condition = "var facetVaultDepositOn = facetVaultDepositWanted && !skipVaultDeposit;";
            const string call = "vaultDepositReport = DepositOutgoingFacetGear(outgoingEquip, incomingEquip);";

            Assert.AreEqual(1, CountOf(body, wanted), "the opt-in and the kill switch must both be in the one condition, read once");
            Assert.AreEqual(1, CountOf(body, condition), "the deposit is on only when wanted and not skipped by an expired deposit-only wait");
            Assert.IsTrue(body.IndexOf(wanted, StringComparison.Ordinal) < body.IndexOf(condition, StringComparison.Ordinal), "fixture: wanted is computed first");
            Assert.AreEqual(1, CountOf(body, "DepositOutgoingFacetGear("), "the deposit runs once per switch");

            var conditionAt = body.IndexOf(condition, StringComparison.Ordinal);
            var gate = body.IndexOf("DecideFacetVaultGate(incomingEquip, facetLocalGuids, outgoingWantsVault,", StringComparison.Ordinal);
            var commit = body.IndexOf("ActiveFacetSlot = targetSlot;", StringComparison.Ordinal);
            var saves = body.IndexOf("SaveCharacterToDatabase();", StringComparison.Ordinal);
            var restore = body.IndexOf("restoreReport = RestoreFacetEquip(incomingEquip", StringComparison.Ordinal);
            var callAt = body.IndexOf(call, StringComparison.Ordinal);
            var ifHead = body.LastIndexOf("if (facetVaultDepositOn)", callAt, StringComparison.Ordinal);
            var summary = body.IndexOf("var summary = new System.Text.StringBuilder();", StringComparison.Ordinal);
            var appended = body.IndexOf("summary.Append(\" \" + string.Join(\" \", vaultDepositReport));", StringComparison.Ordinal);

            Assert.IsTrue(commit >= 0 && saves >= 0 && restore >= 0 && summary >= 0, "fixture: an anchor in TrySwitchFacet has moved");
            Assert.IsTrue(gate >= 0, "the vault gate must be told whether the deposit wants the vault");
            Assert.IsTrue(conditionAt >= 0 && conditionAt < gate, "the condition is read once, before the gate");
            Assert.IsTrue(callAt >= 0, "TrySwitchFacet must call the deposit");
            Assert.IsTrue(ifHead > restore, "the deposit's guard must come after the restore");
            Assert.IsTrue(ifHead > commit && ifHead > saves, "the deposit must come after the switch commits");
            Assert.IsTrue(ifHead < callAt && callAt < summary, "the deposit runs inside its guard, before the summary is built");
            Assert.IsTrue(appended > summary, "the deposit's lines must reach the summary the player reads");
        }

        private static string Squash(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        /// <summary>
        /// F1 (review): THE TOGGLE-OFF RULE AT THE CALL SITE. With the deposit off, the pre-strip selection must not run
        /// and outgoingWantsVault must stay false, so the gate never probes the vault for it. CATCHES the guard being
        /// replaced by `if (true)` (or dropped), and outgoingWantsVault being assigned anywhere else.
        /// </summary>
        [TestMethod]
        public void Wiring_TrySwitchFacet_OutgoingWantsVault_IsSetOnlyUnderTheToggle()
        {
            var body = Squash(MethodBody(ReadServerFile("WorldObjects", "Player_Facets.cs"), "public bool TrySwitchFacet("));

            var conditionAt = body.IndexOf("var facetVaultDepositOn = facetVaultDepositWanted && !skipVaultDeposit;", StringComparison.Ordinal);
            var declare = body.IndexOf("var outgoingWantsVault = false;", StringComparison.Ordinal);
            var guarded = body.IndexOf("if (facetVaultDepositOn) { var incomingGuids", StringComparison.Ordinal);
            var gate = body.IndexOf("DecideFacetVaultGate(incomingEquip, facetLocalGuids, outgoingWantsVault,", StringComparison.Ordinal);

            Assert.IsTrue(conditionAt >= 0 && declare >= 0 && gate >= 0, "fixture: an anchor in TrySwitchFacet has moved");
            Assert.IsTrue(guarded >= 0, "the exact `if (facetVaultDepositOn)` must open the block that builds incomingGuids");
            Assert.IsTrue(conditionAt < declare && declare < guarded && guarded < gate, "condition, declaration, guard, gate - in that order");

            var block = body.Substring(guarded, body.IndexOf('}', guarded) - guarded);
            StringAssert.Contains(block, "outgoingWantsVault = SelectFacetDepositCandidates(", "the only assignment is inside the guard");

            Assert.AreEqual(2, CountOf(body, "outgoingWantsVault ="), "declared false once, assigned once inside the guard, never elsewhere");
        }

        /// <summary>
        /// CATCHES: the production detach, flush or hand-back drifting from the sell path's rules, and the one-argument
        /// Enqueue (which reads a throw as a refusal) creeping into the core.
        /// </summary>
        [TestMethod]
        public void Wiring_VaultPackDeposit_UsesTheVendorRules()
        {
            var text = ReadServerFile("Entity", "AccountVault", "VaultPackDeposit.cs");

            var production = MethodBody(text, "internal static VaultPackDepositResult DepositFromPack(");
            StringAssert.Contains(production, "Player.RemoveFromInventoryAction.SpendItem, deferredSaves");
            StringAssert.Contains(production, "Player.FlushDeferredSaves(deferredSaves)");
            StringAssert.Contains(production, "TryCreateInInventoryWithNetworking(item, out _, burdenCheck: false)");
            StringAssert.Contains(production, "item.SaveBiotaToDatabase();");

            var core = MethodBody(text, "internal static VaultPackDepositResult DepositAndDispose(");
            Assert.AreEqual(2, CountOf(core, ".Enqueue(work, out "), "the batch and its one retry, both with the two-argument Enqueue");
            Assert.AreEqual(0, CountOf(core, ".Enqueue(work)"), "never the one-argument Enqueue");
            Assert.AreEqual(1, CountOf(core, ".TryDepositBatch("), "ONE batch call");
            StringAssert.Contains(core, "!outcome.Deposited && outcome.Commit == VaultDepositCommit.None", "after a throw, only None goes home");
            Assert.IsTrue(core.IndexOf("flushDetachSaves?.Invoke();", StringComparison.Ordinal) < core.IndexOf(".Enqueue(work, out ", StringComparison.Ordinal),
                "the detach saves are flushed before the batch is queued");
        }

        /// <summary>
        /// CATCHES: either production selection call losing the PvP template vault gate (an issued item would then
        /// be deposited), or the gate being swapped for a different template action.
        /// </summary>
        [TestMethod]
        public void Wiring_BothSelections_PassTheTemplateVaultGate()
        {
            var text = ReadServerFile("WorldObjects", "Player_Facets.cs");

            StringAssert.Contains(text, "private bool FacetDepositTemplateBlocked(WorldObject item) => PvpTemplateBlocked(PvpTemplateAction.Vault, item) != null;");

            var deposit = MethodBody(text, "private List<string> DepositOutgoingFacetGear(");
            StringAssert.Contains(deposit, "SelectFacetDepositCandidates(outgoingEquip, wornNow, incomingRemembered, Inventory.Values.ToList(), FacetDepositTemplateBlocked)");

            var sw = MethodBody(text, "public bool TrySwitchFacet(");
            StringAssert.Contains(sw, "SelectFacetDepositCandidates(outgoingEquip, null, incomingGuids, EquippedObjects.Values.ToList(), FacetDepositTemplateBlocked)");
            StringAssert.Contains(sw, "DepositOutgoingFacetGear(outgoingEquip, incomingEquip)", "the deposit must be handed the incoming remembered set");
        }

        /// <summary>CATCHES: /facet vault moved behind the family cooldown, or out from behind the facet_enabled refusal.</summary>
        [TestMethod]
        public void Wiring_FacetVault_IsExemptFromTheCooldown_ButNotFromFacetEnabled()
        {
            var body = MethodBody(ReadServerFile("Command", "Handlers", "FacetCommands.cs"), "public static void HandleFacet(");

            var enabled = body.IndexOf("if (!FacetTunables.DialSource().Enabled)", StringComparison.Ordinal);
            var vault = body.IndexOf("HandleVault(player, parameters.Skip(1).ToArray());", StringComparison.Ordinal);
            var cooldown = body.IndexOf("TryStartFacetCommandCore(", StringComparison.Ordinal);

            Assert.IsTrue(enabled >= 0 && vault >= 0 && cooldown >= 0, "fixture: an anchor in HandleFacet has moved");
            Assert.IsTrue(enabled < vault, "facets switched off must refuse /facet vault too");
            Assert.IsTrue(vault < cooldown, "/facet vault does no shard read and is exempt from the cooldown");
        }
    }
}
