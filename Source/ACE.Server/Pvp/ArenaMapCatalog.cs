using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>One participant's seat, as chosen by <see cref="ArenaMapCatalog.AssignSpawns"/>.</summary>
    public sealed record SpawnAssignment(int TeamIndex, PvpParticipant Participant, PvpSpawnPoint Spawn);

    /// <summary>
    /// The v1 arena maps (Docs/Pvp/DESIGN.md "Match spaces"): landblocks 0x0066 and 0x0067, instanced in
    /// realm 1, which strips base statics and encounters by default (Content/realms/realm1_strip_default.sql),
    /// so an instance of either is an empty room and no content unit is needed.
    ///
    /// <para/>
    /// Geometry, measured against client_cell_1.dat on 2026-09-25: the two landblocks are byte-identical single
    /// enclosed rooms (84 cells, no openings) with a flat floor at z = 0 and about 4000 m2 of floor. Every point
    /// below was checked with `cells --landblock=0x0066 --at=x,y,3.00` (dz = -3.00, floor at z = 0), faces the
    /// room centre (30, -25), and uses z = 0.005, the retail arena convention (Player_Location.cs:626-630), so a
    /// player does not spawn inside the floor. Cell ids are the low 16 bits; the landblock supplies the rest.
    /// F10 and F12 lie on a cell boundary and use the first of the two cells the measurement listed.
    ///
    /// <para/>
    /// Because the geometry is shared, both maps carry the SAME spawn-set objects, and
    /// <see cref="AssignSpawns"/> seats against those shared sets. A future map with its own geometry needs its
    /// own sets and a map-aware seating overload.
    /// </summary>
    public static class ArenaMapCatalog
    {
        public const string OneVOneKey = "1v1";
        public const string TwoVTwoKey = "2v2";
        public const string FfaKey = "ffa";

        /// <summary>
        /// The word players type for the FFA mode since the 2026-10-06 rename to "Tugak Brawl" ("/arena join tugak"). Display and
        /// command-word only: the internal mode key (<see cref="FfaKey"/>), the "arena_ffa" ladder, stored template mode lists and
        /// the pvp_arena_*_ffa tunable keys all keep the old name.
        /// </summary>
        public const string FfaJoinWord = "tugak";

        /// <summary>A mode word as a player or admin typed it (trimmed, lowercased), as its internal mode key: "tugak" is the FFA key, every other word is returned as is. "ffa" stays a working alias because it already is the key.</summary>
        public static string CanonicalModeWord(string word)
        {
            var w = (word ?? string.Empty).Trim().ToLowerInvariant();

            return w == FfaJoinWord ? FfaKey : w;
        }

        /// <summary>
        /// TRUE only for the three arena mode keys, compared ordinally (an allowlist: null, empty, a room key such as "bg", a
        /// battleground key in any case and every unknown key are NOT arena). The fail-closed test for arena-only rules.
        /// </summary>
        public static bool IsArenaModeKey(string modeKey) =>
            string.Equals(modeKey, OneVOneKey, StringComparison.Ordinal)
            || string.Equals(modeKey, TwoVTwoKey, StringComparison.Ordinal)
            || string.Equals(modeKey, FfaKey, StringComparison.Ordinal);

        /// <summary>The word to show a player for typing a mode key: "tugak" for FFA, the key itself otherwise.</summary>
        public static string JoinWord(string modeKey) => modeKey == FfaKey ? FfaJoinWord : modeKey;

        /// <summary>The realm every v1 arena map is instanced in.</summary>
        public const ushort ArenaRealmId = 1;

        /// <summary>FFA seats available per map: the 12-point ring plus 3 inner points.</summary>
        public const int FfaSeats = 15;

        /// <summary>Points on the FFA ring (F1-F12). Seats past this many use the inner points (I1-I3).</summary>
        public const int FfaRingSeats = 12;

        private const float Z = 0.005f;

        private static PvpSpawnPoint P(string label, ushort cellLow, float x, float y, float w, float z) =>
            new PvpSpawnPoint(label, cellLow, x, y, Z, w, z);

        /// <summary>
        /// LobbyMatchmaker's decaying FFA lobby target size: starts at <see cref="PvpArenaDials.FfaTargetPlayers"/>
        /// and drops by one point per <see cref="PvpArenaDials.FfaMinDecaySeconds"/> the oldest queued unit has
        /// waited, floored at <see cref="PvpArenaDials.FfaMinPlayers"/> and never above
        /// <see cref="PvpArenaDials.FfaMaxPlayers"/>. The ONE shared formula behind the FFA lobby progress line
        /// (<see cref="PvpMatchCoordinator"/>'s AnnounceFfaLobby) and the Arena Crier (Docs/Pvp/DESIGN.md "Arena
        /// Crier"), so neither can ever quote a different FFA lobby size than the other.
        /// </summary>
        public static int FfaDecayedTargetSize(TimeSpan oldestWaited, PvpArenaDials dials)
            => FfaDecayedTargetSize(oldestWaited, dials.FfaMinPlayers, dials.FfaTargetPlayers, dials.FfaMaxPlayers, dials.FfaMinDecaySeconds);

        /// <summary>
        /// The same formula as the <see cref="PvpArenaDials"/> overload, taken as raw values so
        /// <see cref="LobbyMatchmaker"/> can call it straight from its <see cref="MatchmakingContext"/> (which
        /// carries these four values but not a whole <see cref="PvpArenaDials"/>).
        /// </summary>
        public static int FfaDecayedTargetSize(TimeSpan oldestWaited, int ffaMinPlayers, int ffaTargetPlayers, int ffaMaxPlayers, int ffaMinDecaySeconds)
        {
            var decaySteps = (int)Math.Floor(oldestWaited.TotalSeconds / Math.Max(1, ffaMinDecaySeconds));
            return Math.Min(Math.Max(ffaMinPlayers, ffaTargetPlayers - decaySteps), ffaMaxPlayers);
        }

        /// <summary>1v1: side A (team 0) north, side B (team 1) south.</summary>
        public static readonly IReadOnlyList<PvpSpawnPoint> OneVOneSet = Array.AsReadOnly(new[]
        {
            P("A", 0x0113, 30.00f, -12.50f, 0.000000f, 1.000000f),
            P("B", 0x0116, 30.00f, -37.50f, 1.000000f, 0.000000f),
        });

        /// <summary>2v2, in seating order: team 0 takes A1, A2; team 1 takes B1, B2.</summary>
        public static readonly IReadOnlyList<PvpSpawnPoint> TwoVTwoSet = Array.AsReadOnly(new[]
        {
            P("A1", 0x010D, 20.00f, -10.00f, 0.289784f, -0.957092f),
            P("A2", 0x0119, 40.00f, -10.00f, 0.289784f, 0.957092f),
            P("B1", 0x0110, 20.00f, -40.00f, 0.957092f, -0.289784f),
            P("B2", 0x011C, 40.00f, -40.00f, 0.957092f, 0.289784f),
        });

        /// <summary>FFA: the ring F1-F12 in order around the room, then the inner points I1-I3.</summary>
        public static readonly IReadOnlyList<PvpSpawnPoint> FfaSet = Array.AsReadOnly(new[]
        {
            P("F1", 0x0120, 50.00f, -25.00f, 0.707107f, 0.707107f),
            P("F2", 0x011F, 47.32f, -15.00f, 0.500000f, 0.866025f),
            P("F3", 0x0119, 40.00f, -7.68f, 0.258819f, 0.965926f),
            P("F4", 0x0112, 30.00f, -5.00f, 0.000000f, 1.000000f),
            P("F5", 0x010D, 20.00f, -7.68f, 0.258819f, -0.965926f),
            P("F6", 0x0107, 12.68f, -15.00f, 0.500000f, -0.866025f),
            P("F7", 0x0108, 10.00f, -25.00f, 0.707107f, -0.707107f),
            P("F8", 0x0109, 12.68f, -35.00f, 0.866025f, -0.500000f),
            P("F9", 0x0110, 20.00f, -42.32f, 0.965926f, -0.258819f),
            P("F10", 0x0116, 30.00f, -45.00f, 1.000000f, 0.000000f),
            P("F11", 0x011C, 40.00f, -42.32f, 0.965926f, 0.258819f),
            P("F12", 0x0121, 47.32f, -35.00f, 0.866025f, 0.500000f),
            P("I1", 0x011A, 38.69f, -22.67f, 0.608761f, 0.793353f),
            P("I2", 0x010E, 23.64f, -18.64f, 0.382683f, -0.923880f),
            P("I3", 0x0115, 27.67f, -33.69f, 0.991445f, -0.130526f),
        });

        private static readonly IReadOnlyDictionary<string, IReadOnlyList<PvpSpawnPoint>> SharedSets =
            new Dictionary<string, IReadOnlyList<PvpSpawnPoint>>
            {
                [OneVOneKey] = OneVOneSet,
                [TwoVTwoKey] = TwoVTwoSet,
                [FfaKey] = FfaSet,
            };

        public static readonly ArenaMap Arena0066 = new ArenaMap("arena_0066", 0x0066, ArenaRealmId, SharedSets) { DisplayName = "Arena I" };

        public static readonly ArenaMap Arena0067 = new ArenaMap("arena_0067", 0x0067, ArenaRealmId, SharedSets) { DisplayName = "Arena II" };

        /// <summary>Every v1 arena map. Each carries every mode's spawn set, so this is also every mode's map pool.</summary>
        public static readonly IReadOnlyList<ArenaMap> All = Array.AsReadOnly(new[] { Arena0066, Arena0067 });

        /// <summary>The map with this key, or null.</summary>
        public static ArenaMap Find(string mapKey)
        {
            foreach (var map in All)
            {
                if (string.Equals(map.MapKey, mapKey, StringComparison.OrdinalIgnoreCase))
                    return map;
            }

            return null;
        }

        /// <summary>
        /// Seats a match's participants, or returns null when the team shape does not fit the mode (the caller
        /// logs and cancels; this never throws for a bad shape). Pure.
        ///   - 1v1: exactly 2 teams of 1. Team list position 0 takes A, position 1 takes B.
        ///   - 2v2: exactly 2 teams of 2. Team at position t, member m takes TwoVTwoSet[t * 2 + m].
        ///   - ffa: 1 to <see cref="FfaSeats"/> teams of 1, seated by <see cref="FfaSeatOrder"/>, in team-list order.
        /// Seats follow the team LIST order, not TeamIndex; TeamIndex is only carried through to the result.
        /// </summary>
        public static IReadOnlyList<SpawnAssignment> AssignSpawns(string modeKey, IReadOnlyList<PvpTeam> teams)
        {
            if (teams == null)
                return null;

            foreach (var team in teams)
            {
                if (team?.Members == null)
                    return null;

                foreach (var member in team.Members)
                {
                    if (member == null)
                        return null;
                }
            }

            switch (modeKey)
            {
                case OneVOneKey:
                    return SeatSides(teams, OneVOneSet, teamSize: 1);

                case TwoVTwoKey:
                    return SeatSides(teams, TwoVTwoSet, teamSize: 2);

                case FfaKey:
                    return SeatFfa(teams);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Indexes into <see cref="FfaSet"/> for a lobby of n (1 to <see cref="FfaSeats"/>), or null outside that.
        /// Up to 12 players are spread evenly around the ring, every (12 / n)-th point (index floor(i * 12 / n)),
        /// so a small lobby never bunches on one side. From 13 up, the whole ring is used and then I1, I2, I3.
        /// </summary>
        public static int[] FfaSeatOrder(int n)
        {
            if (n < 1 || n > FfaSeats)
                return null;

            var seats = new int[n];

            if (n <= FfaRingSeats)
            {
                for (var i = 0; i < n; i++)
                    seats[i] = i * FfaRingSeats / n;
            }
            else
            {
                for (var i = 0; i < n; i++)
                    seats[i] = i;
            }

            return seats;
        }

        private static IReadOnlyList<SpawnAssignment> SeatSides(IReadOnlyList<PvpTeam> teams, IReadOnlyList<PvpSpawnPoint> set, int teamSize)
        {
            if (teams.Count != 2)
                return null;

            var result = new List<SpawnAssignment>(set.Count);

            for (var t = 0; t < teams.Count; t++)
            {
                var members = teams[t].Members;

                if (members.Count != teamSize)
                    return null;

                for (var m = 0; m < members.Count; m++)
                    result.Add(new SpawnAssignment(teams[t].TeamIndex, members[m], set[t * teamSize + m]));
            }

            return result;
        }

        private static IReadOnlyList<SpawnAssignment> SeatFfa(IReadOnlyList<PvpTeam> teams)
        {
            var seats = FfaSeatOrder(teams.Count);

            if (seats == null)
                return null;

            var result = new List<SpawnAssignment>(teams.Count);

            for (var i = 0; i < teams.Count; i++)
            {
                if (teams[i].Members.Count != 1)
                    return null;

                result.Add(new SpawnAssignment(teams[i].TeamIndex, teams[i].Members[0], FfaSet[seats[i]]));
            }

            return result;
        }
    }
}
