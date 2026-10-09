using System;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.RefireStations;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE, DreamWeave, 2026-09-05: the give path for the three Custom Dreamweave Augmentation
    /// brokers (Fi / Bo / Nacci). ONE handler over all three, dispatched by the single
    /// PropertyBool.CustomAugBroker marker in Player_Inventory.GiveObjectToNPC and parameterised by the
    /// NPC's own PropertyInt.AugmentationStat, which names the AugmentationType it brokers. Adding a
    /// fourth broker is content-only: a catalog row in <see cref="CustomAugmentations"/> plus a weenie.
    ///
    /// The player hands over ONE Blank Augmentation Gem; the broker charges a Fibonacci number of them
    /// out of the pack (priced on how many of that augmentation the buyer already owns) and hands back
    /// one upgraded augmentation gem, which the player then uses through the ordinary
    /// AugmentationDevice path.
    ///
    /// THE ORDERING IS THE SAFETY PROPERTY, not a style choice. Player.TryConsumeFromInventoryWithNetworking
    /// (uint wcid, int amount) performs NO total pre-check: it walks the matching items destroying each
    /// one in turn, so a mid-loop removal failure returns false having ALREADY destroyed some, and a
    /// plain shortfall (fewer gems held than asked for) walks the whole list, destroys every one of them
    /// and then returns TRUE - see Player_Inventory.cs:223. The only thing that makes either outcome
    /// unreachable here is <see cref="TryVerify"/>'s have-versus-cost check running BEFORE the consume,
    /// and running again on confirmation. Do not reorder it and do not "simplify" it away.
    ///
    /// PropertyManager reads live in this class only, never in <see cref="CustomAugmentations"/>, which
    /// stays pure so its pricing math is unit-testable (PropertyManager reads THROW in unit tests on a
    /// cache miss - the same split PickupSpeed.cs documents).
    /// </summary>
    public static class CustomAugBroker
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Extra vault entries granted per AugmentationMuleSpace the account holds.</summary>
        public const int MuleSpaceEntriesPerAug = 100;

        // ---------------- tunables ----------------

        /// <summary>custom_aug_brokers_enabled - the kill switch. All three brokers stop trading when false.</summary>
        public static bool Enabled => PropertyManager.GetBool("custom_aug_brokers_enabled").Item;

        /// <summary>custom_aug_gem_wcid - the currency, default 29295 "Blank Augmentation Gem".</summary>
        public static uint GemWcid
        {
            get
            {
                var value = PropertyManager.GetLong("custom_aug_gem_wcid").Item;

                if (value <= 0 || value > uint.MaxValue)
                    return 0;

                return (uint)value;
            }
        }

        /// <summary>
        /// custom_aug_cost_index_cap - the Fibonacci index the price plateaus at, so the cost curve
        /// flattens instead of running away (and, at the far end, instead of overflowing: fib(47)
        /// already exceeds int.MaxValue).
        /// </summary>
        public static int CostIndexCap
        {
            get
            {
                var value = PropertyManager.GetLong("custom_aug_cost_index_cap").Item;

                if (value < 0)
                    return 0;

                if (value > int.MaxValue)
                    return int.MaxValue;

                return (int)value;
            }
        }

        /// <summary>
        /// custom_aug_pickup_speed_bonus - the additive pick-up speed fraction per AugmentationPickupSpeed
        /// the character holds. Read by Player_PickupBoons and fed to PickupSpeed's pure math; it lives here
        /// with the feature's other PropertyManager reads so PickupSpeed itself stays PropertyManager-free.
        /// </summary>
        public static double PickupSpeedBonus => PropertyManager.GetDouble("custom_aug_pickup_speed_bonus").Item;

        /// <summary>
        /// custom_aug_spell_duration_bonus - the additive spell duration fraction per
        /// AugmentationSpellDurationCustom the character holds. Read at all three duration sites
        /// (EnchantmentManager.Add's refresh path, EnchantmentManager.BuildEntry and
        /// AddEnchantmentResult.BuildStack); named once here so the three cannot drift on a typo'd key.
        /// </summary>
        public static double SpellDurationBonus => PropertyManager.GetDouble("custom_aug_spell_duration_bonus").Item;

        // ---------------- account-wide counting (Step 4a) ----------------

        /// <summary>
        /// Sums <paramref name="prop"/> across EVERY character on the account, online and offline, from
        /// PlayerManager's in-memory index. No database read.
        ///
        /// Mirrors Player_AltCharacterBonus.InitAltCharacterBonus in skipping deleted and pending-deletion
        /// characters, and deliberately does NOT skip mules: that method skips them when picking a
        /// progression TARGET, which is a different question. A mule that bought vault space paid for it
        /// and its account keeps the benefit.
        ///
        /// Returns 0 for account id 0 and for an account PlayerManager knows nothing about
        /// (GetAccountPlayersSnapshot returns an empty list), which is what makes this safe to call from a
        /// unit-test context with no PlayerManager state. Summed in long and clamped to
        /// [0, int.MaxValue] so a corrupt property value cannot overflow a caller's arithmetic.
        ///
        /// This lives here rather than in <see cref="CustomAugmentations"/> because it references
        /// PlayerManager; that class is deliberately dependency-free.
        ///
        /// LOCKING: PlayerManager.GetAccountPlayersSnapshot takes PlayerManager's own read lock
        /// (PlayerManager.cs:410). Never call this while holding another subsystem's lock - see
        /// AccountVaultStore.RefreshCapacityBonuses for the one caller that has to care.
        ///
        /// NO CROSS-CHARACTER PRICING RACE, traced rather than assumed. The account-wide price index
        /// is read without a lock, so two characters on one account buying at the same moment would
        /// both see the same count and both pay the lower price. That cannot happen: a second session
        /// for an account is refused at login. AuthenticationHandler calls
        /// NetworkManager.Find(account.AccountName) on BOTH branches of account_login_boots_in_use -
        /// when the switch is off it terminates the incoming session outright, and when it is on it
        /// boots the existing session AND still terminates the incoming one, telling it to retry.
        /// Either way no second session is admitted, and one session holds one Player. This is the
        /// same invariant Player_AltCharacterBonus relies on to cache its catch-up target at login.
        /// If a future change ever admits two sessions per account, this pricing needs a per-account
        /// lock around the read-decide-charge sequence in HandleConfirm.
        /// </summary>
        public static int AccountAugCount(uint accountId, PropertyInt prop)
        {
            if (accountId == 0)
                return 0;

            var total = 0L;

            foreach (var character in PlayerManager.GetAccountPlayersSnapshot(accountId))
            {
                if (character.IsDeleted || character.IsPendingDeletion)
                    continue;

                var owned = character.GetProperty(prop) ?? 0;

                if (owned > 0)
                    total += owned;
            }

            if (total < 0)
                return 0;

            if (total > int.MaxValue)
                return int.MaxValue;

            return (int)total;
        }

        // ---------------- the trade decision ----------------

        /// <summary>
        /// Everything <see cref="TryVerify"/> resolved about one prospective trade. Rebuilt from scratch
        /// on confirmation rather than carried across the dialog, because every field in it can change
        /// while the prompt is open.
        /// </summary>
        public class TradeQuote
        {
            public AugmentationType Type { get; set; }
            public CustomAugmentations.CatalogEntry Entry { get; set; }
            public uint GemWcid { get; set; }
            public int Owned { get; set; }
            public int Have { get; set; }
            public long Cost { get; set; }
        }

        /// <summary>
        /// PURE affordability gate, and the single reason partial consumption is unreachable. Prices the
        /// next augmentation from <paramref name="owned"/> and refuses unless the buyer is holding at
        /// least that many gems, naming BOTH numbers so the refusal is actionable.
        ///
        /// Player-free and PropertyManager-free on purpose: this is the rule the give path, the
        /// confirmation re-check and the unit tests all run, so none of them can drift from the others.
        /// </summary>
        public static bool TryAfford(int owned, int have, int ceilingIndex, out long cost, out string refusal)
        {
            cost = CustomAugmentations.CostFor(owned, ceilingIndex);

            if (have < cost)
            {
                refusal = $"That will cost {cost} Blank Augmentation Gem{(cost == 1 ? "" : "s")} and you are carrying {have}. Come back when you have enough - I have taken nothing.";
                return false;
            }

            refusal = null;
            return true;
        }

        /// <summary>
        /// The charge, wrapped so the have-versus-cost gate and the consume can never be separated. The
        /// consume runs ONLY after <see cref="TryAfford"/> passes; <paramref name="consume"/> is a seam
        /// (the same shape SalvageForge.TryForgeHammer uses for its own post-check work) so a unit test
        /// can prove the short-count case never reaches it without standing up a live Player.
        ///
        /// Returns false with <paramref name="consumed"/> false for a shortfall - nothing was touched -
        /// and false with <paramref name="consumed"/> true if the consume itself failed after being
        /// entered, which is the "contact staff" case: by then items may already be gone.
        /// </summary>
        public static bool TryCharge(int owned, int have, int ceilingIndex, Func<int, bool> consume, out long cost, out bool consumed, out string refusal)
        {
            consumed = false;

            if (!TryAfford(owned, have, ceilingIndex, out cost, out refusal))
                return false;

            consumed = true;

            if (consume == null || !consume((int)cost))
            {
                refusal = "Something went wrong taking your gems. Please contact staff before trading again.";
                return false;
            }

            refusal = null;
            return true;
        }

        // ---------------- the give path ----------------

        /// <summary>
        /// Entry point, called from Player_Inventory.GiveObjectToNPC once PropertyBool.CustomAugBroker has
        /// been checked and the given gem has been bounced back into the player's pack with a
        /// GameEventInventoryServerSaveFailed. Nothing below ever takes the given item itself: the whole
        /// price, including that gem, is consumed from inventory by wcid after the confirmation.
        ///
        /// Step order (each numbered step is one of the plan's, and the numbering is load-bearing):
        /// 1 kill switch, 2 resolve the brokered type, 3 check the given item is the currency,
        /// 4 count owned, 5 price it, 6 refuse a short count taking NOTHING, 7 pack-space pre-flight,
        /// 8 confirm, 9 re-run 1-7, 10 consume, 11 grant, 12 log.
        /// </summary>
        public static void HandleGive(Player player, WorldObject broker, WorldObject item)
        {
            if (player == null || broker == null || item == null)
                return;

            var givenWcid = item.WeenieClassId;

            if (!TryVerify(player, broker, givenWcid, out var quote, out var refusal))
            {
                Refuse(player, broker, refusal);
                return;
            }

            var prompt =
                $"{broker.Name} will trade you {quote.Entry.DisplayName} ({quote.Entry.EffectBlurb}) " +
                $"for {quote.Cost} Blank Augmentation Gem{(quote.Cost == 1 ? "" : "s")}. " +
                $"You are carrying {quote.Have}. This augmentation is uncapped, and the price rises each time. Proceed?";

            if (!player.ConfirmationManager.EnqueueSend(
                    new Confirmation_Custom(player.Guid, () => HandleConfirm(player, broker, givenWcid)),
                    prompt))
            {
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
            }
        }

        /// <summary>
        /// Runs on confirmation. Re-runs the WHOLE gate (steps 1-7) against current state before charging:
        /// the dialog gives the player time to move, spend or bank gems, to buy the augmentation elsewhere
        /// (which raises the price), or for an admin to flip the kill switch. Same reason
        /// RefireStationCommon.HandleConfirm re-verifies.
        /// </summary>
        private static void HandleConfirm(Player player, WorldObject broker, uint givenWcid)
        {
            if (player == null || broker == null)
                return;

            if (!TryVerify(player, broker, givenWcid, out var quote, out var refusal))
            {
                Refuse(player, broker, refusal);
                return;
            }

            var gemWcid = quote.GemWcid;

            // Step 10. TryCharge runs TryAfford one more time against the numbers TryVerify just read, so
            // the consume below is entered only with have >= cost. A false from the consume itself is now
            // a removal failure, never a shortfall.
            if (!TryCharge(
                    quote.Owned,
                    quote.Have,
                    CostIndexCap,
                    amount => player.TryConsumeFromInventoryWithNetworking(gemWcid, amount),
                    out var cost,
                    out var consumed,
                    out var chargeRefusal))
            {
                if (consumed)
                {
                    log.Error($"[CUSTOM AUG] account '{player.Account?.AccountName ?? "unknown"}' ({player.Account?.AccountId.ToString() ?? "?"}), character {player.Name} (0x{player.Guid}): TryConsumeFromInventoryWithNetworking(wcid {gemWcid}, {cost}) FAILED after the affordability check passed ({quote.Have} held). Some gems may already be destroyed and NO reward was granted.");
                }

                Refuse(player, broker, chargeRefusal);
                return;
            }

            // Step 11.
            var reward = WorldObjectFactory.CreateNewWorldObject(quote.Entry.RewardGemWcid);

            if (reward == null || !player.TryCreateInInventoryWithNetworking(reward))
            {
                reward?.Destroy();

                log.Error($"[CUSTOM AUG] account '{player.Account?.AccountName ?? "unknown"}' ({player.Account?.AccountId.ToString() ?? "?"}), character {player.Name} (0x{player.Guid}): consumed {cost} gem(s) (wcid {gemWcid}) for {quote.Type} but could not hand over reward gem wcid {quote.Entry.RewardGemWcid} ({(reward == null ? "weenie missing" : "inventory add failed")}).");

                Refuse(player, broker, "Your gems are spent, but the augmentation gem failed to appear. Please contact staff.");
                return;
            }

            // Step 12. One line per successful trade - this is the only audit record of the price paid.
            log.Info($"[CUSTOM AUG] account '{player.Account?.AccountName ?? "unknown"}' ({player.Account?.AccountId.ToString() ?? "?"}), character {player.Name} (0x{player.Guid}) bought {quote.Type} ({quote.Entry.DisplayName}) for {cost} gem(s) at owned={quote.Owned}; reward wcid {quote.Entry.RewardGemWcid}.");

            RefireStationCommon.SendStationTell(player, broker, $"Done. {reward.Name} is yours - use it when you are ready. That is {quote.Owned + 1} once you do.");

            // An account-wide augmentation's benefit is read from a cached count elsewhere, so tell that
            // cache to recompute. Today the only account-wide entry is MuleSpace and the only cache is
            // AccountVaultStore.muleSpaceBonusEntries.
            //
            // KNOWN GAP, and it is deliberate rather than overlooked: the trade hands over a GEM, and
            // PropertyInt.AugmentationMuleSpace only rises when the player USES it through
            // AugmentationDevice.DoAugmentation. This call therefore recomputes the same number it
            // already held. It is kept because it is free, because it is correct the moment the count
            // does move here, and because a store loaded after the gem is used picks the bonus up in its
            // constructor either way - a missed refresh is a delayed benefit, never a lost one. Making
            // the bonus apply the instant the gem is used needs a refresh at the DoAugmentation site,
            // which is outside this step's scope.
            if (quote.Entry.IsAccountWide)
                AccountVaultManager.RefreshCapacityBonuses(player.Account?.AccountId ?? 0);
        }

        /// <summary>
        /// Steps 1 through 7, run identically on the give and on the confirmation. Returns false with a
        /// player-facing <paramref name="refusal"/> and NOTHING consumed.
        ///
        /// Step 2's misconfiguration cases (no AugmentationStat, or one naming a type that is not in the
        /// catalog) log a WARNING naming the NPC wcid and refuse politely. They never throw: this runs on
        /// the give path, inside a movement callback with no exception boundary above it, so a bad content
        /// row must cost a confused player one refusal, not a server-side exception.
        /// </summary>
        private static bool TryVerify(Player player, WorldObject broker, uint givenWcid, out TradeQuote quote, out string refusal)
        {
            quote = null;

            // 1 - kill switch
            if (!Enabled)
            {
                refusal = "I am not trading right now. Try me again later.";
                return false;
            }

            // 2 - which augmentation does this NPC broker?
            var stat = broker.GetProperty(PropertyInt.AugmentationStat);

            if (stat == null)
            {
                log.Warn($"[CUSTOM AUG] broker '{broker.Name}' (wcid {broker.WeenieClassId}, 0x{broker.Guid}) carries PropertyBool.CustomAugBroker but NO PropertyInt.AugmentationStat (215). Refusing the trade; fix the weenie.");
                refusal = "I seem to have forgotten my trade. Please let the staff know.";
                return false;
            }

            var type = (AugmentationType)stat.Value;

            if (!CustomAugmentations.Catalog.TryGetValue(type, out var entry))
            {
                log.Warn($"[CUSTOM AUG] broker '{broker.Name}' (wcid {broker.WeenieClassId}, 0x{broker.Guid}) names AugmentationStat {stat.Value}, which is not a Custom Dreamweave Augmentation. Refusing the trade; fix the weenie.");
                refusal = "I seem to have forgotten my trade. Please let the staff know.";
                return false;
            }

            // 3 - is the given item the currency? The gem is already back in the player's pack from the
            // GameEventInventoryServerSaveFailed at the intercept, so a refusal here loses nothing.
            var gemWcid = GemWcid;

            if (gemWcid == 0)
            {
                log.Warn($"[CUSTOM AUG] 'custom_aug_gem_wcid' is set to a value outside the wcid range; every broker refuses until it is fixed.");
                refusal = "I seem to have forgotten my trade. Please let the staff know.";
                return false;
            }

            if (givenWcid != gemWcid)
            {
                // The one per-broker line on this path: each catalog entry carries its own wording so Fi, Bo
                // and Nacci do not all refuse in the same voice. Falls back to the shared line if a future
                // catalog entry is added without one, so a missing string is a flat message rather than a
                // null tell.
                refusal = string.IsNullOrWhiteSpace(entry.WrongItemRefusal)
                    ? $"I only deal in Blank Augmentation Gems. Bring me those and I will trade you {entry.DisplayName}."
                    : entry.WrongItemRefusal;
                return false;
            }

            // 4 - how many of this augmentation does the buyer already own? MuleSpace is priced and granted
            // account-wide, so its count is the sum across every character on the account.
            var owned = entry.IsAccountWide
                ? AccountAugCount(player.Account?.AccountId ?? 0, entry.Property)
                : player.GetProperty(entry.Property) ?? 0;

            // 5 + 6 - price it, and refuse a short count taking NOTHING.
            var have = player.GetNumInventoryItemsOfWCID(gemWcid);

            if (!TryAfford(owned, have, CostIndexCap, out var cost, out refusal))
                return false;

            // 7 - pack-space pre-flight for the single reward gem, BEFORE anything is consumed.
            // PreflightEmoteGive covers the emote give path only and is gated off by default, so it does
            // not reach here. The outgoing gems are deliberately NOT credited (ItemsToReceive.CreditOutgoing),
            // matching FragmentPressStation.HasRoomFor: they may sit in a side pack, and crediting a slot
            // that is not the one the reward lands in would let a failing case through. That over-refuses a
            // player whose pack is exactly full, which costs one "make room" round trip and nothing else,
            // because the refusal happens before any charge.
            var itemsToReceive = new ItemsToReceive(player);
            itemsToReceive.Add(entry.RewardGemWcid, 1);

            if (itemsToReceive.PlayerExceedsLimits)
            {
                refusal = "You have no room for what I would hand back. Make some space and come see me again.";
                return false;
            }

            quote = new TradeQuote
            {
                Type = type,
                Entry = entry,
                GemWcid = gemWcid,
                Owned = owned,
                Have = have,
                Cost = cost,
            };

            refusal = null;
            return true;
        }

        /// <summary>
        /// These NPCs speak, unlike the refire stations, so a refusal is a Tell from the broker rather
        /// than a silent bounce or a system chat line.
        /// </summary>
        private static void Refuse(Player player, WorldObject broker, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            RefireStationCommon.SendStationTell(player, broker, message);
        }
    }
}
