namespace ACE.Server.Pinnacles
{
    /// <summary>What a Knot use does about the owner's existing fight (if any).</summary>
    public enum PinnacleEntryAction
    {
        /// <summary>No usable fight: build a fresh copy and a fresh fight.</summary>
        CreateCopy,

        /// <summary>A fight that has not begun, in a copy still loaded: walk back in at the arrival point.</summary>
        ReuseArrival,

        /// <summary>
        /// A live attempt of this Knot that is still being fought (running or held): re-enter at the fight entry point.
        /// The return spends one Knot entry (owner ruling R1, 2026-10-19).
        /// </summary>
        ResumeHeld,

        /// <summary>
        /// A WON attempt of this Knot (its cache owed or being collected): re-enter FREE at the fight entry point
        /// (owner ruling 2026-10-19: returning to collect after a win is always free).
        /// </summary>
        ReturnToCollect,

        /// <summary>Not allowed; the refusal line says why.</summary>
        Refuse,
    }

    /// <summary>The pure routing of a Pinnacle entry. No world, no clock.</summary>
    public static class PinnacleEntryRules
    {
        public const string OtherKnotRefusal = "Another trial is still open for you. Finish or lose it before you pull a different Knot.";

        /// <summary>
        /// <paramref name="fightExists"/>: the owner has a registered fight that has not ended. <paramref name="copyLive"/>:
        /// that fight's ephemeral copy is still the registered, loaded instance. <paramref name="sameKnot"/>: the Knot
        /// being used is the fight's Knot. <paramref name="awaiting"/> / <paramref name="collecting"/>: the two stages of
        /// a Won fight (PinnacleFight.AwaitingDelivery / Collecting).
        ///   - no fight, an ended fight (Lost, or Won and neither awaiting nor collecting) or a dead copy: CreateCopy;
        ///   - NotStarted in a live copy: ReuseArrival for the same Knot; for a different Knot CreateCopy (the caller ends the
        ///     uncharged old fight first: its head talk would charge the old Knot, never this one);
        ///   - Running or Held in a live copy: ResumeHeld for the same Knot, Refuse for another;
        ///   - Won (awaiting or collecting) in a live copy: ReturnToCollect for the same Knot, Refuse for another.
        /// </summary>
        public static PinnacleEntryAction Decide(bool fightExists, PinnacleFightState s, bool awaiting, bool collecting, bool copyLive, bool sameKnot, out string refusal)
        {
            refusal = null;

            if (!fightExists || !copyLive)
                return PinnacleEntryAction.CreateCopy;

            switch (s)
            {
                case PinnacleFightState.Won when collecting || awaiting:
                    if (sameKnot)
                        return PinnacleEntryAction.ReturnToCollect;
                    break;

                case PinnacleFightState.Won when !awaiting:
                case PinnacleFightState.Lost:
                    return PinnacleEntryAction.CreateCopy;

                case PinnacleFightState.NotStarted:
                    return sameKnot ? PinnacleEntryAction.ReuseArrival : PinnacleEntryAction.CreateCopy;

                default:
                    if (sameKnot)
                        return PinnacleEntryAction.ResumeHeld;
                    break;
            }

            refusal = OtherKnotRefusal;
            return PinnacleEntryAction.Refuse;
        }
    }
}
