namespace ACE.Server.ThreadDungeons
{
    /// <summary>Why a Thread-Guide declined to issue a rung. Checked in this order by DecideGrant.</summary>
    public enum GuideRefusal
    {
        None,

        /// <summary>The guide feature is switched off.</summary>
        Disabled,

        /// <summary>The player is below <see cref="ThreadGuideRules.MinPlayerLevel"/>.</summary>
        BelowMinimumLevel,

        /// <summary>The player has already won the last rung (375).</summary>
        LadderComplete,

        /// <summary>The player still holds a current guide item.</summary>
        HoldsGuideItem,

        /// <summary>The player has a guide run that has not ended yet.</summary>
        GuideRunInProgress,
    }

    /// <summary>The outcome of <see cref="ThreadGuideRules.DecideGrant"/>: either a rung to issue or a refusal.</summary>
    public sealed class GuideGrantDecision
    {
        public bool Granted { get; }

        /// <summary>The rung to issue; 0 on a refusal.</summary>
        public int Rung { get; }

        /// <summary>Why it was refused; <see cref="GuideRefusal.None"/> on a grant.</summary>
        public GuideRefusal Reason { get; }

        private GuideGrantDecision(bool granted, int rung, GuideRefusal reason)
        {
            Granted = granted;
            Rung = rung;
            Reason = reason;
        }

        public static GuideGrantDecision Grant(int rung) => new GuideGrantDecision(true, rung, GuideRefusal.None);

        public static GuideGrantDecision Refuse(GuideRefusal reason) => new GuideGrantDecision(false, 0, reason);

        public override string ToString() => Granted ? "Grant(" + Rung + ")" : "Refuse(" + Reason + ")";
    }

    /// <summary>
    /// THE ONE PLACE Thread-Guide detection and issue rules live. Pure: every input is handed in, nothing
    /// is read from the engine, PropertyManager or the database. The Press station, the gem-use handler, the
    /// NPC and the run manager call these rather than testing DungeonGemSpec.Guide themselves, so the
    /// definition of "a guide item" and "the current guide item" cannot drift between them.
    /// </summary>
    public static class ThreadGuideRules
    {
        /// <summary>The lowest character level a Thread-Guide will issue to.</summary>
        public const int MinPlayerLevel = 50;

        /// <summary>True when the spec carries a Thread-Guide tag.</summary>
        public static bool IsGuide(DungeonGemSpec spec) => spec?.Guide != null;

        /// <summary>
        /// True when the spec is a guide item issued to <paramref name="holderGuid"/> AND its serial is the
        /// player's <paramref name="currentSerial"/>. A guide item failing this is either someone else's or a
        /// superseded copy from an earlier issue.
        /// </summary>
        public static bool IsCurrent(DungeonGemSpec spec, uint holderGuid, int currentSerial)
            => IsGuide(spec) && spec.Guide.OwnerGuid == holderGuid && spec.Guide.Serial == currentSerial;

        /// <summary>
        /// Whether to issue a rung, and which. One rule covers all three ways a player asks: the next rung
        /// after a win (highestWon has moved up), a re-grant after a failed run (highestWon has not moved,
        /// and the run is over), and a re-issue after the player destroyed their item (nothing outstanding,
        /// no run). In every case the answer is the next rung above highestWon, provided nothing is
        /// outstanding.
        ///
        /// Refusals are checked in a fixed order - disabled, below the minimum level, ladder complete, holds
        /// a guide item, guide run in progress - so the reason reported is the most fundamental one.
        /// </summary>
        public static GuideGrantDecision DecideGrant(bool enabled, int playerLevel, int highestWon,
            bool hasOutstandingGuideItem, bool hasLiveGuideRun)
        {
            if (!enabled) return GuideGrantDecision.Refuse(GuideRefusal.Disabled);
            if (playerLevel < MinPlayerLevel) return GuideGrantDecision.Refuse(GuideRefusal.BelowMinimumLevel);

            var next = ThreadGuideLadder.NextRung(highestWon);
            if (next == null) return GuideGrantDecision.Refuse(GuideRefusal.LadderComplete);

            if (hasOutstandingGuideItem) return GuideGrantDecision.Refuse(GuideRefusal.HoldsGuideItem);
            if (hasLiveGuideRun) return GuideGrantDecision.Refuse(GuideRefusal.GuideRunInProgress);

            return GuideGrantDecision.Grant(next.Value);
        }
    }
}
