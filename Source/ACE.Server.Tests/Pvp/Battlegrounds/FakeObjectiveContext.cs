using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// A scriptable <see cref="IObjectiveMatchContext"/> for the Attack/Defend tick handler tests. Team 0 attacks, team 1 defends.
    /// Separate from <see cref="FakeBattlegroundContext"/>, which stays the King of the Hill fake.
    /// </summary>
    internal sealed class FakeObjectiveContext : IObjectiveMatchContext
    {
        public static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Guid MatchId { get; } = Guid.NewGuid();
        public string ModeKey => BattlegroundModes.AttackDefendModeKey;
        public List<PvpTeam> TeamList { get; }
        public IReadOnlyList<PvpTeam> Teams => TeamList;
        public PvpMatchState State { get; private set; } = PvpMatchState.Live;
        public DateTime? LiveSinceUtc { get; set; } = Start;
        public Dictionary<int, int> ScoreBoard { get; } = new();
        public Dictionary<int, int> TeamKills { get; } = new();
        public List<BattlegroundSeat> Seats { get; } = new();

        /// <summary>Every line sent to the whole match, in order.</summary>
        public List<string> Announcements { get; } = new();

        /// <summary>Every line sent to one team, in order.</summary>
        public List<(int Team, string Text)> TeamAnnouncements { get; } = new();

        /// <summary>What <see cref="SampleCrystals"/> returns: the crystals still standing.</summary>
        public List<CrystalHealth> Crystals { get; } = new();

        public IReadOnlyList<BattlegroundSeat> ActiveSeats => Seats.Where(BattlegroundSeats.IsActive).ToList();

        public FakeObjectiveContext(int perTeam = 2)
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

        public IReadOnlyList<BattlegroundZoneSample> SampleZone() => Array.Empty<BattlegroundZoneSample>();

        public void RequestDrain(uint characterId, int health, int stamina, int mana, bool lethal)
        {
        }

        public void Announce(string text) => Announcements.Add(text);

        public bool RollHillTieBreakWest() => false;

        public void ReplaceZoneMarkers(KothZone zone)
        {
        }

        public IReadOnlyList<CrystalHealth> SampleCrystals() => Crystals.ToList();

        public void AnnounceToTeam(int teamIndex, string text) => TeamAnnouncements.Add((teamIndex, text));

        /// <summary>Sets one crystal's sampled health, adding it when absent.</summary>
        public void SetCrystal(int index, int current, int max)
        {
            Crystals.RemoveAll(c => c.Index == index);
            Crystals.Add(new CrystalHealth(index, current, max));
        }
    }
}
