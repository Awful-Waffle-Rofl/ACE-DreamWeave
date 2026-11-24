using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// A hand-built <see cref="IGauntletCreatureOwner"/> for the planner and spawner tests (Task 7's GauntletRun is the real
    /// one). Records every death report; <see cref="ThrowOnDeath"/> makes the report throw.
    /// </summary>
    internal sealed class FakeGauntletOwner : IGauntletCreatureOwner
    {
        public bool IsLive { get; set; } = true;
        public int CurrentWave { get; set; } = 1;
        public double CentreGx { get; set; }
        public double CentreGy { get; set; }
        public double Radius { get; set; } = 60.0;
        public List<Player> Members { get; } = new List<Player>();
        public List<Creature> Deaths { get; } = new List<Creature>();
        public bool ThrowOnDeath { get; set; }

        public IEnumerable<Player> OnlineMembersInside() => Members;

        public void OnGauntletCreatureDied(Creature creature)
        {
            if (ThrowOnDeath)
                throw new InvalidOperationException("fake owner death fault");

            Deaths.Add(creature);
        }

        /// <summary>An owner whose circle is centred on <paramref name="centre"/> (global outdoor metres).</summary>
        public static FakeGauntletOwner At(DungeonSpawnPointDef centre, double radius = 60.0)
        {
            var lb = (ushort)(centre.Cell >> 16);
            return new FakeGauntletOwner
            {
                CentreGx = OutdoorSiteGeometry.ToGlobalX(lb, centre.X),
                CentreGy = OutdoorSiteGeometry.ToGlobalY(lb, centre.Y),
                Radius = radius,
            };
        }
    }

    /// <summary>Synthetic outdoor rows and sites (the GauntletSiteStoreTests landblock, 0x5456).</summary>
    internal static class GauntletFixtureSites
    {
        public const ushort Primary = 0x5456;

        public static uint Cell(ushort landblock) => ((uint)landblock << 16) | 0x0001;

        public static DungeonSpawnPointDef At(float x, float y, string kind = "outdoor", bool curated = true)
            => new DungeonSpawnPointDef { Cell = Cell(Primary), X = x, Y = y, Z = 50f, Kind = kind, Curated = curated };

        /// <summary>A point <paramref name="metres"/> from (100, 96) at compass bearing <paramref name="degrees"/> (0 = north, clockwise).</summary>
        public static DungeonSpawnPointDef Ring(double metres, double degrees, string kind = "outdoor")
        {
            var r = degrees * Math.PI / 180.0;
            return At((float)(100 + metres * Math.Sin(r)), (float)(96 + metres * Math.Cos(r)), kind);
        }

        public static DungeonEntryDef Row(string id, IEnumerable<DungeonSpawnPointDef> points) => new DungeonEntryDef
        {
            Id = id,
            Kind = DungeonEntryDef.KindOutdoor,
            Landblock = Primary,
            ExitPortalWcid = 1003604,
            Points = points.ToList(),
            Outdoor = new OutdoorSiteDef
            {
                Primary = Primary,
                Footprint = new List<ushort> { Primary },
                Circle = new OutdoorCircleDef { LandblockHex = "0x5456", Landblock = Primary, X = 100, Y = 96, Radius = 200 },
            },
        };

        /// <summary>A site centred at (100, 96) on <paramref name="row"/>, with the row attached as the store would attach it.</summary>
        public static GauntletSiteDef Site(DungeonEntryDef row) => new GauntletSiteDef
        {
            Id = row.Id,
            Centre = At(100, 96),
            Radius = 60,
            Pen = At(100, 96),
            Entries = new List<DungeonSpawnPointDef> { At(110, 96) },
            Outdoor = row,
        };

        /// <summary>Six portal points 30 m out at 0, 60, ..., 300 degrees, plus one too close and one too far.</summary>
        public static DungeonEntryDef SixPortalRow(string id = "ow_gauntletfixture")
            => Row(id, new[] { 0.0, 60, 120, 180, 240, 300 }.Select(d => Ring(30, d)).Concat(new[] { Ring(5, 90), Ring(90, 90) }));
    }
}
