using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE, DreamWeave, 2026-09-05: pure catalog and pricing math for the three Custom Dreamweave
    /// Augmentations (uncapped mule vault space, pick-up speed and spell duration, bought from Fi / Bo /
    /// Nacci for Blank Augmentation Gems at a Fibonacci price). Kept as a pure, PropertyManager-free class
    /// - no PropertyManager reads, no WorldObject references, no DB access - because PropertyManager reads
    /// THROW in unit tests on a cache miss, which is exactly why PickupSpeed.cs was split out the same way.
    /// Tunable values (the cost-index ceiling, the gem wcid) are passed IN as arguments by callers.
    /// </summary>
    public static class CustomAugmentations
    {
        /// <summary>
        /// One catalog entry for a Custom Dreamweave Augmentation.
        /// </summary>
        public class CatalogEntry
        {
            /// <summary>
            /// The PropertyInt this augmentation's owned count is stored under.
            /// </summary>
            public PropertyInt Property { get; }

            /// <summary>
            /// The wcid of the reward gem this catalog entry's broker hands out.
            /// </summary>
            public uint RewardGemWcid { get; }

            /// <summary>
            /// TRUE when the count and the benefit are account-wide (summed across every character on the
            /// account) rather than per-character. Only MuleSpace is account-wide.
            /// </summary>
            public bool IsAccountWide { get; }

            /// <summary>
            /// Short display name for /augs and confirmation text.
            /// </summary>
            public string DisplayName { get; }

            /// <summary>
            /// One-line effect blurb for /augs.
            /// </summary>
            public string EffectBlurb { get; }

            /// <summary>
            /// What this entry's broker says when handed something that is not the currency, in that
            /// broker's own voice. Step 3 of <see cref="CustomAugBroker"/>.TryVerify is the ONLY message on
            /// the give path that varies per broker; every other one (the confirmation prompt, the short
            /// count refusal, the success line, the misconfiguration refusals) stays shared, because those
            /// carry live numbers or staff-facing instructions and a per-broker rewrite of them would be
            /// three copies of the same arithmetic to keep in sync.
            ///
            /// Must name the currency by its full item name: this is the message that tells a confused
            /// player what to bring, so flavour never replaces the instruction. A catalog test asserts it.
            /// </summary>
            public string WrongItemRefusal { get; }

            public CatalogEntry(PropertyInt property, uint rewardGemWcid, bool isAccountWide, string displayName, string effectBlurb, string wrongItemRefusal)
            {
                Property = property;
                RewardGemWcid = rewardGemWcid;
                IsAccountWide = isAccountWide;
                DisplayName = displayName;
                EffectBlurb = effectBlurb;
                WrongItemRefusal = wrongItemRefusal;
            }
        }

        /// <summary>
        /// The catalog, keyed by AugmentationType, covering exactly the three custom augmentations.
        /// </summary>
        public static readonly IReadOnlyDictionary<AugmentationType, CatalogEntry> Catalog = new Dictionary<AugmentationType, CatalogEntry>()
        {
            {
                AugmentationType.MuleSpace,
                new CatalogEntry(
                    PropertyInt.AugmentationMuleSpace,
                    1003753,
                    isAccountWide: true,
                    displayName: "Nacci's Vault Expansion",
                    effectBlurb: "+100 mule vault entries, account-wide",
                    wrongItemRefusal: "That does not appear in my ledger. Blank Augmentation Gems, nothing else - I keep no column for anything else.")
            },
            {
                AugmentationType.PickupSpeedCustom,
                new CatalogEntry(
                    PropertyInt.AugmentationPickupSpeed,
                    1003754,
                    isAccountWide: false,
                    displayName: "Bo's Quickened Grasp",
                    effectBlurb: "+10% pick-up speed",
                    wrongItemRefusal: "Wrong stone. Blank Augmentation Gems only - if it were one I would have had it off you already.")
            },
            {
                AugmentationType.SpellDurationCustom,
                new CatalogEntry(
                    PropertyInt.AugmentationSpellDurationCustom,
                    1003755,
                    isAccountWide: false,
                    displayName: "Fi's Lingering Casting",
                    effectBlurb: "+10% spell duration",
                    wrongItemRefusal: "That is not a Blank Augmentation Gem. I count only those, and I count carefully.")
            },
        };

        /// <summary>
        /// TRUE when the given AugmentationType is one of the three Custom Dreamweave Augmentations.
        /// </summary>
        public static bool IsCustom(AugmentationType type)
        {
            return Catalog.ContainsKey(type);
        }

        /// <summary>
        /// The retail spell-duration augmentation (AugmentationIncreasedSpellDuration) grants +20% per rank.
        /// Hardcoded because it is a retail value with no tunable, and because the three duration sites this
        /// feeds carried the literal 0.2f before this feature existed.
        /// </summary>
        public const float RetailSpellDurationPerAug = 0.2f;

        /// <summary>
        /// TRUE when a caster holds at least one spell-duration augmentation of EITHER kind, and therefore
        /// when the duration term applies at all.
        ///
        /// THIS PREDICATE IS THE FIX, AND IT IS THE WHOLE FIX. All three duration sites
        /// (EnchantmentManager.Add's refresh path, EnchantmentManager.BuildEntry and
        /// AddEnchantmentResult.BuildStack) previously guarded on AugmentationIncreasedSpellDuration &gt; 0
        /// alone, which SKIPS the entire term - so a player holding only the custom augmentation would have
        /// got nothing at all, not merely a smaller boost. It lives here, pure and named, rather than being
        /// spelled out three times, so a unit test can prove the OR half discriminates without standing up a
        /// live Player (no test in ACE.Server.Tests can construct one - see WeaponModHooksATests's remarks).
        ///
        /// It replaces ONLY the augmentation clause at each site. Every other clause each site carries -
        /// !isWeaponSpell everywhere, spell.DotDuration == 0 at the two EnchantmentManager sites, !equip at
        /// AddEnchantmentResult - is preserved verbatim, including the pre-existing asymmetry that
        /// AddEnchantmentResult has no DotDuration clause. That asymmetry is a real bug and is deliberately
        /// out of scope; do not harmonize it here.
        /// </summary>
        public static bool HasSpellDurationAug(int retailAugs, int customAugs)
        {
            return retailAugs > 0 || customAugs > 0;
        }

        /// <summary>
        /// The spell duration multiplier for a caster holding retailAugs retail spell-duration augmentations
        /// and customAugs Custom Dreamweave ones: 1 + retail*0.2 + custom*perCustomAug.
        ///
        /// ADDITIVE inside a single term, deliberately, not a second multiplicative factor:
        /// WeaponModId.Longevity already multiplies its own factor at these same three sites, and three
        /// stacked multipliers is where buff durations run away.
        ///
        /// Counts below zero are treated as zero, and perCustomAug that is non-finite or negative is treated
        /// as zero, so a corrupt property or a mis-set tunable can never SHORTEN a spell. The result is never
        /// below 1.0 for the same reason.
        /// </summary>
        public static float SpellDurationMultiplier(int retailAugs, int customAugs, double perCustomAug)
        {
            if (retailAugs < 0)
                retailAugs = 0;

            if (customAugs < 0)
                customAugs = 0;

            if (!double.IsFinite(perCustomAug) || perCustomAug < 0)
                perCustomAug = 0;

            var multiplier = 1.0 + retailAugs * RetailSpellDurationPerAug + customAugs * perCustomAug;

            if (multiplier < 1.0)
                multiplier = 1.0;

            return (float)multiplier;
        }

        /// <summary>
        /// The Fibonacci price of the NEXT augmentation of this type, given how many the buyer already owns.
        ///
        /// cost(0) = 1, cost(1) = 1, cost(2) = 2, cost(3) = 3, cost(4) = 5, ... (fib(owned + 1), 1-indexed
        /// so cost(0) and cost(1) are both 1).
        ///
        /// - owned: clamped to 0 if negative.
        /// - ceilingIndex: the index is clamped to [0, ceilingIndex] BEFORE computing, so a large owned
        ///   yields a flat plateau at the ceiling's price rather than an overflow.
        /// - Computed in long throughout; the final result is clamped to int.MaxValue. fib(47) already
        ///   exceeds int.MaxValue, so the clamp is a correctness requirement, not a design preference.
        /// </summary>
        public static long CostFor(int owned, int ceilingIndex)
        {
            if (owned < 0)
                owned = 0;

            if (ceilingIndex < 0)
                ceilingIndex = 0;

            var index = owned;
            if (index > ceilingIndex)
                index = ceilingIndex;

            var a = 1L; // fib(0) equivalent for this sequence: cost(0)
            var b = 1L; // cost(1)

            if (index == 0)
                return a;

            for (var i = 1; i < index; i++)
            {
                var next = a + b;
                a = b;
                b = next;

                if (b > int.MaxValue)
                    b = int.MaxValue;
            }

            return b;
        }
    }
}
