using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One seat offered to <see cref="ThreadDungeonRun"/>'s roster constructor: who, and the values captured
    /// for them at lock time. Plain values, so a roster can be formed and tested without a Player.
    /// </summary>
    public sealed class RosterSeat
    {
        public RosterSeat(uint guid, string name, uint accountId, int level)
        {
            Guid = guid;
            Name = name;
            AccountId = accountId;
            Level = level;
        }

        public uint Guid { get; }
        public string Name { get; }
        public uint AccountId { get; }
        public int Level { get; }
    }

    /// <summary>One locked roster member. Immutable; the mutable per-member state lives inside the run.</summary>
    public sealed class ThreadRosterMember
    {
        internal ThreadRosterMember(uint guid, string name, uint accountId, bool isOwner, int levelAtStart)
        {
            Guid = guid;
            Name = name;
            AccountId = accountId;
            IsOwner = isOwner;
            LevelAtStart = levelAtStart;
        }

        public uint Guid { get; }
        public string Name { get; }
        public uint AccountId { get; }
        public bool IsOwner { get; }
        public int LevelAtStart { get; }
    }

    /// <summary>One materialised item waiting in a member's held pile, tagged with the deal round that put it there.</summary>
    public sealed class HeldPileItem
    {
        public HeldPileItem(WorldObject item, string rareFinderName, int round, IReadOnlyList<int> mergedRounds = null)
        {
            Item = item;
            RareFinderName = rareFinderName;
            Round = round;
            MergedRounds = mergedRounds;
        }

        public WorldObject Item { get; }
        public string RareFinderName { get; }
        public int Round { get; }

        /// <summary>
        /// Other deal rounds absorbed into this item by whole-pile consolidation (ThreadLootStacking.ConsolidatePile,
        /// Threads item 8 round 2, 2026-09-22), alongside <see cref="Round"/>; null when nothing was merged in. Every
        /// round here must still be marked received when this item lands (ruling R21), same as <see cref="Round"/>.
        /// </summary>
        public IReadOnlyList<int> MergedRounds { get; }
    }

    /// <summary>A point-in-time copy of one member's state, for the telemetry row. Detached from the run.</summary>
    public sealed class RosterMemberSnapshot
    {
        public uint Guid;
        public string Name;
        public bool IsOwner;
        public int LevelAtStart;
        public int LevelEnd;
        public bool Entered;
        public int SecondsInside;
        public long XpGained;
        public long LumGained;
        public int PilesReceived;
        public int PilesForfeited;
        public bool SurveyFiled;
        public bool KeyGranted;

        /// <summary>Removed from the run by the puzzle fail policy (ThreadDungeonRun.MarkPuzzleRemoved): no survey, no clear count.</summary>
        public bool PuzzleRemoved;
    }

    /// <summary>
    /// The roster half of a run (Group Threads). A separate partial file so the per-member state and its
    /// invariants read in one place.
    ///
    /// Threading. The roster itself (<see cref="Roster"/> and the guid-keyed member dictionary) is built once in
    /// the constructor and never structurally modified afterwards, so membership lookups take no lock. Every
    /// mutable per-member field is guarded by the run's private stateLock, EXCEPT the per-member XP and
    /// luminance counters, which use Interlocked for the reason the class note gives for XpEarned: they sit on
    /// the kill-grant path and no decision reads them together with other state.
    ///
    /// <see cref="FellowshipAtLock"/> follows the ExitTo convention: written once by TryStart before the run is
    /// published, never re-assigned.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        /// <summary>Mutable state of one member. Fields other than Xp and Lum are read and written under stateLock only.</summary>
        private sealed class MemberState
        {
            public MemberState(ThreadRosterMember member) => Member = member;

            public readonly ThreadRosterMember Member;
            public uint KeyGem;
            public bool FreeKeyEntryClaimed;
            public bool Entered;
            public int SecondsInside;
            public long Xp;
            public long Lum;
            public int LevelEnd;
            public bool SurveyFiled;
            public bool FellowshipWarned;
            public bool DeliveryWanted;
            public readonly List<HeldPileItem> HeldPile = new List<HeldPileItem>();
            public readonly HashSet<int> RoundsReceived = new HashSet<int>();
            public readonly HashSet<int> RoundsForfeited = new HashSet<int>();
        }

        private IReadOnlyList<ThreadRosterMember> roster;
        private Dictionary<uint, MemberState> members;

        private bool groupResolved;
        private double groupHealthMult = 1.0;
        private double groupCountActual = 1.0;
        private int dealRound;

        /// <summary>The locked roster: owner first, then the other members by ascending character guid (invariant 4).</summary>
        public IReadOnlyList<ThreadRosterMember> Roster => roster;

        /// <summary>The lock-time group scaling snapshot. Never null; <see cref="GroupScaling.Solo"/> for a solo run.</summary>
        public GroupScaling Group { get; }

        public bool IsGroup => Group.IsGroup;

        /// <summary>The owner's fellowship at lock. Set once by TryStart before publication; never re-assigned.</summary>
        public Fellowship FellowshipAtLock { get; set; }

        /// <summary>
        /// Owner first, then the rest by ascending guid, null seats and repeated guids dropped. Shared by the
        /// constructor so the roster order has exactly one definition.
        /// </summary>
        internal static List<RosterSeat> OrderRoster(IReadOnlyList<RosterSeat> seats)
        {
            var ordered = new List<RosterSeat>();
            if (seats == null || seats.Count == 0 || seats[0] == null)
                return ordered;

            var owner = seats[0];
            ordered.Add(owner);

            var seen = new HashSet<uint> { owner.Guid };

            foreach (var seat in seats.Skip(1).Where(s => s != null).OrderBy(s => s.Guid))
            {
                if (seen.Add(seat.Guid))
                    ordered.Add(seat);
            }

            return ordered;
        }

        private void InitRoster(List<RosterSeat> ordered)
        {
            var list = new List<ThreadRosterMember>(ordered.Count);
            var dict = new Dictionary<uint, MemberState>(ordered.Count);

            for (var i = 0; i < ordered.Count; i++)
            {
                var seat = ordered[i];
                var member = new ThreadRosterMember(seat.Guid, seat.Name, seat.AccountId, i == 0, seat.Level);
                list.Add(member);
                dict[seat.Guid] = new MemberState(member);
            }

            roster = list.AsReadOnly();
            members = dict;
        }

        public bool IsRosterMember(uint guid) => members.ContainsKey(guid);

        /// <summary>The roster entry for <paramref name="guid"/>, or null for a non-member.</summary>
        public ThreadRosterMember GetMember(uint guid) => members.TryGetValue(guid, out var ms) ? ms.Member : null;

        /// <summary>
        /// Is this gem one of the run's: the owner's gem or any member's key (invariant 6)? A key guid of 0 means
        /// "no key" and never matches.
        /// </summary>
        public bool IsRunGem(uint gemGuid)
        {
            if (gemGuid == GemGuid)
                return true;

            if (gemGuid == 0)
                return false;

            lock (stateLock)
            {
                foreach (var ms in members.Values)
                {
                    if (ms.KeyGem == gemGuid)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Records a member's key gem guid (0 clears it). Ignored for the owner, whose gem is GemGuid, and for non-members.</summary>
        public void SetMemberKey(uint memberGuid, uint keyGemGuid)
        {
            if (!members.TryGetValue(memberGuid, out var ms) || ms.Member.IsOwner)
                return;

            lock (stateLock)
                ms.KeyGem = keyGemGuid;
        }

        /// <summary>The member's key gem guid; 0 for the owner, a keyless member or a non-member.</summary>
        public uint MemberKeyGem(uint memberGuid)
        {
            if (!members.TryGetValue(memberGuid, out var ms))
                return 0;

            lock (stateLock)
                return ms.KeyGem;
        }

        /// <summary>
        /// The deal seats, in roster order. The owner always holds one; a non-owner holds one only while
        /// <see cref="MemberKeyGem"/> is non-zero, so a member who never got a key - offline at lock, or a failed
        /// mint - is dealt nothing, receives no boss bonus and owes nothing to the reap hold. Read once per deal.
        ///
        /// This used to add "or who gave theirs to the Fragment Press", which was true under rulings R7/R32 and is
        /// now unreachable for a LIVE run: since the 2026-09-17 reversal, giving a key to the press while the run is
        /// live ends the run outright, so there is no later deal for that member to be missing from. A key handed
        /// over after the run is dead still clears its record, but nothing deals from a dead run.
        /// </summary>
        public IReadOnlyList<uint> DealSeats()
        {
            var seats = new List<uint>(roster.Count);

            lock (stateLock)
            {
                foreach (var member in roster)
                {
                    // A member the puzzle fail policy removed holds no seat, owner included (ThreadPuzzleFailPolicy).
                    if (puzzleRemoved.Contains(member.Guid))
                        continue;

                    if (member.IsOwner || members[member.Guid].KeyGem != 0)
                        seats.Add(member.Guid);
                }
            }

            return seats;
        }

        /// <summary>Every (member, key) pair with a key, in roster order.</summary>
        public IReadOnlyList<(uint MemberGuid, uint KeyGemGuid)> MemberKeys()
        {
            var keys = new List<(uint MemberGuid, uint KeyGemGuid)>();

            lock (stateLock)
            {
                foreach (var member in roster)
                {
                    var key = members[member.Guid].KeyGem;
                    if (key != 0)
                        keys.Add((member.Guid, key));
                }
            }

            return keys;
        }

        /// <summary>
        /// Ruling R8: the first use of each member's key is free. True exactly once per non-owner member; always
        /// false for the owner (whose gem spends entries as it always has) and for a non-member.
        /// </summary>
        public bool TryClaimFreeKeyEntry(uint memberGuid)
        {
            if (!members.TryGetValue(memberGuid, out var ms) || ms.Member.IsOwner)
                return false;

            lock (stateLock)
            {
                if (ms.FreeKeyEntryClaimed)
                    return false;

                ms.FreeKeyEntryClaimed = true;
                return true;
            }
        }

        /// <summary>Latches that the member has been seen inside the copy. Never cleared.</summary>
        public void MarkMemberEntered(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return;

            lock (stateLock)
                ms.Entered = true;
        }

        public bool MemberEntered(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
                return ms.Entered;
        }

        /// <summary>Adds presence-sampled seconds. Non-positive amounts are ignored; the total saturates at int.MaxValue.</summary>
        public void AddMemberSecondsInside(uint guid, int seconds)
        {
            if (seconds <= 0 || !members.TryGetValue(guid, out var ms))
                return;

            lock (stateLock)
                ms.SecondsInside = (int)Math.Min((long)ms.SecondsInside + seconds, int.MaxValue);
        }

        /// <summary>Per-character XP banked inside this run. Interlocked, outside stateLock (see the file note).</summary>
        public void AddMemberXp(uint guid, long amount)
        {
            if (amount != 0 && members.TryGetValue(guid, out var ms))
                Interlocked.Add(ref ms.Xp, amount);
        }

        /// <summary>Per-character luminance banked inside this run. Same contract as <see cref="AddMemberXp"/>.</summary>
        public void AddMemberLum(uint guid, long amount)
        {
            if (amount != 0 && members.TryGetValue(guid, out var ms))
                Interlocked.Add(ref ms.Lum, amount);
        }

        public void SetMemberLevelEnd(uint guid, int level)
        {
            if (!members.TryGetValue(guid, out var ms))
                return;

            lock (stateLock)
                ms.LevelEnd = level;
        }

        /// <summary>Latches that this member's survey was actually filed.</summary>
        public void MarkMemberSurveyFiled(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return;

            lock (stateLock)
                ms.SurveyFiled = true;
        }

        /// <summary>True exactly once per member: the caller that gets true sends the one-time fellowship warning.</summary>
        public bool TryMarkFellowshipWarned(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
            {
                if (ms.FellowshipWarned)
                    return false;

                ms.FellowshipWarned = true;
                return true;
            }
        }

        /// <summary>H, stamped once by the spawner. 1.0 until <see cref="MarkGroupResolved"/> has run.</summary>
        public double GroupHealthMult { get { lock (stateLock) return groupHealthMult; } }

        /// <summary>C_actual, stamped once by the spawner. 1.0 until <see cref="MarkGroupResolved"/> has run.</summary>
        public double GroupCountActual { get { lock (stateLock) return groupCountActual; } }

        /// <summary>
        /// Write-once. A call with either input NaN, infinite or &lt;= 0 is ignored entirely and does NOT consume
        /// the write, so a later valid call still lands.
        /// </summary>
        public void MarkGroupResolved(double countActual, double healthMult)
        {
            if (!IsPositiveFinite(countActual) || !IsPositiveFinite(healthMult))
                return;

            lock (stateLock)
            {
                if (groupResolved)
                    return;

                groupResolved = true;
                groupCountActual = countActual;
                groupHealthMult = healthMult;
            }
        }

        /// <summary>Loot roll factor per trash or elite kill: H * B. Exactly 1.0 for a solo run.</summary>
        public double TrashLootFactor => Group.TrashLootFactor(GroupHealthMult);

        /// <summary>Loot roll factor per boss kill: E * B. Exactly 1.0 for a solo run.</summary>
        public double BossLootFactor => Group.BossLootFactor();

        /// <summary>Hands out the next deal round: 1, 2, 3, ...</summary>
        public int NextDealRound()
        {
            lock (stateLock)
                return ++dealRound;
        }

        /// <summary>
        /// Adds one item to a member's held pile. False for a null item, a non-member, or once the run has Ended;
        /// the caller then owns the item and destroys it.
        /// </summary>
        public bool AddToHeldPile(uint guid, HeldPileItem item)
        {
            if (item?.Item == null || !members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
            {
                if (state == ThreadDungeonRunState.Ended)
                    return false;

                ms.HeldPile.Add(item);
                return true;
            }
        }

        /// <summary>Removes and returns the member's whole held pile, in insertion order. Empty for a non-member.</summary>
        public List<HeldPileItem> TakeHeldPile(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return new List<HeldPileItem>();

            lock (stateLock)
            {
                var taken = new List<HeldPileItem>(ms.HeldPile);
                ms.HeldPile.Clear();
                return taken;
            }
        }

        public bool HasHeldPile(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
                return ms.HeldPile.Count > 0;
        }

        public bool AnyHeldPile
        {
            get
            {
                lock (stateLock)
                    return members.Values.Any(s => s.HeldPile.Count > 0);
            }
        }

        /// <summary>Records that at least one item from deal round <paramref name="round"/> reached this member's cache (ruling R21).</summary>
        public void MarkPileReceived(uint guid, int round)
        {
            if (!members.TryGetValue(guid, out var ms))
                return;

            lock (stateLock)
                ms.RoundsReceived.Add(round);
        }

        /// <summary>
        /// Run end. Records every distinct round still held as forfeited for its member, empties every pile,
        /// clears every delivery-wanted flag (there is nothing left to deliver), and returns the items, which are
        /// out of the world, for the caller to destroy.
        /// </summary>
        public List<WorldObject> DrainHeldPiles()
        {
            var items = new List<WorldObject>();

            lock (stateLock)
            {
                foreach (var member in roster)
                {
                    var ms = members[member.Guid];

                    foreach (var held in ms.HeldPile)
                    {
                        ms.RoundsForfeited.Add(held.Round);
                        if (held.Item != null)
                            items.Add(held.Item);
                    }

                    ms.HeldPile.Clear();
                    ms.DeliveryWanted = false;
                }
            }

            return items;
        }

        /// <summary>Ruling R20: this member has a pile but was not inside when it was dealt.</summary>
        public void MarkMemberDeliveryWanted(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return;

            lock (stateLock)
                ms.DeliveryWanted = true;
        }

        public bool IsMemberDeliveryWanted(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
                return ms.DeliveryWanted;
        }

        /// <summary>Clears and returns the member's delivery-wanted flag; true for exactly one caller per flag.</summary>
        public bool TryClaimMemberDeliveryWanted(uint guid)
        {
            if (!members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
            {
                if (!ms.DeliveryWanted)
                    return false;

                ms.DeliveryWanted = false;
                return true;
            }
        }

        /// <summary>
        /// One detached snapshot per member, in roster order. PilesForfeited is the larger of the recorded
        /// forfeits and the distinct rounds still held, so a snapshot taken before <see cref="DrainHeldPiles"/>
        /// already reports what would be lost. KeyGranted is true for the owner (whose gem is the run's) and for
        /// a member holding a key. The owner's SurveyFiled also reads the run-level <see cref="SurveyFiled"/>
        /// latch, which is where the existing solo survey path records it.
        /// </summary>
        public IReadOnlyList<RosterMemberSnapshot> SnapshotRoster()
        {
            var snapshots = new List<RosterMemberSnapshot>(roster.Count);

            lock (stateLock)
            {
                foreach (var member in roster)
                {
                    var ms = members[member.Guid];
                    var heldRounds = ms.HeldPile.Select(h => h.Round).Distinct().Count();

                    snapshots.Add(new RosterMemberSnapshot
                    {
                        Guid = member.Guid,
                        Name = member.Name,
                        IsOwner = member.IsOwner,
                        LevelAtStart = member.LevelAtStart,
                        LevelEnd = ms.LevelEnd,
                        Entered = ms.Entered,
                        SecondsInside = ms.SecondsInside,
                        XpGained = Interlocked.Read(ref ms.Xp),
                        LumGained = Interlocked.Read(ref ms.Lum),
                        PilesReceived = ms.RoundsReceived.Count,
                        PilesForfeited = Math.Max(ms.RoundsForfeited.Count, heldRounds),
                        SurveyFiled = ms.SurveyFiled || (member.IsOwner && surveyFiled),
                        KeyGranted = member.IsOwner || ms.KeyGem != 0,
                        PuzzleRemoved = puzzleRemoved.Contains(member.Guid),
                    });
                }
            }

            return snapshots;
        }

        private static bool IsPositiveFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
    }
}
