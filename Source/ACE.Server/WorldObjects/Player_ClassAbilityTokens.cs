using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Facets;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // Drift Network trainer economy - buying a class ability rank for class ability points (CAP).
        //
        // AS OF THE INSTANT-LEARN FLOW, a Drift Network trainer VENDOR never creates a token at all:
        // clicking Buy on a listed rank shows the same Yes/No confirmation immediately, Yes charges CAP
        // and learns the rank in one step (see Vendor.OfferClassAbilityInstantLearn and
        // TryLearnClassAbilityFromVendor below), and No or a timeout leaves everything untouched. The
        // NPC trainer's own use-interaction (ClassAbilityTrainer.HandleTrainer) works the same way,
        // straight through LearnClassAbility.
        //
        // BOUND, PREPAID VOUCHERS STILL EXIST as a separate, secondary path: TryPurchaseClassAbilityVoucher
        // (below), driven only by the interim /cavoucher admin/testing command now that both live front
        // ends instant-learn. A voucher is charged CAP AT ACQUISITION and applies its rank for FREE when
        // used (Gem.UseClassAbilityToken); the exchange NPC still refunds an unused one
        // (RefundUnusedVoucher). Existing vouchers already in a player's pack from before this change keep
        // working exactly as before - nothing here changes their shape.
        //
        // There used to be a second, "legacy" voucher model in which a token charged CAP on USE, discriminated
        // by PropertyBool.ClassAbilityTokenPrepaid on the spawned instance. That marker was only ever stamped by
        // THIS file, so a token bought from a trainer VENDOR - which charged CAP through the vendor currency
        // system (Vendor.AlternateCurrency -> Player.DebitBankedAlternateCurrency) - fell through to the legacy
        // branch and was charged a SECOND time on use. Buying Nether Rush rank 2 cost 4 CAP against a listed 2.
        // The discriminator is gone rather than fixed: an invariant that every acquisition path must remember to
        // stamp is one a new acquisition path will silently break. See the retired PropertyBool 9012.

        /// <summary>
        /// Shared buy-eligibility decision for a class ability token, used by BOTH the Drift Network trainer
        /// VENDORS (<see cref="Vendor.BuyItems_ValidateTransaction"/>) and the interim /cavoucher purchase path.
        /// It encodes the non-currency rules: system enabled, rank order + max rank
        /// (<see cref="ClassAbilityTokenCatalog.Evaluate"/>), the class-tier unlock (T2/T3, the real gate #86,
        /// <see cref="MeetsClassAbilityTierUnlock"/>), and the one-voucher-per-skill hold. The CAP cost itself is
        /// charged by the caller - the vendor currency system, or <see cref="TryPurchaseClassAbilityVoucher"/> -
        /// but affordability is reported here too so a locked/unaffordable token fails with a clear reason before
        /// any charge. Returns FALSE with a player-facing <paramref name="error"/> and no state change.
        /// </summary>
        public bool CanBuyClassAbilityToken(ClassAbilityDefinition def, int tier, out string error)
        {
            error = null;

            // Mule (WaffleACE), checked here rather than left to ValidateClassAbilityRankPrerequisites
            // downstream: this is the shared eligibility gate BOTH the trainer vendor's instant-learn
            // charge (TryLearnClassAbilityFromVendor) and TryPurchaseClassAbilityVoucher run BEFORE
            // spending any currency, so refusing a mule here means it never reaches SpendCurrency at
            // all - the vendor path used to only discover the mule refusal after the CAP charge had
            // already gone through, when ApplyClassAbilityRankPrepaid's own ValidateClassAbilityRankPrerequisites
            // check hit it, which meant a mule's charge had to be refunded rather than never taken.
            // Silent (notify: false) - the caller surfaces 'error' itself, matching every other branch here.
            if (MuleBlocked(MuleAction.TrainClassAbility, notify: false))
            {
                error = "A mule cannot learn class abilities.";
                return false;
            }

            // PvP template (progression lock): refused before any currency moves, for the same reason as the mule guard.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.ClassAbilityPoints);
            if (templateRefusal != null)
            {
                error = templateRefusal;
                return false;
            }

            // PK facet: no class ability point spending of any kind. Here for the same reason as the mule
            // check above - both the trainer vendor's instant-learn charge (TryLearnClassAbilityFromVendor)
            // and TryPurchaseClassAbilityVoucher run this BEFORE any currency moves, so the refusal lands
            // before a debit rather than after it (and the vendor's own buy validation,
            // Vendor.BuyItems_ValidateTransaction, runs it before the confirmation is even offered).
            if (!FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive))
            {
                error = FacetPk.PkFacetClassAbilityRefusal;
                return false;
            }

            // PvP arena (H10): refused in a match, before any currency moves, for the same reason as above
            if (IsInPvpMatch)
            {
                error = PvpArenaText.AbilitySpendRefused;
                return false;
            }

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                error = "Class abilities are not currently enabled on this server.";
                return false;
            }

            var rank = GetClassAbilityRank(def.Id);

            var eval = ClassAbilityTokenCatalog.Evaluate(def, rank, tier, AvailableClassAbilityPoints);
            switch (eval.Outcome)
            {
                case ClassAbilityTokenOutcome.NotImplemented:
                    error = $"{def.DisplayName} cannot be learned yet.";
                    return false;
                case ClassAbilityTokenOutcome.AlreadyMaxRank:
                    error = $"You already have {def.DisplayName} at its maximum rank ({rank}/{def.MaxRank}).";
                    return false;
                case ClassAbilityTokenOutcome.WrongTierOrder:
                    error = tier <= rank
                        ? $"You already have {def.DisplayName} rank {tier}."
                        : $"You must learn {def.DisplayName} rank {rank + 1} before rank {tier}.";
                    return false;
                case ClassAbilityTokenOutcome.InsufficientPoints:
                    error = $"{def.DisplayName} rank {tier} costs {eval.Cost:N0} class ability point{(eval.Cost == 1 ? "" : "s")}; you have {AvailableClassAbilityPoints:N0}.";
                    return false;
            }

            // Class-tier minimum (T2/T3 unlock) - only the FIRST rank is gated; later ranks are already unlocked.
            if (rank == 0 && !MeetsClassAbilityTierUnlock(def, out var tierReason))
            {
                error = tierReason;
                return false;
            }

            // One voucher per skill at a time - no buying ahead of the rank you can currently learn.
            if (TryFindClassAbilityVoucher(def.Id, out _))
            {
                error = $"You already hold a {def.DisplayName} training token - use it before buying another.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Finds a class ability voucher for the given skill already in the player's possession, if any.
        /// Used to enforce "one voucher per skill at a time" so vouchers can't be stockpiled ahead of the rank
        /// the player can currently learn. Every token is prepaid, so carrying the token id is the whole test.
        /// </summary>
        private bool TryFindClassAbilityVoucher(ClassAbilityId skill, out WorldObject voucher)
        {
            voucher = GetAllPossessions().FirstOrDefault(wo =>
                (wo.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0) == (int)skill);

            return voucher != null;
        }

        /// <summary>
        /// Buys a class ability token voucher, paying its rank cost in class ability points up front and placing a
        /// bound, prepaid voucher in the pack. All-or-nothing: everything is validated (system enabled,
        /// rank order + affordability, class-tier minimum, no duplicate voucher, inventory room, content
        /// present) before any points are spent. Returns FALSE with a player-facing error and no state change
        /// on any failure. USING the resulting voucher is free (Gem.UseClassAbilityToken).
        /// </summary>
        public bool TryPurchaseClassAbilityVoucher(ClassAbilityTokenCatalog.Offering offering, out string error)
        {
            error = null;

            var def = offering.Definition;

            // All non-currency eligibility (enabled, rank order, tier unlock, one-voucher-per-skill, affordability)
            // is the same decision the trainer vendors run; share it so the two front ends can never diverge.
            if (!CanBuyClassAbilityToken(def, offering.Tier, out error))
                return false;

            var eval = ClassAbilityTokenCatalog.Evaluate(def, GetClassAbilityRank(offering.SkillId), offering.Tier, AvailableClassAbilityPoints);

            var voucher = WorldObjectFactory.CreateNewWorldObject(offering.Wcid);
            if (voucher == null)
            {
                error = $"The training token for {def.DisplayName} rank {offering.Tier} (wcid {offering.Wcid}) is not in the world database - its content has not been imported yet.";
                return false;
            }

            // Bound: can't be traded or dropped, only used or refunded. Every token weenie already carries
            // Attuned + Bonded (pinned by CommittedTokenContent_AreAttunedAndBonded), so this is belt-and-braces
            // for an instance that somehow reached here without them.
            voucher.Attuned = AttunedStatus.Attuned;
            voucher.Bonded = BondedStatus.Bonded;

            if (!TryCreateInInventoryWithNetworking(voucher))
            {
                voucher.Destroy();
                error = "You don't have room in your pack for that training token.";
                return false;
            }

            // Charge points last. Affordability was checked above and the landblock is single-threaded, so this
            // does not go negative in practice; the voucher is already bound in the pack for the player to use.
            AdjustClassAbilityPoints(-eval.Cost, 0, CapLedgerReason.VoucherBuy,
                ability: def.Name, rankAfter: GetClassAbilityRank(offering.SkillId),
                detail: $"bought a rank {offering.Tier} voucher (wcid {offering.Wcid}) for {eval.Cost}");

            SaveBiotaToDatabase();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You spend {eval.Cost:N0} class ability point{(eval.Cost == 1 ? "" : "s")} for {voucher.Name}. Use it from your pack to learn {def.DisplayName} rank {offering.Tier} - it is already paid for. ({AvailableClassAbilityPoints:N0} point{(AvailableClassAbilityPoints == 1 ? "" : "s")} left.)",
                ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// Applies the next rank of a class ability from a prepaid voucher, WITHOUT charging class ability points
        /// (already paid at purchase). Shares the rank write, the quest-registry update, the cache invalidation,
        /// the stat update and the two saves with the paid path via
        /// <see cref="Player.ApplyClassAbilityRankCore"/>.
        ///
        /// THIS USED TO CREDIT THE RANK COST, CALL LearnClassAbility (which debited it), AND UNDO THE CREDIT ON
        /// FAILURE - net zero via two opposite writes. That shape cannot be ledgered honestly: it would emit a
        /// credit row, a `learn` row whose delta_Available claimed a spend that was really the caller's own
        /// credit coming back, and on failure a third row undoing the first. Three rows and one lie for one
        /// operation. Now it emits exactly one `voucher_apply` row with both deltas zero, which is the truth: a
        /// prepaid rank moves no points, and the spend was already recorded as `voucher_buy` at purchase.
        ///
        /// EQUIVALENCE ARGUMENT, because getting this wrong in one direction learns anything for free and in the
        /// other makes a paid voucher unusable. The prepaid path skips EXACTLY ONE check that the paid path
        /// runs - the affordability check (AvailableClassAbilityPoints &gt;= CostPerRank[rank]) - and it skips it
        /// because the points were charged at purchase by <see cref="TryPurchaseClassAbilityVoucher"/> or by the
        /// trainer vendor's alternate-currency debit. Every other prerequisite still runs, in the same order,
        /// with the same error strings, and that is guaranteed by construction rather than by inspection: both
        /// paths call the single private <see cref="Player.ValidateClassAbilityRankPrerequisites"/>, which holds
        /// the whole set - the Mule block, the Implemented gate, the Tier 2/3 unlock (first rank only), and the
        /// max-rank ceiling. There is no second copy of any of them to drift.
        ///
        /// Returns FALSE with the player-facing error and no state change on any validation failure. There is
        /// nothing to undo on failure any more, because nothing was written before the validations ran.
        /// </summary>
        public bool ApplyClassAbilityRankPrepaid(ClassAbilityDefinition skill, out string error)
        {
            if (!ValidateClassAbilityRankPrerequisites(skill, out var rank, out error))
                return false;

            // Zero deltas, and the row is still worth writing: it is the only record that a prepaid rank was
            // applied, and it is what pairs with the earlier voucher_buy row. ownedCostDelta carries the rank
            // cost because the rank write happens in the core call below, after this row is built.
            var cost = skill.CostPerRank[rank];

            AdjustClassAbilityPoints(0, 0, CapLedgerReason.VoucherApply,
                ability: skill.Name, rankAfter: rank + 1, ownedCostDelta: cost,
                detail: $"prepaid rank {rank + 1} applied (charged {cost} at purchase)");

            return ApplyClassAbilityRankCore(skill, CapLedgerReason.VoucherApply, out error);
        }

        /// <summary>
        /// The Yes-branch of a Drift Network trainer VENDOR's instant-learn confirmation
        /// (<see cref="Vendor.OfferClassAbilityInstantLearn"/>): re-validates everything from scratch
        /// against CURRENT state, charges <paramref name="price"/> in <paramref name="vendor"/>'s
        /// alternate currency, and applies the rank. NO TOKEN IS EVER CREATED for this flow - the
        /// WorldObject the vendor system built to price the purchase was already destroyed before this
        /// method is called.
        ///
        /// RE-VALIDATION IS NECESSARY, NOT DEFENSIVE PARANOIA: the confirmation can sit outstanding for
        /// up to 30 seconds (ConfirmationManager's timeout), during which the player can move, spend
        /// their points elsewhere (another trainer, /abilities, the Luminance/XP exchange stones), or
        /// this vendor can go out of range or reset. <see cref="CanBuyClassAbilityToken"/> is the same
        /// eligibility decision the original Buy click already ran, re-run here against live state.
        ///
        /// <paramref name="price"/> IS THE VENDOR-COMPUTED PRICE from Vendor.BuyItems_ValidateTransaction
        /// (Vendor.GetSellCost applied to the token WorldObject) - the exact figure the client's buy
        /// panel and this confirmation prompt displayed - and is what gets charged, NOT a value
        /// recomputed from <paramref name="def"/>.CostPerRank here. For every token shipped today the two
        /// are equal by construction: every Drift Network trainer weenie is authored with SellPrice 1
        /// (Vendor.CalcSellCost then reduces to price == token.Value), and every token weenie's Value is
        /// pinned to its ClassAbilityDefinition.CostPerRank entry
        /// (ClassAbilityTokenTests.CommittedTokenContent_PricesMatchCostPerRank enforces this in CI) -
        /// but this method charges what was actually displayed rather than assuming that content
        /// invariant holds for every future trainer.
        ///
        /// ALL-OR-NOTHING: eligibility and affordability against <paramref name="price"/> are both
        /// re-checked BEFORE anything is spent. If the currency debit succeeds but
        /// <see cref="ApplyClassAbilityRankPrepaid"/> then fails, the charge is refunded and no rank is
        /// applied. THIS BRANCH IS NOT ASSUMED UNREACHABLE, and an earlier draft of this comment wrongly
        /// called it that: <see cref="CanBuyClassAbilityToken"/> above and ApplyClassAbilityRankPrepaid's
        /// own ValidateClassAbilityRankPrerequisites are two SEPARATE checks (the first is the shared
        /// eligibility gate for both this method and TryPurchaseClassAbilityVoucher; the second is the
        /// shared rank-write precondition for both this method and LearnClassAbility) that happen to
        /// overlap today but are not the same call and are not proven to stay in lockstep as either one
        /// is edited in the future. The refund exists to make a future divergence between them fail safe
        /// (money back, no rank) instead of silently charging for nothing.
        ///
        /// Returns FALSE with a player-facing <paramref name="error"/> and no state change on any
        /// failure; the caller (<see cref="Vendor.OfferClassAbilityInstantLearn"/>'s confirmation
        /// callback) is responsible for resyncing the vendor panel on that outcome. On success this
        /// method sends its own chat/sound feedback and resyncs the panel itself, mirroring
        /// Player.FinalizeBuyTransaction's own successful-buy ending.
        /// </summary>
        public bool TryLearnClassAbilityFromVendor(Vendor vendor, ClassAbilityDefinition def, int tier, uint price, out string error)
        {
            error = null;

            if (!CanBuyClassAbilityToken(def, tier, out error))
                return false;

            var currencyWcid = vendor.AlternateCurrency;

            // Simpler-safe choice, not a generalization: the refund path below (on a failed
            // ApplyClassAbilityRankPrepaid) undoes the charge by crediting AvailableClassAbilityPoints
            // directly, which is only correct because SpendCurrency's CAP branch
            // (Player_Bank.DebitBankedAlternateCurrency's ClassAbilityPointCurrencyWcid case) debits that
            // exact property. A trainer priced in any OTHER alternate currency would spend from a
            // different balance entirely (promissory notes, MMDs/pyreals) while this method still tried
            // to refund CAP that was never taken - crediting points for free. Refusing here rather than
            // handling every currency generally keeps that invariant simple to see is correct; every
            // shipped trainer prices tokens in CAP (Content/sql/weenies/*_npc_*_trainer.sql,
            // AlternateCurrency 1001010), so this is not reachable in practice today.
            if (currencyWcid == null || currencyWcid.Value != ClassAbilityPointCurrencyWcid)
            {
                error = $"{vendor.Name} does not sell class ability training for a currency this can charge.";
                return false;
            }

            // Same affordability test Vendor.BuyItems_ValidateTransaction ran against the original
            // basket (inventory count of the alt-currency wcid, plus its banked balance), re-run against
            // CURRENT state and against the ACTUAL price rather than CanBuyClassAbilityToken's own
            // CostPerRank-based affordability check, which is what makes this correct even if a future
            // trainer's price ever diverges from CostPerRank.
            var playerAltCurrency = GetNumInventoryItemsOfWCID(currencyWcid.Value);
            if (playerAltCurrency + GetBankedAlternateCurrency(currencyWcid.Value) < price)
            {
                error = $"{def.DisplayName} rank {tier} costs {price:N0} class ability point{(price == 1 ? "" : "s")}; you have {AvailableClassAbilityPoints:N0}.";
                return false;
            }

            if (SpendCurrency(currencyWcid.Value, price, true) == null)
            {
                error = "The charge could not be completed. Nothing was learned.";
                return false;
            }

            if (!ApplyClassAbilityRankPrepaid(def, out error))
            {
                // The currency has already moved (SpendCurrency above already debits Available directly
                // for the CAP wcid, via Player_Bank.DebitBankedAlternateCurrency's CapLedgerReason.VendorDebit
                // write) and the rank was not applied - refund it. VendorDebit is a sink reason in BOTH
                // directions (CapLedger.IsSinkReason), so this positive row nets the earlier negative one
                // to zero recorded spend, exactly like RefundUnusedVoucher undoing a VoucherBuy.
                AdjustClassAbilityPoints((int)price, 0, CapLedgerReason.VendorDebit,
                    ability: def.Name,
                    detail: $"refunded a failed instant-learn charge of {price} after the rank could not be applied ({error})");

                SaveBiotaToDatabase();

                return false;
            }

            // No RecordVendorBuy: its own doc comment requires the item to have actually reached the
            // player's inventory ("Call this at the point the item actually reaches the player's
            // inventory, never on validation") - true for every other vendor purchase, but never true
            // here. RecordVendorPayment (the currency leg) still fires, and the CAP ledger's VendorDebit
            // row above already carries the ability name, tier and price for this exact spend.
            AnalyticsManager.RecordVendorPayment(this, vendor, true, currencyWcid.Value, price);

            vendor.MoneyIncome += (int)price;
            vendor.NumItemsSold++;

            Session.Network.EnqueueSend(new GameMessageSound(Guid, Sound.PickUpItem));
            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You learn {def.DisplayName} rank {tier}. ({AvailableClassAbilityPoints:N0} class ability point{(AvailableClassAbilityPoints == 1 ? "" : "s")} left.)",
                ChatMessageType.Broadcast));

            // altCurrencySpent = price, matching FinalizeBuyTransaction's own successful-buy call: the
            // client speculatively decremented its shown balance by price when it sent the buy request,
            // and GameEventApproachVendor's alt-currency figure is (live balance) + altCurrencySpent, so
            // this is what makes the displayed total agree with what the client already shows.
            vendor.ApproachVendor(this, VendorType.Buy, price);

            return true;
        }

        /// <summary>
        /// Refunds an UNUSED, prepaid class ability voucher: returns its rank cost in class ability points and
        /// consumes the token. This is the exchange NPC's job - for players who bought the wrong token. A token
        /// that has already been used no longer exists to refund, and a learned skill is respec'd via the
        /// separate /abilities unlearn path, so this only ever touches vouchers still sitting in the pack.
        /// </summary>
        public bool RefundUnusedVoucher(WorldObject voucher, out string error)
        {
            error = null;

            if (voucher == null || FindObject(voucher.Guid.Full, SearchLocations.MyInventory) == null)
            {
                error = "That training token is not in your pack.";
                return false;
            }

            // Every class ability token is prepaid, so carrying a registered token id is the whole test - there is
            // no unrefundable "legacy" token to screen out.
            var tokenSkillId = voucher.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0;

            if (tokenSkillId <= 0)
            {
                error = $"The {voucher.Name} is not a refundable training token.";
                return false;
            }

            if (!ClassAbilityRegistry.Abilities.TryGetValue((ClassAbilityId)tokenSkillId, out var def))
            {
                // Not a live ability - but it may be a RETIRED one whose token is still in a pack. Those are
                // exactly the tokens that can never be used again (Gem.UseClassAbilityToken sends the player
                // here), so refusing them would strand prepaid points forever. Anything the retired table
                // doesn't recognise is still refused: an unpriceable token must not be guessed at.
                return TryRefundRetiredVoucher(voucher, (ClassAbilityId)tokenSkillId, out error);
            }

            var tier = voucher.GetProperty(PropertyInt.ClassAbilityTokenTier) ?? 0;
            if (tier < 1 || tier > def.CostPerRank.Length)
            {
                error = $"The {voucher.Name} is misconfigured and cannot be refunded.";
                return false;
            }

            var refund = def.CostPerRank[tier - 1];

            if (!TryConsumeFromInventoryWithNetworking(voucher, 1))
            {
                error = $"Could not take the {voucher.Name} to refund it.";
                return false;
            }

            AdjustClassAbilityPoints(refund, 0, CapLedgerReason.VoucherRefund,
                ability: def.Name, rankAfter: GetClassAbilityRank(def.Id),
                detail: $"refunded an unused rank {tier} voucher for {refund}");

            SaveBiotaToDatabase();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You return {voucher.Name} and recover {refund:N0} class ability point{(refund == 1 ? "" : "s")}. Available: {AvailableClassAbilityPoints:N0}.",
                ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// The retired-ability half of <see cref="RefundUnusedVoucher"/>: pays back the HISTORICAL cost of a
        /// token whose ability has since been retired, using <see cref="RetiredClassAbilities"/> as the price
        /// list (the live registry no longer holds the definition, by design).
        ///
        /// This is the token-side mirror of <see cref="SweepRetiredClassAbilities"/>, which refunds retired
        /// ranks a character already LEARNED. Between them, no class ability point spent on a retired ability
        /// is stranded: learned ranks are swept at login, unused tokens are refunded here at the exchanger.
        /// </summary>
        private bool TryRefundRetiredVoucher(WorldObject voucher, ClassAbilityId retiredId, out string error)
        {
            error = null;

            var tier = voucher.GetProperty(PropertyInt.ClassAbilityTokenTier) ?? 0;

            if (!RetiredClassAbilities.TryGetRefund(retiredId, tier, out var refund, out var displayName))
            {
                error = $"The {voucher.Name} is not a refundable training token.";
                return false;
            }

            if (!TryConsumeFromInventoryWithNetworking(voucher, 1))
            {
                error = $"Could not take the {voucher.Name} to refund it.";
                return false;
            }

            // `ability` carries the RETIRED display name: the live registry no longer holds a definition for
            // this id, so that name is the only one that still exists for it.
            AdjustClassAbilityPoints(refund, 0, CapLedgerReason.VoucherRefundRetired,
                ability: displayName,
                detail: $"refunded a rank {tier} voucher for retired ability {(int)retiredId} for {refund}");

            SaveBiotaToDatabase();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"{displayName} has been retired. You return {voucher.Name} and recover {refund:N0} class ability point{(refund == 1 ? "" : "s")}. Available: {AvailableClassAbilityPoints:N0}.",
                ChatMessageType.Broadcast));

            return true;
        }
    }
}
