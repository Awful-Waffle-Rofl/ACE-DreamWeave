namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Why one fold pass stopped.
    ///
    /// THE POINT OF THIS ENUM IS THAT "ZERO FOLDED" IS NOT AN ANSWER. Six different states produce a
    /// pass that folds nothing, and only one of them means the account is migrated. A caller that reads
    /// zero as done reports a migration complete over a vault it never touched, which is what happened
    /// on stage before this existed.
    /// </summary>
    public enum VaultFoldBatchTerminal
    {
        /// <summary>
        /// The store may still have foldable items. FIRST so that it is default(T): a terminal a future
        /// edit forgets to set then errs towards a wasted pass rather than towards a false completion.
        /// </summary>
        MoreToDo,

        /// <summary>
        /// The store has latched AccountVaultStore.FoldHasNothingLeft: every stored biota it can see has
        /// either folded or been refused once. The ONLY terminal that means an account is migrated.
        /// </summary>
        Exhausted,

        /// <summary>
        /// The store is registered but cold, so the pass read nothing rather than loading a vault it was
        /// not asked to load. Says nothing at all about whether the account has foldable items.
        /// </summary>
        NotWarm,

        /// <summary>account_vault_class_storage is off. The tier is stopped, not finished.</summary>
        StorageDisabled,

        /// <summary>The pass was given a budget of zero or less, so it never looked at anything.</summary>
        BudgetZero,

        /// <summary>
        /// The class ledger refused a credit or could not be reached, and the pass stopped rather than
        /// multiplying one database problem by the budget. The item it stopped on was put back.
        /// </summary>
        Aborted,
    }

    /// <summary>
    /// What one call to AccountVaultStore.FoldSomeStoredItemsOnQueue(double, int, out VaultFoldBatchResult)
    /// did, and why it stopped.
    ///
    /// A CLASS RATHER THAN A STRUCT on purpose: the pass fills it in as it goes, including from inside
    /// the finally that reports the timing, and the caller reaches it through a local captured by the
    /// lambda handed to AccountVaultStore.Enqueue. A struct would be copied at the capture and the
    /// counters would arrive back at zero.
    ///
    /// The four refusal counters are separated because they mean different things to whoever is reading
    /// a migration report: <see cref="RefusedPredicate"/> is the ordinary, expected majority of any real
    /// vault (anything that is not interchangeable salvage), while <see cref="RefusedRoundTrip"/> points
    /// at content that cannot be rebuilt and <see cref="RefusedTakeOut"/> at a store that could not give
    /// an item up. Summed into one number they would be indistinguishable, and only one of the three is
    /// worth waking anybody for.
    /// </summary>
    public sealed class VaultFoldBatchResult
    {
        /// <summary>Candidates this pass actually looked at. Counted in the loop, so an aborted pass reports how far it got rather than the whole budget.</summary>
        public int Examined;

        /// <summary>Candidates that collapsed into a class row and had their biota destroyed.</summary>
        public int Folded;

        /// <summary>Candidates the class predicate refused: not classifiable. Remembered, never retried.</summary>
        public int RefusedPredicate;

        /// <summary>Candidates whose class could not be rebuilt into the same class. Remembered, never retried.</summary>
        public int RefusedRoundTrip;

        /// <summary>Candidates that could not be taken out of their vault. Remembered, never retried.</summary>
        public int RefusedTakeOut;

        /// <summary>Candidates that are foldable but belong to a class this pass is not draining. NOT refused: nothing was touched and they fold on a later pass.</summary>
        public int WrongClass;

        /// <summary>
        /// The candidate the pass ABORTED on: the class ledger refused its credit or could not be
        /// reached, so the item was put back and the pass stopped. At most one per pass, by construction.
        ///
        /// It exists because the outcome buckets are meant to ACCOUNT FOR <see cref="Examined"/>, and
        /// without it they did not: the aborting candidate was counted as examined and appeared in no
        /// bucket at all, so an operator reading a report saw Examined exceed the sum of every other
        /// field by exactly one, with nothing naming the difference. A missing row in a reconciliation is
        /// worse than a large one, because there is no number to go and look at.
        /// </summary>
        public int Aborted;

        /// <summary>
        /// Every candidate this pass looked at lands in exactly one bucket, so this must always equal
        /// <see cref="Examined"/>. Exposed rather than left implicit so a test can assert the identity
        /// and a future outcome added to the switch cannot go uncounted silently.
        /// </summary>
        public int Accounted => Folded + RefusedPredicate + RefusedRoundTrip + RefusedTakeOut + WrongClass + Aborted;

        /// <summary>Why the pass stopped. Set exactly once on every path.</summary>
        public VaultFoldBatchTerminal Terminal;
    }
}
