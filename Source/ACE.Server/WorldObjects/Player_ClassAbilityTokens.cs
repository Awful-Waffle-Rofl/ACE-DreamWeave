using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // Drift Network trainer economy - the "buy a class ability token for class ability points" front end.
        //
        // ONE payment model, no exceptions: the class ability point (CAP) cost is charged AT ACQUISITION, and
        // every token is a bound, prepaid voucher that applies its rank for FREE when used (see
        // Gem.UseClassAbilityToken). The exchange NPC refunds an unused voucher.
        //
        // There used to be a second, "legacy" model in which a token charged CAP on USE, discriminated by
        // PropertyBool.ClassAbilityTokenPrepaid on the spawned instance. That marker was only ever stamped by
        // THIS file, so a token bought from a trainer VENDOR - which charges CAP through the vendor currency
        // system (Vendor.AlternateCurrency -> Player.DebitBankedAlternateCurrency) - fell through to the legacy
        // branch and was charged a SECOND time on use. Buying Nether Rush rank 2 cost 4 CAP against a listed 2.
        // The discriminator is gone rather than fixed: an invariant that every acquisition path must remember to
        // stamp is one a new acquisition path will silently break. See the retired PropertyBool 9012.
        //
        // The trainer/exchanger NPCs are the intended front end; until they ship, /cavoucher exercises this.

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
            AvailableClassAbilityPoints -= eval.Cost;
            SaveBiotaToDatabase();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You spend {eval.Cost:N0} class ability point{(eval.Cost == 1 ? "" : "s")} for {voucher.Name}. Use it from your pack to learn {def.DisplayName} rank {offering.Tier} - it is already paid for. ({AvailableClassAbilityPoints:N0} point{(AvailableClassAbilityPoints == 1 ? "" : "s")} left.)",
                ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// Applies the next rank of a class ability from a prepaid voucher, WITHOUT charging class ability points
        /// (already paid at purchase). Reuses the shared <see cref="LearnClassAbility"/> path by crediting the
        /// rank cost back first so its internal spend nets to zero - no duplicated rank-write/save logic and no
        /// double charge. Returns FALSE (and undoes the temporary credit) on any failure LearnClassAbility reports.
        /// </summary>
        public bool ApplyClassAbilityRankPrepaid(ClassAbilityDefinition skill, out string error)
        {
            var rankBefore = GetClassAbilityRank(skill.Id);
            var cost = rankBefore >= 0 && rankBefore < skill.CostPerRank.Length ? skill.CostPerRank[rankBefore] : 0;

            AvailableClassAbilityPoints += cost;

            if (!LearnClassAbility(skill, out error))
            {
                AvailableClassAbilityPoints -= cost;   // undo the temporary credit; LearnClassAbility changed nothing
                return false;
            }

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

            if (tokenSkillId <= 0 || !ClassAbilityRegistry.Abilities.TryGetValue((ClassAbilityId)tokenSkillId, out var def))
            {
                error = $"The {voucher.Name} is not a refundable training token.";
                return false;
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

            AvailableClassAbilityPoints += refund;
            SaveBiotaToDatabase();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You return {voucher.Name} and recover {refund:N0} class ability point{(refund == 1 ? "" : "s")}. Available: {AvailableClassAbilityPoints:N0}.",
                ChatMessageType.Broadcast));

            return true;
        }
    }
}
