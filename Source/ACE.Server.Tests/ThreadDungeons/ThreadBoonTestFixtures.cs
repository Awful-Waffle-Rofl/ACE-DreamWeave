using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using TreasureDeath = ACE.Database.Models.World.TreasureDeath;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Shared builders for the Thread Shrine tests (ThreadBoonLedgerTests, ThreadBoonHookTests, ThreadLootBoonTests,
    /// ThreadBonusKillTests, ShrineActivationTests, ShrineEncounterTypeTests). No world, no database, no PropertyManager
    /// read: the harness cannot build a Player, so presence is asked through ThreadRunPresence.IsMemberInside's guid and
    /// position overload, which the Player overload forwards to.
    ///
    /// A granted blessing raises a process-wide gate (ThreadBoons.AnyLive); every test that grants releases its runs in a
    /// finally through <see cref="Releaser"/>, so no other test class ever sees the gate up.
    /// </summary>
    internal static class ThreadBoonTestFixtures
    {
        public const uint Owner = 0x50000010u;
        public const uint Member = 0x50000020u;
        public const uint Member2 = 0x50000030u;
        public const uint Stranger = 0x50000099u;

        public const ushort Lb = 0x0150;

        private static uint nextInstance = 0x80007000u;

        public static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        /// <summary>An Active dungeon run on landblock 0x0150 whose roster is Owner plus <paramref name="others"/> (4 placed, nothing killed).</summary>
        public static ThreadDungeonRun Run(params uint[] others) => Run(false, others);

        public static ThreadDungeonRun Run(bool pooled, params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));

            var spec = new DungeonGemSpec("shrine_test", 200, 6, "any", 3, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "shrine_test", Landblock = Lb, ExitPortalWcid = 1003601, Entry = "0x0150018A 0 0 0 1 0 0 0" };
            var instance = System.Threading.Interlocked.Increment(ref nextInstance);
            var run = new ThreadDungeonRun(instance, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));

            if (pooled)
                run.MarkPooledLoot(true);

            run.MarkPopulated(planned: 4, spawned: 4, bossWcid: 0);
            return run;
        }

        /// <summary>A position in the run's copy (its instance, its landblock).</summary>
        public static Position Inside(ThreadDungeonRun run) => new Position(0x0150018Au, 10f, 10f, 0f, 0f, 0f, 0f, 1f, run.Instance);

        /// <summary>The same spot in another instance (the retail world, another copy).</summary>
        public static Position OtherInstance(ThreadDungeonRun run) => new Position(0x0150018Au, 10f, 10f, 0f, 0f, 0f, 0f, 1f, run.Instance + 1000u);

        /// <summary>The run's instance but a landblock the copy does not own.</summary>
        public static Position OffLandblock(ThreadDungeonRun run) => new Position(0x0151018Au, 10f, 10f, 0f, 0f, 0f, 0f, 1f, run.Instance);

        public static IReadOnlyList<ShrineBoonRow> AllKinds(double magnitude = 1.25)
            => Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>().Select(k => new ShrineBoonRow(k, magnitude, 1.0)).ToList();

        public static IReadOnlyList<ShrineBoonRow> Only(ThreadBoonKind kind, double magnitude = 1.25)
            => new[] { new ShrineBoonRow(kind, magnitude, 1.0) };

        /// <summary>One shrine use with a fresh latch; returns the outcome and the kind granted.</summary>
        public static (ShrineUseOutcome Outcome, ThreadBoonKind Kind, double Magnitude) Use(ThreadDungeonRun run, IReadOnlyList<ShrineBoonRow> table,
            IReadOnlyCollection<uint> recipients, double draw = 0.0, ShrineLatch latch = null, bool userInside = true)
        {
            var outcome = run.TryUseShrine(latch ?? new ShrineLatch(), userInside, recipients, table, () => draw, out var kind, out var magnitude);
            return (outcome, kind, magnitude);
        }

        /// <summary>Releases every tracked run's blessings on Dispose (the shrine type's OnRunEnded does this in production).</summary>
        internal sealed class Releaser : IDisposable
        {
            private readonly List<ThreadDungeonRun> runs = new List<ThreadDungeonRun>();

            public ThreadDungeonRun Track(ThreadDungeonRun run)
            {
                runs.Add(run);
                return run;
            }

            public void Dispose()
            {
                foreach (var run in runs)
                    run.ReleaseBoons();
            }
        }

        /// <summary>A run creature (the GroupLootBankTests shape): back-reference, role, level 100, a tier-6 profile.</summary>
        public static Creature RunCreature(ThreadDungeonRun run, DungeonRole role = DungeonRole.Trash, bool bonus = false)
        {
            // The Creature constructor reads the vital formulas (Creature.SetEphemeralValues -> GameTables).
            TestGameTables.EnsureInitialized();

            var creature = new Creature(new Weenie { WeenieClassId = 42, WeenieType = WeenieType.Creature }, new ObjectGuid(ThreadLootTestFixtures.NextGuid()));
            creature.P_DungeonRun = run;
            creature.SetProperty(PropertyInt.ThreadDungeonRunId, (int)run.RunId);
            creature.DungeonRole = role;
            creature.Level = 100;
            creature.DeathTreasureOverride = new TreasureDeath { Tier = 6 };
            creature.IsThreadBonusCreature = bonus;
            return creature;
        }

        public static WorldObject Named(uint guid, string name)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            }, new ObjectGuid(guid));
    }
}
