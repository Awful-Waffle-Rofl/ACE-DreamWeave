using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.Entity;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Managers
{
    public class QuestManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This is almost always a Player
        /// 
        /// however there are some rare cases of Creatures having quests
        /// such as 'chickencrossingroad'
        /// </summary>
        public Creature Creature { get; }

        public Fellowship Fellowship { get; }

        private ICollection<CharacterPropertiesQuestRegistry> runtimeQuests { get; set; } = new HashSet<CharacterPropertiesQuestRegistry>();

        public string Name
        {
            get
            {
                if (Creature != null)
                    return Creature.Name;
                else
                    return $"Fellowship({Fellowship.FellowshipName})";
            }

        }
        public uint IDtoUseForQuestRegistry
        {
            get
            {
                if (Creature != null)
                    return Creature.Guid.Full;
                else
                    return 1; 
                    //return Fellowship.FellowshipLeaderGuid;
            }
        }

        public static bool Debug = false;

        /// <summary>
        /// Constructs a new QuestManager for a Player / Creature
        /// </summary>
        public QuestManager(Creature creature)
        {
            Creature = creature;
        }

        /// <summary>
        /// Constructs a new QuestManager for a Fellowship
        /// </summary>
        public QuestManager(Fellowship fellowship)
        {
            Fellowship = fellowship;
        }

        /// <summary>
        /// This will return a clone of the quests collection. You should not mutate the results.
        /// This is mostly used for information/debugging
        /// </summary>
        /// <returns></returns>
        public ICollection<CharacterPropertiesQuestRegistry> GetQuests()
        {
            if (Creature is Player player)
                return player.Character.GetQuests(player.CharacterDatabaseLock);

            // Not a player
            return runtimeQuests;
        }

        /// <summary>
        /// Returns TRUE if a player has started a particular quest
        /// </summary>
        public bool HasQuest(string questFormat)
        {
            var questName = GetQuestName(questFormat);
            var hasQuest = GetQuest(questName) != null;

            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.HasQuest({questFormat}): {hasQuest}");

            return hasQuest;
        }

        public bool HasQuestCompletes(string questName)
        {
            if (Debug) Console.WriteLine($"{Name}.QuestManager.HasQuestCompletes({questName})");

            if (!questName.Contains("@"))
                return HasQuest(questName);

            var pieces = questName.Split('@');
            if (pieces.Length != 2)
            {
                Console.WriteLine($"{Name}.QuestManager.HasQuestCompletes({questName}): error parsing quest name");
                return false;
            }
            var name = pieces[0];
            if (!Int32.TryParse(pieces[1], out var numCompletes))
            {
                Console.WriteLine($"{Name}.QuestManager.HasQuestCompletes({questName}): unknown quest format");
                return HasQuest(questName);
            }
            var quest = GetQuest(name);
            if (quest == null)
                return false;

            var success = quest.NumTimesCompleted == numCompletes;     // minimum or exact?
            if (Debug) Console.WriteLine(success);
            return success;
        }

        /// <summary>
        /// Returns an active or completed quest for this player
        /// </summary>
        public CharacterPropertiesQuestRegistry GetQuest(string questName)
        {
            if (Creature is Player player)
                return player.Character.GetQuest(questName, player.CharacterDatabaseLock);

            // Not a player
            return runtimeQuests.FirstOrDefault(q => q.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase));
        }

        private CharacterPropertiesQuestRegistry GetOrCreateQuest(string questName, out bool questRegistryWasCreated)
        {
            if (Creature is Player player)
                return player.Character.GetOrCreateQuest(questName, player.CharacterDatabaseLock, out questRegistryWasCreated);

            // Not a player
            var existing = runtimeQuests.FirstOrDefault(q => q.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new CharacterPropertiesQuestRegistry
                {
                    QuestName = questName,
                };

                runtimeQuests.Add(existing);

                questRegistryWasCreated = true;
            }
            else
                questRegistryWasCreated = false;

            return existing;
        }

        /// <summary>
        /// Adds or updates a quest completion to the player's registry
        /// </summary>
        public void Update(string questFormat)
        {
            // Mule (WaffleACE): a mule makes no quest progress. Update and SetQuestCompletions are the only
            // two row-creating / advancing primitives here (Increment and SetQuestBits both route through
            // them), and Decrement / Erase cannot advance a quest so they are deliberately not guarded.
            // Silent, because emote scripts call this in bulk and a chat refusal per row would flood the
            // player. QuestManager can also hold a plain Creature, so the guard only applies to a Player.
            if (Creature is Player muleCheck && muleCheck.MuleBlocked(MuleAction.AdvanceQuest, notify: false))
                return;

            // PvP template (progression lock): a templated player makes no quest progress. Silent for the same reason
            // as the mule guard above.
            if (Creature is Player templateCheck && templateCheck.PvpTemplateBlocked(PvpTemplateAction.QuestStamp) != null)
                return;

            var questName = GetQuestName(questFormat);

            var quest = GetOrCreateQuest(questName, out var questRegistryWasCreated);

            if (questRegistryWasCreated)
            {
                quest.LastTimeCompleted = (uint) Time.GetUnixTime();
                quest.NumTimesCompleted = 1; // initial add / first solve

                quest.CharacterId = IDtoUseForQuestRegistry;

                if (Debug) Console.WriteLine($"{Name}.QuestManager.Update({quest}): added quest");

                if (Creature is Player player)
                {
                    player.CharacterChangesDetected = true;

                    player.ContractManager.NotifyOfQuestUpdate(quest.QuestName);

                    // a brand new registry row is worth a quest stamp - only here, never on the update path
                    // (see Player_QuestStamps)
                    player.HandleQuestStampRowCreated(quest.QuestName, notify: true);

                    // Bluespire ladder: a brand new registry row is the ONE event that means "this character
                    // has done this for the first time, ever", which is exactly the once-per-character-ever
                    // payout rule - re-running a cleared rung reaches the else branch below and pays nothing.
                    // A SIBLING of the stamp call above, deliberately not a line inside it: that method is
                    // gated on quest_stamps_enabled, and the ladder reward must not stop when an unrelated
                    // feature is switched off.
                    player.TryPayBluespireLadderReward(quest.QuestName);
                }
            }
            else
            {
                if (IsMaxSolves(questName))
                {
                    if (Debug) Console.WriteLine($"{Name}.QuestManager.Update({quest}): can not update existing quest. IsMaxSolves({questName}) is true.");
                    return;
                }

                // update existing quest
                quest.LastTimeCompleted = (uint)Time.GetUnixTime();
                quest.NumTimesCompleted++;

                if (Debug) Console.WriteLine($"{Name}.QuestManager.Update({quest}): updated quest ({quest.NumTimesCompleted})");

                if (Creature is Player player)
                {
                    player.CharacterChangesDetected = true;

                    player.ContractManager.NotifyOfQuestUpdate(quest.QuestName);
                }
            }
        }

        /// <summary>
        /// Initialize a quest completion with the provided number to the player's registry
        /// </summary>
        public void SetQuestCompletions(string questFormat, int questCompletions = 0)
        {
            // Mule (WaffleACE): see the note on Update above - same reasoning, silent for the same reason.
            if (Creature is Player muleCheck && muleCheck.MuleBlocked(MuleAction.AdvanceQuest, notify: false))
                return;

            // PvP template (progression lock): see the note on Update above.
            if (Creature is Player templateCheck && templateCheck.PvpTemplateBlocked(PvpTemplateAction.QuestStamp) != null)
                return;

            var questName = GetQuestName(questFormat);

            var maxSolves = GetMaxSolves(questName);

            var numTimesCompleted = maxSolves > -1 ? Math.Min(questCompletions, maxSolves) : Math.Abs(questCompletions);

            var quest = GetOrCreateQuest(questName, out var questRegistryWasCreated);

            if (questRegistryWasCreated)
            {
                quest.LastTimeCompleted = (uint) Time.GetUnixTime();
                quest.NumTimesCompleted = numTimesCompleted; // initialize the quest to the given completions

                quest.CharacterId = IDtoUseForQuestRegistry;

                if (Debug) Console.WriteLine($"{Name}.QuestManager.SetQuestCompletions({questFormat}): initialized quest to {quest.NumTimesCompleted}");

                if (Creature is Player player)
                {
                    player.CharacterChangesDetected = true;

                    player.ContractManager.NotifyOfQuestUpdate(quest.QuestName);

                    // A brand new registry row is worth a quest stamp, and this path announces it like any
                    // other first-time stamp. This looks like a silent bookkeeping path but is not: retail
                    // content uses SetQuestCompletions as its "stamp at quest hand-out" mechanism (the
                    // Facility Hub Wardens, e.g. wcid 42124, stamp fachubbanderlingcampportal_flag via
                    // EmoteType.SetQuestCompletions when handing out the task), so staying silent here would
                    // hide the acceptance stamp the player just earned. Stamps are player-visible events on
                    // this server, and an internal-looking flag name in the message is acceptable noise -
                    // players already see names like FacilityHubFound_1111 from the Update path.
                    // SetQuestBits routes through here too, but only creates a row on the FIRST bit set, so a
                    // bitfield quest produces at most one message ever.
                    player.HandleQuestStampRowCreated(quest.QuestName, notify: true);

                    // Bluespire ladder: mirrored from Update's created-row branch above, for the same reason
                    // and on the same terms - a created row here is equally a first-ever clear, so content
                    // that stamps a rung clear with SetQuestCompletions rather than Update must pay
                    // identically. See the note at the Update call site.
                    player.TryPayBluespireLadderReward(quest.QuestName);
                }
            }
            else
            {
                // update existing quest
                quest.LastTimeCompleted = (uint)Time.GetUnixTime();
                quest.NumTimesCompleted = numTimesCompleted;

                if (Debug) Console.WriteLine($"{Name}.QuestManager.SetQuestCompletions({questFormat}): initialized quest to {quest.NumTimesCompleted}");

                if (Creature is Player player)
                {
                    player.CharacterChangesDetected = true;

                    player.ContractManager.NotifyOfQuestUpdate(quest.QuestName);
                }
            }
        }

        /// <summary>
        /// Returns TRUE if player can solve this quest now
        /// </summary>
        public bool CanSolve(string questFormat)
        {
            var questName = GetQuestName(questFormat);

            // verify max solves / quest timer
            var nextSolveTime = GetNextSolveTime(questName);

            var canSolve = nextSolveTime == TimeSpan.MinValue;
            if (Debug) Console.WriteLine($"{Name}.QuestManager.CanSolve({questName}): {canSolve}");
            return canSolve;
        }

        /// <summary>
        /// Returns TRUE if player has reached the maximum # of solves for this quest
        /// </summary>
        public bool IsMaxSolves(string questName)
        {
            var quest = DatabaseManager.World.GetCachedQuest(questName);
            if (quest == null) return false;

            var playerQuest = GetQuest(questName);
            if (playerQuest == null) return false;  // player hasn't completed this quest yet

            // return TRUE if quest has solve limit, and it has been reached
            return quest.MaxSolves > -1 && playerQuest.NumTimesCompleted >= quest.MaxSolves;
        }

        /// <summary>
        /// Returns the maximum # of solves for this quest
        /// </summary>
        public int GetMaxSolves(string questFormat)
        {
            var questName = GetQuestName(questFormat);

            var quest = DatabaseManager.World.GetCachedQuest(questName);
            if (quest == null) return 0;

            return quest.MaxSolves;
        }

        /// <summary>
        /// Returns the current # of solves for this quest
        /// </summary>
        public int GetCurrentSolves(string questFormat)
        {
            var questName = GetQuestName(questFormat);

            var quest = GetQuest(questName);
            if (quest == null) return 0;

            return quest.NumTimesCompleted;
        }

        /// <summary>
        /// Some quests we do not want to scale MinDelta if "quest_mindelta_rate" has been set.
        /// They may be things that are races against time, like Colo
        /// </summary>
        public static bool CanScaleQuestMinDelta(Database.Models.World.Quest quest)
        {
            if (quest.Name.StartsWith("ColoArena"))
                return false;

            return true;
        }

        /// <summary>
        /// Returns the time remaining until the player can solve this quest again
        /// </summary>
        public TimeSpan GetNextSolveTime(string questFormat)
        {
            var questName = GetQuestName(questFormat);

            var quest = DatabaseManager.World.GetCachedQuest(questName);
            if (quest == null)
                return TimeSpan.MaxValue;   // world quest not found - cannot solve it

            var playerQuest = GetQuest(questName);
            if (playerQuest == null)
                return TimeSpan.MinValue;   // player hasn't completed this quest yet - can solve immediately

            if (quest.MaxSolves > -1 && playerQuest.NumTimesCompleted >= quest.MaxSolves)
                return TimeSpan.MaxValue;   // cannot solve this quest again - max solves reached / exceeded

            var currentTime = (uint)Time.GetUnixTime();
            uint nextSolveTime;

            if (CanScaleQuestMinDelta(quest))
                nextSolveTime = playerQuest.LastTimeCompleted + (uint)(quest.MinDelta * PropertyManager.GetDouble("quest_mindelta_rate", 1).Item);
            else
                nextSolveTime = playerQuest.LastTimeCompleted + quest.MinDelta;

            if (currentTime >= nextSolveTime)
                return TimeSpan.MinValue;   // can solve again now - next solve time expired

            // return the time remaining on the player's quest timer
            return TimeSpan.FromSeconds(nextSolveTime - currentTime);
        }

        /// <summary>
        /// Increment the number of times completed for a quest
        /// </summary>
        public void Increment(string questName, int amount = 1)
        {
            for (var i = 0; i < amount; i++)
                Update(questName);
        }

        /// <summary>
        /// Decrement the number of times completed for a quest
        /// </summary>
        public void Decrement(string quest, int amount = 1)
        {
            var questName = GetQuestName(quest);

            var existing = GetQuest(questName);

            if (existing != null)
            {
                //if (existing.NumTimesCompleted == 0)
                //{
                //    if (Debug) Console.WriteLine($"{Name}.QuestManager.Decrement({quest}): can not Decrement existing quest. {questName}.NumTimesCompleted is already 0.");
                //    return;
                //}

                // update existing quest
                existing.LastTimeCompleted = (uint)Time.GetUnixTime();
                existing.NumTimesCompleted -= amount;

                if (Debug) Console.WriteLine($"{Name}.QuestManager.Decrement({quest}): updated quest ({existing.NumTimesCompleted})");

                if (Creature is Player player)
                {
                    player.CharacterChangesDetected = true;
                    player.ContractManager.NotifyOfQuestUpdate(existing.QuestName);
                }
            }
        }

        /// <summary>
        /// Removes an existing quest from the Player's registry
        /// </summary>
        public void Erase(string questFormat)
        {
            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.Erase({questFormat})");

            var questName = GetQuestName(questFormat);

            if (Creature is Player player)
            {
                if (player.Character.EraseQuest(questName, player.CharacterDatabaseLock))
                {
                    player.CharacterChangesDetected = true;

                    player.ContractManager.NotifyOfQuestUpdate(questName);
                }
            }
            else
            {
                // Not a player
                var quests = runtimeQuests.Where(q => q.QuestName.Equals(questName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var quest in quests)
                    runtimeQuests.Remove(quest);
            }
        }

        /// <summary>
        /// Removes an all quests from registry
        /// </summary>
        public void EraseAll()
        {
            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.EraseAll");

            if (Creature is Player player)
            {
                player.Character.EraseAllQuests(out var questNamesErased, player.CharacterDatabaseLock);

                if (questNamesErased.Count > 0)
                {
                    player.CharacterChangesDetected = true;

                    foreach (var questName in questNamesErased)
                        player.ContractManager.NotifyOfQuestUpdate(questName);
                }
            }
            else
            {
                // Not a player
                runtimeQuests.Clear();
            }
        }

        /// <summary>
        /// Shows the current quests in progress for a Player
        /// </summary>
        public void ShowQuests(Player player)
        {
            Console.WriteLine("ShowQuests");

            var quests = GetQuests();

            if (quests.Count == 0)
            {
                Console.WriteLine("No quests in progress for " + Name);
                return;
            }

            foreach (var quest in quests)
            {
                Console.WriteLine("Quest Name: " + quest.QuestName);
                Console.WriteLine("Times Completed: " + quest.NumTimesCompleted);
                Console.WriteLine("Last Time Completed: " + quest.LastTimeCompleted);
                Console.WriteLine("Player ID: " + quest.CharacterId.ToString("X8"));
                Console.WriteLine("----");
            }
        }

        public void Stamp(string questFormat)
        {
            var questName = GetQuestName(questFormat);
            Update(questName);  // ??
        }

        /// <summary>
        /// Returns the quest name without the @ comment
        /// </summary>
        /// <param name="questFormat">A quest name with an optional @comment on the end</param>
        public static string GetQuestName(string questFormat)
        {
            var idx = questFormat.IndexOf('@');     // strip comment
            if (idx == -1)
                return questFormat;

            var questName = questFormat.Substring(0, idx);
            return questName;
        }

        /// <summary>
        /// Returns TRUE if player has solved this quest between min-max times
        /// </summary>
        public bool HasQuestSolves(string questFormat, int? _min, int? _max)
        {
            var questName = GetQuestName(questFormat);    // strip optional @comment

            var quest = GetQuest(questName);
            var numSolves = quest != null ? quest.NumTimesCompleted : 0;

            int min = _min ?? int.MinValue;    // use defaults?
            int max = _max ?? int.MaxValue;

            var hasQuestSolves = numSolves >= min && numSolves <= max;    // verify: can either of these be -1?
            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.HasQuestSolves({questFormat}, {_min}, {_max}): {hasQuestSolves}");

            return hasQuestSolves;
        }

        /// <summary>
        /// Called when a player hasn't started a quest yet
        /// </summary>
        public void HandleNoQuestError(WorldObject wo)
        {
            var player = Creature as Player;

            if (player == null) return;

            var error = new GameEventInventoryServerSaveFailed(player.Session, wo.Guid.Full, WeenieError.ItemRequiresQuestToBePickedUp);
            player.Session.Network.EnqueueSend(error);
        }

        public void HandlePortalQuestError(string questName)
        {
            var player = Creature as Player;

            if (player == null) return;

            if (!HasQuest(questName))
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouMustCompleteQuestToUsePortal));
            }
            else if (CanSolve(questName))
            {
                var error = new GameEventWeenieError(player.Session, WeenieError.QuestSolvedTooLongAgo);
                var text = new GameMessageSystemChat("You completed the quest this portal requires too long ago!", ChatMessageType.Magic); // This msg wasn't sent in retail PCAP, leading to a completely silent fail when using the portal with an expired flag.
                player.Session.Network.EnqueueSend(text, error);
            }
        }

        /// <summary>
        /// Called when either the player has completed the quest too recently, or max solves has been reached.
        /// </summary>
        public void HandleSolveError(string questName)
        {
            var player = Creature as Player;
            if (player == null) return;

            if (IsMaxSolves(questName))
            {
                var error = new GameEventInventoryServerSaveFailed(player.Session, 0, WeenieError.YouHaveSolvedThisQuestTooManyTimes);
                var text = new GameMessageSystemChat("You have solved this quest too many times!", ChatMessageType.Broadcast);
                player.Session.Network.EnqueueSend(text, error);
            }
            else
            {
                var error = new GameEventInventoryServerSaveFailed(player.Session, 0, WeenieError.YouHaveSolvedThisQuestTooRecently);
                var text = new GameMessageSystemChat("You have solved this quest too recently!", ChatMessageType.Broadcast);

                var remainStr = GetNextSolveTime(questName).GetFriendlyString();
                var remain = new GameMessageSystemChat($"You may complete this quest again in {remainStr}.", ChatMessageType.Broadcast);
                player.Session.Network.EnqueueSend(text, remain, error);
            }
        }

        /// <summary>
        /// The result of a single kill-task stamp attempt, as decided by <see cref="ApplyKillStamp"/>.
        ///
        /// Bluespire ladder (WaffleACE): this exists so the daily-cooldown gate and the repeat-payout
        /// decision can both be made from one return value instead of re-deriving "did this kill actually
        /// advance anything" from GetQuest state at two separate call sites.
        /// </summary>
        internal enum KillStampOutcome
        {
            /// <summary>bootstrapRow was false and the character does not hold this quest - the original
            /// early-return case, unchanged.</summary>
            NotHeld,

            /// <summary>bootstrapRow was true, the row already exists, and it is still within its MinDelta
            /// window - Bluespire ladder daily cooldown. Neither stamped nor timer-reset.</summary>
            OnCooldown,

            /// <summary>The registry row did not exist before this call and does now - a first-ever clear.
            /// Already paid inside Update's created-row branch; never pay again here.</summary>
            Created,

            /// <summary>The registry row already existed and was incremented again (the pre-existing,
            /// non-ladder behaviour for every other kill task, and the ladder's post-cooldown re-clear).</summary>
            Restamped,

            /// <summary>Either a bootstrap creation or an existing-row restamp was silently declined by
            /// Update: no row existed before and none exists after (the mule guard refused the creation -
            /// not an error, a mule makes no quest progress), OR a row already existed and its completion
            /// state (NumTimesCompleted, LastTimeCompleted) is UNCHANGED after Stamp - Update's mule guard
            /// or its IsMaxSolves guard silently returned without touching it. Both are the same shape from
            /// the caller's side: this kill produced no progress, so it must not be reported as a restamp
            /// and must never pay the ladder's repeat reward.</summary>
            Refused
        }

        /// <summary>
        /// Applies (or refuses) a single kill-task stamp and reports what happened, so callers can react
        /// without re-deriving state from GetQuest themselves.
        /// </summary>
        /// <param name="bootstrapRow">
        /// Bluespire ladder (WaffleACE): when true, skip the HasQuest gate below so this kill CREATES the
        /// registry row instead of only crediting one the character already holds, and gates on the
        /// ladder's daily cooldown before stamping. Passed by exactly one caller - HandleKillTask, itself
        /// called only for the ladder's own six rung-clear stamps when true - so no other kill task can
        /// inherit either behaviour by accident.
        /// </param>
        internal KillStampOutcome ApplyKillStamp(string questName, bool bootstrapRow)
        {
            // The GetCachedQuest lookup in HandleKillTask still stands in front of this, bootstrap or not,
            // so a kill can never create a row for a quest name that has no world-database row behind it.
            if (!bootstrapRow && !HasQuest(questName))
                return KillStampOutcome.NotHeld;

            // Bluespire ladder daily cooldown: checked BEFORE Stamp, so a kill that lands inside the
            // window neither re-stamps (incrementing NumTimesCompleted) nor resets LastTimeCompleted - the
            // timer keeps counting down from the ORIGINAL clear, not from the kill that found it still on
            // cooldown. Scoped to the ladder's own six rung-clear names (BluespireLadderRewards.
            // RungForQuestName), NOT to bootstrapRow alone: bootstrapRow is also true for a
            // KillQuestCreatesRow kill (WaffleACE round 17), and that quest's world row (e.g.
            // BluespireMenhirDrummer, max_Solves 1) has no MinDelta cooldown at all - CanSolve on it
            // returns false once solved for an entirely different reason (the solve cap, not a cooldown
            // window), and GetNextSolveTime has no real "cleared again in" time to report for it. Gating
            // this on RungForQuestName keeps the ladder's cooldown message exactly as it was, and lets a
            // KillQuestCreatesRow-only repeat kill fall through to the normal Stamp/Refused path below,
            // printing the same message a non-bootstrap repeat kill on an existing row always has.
            // Non-bootstrap kill tasks (bootstrapRow false) never reach this check either way, so their
            // existing-row behaviour is unchanged even when their world quest row carries a MinDelta > 0 -
            // CanSolve/GetNextSolveTime are consulted only here, exactly as before this change.
            if (bootstrapRow && BluespireLadderRewards.RungForQuestName(questName) != 0 && HasQuest(questName) && !CanSolve(questName))
                return KillStampOutcome.OnCooldown;

            // Classify by COMPLETION STATE, never by row existence alone. Update() has two silent
            // no-op paths on an EXISTING row - the mule guard (:179 above, MuleBlocked(AdvanceQuest)) and
            // IsMaxSolves (:216-220) - and in both, the row is left in place with NumTimesCompleted and
            // LastTimeCompleted untouched. Existence-only classification would read that as a Restamp, and
            // a bootstrap Restamp is exactly what pays the ladder's repeat reward - so a muled character
            // (whose fellowship membership is not re-checked after conversion, and who is still credited by
            // OnDeath_HandleKillTask's fellow-in-range branch) would be paid on every fellow's re-clear with
            // no cooldown, because LastTimeCompleted never advances and CanSolve stays true forever.
            var rowBefore = GetQuest(questName);
            var hadRowBefore = rowBefore != null;
            var numBefore = rowBefore?.NumTimesCompleted;
            var lastBefore = rowBefore?.LastTimeCompleted;

            Stamp(questName);

            var rowAfter = GetQuest(questName);

            if (rowAfter == null)
                return KillStampOutcome.Refused;

            if (!hadRowBefore)
                return KillStampOutcome.Created;

            if (rowAfter.NumTimesCompleted == numBefore && rowAfter.LastTimeCompleted == lastBefore)
                return KillStampOutcome.Refused;

            return KillStampOutcome.Restamped;
        }

        /// <summary>
        /// Builds the kill-task progress message sent to the player after a stamp attempt.
        /// </summary>
        /// <param name="numTimesCompleted">The player's current NumTimesCompleted for this quest.</param>
        /// <param name="pluralName">The plural name of the creature killed.</param>
        /// <param name="maxSolves">The quest's MaxSolves (-1 means unlimited).</param>
        /// <param name="isMaxSolves">Whether <see cref="IsMaxSolves"/> is true for this quest right now.</param>
        internal static string BuildKillTaskMessage(int numTimesCompleted, string pluralName, int maxSolves, bool isMaxSolves)
        {
            var msg = $"You have killed {numTimesCompleted} {pluralName}!";

            if (isMaxSolves)
                msg += $" Your task is complete!";
            else if (maxSolves < 0)
                // Unlimited-solve kill tasks (MaxSolves -1, e.g. daily-repeatable Bluespire ladder clears)
                // are never IsMaxSolves, so without this branch the else below prints "You must kill -1".
                msg += $" Task complete!";
            else
                msg += $" You must kill {maxSolves} to complete your task.";

            return msg;
        }

        /// <summary>
        /// Increments the counter for a kill task for a player
        /// </summary>
        /// <param name="bootstrapRow">
        /// Bluespire ladder (WaffleACE): when true, let this kill CREATE the player's registry row rather
        /// than only credit an existing one, and gate it on the ladder's daily cooldown. See
        /// <see cref="ApplyKillStamp"/> and BluespireLadderRewards.ShouldBootstrapClearRow for the
        /// bootstrap cycle that makes the usual cure (pre-arming the row with SetQuestCompletions)
        /// unavailable to the ladder.
        /// </param>
        public void HandleKillTask(string killQuestName, WorldObject killedCreature, bool bootstrapRow = false)
        {
            var player = Creature as Player;
            if (player == null) return;

            // http://acpedia.org/wiki/Announcements_-_2012/12_-_A_Growing_Twilight#Release_Notes

            if (killedCreature == null)
            {
                log.ErrorFormat("{0}.QuestManager.HandleKillTask({1}): input object is null!", Name, killQuestName);
                return;
            }

            var questName = GetQuestName(killQuestName);
            var quest = DatabaseManager.World.GetCachedQuest(questName);

            if (quest == null)
            {
                log.ErrorFormat("{0}.QuestManager.HandleKillTask({1}): couldn't find kill task {2} in database", Name, killQuestName, questName);
                return;
            }

            var outcome = ApplyKillStamp(questName, bootstrapRow);

            if (outcome == KillStampOutcome.NotHeld)
                return;

            if (outcome == KillStampOutcome.OnCooldown)
            {
                var remainStr = GetNextSolveTime(questName).GetFriendlyString();
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You have already cleared this rung today. It can be cleared again in {remainStr}.", ChatMessageType.Broadcast));

                return;
            }

            var playerQuest = GetQuest(questName);

            if (playerQuest == null)
            {
                // playerQuest can only be null here for the no-row-ever-existed shape of Refused (a
                // bootstrap creation the mule guard turned down) - the blocked-restamp shape of Refused
                // always leaves an existing row in place, so it never reaches this branch. A mule making no
                // quest progress is the intended outcome, not an error, so it must not be logged as one.
                if (outcome != KillStampOutcome.Refused)
                    log.ErrorFormat("{0}.QuestManager.HandleKillTask({1}): couldn't find kill task {2} in player quests", Name, killQuestName, questName);

                return;
            }

            var msg = BuildKillTaskMessage(playerQuest.NumTimesCompleted, killedCreature.GetPluralName(), quest.MaxSolves, IsMaxSolves(questName));

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

            // Bluespire ladder daily repeat payout (WaffleACE): only Restamped - an existing bootstrap row
            // re-stamped after its cooldown elapsed - pays here. Created already paid inside Update's
            // created-row branch (TryPayBluespireLadderReward), so paying again on Created would double-pay
            // the character's first-ever clear. The bootstrapRow guard keeps this call scoped to the
            // ladder's own six rung names; BluespireLadderRewards.Pay would no-op for anything else anyway,
            // via its own RungForQuestName check.
            if (bootstrapRow && BluespireLadderRewards.ShouldPayOnKillStamp(outcome))
                player.TryPayBluespireLadderReward(questName, firstClear: false);
        }

        /// <summary>
        /// Called when a player kills Creature
        /// </summary>
        public void OnDeath(WorldObject killer)
        {
        }

        public bool HasQuestBits(string questFormat, int bits)
        {
            var questName = GetQuestName(questFormat);

            var quest = GetQuest(questName);
            if (quest == null) return false;

            var hasQuestBits = (quest.NumTimesCompleted & bits) == bits;

            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.HasQuestBits({questFormat}, 0x{bits:X}): {hasQuestBits}");

            return hasQuestBits;
        }

        public bool HasNoQuestBits(string questFormat, int bits)
        {
            var questName = GetQuestName(questFormat);

            var quest = GetQuest(questName);
            if (quest == null) return true;

            var hasNoQuestBits = (quest.NumTimesCompleted & bits) == 0;

            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.HasNoQuestBits({questFormat}, 0x{bits:X}): {hasNoQuestBits}");

            return hasNoQuestBits;
        }

        public void SetQuestBits(string questFormat, int bits, bool on = true)
        {
            var questName = GetQuestName(questFormat);

            var quest = GetQuest(questName);

            var questBits = 0;

            if (quest != null) questBits = quest.NumTimesCompleted;

            if (on)
                questBits |= bits;
            else
                questBits &= ~bits;

            if (Debug)
                Console.WriteLine($"{Name}.QuestManager.SetQuestBits({questFormat}, 0x{bits:X}): {on}");

            SetQuestCompletions(questFormat, questBits);
        }
    }
}
