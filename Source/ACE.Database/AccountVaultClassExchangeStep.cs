namespace ACE.Database
{
    /// <summary>
    /// One row's part in an atomic multi-row class exchange (see
    /// <see cref="ShardDatabase.TryExchangeAccountVaultClasses"/>): the state the caller believes the
    /// row is in, the signed deltas to apply to it, and the seed that creates it if it does not exist.
    ///
    /// THE EXPECTED PRE-STATE IS THE WHOLE CONTRACT. The exchange applies only if every row is at
    /// exactly (<see cref="ExpectedCount"/>, <see cref="ExpectedTotal"/>), and it recognises a retry
    /// of itself only if every row is at exactly expected + delta. A row that does not exist reads as
    /// (0, 0) for both comparisons, so a step that creates a row says so with an expected pre-state of
    /// 0/0, and a step that drains a row to nothing has a post-state of 0/0 that matches whether the
    /// emptied row is still there or has already been reaped.
    ///
    /// <see cref="Seed"/> is REQUIRED whenever the expected pre-state is 0/0, because only then can
    /// the row be absent and need creating; it is never read otherwise. It follows the same rule as
    /// every other class seed: its canonical form must hash to <see cref="ClassKey"/>.
    ///
    /// Immutable by construction: the exchange validates a step once, before any statement, and a
    /// step that could change afterwards would make that validation meaningless.
    /// </summary>
    public sealed class AccountVaultClassExchangeStep
    {
        public AccountVaultClassExchangeStep(string classKey, long expectedCount, long expectedTotal, long countDelta, long valueDelta,
                                             AccountVaultClassSeed seed = null)
        {
            ClassKey = classKey;
            ExpectedCount = expectedCount;
            ExpectedTotal = expectedTotal;
            CountDelta = countDelta;
            ValueDelta = valueDelta;
            Seed = seed;
        }

        /// <summary>VaultItemClass.ClassKey: 32 lowercase hex characters.</summary>
        public string ClassKey { get; }

        /// <summary>The ITEM count the row must hold before the exchange. 0 for a row that must not exist yet.</summary>
        public long ExpectedCount { get; }

        /// <summary>The pooled total_Value the row must hold before the exchange. 0 for a row that must not exist yet.</summary>
        public long ExpectedTotal { get; }

        /// <summary>Signed ITEM-count delta. Never a unit count.</summary>
        public long CountDelta { get; }

        /// <summary>Signed pooled-value delta.</summary>
        public long ValueDelta { get; }

        /// <summary>Creates the row if absent. Required when the expected pre-state is 0/0, ignored otherwise.</summary>
        public AccountVaultClassSeed Seed { get; }
    }
}
