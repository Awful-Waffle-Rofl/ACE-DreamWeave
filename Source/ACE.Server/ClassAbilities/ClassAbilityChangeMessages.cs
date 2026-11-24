namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The chat lines a player sees when one of their class ability ranks changes, in one place so every path that
    /// makes the change says it the same way. All of them go out on ChatMessageType.Broadcast, the channel the
    /// trainer, the vendor and /abilities already use (ACE.Entity/Enum/ChatMessageType.cs, Broadcast = 0x00, the
    /// default system-message channel). Pure, so the exact text is unit-tested (ClassAbilityChangeMessageTests).
    ///
    /// - <see cref="Learned"/>: the trainer's instant learn (ClassAbilityTrainer.HandleTrainer), the vendor's instant
    ///   learn (Player.TryLearnClassAbilityFromVendor) and the web panel's raise.
    /// - <see cref="Unlearned"/>: /abilities unlearn and the web panel's unlearn.
    /// - <see cref="Lowered"/>: the web panel's one-rank lower (no in-game command lowers a single rank), in the
    ///   unlearn line's style.
    /// </summary>
    public static class ClassAbilityChangeMessages
    {
        /// <summary>Appended to a line the web panel sends, so the player can tell where the change came from.</summary>
        public const string WebPanelMarker = " (web panel)";

        private static string Points(int n) => n == 1 ? "" : "s";

        /// <summary>"You learn Thorns rank 2. (5 class ability points left.)"</summary>
        public static string Learned(string displayName, int rank, int pointsLeft) =>
            $"You learn {displayName} rank {rank}. ({pointsLeft:N0} class ability point{Points(pointsLeft)} left.)";

        /// <summary>The /abilities points line: "Points: 5 available, 40 lifetime earned."</summary>
        public static string PointsSummary(int available, int lifetimeEarned) =>
            $"Points: {available:N0} available, {lifetimeEarned:N0} lifetime earned.";

        /// <summary>"You have unlearned Thorns and recovered 3 class ability points. Points: 8 available, 40 lifetime earned."</summary>
        public static string Unlearned(string displayName, int refunded, int available, int lifetimeEarned) =>
            $"You have unlearned {displayName} and recovered {refunded:N0} class ability point{Points(refunded)}. {PointsSummary(available, lifetimeEarned)}";

        /// <summary>"You have lowered Thorns to rank 1 and recovered 1 class ability point. Points: 6 available, 40 lifetime earned."</summary>
        public static string Lowered(string displayName, int rank, int refunded, int available, int lifetimeEarned) =>
            $"You have lowered {displayName} to rank {rank} and recovered {refunded:N0} class ability point{Points(refunded)}. {PointsSummary(available, lifetimeEarned)}";

        /// <summary>A line as the web panel sends it: the same text plus <see cref="WebPanelMarker"/>.</summary>
        public static string FromWebPanel(string line) => line + WebPanelMarker;
    }
}
