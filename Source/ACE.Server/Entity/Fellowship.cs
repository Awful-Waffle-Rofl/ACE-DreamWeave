using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Entity
{
    public class Fellowship
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The retail maximum # of fellowship members, and the default for 'fellowship_max_members'.
        /// </summary>
        public const int RetailMaxFellows = 9;

        /// <summary>
        /// Hard ceiling on 'fellowship_max_members'. Nothing in the wire format or the client's fellow list
        /// caps the roster (it is a length-prefixed packable hash table, and GameEventFellowshipFullUpdate
        /// already spans multiple packet fragments at 9 members), but the full-update packet grows ~60 bytes
        /// per fellow and OnVitalUpdate costs O(n^2) messages per tick, so an unbounded value is a footgun.
        /// </summary>
        public const int AbsoluteMaxFellows = 100;

        /// <summary>
        /// The maximum # of fellowship members, from the 'fellowship_max_members' server property.
        /// Clamped to [1, <see cref="AbsoluteMaxFellows"/>].
        /// </summary>
        public static int MaxFellows
        {
            get
            {
                var configured = PropertyManager.GetLong("fellowship_max_members").Item;

                return (int)Math.Clamp(configured, 1, AbsoluteMaxFellows);
            }
        }

        public string FellowshipName;
        public uint FellowshipLeaderGuid;

        public bool DesiredShareXP;     // determined by the leader's 'ShareFellowshipExpAndLuminance' client option when fellowship is created
        public bool ShareLoot;          // determined by the leader's 'ShareFellowshipLoot' client option when fellowship is created

        public bool ShareXP;            // whether or not XP sharing is currently enabled, as determined by DesiredShareXP && level restrictions
        public bool EvenShare;          // true if all fellows are >= level 50, or all fellows are within 5 levels of the leader

        public bool Open;               // indicates if non-leaders can invite new fellowship members
        public bool IsLocked;           // only set through emotes. if a fellowship is locked, new fellowship members cannot be added

        public Dictionary<uint, WeakReference<Player>> FellowshipMembers;

        public Dictionary<uint, int> DepartedMembers;

        public Dictionary<string, FellowshipLockData> FellowshipLocks;

        public QuestManager QuestManager;

        /// <summary>
        /// WaffleACE: when true, FellowshipManager.Tick() ejects members who have not contributed XP to
        /// the fellowship for 'fellowship_leech_timeout' seconds. Merely sharing a landblock does not count
        /// as activity. Toggled per-fellowship by the leader via /fship noleech on|off.
        /// </summary>
        public bool LeechManagementEnabled;

        /// <summary>
        /// WaffleACE: per-member last-active unix timestamp for leech management.
        /// Refreshed only when a member contributes XP/luminance to the fellowship (StampContribution).
        /// </summary>
        public Dictionary<uint, double> LeechActivity;

        /// <summary>
        /// WaffleACE: unix timestamp of each leech ejection from this fellowship. A booted player cannot
        /// rejoin this fellowship (invite, /fship join, or mass-add) until 'fellowship_leech_rejoin_lockout'
        /// seconds have passed. In-memory only, like the fellowship itself.
        /// </summary>
        public Dictionary<uint, double> LeechBoots;

        /// <summary>
        /// Called when a player first creates a Fellowship
        /// </summary>
        public Fellowship(Player leader, string fellowshipName, bool shareXP)
        {
            DesiredShareXP = shareXP;
            ShareXP = shareXP;

            // get loot sharing from leader's character options
            ShareLoot = leader.GetCharacterOption(CharacterOption.ShareFellowshipLoot);

            FellowshipLeaderGuid = leader.Guid.Full;
            FellowshipName = fellowshipName;
            EvenShare = false;

            FellowshipMembers = new Dictionary<uint, WeakReference<Player>>() { { leader.Guid.Full, new WeakReference<Player>(leader) } };

            Open = false;

            QuestManager = new QuestManager(this);
            IsLocked = false;
            DepartedMembers = new Dictionary<uint, int>();
            FellowshipLocks = new Dictionary<string, FellowshipLockData>();

            LeechManagementEnabled = PropertyManager.GetBool("fellowship_leech_check_default").Item;
            LeechActivity = new Dictionary<uint, double>() { { leader.Guid.Full, Time.GetUnixTime() } };
            LeechBoots = new Dictionary<uint, double>();

            FellowshipManager.Register(this);
        }

        /// <summary>
        /// Called when a player clicks the 'add fellow' button
        /// </summary>
        public void AddFellowshipMember(Player inviter, Player newMember)
        {
            if (inviter == null || newMember == null)
                return;

            var lockoutRemaining = GetLeechLockoutRemaining(newMember);

            if (lockoutRemaining > 0)
            {
                inviter.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{newMember.Name} was removed from this fellowship for inactivity and cannot rejoin for another {LockoutMinutes(lockoutRemaining)} minute(s).",
                    ChatMessageType.Fellowship));
                return;
            }

            if (IsLocked)
            {

                if (!DepartedMembers.TryGetValue(newMember.Guid.Full, out var timeDeparted))
                {
                    inviter.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(inviter.Session, WeenieErrorWithString.LockedFellowshipCannotRecruit_, newMember.Name));
                    //newMember.SendWeenieError(WeenieError.LockedFellowshipCannotRecruitYou);
                    return;
                }
                else
                {
                    var timeLimit = Time.GetDateTimeFromTimestamp(timeDeparted).AddSeconds(600);
                    if (DateTime.UtcNow > timeLimit)
                    {
                        inviter.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(inviter.Session, WeenieErrorWithString.LockedFellowshipCannotRecruit_, newMember.Name));
                        //newMember.SendWeenieError(WeenieError.LockedFellowshipCannotRecruitYou);
                        return;
                    }
                }
            }

            if (FellowshipMembers.Count >= MaxFellows)
            {
                inviter.Session.Network.EnqueueSend(new GameEventWeenieError(inviter.Session, WeenieError.YourFellowshipIsFull));
                return;
            }

            if (newMember.Fellowship != null || FellowshipMembers.ContainsKey(newMember.Guid.Full))
            {
                inviter.Session.Network.EnqueueSend(new GameMessageSystemChat($"{newMember.Name} is already a member of a Fellowship.", ChatMessageType.Broadcast));
            }
            else
            {
                if (PropertyManager.GetBool("fellow_busy_no_recruit").Item && newMember.IsBusy)
                {
                    inviter.Session.Network.EnqueueSend(new GameMessageSystemChat($"{newMember.Name} is busy.", ChatMessageType.Broadcast));
                    return;
                }

                if (newMember.GetCharacterOption(CharacterOption.AutomaticallyAcceptFellowshipRequests))
                {
                    AddConfirmedMember(inviter, newMember, true);
                }
                else
                {
                    if (!newMember.ConfirmationManager.EnqueueSend(new Confirmation_Fellowship(inviter.Guid, newMember.Guid), inviter.Name))
                    {
                        inviter.Session.Network.EnqueueSend(new GameMessageSystemChat($"{newMember.Name} is busy.", ChatMessageType.Broadcast));
                    }
                }
            }
        }

        /// <summary>
        /// Finalizes the process of adding a player to the fellowship
        /// If the player doesn't have the 'automatically accept fellowship requests' option set,
        /// this would be after they responded to the popup window
        /// </summary>
        public void AddConfirmedMember(Player inviter, Player player, bool response)
        {
            if (inviter == null || inviter.Session == null || inviter.Session.Player == null || player == null) return;

            if (!response)
            {
                // player clicked 'no' on the fellowship popup
                inviter.Session.Network.EnqueueSend(new GameMessageSystemChat($"{player.Name} declines your invite", ChatMessageType.Fellowship));
                inviter.Session.Network.EnqueueSend(new GameEventWeenieError(inviter.Session, WeenieError.FellowshipDeclined));
                return;
            }

            if (FellowshipMembers.Count >= MaxFellows)
            {
                inviter.Session.Network.EnqueueSend(new GameEventWeenieError(inviter.Session, WeenieError.YourFellowshipIsFull));
                return;
            }

            // final gate for every add path (invite confirm, /fship join, addlandblock): a leech-booted
            // player stays out of this fellowship until the rejoin lockout expires.
            var lockoutRemaining = GetLeechLockoutRemaining(player);

            if (lockoutRemaining > 0)
            {
                var minutes = LockoutMinutes(lockoutRemaining);

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You were removed from this fellowship for inactivity and cannot rejoin for another {minutes} minute(s).",
                    ChatMessageType.Fellowship));
                inviter.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{player.Name} was removed from this fellowship for inactivity and cannot rejoin for another {minutes} minute(s).",
                    ChatMessageType.Fellowship));
                return;
            }

            FellowshipMembers.TryAdd(player.Guid.Full, new WeakReference<Player>(player));
            player.Fellowship = inviter.Fellowship;

            LeechActivity[player.Guid.Full] = Time.GetUnixTime();

            CalculateXPSharing();

            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values.Where(i => i.Guid != player.Guid))
                member.Session.Network.EnqueueSend(new GameEventFellowshipUpdateFellow(member.Session, player, ShareXP));

            if (ShareLoot)
            {
                foreach (var member in fellowshipMembers.Values.Where(i => i.Guid != player.Guid))
                {
                    member.Session.Network.EnqueueSend(new GameMessageSystemChat($"{player.Name} has given you permission to loot his or her kills.", ChatMessageType.Broadcast));
                    member.Session.Network.EnqueueSend(new GameMessageSystemChat($"{player.Name} may now loot your kills.", ChatMessageType.Broadcast));

                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{member.Name} has given you permission to loot his or her kills.", ChatMessageType.Broadcast));
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{member.Name} may now loot your kills.", ChatMessageType.Broadcast));
                }
            }

            UpdateAllMembers();

            if (inviter.CurrentMotionState.Stance == MotionStance.NonCombat) // only do this motion if inviter is at peace, other times motion is skipped. 
                inviter.SendMotionAsCommands(MotionCommand.BowDeep, MotionStance.NonCombat);
        }

        public void RemoveFellowshipMember(Player player, Player leader)
        {
            if (player == null) return;

            var fellowshipMembers = GetFellowshipMembers();

            if (!fellowshipMembers.ContainsKey(player.Guid.Full))
            {
                log.Warn($"{leader.Name} tried to dismiss {player.Name} from the fellowship, but {player.Name} was not found in the fellowship");

                var done = true;

                if (player.Fellowship != null)
                {
                    if (player.Fellowship == this)
                    {
                        log.Warn($"{player.Name} still has a reference to this fellowship somehow. This shouldn't happen");
                        done = false;
                    }
                    else
                        log.Warn($"{player.Name} has a reference to a different fellowship. {leader.Name} is possibly sending crafted data!");
                }

                if (done) return;
            }

            foreach (var member in fellowshipMembers.Values)
            {
                member.Session.Network.EnqueueSend(new GameEventFellowshipDismiss(member.Session, player));
                member.Session.Network.EnqueueSend(new GameMessageSystemChat($"{player.Name} dismissed from fellowship", ChatMessageType.Fellowship));
            }

            FellowshipMembers.Remove(player.Guid.Full);
            player.Fellowship = null;

            CalculateXPSharing();

            UpdateAllMembers();
        }

        private void UpdateAllMembers()
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
                member.Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(member.Session));
        }

        private void SendMessageAndUpdate(string message)
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
            {
                member.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Fellowship));

                member.Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(member.Session));
            }
        }

        private void SendBroadcastAndUpdate(string message)
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
            {
                member.Session.Network.EnqueueSend(new GameEventChannelBroadcast(member.Session, Channel.FellowBroadcast, "", message));

                member.Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(member.Session));
            }
        }

        public void BroadcastToFellow(string message)
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
                member.Session.Network.EnqueueSend(new GameEventChannelBroadcast(member.Session, Channel.FellowBroadcast, "", message));
        }

        public void TellFellow(WorldObject sender, string message)
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
                member.Session.Network.EnqueueSend(new GameEventChannelBroadcast(member.Session, Channel.Fellow, sender.Name, message));
        }

        private void SendWeenieErrorWithStringAndUpdate(WeenieErrorWithString error, string message)
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
            {
                member.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(member.Session, error, message));

                member.Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(member.Session));
            }
        }

        public void QuitFellowship(Player player, bool disband)
        {
            if (player == null) return;

            if (player.Guid.Full == FellowshipLeaderGuid)
            {
                if (disband)
                {
                    var fellowshipMembers = GetFellowshipMembers();

                    foreach (var member in fellowshipMembers.Values)
                    {
                        member.Session.Network.EnqueueSend(new GameEventFellowshipDisband(member.Session));

                        if (ShareLoot)
                        {
                            member.Session.Network.EnqueueSend(new GameMessageSystemChat("You no longer have permission to loot anyone else's kills.", ChatMessageType.Broadcast));

                            // you would expect this occur, but it did not in retail pcaps
                            //foreach (var fellow in fellowshipMembers.Values)
                            //    member.Session.Network.EnqueueSend(new GameMessageSystemChat($"{fellow.Name} does not have permission to loot your kills.", ChatMessageType.Broadcast));
                        }

                        member.Fellowship = null;
                    }
                }
                else
                {
                    FellowshipMembers.Remove(player.Guid.Full);

                    if (IsLocked)
                    {
                        var timestamp = (int)Time.GetUnixTime();
                        if (!DepartedMembers.TryAdd(player.Guid.Full, timestamp))
                            DepartedMembers[player.Guid.Full] = timestamp;
                    }

                    player.Fellowship = null;

                    player.Session.Network.EnqueueSend(new GameEventFellowshipQuit(player.Session, player.Guid.Full));
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("You no longer have permission to loot anyone else's kills.", ChatMessageType.Broadcast));

                    var fellowshipMembers = GetFellowshipMembers();

                    foreach (var member in fellowshipMembers.Values)
                    {
                        member.Session.Network.EnqueueSend(new GameEventFellowshipQuit(member.Session, player.Guid.Full));

                        if (ShareLoot)
                        {
                            member.Session.Network.EnqueueSend(new GameMessageSystemChat($"You have lost permission to loot the kills of {player.Name}.", ChatMessageType.Broadcast));
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{member.Name} does not have permission to loot your kills.", ChatMessageType.Broadcast));
                        }
                    }
                    AssignNewLeader(null, null);

                    CalculateXPSharing();
                }
            }
            else if (!disband)
            {
                FellowshipMembers.Remove(player.Guid.Full);

                if (IsLocked)
                {
                    var timestamp = (int)Time.GetUnixTime();

                    if (!DepartedMembers.TryAdd(player.Guid.Full, timestamp))
                        DepartedMembers[player.Guid.Full] = timestamp;
                }

                player.Session.Network.EnqueueSend(new GameEventFellowshipQuit(player.Session, player.Guid.Full));

                var fellowshipMembers = GetFellowshipMembers();

                foreach (var member in fellowshipMembers.Values)
                {
                    member.Session.Network.EnqueueSend(new GameEventFellowshipQuit(member.Session, player.Guid.Full));

                    if (ShareLoot)
                    {
                        member.Session.Network.EnqueueSend(new GameMessageSystemChat($"You have lost permission to loot the kills of {player.Name}.", ChatMessageType.Broadcast));
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{member.Name} does not have permission to loot your kills.", ChatMessageType.Broadcast));
                    }
                }

                player.Fellowship = null;

                CalculateXPSharing();
            }
        }

        public void AssignNewLeader(Player oldLeader, Player newLeader)
        {
            if (newLeader != null)
            {
                FellowshipLeaderGuid = newLeader.Guid.Full;

                if (oldLeader != null)
                    oldLeader.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(oldLeader.Session, WeenieErrorWithString.YouHavePassedFellowshipLeadershipTo_, newLeader.Name));

                SendWeenieErrorWithStringAndUpdate(WeenieErrorWithString._IsNowLeaderOfFellowship, newLeader.Name);
            }
            else
            {
                // leader has dropped, assign new random leader
                var fellowshipMembers = GetFellowshipMembers();

                if (fellowshipMembers.Count == 0) return;

                var rng = ThreadSafeRandom.Next(0, fellowshipMembers.Count - 1);

                var fellowGuids = fellowshipMembers.Keys.ToList();

                FellowshipLeaderGuid = fellowGuids[rng];

                var newLeaderName = fellowshipMembers[FellowshipLeaderGuid].Name;

                if (oldLeader != null)
                    oldLeader.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(oldLeader.Session, WeenieErrorWithString.YouHavePassedFellowshipLeadershipTo_, newLeaderName));

                SendWeenieErrorWithStringAndUpdate(WeenieErrorWithString._IsNowLeaderOfFellowship, newLeaderName);
            }
        }

        public void UpdateOpenness(bool isOpen)
        {
            Open = isOpen;
            var openness = Open ? WeenieErrorWithString._IsNowOpenFellowship : WeenieErrorWithString._IsNowClosedFellowship;
            SendWeenieErrorWithStringAndUpdate(openness, FellowshipName);
        }

        public void UpdateLock(bool isLocked, string lockName)
        {
            // Unlocking a fellowship is not possible without disbanding in retail worlds, so in all likelihood, this is only firing for fellowships being locked by emotemanager

            IsLocked = isLocked;

            if (string.IsNullOrWhiteSpace(lockName))
                lockName = "Undefined";

            if (isLocked)
            {
                Open = false;

                DepartedMembers.Clear();

                var timestamp = Time.GetUnixTime();
                if (!FellowshipLocks.TryAdd(lockName, new FellowshipLockData(timestamp)))
                    FellowshipLocks[lockName].UpdateTimestamp(timestamp);

                SendBroadcastAndUpdate("Your fellowship is now locked.  You may not recruit new members.  If you leave the fellowship, you have 15 minutes to be recruited back into the fellowship.");
            }
            else
            {
                // Unlocking a fellowship is not possible without disbanding in retail worlds, so in all likelihood, this never occurs

                DepartedMembers.Clear();

                FellowshipLocks.Remove(lockName);

                SendBroadcastAndUpdate("Your fellowship is now unlocked.");
            }
        }

        /// <summary>
        /// Calculates fellowship XP sharing (ShareXP, EvenShare) from fellow levels
        /// </summary>
        private void CalculateXPSharing()
        {
            // WaffleACE: level-based sharing restrictions are OFF by default. Access to content is gated by
            // portal restrictions, not by who you are allowed to earn alongside, so a fellowship always shares
            // evenly regardless of the level spread between its members.
            //
            // This makes EvenShare permanently true, which means SplitXp always takes the GetMemberSharePercent
            // plateau branch - the level-proportional branch below it is unreachable while this is off. Set
            // 'fellowship_level_restrictions' to true to restore the retail behaviour described below.
            //
            // NOTE: with restrictions off, a low-level character sharing a high-level fellow's kills earns a
            // full even share. GetDistanceScalar still requires them to actually be present, and /fship noleech
            // (see 'fellowship_leech_check_default') is the intended counter to passive carrying.
            if (!PropertyManager.GetBool("fellowship_level_restrictions").Item)
            {
                ShareXP = DesiredShareXP;
                EvenShare = true;
                return;
            }

            // - If all members of the fellowship are level 50 or above, all members will share XP equally

            // - If all members of the fellowship are within 5 levels of the founder, XP will be shared equally

            // - If members are all within ten levels of the founder, XP will be shared proportionally.

            var fellows = GetFellowshipMembers();

            var allEvenShareLevel = PropertyManager.GetLong("fellowship_even_share_level").Item;
            var allOverEvenShareLevel = !fellows.Values.Any(f => (f.Level ?? 1) < allEvenShareLevel);

            if (allOverEvenShareLevel)
            {
                ShareXP = DesiredShareXP;
                EvenShare = true;
                return;
            }

            var leader = PlayerManager.GetOnlinePlayer(FellowshipLeaderGuid);
            if (leader == null)
                return;

            var maxLevelDiff = fellows.Values.Max(f => Math.Abs((leader.Level ?? 1) - (f.Level ?? 1)));

            if (maxLevelDiff <= 5)
            {
                ShareXP = DesiredShareXP;
                EvenShare = true;
            }
            else if (maxLevelDiff <= 10)
            {
                ShareXP = DesiredShareXP;
                EvenShare = false;
            }
            else
            {
                ShareXP = false;
                EvenShare = false;
            }
        }

        /// <summary>
        /// Splits XP amongst fellowship members, depending on XP type and fellow settings
        /// </summary>
        /// <param name="amount">The input amount of XP</param>
        /// <param name="xpType">The type of XP (quest XP is handled differently)</param>
        /// <param name="player">The fellowship member who originated the XP</param>
        public void SplitXp(ulong amount, XpType xpType, ShareType shareType, Player player)
        {
            // https://asheron.fandom.com/wiki/Announcements_-_2002/02_-_Fever_Dreams#Letter_to_the_Players_1

            var fellowshipMembers = GetFellowshipMembers();

            shareType &= ~ShareType.Fellowship;

            // WaffleACE: the earner is contributing XP to the fellowship — refresh their leech activity.
            StampContribution(player);

            // A kill is combat-sourced, so each receiving member's share (including a non-earner's, which is
            // typed XpType.Fellowship and so indistinguishable from shared quest XP) is eligible for that
            // member's own offline bonus. Quest XP shared into the fellowship is not.
            var combatShare = xpType == XpType.Kill;

            // quest turn-ins: flat share (retail default)
            if (xpType == XpType.Quest && !PropertyManager.GetBool("fellow_quest_bonus").Item)
            {
                var perAmount = (long)amount / fellowshipMembers.Count;

                foreach (var member in fellowshipMembers.Values)
                {
                    var fellowXpType = player == member ? XpType.Quest : XpType.Fellowship;

                    member.GrantXP(perAmount, fellowXpType, shareType, combatShare);
                }
            }

            // divides XP evenly to all the sharable fellows within level range,
            // but with a significant boost to the amount of xp, based on # of fellowship members
            else if (EvenShare)
            {
                var totalAmount = (ulong)Math.Round(amount * GetMemberSharePercent());

                foreach (var member in fellowshipMembers.Values)
                {
                    var scalar = GetDistanceScalar(player, member, xpType);
                    if (scalar <= 0)
                        continue;

                    var shareAmount = (ulong)Math.Round(totalAmount * scalar);

                    var fellowXpType = player == member ? xpType : XpType.Fellowship;

                    member.GrantXP((long)shareAmount, fellowXpType, shareType, combatShare);
                }

                return;
            }

            // divides XP to all sharable fellows within level range
            // based on each fellowship member's level
            else
            {
                var levelXPSum = fellowshipMembers.Values.Select(p => p.GetXPToNextLevel(p.Level.Value)).Sum();

                foreach (var member in fellowshipMembers.Values)
                {
                    var scalar = GetDistanceScalar(player, member, xpType);
                    if (scalar <= 0)
                        continue;

                    var levelXPScale = (double)member.GetXPToNextLevel(member.Level.Value) / levelXPSum;

                    var playerTotal = (ulong)Math.Round(amount * levelXPScale * scalar);

                    var fellowXpType = player == member ? xpType : XpType.Fellowship;

                    member.GrantXP((long)playerTotal, fellowXpType, shareType, combatShare);
                }
            }
        }

        /// <summary>
        /// Splits luminance amongst fellowship members, depending on XP type and fellow settings
        /// </summary>
        /// <param name="amount">The input amount of luminance</param>
        /// <param name="xpType">The type of lumaniance (quest luminance is handled differently)</param>
        /// <param name="player">The fellowship member who originated the luminance</param>
        public void SplitLuminance(ulong amount, XpType xpType, ShareType shareType, Player player)
        {
            // https://asheron.fandom.com/wiki/Announcements_-_2002/02_-_Fever_Dreams#Letter_to_the_Players_1

            shareType &= ~ShareType.Fellowship;

            // WaffleACE: the earner is contributing luminance to the fellowship — refresh their leech activity.
            StampContribution(player);

            if (xpType == XpType.Quest)
            {
                // quest luminance is not shared. NOTE: this is the one place luminance still diverges from
                // XP, whose quest branch flat-splits across the fellowship (gated by 'fellow_quest_bonus').
                player.GrantLuminance((long)amount, XpType.Quest, shareType);
            }
            else
            {
                // WaffleACE: mirrors SplitXp's EvenShare branch exactly - same group multiplier, same
                // distance model. This is structural, not cosmetic: the previous implementation divided a
                // *pot* (amount / total roster count) and then granted only to fellows in range, so any
                // out-of-range member's slice was silently destroyed rather than redistributed.
                //
                // Computing each member's share independently removes that failure mode by construction -
                // there is no pot to lose from. An out-of-range fellow simply scales to 0 and nobody else's
                // share is affected, which is exactly why SplitXp never had the bug.
                //
                // It also picks up two things luminance was missing: the fellowship group multiplier (so
                // luminance now benefits from fellowshipping the same way XP does), and GetDistanceScalar's
                // graduated falloff in place of the binary WithinRange radar check.
                //
                // Luminance banks straight into the uncapped bank with no luminance flag required, so
                // unflagged fellows share too (no MaximumLuminance != null gate).
                var fellowshipMembers = GetFellowshipMembers();

                if (fellowshipMembers.Count == 0)
                    return;

                // a kill is combat-sourced, so each receiving member's share is eligible for their own offline
                // bonus (this else branch is only reached for non-quest luminance)
                var combatShare = xpType == XpType.Kill;

                var totalAmount = (ulong)Math.Round(amount * GetMemberSharePercent());

                foreach (var member in fellowshipMembers.Values)
                {
                    var scalar = GetDistanceScalar(player, member, xpType);
                    if (scalar <= 0)
                        continue;

                    var shareAmount = (ulong)Math.Round(totalAmount * scalar);

                    var fellowXpType = player == member ? xpType : XpType.Fellowship;

                    member.GrantLuminance((long)shareAmount, fellowXpType, shareType, combatShare);
                }
            }
        }

        /// <summary>
        /// Retail per-member EvenShare XP multiplier, indexed by fellowship size (index 0 unused).
        ///
        /// Read it as total group throughput (size * share): 1.0, 1.5, 1.8, 2.2, 2.5, 2.7, 2.8, 2.8, 2.7.
        /// Retail deliberately *plateaus* the total around 2.8x at 7-8 fellows and eases back at 9 - adding
        /// a 9th member does not increase the group's aggregate XP, it only spreads it thinner. That plateau
        /// is the property <see cref="GetMemberSharePercent(int, double)"/> extends past 9.
        /// </summary>
        private static readonly double[] RetailSharePercent = { 0.0, 1.0, .75, .6, .55, .5, .45, .4, .35, .3 };

        /// <summary>
        /// Per-member EvenShare XP multiplier for a fellowship of <paramref name="memberCount"/>.
        ///
        /// Sizes 1-9 return the retail table verbatim. Past 9 the share is <paramref name="groupPlateau"/> /
        /// memberCount, which holds total group XP flat at the plateau while the per-head share keeps
        /// falling. With the default plateau of 2.7 (= 9 * 0.3) this is continuous at 9, so raising
        /// 'fellowship_max_members' cannot change what any fellowship of 9 or fewer already earns.
        ///
        /// NOTE: the old implementation was a switch with cases 1-9 that fell through to `return 1.0`, so a
        /// 10-member fellowship would have granted every member the *full* unsplit XP - a 10x group-XP
        /// exploit that armed itself the moment anyone raised the member cap. The formula below is total,
        /// so no roster size can reach that fallthrough.
        /// </summary>
        public static double GetMemberSharePercent(int memberCount, double groupPlateau)
        {
            if (memberCount <= 1)
                return 1.0;

            if (memberCount < RetailSharePercent.Length)
                return RetailSharePercent[memberCount];

            // guard a misconfigured (zero/negative) plateau rather than zeroing out everyone's XP
            if (groupPlateau <= 0.0)
                groupPlateau = RetailSharePercent[^1] * (RetailSharePercent.Length - 1);

            return groupPlateau / memberCount;
        }

        internal double GetMemberSharePercent()
        {
            var fellowshipMembers = GetFellowshipMembers();

            return GetMemberSharePercent(fellowshipMembers.Count, PropertyManager.GetDouble("fellowship_share_group_plateau").Item);
        }

        public const int MaxDistance = 600;

        /// <summary>
        /// Returns the amount to scale the XP for a fellow
        /// based on distance from the earner
        /// </summary>
        public double GetDistanceScalar(Player earner, Player fellow, XpType xpType)
        {
            if (earner == null || fellow == null)
                return 0.0f;

            if (xpType == XpType.Quest)
                return 1.0f;

            // https://asheron.fandom.com/wiki/Announcements_-_2004/01_-_Mirror,_Mirror#Rollout_Article

            // If they are indoors while you are outdoors, or vice-versa.
            if (earner.Location.Indoors != fellow.Location.Indoors)
                return 0.0f;

            // If you are both indoors but in different landblocks.
            if (earner.Location.Indoors && fellow.Location.Indoors && earner.Location.InstancedLandblock != fellow.Location.InstancedLandblock)
                return 0.0f;

            var dist = earner.Location.Distance2D(fellow.Location);

            if (dist >= MaxDistance * 2.0f)
                return 0.0f;

            if (dist <= MaxDistance)
                return 1.0f;

            var scalar = 1.0f - (dist - MaxDistance) / MaxDistance;

            return Math.Max(0.0f, scalar);
        }

        /// <summary>
        /// Returns fellows within radar range (75 units outdoors, 25 units indoors)
        /// </summary>
        public List<Player> WithinRange(Player player, bool includeSelf = false)
        {
            var fellows = GetFellowshipMembers();

            var landblockRange = PropertyManager.GetBool("fellow_kt_landblock").Item;

            var results = new List<Player>();

            foreach (var fellow in fellows.Values)
            {
                if (player == fellow && !includeSelf)
                    continue;

                var shareable = player == fellow || landblockRange ?
                    player.CurrentLandblock == fellow.CurrentLandblock || player.Location.DistanceTo(fellow.Location) <= 192.0f :
                    player.Location.Distance2D(fellow.Location) <= player.CurrentRadarRange && player.ObjMaint.VisibleObjectsContainsKey(fellow.Guid.Full);      // 2d visible distance / radar range?

                if (shareable)
                    results.Add(fellow);
            }
            return results;
        }

        /// <summary>
        /// Called when someone in the fellowship levels up
        /// </summary>
        public void OnFellowLevelUp(Player player)
        {
            CalculateXPSharing();

            var fellowshipMembers = GetFellowshipMembers();

            foreach (var fellow in fellowshipMembers.Values)
            {
                if (fellow == player)
                    continue;

                fellow.Session.Network.EnqueueSend(new GameMessageSystemChat($"{player.Name} is now level {player.Level}!", ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// WaffleACE: guids of members whose vitals changed since the last flush. Guarded by
        /// <see cref="vitalLock"/> because members on different landblocks tick on different threads.
        /// </summary>
        private readonly HashSet<uint> vitalDirty = new HashSet<uint>();
        private readonly object vitalLock = new object();

        /// <summary>
        /// WaffleACE: flag a member's vitals as changed. Replaces the old OnVitalUpdate, which sent a message
        /// to every panel-open fellow immediately from Player_Tick.
        ///
        /// That was the single worst scaling cost in the fellowship system: Player.UpdateVital sets the dirty
        /// flag on *any* vital change (every damage tick, heal, and stamina drain), and Player_Tick runs at the
        /// 60Hz world tick, so traffic was n * n_panelOpen messages per tick with no upper bound - O(n^2) at
        /// up to 60Hz. Now the work is coalesced and flushed on a fixed cadence by FellowshipManager.Tick.
        /// </summary>
        public void MarkVitalDirty(Player player)
        {
            if (player == null)
                return;

            lock (vitalLock)
                vitalDirty.Add(player.Guid.Full);
        }

        /// <summary>
        /// WaffleACE: send one vitals update per (changed member, viewing member) pair and reset the dirty set.
        /// Called on the 'fellowship_vital_update_interval' cadence from FellowshipManager.Tick().
        ///
        /// This deliberately still sends the same GameEventFellowshipUpdateFellow the client already expects
        /// for vitals - only the *rate* changes, so there is no client-side behaviour risk. (Collapsing to a
        /// single GameEventFellowshipFullUpdate per viewer would be fewer messages still, but that is the
        /// whole-roster refresh event and risks disturbing panel state, for no meaningful extra saving.)
        /// </summary>
        public void FlushVitalUpdates()
        {
            uint[] dirty;

            lock (vitalLock)
            {
                if (vitalDirty.Count == 0)
                    return;

                dirty = vitalDirty.ToArray();
                vitalDirty.Clear();
            }

            var members = GetFellowshipMembers();

            // only members with the panel open can see vitals, so with no viewers the whole sweep is free
            var viewers = members.Values.Where(m => m.FellowshipPanelOpen).ToList();

            if (viewers.Count == 0)
                return;

            foreach (var guid in dirty)
            {
                if (!members.TryGetValue(guid, out var subject))
                    continue;

                foreach (var viewer in viewers)
                    viewer.Session.Network.EnqueueSend(new GameEventFellowshipUpdateFellow(viewer.Session, subject, ShareLoot, FellowUpdateType.Vitals));
            }
        }

        public void OnDeath(Player player)
        {
            var fellowshipMembers = GetFellowshipMembers();

            foreach (var fellow in fellowshipMembers.Values)
            {
                if (fellow != player)
                    fellow.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your fellow {player.Name} has died!", ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// WaffleACE: seconds remaining before a leech-booted player may rejoin this fellowship.
        /// Returns 0 if the player was never booted or their lockout has expired (expired entries are pruned).
        /// </summary>
        public double GetLeechLockoutRemaining(Player player)
        {
            if (player == null || LeechBoots == null || !LeechBoots.TryGetValue(player.Guid.Full, out var bootTime))
                return 0;

            var lockout = PropertyManager.GetLong("fellowship_leech_rejoin_lockout").Item;
            var remaining = bootTime + lockout - Time.GetUnixTime();

            if (remaining <= 0)
            {
                LeechBoots.Remove(player.Guid.Full);
                return 0;
            }

            return remaining;
        }

        /// <summary>
        /// WaffleACE: whole minutes for player-facing lockout messages, rounded up so "1 minute" never means "already expired".
        /// </summary>
        public static int LockoutMinutes(double seconds) => (int)Math.Ceiling(seconds / 60);

        /// <summary>
        /// WaffleACE: refresh a member's leech-activity timestamp because they contributed XP/luminance.
        /// </summary>
        public void StampContribution(Player player)
        {
            if (player == null || LeechActivity == null)
                return;

            LeechActivity[player.Guid.Full] = Time.GetUnixTime();
        }

        /// <summary>
        /// WaffleACE: called on a throttled cadence by FellowshipManager.Tick().
        /// Ejects members whose last XP contribution has gone stale past the timeout when leech management
        /// is enabled, and returns the number of live members remaining (0 => the fellowship can be pruned).
        /// </summary>
        public int OnTick()
        {
            var members = GetFellowshipMembers();

            if (members.Count == 0)
                return 0;

            var now = Time.GetUnixTime();

            // seed a grace timestamp for any member we haven't seen yet (e.g. joined between ticks). Activity
            // is only ever refreshed by an XP/luminance contribution (StampContribution) — sharing a landblock
            // does NOT keep a member safe, so a present-but-idle member still times out as a leech.
            foreach (var member in members.Values)
            {
                if (!LeechActivity.ContainsKey(member.Guid.Full))
                    LeechActivity[member.Guid.Full] = now;
            }

            // prune activity entries for members who have left
            foreach (var staleGuid in LeechActivity.Keys.Where(k => !members.ContainsKey(k)).ToList())
                LeechActivity.Remove(staleGuid);

            var rejoinLockout = PropertyManager.GetLong("fellowship_leech_rejoin_lockout").Item;

            // prune expired boot entries so the dictionary doesn't grow for the fellowship's lifetime
            foreach (var expiredGuid in LeechBoots.Where(kvp => now - kvp.Value > rejoinLockout).Select(kvp => kvp.Key).ToList())
                LeechBoots.Remove(expiredGuid);

            if (LeechManagementEnabled)
            {
                var timeout = PropertyManager.GetLong("fellowship_leech_timeout").Item;
                var leader = PlayerManager.GetOnlinePlayer(FellowshipLeaderGuid);

                foreach (var member in members.Values.ToList())
                {
                    if (member.Guid.Full == FellowshipLeaderGuid)   // never auto-eject the leader
                        continue;

                    if (!LeechActivity.TryGetValue(member.Guid.Full, out var lastActive))
                        continue;

                    if (now - lastActive <= timeout)
                        continue;

                    LeechBoots[member.Guid.Full] = now;

                    member.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"You have been removed from the fellowship for inactivity - no XP contribution to the fellowship for over {timeout / 60} minute(s). You may not rejoin this fellowship for {LockoutMinutes(rejoinLockout)} minute(s).",
                        ChatMessageType.Fellowship));

                    RemoveFellowshipMember(member, leader);
                }

                members = GetFellowshipMembers();
            }

            return members.Count;
        }

        public Dictionary<uint, Player> GetFellowshipMembers()
        {
            var results = new Dictionary<uint, Player>();
            var dropped = new HashSet<uint>();

            foreach (var kvp in FellowshipMembers)
            {
                var playerGuid = kvp.Key;
                var playerRef = kvp.Value;

                playerRef.TryGetTarget(out var player);

                if (player != null && player.Session != null && player.Session.Player != null && player.Fellowship != null)
                    results.Add(playerGuid, player);
                else
                    dropped.Add(playerGuid);
            }

            // TODO: process dropped list
            if (dropped.Count > 0)
                ProcessDropList(FellowshipMembers, dropped);

            return results;
        }

        public void ProcessDropList(Dictionary<uint, WeakReference<Player>> fellowshipMembers, HashSet<uint> fellowGuids)
        {
            foreach (var fellowGuid in fellowGuids)
            {
                var offlinePlayer = PlayerManager.FindByGuid(fellowGuid);
                var offlineName = offlinePlayer != null ? offlinePlayer.Name : "NULL";

                log.Warn($"Dropped fellow: {offlineName}");
                fellowshipMembers.Remove(fellowGuid);
            }
            if (fellowGuids.Contains(FellowshipLeaderGuid))
                AssignNewLeader(null, null);

            CalculateXPSharing();
            UpdateAllMembers();
        }
    }

    public static class FellowshipExtensions
    {
        private static readonly HashComparer hashComparer = new HashComparer(32);

        public static void Write(this BinaryWriter writer, Dictionary<uint, int> departedFellows)
        {
            PackableHashTable.WriteHeader(writer, departedFellows.Count, hashComparer.NumBuckets);

            var sorted = new SortedDictionary<uint, int>(departedFellows, hashComparer);

            foreach (var departed in sorted)
            {
                writer.Write(departed.Key);
                writer.Write(departed.Value);
            }
        }
    }
}
