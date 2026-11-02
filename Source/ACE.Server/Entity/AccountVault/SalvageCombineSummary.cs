namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// What one <see cref="AccountVaultStore.ConsolidatePartialSalvageOnQueue"/> pass did. Immutable.
    ///
    /// <see cref="Applied"/> is the only field a caller may act on: the counts describe the pass as it was
    /// PLANNED, so on a Refused or Failed exchange they are non-zero while nothing (or nothing provable)
    /// moved. The player message is therefore gated on Applied, never on the counts alone.
    /// </summary>
    public sealed class SalvageCombineSummary
    {
        /// <summary>The no-op answer: a gate stopped the pass, nothing qualified, or the pass threw.</summary>
        public static readonly SalvageCombineSummary None = new SalvageCombineSummary(0, 0, 0, 0, 0, false);

        /// <summary>Partial bags consumed across every group the pass acted on.</summary>
        public int BagsIn { get; }

        /// <summary>Full bags produced.</summary>
        public int FullOut { get; }

        /// <summary>Partial bags produced (at most one per group).</summary>
        public int PartialOut { get; }

        /// <summary>Value lost to the per-bag cap (salvage_bag_value_cap) across the pass.</summary>
        public long ValueCappedOff { get; }

        /// <summary>Value lost to integer rounding of split fragments across the pass.</summary>
        public long ValueRoundingDelta { get; }

        /// <summary>True only when the class exchange reported Applied.</summary>
        public bool Applied { get; }

        public SalvageCombineSummary(int bagsIn, int fullOut, int partialOut, long valueCappedOff, long valueRoundingDelta, bool applied)
        {
            BagsIn = bagsIn;
            FullOut = fullOut;
            PartialOut = partialOut;
            ValueCappedOff = valueCappedOff;
            ValueRoundingDelta = valueRoundingDelta;
            Applied = applied;
        }

        /// <summary>The same counts with <see cref="Applied"/> set as given.</summary>
        internal SalvageCombineSummary WithApplied(bool applied)
            => new SalvageCombineSummary(BagsIn, FullOut, PartialOut, ValueCappedOff, ValueRoundingDelta, applied);

        /// <summary>
        /// The depositor's chat line for this pass, or null when there is nothing to tell them (not
        /// Applied, or no bags went in).
        /// </summary>
        public string PlayerMessage => Applied && BagsIn > 0 ? FormatPlayerMessage(BagsIn, FullOut, PartialOut) : null;

        /// <summary>
        /// Pure formatter for the depositor's chat line. Null when <paramref name="bagsIn"/> is not positive.
        ///
        /// "Your mule combined {bagsIn} partial salvage bags into {fullOut} full bag(s)[ and N partial bag(s)]."
        /// and, when nothing came out full, "Your mule combined {bagsIn} partial salvage bags into
        /// {partialOut} partial bag(s)." bagsIn is never singular in practice (a group needs two bags), so
        /// "bags" is not pluralised on it.
        /// </summary>
        public static string FormatPlayerMessage(int bagsIn, int fullOut, int partialOut)
        {
            if (bagsIn <= 0)
                return null;

            if (fullOut <= 0)
                return $"Your mule combined {bagsIn} partial salvage bags into {partialOut} partial bag{(partialOut == 1 ? "" : "s")}.";

            var tail = partialOut <= 0
                ? ""
                : partialOut == 1 ? " and 1 partial bag" : $" and {partialOut} partial bags";

            return $"Your mule combined {bagsIn} partial salvage bags into {fullOut} full bag{(fullOut == 1 ? "" : "s")}{tail}.";
        }
    }
}
