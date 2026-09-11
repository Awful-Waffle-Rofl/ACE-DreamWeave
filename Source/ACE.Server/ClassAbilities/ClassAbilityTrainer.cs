using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Drift Network trainer / exchanger NPC interaction - the intended in-game front end for the class ability
    /// point (CAP) voucher economy. An NPC weenie opts in via data, with no bespoke WeenieType:
    ///  - <see cref="PropertyString.ClassAbilityTrainerAbilities"/> = comma/semicolon list of ClassAbilityId names ->
    ///    a TRAINER that, on use, sells the next learnable rank of each listed skill for CAP.
    ///  - <see cref="PropertyBool.ClassAbilityExchanger"/> = true -> the EXCHANGER that, on use, refunds a
    ///    player's unused prepaid vouchers.
    ///  - <see cref="PropertyBool.ClassAbilityXpExchanger"/> = true -> the EXPERIENCE-for-CAP stone, the
    ///    Luminance stone's sibling (XP-LANE-SPEC sec 3.7). Same statue model, different colour.
    ///  - <see cref="PropertyBool.ClassAbilityLumExchanger"/> = true -> the Luminance-for-CAP PEDESTAL that, on
    ///    use, prompts to buy one class ability point at the piecewise curve price (an object, not an NPC).
    ///
    /// <see cref="Creature.ActOnUse"/> (NPCs) and <see cref="GenericObject.ActOnUse"/> (the pedestal) dispatch
    /// here; an NPC's greeting emote still fires first via
    /// <see cref="Managers.EmoteManager.OnUse"/>. All buy/refund work reuses the shipped engine
    /// (<see cref="Player.TryPurchaseClassAbilityVoucher"/> / <see cref="Player.RefundUnusedVoucher"/>) - the same
    /// methods the interim /cavoucher command calls.
    ///
    /// The per-trainer skill list is deliberately carried as weenie DATA, not derived from a skill->class map,
    /// so this content has no code dependency on the parallel class-grouping engine: a trainer sells exactly
    /// what its weenie lists.
    ///
    /// UI note: AC confirmations are Yes/No only (no list widget), so a trainer/exchanger with several options
    /// presents them as a short CHAIN of Yes/No prompts - Yes acts on that option and ends; No advances to the
    /// next. Bounded in practice because each class stocks only a couple of token skills.
    /// </summary>
    public static class ClassAbilityTrainer
    {
        /// <summary>
        /// If <paramref name="npc"/> is flagged as a class ability trainer or exchanger, runs its interaction with
        /// <paramref name="player"/> and returns true; otherwise returns false so normal NPC-use behavior (its
        /// emotes) is left untouched.
        /// </summary>
        public static bool TryHandleUse(WorldObject npc, Player player)
        {
            if (npc == null || player == null)
                return false;

            if (npc.GetProperty(PropertyBool.ClassAbilityExchanger) ?? false)
            {
                HandleExchanger(npc, player);
                return true;
            }

            if (npc.GetProperty(PropertyBool.ClassAbilityXpExchanger) ?? false)
            {
                HandleXpExchange(npc, player);
                return true;
            }

            if (npc.GetProperty(PropertyBool.ClassAbilityLumExchanger) ?? false)
            {
                HandleLumExchange(npc, player);
                return true;
            }

            if (npc.GetProperty(PropertyBool.ClassAbilityRespecer) ?? false)
            {
                HandleRespec(npc, player);
                return true;
            }

            var trainerList = npc.GetProperty(PropertyString.ClassAbilityTrainerAbilities);
            if (!string.IsNullOrWhiteSpace(trainerList))
            {
                HandleTrainer(npc, player, ParseTrainerAbilities(trainerList));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves a trainer's <see cref="PropertyString.ClassAbilityTrainerAbilities"/> value (comma/semicolon
        /// separated ClassAbilityId names) into definitions, in listed order, skipping blanks, names that don't
        /// resolve (so a trainer can list a skill the parallel engine hasn't shipped yet without erroring), and
        /// duplicates. Case-insensitive. Pure - unit tested in ClassAbilityTrainerParseTests.
        /// </summary>
        public static IReadOnlyList<ClassAbilityDefinition> ParseTrainerAbilities(string list)
        {
            var result = new List<ClassAbilityDefinition>();
            if (string.IsNullOrWhiteSpace(list))
                return result;

            var seen = new HashSet<ClassAbilityId>();
            foreach (var token in list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token.Trim();
                if (name.Length == 0)
                    continue;

                if (ClassAbilityRegistry.TryGetByName(name, out var def) && seen.Add(def.Id))
                    result.Add(def);
            }

            return result;
        }

        private static void Send(Player player, string message) =>
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));

        private static void HandleTrainer(WorldObject npc, Player player, IReadOnlyList<ClassAbilityDefinition> skills)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Send(player, "Class abilities are not currently enabled on this server.");
                return;
            }

            if (skills.Count == 0)
            {
                Send(player, $"{npc.Name} has nothing to teach right now.");
                return;
            }

            Send(player, $"{npc.Name}: I can train the following. You hold {player.AvailableClassAbilityPoints:N0} class ability point{(player.AvailableClassAbilityPoints == 1 ? "" : "s")}.");

            // For each stocked skill, describe its state and, when the next rank is buyable, queue a Yes/No offer.
            var choices = new List<(string prompt, Action onAccept)>();
            foreach (var def in skills)
            {
                var rank = player.GetClassAbilityRank(def.Id);

                if (rank >= def.MaxRank)
                {
                    Send(player, $"  {def.DisplayName}: mastered (rank {rank}/{def.MaxRank}).");
                    continue;
                }

                var nextTier = rank + 1;
                if (!ClassAbilityTokenCatalog.TryResolve(def.Id, nextTier, out var offering))
                {
                    Send(player, $"  {def.DisplayName}: not available yet.");
                    continue;
                }

                // Class-tier minimum gate (T2/T3, the real gate #86). Only the first rank is gated; later ranks
                // are already unlocked. Locked ranks show their reason here instead of being offered.
                if (rank == 0 && !player.MeetsClassAbilityTierUnlock(def, out var tierReason))
                {
                    Send(player, $"  {def.DisplayName}: locked - {tierReason}");
                    continue;
                }

                // Already holding an unused voucher for this skill - no stockpiling ahead of the current rank.
                if (HoldsVoucherFor(player, def.Id))
                {
                    Send(player, $"  {def.DisplayName}: you already hold a training token - use it first.");
                    continue;
                }

                var cost = def.CostPerRank[nextTier - 1];
                var affordable = player.AvailableClassAbilityPoints >= cost;
                Send(player, $"  {def.DisplayName}: rank {nextTier} for {cost:N0} point{(cost == 1 ? "" : "s")}{(affordable ? "" : " (more than you hold)")}.");

                var captured = offering;
                choices.Add((
                    $"Train {def.DisplayName} rank {nextTier} for {cost:N0} class ability point{(cost == 1 ? "" : "s")}?\n\nThis buys a bound training token; use it from your pack to learn the rank.",
                    () => { if (!player.TryPurchaseClassAbilityVoucher(captured, out var error)) Send(player, error); }));
            }

            if (choices.Count == 0)
            {
                Send(player, "There is nothing you can train from me right now.");
                return;
            }

            OfferChoices(player, choices, 0);
        }

        private static void HandleExchanger(WorldObject npc, Player player)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Send(player, "Class abilities are not currently enabled on this server.");
                return;
            }

            // Every class ability token is prepaid and therefore refundable; carrying a token id is the whole test.
            var vouchers = player.GetAllPossessions()
                .Where(wo => (wo.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0) > 0)
                .ToList();

            if (vouchers.Count == 0)
            {
                Send(player, $"{npc.Name}: You hold no unused training tokens for me to refund.");
                return;
            }

            Send(player, $"{npc.Name}: I can refund these unused tokens for the class ability points you paid:");

            var choices = new List<(string prompt, Action onAccept)>();
            foreach (var voucher in vouchers)
            {
                Send(player, $"  {voucher.Name}");

                var captured = voucher;
                choices.Add((
                    $"Refund {captured.Name} and recover its class ability points?",
                    () => { if (!player.RefundUnusedVoucher(captured, out var error)) Send(player, error); }));
            }

            OfferChoices(player, choices, 0);
        }

        /// <summary>
        /// The Arcane Pedestal interaction: prompts the player to exchange Luminance for one class ability point
        /// at the piecewise curve price (DESIGN.md sec 2b). On Yes it runs the same purchase the /abilities buy
        /// command used to; the item-level-up burst comes from GrantClassAbilityPoints, the purchase's own
        /// choke point. Luminance is drawn available-then-banked by the underlying purchase.
        /// </summary>
        private static void HandleLumExchange(WorldObject device, Player player)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Send(player, "Class abilities are not currently enabled on this server.");
                return;
            }

            var purchased = player.ClassAbilityPointsPurchasedWithLum;
            var cost = Player.LumCostForClassAbilityPoints(purchased, 1);
            var totalLum = player.GetSpendableLuminance();

            Send(player, $"{device.Name}: this weighs your Luminance and renders it as a class ability point. Each point costs more than the last; the only limit is the price.");

            if (totalLum < cost)
            {
                Send(player, $"Your next point would cost {cost:N0} Luminance; you have {totalLum:N0} ({player.AvailableLuminance ?? 0:N0} available + {player.BankedLuminance:N0} banked).");
                return;
            }

            var prompt = $"Exchange {cost:N0} Luminance for 1 class ability point?\n\nThis is point number {purchased + 1:N0}; the next will cost more. Luminance is drawn from your available balance first, then your bank.";

            var confirmation = new Confirmation_ClassAbilityChoice(player.Guid, accepted =>
            {
                if (!accepted)
                    return;

                // TryBuyClassAbilityPoints recomputes the price and charges available-then-banked; it also
                // messages the gain and plays the item-level-up burst via GrantClassAbilityPoints, so
                // there is nothing left to do here on success.
                if (!player.TryBuyClassAbilityPoints(1, out var error))
                    Send(player, error);
            });

            if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }
        /// <summary>
        /// The EXPERIENCE exchange stone: prompts the player to trade unassigned experience for one class
        /// ability point at the xp curve price (Docs/ClassAbilities/XP-LANE-SPEC.md sec 3). The sibling of
        /// <see cref="HandleLumExchange"/>, with three deliberate differences:
        ///  - it is gated on character level, because below the gate this exchange would compete with skill
        ///    and attribute training for the same experience pool,
        ///  - there is no experience bank, so affordability is a single AvailableExperience check rather
        ///    than available-plus-banked,
        ///  - it spends through SpendXP, which touches AvailableExperience only, so buying a point never
        ///    costs the character a level.
        /// </summary>
        private static void HandleXpExchange(WorldObject device, Player player)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Send(player, "Class abilities are not currently enabled on this server.");
                return;
            }

            var minLevel = Player.ClassAbilityXpMinLevel;
            if ((player.Level ?? 1) < minLevel)
            {
                Send(player, $"{device.Name}: this weighs the experience of a finished climb. Return at level {minLevel:N0}; you are {player.Level ?? 1:N0}.");
                return;
            }

            var purchased = player.ClassAbilityPointsPurchasedWithXp;
            var cost = Player.XpCostForClassAbilityPoints(purchased, 1);
            var available = player.AvailableExperience ?? 0;

            Send(player, $"{device.Name}: this weighs your unassigned experience and renders it as a class ability point. Each point costs more than the last; the only limit is the price.");

            if (available < cost)
            {
                Send(player, $"Your next point would cost {cost:N0} experience; you have {available:N0} unassigned.");
                return;
            }

            var prompt = $"Exchange {cost:N0} experience for 1 class ability point?\n\nThis is point number {purchased + 1:N0}; the next will cost more. Only your UNASSIGNED experience is spent - your level and total experience are untouched.";

            var confirmation = new Confirmation_ClassAbilityChoice(player.Guid, accepted =>
            {
                if (!accepted)
                    return;

                // TryBuyClassAbilityPointsWithXp re-checks the level gate and the price, spends through
                // SpendXP, and messages the gain via GrantClassAbilityPoints - nothing to do here on success.
                if (!player.TryBuyClassAbilityPointsWithXp(1, out var error))
                    Send(player, error);
            });

            if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// The full class-ability respec NPC: charges a flat Luminance fee and unlearns EVERY class ability
        /// the player currently knows in one pass, refunding all spent points. This is the "start over"
        /// button, distinct from the per-ability /abilities unlearn respec (which stays available at its own
        /// per-ability Luminance fee).
        ///
        /// The flat fee is LEVEL-GATED - see <see cref="FullRespecFee"/>. Below
        /// class_ability_full_respec_free_below_level (default 275) it is waived entirely and the respec is
        /// free; at and above that level the flat fee applies. The per-ability unlearn fee is not gated.
        ///
        /// TIER ACCESS RESETS BY ITSELF - there is nothing here to preserve or to strip. DESIGN.md sec 4
        /// describes a PURCHASABLE tier unlock persisted as a ClassAbilityTier_&lt;class&gt; quest row; that
        /// was designed and never built. No such row is written or read anywhere in the server. The live
        /// gate is <see cref="Player.MeetsClassAbilityTierUnlock"/>, which derives access from two values:
        /// TotalClassAbilityPointsEarned (lifetime, never decreases) and
        /// <see cref="Player.PointsSpentInClass"/>, which is computed from CURRENTLY OWNED ranks.
        ///
        /// So a full respec drops spent-in-class to 0 and every Tier 2/3 skill re-locks on its own until the
        /// player re-invests. That is the intended "start from scratch" behaviour (user, 2026-08-03). Only
        /// the lifetime-earned half survives, which it must - it is the same counter the level-milestone and
        /// enlightenment grants read, and resetting it would re-fire those grants.
        /// </summary>
        private static void HandleRespec(WorldObject npc, Player player)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Send(player, "Class abilities are not currently enabled on this server.");
                return;
            }

            // Snapshot of every currently-learned ability and its rank, NOT the live rank cache - unlearning
            // mutates that cache as it goes. ClassAbilityRegistry.Abilities.Values is the full catalog;
            // filtering + ToList materializes the owned subset up front.
            var owned = ClassAbilityRegistry.Abilities.Values
                .Select(skill => (skill, rank: player.GetClassAbilityRank(skill.Id)))
                .Where(entry => entry.rank > 0)
                .ToList();

            if (owned.Count == 0)
            {
                Send(player, $"{npc.Name}: You have not learned any class abilities - there is nothing for me to respec.");
                return;
            }

            var totalRefund = owned.Sum(entry => entry.skill.CumulativeCost(entry.rank));
            var fee = FullRespecFee(player.Level ?? 1);

            Send(player, $"{npc.Name}: I can unlearn every class ability you know at once. You currently know:");
            foreach (var (skill, rank) in owned)
                Send(player, $"  {skill.DisplayName} - rank {rank}/{skill.MaxRank}");

            if (fee <= 0)
                Send(player, $"{npc.Name}: You are still finding your way, so I ask nothing for this. When you have come into your full strength, the unbinding will carry a price.");

            var price = fee <= 0 ? "free of charge" : $"for {fee:N0} Luminance";

            var prompt = $"Unlearn all {owned.Count} class abilities and recover {totalRefund:N0} class ability point{(totalRefund == 1 ? "" : "s")} {price}?\n\nTier 2 and Tier 3 skills will re-lock until you reinvest.";

            var confirmation = new Confirmation_ClassAbilityChoice(player.Guid, accepted =>
            {
                if (!accepted)
                    return;

                // Re-validate and re-snapshot here - state (Luminance, learned abilities) may have changed
                // while the confirmation was outstanding.
                var current = ClassAbilityRegistry.Abilities.Values
                    .Select(skill => (skill, rank: player.GetClassAbilityRank(skill.Id)))
                    .Where(entry => entry.rank > 0)
                    .ToList();

                if (current.Count == 0)
                {
                    Send(player, "You have not learned any class abilities - there is nothing to respec.");
                    return;
                }

                // Re-read the level as well as the tunables: the fee is level-gated, so a player who
                // dinged the charging level while the confirmation was outstanding pays, and one who did not,
                // does not. A zero fee passes straight through TrySpendLuminanceIncludingBank untouched.
                var currentFee = FullRespecFee(player.Level ?? 1);

                // All-or-nothing: TrySpendLuminanceIncludingBank checks the combined available+banked total
                // BEFORE drawing from either pool, so a failed charge leaves every learned ability and every
                // point completely untouched - nothing below this line runs unless the fee is paid in full.
                if (!player.TrySpendLuminanceIncludingBank(currentFee))
                {
                    Send(player, $"The full respec costs {currentFee:N0} Luminance; you have {player.GetSpendableLuminance():N0} ({player.AvailableLuminance ?? 0:N0} available + {player.BankedLuminance:N0} banked).");
                    return;
                }

                // One batch id shared by every row this loop emits, so a CAP incident read can tell "one
                // respec that touched N abilities" from "N separate unlearns" - the whole reason
                // character_cap_ledger.batch_Id exists. 32 chars, matching the char(32) column.
                var batchId = System.Guid.NewGuid().ToString("N");

                var refundedTotal = 0;
                foreach (var (skill, _) in current)
                {
                    // ignoreFee: true - the flat price already charged above covers every ability being
                    // unlearned, so UnlearnClassAbility's own per-ability Luminance fee must not ALSO apply.
                    if (player.UnlearnClassAbility(skill, out var error, out var refunded, ignoreFee: true,
                            reason: CapLedgerReason.Respec, batchId: batchId))
                        refundedTotal += refunded;
                    else
                        Send(player, $"{skill.DisplayName}: {error}");
                }

                Send(player, $"You have unlearned all {current.Count} class abilities and recovered {refundedTotal:N0} class ability point{(refundedTotal == 1 ? "" : "s")}. Tier 2 and Tier 3 skills are locked again until you have reinvested in a class. Available: {player.AvailableClassAbilityPoints:N0}.");
            });

            if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// Luminance price of the full respec for a character at <paramref name="level"/>. The flat
        /// class_ability_full_respec_lum_cost is WAIVED entirely below
        /// class_ability_full_respec_free_below_level (default 275) and charged in full at and above it: a
        /// character still levelling is expected to re-plan its build as it grows, while a maxed character
        /// respeccing is an endgame decision that should cost something. The boundary matches
        /// class_ability_xp_min_level - 275 is where skills are effectively maxed and the build is settled.
        /// A free-below level of 0 disables the waiver and charges the flat fee at every level.
        ///
        /// A zero fee is safe to pass straight to TrySpendLuminanceIncludingBank, which returns true without
        /// touching either Luminance pool for a non-positive amount.
        /// </summary>
        internal static long FullRespecFee(int level)
        {
            var freeBelowLevel = PropertyManager.GetLong("class_ability_full_respec_free_below_level").Item;

            if (freeBelowLevel > 0 && level < freeBelowLevel)
                return 0;

            return PropertyManager.GetLong("class_ability_full_respec_lum_cost").Item;
        }

        private static bool HoldsVoucherFor(Player player, ClassAbilityId skillId) =>
            player.GetAllPossessions().Any(wo =>
                (wo.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0) == (int)skillId);

        /// <summary>
        /// Presents a list of Yes/No choices one at a time. Yes runs that choice's action and ends the chain;
        /// No (or a lapsed prompt does nothing and) advances to the next. Recurses via the confirmation callback,
        /// so there is no stack growth and only one confirmation is ever outstanding.
        /// </summary>
        private static void OfferChoices(Player player, List<(string prompt, Action onAccept)> choices, int index)
        {
            if (index >= choices.Count)
                return;

            var (prompt, onAccept) = choices[index];

            var confirmation = new Confirmation_ClassAbilityChoice(player.Guid, accepted =>
            {
                if (accepted)
                    onAccept();
                else
                    OfferChoices(player, choices, index + 1);
            });

            if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// A Yes/No confirmation that hands the response back to a callback (unlike Confirmation_Custom, which
        /// only fires on Yes), letting the trainer/exchanger advance its option chain on No. A timeout ends the
        /// chain quietly.
        /// </summary>
        private class Confirmation_ClassAbilityChoice : Confirmation
        {
            private readonly Action<bool> onResponse;

            public Confirmation_ClassAbilityChoice(ObjectGuid playerGuid, Action<bool> onResponse)
                : base(playerGuid, ConfirmationType.Yes_No)
            {
                this.onResponse = onResponse;
            }

            public override void ProcessConfirmation(bool response, bool timeout = false)
            {
                if (Player == null || timeout)
                    return;

                onResponse(response);
            }
        }
    }
}
