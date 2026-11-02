using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// PvP Arena and Battleground eligibility (owner ruling 2026-10-15: one Thread clear replaces the level and PK facet
    /// requirements) and its one-time notice (PropertyBool 9077).
    /// </summary>
    partial class Player
    {
        /// <summary>Whether the one-time PvP eligibility notice has been shown. Absent means not yet.</summary>
        public bool PvpEligibleNoticeShown
        {
            get => GetProperty(PropertyBool.PvpEligibleNoticeShown) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.PvpEligibleNoticeShown); else SetProperty(PropertyBool.PvpEligibleNoticeShown, value); }
        }

        /// <summary>
        /// Whether this character has completed at least one Thread (PvpEligibility.HasClearedAThread over the lifetime
        /// clear count, the Thread-Guide rung and the survey history). Read by the arena admission facts and by the notice.
        /// </summary>
        public bool HasClearedAThread => PvpEligibility.HasClearedAThread(
            ThreadClearsLifetime,
            ThreadGuideLevel,
            !SurveyLevelRing.Parse(GetProperty(PropertyString.DungeonSurveyLevels)).IsEmpty);

        /// <summary>
        /// The one-time notice telling a character who has completed a Thread that they can join PvP Arenas and
        /// Battlegrounds. A copy of SendThreadGuideNoticeIfDue's shape: a pure gate (PvpEligibility.ShouldSendNotice: the
        /// arena on, the latch unset, a Thread cleared), the latch set and saved BEFORE anything is sent, then a popup and
        /// a chat mirror. With the arena off neither the latch nor the send happens, so it fires after the arena opens.
        /// Called right after a Thread clear is counted (ThreadGuideStation.IncrementClears) and on the login catch-up
        /// (Player_Networking), which covers a veteran who cleared before ThreadClearsLifetime existed.
        /// </summary>
        public void SendPvpEligibleNoticeIfDue()
        {
            // Cheap per-player gates first, so the (settings-reading) arena dial is only touched for a character who could
            // actually be told. Then an unsendable notice (no session) is never latched, so it is not spent.
            if (PvpEligibleNoticeShown || !HasClearedAThread)
                return;

            if (Session?.Network == null)
                return;

            PvpEligibility.SendNoticeIfDue(
                PvpTunables.DialSource().Enabled,
                PvpEligibleNoticeShown,
                HasClearedAThread,
                latch: () =>
                {
                    PvpEligibleNoticeShown = true;
                    SaveBiotaToDatabase();
                },
                send: () =>
                {
                    Session.Network.EnqueueSend(new GameEventPopupString(Session, PvpEligibility.NoticeText));
                    Session.Network.EnqueueSend(new GameMessageSystemChat(PvpEligibility.NoticeText, ChatMessageType.Advancement));
                });
        }
    }
}
