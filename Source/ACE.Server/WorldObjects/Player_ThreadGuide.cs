using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Thread-Guide per-character state (PropertyInt 9080-9082, PropertyBool 9073) and the one-time level-50
    /// notice. The flows that read and write this state are ThreadDungeons.ThreadGuideFlow and ThreadGuideStation.
    /// </summary>
    partial class Player
    {
        /// <summary>The highest Thread-Guide rung won, 0 when none. Absent reads as 0.</summary>
        public int ThreadGuideLevel
        {
            get => GetProperty(PropertyInt.ThreadGuideLevel) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.ThreadGuideLevel); else SetProperty(PropertyInt.ThreadGuideLevel, value); }
        }

        /// <summary>The current guide grant's serial, 0 before the first grant. Absent reads as 0.</summary>
        public int ThreadGuideGrantSerial
        {
            get => GetProperty(PropertyInt.ThreadGuideGrantSerial) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.ThreadGuideGrantSerial); else SetProperty(PropertyInt.ThreadGuideGrantSerial, value); }
        }

        /// <summary>Lifetime count of all Thread clears. Absent reads as 0.</summary>
        public int ThreadClearsLifetime
        {
            get => GetProperty(PropertyInt.ThreadClearsLifetime) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.ThreadClearsLifetime); else SetProperty(PropertyInt.ThreadClearsLifetime, value); }
        }

        /// <summary>Whether the one-time level-50 Thread-Guide notice has been shown. Absent means not yet.</summary>
        public bool ThreadGuideNoticeShown
        {
            get => GetProperty(PropertyBool.ThreadGuideNoticeShown) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ThreadGuideNoticeShown); else SetProperty(PropertyBool.ThreadGuideNoticeShown, value); }
        }

        /// <summary>
        /// The one-time notice telling a character who has reached level 50 where the Thread-Guide is. A copy of
        /// SendFacetUnlockNoticeIfDue's shape: a pure gate (ThreadGuideFlow.ShouldShowNotice: the guide enabled,
        /// the latch unset, level 50 or more), the latch set and saved BEFORE anything is sent, then a popup and a
        /// chat mirror. Called on level-up (CheckForLevelup) and on the login catch-up (Player_Networking).
        /// </summary>
        public void SendThreadGuideNoticeIfDue()
        {
            // Availability FIRST; the world-database weenie lookup is lazy and cached (ThreadGuideStation.NpcWeenieExists)
            // and is reached only by a character every other gate has already passed.
            if (!ThreadGuideStation.ReadAvailable())
                return;

            ThreadGuideFlow.SendNoticeIfDue(
                true,
                ThreadGuideStation.NpcWeenieExists,
                ThreadGuideNoticeShown,
                Level ?? 0,
                ThreadClearsLifetime,
                ThreadGuideLevel,
                !SurveyLevelRing.Parse(GetProperty(PropertyString.DungeonSurveyLevels)).IsEmpty,
                latch: () =>
                {
                    ThreadGuideNoticeShown = true;
                    SaveBiotaToDatabase();
                },
                send: () =>
                {
                    Session.Network.EnqueueSend(new GameEventPopupString(Session, ThreadGuideText.Notice));
                    Session.Network.EnqueueSend(new GameMessageSystemChat(ThreadGuideText.Notice, ChatMessageType.Advancement));
                });
        }

        /// <summary>
        /// Once per login: a player who has had a guide fragment before but now holds none and has no guide run
        /// open (an offline fail, or a regrant that met a full pack) is pointed back to the Thread-Guide.
        /// </summary>
        public void SendThreadGuideLoginReminderIfDue()
        {
            if (ThreadGuideGrantSerial <= 0 || ThreadGuideLevel >= ThreadGuideLadder.LastRung)
                return;

            var due = ThreadGuideFlow.ShouldSendLoginReminder(
                ThreadGuideStation.ReadAvailable(),
                ThreadGuideLevel,
                ThreadGuideGrantSerial,
                ThreadGuideFlow.HasOutstandingGuideItem(Inventory.Values, Guid.Full, ThreadGuideGrantSerial),
                ThreadGuideFlow.HasLiveGuideRun(ThreadDungeonManager.LiveRuns, Guid.Full));

            if (due)
                Session.Network.EnqueueSend(new GameMessageSystemChat(ThreadGuideText.LoginReminder, ChatMessageType.Broadcast));
        }
    }
}
