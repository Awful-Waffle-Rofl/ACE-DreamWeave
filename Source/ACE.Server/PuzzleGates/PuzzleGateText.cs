namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// Every player-facing line of the puzzle gate. By invariant no line here names a SPECIFIC colour, a
    /// slot or a lever position: the chat channel must never leak the answer. The generic word "color" is
    /// fine. ASCII only.
    /// </summary>
    public static class PuzzleGateText
    {
        // ---- approach prompts: one line when a player first comes near (approved text, 2026-10-06) ----

        public const string PromptSigil = "A ward bars the way. Match the beam color.";

        public const string PromptBeamIndicated = "A ward bars the way. Follow the beam that matches the gate's color to its lever.";

        public const string PromptBeamSingle = "A ward bars the way. Follow the beam to its lever.";

        public const string PromptOdd = "A ward bars the way. Pick the lever that is different.";

        public const string PromptShuffle = "A ward bars the way. Find the lever.";

        /// <summary>The approach prompt for a placement. A single-beam placement has no indicator to match.</summary>
        public static string PromptFor(PuzzleGateType type, int beams)
        {
            switch (type)
            {
                case PuzzleGateType.Sigil: return PromptSigil;
                case PuzzleGateType.Beam: return beams >= 2 ? PromptBeamIndicated : PromptBeamSingle;
                case PuzzleGateType.Odd: return PromptOdd;
                default: return PromptShuffle;
            }
        }

        public const string Locked = "The mechanism is still settling. Wait a moment.";

        public const string WrongAnswer = "The mechanism shudders and resets.";

        public const string RoundAdvanced = "Something clicks into place, and the mechanism rearranges itself.";

        public const string Solved = "The gate grinds open.";

        public const string AlreadySolved = "The mechanism is spent. The gate stands open.";

        // ---- gate models and the Thread reward scene (placeholder wording, pending owner review) ----

        /// <summary>A solve that DESTROYS the gate (a barrier, or a door with no open animation).</summary>
        public const string SolvedWard = "The ward shatters.";

        /// <summary>A solve of the reward scene's focal object.</summary>
        public const string SolvedSeal = "The seal breaks.";

        /// <summary>A reward-scene lever pulled before the run's kills are done: refused, no penalty.</summary>
        public const string RewardNotArmed = "The seal will not answer while this place is still defended.";

        /// <summary>Told once to a run's recipients when its kills are done but the reward is still sealed.</summary>
        public const string RewardArmed = "The dungeon falls quiet, but its reward is sealed. Break the seal to claim it.";

        /// <summary>The armed line when the reward scene formed within a few metres of the member.</summary>
        public const string RewardArmedBeside = RewardArmed + " It forms beside you.";

        /// <summary>{where} when the scene is straight overhead (approved text, 2026-10-07).</summary>
        public const string RewardArmedDirectlyAbove = "directly above you";

        /// <summary>{where} when the scene is straight underfoot.</summary>
        public const string RewardArmedDirectlyBelow = "directly below you";

        /// <summary>
        /// {where} when the scene is under 2 m away horizontally and under 3 m vertically yet more than 5 m on foot (the
        /// far side of a wall): "It lies nearby, about {n} m away on foot." Approved 2026-10-07.
        /// </summary>
        public const string RewardArmedNearby = "nearby";

        /// <summary>Follows the metres when they are a walking distance.</summary>
        public const string RewardArmedOnFoot = " on foot";

        /// <summary>The closing clause when the scene stands in the boss's room.</summary>
        public const string RewardArmedBossChamber = ", in the boss's chamber";

        /// <summary>{where} for an 8-point compass word: "to the northeast".</summary>
        public static string RewardArmedCompass(string direction) => "to the " + direction;

        /// <summary>The vertical clause: ", 12 m below you".</summary>
        public static string RewardArmedVertical(int metres, bool above)
            => $", {metres.ToString(System.Globalization.CultureInfo.InvariantCulture)} m {(above ? "above" : "below")} you";

        /// <summary>
        /// The armed line pointing a member at the reward scene (approved text, 2026-10-07):
        /// "{base} It lies {where}, about {n} m away[ on foot][{vertical}][, in the boss's chamber]."
        /// </summary>
        public static string RewardArmedToward(string where, int metres, bool onFoot, string vertical, bool bossChamber)
            => $"{RewardArmed} It lies {where}, about {metres.ToString(System.Globalization.CultureInfo.InvariantCulture)} m away{(onFoot ? RewardArmedOnFoot : "")}{vertical ?? ""}{(bossChamber ? RewardArmedBossChamber : "")}.";

        /// <summary>The solve line for a gate model: the door line for an opened door (unchanged admin text), otherwise the ward or seal line.</summary>
        public static string SolvedFor(PuzzleGateForm form, PuzzleGateSolveAction action)
        {
            if (form == PuzzleGateForm.Focal)
                return SolvedSeal;

            return action == PuzzleGateSolveAction.Open ? Solved : SolvedWard;
        }

        // ---- Thread puzzle fail policy (approved text, 2026-10-06; {n} a count, {t} a FormatDuration) ----

        /// <summary>Told on every counted (scored) wrong pull that did not reach the threshold.</summary>
        public static string FailWarning(int remaining)
            => remaining == 1
                ? "The ward recoils. 1 more failed attempt and you will be cast out of the Threads."
                : $"The ward recoils. {remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)} more failed attempts and you will be cast out of the Threads.";

        /// <summary>Told to each character a lockout removes from a run.</summary>
        public static string LockedOut(System.TimeSpan remaining)
            => $"The ward rejects you. You are barred from the Threads for {FormatDuration(remaining)}.";

        /// <summary>The refusal at gem use, group start and re-entry while a lockout runs.</summary>
        public static string Barred(System.TimeSpan remaining)
            => $"You are barred from the Threads for another {FormatDuration(remaining)}.";

        /// <summary>
        /// A group member the policy removed tries to re-enter the same run after their lockout has run out.
        /// Approved 2026-10-06. Reachable whenever the lockout is shorter than the run (the defaults are 120 and 180 minutes).
        /// </summary>
        public const string CastOutOfRun = "You were cast out of this Thread and cannot return to it.";

        /// <summary>
        /// "1h 54m", "2h", "45m". Rounded UP to the whole minute, so a lockout with seconds left never reads as
        /// over ("0m"); anything at or below zero reads "1m" for the same reason. No days unit: hours just grow.
        /// </summary>
        public static string FormatDuration(System.TimeSpan span)
        {
            var minutes = (long)System.Math.Ceiling(span.TotalMinutes);
            if (minutes < 1)
                minutes = 1;

            var hours = minutes / 60;
            var rest = minutes % 60;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            if (hours == 0)
                return $"{rest.ToString(inv)}m";

            return rest == 0 ? $"{hours.ToString(inv)}h" : $"{hours.ToString(inv)}h {rest.ToString(inv)}m";
        }
    }
}