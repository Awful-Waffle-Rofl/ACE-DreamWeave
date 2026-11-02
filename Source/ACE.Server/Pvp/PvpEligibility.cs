using System;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Who may join a PvP Arena or Battleground, and when they are told so. Owner ruling 2026-10-15: one Thread clear
    /// replaces the level and PK facet requirements. The admission preflight (PvpAdmission, through
    /// PvpPlayerFacts.HasClearedThread) and the one-time eligibility notice (Player.SendPvpEligibleNoticeIfDue) both
    /// call <see cref="HasClearedAThread"/>, so a player is told they can join exactly when the join stops refusing them.
    /// </summary>
    public static class PvpEligibility
    {
        /// <summary>The one-time popup and chat line sent the moment a character becomes eligible.</summary>
        public const string NoticeText = "You have completed a Thread and are now eligible to join PvP Arenas and Battlegrounds! Visit the PK room on the lower level of the Marketplace for more details.";

        /// <summary>
        /// Whether the character has completed at least one Thread. The same three signals ThreadGuideFlow.ShouldShowNotice
        /// treats as a veteran: a lifetime clear count above zero (ThreadClearsLifetime is new, so a veteran who cleared
        /// before it shipped reads 0), a Thread-Guide rung won (the level-50 intro Thread counts), or a survey ever filed.
        /// </summary>
        public static bool HasClearedAThread(int clearsLifetime, int guideLevel, bool hasSurveyHistory)
            => clearsLifetime > 0 || guideLevel > 0 || hasSurveyHistory;

        /// <summary>
        /// The notice gate: the arena master switch is on (pvp_arena_enabled), the latch is unset, and the character is
        /// eligible. With the arena off the notice is neither latched nor sent, so it fires once the arena opens.
        /// </summary>
        public static bool ShouldSendNotice(bool arenaEnabled, bool noticeAlreadyShown, bool hasClearedThread)
            => arenaEnabled && !noticeAlreadyShown && hasClearedThread;

        /// <summary>
        /// The notice, the Thread-Guide notice's shape: when <see cref="ShouldSendNotice"/> says so, <paramref name="latch"/>
        /// (set and save the latch) runs BEFORE <paramref name="send"/>; when it does not, NEITHER runs. True when it fired.
        /// </summary>
        public static bool SendNoticeIfDue(bool arenaEnabled, bool noticeAlreadyShown, bool hasClearedThread, Action latch, Action send)
        {
            if (!ShouldSendNotice(arenaEnabled, noticeAlreadyShown, hasClearedThread))
                return false;

            latch?.Invoke();
            send?.Invoke();
            return true;
        }
    }
}
