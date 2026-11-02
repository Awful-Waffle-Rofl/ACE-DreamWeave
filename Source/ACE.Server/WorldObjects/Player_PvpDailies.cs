using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The player side of the PvP dailies (Pvp/PvpDailyCatalog.cs): one eligible seat's match facts, applied on this player's own action
    /// chain by <see cref="PvpDailyProgress"/>. The coordinator builds the facts once in Resolve and the live gateway queues this exactly
    /// once per match. An offline player is skipped by the gateway: daily progress has no offline path. The one exception is a Live 1v1
    /// forfeit, whose GladiatorsStreak reset is pended on the stored character and applied at the next login (PvpDailyForfeitDispatcher).
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Applies <paramref name="facts"/> to this character. Runs inside the PvP template system scope: the facts are the match's own
        /// result, written by the server, and a player who is still templated when they arrive (the post-match window) would otherwise
        /// have every quest write silently refused by the template progression lock in QuestManager.SetQuestCompletions.
        /// </summary>
        public void ApplyPvpDailyFacts(PvpMatchFacts facts)
        {
            if (facts == null || facts.CharacterId != Guid.Full)
                return;

            PvpDailyProgressResult changes;

            // TEMPLATE PROGRESSION LOCK BYPASS, scoped to this one call and nothing else (accepted on #1667). The facts arrive while the
            // player is usually still templated (Resolve runs before the post-match exit restores them), and the progression lock
            // (PvpTemplateGate.Decide, PvpTemplateAction.QuestStamp) makes QuestManager.SetQuestCompletions return silently for a templated
            // player. The lock exists to stop a templated character progressing through its own actions; this write is the server
            // recording the match result, which is the whole point of a PvP daily. Do not widen this scope.
            using (BeginPvpTemplateSystemOperation())
                changes = PvpDailyProgress.Apply(new PvpDailyPlayerTarget(this), facts);

            // The moved lines to send: a task on turn-in cooldown is left out (its giver refuses it, so "Return to X" would be false);
            // its status line below carries the progress. The sent set is also what the status skips.
            var sent = PvpDailyCatalog.MatchLines(changes.Moved, new PvpDailyStatusSource(this));

            if (changes.Count > 0)
            {
                log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): match {facts.MatchId} daily progress: {string.Join("; ", changes.Log)}");

                // One [Arena] line per counter that advanced, in the channel every other arena line uses.
                foreach (var line in sent)
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

                RushNextPlayerSave(5);
            }

            // Then where the player stands on every OTHER daily they hold (or have on turn-in cooldown), sent even when nothing moved this
            // match. The moved lines above are kept as the "this match" information and the status skips them, so no line is printed twice.
            // Sent from here (not from ExitPvpMatchNow) because an eliminated FFA player leaves before Resolve; this runs once per match.
            SendPvpDailyStatus(null, sent);
        }

        /// <summary>
        /// Sends this player's daily standing (<see cref="PvpDailyCatalog.StatusLines"/>) as [Arena] chat lines. <paramref name="giver"/>
        /// limits it to one NPC's tasks; <paramref name="alreadyShown"/> is the post-match form (see StatusLines).
        /// </summary>
        public void SendPvpDailyStatus(string giver = null, System.Collections.Generic.IReadOnlyCollection<string> alreadyShown = null)
        {
            if (Session == null)
                return;

            foreach (var line in PvpDailyCatalog.StatusLines(new PvpDailyStatusSource(this), giver, alreadyShown))
                Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        /// <summary>The live <see cref="IPvpDailyStatusSource"/>: read-only quest registry lookups.</summary>
        private sealed class PvpDailyStatusSource : IPvpDailyStatusSource
        {
            private readonly Player player;

            public PvpDailyStatusSource(Player player)
            {
                this.player = player;
            }

            public bool HasQuest(string questName) => player.QuestManager.HasQuest(questName);

            public int GetSolves(string questName) => player.QuestManager.GetCurrentSolves(questName);

            public TimeSpan? CooldownRemaining(string questName)
            {
                if (!player.QuestManager.HasQuest(questName))
                    return null;

                // MinValue: can solve now (timer ran out); MaxValue: max solves reached or no world row. Neither is a countdown.
                return PvpDailyCatalog.ToCountdown(player.QuestManager.GetNextSolveTime(questName));
            }
        }

        /// <summary>Seconds after entering the world before a pending GladiatorsStreak reset is applied (the owed-Blood delay).</summary>
        private const float PendingPvpStreakResetLoginDelaySeconds = 5.0f;

        /// <summary>
        /// Login (WorldManager): when a Live 1v1 forfeit happened while this character was offline or logging out, the forfeit dispatcher
        /// left <see cref="PvpDailyProgress.PendingStreakResetMarker"/> in the streak state. Queues its application on this player's chain:
        /// the marker is taken and a forfeit fact goes through <see cref="ApplyPvpDailyFacts"/>, the same path an online forfeiter takes,
        /// so the streak resets to 0 if the quest is held and not yet complete. A no-op when nothing is pending.
        /// </summary>
        public void ScheduleApplyPendingPvpStreakReset()
        {
            if (GetProperty(PropertyString.PvpDailyGladiatorsStreakState) != PvpDailyProgress.PendingStreakResetMarker)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(PendingPvpStreakResetLoginDelaySeconds);
            chain.AddAction(this, () =>
            {
                try
                {
                    ApplyPendingPvpStreakReset();
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): applying the pending GladiatorsStreak reset threw", ex);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>Takes a pending GladiatorsStreak reset, if any, and applies it as a forfeit fact. On this player's own action chain.</summary>
        public void ApplyPendingPvpStreakReset()
        {
            if (!PvpDailyProgress.ApplyPendingStreakReset(new PvpDailyPlayerTarget(this), Guid.Full, ApplyPvpDailyFacts))
                return;

            RushNextPlayerSave(5);
        }

        /// <summary>The live <see cref="IPvpDailyTarget"/>: the quest registry through QuestManager, the state and stamps through reserved properties.</summary>
        private sealed class PvpDailyPlayerTarget : IPvpDailyTarget
        {
            private readonly Player player;

            public PvpDailyPlayerTarget(Player player)
            {
                this.player = player;
            }

            public bool HasQuest(string questName) => player.QuestManager.HasQuest(questName);

            public int GetSolves(string questName) => player.QuestManager.GetCurrentSolves(questName);

            /// <summary>Counter names already warned about a read-back mismatch, so each is warned about once per process.</summary>
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> readBackWarned = new(StringComparer.OrdinalIgnoreCase);

            public void SetSolves(string questName, int completions)
            {
                player.QuestManager.SetQuestCompletions(questName, completions);

                // SetQuestCompletions clamps to the world quest row's MaxSolves (a MISSING row reads as 0) and refuses silently for a mule
                // or a templated player outside the bypass. Any of those leaves the counter short of what was written: say so, once per name.
                var stored = player.QuestManager.GetCurrentSolves(questName);

                if (stored != completions && readBackWarned.TryAdd(questName, true))
                    log.Warn($"[PVP] {player.Name} (0x{player.Guid.Full:X8}): daily counter {questName} reads {stored} after writing {completions}; check the quest row exists with max_solves -1 or at least the target (warned once per counter)");
            }

            public string GetState(PvpDailyStateSlot slot) => player.GetProperty(StateProperty(slot));

            public void SetState(PvpDailyStateSlot slot, string value)
            {
                if (value == null)
                    player.RemoveProperty(StateProperty(slot));
                else
                    player.SetProperty(StateProperty(slot), value);
            }

            public int GetStamp(PvpDailyStamp stamp) => player.GetProperty(StampProperty(stamp)) ?? 0;

            public void SetStamp(PvpDailyStamp stamp, int value) => player.SetProperty(StampProperty(stamp), value);

            private static PropertyString StateProperty(PvpDailyStateSlot slot) => slot switch
            {
                PvpDailyStateSlot.ProvingGround => PropertyString.PvpDailyProvingGroundState,
                PvpDailyStateSlot.GladiatorsStreak => PropertyString.PvpDailyGladiatorsStreakState,
                _ => throw new ArgumentOutOfRangeException(nameof(slot)),
            };

            private static PropertyInt StampProperty(PvpDailyStamp stamp) => stamp switch
            {
                PvpDailyStamp.LifetimeBattlegroundWins => PropertyInt.PvpLifetimeBattlegroundWins,
                PvpDailyStamp.PeakRating => PropertyInt.PvpPeakRating,
                _ => throw new ArgumentOutOfRangeException(nameof(stamp)),
            };
        }
    }
}
