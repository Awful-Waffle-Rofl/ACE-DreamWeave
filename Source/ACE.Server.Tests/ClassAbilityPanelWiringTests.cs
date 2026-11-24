using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source pins for the class ability web panel's Player-side wiring, which no test here can drive (a Player cannot
    /// be constructed in this harness): the revision bumps on both funnels every rank and balance change passes
    /// through, the Can* checks run the SAME validators as the mutators, the one-rank lower finishes like the unlearn
    /// tail, and the live service runs its writes through those methods on the world queue.
    /// </summary>
    [TestClass]
    public class ClassAbilityPanelWiringTests
    {
        private static string Source(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = ClassAbilityAffinityDeclarationTests.RepoRoot();
            Array.Copy(parts, 0, all, 1, parts.Length);
            return File.ReadAllText(Path.Combine(all)).Replace("\r\n", "\n");
        }

        private static string Abilities => Source("Source", "ACE.Server", "WorldObjects", "Player_ClassAbilities.cs");
        private static string Panel => Source("Source", "ACE.Server", "WorldObjects", "Player_ClassAbilityPanel.cs");
        private static string Service => Source("Source", "ACE.Server", "Managers", "Market", "ClassAbilityPanelService.cs");

        /// <summary>The text from <paramref name="start"/> to the next occurrence of <paramref name="end"/>.</summary>
        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0, $"not found: {start}");
            var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.IsTrue(to > from, $"could not isolate {start}");
            return text.Substring(from, to - from);
        }

        private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

        [TestMethod]
        public void Revision_BumpsInBothFunnels()
        {
            var adjust = Between(Abilities, "private void AdjustClassAbilityPoints(", "public bool TryAdminAdjustClassAbilityPoints(");
            StringAssert.Contains(adjust, "ClassAbilityRevision.Bump();", "every CAP balance write must bump the panel revision");

            var invalidate = Between(Abilities, "private void InvalidateClassAbilityHookCaches()", "public void ApplyOutgoingDamageClassAbilities(");
            StringAssert.Contains(invalidate, "ClassAbilityRevision.Bump();", "every rank change must bump the panel revision");
        }

        [TestMethod]
        public void Revision_IsPerPlayerInstance()
        {
            // a per-instance field, created once (CompareExchange) on first use, never a static
            StringAssert.Contains(Panel, "private ClassAbilityRevisionCounter classAbilityRevision;");
            StringAssert.Contains(Panel, "Interlocked.CompareExchange(ref classAbilityRevision, new ClassAbilityRevisionCounter(), null);");
            Assert.AreEqual(0, Count(Panel, "static ClassAbilityRevisionCounter"));
        }

        [TestMethod]
        public void CanRaise_RunsTheLearnValidator_ThenTheTrainersVoucherRefusal()
        {
            var learn = Between(Abilities, "public bool LearnClassAbility(ClassAbilityDefinition skill, out string error)", "AdjustClassAbilityPoints(");
            StringAssert.Contains(learn, "if (!ValidateClassAbilityLearn(skill, out var rank, out var cost, out error))");

            var canRaise = Between(Panel, "public bool CanRaiseClassAbility(", "public bool CanLowerClassAbility(");
            var validate = canRaise.IndexOf("ValidateClassAbilityLearn(skill,", StringComparison.Ordinal);
            var voucher = canRaise.IndexOf("ClassAbilityTrainer.VoucherHeldRefusal(skill)", StringComparison.Ordinal);
            Assert.IsTrue(validate >= 0 && voucher > validate, "the learn validator, then the voucher refusal");
            StringAssert.Contains(canRaise, "ClassAbilityTrainer.HoldsVoucherFor(this, skill.Id)");
            Assert.AreEqual(0, Count(canRaise, "AdjustClassAbilityPoints("), "CanRaise must move nothing");
        }

        [TestMethod]
        public void CanLower_RunsTheRemovalValidator_AndTheFeeSentence()
        {
            var canLower = Between(Panel, "public bool CanLowerClassAbility(", "public bool CanUnlearnClassAbility(");
            StringAssert.Contains(canLower, "ValidateClassAbilityRankRemoval(skill, lowerOneRank: true, CapLedgerReason.Lower,");
            StringAssert.Contains(canLower, "return CanAffordClassAbilityRemovalFee(out refusal);");
            StringAssert.Contains(Between(Panel, "private bool CanAffordClassAbilityRemovalFee(", "\n    }\n}"), "ClassAbilityRemovalFeeRefusal(fee, spendable)");
            Assert.AreEqual(0, Count(canLower, "TrySpendLuminanceIncludingBank"), "CanLower must charge nothing");
            Assert.AreEqual(0, Count(canLower, "AdjustClassAbilityPoints("), "CanLower must move nothing");

            var charge = Between(Abilities, "private bool TryChargeClassAbilityRemovalFee(out string error)", "public bool LowerClassAbilityRank(");
            StringAssert.Contains(charge, "error = ClassAbilityRemovalFeeRefusal(fee, GetSpendableLuminance());", "the charge and the check share one sentence");
        }

        [TestMethod]
        public void Lower_FinishesLikeTheUnlearnTail()
        {
            var lower = Between(Abilities, "public bool LowerClassAbilityRank(", "\n        }\n");

            string[] order =
            {
                "ValidateClassAbilityRankRemoval(skill, lowerOneRank: true, CapLedgerReason.Lower,",
                "if (newRank <= 0)\n                return UnlearnClassAbility(skill, out error, out refunded);",
                "TryChargeClassAbilityRemovalFee(out error)",
                "refunded = ClassAbilityRankRemoval.Refund(skill, rank, newRank);",
                "quest.NumTimesCompleted = newRank;",
                "GetClassAbilityCache()[skill.Id] = newRank;",
                "AdjustClassAbilityPoints(refunded, 0, CapLedgerReason.Lower,",
                "InvalidateClassAbilityHookCaches();",
                "SendEnhancedStatUpdate(skill);",
                "SaveBiotaToDatabase();",
                "SaveCharacterToDatabase();",
            };

            var at = -1;
            foreach (var needle in order)
            {
                var next = lower.IndexOf(needle, at + 1, StringComparison.Ordinal);
                Assert.IsTrue(next > at, $"out of order or missing: {needle}");
                at = next;
            }

            StringAssert.Contains(lower, "rankAfter: newRank", "the ledger row records the rank left");
            Assert.AreEqual(0, Count(lower, "EraseQuest("), "lowering rank 1 delegates to UnlearnClassAbility; the lower itself never erases");
        }

        [TestMethod]
        public void LiveService_WritesThroughThePlayersOwnMethods_OnTheWorldQueue()
        {
            var apply = Between(Service, "private static ClassAbilityPanelChangeResult ApplyOnWorld(", "private static Player FindEligible(");

            StringAssert.Contains(apply, "player.CanRaiseClassAbility(skill, out error) && player.LearnClassAbility(skill, out error)");
            StringAssert.Contains(apply, "player.CanLowerClassAbility(skill, out error) && player.LowerClassAbilityRank(skill, out error, out refunded)");
            StringAssert.Contains(apply, "player.CanUnlearnClassAbility(skill, out error) && player.UnlearnClassAbility(skill, out error, out refunded)");
            StringAssert.Contains(apply, "if (ClassAbilityPanelModel.IsStale(player.GetClassAbilityRank(skill.Id), change.FromRank))", "the compare-and-set reads the LIVE rank");
            StringAssert.Contains(apply, "return new ClassAbilityPanelChangeResult(MarketError.NotInVaultArea, area, Snapshot(player, launchBound: true));", "the area refusal carries a fresh panel");

            // contract order on the world thread: online, location, compare-and-set, then the rules
            var online = apply.IndexOf("MarketError.CharacterOffline", StringComparison.Ordinal);
            var area = apply.IndexOf("MarketError.NotInVaultArea", StringComparison.Ordinal);
            var stale = apply.IndexOf("MarketError.Stale", StringComparison.Ordinal);
            var rules = apply.IndexOf("switch (change.Kind)", StringComparison.Ordinal);
            Assert.IsTrue(online >= 0 && online < area && area < stale && stale < rules);

            Assert.AreEqual(0, Count(Service, "NetworkManager.Find("), "NetworkManager.Find throws with two sessions on one account");
            Assert.AreEqual(0, Count(Service, ".EnqueueAction("), "world work goes through the injected world queue, never Player.EnqueueAction");

            var peek = Between(Service, "public string PeekRevision(uint characterGuid)", "public ClassAbilityPanelReadResult Read(");
            Assert.AreEqual(0, Count(peek, "AdminWorldCall"), "the since fast path must not visit the world thread");
        }

        [TestMethod]
        public void Readout_LiveSubjectAsksTheHandler_OfflineSubjectNever()
        {
            var live = Between(Service, "private sealed class PlayerSubject", "private sealed class OfflineSubject");
            StringAssert.Contains(live, "ClassAbilityRegistry.GetHandler(ability.Id) is IAbilityReadout source ? source.GetReadout(player, rank) : null");

            var offline = Between(Service, "private sealed class OfflineSubject", "\n    }\n}");
            StringAssert.Contains(offline, "public ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank) => null;");
        }

        /// <summary>
        /// The web panel's chat line: exactly one send in the whole write path, on the Broadcast channel the in-game
        /// lines use, built by ChangeChatLine, and placed after every refusal return (offline, not in area, stale,
        /// invalid, refused), so only a committed change says anything. A real Player cannot be built here.
        /// </summary>
        [TestMethod]
        public void ChangeChat_OneSend_OnlyAfterTheChangeCommitted()
        {
            var apply = Between(Service, "private static ClassAbilityPanelChangeResult ApplyOnWorld(", "private static Player FindEligible(");

            Assert.AreEqual(1, Regex.Matches(Service, @"EnqueueSend\(").Count, "one chat send in the panel service");
            Assert.AreEqual(1, Regex.Matches(apply, @"EnqueueSend\(").Count, "and it is in ApplyOnWorld");
            Assert.AreEqual(0, Regex.Matches(Panel, @"EnqueueSend\(|GameMessageSystemChat").Count, "the Player's panel validators send nothing");

            var send = apply.IndexOf("player.Session.Network.EnqueueSend(new GameMessageSystemChat(", StringComparison.Ordinal);
            Assert.IsTrue(send > 0, "the send");
            StringAssert.Contains(apply.Substring(send), "ClassAbilityPanelModel.ChangeChatLine(change.Kind, skill, player.GetClassAbilityRank(skill.Id), refunded,");
            StringAssert.Contains(apply.Substring(send), "ChatMessageType.Broadcast));");

            var refusals = Regex.Matches(apply, @"return new ClassAbilityPanelChangeResult\(MarketError\.(?!None)\w+");
            Assert.AreEqual(7, refusals.Count, "disabled, offline, state, not in area, stale, invalid, refused: " + string.Join(", ", refusals.Select(m => m.Value)));
            Assert.IsTrue(refusals.All(m => m.Index < send), "every refusal returns before the send");

            var applied = apply.IndexOf("if (!applied)", StringComparison.Ordinal);
            Assert.IsTrue(applied > 0 && applied < send, "the send follows the applied check");

            var success = apply.IndexOf("return new ClassAbilityPanelChangeResult(MarketError.None", StringComparison.Ordinal);
            Assert.IsTrue(send < success, "and precedes the success answer");
        }

        /// <summary>Audit 1: the shared removal core refuses a PvP-templated player right after the arena check.</summary>
        [TestMethod]
        public void RemovalCore_RefusesWhilePvpTemplated_RightAfterTheArena()
        {
            var core = Between(Abilities, "private bool ValidateClassAbilityRankRemoval(", "private static long ClassAbilityRemovalFee()");

            var arena = core.IndexOf("if (IsInPvpMatch)", StringComparison.Ordinal);
            var template = core.IndexOf("var tpl = PvpTemplateBlocked(PvpTemplateAction.ClassAbilityPoints);\n            if (tpl != null)\n            {\n                error = tpl;\n                return false;\n            }", StringComparison.Ordinal);
            var reprice = core.IndexOf("if (ClassAbilityRepricePendingFor(skill.Id))", StringComparison.Ordinal);

            Assert.IsTrue(arena >= 0 && template > arena && reprice > template, $"arena {arena}, template {template}, reprice {reprice}");

            // every removal path runs the core first: unlearn, lower and both panel validators
            StringAssert.Contains(Abilities, "if (!ValidateClassAbilityRankRemoval(skill, lowerOneRank: false, reason, out var rank, out _, out error))");
            StringAssert.Contains(Abilities, "if (!ValidateClassAbilityRankRemoval(skill, lowerOneRank: true, CapLedgerReason.Lower, out var rank, out var newRank, out error))");
            Assert.AreEqual(2, Count(Panel, "ValidateClassAbilityRankRemoval("), "CanLower and CanUnlearn");
        }

        /// <summary>Audit 2: the trainer's Yes buys the quoted rank only; a rank changed elsewhere since the prompt refuses.</summary>
        [TestMethod]
        public void TrainerYes_RefusesWhenTheQuotedRankChanged()
        {
            var trainer = Source("Source", "ACE.Server", "ClassAbilities", "ClassAbilityTrainer.cs");
            var closure = Between(trainer, "var capturedDef = def;", "OfferChoices(player, choices, 0);");

            var check = closure.IndexOf("if (player.GetClassAbilityRank(capturedDef.Id) != nextTier - 1)\n                            Send(player, QuoteChangedRefusal);", StringComparison.Ordinal);
            var learn = closure.IndexOf("player.LearnClassAbility(capturedDef, out var error)", StringComparison.Ordinal);
            Assert.IsTrue(check >= 0 && learn > check, "the rank check runs before the learn");
            StringAssert.Contains(closure, "else if (!player.LearnClassAbility(capturedDef, out var error))", "and the learn only on the quoted rank");

            Assert.AreEqual("That ability changed since I quoted it. Speak to me again.", ACE.Server.ClassAbilities.ClassAbilityTrainer.QuoteChangedRefusal);
        }

        /// <summary>Audit 4: the switches are re-read first thing on the world thread, and Program passes the route's own switch in.</summary>
        [TestMethod]
        public void ApplyOnWorld_RereadsTheSwitches_First()
        {
            var apply = Between(Service, "private static ClassAbilityPanelChangeResult ApplyOnWorld(", "private static Player FindEligible(");

            var gate = apply.IndexOf("if (!panelEnabled())", StringComparison.Ordinal);
            var find = apply.IndexOf("var player = FindEligible(accountId, characterGuid);", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0 && gate < find, "the switch check comes before anything else");
            StringAssert.Contains(apply, "return new ClassAbilityPanelChangeResult(MarketError.PanelDisabled, disabledText,");

            var program = Source("Source", "ACE.Server", "Program.cs");
            StringAssert.Contains(program, "apiOptions.RequestTimeoutMs),\n                apiOptions.ClassAbilityPanelEnabled);");
        }

        /// <summary>Audit 6: dead, busy, teleporting and the PK logout countdown refuse a write (rule_refused) and make the snapshot read-only.</summary>
        [TestMethod]
        public void StateGate_ReadsTheFourInputs_RefusesWrites_AndReadOnlySnapshots()
        {
            StringAssert.Contains(Service,
                "ClassAbilityPanelModel.StateRefusal(dead: player.IsDead, busy: player.IsBusy, teleporting: player.Teleporting, pkLogout: player.PKLogout);");

            var apply = Between(Service, "private static ClassAbilityPanelChangeResult ApplyOnWorld(", "private static Player FindEligible(");
            var state = apply.IndexOf("var state = StateRefusal(player);", StringComparison.Ordinal);
            var refuse = apply.IndexOf("return new ClassAbilityPanelChangeResult(MarketError.RuleRefused, state, Snapshot(player, launchBound: true));", StringComparison.Ordinal);
            var area = apply.IndexOf("MuleSummonHandler.ClassAbilityPanelAreaRefusal(player.Location)", StringComparison.Ordinal);
            Assert.IsTrue(state > 0 && refuse > state && area > refuse, "the state gate runs after online and before the area");

            var snapshot = Between(Service, "private static ClassAbilityPanelSnapshot Snapshot(", "private static string StateRefusal(Player player)");
            StringAssert.Contains(snapshot, "refusal = ClassAbilityPanelModel.WriteRefusal(launchBound, online: true, state, area);", "reads go read-only with the sentence");
            StringAssert.Contains(snapshot, "var area = launchBound && state == null ? MuleSummonHandler.ClassAbilityPanelAreaRefusal(player.Location) : null;",
                "the area is not asked once a state refusal already wins");
        }

        /// <summary>
        /// F2: the full respec charges its flat Luminance fee BEFORE the per-ability unlearn loop, so the PvP template
        /// refusal (which that loop's shared removal core would raise per ability) must come first, like the arena one.
        /// </summary>
        [TestMethod]
        public void FullRespec_RefusesWhilePvpTemplated_BeforeTheFee()
        {
            var trainer = Source("Source", "ACE.Server", "ClassAbilities", "ClassAbilityTrainer.cs");
            var respec = Between(trainer, "private static void HandleRespec(WorldObject npc, Player player)", "\n        private static ");

            var arena = respec.IndexOf("if (player.IsInPvpMatch)", StringComparison.Ordinal);
            var template = respec.IndexOf("var tpl = player.PvpTemplateBlocked(PvpTemplateAction.ClassAbilityPoints);\n                if (tpl != null)\n                {\n                    Send(player, tpl);\n                    return;\n                }", StringComparison.Ordinal);
            var fee = respec.IndexOf("TrySpendLuminanceIncludingBank(currentFee)", StringComparison.Ordinal);
            var loop = respec.IndexOf("player.UnlearnClassAbility(", StringComparison.Ordinal);

            Assert.IsTrue(arena > 0 && template > arena && fee > template && loop > fee, $"arena {arena}, template {template}, fee {fee}, loop {loop}");
        }

        [TestMethod]
        public void FindEligible_GathersEveryInputOfTheRule()
        {
            var find = Between(Service, "private static Player FindEligible(", "private static ClassAbilityPanelSnapshot Snapshot(");

            StringAssert.Contains(find, "var eligible = ClassAbilityPanelModel.IsEligible(accountId,");
            StringAssert.Contains(find, "playerFound: player != null,");
            StringAssert.Contains(find, "hasSession: session != null,");
            StringAssert.Contains(find, "sessionAccountId: session?.AccountId ?? 0,");
            StringAssert.Contains(find, "worldConnected: session?.State == SessionState.WorldConnected,");
            StringAssert.Contains(find, "sessionHoldsPlayer: session != null && ReferenceEquals(session.Player, player),");
            StringAssert.Contains(find, "loggingOut: player?.IsLoggingOut ?? true);");
            StringAssert.Contains(find, "return eligible ? player : null;");
        }

        [TestMethod]
        public void CanUnlearn_RunsTheUnlearnValidator()
        {
            var canUnlearn = Between(Panel, "public bool CanUnlearnClassAbility(", "private bool CanAffordClassAbilityRemovalFee(");
            StringAssert.Contains(canUnlearn, "ValidateClassAbilityRankRemoval(skill, lowerOneRank: false, CapLedgerReason.Unlearn,", "an unlearn refusal must say unlearn");
        }

        [TestMethod]
        public void AdminWorldCall_KeepsTheAdminLogPrefix_PanelHasItsOwn()
        {
            var call = Source("Source", "ACE.Server", "Managers", "Market", "AdminWorldCall.cs");
            StringAssert.Contains(call, "internal const string DefaultLogPrefix = \"[MARKET][ADMIN] a queued admin action\";");
            StringAssert.Contains(call, "string logPrefix = DefaultLogPrefix)");
            StringAssert.Contains(call, "log.Warn($\"{logPrefix} threw: {ex.GetFullMessage()}\");");

            StringAssert.Contains(Service, "PanelReadLogPrefix = \"[MARKET][PANEL] ");
            StringAssert.Contains(Service, "PanelChangeLogPrefix = \"[MARKET][PANEL] ");
        }

        [TestMethod]
        public void Program_WiresBothSwitchesAndTheWorldQueue()
        {
            var program = Source("Source", "ACE.Server", "Program.cs");

            StringAssert.Contains(program, "apiOptions.ClassAbilityPanelEnabled = () => PropertyManager.GetBool(\"class_abilities_enabled\").Item\n                && PropertyManager.GetBool(\"class_ability_web_panel_enabled\").Item;");
            StringAssert.Contains(program, "new ACE.Server.Managers.Market.LiveClassAbilityPanelService(\n                WorldManager.EnqueueAction,");
            StringAssert.Contains(program, "MarketApiOptions.EffectivePanelWorldTimeoutMs(apiOptions.ClassAbilityPanelWorldTimeoutMs, apiOptions.RequestTimeoutMs)", "the world wait is clamped where the host is configured");
        }

        [TestMethod]
        public void WebPanelSwitch_ShipsOn()
        {
            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties["class_ability_web_panel_enabled"].Item);
        }
    }
}
