using System;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.CapacityUpgrades
{
    /// <summary>
    /// ONE purchase flow for both account capacity upgrades (/mule upgrade, /market upgrade),
    /// parameterised by <see cref="CapacityUpgradeKind"/>. Modelled on CustomAugBroker's give/confirm
    /// split: <see cref="TryVerify"/> is the whole gate, run identically at the prompt and again on
    /// confirmation, so the two can never drift.
    ///
    /// The pure decision (kill switch, count availability, pricing, affordability) is
    /// <see cref="Decide"/>, which reads nothing from PropertyManager or a live Player - every input is
    /// a parameter - so <c>CapacityUpgradeBrokerTests</c> can exercise every refusal with no live state.
    /// <see cref="TryVerify"/> is the thin, Player-bound wrapper that supplies those parameters.
    /// </summary>
    public static class CapacityUpgradeBroker
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // ---------------- pure decision ----------------

        public enum CheckResult
        {
            Ok,
            Disabled,
            CountUnavailable,
            AtMax,
            PricingUnavailable,
            InsufficientFunds,
        }

        /// <summary>Everything one quote needs to prompt, confirm and charge.</summary>
        public readonly struct Quote
        {
            public readonly CapacityUpgradeKind Kind;
            public readonly int Owned;
            public readonly long Mmd;
            public readonly long Pyreals;
            public readonly int CurrentCap;
            public readonly int NewCap;

            public Quote(CapacityUpgradeKind kind, int owned, long mmd, long pyreals, int currentCap, int newCap)
            {
                Kind = kind;
                Owned = owned;
                Mmd = mmd;
                Pyreals = pyreals;
                CurrentCap = currentCap;
                NewCap = newCap;
            }
        }

        /// <summary>
        /// PURE: no PropertyManager or Player reads. <paramref name="countOk"/> must come from
        /// <see cref="AccountCapacityUpgradeManager.TryGetCount"/> - a failed read is never treated as
        /// owning 0. <paramref name="currentCap"/> is the cap BEFORE this purchase (base plus any other
        /// bonus plus <paramref name="owned"/> upgrades already bought); <paramref name="perUpgradeAmount"/>
        /// is added to it for the quote's NewCap.
        /// </summary>
        public static CheckResult Decide(
            bool enabled,
            bool countOk,
            int owned,
            long baseMmd,
            long growthPct,
            long maxCount,
            int currentCap,
            int perUpgradeAmount,
            long bankedPyreals,
            out Quote quote)
        {
            quote = default;

            if (!enabled)
                return CheckResult.Disabled;

            if (!countOk)
                return CheckResult.CountUnavailable;

            // Distinguish "genuinely at the max" from "cannot be priced" (a misconfigured
            // baseMmd/growthPct, or an overflow) BEFORE calling TryQuote, so a bad setting is never
            // reported to a player as having bought every upgrade. TryQuote itself stays pure and
            // unchanged - this mirrors its own owned/limit check.
            var limit = Math.Min(maxCount, CapacityUpgradePricing.HardMaxPurchases);
            if (limit < 0)
                limit = 0;

            if (owned >= limit)
                return CheckResult.AtMax;

            if (!CapacityUpgradePricing.TryQuote(owned, baseMmd, growthPct, maxCount, out var mmd, out var pyreals))
                return CheckResult.PricingUnavailable;

            var newCap = currentCap + perUpgradeAmount;

            if (bankedPyreals < pyreals)
            {
                quote = new Quote(0, owned, mmd, pyreals, currentCap, newCap);
                return CheckResult.InsufficientFunds;
            }

            quote = new Quote(0, owned, mmd, pyreals, currentCap, newCap);
            return CheckResult.Ok;
        }

        // ---------------- per-kind configuration ----------------

        private sealed class KindSpec
        {
            public CapacityUpgradeKind Kind;
            public string Prefix;
            public int PerUpgradeAmount;
            public Func<bool> Enabled;
            public Func<long> MaxCount;

            /// <summary>Cap contributed by everything OTHER than this account's capacity upgrades.</summary>
            public Func<uint, int> OtherCap;
            public Action<uint> RefreshCaps;
            public string CapNoun;
        }

        private static KindSpec GetSpec(CapacityUpgradeKind kind)
        {
            switch (kind)
            {
                case CapacityUpgradeKind.MuleVault:
                    return new KindSpec
                    {
                        Kind = kind,
                        Prefix = "[MULE]",
                        PerUpgradeAmount = CapacityUpgradePricing.MuleEntriesPerUpgrade,
                        Enabled = () => PropertyManager.GetBool("mule_upgrade_enabled").Item,
                        MaxCount = () => PropertyManager.GetLong("mule_upgrade_max_count").Item,
                        OtherCap = accountId => AccountVaultStore.BaseEntryCap
                            + CustomAugBroker.AccountAugCount(accountId, PropertyInt.AugmentationMuleSpace) * CustomAugBroker.MuleSpaceEntriesPerAug,
                        RefreshCaps = AccountVaultManager.RefreshCapacityBonuses,
                        CapNoun = "entries",
                    };

                case CapacityUpgradeKind.MarketListings:
                    return new KindSpec
                    {
                        Kind = kind,
                        Prefix = "[MARKET]",
                        PerUpgradeAmount = CapacityUpgradePricing.ListingsPerUpgrade,
                        Enabled = () => PropertyManager.GetBool("market_upgrade_enabled").Item,
                        MaxCount = () => PropertyManager.GetLong("market_upgrade_max_count").Item,
                        OtherCap = accountId => (int)Math.Max(1, PropertyManager.GetLong("market_max_listings_per_account").Item),
                        // MarketManager.EffectiveListingCap reads AccountCapacityUpgradeManager directly
                        // (never its own cache), so there is nothing here to invalidate.
                        RefreshCaps = _ => { },
                        CapNoun = "listings",
                    };

                default:
                    return null;
            }
        }

        // ---------------- Player-bound verify ----------------

        /// <summary>
        /// Runs the whole gate against CURRENT state and returns a player-facing refusal, or true with a
        /// populated <paramref name="quote"/>. Called identically from the prompt and from the confirm
        /// handler.
        /// </summary>
        private static bool TryVerify(Player player, CapacityUpgradeKind kind, out Quote quote, out string refusal)
        {
            quote = default;
            refusal = null;

            var spec = GetSpec(kind);
            var accountId = player.Account?.AccountId ?? 0;

            if (accountId == 0)
            {
                refusal = $"{spec.Prefix} Your upgrade record could not be read. Nothing was charged. Try again shortly.";
                return false;
            }

            var enabled = spec.Enabled();
            var countOk = AccountCapacityUpgradeManager.TryGetCount(accountId, kind, out var owned);
            var baseMmd = PropertyManager.GetLong("capacity_upgrade_base_cost_mmd").Item;
            var growthPct = PropertyManager.GetLong("capacity_upgrade_cost_growth_percent").Item;
            var maxCount = spec.MaxCount();
            var currentCap = spec.OtherCap(accountId) + owned * spec.PerUpgradeAmount;
            var bankedPyreals = player.BankedPyreals;

            var result = Decide(enabled, countOk, owned, baseMmd, growthPct, maxCount, currentCap, spec.PerUpgradeAmount, bankedPyreals, out var decided);

            // Quote's Kind isn't set by the PropertyManager-free Decide (it never sees the enum), so
            // it's stamped here.
            quote = new Quote(kind, decided.Owned, decided.Mmd, decided.Pyreals, decided.CurrentCap, decided.NewCap);

            switch (result)
            {
                case CheckResult.Ok:
                    return true;

                case CheckResult.Disabled:
                    refusal = $"{spec.Prefix} Upgrades are not available right now.";
                    return false;

                case CheckResult.CountUnavailable:
                    refusal = $"{spec.Prefix} Your upgrade record could not be read. Nothing was charged. Try again shortly.";
                    return false;

                case CheckResult.AtMax:
                    var effectiveMax = Math.Min(maxCount, CapacityUpgradePricing.HardMaxPurchases);
                    if (effectiveMax < 0)
                        effectiveMax = 0;
                    refusal = $"{spec.Prefix} Your account has bought every available upgrade ({effectiveMax:N0}).";
                    return false;

                case CheckResult.PricingUnavailable:
                    log.Warn($"[CAPACITY UPGRADE] pricing unavailable for kind={kind}: capacity_upgrade_base_cost_mmd={baseMmd} capacity_upgrade_cost_growth_percent={growthPct}");
                    refusal = $"{spec.Prefix} Upgrades are not available right now.";
                    return false;

                case CheckResult.InsufficientFunds:
                    refusal = $"{spec.Prefix} Upgrade #{quote.Owned + 1} costs {quote.Mmd:N0} MMD ({quote.Pyreals:N0} pyreals), and your bank holds {bankedPyreals:N0} pyreals. Use /bank deposit notes to bank the Trade Notes you are carrying.";
                    return false;

                default:
                    refusal = $"{spec.Prefix} The purchase could not be confirmed. Do not retry yet: relog, then run the command again to see whether it went through.";
                    return false;
            }
        }

        // ---------------- entry point ----------------

        /// <summary>/mule upgrade and /market upgrade both call this.</summary>
        public static void RequestUpgrade(Player player, CapacityUpgradeKind kind)
        {
            if (player == null)
                return;

            if (!TryVerify(player, kind, out var quote, out var refusal))
            {
                Refuse(player, refusal);
                return;
            }

            var prompt = kind == CapacityUpgradeKind.MuleVault
                ? $"Buy mule vault upgrade #{quote.Owned + 1} for {quote.Mmd:N0} MMD ({quote.Pyreals:N0} pyreals) from your bank? Your account vault grows from {quote.CurrentCap:N0} to {quote.NewCap:N0} entries."
                : $"Buy market listing upgrade #{quote.Owned + 1} for {quote.Mmd:N0} MMD ({quote.Pyreals:N0} pyreals) from your bank? Your account listing limit grows from {quote.CurrentCap:N0} to {quote.NewCap:N0}.";

            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => HandleConfirm(player, kind, quote)), prompt))
            {
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
            }
        }

        /// <summary>
        /// Runs on Yes. Re-verifies from scratch, then refuses unless the fresh quote's count AND cost
        /// match the one shown at the prompt EXACTLY - the price is never silently re-quoted.
        /// </summary>
        private static void HandleConfirm(Player player, CapacityUpgradeKind kind, Quote originalQuote)
        {
            var spec = GetSpec(kind);

            if (!TryVerify(player, kind, out var freshQuote, out var refusal))
            {
                Refuse(player, refusal);
                return;
            }

            if (freshQuote.Owned != originalQuote.Owned || freshQuote.Mmd != originalQuote.Mmd || freshQuote.Pyreals != originalQuote.Pyreals)
            {
                Refuse(player, $"{spec.Prefix} The upgrade changed before you confirmed. Nothing was charged. Run the command again to see the current price.");
                return;
            }

            var accountId = player.Account?.AccountId ?? 0;

            var purchase = AccountCapacityUpgradeManager.TryPurchase(
                accountId, kind, originalQuote.Owned, originalQuote.Mmd, originalQuote.Pyreals, player.Guid.Full, out var newBalance);

            switch (purchase)
            {
                case CapacityUpgradePurchaseResult.Applied:
                {
                    spec.RefreshCaps(accountId);

                    player.PlayParticleEffect(PlayScript.AetheriaLevelUp, player.Guid);

                    var n = originalQuote.Owned + 1;
                    var newCap = originalQuote.NewCap;

                    var success = kind == CapacityUpgradeKind.MuleVault
                        ? $"[MULE] Upgrade #{n} purchased. Your account vault now holds up to {newCap:N0} entries."
                        : $"[MARKET] Upgrade #{n} purchased. Your account can now hold {newCap:N0} active listings.";

                    var baseMmd = PropertyManager.GetLong("capacity_upgrade_base_cost_mmd").Item;
                    var growthPct = PropertyManager.GetLong("capacity_upgrade_cost_growth_percent").Item;
                    var maxCount = spec.MaxCount();

                    if (CapacityUpgradePricing.TryQuote(n, baseMmd, growthPct, maxCount, out var nextMmd, out _))
                        success += $" Next upgrade: {nextMmd:N0} MMD.";

                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(success, ChatMessageType.System));

                    log.Info($"[CAPACITY UPGRADE] account={accountId} char={player.Name} kind={kind} number={n} mmd={originalQuote.Mmd} pyreals={originalQuote.Pyreals} balance={newBalance}");
                    break;
                }

                case CapacityUpgradePurchaseResult.InsufficientFunds:
                    Refuse(player, $"{spec.Prefix} Upgrade #{originalQuote.Owned + 1} costs {originalQuote.Mmd:N0} MMD ({originalQuote.Pyreals:N0} pyreals), and your bank holds {player.BankedPyreals:N0} pyreals. Use /bank deposit notes to bank the Trade Notes you are carrying.");
                    break;

                case CapacityUpgradePurchaseResult.PriceChanged:
                    Refuse(player, $"{spec.Prefix} The upgrade changed before you confirmed. Nothing was charged. Run the command again to see the current price.");
                    break;

                default:
                    Refuse(player, $"{spec.Prefix} The purchase could not be confirmed. Do not retry yet: relog, then run the command again to see whether it went through.");
                    break;
            }
        }

        private static void Refuse(Player player, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
        }
    }
}
