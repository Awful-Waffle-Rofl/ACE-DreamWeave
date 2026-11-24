using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// Where the Avatar lifecycle is called from the allegiance code. Each pin names the call and its landmark, so a
    /// call removed, renamed or moved to the wrong side of its landmark fails here.
    /// </summary>
    [TestClass]
    public class AvatarAllegianceHookSiteTests
    {
        private const string PlayerAllegiance = "Source/ACE.Server/WorldObjects/Player_Allegiance.cs";
        private const string AllegianceManagerFile = "Source/ACE.Server/Managers/AllegianceManager.cs";

        private static string Body(string file, string signature) => PooledLootSourceText.MethodBody(PooledLootSourceText.Read(file), signature);

        private static int At(string body, string text, int from = 0)
        {
            var i = body.IndexOf(text, from, StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, $"missing: {text}");
            return i;
        }

        private static int Count(string body, string text)
        {
            var n = 0;
            for (var i = body.IndexOf(text, StringComparison.Ordinal); i >= 0; i = body.IndexOf(text, i + 1, StringComparison.Ordinal))
                n++;
            return n;
        }

        // ---- swear ---------------------------------------------------------------------------------------------

        private static string SwearBody() => Body(PlayerAllegiance,
            "public void SwearAllegiance(uint targetGuid, bool success, bool confirmed = false, AvatarSnapshot avatarSnapshot = null)");

        [TestMethod]
        public void Swear_UnconfirmedBranch_TriesTheAvatarPopupBeforeTheClientSwearPopup()
        {
            var body = SwearBody();
            var unconfirmed = At(body, "if (!confirmed)");
            var avatar = At(body, "if (TryBeginAvatarSwear(patron))");
            var plain = At(body, "new Confirmation_SwearAllegiance(patron.Guid, Guid)");

            Assert.IsTrue(unconfirmed < avatar && avatar < plain, "the Avatar popup must be tried first inside the unconfirmed branch");
        }

        [TestMethod]
        public void Swear_ConfirmedPath_ChecksTheSnapshotBeforeAnyPropertyChange_AndBindsOnlyAfterTheSwear()
        {
            var body = SwearBody();
            var plain = At(body, "new Confirmation_SwearAllegiance(patron.Guid, Guid)");
            var check = At(body, "if (!AvatarLifecycle.CheckConfirmedSwear(avatarSnapshot, ");
            var patronSet = At(body, "PatronId = targetGuid;");
            var swear = At(body, "AllegianceManager.OnSwearAllegiance(this);");
            var bind = At(body, "AvatarLifecycle.CompleteSwear(monarchGuid, avatarSnapshot);");

            Assert.IsTrue(plain < check && check < patronSet, "the confirm re-check must run before PatronId changes");
            Assert.IsTrue(At(body, "return;", check) < patronSet, "a failed re-check must return before PatronId changes");
            Assert.IsTrue(swear < bind, "the Avatar moves only after the swear has happened");
            Assert.AreEqual(1, Count(body, "AvatarLifecycle.CompleteSwear("));
        }

        [TestMethod]
        public void AvatarSwearPopup_AsksTheMonarchFirst_ThenThePatronWithAChoice_CarryingTheSnapshot()
        {
            var body = Body(PlayerAllegiance, "private bool TryBeginAvatarSwear(Player patron)");

            var plan = At(body, "AvatarLifecycle.PlanSwear(");
            var none = At(body, "if (plan == null || plan.Effect == SwearAvatarEffect.None)");
            var recheck = At(body, "if (!AvatarLifecycle.CheckConfirmedSwear(plan.Snapshot, ");
            var patronYes = At(body, ".SwearAllegiance(patronGuid.Full, true, true, plan.Snapshot)");
            var patronSend = At(body, "target.ConfirmationManager.EnqueueSend(offer, ");
            var monarchSend = At(body, "ConfirmationManager.EnqueueSend(ask, plan.SwearerText)");

            // Both Choice Yes paths run on the answering player's action queue: each hands its work to the world thread.
            var monarchYes = At(body, "var ask = new Confirmation_Choice(Guid, () => WorldManager.EnqueueAction(new ActionEventDelegate(() =>");
            var patronYesWrapped = At(body, "() => WorldManager.EnqueueAction(new ActionEventDelegate(() => PlayerManager.GetOnlinePlayer(vassalGuid)?.SwearAllegiance(patronGuid.Full, true, true, plan.Snapshot)))");
            var monarchYesEnd = At(body, "})), null, DateTime.UtcNow);", patronSend);
            Assert.IsTrue(monarchYes < recheck && recheck < patronYesWrapped && monarchYesEnd < monarchSend,
                "the monarch's re-check and the patron popup run inside the world-thread action");

            Assert.IsTrue(plan < none, "classify first");
            Assert.IsTrue(recheck < patronYes && patronYes < patronSend, "the patron popup is sent from the monarch's Yes, after a re-check");
            Assert.IsTrue(patronSend < monarchSend, "the patron popup lives inside the monarch's Yes callback");
            Assert.AreEqual(-1, body.IndexOf("Confirmation_SwearAllegiance", StringComparison.Ordinal), "the patron gets a Confirmation_Choice, not the client swear popup");
            At(body, "You are already answering another question.");
        }

        // ---- break and boot ------------------------------------------------------------------------------------

        [TestMethod]
        public void Break_IsGated_AndTheBodyRunsOnlyThroughTheGate()
        {
            var body = Body(PlayerAllegiance, "public void HandleActionBreakAllegiance(uint targetGuid)");

            At(body, "GateAvatarBreak(() => PlanAvatarBreak(targetGuid), () => IsBreakable(targetGuid), () => DoBreakAllegiance(targetGuid));");
            Assert.AreEqual(-1, body.IndexOf("OnBreakAllegiance(", StringComparison.Ordinal), "the break body must have moved into DoBreakAllegiance");

            At(Body(PlayerAllegiance, "private void DoBreakAllegiance(uint targetGuid)"), "AllegianceManager.OnBreakAllegiance(this, target);");
        }

        [TestMethod]
        public void Boot_IsGated_AfterItsChecks_AndTheBodyRunsOnlyThroughTheGate()
        {
            var body = Body(PlayerAllegiance, "public void HandleActionBreakAllegianceBoot(string playerName, bool accountBoot)");

            var lastCheck = At(body, "You cannot boot the monarch from the allegiance!");
            var gate = At(body, "GateAvatarBreak(");
            var doBoot = At(body, "DoBreakAllegianceBoot(current, accountBoot);", gate);

            Assert.IsTrue(lastCheck < gate && gate < doBoot);
            Assert.AreEqual(-1, body.IndexOf("OnBreakAllegiance(", StringComparison.Ordinal), "the boot body must have moved into DoBreakAllegianceBoot");

            At(Body(PlayerAllegiance, "private void DoBreakAllegianceBoot(IPlayer player, bool accountBoot)"), "AllegianceManager.OnBreakAllegiance(player, patron);");
        }

        [TestMethod]
        public void Boot_Yes_BootsTheCurrentRecord_NotTheOneCapturedAtTheAsk()
        {
            var body = Body(PlayerAllegiance, "public void HandleActionBreakAllegianceBoot(string playerName, bool accountBoot)");

            var gate = At(body, "GateAvatarBreak(");
            var resolve = At(body, "var current = PlayerManager.FindByGuid(player.Guid);", gate);
            var missing = At(body, "if (current == null)", resolve);
            var missingReturn = At(body, "return;", missing);
            var boot = At(body, "DoBreakAllegianceBoot(current, accountBoot);", missingReturn);

            Assert.IsTrue(resolve < missing && missingReturn < boot, "re-resolve, return when gone, then boot the current record");
            Assert.AreEqual(1, Count(body, "DoBreakAllegianceBoot("), "the boot runs only through the re-resolved record");
            Assert.AreEqual(0, Count(body, "DoBreakAllegianceBoot(player"), "never the IPlayer captured when the popup was asked");
        }

        /// <summary>The first statement of the block that opens after <paramref name="landmark"/>, skipping line comments.</summary>
        private static string FirstStatementAfter(string body, string landmark)
        {
            var open = body.IndexOf('{', At(body, landmark) + landmark.Length - 1);
            Assert.IsTrue(open >= 0, $"no block after: {landmark}");
            foreach (var raw in body.Substring(open + 1).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                return line;
            }
            return "";
        }

        private const string OnlineGuard = "if (PlayerManager.GetOnlinePlayer(Guid.Full) != this)";

        [TestMethod]
        public void MarshalledWorldActions_FirstCheckThisCharacterIsStillTheOnlineOne()
        {
            var swear = Body(PlayerAllegiance, "private bool TryBeginAvatarSwear(Player patron)");
            Assert.AreEqual(OnlineGuard, FirstStatementAfter(swear, "var ask = new Confirmation_Choice(Guid, () => WorldManager.EnqueueAction(new ActionEventDelegate(() =>"),
                "the monarch's swear Yes, on the world thread");

            var gate = Body(PlayerAllegiance, "private void GateAvatarBreak(Func<AvatarBreakGate> plan, Func<bool> stillBreakable, Action doBreak)");
            Assert.AreEqual(OnlineGuard, FirstStatementAfter(gate, "var confirmation = new Confirmation_Choice(Guid, () => WorldManager.EnqueueAction(new ActionEventDelegate(() =>"),
                "the break and boot Yes, on the world thread");

            // The patron's swear Yes already re-resolves the vassal by guid before it acts.
            At(swear, "() => WorldManager.EnqueueAction(new ActionEventDelegate(() => PlayerManager.GetOnlinePlayer(vassalGuid)?.SwearAllegiance(");
        }

        [TestMethod]
        public void BreakGate_PopupYes_Reclassifies_ThenBreaks_ThenCompletes()
        {
            var body = Body(PlayerAllegiance, "private void GateAvatarBreak(Func<AvatarBreakGate> plan, Func<bool> stillBreakable, Action doBreak)");

            var asks = At(body, "if (gate.Effect == BreakAvatarEffect.Inheritance || gate.Effect == BreakAvatarEffect.GoesInactive)");
            var popup = At(body, "var confirmation = new Confirmation_Choice(Guid, () => WorldManager.EnqueueAction(new ActionEventDelegate(() =>", asks);
            var breakable = At(body, "if (!stillBreakable())", popup);
            var notBreakable = At(body, "return;", breakable);
            var again = At(body, "var again = plan();", breakable);
            var match = At(body, "if (!AvatarLifecycle.BreakStillMatches(gate, again, out var reason))", again);
            var cancel = At(body, "return;", match);
            var breakOnYes = At(body, "doBreak();", match);
            var completeOnYes = At(body, "AvatarLifecycle.CompleteBreak(again, Guid.Full);", breakOnYes);
            var wrapEnd = At(body, "})), null, DateTime.UtcNow);", completeOnYes);
            var send = At(body, "ConfirmationManager.EnqueueSend(confirmation, gate.Text)", completeOnYes);
            Assert.IsTrue(wrapEnd < send, "the re-check, the break and CompleteBreak all run inside the one world-thread action");
            var popupReturn = At(body, "return;", send);

            Assert.IsTrue(notBreakable < again, "a target no longer breakable returns before re-planning");
            Assert.IsTrue(cancel < breakOnYes, "a mismatch returns before the break");
            Assert.IsTrue(send > completeOnYes);

            var immediate = At(body, "doBreak();", send);
            Assert.IsTrue(immediate > popupReturn, "the immediate break sits after the popup branch's return");
            At(body, "AvatarLifecycle.CompleteBreak(gate, Guid.Full);", immediate);
            Assert.AreEqual(2, Count(body, "doBreak();"), "the break runs on Yes or at once, never both");
        }

        // ---- login and officers --------------------------------------------------------------------------------

        [TestMethod]
        public void Login_TouchesAndRecomputes_FromTheAllegianceMonarch_OutsideTheDelayedChain()
        {
            var body = Body(PlayerAllegiance, "public void HandleAllegianceOnLogin()");

            var chain = At(body, "actionChain.EnqueueChain();");
            var login = At(body, "AvatarLifecycle.OnLogin(Account?.AccountId, Allegiance?.MonarchId ?? Guid.Full);");

            Assert.IsTrue(chain < login, "the Keeper recompute must not wait for the 3 second chain");
            Assert.AreEqual(1, Count(body, "AvatarLifecycle.OnLogin("));
        }

        [TestMethod]
        public void OnLogin_TouchesTheAccountBeforeAnyRecompute()
        {
            var body = Body("Source/ACE.Server/AllegianceAvatar/AvatarLifecycle.cs", "public static void OnLogin(uint? accountId, uint allegianceMonarchGuid)");

            var touch = At(body, "AvatarManager.TouchAccount(accountId.Value);");
            var firstRecompute = At(body, "RecomputeKeeper(");

            Assert.IsTrue(touch < firstRecompute, "pre-flight 28: refresh the account before the Keeper recompute");
        }

        [TestMethod]
        public void OfficerChanges_RecomputeTheKeeper_AfterBuildOfficers()
        {
            foreach (var signature in new[]
            {
                "public void HandleActionSetAllegianceOfficer(string playerName, uint officerLevel)",
                "public void HandleActionRemoveAllegianceOfficer(string officerName)",
                "public void HandleActionClearAllegianceOfficers()",
            })
            {
                var body = Body(PlayerAllegiance, signature);
                var build = At(body, "Allegiance.BuildOfficers();");
                var recompute = At(body, "AvatarLifecycle.RecomputeKeeper(Allegiance.MonarchId ?? Guid.Full);");
                Assert.IsTrue(build < recompute, signature);
            }
        }

        // ---- delete and the tick -------------------------------------------------------------------------------

        [TestMethod]
        public void Delete_PlansAboveTheGuard_AppliesInTheGuardAndAfterTheRebuild()
        {
            var body = Body(AllegianceManagerFile, "internal static void DoHandlePlayerDelete(uint playerGuid)");

            var load = At(body, "var allegiance = GetAllegiance(player);");
            var plan = At(body, "var avatarPlan = AvatarLifecycle.PlanMonarchDelete(playerGuid);");
            var guard = At(body, "if (allegiance == null)");
            var applySolo = At(body, "AvatarLifecycle.ApplyMonarchDelete(avatarPlan);", guard);
            var guardReturn = At(body, "return;", guard);
            var rebuild = At(body, "Rebuild(GetAllegiance(p));");
            var applySplit = At(body, "AvatarLifecycle.ApplyMonarchDelete(avatarPlan);", rebuild);
            var recompute = At(body, "AvatarLifecycle.RecomputeKeeper(GetMonarch(p).Guid.Full);", applySplit);

            Assert.IsTrue(load < plan && plan < guard, "plan on the pre-split tree, above the allegiance == null guard");
            Assert.IsTrue(applySolo < guardReturn, "a solo monarch's Avatar is applied (retired) before the guard returns");
            Assert.IsTrue(applySplit > rebuild && recompute > applySplit);
            Assert.AreEqual(2, Count(body, "AvatarLifecycle.ApplyMonarchDelete(avatarPlan);"));
        }

        [TestMethod]
        public void ProductionInitialize_WiresTheLiveMonarchStatus_ForTheRecomputeRepair()
        {
            var init = Body("Source/ACE.Server/AllegianceAvatar/AvatarManager.cs", "public static void Initialize()");
            At(init, "AvatarLifecycle.LiveMonarchStatus);");

            // A member's Monarch property holds the monarch's own guid too: only one naming someone else is a vassal.
            var status = Body("Source/ACE.Server/AllegianceAvatar/AvatarLifecycle.cs", "internal static AvatarMonarchStatus LiveMonarchStatus(uint guid)");
            At(status, "player.MonarchId.HasValue && player.MonarchId.Value != guid ? AvatarMonarchStatus.Vassal");
            At(status, "if (player == null || (player is Player online && online.IsDeleted))");
        }

        [TestMethod]
        public void Tick_RecomputesTheKeeperThroughTheLifecycle_PerRoster()
        {
            var body = Body("Source/ACE.Server/AllegianceAvatar/AvatarManager.cs", "private static void Recompute(double now, AvatarTuning t)");

            At(body, "AvatarLifecycle.RecomputeKeeper(monarch, roster);");
            Assert.AreEqual(-1, body.IndexOf("AvatarKeeperResolver.Resolve(", StringComparison.Ordinal), "the Keeper must not be resolved inline, where a change is never announced");
        }
    }
}
