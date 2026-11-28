using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Avatar fighter's aura (owner direction 2026-11-25: a boon to the team while it stands). The harness cannot build
    /// a Player, so the read is proven in two halves, as ThreadBoonHookTests does: what the Player-free read returns for a
    /// member after a REAL placed fighter (FighterRunHarness, the live hooks behind seams) published its membership, in range,
    /// out of range, in another instance, held, after the fighter's death, after the run's removal, and once stale; and source
    /// pins that both rating getters fold exactly that read into their base term, for players only, and that the Thorns
    /// reflect takes it back out. Reads no PropertyManager key. GauntletAura's statics are reset after every test.
    /// </summary>
    [TestClass]
    public class GauntletAuraTests
    {
        private const uint Near = 0x500000B1;
        private const uint Far = 0x500000B2;
        private const uint Held = 0x500000B3;
        private const uint Elsewhere = 0x500000B4;

        private Func<double> savedNow;

        [ClassInitialize]
        public static void Init(TestContext _) => GauntletFighterFixtures.EnsureInitialized();

        [TestInitialize]
        public void Setup()
        {
            savedNow = GauntletAura.Now;
            GauntletAura.ResetForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            GauntletAura.Now = savedNow;
            GauntletAura.ResetForTests();
        }

        private static Position Offset(Position from, float dx, uint? instance = null)
        {
            var p = new Position(from) { PositionX = from.PositionX + dx };

            if (instance.HasValue)
                p.Instance = instance.Value;

            return p;
        }

        /// <summary>A live run's placed fighter whose members are: Near (10 m), Far (40 m), Held (5 m, held) and Elsewhere (5 m, another instance).</summary>
        private static (FighterRunHarness h, GauntletRun run, GauntletAvatarFighter fighter) PlacedWithMembers(GauntletTuning tuning = null)
        {
            var h = new FighterRunHarness();

            if (tuning != null)
                h.Tuning = tuning;

            var run = h.StartActive();
            var fighter = h.Placed.Single();
            var at = fighter.Location;

            fighter.AuraCandidatesSeam = () => new[]
            {
                new GauntletAura.AuraCandidate(Near, false, Offset(at, 10)),
                new GauntletAura.AuraCandidate(Far, false, Offset(at, 40)),
                new GauntletAura.AuraCandidate(Held, true, Offset(at, 5)),
                new GauntletAura.AuraCandidate(Elsewhere, false, Offset(at, 5, at.Instance + 1)),
            };

            return (h, run, fighter);
        }

        // ---- the pure rule ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Membership_AliveNotDownSameInstanceWithinRadius()
        {
            Assert.IsTrue(GauntletAura.IsAuraMember(true, false, true, 30.0, 30.0), "on the edge counts");
            Assert.IsTrue(GauntletAura.IsAuraMember(true, false, true, 0.0, 30.0));
            Assert.IsFalse(GauntletAura.IsAuraMember(true, false, true, 30.01, 30.0), "outside the radius");
            Assert.IsFalse(GauntletAura.IsAuraMember(false, false, true, 1.0, 30.0), "a dead fighter gives nothing");
            Assert.IsFalse(GauntletAura.IsAuraMember(true, true, true, 1.0, 30.0), "a down (dead or held) member gets nothing");
            Assert.IsFalse(GauntletAura.IsAuraMember(true, false, false, 1.0, 30.0), "another instance gets nothing");
            Assert.IsFalse(GauntletAura.IsAuraMember(true, false, true, 1.0, 0.0), "radius 0 is the aura off");
            Assert.IsFalse(GauntletAura.IsAuraMember(true, false, true, 1.0, double.NaN));
            Assert.IsFalse(GauntletAura.IsAuraMember(true, false, true, double.NaN, 30.0));
        }

        [TestMethod]
        public void Members_FromPositions()
        {
            var at = GauntletFighterFixtures.At(50, GauntletFighterFixtures.InstanceA);
            var candidates = new[]
            {
                new GauntletAura.AuraCandidate(Near, false, Offset(at, 29)),
                new GauntletAura.AuraCandidate(Far, false, Offset(at, 31)),
                new GauntletAura.AuraCandidate(Held, true, Offset(at, 1)),
                new GauntletAura.AuraCandidate(Elsewhere, false, Offset(at, 1, GauntletFighterFixtures.InstanceB)),
                new GauntletAura.AuraCandidate(0x500000B5, false, null),
            };

            CollectionAssert.AreEquivalent(new[] { Near }, GauntletAura.Members(candidates, at, true, 30.0).ToList());
            Assert.AreEqual(0, GauntletAura.Members(candidates, at, false, 30.0).Count, "a dead fighter covers nobody");
            Assert.AreEqual(0, GauntletAura.Members(candidates, null, true, 30.0).Count);
            Assert.AreEqual(0, GauntletAura.Members(null, at, true, 30.0).Count);
        }

        // ---- what a member's rating read receives from a placed fighter ----------------------------------------------------------

        [TestMethod]
        public void PlacedFighter_PublishesItsRunsAura_ToMembersInRangeOnly()
        {
            var (_, run, fighter) = PlacedWithMembers();

            Assert.IsTrue(fighter.AuraArmed, "the Gauntlet placement arms the aura from the run's tuning");
            Assert.AreEqual(run.RunKey, fighter.AuraRunKey);
            Assert.AreEqual(30.0, fighter.AuraRadius);
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance), "fixture: nothing before the first publish");

            Assert.IsTrue(fighter.TickAura(1000.0), "the first tick publishes");

            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, run.Instance), "in range: +10 damage rating");
            Assert.AreEqual(10, GauntletAura.DamageResistRatingFor(Near, run.Instance), "in range: +10 damage resist rating");
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Far, run.Instance), "40 m: out of range");
            Assert.AreEqual(0, GauntletAura.DamageResistRatingFor(Far, run.Instance));
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Held, run.Instance), "held in the death pen");
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Elsewhere, run.Instance), "not in the fighter's instance");
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance + 1), "a read from another instance");
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, 0), "the open world");

            Assert.IsFalse(fighter.TickAura(1000.5), "at most one publish per second");
            Assert.IsTrue(fighter.TickAura(1001.0));
        }

        [TestMethod]
        public void TheRatingsComeFromTheRunsTuning()
        {
            var (_, run, fighter) = PlacedWithMembers(GauntletTuning.Defaults with { FighterAuraDamageRating = 7, FighterAuraResistRating = 13, FighterAuraRadius = 50.0 });

            fighter.TickAura(1000.0);

            Assert.AreEqual(7, GauntletAura.DamageRatingFor(Near, run.Instance));
            Assert.AreEqual(13, GauntletAura.DamageResistRatingFor(Near, run.Instance));
            Assert.AreEqual(7, GauntletAura.DamageRatingFor(Far, run.Instance), "the tuned 50 m radius takes in the 40 m member");
        }

        [TestMethod]
        public void RatingsOfZero_OrRadiusZero_IsTheAuraOff()
        {
            var (_, run, off) = PlacedWithMembers(GauntletTuning.Defaults with { FighterAuraDamageRating = 0, FighterAuraResistRating = 0 });
            Assert.IsFalse(off.AuraArmed);
            Assert.IsFalse(off.TickAura(1000.0));
            Assert.IsFalse(GauntletAura.AnyLive);

            var (_, _, noRadius) = PlacedWithMembers(GauntletTuning.Defaults with { FighterAuraRadius = 0 });
            Assert.IsFalse(noRadius.AuraArmed);
            Assert.IsFalse(noRadius.TickAura(1000.0));
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance));
        }

        [TestMethod]
        public void Death_ClearsTheAuraAtOnce()
        {
            var (_, run, fighter) = PlacedWithMembers();
            fighter.TickAura(1000.0);
            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, run.Instance), "fixture: covered");

            // the real death entry point, through the fighter's override
            fighter.OnDeath(null, DamageType.Slash);

            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance), "the aura goes the moment it falls");
            Assert.AreEqual(0, GauntletAura.DamageResistRatingFor(Near, run.Instance));
            Assert.IsFalse(GauntletAura.AnyLive);
            Assert.IsFalse(fighter.TickAura(1005.0), "a dead fighter never publishes again");
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance));
        }

        [TestMethod]
        public void TheRespawnedFighter_PublishesAgain()
        {
            var (h, run, first) = PlacedWithMembers();
            first.OnDeath(null, DamageType.Slash);
            h.Advance(h.Tuning.FighterRespawnSeconds + 1.5);

            var second = h.Placed.Last();
            Assert.AreNotSame(first, second, "fixture: respawned");
            Assert.IsTrue(second.AuraArmed, "the respawn is armed too");

            second.AuraCandidatesSeam = () => new[] { new GauntletAura.AuraCandidate(Near, false, Offset(second.Location, 3)) };
            Assert.IsTrue(second.TickAura(2000.0));
            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, run.Instance));
        }

        [TestMethod]
        public void TheRunsRemoval_ClearsTheAura()
        {
            var (h, run, fighter) = PlacedWithMembers();
            fighter.TickAura(1000.0);
            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, run.Instance), "fixture: covered");

            h.Hooks.Remove(run);

            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance));
            Assert.IsFalse(fighter.TickAura(1001.0), "an unbound fighter (its copy no longer live) publishes nothing");
            Assert.IsFalse(GauntletAura.AnyLive);
        }

        [TestMethod]
        public void AStaleSnapshot_CountsForNothing()
        {
            var clock = 5000.0;
            GauntletAura.Now = () => clock;

            var (_, run, fighter) = PlacedWithMembers();
            fighter.TickAura(1.0);
            Assert.AreEqual(5000.0, GauntletAura.For(run.Instance).PublishedAt, "stamped with the aura clock");

            clock = 5000.0 + GauntletAura.StaleSeconds;
            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, run.Instance), "3 s old: still counts");

            clock = 5000.0 + GauntletAura.StaleSeconds + 0.01;
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance), "older than 3 s: a fighter that stopped publishing gives nothing");
            Assert.AreEqual(0, GauntletAura.DamageResistRatingFor(Near, run.Instance));

            // the next publish restores it
            fighter.TickAura(2.0);
            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, run.Instance));
        }

        [TestMethod]
        public void ADeadFightersClear_NeverRemovesAnotherFightersAura()
        {
            var a = new object();
            var b = new object();

            GauntletAura.Publish(new GauntletAura.Snapshot(1, 0x7F020001, a, GauntletAura.Now(), 10, 10, new[] { Near }));
            GauntletAura.Publish(new GauntletAura.Snapshot(2, 0x7F020002, b, GauntletAura.Now(), 10, 10, new[] { Near }));

            Assert.IsTrue(GauntletAura.ClearFrom(a));
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, 0x7F020001));
            Assert.AreEqual(10, GauntletAura.DamageRatingFor(Near, 0x7F020002), "the other run's aura stands");
            Assert.IsFalse(GauntletAura.ClearFrom(a), "already gone");
            Assert.IsTrue(GauntletAura.ClearRun(2));
            Assert.IsFalse(GauntletAura.AnyLive);
        }

        // ---- the hook sites (source pins) -------------------------------------------------------------------------------------------

        private const string RatingSource = "Source/ACE.Server/WorldObjects/Creature_Rating.cs";

        [TestMethod]
        public void BothRatingGetters_FoldTheAuraIntoTheBaseTerm_ForPlayersOnly()
        {
            var source = PooledLootSourceText.Read(RatingSource);

            var damage = PooledLootSourceText.MethodBody(source, "public int GetDamageRating()");
            var dHook = damage.IndexOf("if (!blessingFreeRatings && this is Player gaPlayer)\n                damageRating += ACE.Server.Avatars.Gauntlet.GauntletAura.DamageRatingFor(gaPlayer);", StringComparison.Ordinal);
            Assert.IsTrue(dHook >= 0, "GetDamageRating adds the aura for a player");
            Assert.IsTrue(dHook < damage.IndexOf("return damageRating", StringComparison.Ordinal), "before the sum is returned");

            var resist = PooledLootSourceText.MethodBody(source, "public int GetDamageResistRating(CombatType? combatType = null, bool directDamage = true)");
            var rHook = resist.IndexOf("if (!blessingFreeRatings && this is Player gaPlayer)\n                damageResistRating += ACE.Server.Avatars.Gauntlet.GauntletAura.DamageResistRatingFor(gaPlayer);", StringComparison.Ordinal);
            Assert.IsTrue(rHook >= 0, "GetDamageResistRating adds the aura for a player");
            Assert.IsTrue(rHook < resist.IndexOf("return damageResistRating", StringComparison.Ordinal));

            Assert.AreEqual(1, Count(damage, "GauntletAura."), "one read in GetDamageRating");
            Assert.AreEqual(1, Count(resist, "GauntletAura."), "one read in GetDamageResistRating");
        }

        [TestMethod]
        public void TheAura_NeverGrowsTheThornsReflect_SourcePin()
        {
            // Player.ApplyThornsReflect reads each rating ONCE, through the blessing-free reads (no shrine blessing, no aura),
            // never total-minus-blessing, which could straddle an aura publish between its two reads.
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs");
            var reflect = PooledLootSourceText.MethodBody(src, "public void ApplyThornsReflect(Creature attacker, DamageType damageType, double strengthMultiplier)");

            StringAssert.Contains(reflect, "GetPositiveRatingMod(GetDamageRatingWithoutBlessings())");
            StringAssert.Contains(reflect, "RatingAbility.DamageResistRatingThornsMultiplier(drrGrant, GetDamageResistRatingWithoutBlessings())");
            Assert.AreEqual(0, Count(reflect, "GetDamageRating()"), "no total damage rating read left");
            Assert.AreEqual(0, Count(reflect, "GetDamageResistRating("), "no total resist rating read left");
            Assert.AreEqual(0, Count(reflect, "GauntletAura."), "no second aura read to race");
            Assert.AreEqual(0, Count(reflect, "ThreadBoons."), "no second shrine read to race");
        }

        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<ACE.Entity.Enum.Properties.PositionType, Position> PositionCache(WorldObject wo);

        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<ACE.Entity.Enum.Properties.PositionType, Position> EphemeralPositions(WorldObject wo);

        [TestMethod]
        public void TheBlessingFreeReads_CannotIncludeTheAura_OnARealPlayer()
        {
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();
            // an uninitialized Player has neither position dictionary
            PositionCache(player) = new Dictionary<ACE.Entity.Enum.Properties.PositionType, Position>();
            EphemeralPositions(player) = new Dictionary<ACE.Entity.Enum.Properties.PositionType, Position>();
            player.Location = GauntletFighterFixtures.At(50, GauntletFighterFixtures.InstanceA);
            Assert.AreEqual(GauntletFighterFixtures.InstanceA, player.Location.Instance, "fixture: the player stands in the copy");

            var savedGate = GauntletAura.IsSuppressed;

            try
            {
                GauntletAura.IsSuppressed = _ => false;

                var damageBefore = player.GetDamageRating();
                var resistBefore = player.GetDamageResistRating();

                GauntletAura.Publish(new GauntletAura.Snapshot(1, GauntletFighterFixtures.InstanceA, new object(), GauntletAura.Now(), 10, 10, new[] { player.Guid.Full }));

                Assert.AreEqual(damageBefore + 10, player.GetDamageRating(), "the getter carries the aura");
                Assert.AreEqual(resistBefore + 10, player.GetDamageResistRating(), "the getter carries the aura");
                Assert.AreEqual(damageBefore, player.GetDamageRatingWithoutBlessings(), "the Thorns read never does");
                Assert.AreEqual(resistBefore, player.GetDamageResistRatingWithoutBlessings(), "the Thorns read never does");
                Assert.AreEqual(damageBefore + 10, player.GetDamageRating(), "and the flag is cleared after the blessing-free read");

                // the PvP gate (class abilities never PvP): a suppressed member gets none of it
                GauntletAura.IsSuppressed = _ => true;
                Assert.AreEqual(damageBefore, player.GetDamageRating(), "suppressed (PK facet or arena mask): no aura");
                Assert.AreEqual(resistBefore, player.GetDamageResistRating());
            }
            finally
            {
                GauntletAura.IsSuppressed = savedGate;
            }
        }

        [TestMethod]
        public void ThePvpGate_IsTheClassAbilityOne()
        {
            var aura = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAura.cs");
            StringAssert.Contains(aura, "internal static Func<Player, bool> IsSuppressed = p => p.ClassAbilitySuppressed;");

            foreach (var sig in new[] { "public static int DamageRatingFor(Player player)", "public static int DamageResistRatingFor(Player player)" })
                StringAssert.Contains(PooledLootSourceText.MethodBody(aura, sig), "IsSuppressed(player)", sig);
        }

        [TestMethod]
        public void ADeathDuringTheMemberRead_NeverLeavesTheAuraUp()
        {
            // the death lands while TickAura builds its set: ReportDeathOnce's clear runs BEFORE the publish
            var (_, run, fighter) = PlacedWithMembers();
            fighter.AuraCandidatesSeam = () =>
            {
                fighter.ReportDeathOnce();
                return new[] { new GauntletAura.AuraCandidate(Near, false, Offset(fighter.Location, 3)) };
            };

            Assert.IsFalse(fighter.TickAura(1000.0), "the publish is withdrawn");
            Assert.AreEqual(0, GauntletAura.DamageRatingFor(Near, run.Instance), "no aura from a fighter that fell mid-publish");
            Assert.IsFalse(GauntletAura.AnyLive);
        }

        [TestMethod]
        public void TheFighter_PublishesBesideTheLeaderRefresh_AndThePlacementArmsIt()
        {
            var fighter = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAvatarFighter.cs");
            var find = PooledLootSourceText.MethodBody(fighter, "public override void HandleFindTarget()");

            var leader = find.IndexOf("RefreshLeader(Timers.RunningTime);", StringComparison.Ordinal);
            var aura = find.IndexOf("TickAura(Timers.RunningTime);", StringComparison.Ordinal);
            var baseFind = find.IndexOf("base.HandleFindTarget();", StringComparison.Ordinal);
            Assert.IsTrue(leader >= 0 && leader < aura && aura < baseFind, "the aura tick sits beside the leader refresh, every monster tick");

            var hooks = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletFighterHooks.cs");
            var place = PooledLootSourceText.MethodBody(hooks, "internal GauntletAvatarFighter PlaceNew(Slot slot)");
            StringAssert.Contains(place, "fighter.ArmAura(run.RunKey, tuning.FighterAuraRadius, tuning.FighterAuraDamageRating, tuning.FighterAuraResistRating);");
        }

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
