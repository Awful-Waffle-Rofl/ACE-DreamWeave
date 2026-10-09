using System;
using System.Numerics;

namespace ACE.Server.Entity.CapacityUpgrades
{
    /// <summary>
    /// Pricing for account capacity upgrades (/mule upgrade, /market upgrade). PURE: every input is a
    /// parameter and nothing here reads a setting, following the CustomAugmentations / CustomAugBroker
    /// split, because PropertyManager reads throw in unit tests on a cache miss and this math is exactly
    /// what the tests need to pin.
    ///
    /// The price of the purchase made when <c>n</c> upgrades of that kind are already owned is
    ///
    ///     ceil(baseMmd * (100 + growthPct)^n / 100^n)   MMD
    ///
    /// computed EXACTLY with BigInteger from the formula every time. It is never stepped from the
    /// previous price: rounding each step up and compounding the rounded value drifts upward, away from
    /// the formula's answer.
    /// </summary>
    public static class CapacityUpgradePricing
    {
        /// <summary>
        /// The most upgrades of one kind any account may ever own, whatever the configured maximum says.
        /// It also bounds the BigInteger work in <see cref="CostMmd"/>.
        /// </summary>
        public const int HardMaxPurchases = 100;

        /// <summary>Mule vault entries granted per /mule upgrade.</summary>
        public const int MuleEntriesPerUpgrade = 100;

        /// <summary>Maximum active market listings granted per /market upgrade.</summary>
        public const int ListingsPerUpgrade = 10;

        /// <summary>Pyreals per MMD (Massive Mana Charge) at the price conversion.</summary>
        public const long PyrealsPerMmd = 250_000;

        /// <summary>
        /// The MMD price of the next upgrade when <paramref name="owned"/> are already owned.
        ///
        /// Throws <see cref="ArgumentOutOfRangeException"/> for <paramref name="owned"/> outside
        /// [0, <see cref="HardMaxPurchases"/>], <paramref name="baseMmd"/> below 1 or
        /// <paramref name="growthPct"/> below 0, and <see cref="OverflowException"/> when the price does
        /// not fit a long. Callers that must not throw use <see cref="TryQuote"/>.
        /// </summary>
        public static long CostMmd(int owned, long baseMmd, long growthPct)
        {
            if (owned < 0 || owned > HardMaxPurchases)
                throw new ArgumentOutOfRangeException(nameof(owned), owned, $"owned must be in [0, {HardMaxPurchases}]");

            if (baseMmd < 1)
                throw new ArgumentOutOfRangeException(nameof(baseMmd), baseMmd, "baseMmd must be at least 1");

            if (growthPct < 0)
                throw new ArgumentOutOfRangeException(nameof(growthPct), growthPct, "growthPct must not be negative");

            var numerator = new BigInteger(baseMmd) * BigInteger.Pow(new BigInteger(100) + growthPct, owned);
            var denominator = BigInteger.Pow(new BigInteger(100), owned);

            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);

            if (!remainder.IsZero)
                quotient += BigInteger.One;

            if (quotient > long.MaxValue)
                throw new OverflowException($"the capacity upgrade price for owned {owned}, base {baseMmd}, growth {growthPct}% does not fit a long");

            return (long)quotient;
        }

        /// <summary>
        /// Converts an MMD price to pyreals with a checked multiply. FALSE when <paramref name="mmd"/> is
        /// not positive or the product overflows a long; <paramref name="pyreals"/> is 0 then.
        /// </summary>
        public static bool TryCostPyreals(long mmd, out long pyreals)
        {
            pyreals = 0;

            if (mmd <= 0)
                return false;

            try
            {
                pyreals = checked(mmd * PyrealsPerMmd);
                return true;
            }
            catch (OverflowException)
            {
                pyreals = 0;
                return false;
            }
        }

        /// <summary>
        /// The full price of the next upgrade in MMD and pyreals, or FALSE when it may not be sold:
        /// <paramref name="owned"/> is negative or has reached min(<paramref name="maxCount"/>,
        /// <see cref="HardMaxPurchases"/>), <paramref name="baseMmd"/> is below 1,
        /// <paramref name="growthPct"/> is negative, or either price overflows a long. Both outs are 0
        /// on FALSE.
        /// </summary>
        public static bool TryQuote(int owned, long baseMmd, long growthPct, long maxCount, out long mmd, out long pyreals)
        {
            mmd = 0;
            pyreals = 0;

            var limit = Math.Min(maxCount, HardMaxPurchases);

            if (owned < 0 || owned >= limit)
                return false;

            if (baseMmd < 1 || growthPct < 0)
                return false;

            long price;

            try
            {
                price = CostMmd(owned, baseMmd, growthPct);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (!TryCostPyreals(price, out var pyrealPrice))
                return false;

            mmd = price;
            pyreals = pyrealPrice;
            return true;
        }
    }
}
