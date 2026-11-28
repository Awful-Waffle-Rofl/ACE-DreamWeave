using System;
using System.Collections.Generic;

using ACE.Server.Avatars;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Who hears a run's announcements. An ordinary run is heard server-wide; a quiet private Avatar event (Avatar phase 1,
    /// Task 12) is heard only by the Avatar's allegiance. Payout and participation are not touched by the announcements. Rewards: a quiet private event pays its members only (see <see cref="ExcludesFromRewards"/>).
    /// </summary>
    public sealed partial class WorldEvent
    {
        /// <summary>Copied from the request: false, with <see cref="AvatarId"/> set, keeps this run's announcements to the Avatar's allegiance.</summary>
        public bool AnnounceServerWide { get; }

        private readonly ulong avatarFavorPrice;
        private bool avatarFavorSettled;

        /// <summary>
        /// Settles the Favor and the daily claim an Avatar's private event reserved, from <see cref="Transition"/> (the one place
        /// State is written): committed exactly once when the run first reaches Active, released exactly once if it ends
        /// (Resolved, Cleanup or Done) without ever having been Active, so an event that never started is free and does not use
        /// the day's slot. Runs at a price of 0 too: the daily claim must settle whatever the price, or a free event would hold
        /// its claim until a restart. Reservations and claims are memory only, so a restart before Active releases them
        /// implicitly.
        /// </summary>
        private void SettleAvatarFavor(WorldEventState to)
        {
            if (AvatarId == null || avatarFavorSettled)
                return;

            bool commit;

            if (to == WorldEventState.Active)
                commit = true;
            else if (to == WorldEventState.Resolved || to == WorldEventState.Cleanup || to == WorldEventState.Done)
                commit = false;
            else
                return;

            avatarFavorSettled = true;

            try
            {
                WorldEventHooks.AvatarFavorSettleSink(AvatarId.Value, avatarFavorPrice, commit);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] Run {RunId}: settling the Avatar's Favor ({(commit ? "commit" : "release")}) threw", ex);
            }
        }

        /// <summary>
        /// Send point for a run's HEADLINE lines (teaser, start, active, time-left, outcome, fail, death protection). A quiet
        /// Avatar event sends them to the Avatar's allegiance only, in the allegiance look, and never to Discord. Every other
        /// run - public, or a private run that was bought announced - goes through <see cref="WorldEventAnnouncer.Broadcast"/>
        /// unchanged, so the announced price still buys the server-wide headline and the Discord teaser. World thread.
        /// </summary>
        internal void Announce(string msg, bool relayToDiscord = false)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            if (IsQuietPrivate)
            {
                SendToAllegiance(msg);
                return;
            }

            WorldEventAnnouncer.Broadcast(msg, relayToDiscord);
        }

        /// <summary>
        /// Send point for an IN-RUN line that is world-wide for a public run (boss arrived, boss health, objective lines): a
        /// public run broadcasts it as before; a private run sends it to the allegiance plus, when it was bought announced, the
        /// radius-local players (<see cref="AnnounceToAllegianceAndLocal"/>). World thread.
        /// </summary>
        internal void AnnounceInRunWide(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            if (AvatarId != null)
                AnnounceToAllegianceAndLocal(msg);
            else
                WorldEventAnnouncer.Broadcast(msg);
        }

        /// <summary>
        /// Send point for an IN-RUN line meant for the people around the anchor (waves, local objective lines). A public run
        /// sends it radius-local exactly as before; a quiet private run sends it to the allegiance only; an announced private
        /// run sends it to the allegiance plus the radius-local players. The ONLY place outside WorldEventAnnouncer that calls
        /// <see cref="WorldEventAnnouncer.Local"/>. World thread.
        /// </summary>
        internal void AnnounceLocal(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            if (AvatarId != null)
                AnnounceToAllegianceAndLocal(msg);
            else
                WorldEventAnnouncer.LocalAnnounceSink(this, msg, null);
        }

        /// <summary>The last monarch known to this run: the purchase's, then each one an allegiance send or the snapshot resolved (world thread only).</summary>
        private uint? lastMonarch;

        private bool IsQuietPrivate => AvatarId != null && !AnnounceServerWide;

        /// <summary>
        /// The Avatar's CURRENT monarch (the monarch can change mid-event), falling back to the last one this run resolved (seeded from
        /// <see cref="WorldEventRequest.AvatarMonarchGuid"/> at construction) so an Avatar unbound mid-run (absorbed or retired,
        /// MonarchGuid cleared) still has an allegiance. Null for a non-Avatar run or when none is known. Remembers what it resolves.
        /// It writes <see cref="lastMonarch"/>, so it is called from the world thread only (announcements and the Rewarding snapshot);
        /// the claim path never calls it and reads only the immutable <see cref="rewardMembers"/> snapshot.
        /// </summary>
        private uint? ResolveAvatarMonarch()
        {
            if (AvatarId == null)
                return null;

            var monarch = AvatarManager.Get(AvatarId.Value)?.MonarchGuid ?? lastMonarch;

            if (monarch != null)
                lastMonarch = monarch;

            return monarch;
        }

        /// <summary>The characters who may take a quiet private event's reward, frozen at Rewarding entry. Built whole, then published once; null until taken.</summary>
        private volatile HashSet<uint> rewardMembers;

        /// <summary>
        /// Freezes, once, who a quiet private Avatar event pays: every character of the Avatar's monarch's allegiance (its roster, the
        /// monarch, and every participant who reads as sworn to that monarch) per <see cref="WorldEventHooks.CharacterMonarchSource"/>.
        /// Membership is as of the END of the fight (Rewarding entry): someone who swears in before then is paid, someone who swears in
        /// or leaves afterwards changes nothing. A no-op for any other run and on every call after the first.
        /// If the Avatar's monarch changed mid-event and the manager has not rebound yet, the resolved (old) monarch yields no members;
        /// the snapshot then retries once with the monarch most participants report (<see cref="MostCommonParticipantMonarch"/>).
        /// If no members are found at all, or a read throws, it freezes an EMPTY set: nobody is confirmed, so everybody is excluded
        /// (fail closed), and the empty case is logged at Error.
        /// Called from Finish at Rewarding entry, beside <see cref="DealLootPool"/>, on the world thread.
        /// </summary>
        internal void SnapshotRewardMembers()
        {
            if (!IsQuietPrivate || rewardMembers != null)
                return;

            var members = new HashSet<uint>();
            uint? monarch = null;

            try
            {
                monarch = ResolveAvatarMonarch();

                if (monarch != null)
                    members = MembersOf(monarch.Value);

                if (members.Count == 0)
                {
                    var successor = MostCommonParticipantMonarch();

                    if (successor != null && successor != monarch)
                    {
                        members = MembersOf(successor.Value);
                        log.Warn($"[WORLDEVENT] Run {RunId}: quiet private Avatar event {AvatarId} resolved monarch {monarch?.ToString() ?? "none"} with no members; using the participants' monarch {successor}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] Run {RunId}: freezing the reward members threw; no one is a member", ex);
                members = new HashSet<uint>();
            }

            if (members.Count == 0)
                log.Error($"[WORLDEVENT] Run {RunId}: quiet private Avatar event {AvatarId} froze NO reward members (monarch {monarch?.ToString() ?? "unknown"}); every claim is withheld");

            rewardMembers = members;
            log.Info($"[WORLDEVENT] Run {RunId}: quiet private event reward members frozen: {members.Count}");
        }

        /// <summary>The characters, among the monarch's roster, the monarch and every participant, that <see cref="WorldEventHooks.CharacterMonarchSource"/> places under <paramref name="monarch"/>.</summary>
        private HashSet<uint> MembersOf(uint monarch)
        {
            var candidates = new HashSet<uint>(WorldEventHooks.AllegianceMemberSource(monarch) ?? Array.Empty<uint>()) { monarch };

            foreach (var record in Participation.Records)
                candidates.Add(record.CharacterGuid);

            var members = new HashSet<uint>();

            foreach (var guid in candidates)
            {
                if (WorldEventHooks.CharacterMonarchSource(guid) == monarch)
                    members.Add(guid);
            }

            return members;
        }

        /// <summary>
        /// The monarch guid most participants report through <see cref="WorldEventHooks.CharacterMonarchSource"/> (ties: the lower guid), ignoring a
        /// participant whose answer is their own guid unless another participant reports it too (a solo character is not a monarch to adopt).
        /// Null when there is no participant or no such value.
        /// </summary>
        private uint? MostCommonParticipantMonarch()
        {
            var counts = new Dictionary<uint, int>();
            var selfOnly = new Dictionary<uint, bool>();

            foreach (var record in Participation.Records)
            {
                var reported = WorldEventHooks.CharacterMonarchSource(record.CharacterGuid);
                counts[reported] = counts.TryGetValue(reported, out var n) ? n + 1 : 1;
                selfOnly[reported] = (!selfOnly.TryGetValue(reported, out var only) || only) && reported == record.CharacterGuid;
            }

            uint? best = null;
            var bestCount = 0;

            foreach (var kv in counts)
            {
                if (kv.Value == 1 && selfOnly[kv.Key])
                    continue;

                if (kv.Value > bestCount || (kv.Value == bestCount && best != null && kv.Key < best.Value))
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }

            return best;
        }

        /// <summary>
        /// True when this run withholds its event rewards (the cache payout, extras and the pooled-loot share) from
        /// <paramref name="characterGuid"/>: only a QUIET private Avatar event does, and only from a character not in the members
        /// frozen at Rewarding entry (owner ruling 2026-11-26). Announced private events and public runs withhold from nobody.
        /// A quiet private run with no snapshot yet fails closed. Reads only the immutable snapshot, so any thread may call it.
        /// </summary>
        internal bool ExcludesFromRewards(uint characterGuid)
        {
            if (!IsQuietPrivate)
                return false;

            var members = rewardMembers;

            return members == null || !members.Contains(characterGuid);
        }

        private readonly HashSet<uint> rewardExclusionNotified = new HashSet<uint>();

        /// <summary>True the first time it is called for <paramref name="characterGuid"/>: the excluded line is sent once per character per run.</summary>
        internal bool MarkRewardExclusionNotified(uint characterGuid)
        {
            lock (rewardExclusionNotified)
                return rewardExclusionNotified.Add(characterGuid);
        }

        /// <summary>A private run's in-run line: the allegiance always, plus the radius-local copy only for an announced run (outsiders were invited).</summary>
        private void AnnounceToAllegianceAndLocal(string msg)
        {
            var reached = SendToAllegiance(msg);

            if (IsQuietPrivate)
                return;

            // Skip anyone the allegiance send already reached so a member near the anchor hears each line once.
            try
            {
                WorldEventAnnouncer.LocalAnnounceSink(this, msg, reached);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] Run {RunId}: local announcement failed", ex);
            }
        }

        /// <summary>
        /// Resolves the CURRENT monarch at send time (the monarch can change mid-event) and sends the line, in the allegiance
        /// look, through <see cref="WorldEventAnnouncer.AllegianceSink"/>. With no allegiance the line is logged only. Returns
        /// the guids reached (null when none).
        /// </summary>
        private ISet<uint> SendToAllegiance(string msg)
        {
            // Falls back to the last monarch this run resolved, so an Avatar unbound mid-run (absorbed or retired, MonarchGuid
            // cleared) does not leave the run's remaining lines with nobody to hear them.
            var monarch = ResolveAvatarMonarch();

            if (monarch == null)
            {
                log.Info($"[WORLDEVENT] Run {RunId}: private Avatar event line has no allegiance to send to, Avatar {AvatarId} has no monarch: {msg}");
                return null;
            }

            try
            {
                return WorldEventAnnouncer.AllegianceSink(monarch.Value, WorldEventAnnouncer.AllegianceLook(msg));
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] Run {RunId}: allegiance announcement failed", ex);
                return null;
            }
        }
    }
}
