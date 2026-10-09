using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>A scriptable IBattlegroundMatchContext for the score win condition and KOTH handler tests.</summary>
    internal sealed class FakeBattlegroundContext : IBattlegroundMatchContext
    {
        public static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Guid MatchId { get; } = Guid.NewGuid();
        public string ModeKey => BattlegroundModes.KothModeKey;
        public List<PvpTeam> TeamList { get; }
        public IReadOnlyList<PvpTeam> Teams => TeamList;
        public PvpMatchState State { get; private set; } = PvpMatchState.Live;
        public DateTime? LiveSinceUtc { get; set; } = Start;
        public Dictionary<int, int> ScoreBoard { get; } = new();
        public Dictionary<int, int> TeamKills { get; } = new();
        public List<BattlegroundSeat> Seats { get; } = new();
        public List<BattlegroundZoneSample> Samples { get; } = new();
        public List<(uint Id, int Health, int Stamina, int Mana, bool Lethal)> Drains { get; } = new();
        public List<string> Announcements { get; } = new();

        public IReadOnlyList<BattlegroundSeat> ActiveSeats => Seats.Where(BattlegroundSeats.IsActive).ToList();

        public FakeBattlegroundContext(int perTeam = 2)
        {
            TeamList = new List<PvpTeam>();
            uint id = 1;

            for (var t = 0; t < 2; t++)
            {
                var members = new List<PvpParticipant>();
                for (var i = 0; i < perTeam; i++)
                {
                    var p = new PvpParticipant(id++, 1500, ipKey: "ip" + (id - 1));
                    members.Add(p);
                    Seats.Add(new BattlegroundSeat(p, t, false));
                }
                TeamList.Add(new PvpTeam(t, members));
            }
        }

        public void SetState(PvpMatchState state) => State = state;

        public IReadOnlyList<BattlegroundZoneSample> SampleZone() => Samples.ToList();

        public void RequestDrain(uint characterId, int health, int stamina, int mana, bool lethal) => Drains.Add((characterId, health, stamina, mana, lethal));

        public void Announce(string text) => Announcements.Add(text);

        /// <summary>The answer RollHillTieBreakWest gives; true is the west side.</summary>
        public bool TieBreakWest { get; set; }

        /// <summary>How many times the handler asked for the coin flip.</summary>
        public int TieBreakRolls { get; private set; }

        public bool RollHillTieBreakWest()
        {
            TieBreakRolls++;
            return TieBreakWest;
        }

        /// <summary>Every zone the handler asked the coordinator to re-plan the markers on, in order.</summary>
        public List<KothZone> ReplacedZones { get; } = new();

        public void ReplaceZoneMarkers(KothZone zone) => ReplacedZones.Add(zone);

        public PvpParticipant Member(int team, int index) => TeamList[team].Members[index];

        public void SetRespawning(PvpParticipant p, bool respawning)
        {
            var i = Seats.FindIndex(s => s.Participant == p);
            Seats[i] = Seats[i] with { Respawning = respawning };
        }

        /// <summary>Adds a sample for the member at (x, y, z) with every other flag in the "counts" state.</summary>
        public BattlegroundZoneSample Sample(PvpParticipant p, double x, double y, double z, bool inInstance = true, bool dead = false, bool teleporting = false, string ip = null)
        {
            var team = Seats.First(s => s.Participant == p).TeamIndex;
            var s = new BattlegroundZoneSample(p.CharacterId, team, ip ?? p.IpKey, inInstance, dead, teleporting, x, y, z);
            Samples.Add(s);
            return s;
        }
    }
}