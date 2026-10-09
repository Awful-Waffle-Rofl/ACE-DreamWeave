using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>The moving hill (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"): the pure schedule, the compass, the catalog sites and the tick handler.</summary>
    [TestClass]
    public class KothMovingHillTests
    {
        private static readonly DateTime Start = FakeBattlegroundContext.Start;
        private static readonly BattlegroundLayout Layout = BattlegroundMapCatalog.Bg016c;

        private static KothSiteChoice? Next(int west, int east, int movesMade = 0, int moves = 2, double elapsed = 0, KothSide current = KothSide.Centre, bool tieWest = true, int target = 300, double limit = 900)
            => KothHillSchedule.NextSite(west, east, target, elapsed, limit, movesMade, moves, current, () => tieWest);

        // ================= the pure schedule =================

        [TestMethod]
        public void Score_MovesAtOneThirdAndTwoThirdsOfTheTarget_ForTwoMoves()
        {
            Assert.IsNull(Next(99, 0), "99 of 300 is under 1/3");
            Assert.IsNotNull(Next(100, 0), "exactly 1/3 fires step 1");
            Assert.IsNull(Next(199, 0, movesMade: 1, current: KothSide.East), "199 is under 2/3");
            Assert.IsNotNull(Next(200, 0, movesMade: 1, current: KothSide.East), "exactly 2/3 fires step 2");
            Assert.IsNull(Next(299, 0, movesMade: 2, current: KothSide.Centre), "both steps are used up");
        }

        [TestMethod]
        public void Score_ThresholdUsesTheLeader_WhicheverTeamLeads()
        {
            Assert.IsNotNull(Next(10, 100));
            Assert.IsNotNull(Next(100, 10));
            Assert.IsNull(Next(99, 99), "neither team has reached 1/3");
        }

        [TestMethod]
        public void Score_ThresholdsScaleWithTheMoveCount()
        {
            // One move fires at 1/2 of the target; four moves at 1/5, 2/5, 3/5, 4/5.
            Assert.IsNull(Next(149, 0, moves: 1));
            Assert.IsNotNull(Next(150, 0, moves: 1));
            Assert.IsNull(Next(59, 0, moves: 4));
            Assert.IsNotNull(Next(60, 0, moves: 4));
            Assert.IsNotNull(Next(180, 0, movesMade: 2, moves: 4));
            Assert.IsNull(Next(179, 0, movesMade: 2, moves: 4));
        }

        [TestMethod]
        public void TimeFallback_FiresAtTheSameFractionsOfTheTimeLimit()
        {
            Assert.IsNull(Next(0, 0, elapsed: 299.9), "just under 1/3 of 900 s");
            Assert.IsNotNull(Next(0, 0, elapsed: 300));
            Assert.IsNull(Next(0, 0, movesMade: 1, elapsed: 599.9, current: KothSide.West));
            Assert.IsNotNull(Next(0, 0, movesMade: 1, elapsed: 600, current: KothSide.West));
            Assert.IsNull(Next(0, 0, movesMade: 2, elapsed: 900), "no step beyond the last");
        }

        [TestMethod]
        public void TimeFallback_IsIgnoredWithoutAPositiveLimit_AndScoreStillWorks()
        {
            Assert.IsNull(Next(0, 0, elapsed: 100000, limit: 0));
            Assert.IsNull(Next(0, 0, elapsed: 100000, limit: -5));
            Assert.IsNotNull(Next(100, 0, elapsed: 1, limit: 0));
        }

        [TestMethod]
        public void Whichever_ComesFirst_Wins_AndTheStepIsStillTheSameOne()
        {
            Assert.IsNotNull(Next(100, 0, elapsed: 10), "score first");
            Assert.IsNotNull(Next(5, 0, elapsed: 400), "time first");
        }

        [TestMethod]
        public void Steps_AreMonotonic_OneAtATime_NeverBack()
        {
            // A score far past both thresholds still yields only the NEXT step: out to the trailing room, then back.
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), Next(300, 0, movesMade: 0));
            Assert.AreEqual(KothSiteChoice.Start, Next(300, 0, movesMade: 1, current: KothSide.East));
            Assert.IsNull(Next(300, 0, movesMade: 2, current: KothSide.Centre));
            Assert.IsNull(Next(300, 0, movesMade: 5), "past the end stays put");
            Assert.IsNull(Next(300, 0, movesMade: -1), "a negative count is refused");
        }

        [TestMethod]
        public void ZeroMoves_NeverMoves()
        {
            Assert.IsNull(Next(300, 0, moves: 0));
            Assert.IsNull(Next(0, 0, moves: 0, elapsed: 9999));
            Assert.IsNull(Next(300, 0, moves: -3));
        }

        [TestMethod]
        public void TrailingTeamsRoom_IsChosen()
        {
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), Next(120, 40), "west leads, east trails: the hill goes to the east room");
            Assert.AreEqual(new KothSiteChoice(1, KothSide.West), Next(40, 120), "east leads, west trails: the hill goes to the west room");
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), Next(220, 40, movesMade: 1, current: KothSide.West), "from the other room it crosses to the trailing team's room");
            Assert.AreEqual(new KothSiteChoice(1, KothSide.West), Next(40, 220, movesMade: 1, current: KothSide.East));
        }

        [TestMethod]
        public void AlreadyInTheTrailingTeamsRoom_ReturnsToTheCentre()
        {
            Assert.AreEqual(KothSiteChoice.Start, Next(220, 40, movesMade: 1, current: KothSide.East), "east trails and the hill is already there");
            Assert.AreEqual(KothSiteChoice.Start, Next(40, 220, movesMade: 1, current: KothSide.West));
            Assert.AreEqual(0, Next(220, 40, movesMade: 1, current: KothSide.East)?.Pair);
            Assert.AreEqual(KothSide.Centre, Next(220, 40, movesMade: 1, current: KothSide.East)?.Side);
        }

        [TestMethod]
        public void Tie_FromTheCentre_UsesTheInjectedFlip()
        {
            Assert.AreEqual(new KothSiteChoice(1, KothSide.West), Next(0, 0, elapsed: 300, tieWest: true));
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), Next(0, 0, elapsed: 300, tieWest: false));
        }

        [TestMethod]
        public void Tie_FromARoom_GoesToTheOppositeRoom_IgnoringTheFlip()
        {
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), Next(50, 50, movesMade: 1, elapsed: 600, current: KothSide.West, tieWest: true));
            Assert.AreEqual(new KothSiteChoice(1, KothSide.West), Next(50, 50, movesMade: 1, elapsed: 600, current: KothSide.East, tieWest: false));
        }

        // ================= the compass =================

        [TestMethod]
        public void Compass_NamesTheDirectionFromTheMapCentre()
        {
            Assert.AreEqual("centre", KothHillSchedule.Compass(0.2, -0.3));
            Assert.AreEqual("east", KothHillSchedule.Compass(10, 0));
            Assert.AreEqual("west", KothHillSchedule.Compass(-10, 0));
            Assert.AreEqual("north", KothHillSchedule.Compass(0, 10));
            Assert.AreEqual("south", KothHillSchedule.Compass(0, -10));
            Assert.AreEqual("north-east", KothHillSchedule.Compass(10, 10));
            Assert.AreEqual("north-west", KothHillSchedule.Compass(-10, 10));
            Assert.AreEqual("south-west", KothHillSchedule.Compass(-10, -10));
            Assert.AreEqual("south-east", KothHillSchedule.Compass(10, -10));
        }

        [TestMethod]
        public void Compass_OfTheCatalogSites()
        {
            string Dir(KothSiteChoice c) { var (x, y) = Layout.SiteFor(c); return KothHillSchedule.Compass(x - Layout.ZoneX, y - Layout.ZoneY); }

            Assert.AreEqual("north-west", Dir(new KothSiteChoice(1, KothSide.West)));
            Assert.AreEqual("north-east", Dir(new KothSiteChoice(1, KothSide.East)));
            Assert.AreEqual("centre", Dir(KothSiteChoice.Start));
        }

        // ================= the catalog sites =================

        [TestMethod]
        public void Catalog_HasOnePair_AndTheCentreSiteIsTheOldZone()
        {
            Assert.AreEqual(1, Layout.ZonePairs.Count, "one mirrored pair: the two freed spawn rooms");
            Assert.AreEqual((55f, -35f), Layout.SiteFor(KothSiteChoice.Start));
            Assert.AreEqual((55f, -35f), Layout.SiteFor(new KothSiteChoice(9, KothSide.West)), "an unknown pair falls back to the centre");
        }

        [TestMethod]
        public void Catalog_EachPairIsAnExactXMirror_SameY()
        {
            for (var k = 1; k <= Layout.ZonePairs.Count; k++)
            {
                var west = Layout.SiteFor(new KothSiteChoice(k, KothSide.West));
                var east = Layout.SiteFor(new KothSiteChoice(k, KothSide.East));

                Assert.AreEqual(110f - west.X, east.X, 0.0, $"pair {k} east x is 110 - west x");
                Assert.AreEqual(west.Y, east.Y, 0.0, $"pair {k} shares y");
                Assert.IsTrue(west.X < Layout.ZoneX && east.X > Layout.ZoneX, $"pair {k} straddles the axis");
            }
        }

        [TestMethod]
        public void Catalog_EverySiteIsAtLeastTwoRadiiFromEveryOther()
        {
            var radius = BattlegroundTunables.Defaults.KothZoneRadius;
            var sites = new[] { KothSiteChoice.Start }
                .Concat(Enumerable.Range(1, Layout.ZonePairs.Count).SelectMany(k => new[] { new KothSiteChoice(k, KothSide.West), new KothSiteChoice(k, KothSide.East) }))
                .Select(c => Layout.SiteFor(c)).ToList();

            Assert.AreEqual(3, sites.Count);

            for (var i = 0; i < sites.Count; i++)
                for (var j = i + 1; j < sites.Count; j++)
                {
                    var d = Math.Sqrt(Math.Pow(sites[i].X - sites[j].X, 2) + Math.Pow(sites[i].Y - sites[j].Y, 2));
                    Assert.IsTrue(d >= 2 * radius, $"sites {i} and {j} are {d:0.0} m apart");
                }
        }

        [TestMethod]
        public void Catalog_NoSiteContainsASpawnPointOrPen()
        {
            var radius = BattlegroundTunables.Defaults.KothZoneRadius;
            var points = Layout.TeamSpawns.SelectMany(t => t).Concat(Layout.TeamPens).ToList();

            for (var k = 1; k <= Layout.ZonePairs.Count; k++)
                foreach (var side in new[] { KothSide.West, KothSide.East })
                {
                    var (x, y) = Layout.SiteFor(new KothSiteChoice(k, side));

                    foreach (var p in points)
                        Assert.IsTrue(Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2)) > radius + 4, $"pair {k} {side} is within the zone radius plus a 4 m margin of {p.Label}");
                }
        }

        [TestMethod]
        public void Catalog_MirroredSitesAreEquidistantFromTheirOwnTeamsSpawns()
        {
            double Mean(KothSiteChoice c, int team)
            {
                var (x, y) = Layout.SiteFor(c);
                return Layout.SpawnsFor(team).Average(s => Math.Sqrt(Math.Pow(x - s.X, 2) + Math.Pow(y - s.Y, 2)));
            }

            for (var k = 1; k <= Layout.ZonePairs.Count; k++)
                Assert.AreEqual(Mean(new KothSiteChoice(k, KothSide.West), 0), Mean(new KothSiteChoice(k, KothSide.East), 1), 1e-3, $"pair {k}: west site to team 0 equals east site to team 1");
        }

        // ================= the tick handler =================

        private static readonly BattlegroundDials Dials = BattlegroundTunables.Defaults with
        {
            KothTickSeconds = 4,
            KothHoldPoints = 7,
            KothDecayPoints = 3,
            KothScoreTarget = 300,
            TimeLimitSecondsKoth = 900,
            KothScoreAnnounceSeconds = 100000,
            KothHillMoves = 2
        };

        private static DateTime At(int seconds) => Start.AddSeconds(seconds);

        private static (FakeBattlegroundContext M, KothTickHandler H) Live(BattlegroundDials dials = null, BattlegroundLayout layout = null)
        {
            var d = dials ?? Dials;
            var m = new FakeBattlegroundContext();
            var h = new KothTickHandler(d, BattlegroundModes.KothZoneFor(Layout, d), layout ?? Layout);
            h.OnLive(m);
            return (m, h);
        }

        private static string Moved(string direction) => BattlegroundText.HillMoved(direction);

        [TestMethod]
        public void Move_AtOneThirdOfTheTarget_SwitchesTheZoneToTheTrailingTeamsSite_AndTellsEveryone()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 100;
            m.ScoreBoard[1] = 40;

            h.OnTick(m, At(4));

            Assert.AreEqual(1, h.MovesMade);
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), h.Site, "west leads, so the hill goes to the east room");
            Assert.AreEqual(95.0, h.Zone.CenterX, 0.0);
            Assert.AreEqual(-15.0, h.Zone.CenterY, 0.0);
            Assert.AreEqual(Dials.KothZoneRadius, h.Zone.Radius, 0.0, "radius and height carry over");
            CollectionAssert.Contains(m.Announcements, Moved("north-east"));
            Assert.AreEqual(1, m.ReplacedZones.Count);
            Assert.AreEqual(h.Zone, m.ReplacedZones[0], "the markers are re-planned on the zone now being scored");
        }

        [TestMethod]
        public void TheMoveTick_StillScoresTheOldZone_TheNextTickScoresTheNewOne()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 95;
            m.ScoreBoard[1] = 0;
            var w = m.Member(0, 0);
            m.Sample(w, 55, -35, 0.005);

            h.OnTick(m, At(4));

            Assert.AreEqual(95 + Dials.KothHoldPoints, m.ScoreBoard[0], "scored on the old zone on the tick it crossed 1/3");
            Assert.AreEqual(1, h.MovesMade);

            // The player stays at the old centre: no longer in the zone.
            h.OnTick(m, At(8));
            Assert.AreEqual(95 + Dials.KothHoldPoints, m.ScoreBoard[0], "standing on the old hill scores nothing now");
            Assert.AreEqual(1, m.Drains.Count, "only the move tick drained, on the old zone");
        }

        [TestMethod]
        public void AfterTheMove_ThePlayerOnTheNewSiteScores_AndTheOldCentreDoesNot()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 0;
            m.ScoreBoard[1] = 100;
            h.OnTick(m, At(4)); // east leads, hill goes to the WEST room (15, -15)
            Assert.AreEqual(KothSide.West, h.Site.Side);

            var w = m.Member(0, 0);
            m.Sample(w, 15, -15, 0.005);
            h.OnTick(m, At(8));

            Assert.AreEqual(Dials.KothHoldPoints, m.ScoreBoard[0], "west holds the new site");
            Assert.AreEqual(1, m.Drains.Count, "the drain is applied on the new zone");

            m.Samples.Clear();
            m.Sample(w, 55, -35, 0.005);
            var before = m.ScoreBoard[0];
            h.OnTick(m, At(12));

            Assert.AreEqual(before, m.ScoreBoard[0], "the old centre no longer counts");
            Assert.AreEqual(1, m.Drains.Count, "and no drain is applied from the old zone");
        }

        [TestMethod]
        public void Move_ResetsTheHoldingState_SoTheNewHoldIsAnnouncedAfresh()
        {
            var (m, h) = Live();
            var w = m.Member(0, 0);
            m.Sample(w, 55, -35, 0.005);
            m.ScoreBoard[1] = 103; // east leads: west holding drains it by 3 to exactly 1/3, so the move goes west

            // Tick 1 scores west holding the centre ("West holds the hill!") and then the move fires.
            h.OnTick(m, At(4));
            Assert.AreEqual(1, m.Announcements.Count(a => a == BattlegroundText.ZoneHeld(0)));
            Assert.AreEqual(1, h.MovesMade);

            // West moves to the new site and holds it: the held state was reset, so it is announced again.
            m.Samples.Clear();
            m.Sample(w, 15, -15, 0.005);
            h.OnTick(m, At(8));

            Assert.AreEqual(2, m.Announcements.Count(a => a == BattlegroundText.ZoneHeld(0)), "no carried-over holder: the hold is announced again");
        }

        [TestMethod]
        public void TimeFallback_MovesWithoutAnyScore_AndATieUsesTheCoinFlip()
        {
            var (m, h) = Live();
            m.TieBreakWest = true;

            h.OnTick(m, At(299));
            Assert.AreEqual(0, h.MovesMade, "just under 1/3 of the 900 s limit");

            h.OnTick(m, At(300));
            Assert.AreEqual(1, h.MovesMade);
            Assert.AreEqual(KothSide.West, h.Site.Side, "tied, centre, flip says west");
            Assert.IsTrue(m.TieBreakRolls > 0);

            var (m2, h2) = Live();
            m2.TieBreakWest = false;
            h2.OnTick(m2, At(300));
            Assert.AreEqual(KothSide.East, h2.Site.Side, "flip says east");
        }

        /// <summary>The random source is touched only on the tick a tied move off the centre actually fires.</summary>
        [TestMethod]
        public void CoinFlip_IsRolledLazily_OnlyWhenATiedMoveFromTheCentreFires()
        {
            var (m, h) = Live();
            m.TieBreakWest = true;

            for (var t = 4; t < 300; t += 4)
                h.OnTick(m, At(t));

            Assert.AreEqual(0, h.MovesMade);
            Assert.AreEqual(0, m.TieBreakRolls, "no roll over many 0-0 ticks before any threshold");

            h.OnTick(m, At(300));

            Assert.AreEqual(1, h.MovesMade);
            Assert.AreEqual(1, m.TieBreakRolls, "exactly one roll, on the tick the move fires");

            h.OnTick(m, At(600));
            Assert.AreEqual(2, h.MovesMade);
            Assert.AreEqual(1, m.TieBreakRolls, "the move off a side uses the opposite-side rule, no roll");
        }

        /// <summary>The tick that decides the match (a team at or past the target) announces no hill move.</summary>
        [TestMethod]
        public void MatchDecidingTick_SendsNoHillMovedLine()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = Dials.KothScoreTarget;
            m.ScoreBoard[1] = 0;

            h.OnTick(m, At(4));

            Assert.AreEqual(0, h.MovesMade);
            Assert.AreEqual(0, m.ReplacedZones.Count);
            Assert.IsFalse(m.Announcements.Any(a => a.Contains("The hill has moved!")));

            var (m2, h2) = Live();
            m2.ScoreBoard[0] = Dials.KothScoreTarget - 1;
            h2.OnTick(m2, At(4));
            Assert.AreEqual(1, h2.MovesMade, "one point short of the target still moves");
        }

        [TestMethod]
        public void TiedAgain_OffTheCentre_GoesToTheOppositeSide()
        {
            var (m, h) = Live();
            m.TieBreakWest = true;
            h.OnTick(m, At(300));
            Assert.AreEqual(KothSide.West, h.Site.Side);

            h.OnTick(m, At(600));
            Assert.AreEqual(2, h.MovesMade);
            Assert.AreEqual(new KothSiteChoice(1, KothSide.East), h.Site, "tied while in the west room: the opposite room");
        }

        [TestMethod]
        public void EachStepFiresOnce_AndNeverBeyondTheConfiguredMoves()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 299;
            m.ScoreBoard[1] = 0;

            for (var i = 1; i <= 10; i++)
                h.OnTick(m, At(4 * i));

            Assert.AreEqual(2, h.MovesMade, "299 of 300 passes both thresholds but only two steps exist");
            Assert.AreEqual(2, m.ReplacedZones.Count);
            Assert.AreEqual(2, m.Announcements.Count(a => a.Contains("The hill has moved!")));
            Assert.AreEqual(KothSiteChoice.Start, h.Site, "west leads, so step 1 went to the east room and step 2 (east still trailing) returned to the centre");
            Assert.AreEqual(55.0, h.Zone.CenterX, 0.0);
            Assert.AreEqual(-35.0, h.Zone.CenterY, 0.0);
            CollectionAssert.Contains(m.Announcements, Moved("centre"));
        }

        /// <summary>Three moves with the east team trailing throughout: the east room, back to the centre, the east room again, and never a coin flip.</summary>
        [TestMethod]
        public void ThreeMoves_WithEastPersistentlyTrailing_AlternateEastRoomAndCentre_WithoutRolls()
        {
            var (m, h) = Live(Dials with { KothHillMoves = 3 });
            var sites = new System.Collections.Generic.List<KothSiteChoice>();
            m.ScoreBoard[1] = 0;

            foreach (var (west, tickAt) in new[] { (75, 4), (150, 8), (225, 12) })
            {
                m.ScoreBoard[0] = west;
                h.OnTick(m, At(tickAt));
                sites.Add(h.Site);
            }

            CollectionAssert.AreEqual(new[] { new KothSiteChoice(1, KothSide.East), KothSiteChoice.Start, new KothSiteChoice(1, KothSide.East) }, sites);
            Assert.AreEqual(3, h.MovesMade);
            Assert.AreEqual(0, m.TieBreakRolls, "no tie anywhere, so the random source is never touched");
            Assert.AreEqual(95.0, h.Zone.CenterX, 0.0);
        }

        /// <summary>Once the hill has moved, binding a layout (even one with a different centre) leaves the zone and site alone.</summary>
        [TestMethod]
        public void BindLayout_IsIgnoredOnceTheHillHasMoved()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 100;
            h.OnTick(m, At(4));
            Assert.AreEqual(1, h.MovesMade);
            var zone = h.Zone;
            var site = h.Site;

            h.BindLayout(Layout with { ZoneX = 70f, ZonePairs = new[] { new KothSitePair(30f, -20f) } });

            Assert.AreEqual(zone, h.Zone);
            Assert.AreEqual(site, h.Site);
            Assert.AreEqual(1, h.MovesMade);
        }
        [TestMethod]
        public void ZeroMoves_BehavesExactlyAsBefore()
        {
            var (m, h) = Live(Dials with { KothHillMoves = 0 });
            var start = h.Zone;
            m.ScoreBoard[0] = 299;
            m.ScoreBoard[1] = 0;

            for (var i = 1; i <= 100; i++)
                h.OnTick(m, At(4 * i));

            Assert.AreEqual(0, h.MovesMade);
            Assert.AreEqual(start, h.Zone);
            Assert.AreEqual(0, m.ReplacedZones.Count);
            Assert.IsFalse(m.Announcements.Any(a => a.Contains("moved")));
            Assert.AreEqual(0, m.TieBreakRolls);
        }

        [TestMethod]
        public void MovesAreClampedToThree_AndNoLayoutOrNoPairsMeansNoMoves()
        {
            var (m, h) = Live(Dials with { KothHillMoves = 9 });
            m.ScoreBoard[0] = 299;
            for (var i = 1; i <= 50; i++)
                h.OnTick(m, At(4 * i));
            Assert.AreEqual(KothHillSchedule.MaxMoves, h.MovesMade, "9 requested, clamped to 3");
            Assert.AreEqual(3, KothHillSchedule.MaxMoves);

            var m2 = new FakeBattlegroundContext();
            var bare = new KothTickHandler(Dials, BattlegroundModes.KothZoneFor(Layout, Dials));
            bare.OnLive(m2);
            m2.ScoreBoard[0] = 299;
            for (var i = 1; i <= 50; i++)
                bare.OnTick(m2, At(4 * i));
            Assert.AreEqual(0, bare.MovesMade, "the two-argument handler never moves");

            var (m3, h3) = Live(Dials, Layout with { ZonePairs = Array.Empty<KothSitePair>() });
            m3.ScoreBoard[0] = 299;
            for (var i = 1; i <= 50; i++)
                h3.OnTick(m3, At(4 * i));
            Assert.AreEqual(0, h3.MovesMade, "a layout with no pairs never moves");
        }
    }
}