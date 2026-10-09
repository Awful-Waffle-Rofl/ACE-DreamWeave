using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for "instant learn on purchase" - a Drift Network trainer VENDOR's Buy click on a class
    /// ability token now shows the same Yes/No confirmation the item-use path always had, charges class
    /// ability points (CAP) only on Yes, and never creates a token WorldObject at all.
    ///
    /// NO LIVE PLAYER IS CONSTRUCTED HERE. This project's established constraint (see MuleCommerceTests'
    /// class remarks, independently re-verified there) is that Player's constructors call
    /// DatabaseManager.Authentication.GetAccountById unconditionally (a live MySQL round trip) and
    /// SetEphemeralValues needs a real client dat lookup, so no test anywhere in ACE.Server.Tests builds
    /// one. The code under test here - Player.TryLearnClassAbilityFromVendor and
    /// Vendor.OfferClassAbilityInstantLearn - are both instance methods that need a live Player (for
    /// ConfirmationManager, Session, SaveBiotaToDatabase, the CAP property funnel) and cannot be driven
    /// behaviorally for the same reason IsAcceptableToSell's sibling FinalizeBuyTransaction paths cannot
    /// be (MuleCommerceTests). Per that same precedent (e.g.
    /// CallSite_RejectedBuy_ReApproachesAPersonalVendorOnlyWhenItsViewIsStale), the invariants that matter
    /// most for a CAP-charging bug - charge exactly once, refund on a failed learn, re-validate before
    /// charging, never double-create a token - are pinned as source-level regression tests instead of
    /// pinning trivial behavior a real harness would be needed to exercise honestly.
    ///
    /// What IS exercised behaviorally is the one piece of this change that is pure: Vendor.CalcSellCost,
    /// confirming the "price == CostPerRank when a trainer's SellPrice is 1" mechanism
    /// TryLearnClassAbilityFromVendor's own doc comment relies on (already pinned at the content level by
    /// ClassAbilityTokenTests.CommittedTokenContent_PricesMatchCostPerRank; this is the code-level half).
    /// </summary>
    [TestClass]
    public class ClassAbilityInstantLearnTests
    {
        private static string ReadSource(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    return AccountVaultPurgeTests.StripComments(File.ReadAllText(candidate));
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private const string PlayerTokensPath = "Source/ACE.Server/WorldObjects/Player_ClassAbilityTokens.cs";
        private const string VendorPath = "Source/ACE.Server/WorldObjects/Vendor.cs";
        private const string TrainerPath = "Source/ACE.Server/ClassAbilities/ClassAbilityTrainer.cs";

        /// <summary>Slices out one method's body by brace-matching from its signature to its closing brace.</summary>
        private static string ExtractMethodBody(string code, string signatureAnchor)
        {
            var start = code.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find method signature anchor: {signatureAnchor}");

            var openBrace = code.IndexOf('{', start);
            Assert.IsTrue(openBrace >= 0, $"could not find opening brace after: {signatureAnchor}");

            var depth = 0;
            for (var i = openBrace; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return code.Substring(openBrace, i - openBrace + 1);
                }
            }

            Assert.Fail($"unbalanced braces scanning method body for: {signatureAnchor}");
            return null;
        }

        // ---- Player.TryLearnClassAbilityFromVendor -------------------------------------------------

        [TestMethod]
        public void TryLearnClassAbilityFromVendor_ChargesExactlyOnce_ViaThePrepaidApplyPath()
        {
            var code = ReadSource(PlayerTokensPath);
            var body = ExtractMethodBody(code, "public bool TryLearnClassAbilityFromVendor(");

            var spendCount = System.Text.RegularExpressions.Regex.Matches(body, @"SpendCurrency\(").Count;
            Assert.AreEqual(1, spendCount, "the vendor currency must be charged exactly once per call - a second SpendCurrency would double-bill the player");

            // Must apply the rank through the PREPAID path (which charges no points of its own - the
            // points already moved via SpendCurrency above), never through LearnClassAbility (which would
            // charge CostPerRank a SECOND time on top of the vendor currency debit).
            Assert.IsTrue(body.Contains("ApplyClassAbilityRankPrepaid("),
                "must apply the rank via ApplyClassAbilityRankPrepaid, which charges nothing - the vendor currency debit already paid for it");

            Assert.IsFalse(body.Contains("LearnClassAbility("),
                "must NOT call LearnClassAbility - it would charge CostPerRank a second time on top of the SpendCurrency debit already applied");
        }

        [TestMethod]
        public void TryLearnClassAbilityFromVendor_RefundsThePriceIfTheRankCannotBeApplied()
        {
            var code = ReadSource(PlayerTokensPath);
            var body = ExtractMethodBody(code, "public bool TryLearnClassAbilityFromVendor(");

            var iSpend = body.IndexOf("SpendCurrency(", StringComparison.Ordinal);
            var iApply = body.IndexOf("ApplyClassAbilityRankPrepaid(", StringComparison.Ordinal);
            var iRefund = body.IndexOf("AdjustClassAbilityPoints((int)price, 0, CapLedgerReason.VendorDebit", StringComparison.Ordinal);

            Assert.IsTrue(iSpend >= 0 && iApply > iSpend, "the charge must happen before the rank is applied");
            Assert.IsTrue(iRefund > iApply, "a positive refund of `price` must appear after the ApplyClassAbilityRankPrepaid call, to undo a failed apply");

            // The refund must be reachable only from the failure branch, not unconditionally.
            var refundContext = body.Substring(Math.Max(0, iRefund - 200), Math.Min(200, iRefund));
            Assert.IsTrue(refundContext.Contains("!ApplyClassAbilityRankPrepaid("),
                "the refund must be guarded by a failed ApplyClassAbilityRankPrepaid check, not run unconditionally");
        }

        [TestMethod]
        public void TryLearnClassAbilityFromVendor_ReChecksEligibility_BeforeChargingAnything()
        {
            var code = ReadSource(PlayerTokensPath);
            var body = ExtractMethodBody(code, "public bool TryLearnClassAbilityFromVendor(");

            var iEligible = body.IndexOf("CanBuyClassAbilityToken(", StringComparison.Ordinal);
            var iSpend = body.IndexOf("SpendCurrency(", StringComparison.Ordinal);

            Assert.IsTrue(iEligible >= 0, "must re-run CanBuyClassAbilityToken - the confirmation can sit outstanding for up to 30 seconds, during which eligibility can change");
            Assert.IsTrue(iEligible < iSpend, "eligibility must be re-checked BEFORE any currency is charged");
        }

        [TestMethod]
        public void TryLearnClassAbilityFromVendor_RejectsANonCapAlternateCurrency()
        {
            var code = ReadSource(PlayerTokensPath);
            var body = ExtractMethodBody(code, "public bool TryLearnClassAbilityFromVendor(");

            Assert.IsTrue(body.Contains("ClassAbilityPointCurrencyWcid"),
                "must refuse (or otherwise special-case) a vendor whose AlternateCurrency is not the CAP wcid - the refund path below assumes the charge came out of AvailableClassAbilityPoints, which is only true for that one currency");
        }

        [TestMethod]
        public void TryLearnClassAbilityFromVendor_ChecksAffordability_AgainstTheActualPriceParameter()
        {
            var code = ReadSource(PlayerTokensPath);
            var body = ExtractMethodBody(code, "public bool TryLearnClassAbilityFromVendor(");

            // Must compare against the `price` parameter (the vendor-displayed figure), not silently
            // recompute from def.CostPerRank, per the "charge exactly what was displayed" requirement.
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(body, @"<\s*price\b") ||
                          System.Text.RegularExpressions.Regex.IsMatch(body, @"price\s*<"),
                "must compare the player's spendable alternate currency against `price`");
        }

        /// <summary>
        /// A mule must be refused BEFORE any currency is charged, in the SHARED eligibility gate
        /// (CanBuyClassAbilityToken) rather than only downstream in
        /// ApplyClassAbilityRankPrepaid's ValidateClassAbilityRankPrerequisites - covering both
        /// TryLearnClassAbilityFromVendor (which calls CanBuyClassAbilityToken before SpendCurrency) and
        /// TryPurchaseClassAbilityVoucher (which shares the same gate) in one place, rather than needing
        /// a mule check duplicated at every acquisition front end.
        /// </summary>
        [TestMethod]
        public void CanBuyClassAbilityToken_RefusesAMule_BeforeAnyOtherCheck()
        {
            var code = ReadSource(PlayerTokensPath);
            var body = ExtractMethodBody(code, "public bool CanBuyClassAbilityToken(");

            var iMuleCheck = body.IndexOf("MuleBlocked(MuleAction.TrainClassAbility", StringComparison.Ordinal);
            Assert.IsTrue(iMuleCheck >= 0, "CanBuyClassAbilityToken must refuse a mule via MuleBlocked(MuleAction.TrainClassAbility)");

            var iMuleReturn = body.IndexOf("return false;", iMuleCheck, StringComparison.Ordinal);
            var iReturnTrue = body.LastIndexOf("return true;", StringComparison.Ordinal);

            Assert.IsTrue(iMuleReturn > iMuleCheck && iMuleReturn < iReturnTrue,
                "the mule refusal must return false before the method's final `return true`");

            // Must be the FIRST decision in the method - before class_abilities_enabled or any of the
            // other eligibility branches - so a mule never falls through to a currency charge on any path.
            var iEnabledCheck = body.IndexOf("class_abilities_enabled", StringComparison.Ordinal);
            Assert.IsTrue(iEnabledCheck < 0 || iMuleCheck < iEnabledCheck,
                "the mule check should run before (or at worst alongside) every other eligibility check, never after");

            Assert.IsTrue(body.Contains("\"A mule cannot learn class abilities.\""),
                "must surface the same player-facing text every other mule-training refusal uses");
        }

        // ---- Vendor.OfferClassAbilityInstantLearn / BuyItems_ValidateTransaction -------------------

        [TestMethod]
        public void BuyItemsValidateTransaction_RejectsAClassAbilityTokenMixedWithAnythingElse()
        {
            var code = ReadSource(VendorPath);

            var iCount = code.IndexOf("tokenItem != null && purchaseItems.Count != 1", StringComparison.Ordinal);
            Assert.IsTrue(iCount >= 0, "a class-ability token purchase must be refused unless it is the ONLY item in the transaction");

            var iMessage = code.IndexOf("Class ability training is purchased one rank at a time.", iCount, StringComparison.Ordinal);
            Assert.IsTrue(iMessage > iCount && iMessage < iCount + 300, "the mixed-basket rejection must send a player-facing reason");
        }

        /// <summary>
        /// The per-item token-buyability gate (ClassAbilityTokensBuyable) must word its own duplicate-
        /// skill and multi-copy refusals the SAME way as the basket-level check above - both describe the
        /// same rule ("training is one rank at a time") to the player, and a leftover reference to a
        /// "training token" being bought would describe the RETIRED token-in-inventory flow.
        /// </summary>
        [TestMethod]
        public void ClassAbilityTokensBuyable_UsesTheSameOneRankAtATimeWording()
        {
            var code = ReadSource(VendorPath);

            Assert.IsFalse(code.Contains("You can only buy one class ability training token at a time."),
                "the old token-shaped wording must be fully replaced");

            var occurrences = System.Text.RegularExpressions.Regex.Matches(
                code, System.Text.RegularExpressions.Regex.Escape("Class ability training is purchased one rank at a time.")).Count;

            Assert.AreEqual(3, occurrences,
                "expected exactly 3 occurrences: the basket-level mixed-purchase check, and ClassAbilityTokensBuyable's duplicate-skill and multi-copy refusals");
        }

        [TestMethod]
        public void OfferClassAbilityInstantLearn_NeverCallsFinalizeBuyTransaction_AndAlwaysCleansUpTheToken()
        {
            var code = ReadSource(VendorPath);
            var body = ExtractMethodBody(code, "private bool OfferClassAbilityInstantLearn(");

            Assert.IsFalse(body.Contains("FinalizeBuyTransaction"),
                "the instant-learn path must never call FinalizeBuyTransaction - no token may ever be created in inventory");

            Assert.IsTrue(body.Contains("CleanupCreatedItems(defaultItems)"),
                "the token WorldObject built to price the purchase must be destroyed, not left to leak a guid");
        }

        [TestMethod]
        public void OfferClassAbilityInstantLearn_ResyncsThePanel_AsSoonAsThePromptIsEnqueued()
        {
            var code = ReadSource(VendorPath);
            var body = ExtractMethodBody(code, "private bool OfferClassAbilityInstantLearn(");

            var iEnqueueCheck = body.IndexOf("if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))", StringComparison.Ordinal);
            Assert.IsTrue(iEnqueueCheck >= 0, "must enqueue the Yes/No confirmation");

            // Everything from the end of the EnqueueSend-failed `if` block onward is the SUCCESS path
            // (the confirmation was queued) - the lambda passed to Confirmation_ClassAbilityChoice
            // appears textually EARLIER in the method (it is only invoked later, on the player's eventual
            // response) and also calls ApproachVendor(Undef) on its own failure branches, so the search
            // must be scoped past the enqueue check to avoid matching that unrelated, earlier call.
            var afterEnqueueCheck = body.Substring(iEnqueueCheck);
            var iReturnTrue = afterEnqueueCheck.IndexOf("return true;", StringComparison.Ordinal);
            var iResync = afterEnqueueCheck.IndexOf("ApproachVendor(player, VendorType.Undef)", StringComparison.Ordinal);

            Assert.IsTrue(iReturnTrue >= 0, "the success path must return true after enqueueing");
            Assert.IsTrue(iResync >= 0 && iResync < iReturnTrue,
                "must resync the vendor panel to VendorType.Undef right after successfully enqueueing the prompt, before returning true - the client already decremented its shown balance when it sent the buy request, and nothing has been charged yet");
        }

        [TestMethod]
        public void OfferClassAbilityInstantLearn_OnNo_ResyncsWithoutLearning()
        {
            var code = ReadSource(VendorPath);
            var body = ExtractMethodBody(code, "private bool OfferClassAbilityInstantLearn(");

            var iNotAccepted = body.IndexOf("if (!accepted)", StringComparison.Ordinal);
            Assert.IsTrue(iNotAccepted >= 0, "the confirmation callback must branch on the No case");

            var noBranchEnd = body.IndexOf("if (freshVendor == null)", iNotAccepted, StringComparison.Ordinal);
            Assert.IsTrue(noBranchEnd > iNotAccepted, "could not isolate the No branch");

            var noBranch = body.Substring(iNotAccepted, noBranchEnd - iNotAccepted);

            Assert.IsFalse(noBranch.Contains("TryLearnClassAbilityFromVendor"), "No must never learn anything");
            Assert.IsTrue(noBranch.Contains("ApproachVendor"), "No must still resync the panel");
        }

        // ---- ClassAbilityTrainer's NPC-use front end -------------------------------------------------

        [TestMethod]
        public void ClassAbilityTrainer_YesAction_LearnsDirectly_NotThroughAVoucher()
        {
            var code = ReadSource(TrainerPath);
            var body = ExtractMethodBody(code, "private static void HandleTrainer(");

            Assert.IsTrue(body.Contains("player.LearnClassAbility("),
                "the trainer NPC's Yes action must call LearnClassAbility directly (instant learn)");

            Assert.IsFalse(body.Contains("TryPurchaseClassAbilityVoucher"),
                "the trainer NPC's live use-interaction must no longer buy a bound training token");
        }

        [TestMethod]
        public void TryPurchaseClassAbilityVoucher_StillExists_ForTheInterimCavoucherCommand()
        {
            // Kept intact per the task's instructions: /cavoucher (and any already-held voucher) must
            // keep working exactly as before. Reflection over the compiled method proves the signature
            // survived the refactor, independent of any source-text reformatting.
            var method = typeof(Player).GetMethod("TryPurchaseClassAbilityVoucher");
            Assert.IsNotNull(method, "TryPurchaseClassAbilityVoucher must still exist on Player for /cavoucher");
        }

        [TestMethod]
        public void ApplyClassAbilityRankPrepaid_StillExists_ForAlreadyHeldVouchers()
        {
            var method = typeof(Player).GetMethod("ApplyClassAbilityRankPrepaid");
            Assert.IsNotNull(method, "ApplyClassAbilityRankPrepaid must still exist so a voucher already in a pack (bought before this change, or via /cavoucher) still works from Gem.UseClassAbilityToken");
        }

        // ---- The pure pricing mechanism the whole design leans on ------------------------------------

        /// <summary>
        /// The code-level half of ClassAbilityTokenTests.CommittedTokenContent_PricesMatchCostPerRank
        /// (which pins the CONTENT: every token's Value/StackUnitValue equal its CostPerRank entry). This
        /// pins the FORMULA: with a trainer's SellPrice of 1 (every shipped Drift Network trainer -
        /// verified against Content/sql/weenies/*_trainer.sql and Content/realms/driftnetwork_hub.sql,
        /// all eight carry PropertyFloat 38 = 1), Vendor.CalcSellCost(1.0, unitValue, ..., 1) == unitValue
        /// for exactly the range of CAP costs this game's class abilities ever charge (1..100). Together
        /// the two tests prove price == CostPerRank[tier-1] for every committed token, which is what lets
        /// TryLearnClassAbilityFromVendor's doc comment say the two "agree by construction" rather than
        /// merely by coincidence.
        /// </summary>
        [TestMethod]
        public void CalcSellCost_AtSellPriceOne_ReturnsTheUnitValueUnchanged()
        {
            for (var costPerRank = 1; costPerRank <= 100; costPerRank++)
            {
                var price = Vendor.CalcSellCost(1.0, costPerRank, ItemType.TinkeringMaterial, stackSize: 1);
                Assert.AreEqual((uint)costPerRank, price, $"CalcSellCost(1.0, {costPerRank}, ..., 1) must equal {costPerRank} unchanged");
            }
        }
    }
}
