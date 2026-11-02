using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using ACE.Entity;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// Where the crystals of one Attack/Defend match stand, as the performance ledger needs them to classify "near the vulnerable
    /// crystal" (<see cref="CrystalProximity"/>): the plan's crystals and sequence, the match space's landblock and instance, and the
    /// pvp_bg_ad_kill_range snapshot taken when the match formed. Immutable; bound once, when the space is allocated (before Live). The sites'
    /// positions in the match space are built once, here, so the per-hit check allocates nothing.
    /// </summary>
    public sealed record CrystalProximityFrame(IReadOnlyList<PlannedCrystal> Crystals, CrystalSequence Sequence, uint MatchLandblock, uint MatchInstance, double Range)
    {
        /// <summary>Element i is <c>Crystals[i]</c>'s site in the match space (<see cref="CrystalProximity.SitePositions"/>). Never mutated.</summary>
        public Position[] SitePositions { get; } = CrystalProximity.SitePositions(Crystals, MatchLandblock, MatchInstance);
    }

    /// <summary>
    /// One seat's counters at one moment (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"). Plain data. <see cref="HealingDone"/> and
    /// <see cref="EngagedSeconds"/> are the conduct signals ("Conduct signals"); the performance modifier never reads them.
    /// </summary>
    public readonly record struct SeatPerformanceSnapshot(
        uint CharacterId,
        int TeamIndex,
        long PlayerDamage,
        long CrystalDamage,
        long DefenderDamageNear,
        long DefenderDamageFar,
        long Killshots,
        long KillshotsNearCrystal,
        long KillshotsFarCrystal,
        long KillshotsNearHill,
        long HillSeconds,
        long HealingDone = 0,
        long EngagedSeconds = 0,
        long KillshotsOnHill = 0);

    /// <summary>
    /// One seat's performance counters. Every counter is a <c>long</c> written with <see cref="Interlocked"/>, because the damage hook
    /// runs on landblock threads while the coordinator (world thread) credits killshots and hill time and reads the snapshot at Resolve.
    /// </summary>
    public sealed class SeatPerformance
    {
        private long _playerDamage;
        private long _crystalDamage;
        private long _defenderDamageNear;
        private long _defenderDamageFar;
        private long _killshots;
        private long _killshotsNearCrystal;
        private long _killshotsFarCrystal;
        private long _killshotsNearHill;
        private long _hillSeconds;
        private long _healingDone;
        private long _engagedSeconds;
        private long _killshotsOnHill;

        public SeatPerformance(uint characterId, int teamIndex)
        {
            CharacterId = characterId;
            TeamIndex = teamIndex;
        }

        public uint CharacterId { get; }

        /// <summary>The seat's team at formation (team 0 is the attacker in Attack/Defend and west in King of the Hill).</summary>
        public int TeamIndex { get; }

        internal void AddPlayerDamage(long amount) => Interlocked.Add(ref _playerDamage, amount);

        internal void AddCrystalDamage(long amount) => Interlocked.Add(ref _crystalDamage, amount);

        internal void AddDefenderDamage(long amount, bool near) => Interlocked.Add(ref near ? ref _defenderDamageNear : ref _defenderDamageFar, amount);

        internal void AddKillshot(bool? nearCrystal, bool nearHill, bool onHill = false)
        {
            Interlocked.Increment(ref _killshots);

            if (nearCrystal == true)
                Interlocked.Increment(ref _killshotsNearCrystal);
            else if (nearCrystal == false)
                Interlocked.Increment(ref _killshotsFarCrystal);

            if (nearHill)
                Interlocked.Increment(ref _killshotsNearHill);

            if (onHill)
                Interlocked.Increment(ref _killshotsOnHill);
        }

        internal void AddHillSeconds(long seconds) => Interlocked.Add(ref _hillSeconds, seconds);

        internal void AddHealingDone(long amount) => Interlocked.Add(ref _healingDone, amount);

        internal void AddEngagedSeconds(long seconds) => Interlocked.Add(ref _engagedSeconds, seconds);

        public SeatPerformanceSnapshot Snapshot() => new SeatPerformanceSnapshot(
            CharacterId,
            TeamIndex,
            Interlocked.Read(ref _playerDamage),
            Interlocked.Read(ref _crystalDamage),
            Interlocked.Read(ref _defenderDamageNear),
            Interlocked.Read(ref _defenderDamageFar),
            Interlocked.Read(ref _killshots),
            Interlocked.Read(ref _killshotsNearCrystal),
            Interlocked.Read(ref _killshotsFarCrystal),
            Interlocked.Read(ref _killshotsNearHill),
            Interlocked.Read(ref _hillSeconds),
            Interlocked.Read(ref _healingDone),
            Interlocked.Read(ref _engagedSeconds),
            Interlocked.Read(ref _killshotsOnHill));
    }

    /// <summary>
    /// The per-player performance stats of one battleground or Tugak Brawl match (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"): damage
    /// dealt to opposing players, crystal damage, defender damage near and far the vulnerable crystal, cross-team killshots (near and far
    /// the crystal, near the hill), hill seconds, and the conduct signals ("Conduct signals"): health restored to teammates and engaged
    /// seconds. The coordinator logs it at Resolve. Two readers: the battleground performance modifier (its role stats only), and a Tugak Brawl timeout, which ranks its survivors by player damage (<see cref="PlayerDamageByTeam"/>).
    ///
    /// <para/>
    /// Lock-free. The seat dictionary is built ONCE, when the match forms, and never changes afterwards, so concurrent reads of it
    /// are safe; each seat's counters are <see cref="Interlocked"/> longs. The crystal frame is one volatile reference bound once by
    /// the coordinator when the space is allocated, and the destroyed set is a 64-bit mask the coordinator ORs in on the world thread
    /// when a crystal's destruction is counted; landblock threads read both with no lock (a reader sees the old or the new value, never
    /// a torn one), like <see cref="PvpMatch.CrystalSequence"/>. Pure: no clock.
    /// </summary>
    public sealed class BattlegroundPerformanceLedger
    {
        private readonly Dictionary<uint, SeatPerformance> _seats;
        private volatile CrystalProximityFrame _crystals;
        private long _destroyedMask;

        /// <summary><see cref="IsCrystalStanding"/> as a delegate, created once so the per-hit proximity check does not allocate one.</summary>
        private readonly Func<int, bool> _isCrystalStanding;

        /// <param name="seats">Every participant at formation, with their team index.</param>
        public BattlegroundPerformanceLedger(IEnumerable<(uint CharacterId, int TeamIndex)> seats)
        {
            _seats = new Dictionary<uint, SeatPerformance>();
            _isCrystalStanding = IsCrystalStanding;

            foreach (var (id, team) in seats ?? Enumerable.Empty<(uint, int)>())
                _seats[id] = new SeatPerformance(id, team);
        }

        /// <summary>A ledger for every member of <paramref name="teams"/>, each on their own team index.</summary>
        public static BattlegroundPerformanceLedger ForTeams(IEnumerable<PvpTeam> teams)
            => new BattlegroundPerformanceLedger((teams ?? Enumerable.Empty<PvpTeam>()).SelectMany(t => t.Members.Select(p => (p.CharacterId, t.TeamIndex))));

        /// <summary>The seat of <paramref name="characterId"/>, or null for anyone who was not in the match at formation.</summary>
        public SeatPerformance Seat(uint characterId) => _seats.TryGetValue(characterId, out var seat) ? seat : null;

        /// <summary>Every seat, in no particular order.</summary>
        public IEnumerable<SeatPerformance> Seats => _seats.Values;

        /// <summary>The crystal frame, or null for a match with no crystals (King of the Hill) or before the space is allocated.</summary>
        public CrystalProximityFrame Crystals => _crystals;

        /// <summary>Binds the match's crystals, once (a later call is ignored). Coordinator, world thread, before the match goes Live.</summary>
        public void BindCrystals(CrystalProximityFrame frame)
        {
            if (frame != null && _crystals == null)
                _crystals = frame;
        }

        /// <summary>Records that the crystal at <paramref name="index"/> fell. Coordinator, world thread. Indices outside 0..63 are ignored.</summary>
        public void MarkCrystalDestroyed(int index)
        {
            if (index < 0 || index >= 64)
                return;

            Interlocked.Or(ref _destroyedMask, 1L << index);
        }

        /// <summary>True while the crystal at <paramref name="index"/> has not been recorded as destroyed.</summary>
        public bool IsCrystalStanding(int index) => index < 0 || index >= 64 || (Interlocked.Read(ref _destroyedMask) & (1L << index)) == 0;

        /// <summary>
        /// Near (true) or far (false) the vulnerable crystal, by <see cref="CrystalProximity"/> over the bound frame's prebuilt site positions
        /// and the destroyed set (no allocation); null when no crystals are bound (King of the Hill, or a match whose space is not allocated yet).
        /// </summary>
        public bool? IsNearVulnerableCrystal(Position position)
        {
            var frame = _crystals;

            if (frame == null)
                return null;

            return CrystalProximity.VulnerableIndexNear(frame.Crystals, frame.SitePositions, frame.Sequence, frame.MatchInstance, frame.Range, position, _isCrystalStanding) >= 0;
        }

        /// <summary>
        /// <paramref name="amount"/> points of damage the attacker <paramref name="attackerId"/> dealt to an opposing player of this match,
        /// who stood at <paramref name="victimAt"/>. A defender's damage in a crystal match is also split near / far the vulnerable crystal by
        /// the VICTIM's position. Nothing for a character with no seat. The caller has already decided the hit counts (same Live match,
        /// different teams).
        /// </summary>
        public void CreditPlayerDamage(uint attackerId, uint amount, Position victimAt)
        {
            if (amount == 0 || !_seats.TryGetValue(attackerId, out var seat))
                return;

            seat.AddPlayerDamage(amount);

            if (seat.TeamIndex != CrystalWinCondition.DefenderTeam)
                return;

            var near = IsNearVulnerableCrystal(victimAt);

            if (near.HasValue)
                seat.AddDefenderDamage(amount, near.Value);
        }

        /// <summary><paramref name="amount"/> points the attacker took off a crystal of this match. Nothing for a character with no seat.</summary>
        public void CreditCrystalDamage(uint attackerId, uint amount)
        {
            if (amount != 0 && _seats.TryGetValue(attackerId, out var seat))
                seat.AddCrystalDamage(amount);
        }

        /// <summary>
        /// A cross-team killshot by <paramref name="killerId"/>. <paramref name="nearCrystal"/> is null outside a crystal match;
        /// <paramref name="nearHill"/> is false outside King of the Hill. <paramref name="onHill"/> is the STRICT form of
        /// <paramref name="nearHill"/>: the victim died inside the hill zone itself, with no margin (<see cref="IsOnHill"/>); it feeds the
        /// PvP daily Usurper (PvpDailyCatalog) and nothing in the rating or the conduct floor reads it. The caller has already decided the
        /// kill counts.
        /// </summary>
        public void CreditKillshot(uint killerId, bool? nearCrystal, bool nearHill, bool onHill = false)
        {
            if (_seats.TryGetValue(killerId, out var seat))
                seat.AddKillshot(nearCrystal, nearHill, onHill);
        }

        /// <summary><paramref name="seconds"/> of hill time for one IP-deduped on-the-point sample of <paramref name="characterId"/>.</summary>
        public void CreditHillSeconds(uint characterId, int seconds)
        {
            if (seconds > 0 && _seats.TryGetValue(characterId, out var seat))
                seat.AddHillSeconds(seconds);
        }

        /// <summary>
        /// <paramref name="amount"/> points of health <paramref name="healerId"/> actually restored to a TEAMMATE (the vital write's own
        /// result, so overheal is never counted). Nothing for 0 or for a character with no seat. The caller has already decided the heal
        /// counts (same Live match, same team, not a self-heal).
        /// </summary>
        public void CreditHealing(uint healerId, uint amount)
        {
            if (amount != 0 && _seats.TryGetValue(healerId, out var seat))
                seat.AddHealingDone(amount);
        }

        /// <summary><paramref name="seconds"/> of engaged time for <paramref name="characterId"/> (the coordinator's conduct sampler).</summary>
        public void CreditEngagedSeconds(uint characterId, int seconds)
        {
            if (seconds > 0 && _seats.TryGetValue(characterId, out var seat))
                seat.AddEngagedSeconds(seconds);
        }

        /// <summary>
        /// Each team's enemy-player damage this match: the sum of its seats' <see cref="SeatPerformanceSnapshot.PlayerDamage"/>. A team
        /// with no seat in the ledger is absent. Tugak Brawl's timeout ranks its survivors by this (Docs/Pvp/DESIGN.md "Win condition").
        /// </summary>
        public IReadOnlyDictionary<int, long> PlayerDamageByTeam()
            => _seats.Values.Select(s => s.Snapshot()).GroupBy(s => s.TeamIndex).ToDictionary(g => g.Key, g => g.Sum(s => s.PlayerDamage));

        /// <summary>Every seat's counters, ordered by team then character id.</summary>
        public IReadOnlyList<SeatPerformanceSnapshot> Snapshot()
            => _seats.Values.Select(s => s.Snapshot()).OrderBy(s => s.TeamIndex).ThenBy(s => s.CharacterId).ToList();

        /// <summary>
        /// True when a death at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) in the landblock frame lies inside the
        /// King of the Hill zone widened by <paramref name="margin"/> metres horizontally (pvp_bg_perf_koth_near_margin); the height
        /// tolerance is the zone's own. A margin that is NaN, infinite or negative counts as 0. Pure.
        /// </summary>
        public static bool IsNearHill(KothZone zone, double margin, double x, double y, double z)
        {
            var m = double.IsNaN(margin) || double.IsInfinity(margin) || margin < 0.0 ? 0.0 : margin;

            return (zone with { Radius = zone.Radius + m }).Contains(x, y, z);
        }

        /// <summary>
        /// True when a death at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) lies inside the King of the Hill zone
        /// itself: <see cref="IsNearHill"/> with a margin of exactly 0, so the same geometry with no widening. Pure.
        /// </summary>
        public static bool IsOnHill(KothZone zone, double x, double y, double z) => IsNearHill(zone, 0.0, x, y, z);

        /// <summary>
        /// The one log line per seat written at Resolve: <c>[PVP] match &lt;id&gt; perf: &lt;name&gt; (0x&lt;id&gt;) team &lt;t&gt;: player_dmg N,
        /// crystal_dmg N, def_dmg_near N, def_dmg_far N, kills N, kills_near_crystal N, kills_far_crystal N, kills_near_hill N, hill_s N,
        /// healing N, engaged_s N</c>. Pure.
        /// </summary>
        public static string FormatLogLine(Guid matchId, string name, SeatPerformanceSnapshot s)
            => string.Format(CultureInfo.InvariantCulture,
                "[PVP] match {0} perf: {1} (0x{2:X8}) team {3}: player_dmg {4}, crystal_dmg {5}, def_dmg_near {6}, def_dmg_far {7}, kills {8}, kills_near_crystal {9}, kills_far_crystal {10}, kills_near_hill {11}, hill_s {12}, healing {13}, engaged_s {14}",
                matchId, name, s.CharacterId, s.TeamIndex, s.PlayerDamage, s.CrystalDamage, s.DefenderDamageNear, s.DefenderDamageFar,
                s.Killshots, s.KillshotsNearCrystal, s.KillshotsFarCrystal, s.KillshotsNearHill, s.HillSeconds, s.HealingDone, s.EngagedSeconds);

        /// <summary>
        /// The second line per seat, written after the rating is computed when the performance modifier is on (Docs/Pvp/BATTLEGROUNDS.md
        /// "Rating and performance"): <c>[PVP] match &lt;id&gt; perf_mod: &lt;name&gt; (0x&lt;id&gt;) team &lt;t&gt;: perf_score P, perf_mod m, perf_applied a</c>.
        /// P is "-" for a seat that was not scored; m is the clamped modifier before the team's zero-sum pass; a is the modifier the rating
        /// applied, "-" for an unrated match. Pure.
        /// </summary>
        public static string FormatModifierLine(Guid matchId, string name, uint characterId, int teamIndex, double? score, double modifier, double? applied)
            => string.Format(CultureInfo.InvariantCulture, "[PVP] match {0} perf_mod: {1} (0x{2:X8}) team {3}: perf_score {4}, perf_mod {5}, perf_applied {6}",
                matchId, name, characterId, teamIndex,
                score.HasValue ? score.Value.ToString("0.000", CultureInfo.InvariantCulture) : "-",
                modifier.ToString("+0.000;-0.000;+0.000", CultureInfo.InvariantCulture),
                applied.HasValue ? applied.Value.ToString("+0.000;-0.000;+0.000", CultureInfo.InvariantCulture) : "-");
    }
}
