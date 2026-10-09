namespace ACE.Server.Pvp
{
    /// <summary>
    /// How a formed <see cref="MatchProposal"/> decides whether to proceed once accept replies are in.
    /// Docs/Pvp/DESIGN.md "Accept (FFA)": 1v1/2v2 are AllOrNothing (a single decline/timeout cancels the
    /// whole proposal); FFA is ProceedIfAtLeast(currentMinimum) (declines/timeouts just shrink the lobby,
    /// as long as the mode's live minimum is still met).
    /// </summary>
    public abstract record AcceptPolicy
    {
        public sealed record AllOrNothingPolicy : AcceptPolicy;

        public sealed record ProceedIfAtLeastPolicy(int CurrentMinimum) : AcceptPolicy;

        public static readonly AcceptPolicy AllOrNothing = new AllOrNothingPolicy();

        public static AcceptPolicy ProceedIfAtLeast(int currentMinimum) => new ProceedIfAtLeastPolicy(currentMinimum);

        /// <summary>
        /// Whether the proposal should proceed given how many of the expected participants actually
        /// arrived (accepted in time). AllOrNothing needs every one of them; ProceedIfAtLeast needs only
        /// its own CurrentMinimum, regardless of how many were originally proposed.
        /// </summary>
        public bool Proceeds(int arrivedCount, int expectedCount)
        {
            return this switch
            {
                AllOrNothingPolicy => arrivedCount >= expectedCount,
                ProceedIfAtLeastPolicy p => arrivedCount >= p.CurrentMinimum,
                _ => false
            };
        }
    }
}
