using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Knots;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Builders shared by PeddlerAccountTests, PeddlerPaymentTests and PeddlerBuyTests. Runs are built directly (no TryStart), so no
    /// PropertyManager key is read, and
    /// every object factory a buy reaches is swapped by <see cref="WithSeams"/> and restored in its finally.
    /// </summary>
    internal static class PeddlerTestFixtures
    {
        public const uint Owner = 0x50000110u;
        public const uint Member = 0x50000120u;
        public const uint Member2 = 0x50000130u;

        public const uint PlainWcid = 801;
        public const uint NoteWcid = 802;
        public static readonly uint[] SalvageWcids = { 811, 812, 813, 814, 815, 816, 817 };

        public static readonly ThreadEncounterPrizeEntry PlainEntry = new ThreadEncounterPrizeEntry("plain", new[] { PlainWcid }, 1, 0);
        public static readonly ThreadEncounterPrizeEntry NoteEntry = new ThreadEncounterPrizeEntry("notes", new[] { NoteWcid }, 1, 1);
        public static readonly ThreadEncounterPrizeEntry SalvageEntry = new ThreadEncounterPrizeEntry("salvage", SalvageWcids, 1, 1, new ThreadEncounterPrizeSalvage(1, 10, 100, 10));
        public static readonly ThreadEncounterPrizeEntry FiberEntry = new ThreadEncounterPrizeEntry("fiber", new[] { KnotItems.FiberWcid }, 2, 1, fiber: true);
        public static readonly ThreadEncounterPrizeEntry PatternEntry = new ThreadEncounterPrizeEntry("pattern", new[] { KnotItems.PatternWcid }, 1, 1, pattern: true);

        public static readonly PinnacleDef Pinnacle = new PinnacleDef { Key = "p1", Name = "P One" };

        private static readonly Dictionary<string, ThreadEncounterPrizeEntry> Entries =
            new[] { PlainEntry, NoteEntry, SalvageEntry, FiberEntry, PatternEntry }.ToDictionary(e => e.Id);

        public static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        /// <summary>A pooled, ACTIVE solo run (two creatures spawned, none killed).</summary>
        public static ThreadDungeonRun ActiveSoloRun(bool pooled = true)
        {
            var run = NewRun(pooled);
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "fixture: Active");
            return run;
        }

        /// <summary>A pooled, ACTIVE group run of Owner plus the given members, each holding a key (so a deal seat).</summary>
        public static ThreadDungeonRun ActiveGroupRun(params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80005679u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
            run.MarkPooledLoot(true);

            foreach (var other in others)
                run.SetMemberKey(other, 0x80002000u + (other & 0xFFFu));

            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "fixture: Active");
            Assert.IsTrue(run.IsGroup, "fixture: group");
            return run;
        }

        /// <summary>Banks <paramref name="count"/> kills of <paramref name="rolls"/> rolls each.</summary>
        public static void Bank(ThreadDungeonRun run, int count, int rolls = 1)
        {
            for (var i = 0; i < count; i++)
                Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null, rolls, 0)), "fixture: banked");
        }

        /// <summary>Kills both spawned creatures: the run clears.</summary>
        public static void Clear(ThreadDungeonRun run)
        {
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: Cleared");
        }

        /// <summary>Hands every ledger roll out (what delivery or the trickle does), so the clear-time pile is complete.</summary>
        public static void DrainLedger(ThreadDungeonRun run)
        {
            while (run.TryClaimNextLootEntry(out _)) { }
        }

        public static WorldObject Generic(uint wcid = 30000, int? knotKind = null)
        {
            var weenie = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic, PropertiesInt = new Dictionary<PropertyInt, int>() };

            if (knotKind.HasValue)
                weenie.PropertiesInt[PropertyInt.KnotItemKind] = knotKind.Value;

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        public static List<HeldPileItem> Pile(ThreadDungeonRun run, uint guid)
        {
            var taken = run.TakeHeldPile(guid);

            foreach (var held in taken)
                Assert.IsTrue(run.AddToHeldPile(guid, held), "put the pile back");

            return taken;
        }

        public static List<HeldPileItem> AddPlain(ThreadDungeonRun run, uint guid, int count)
        {
            var added = new List<HeldPileItem>();

            for (var i = 0; i < count; i++)
            {
                var held = new HeldPileItem(Generic(), null, 1);
                Assert.IsTrue(run.AddToHeldPile(guid, held));
                added.Add(held);
            }

            return added;
        }

        // ---- the clear-only shop -----------------------------------------------------------------------------------

        /// <summary>A plan whose offers are plain items at <paramref name="prices"/> (default one offer at 5).</summary>
        public static PeddlerPlan TestPlan(params int[] prices)
        {
            if (prices == null || prices.Length == 0)
                prices = new[] { 5 };

            var offers = prices.Select((p, i) => new PeddlerOffer(new ThreadEncounterPrizePick(PlainEntry, new[] { PlainWcid }), p, i == 0)).ToList();
            return new PeddlerPlan(null, new PeddlerConfig(), offers, 1.0);
        }

        /// <summary>
        /// <see cref="TestPlan"/> whose config caps <paramref name="onePerPlayer"/> at one per player (PeddlerConfig.OnePerPlayer):
        /// what ThreadDungeonRun.TryChargePeddlerPurchase and ThreadPeddlerVendor.RowsFor read through the run's plan.
        /// </summary>
        public static PeddlerPlan CappedPlan(IEnumerable<string> onePerPlayer, params int[] prices)
        {
            var plain = TestPlan(prices);
            var config = new PeddlerConfig { OnePerPlayer = new HashSet<string>(onePerPlayer, StringComparer.Ordinal) };
            return new PeddlerPlan(null, config, plain.Offers, 1.0);
        }

        /// <summary>
        /// A percent-priced plan (owner ruling 2026-11-20) matching ThreadPeddlerVendorTests' three listings: notes flat 50 at
        /// 6.25%, plain flat 240 at 30%, and a Pattern for <see cref="Pinnacle"/> flat 320 at 80%, each with jitter factor
        /// <paramref name="jitter"/>. The spawn multiplier is 1.6 on purpose: it must NOT reach the percent term.
        /// </summary>
        public static PeddlerPlan PercentPlan(double jitter = 1.0, IEnumerable<string> onePerPlayer = null)
        {
            var offers = new List<PeddlerOffer>
            {
                new PeddlerOffer(new ThreadEncounterPrizePick(NoteEntry, new[] { NoteWcid }), 50, true, jitter),
                new PeddlerOffer(new ThreadEncounterPrizePick(PlainEntry, new[] { PlainWcid }), 240, false, jitter),
                new PeddlerOffer(new ThreadEncounterPrizePick(PatternEntry, new[] { KnotItems.PatternWcid }, 0, Pinnacle), 320, false, jitter),
            };

            var config = new PeddlerConfig
            {
                PricePercent = new Dictionary<string, double> { { NoteEntry.Id, 6.25 }, { PlainEntry.Id, 30 }, { PatternEntry.Id, 80 } },
                OnePerPlayer = new HashSet<string>(onePerPlayer ?? Array.Empty<string>(), StringComparer.Ordinal),
            };

            return new PeddlerPlan(null, config, offers, 1.6);
        }

        /// <summary>Gives an ACTIVE run its peddler: the claim, then the plan.</summary>
        public static PeddlerPlan GivePlan(ThreadDungeonRun run, PeddlerPlan plan = null)
        {
            plan ??= TestPlan();
            Assert.IsTrue(run.TryClaimPeddler(), "fixture: claimed");
            Assert.IsTrue(run.TrySetPeddlerPlan(plan), "fixture: plan recorded");
            return plan;
        }

        /// <summary>Clears the run, hands the whole ledger out, and gives each guid <paramref name="plain"/> ordinary pile items.</summary>
        public static void ClearWithPiles(ThreadDungeonRun run, int plain, params uint[] guids)
        {
            Clear(run);
            DrainLedger(run);
            Assert.IsTrue(run.PeddlerClearPileComplete, "fixture: the clear pile is complete");

            foreach (var guid in guids)
                AddPlain(run, guid, plain);
        }

        /// <summary>Opens one member's shop the way Present does: the gate says Present, then the run opens it.</summary>
        public static void OpenShop(ThreadDungeonRun run, uint guid)
        {
            Assert.AreEqual(PeddlerGate.Present, run.PeddlerGateFor(guid), "fixture: the gate presents");
            Assert.IsTrue(run.TryOpenPeddlerShop(guid), "fixture: shop open");
        }

        /// <summary>What the factories built while a body ran.</summary>
        public sealed class Seams
        {
            /// <summary>What ThreadPeddlerPayment.KnotsInputs returns; default: open, at the run's level, with <see cref="Pinnacle"/> in the pool.</summary>
            public Func<ThreadDungeonRun, (int? FiberLevel, IReadOnlyList<PinnacleDef> PatternPool)> Knots = r => (r.KnotLevel, new[] { Pinnacle });

            /// <summary>Called before every CreateObject; may throw to simulate a failing factory.</summary>
            public Action<uint> BeforeCreate = _ => { };

            /// <summary>Guards every Add below: the concurrent-buy test runs the factories on two threads at once.</summary>
            public readonly object Sync = new object();

            public readonly List<uint> Created = new List<uint>();
            public readonly List<WorldObject> Objects = new List<WorldObject>();
            public readonly List<int> FiberLevels = new List<int>();
            public readonly List<PinnacleDef> Patterns = new List<PinnacleDef>();
        }

        /// <summary>
        /// Runs <paramref name="body"/> with every object seam a buy reaches swapped to in-memory builders, and the
        /// purchase entry lookup pointed at this file's entries; restores each in a finally.
        /// </summary>
        public static void WithSeams(Action<Seams> body)
        {
            var seams = new Seams();

            var oCreate = ThreadEncounterPrize.CreateObject;
            var oExists = ThreadEncounterPrize.WeenieExists;
            var oFiber = KnotDrops.FiberFactory;
            var oPattern = KnotDrops.PatternFactory;
            var oLookup = ThreadPeddlerPayment.EntryLookup;
            var oKnots = ThreadPeddlerPayment.KnotsInputs;

            try
            {
                ThreadEncounterPrize.CreateObject = w => { seams.BeforeCreate(w); var o = Generic(w); lock (seams.Sync) { seams.Created.Add(w); seams.Objects.Add(o); } return o; };
                ThreadPeddlerPayment.KnotsInputs = r => seams.Knots(r);
                ThreadEncounterPrize.WeenieExists = _ => true;
                KnotDrops.FiberFactory = level => { lock (seams.Sync) seams.FiberLevels.Add(level); return Generic(KnotItems.FiberWcid, KnotItemKind.Fiber); };
                KnotDrops.PatternFactory = def => { lock (seams.Sync) seams.Patterns.Add(def); return Generic(KnotItems.PatternWcid, KnotItemKind.Pattern); };
                ThreadPeddlerPayment.EntryLookup = id => id != null && Entries.TryGetValue(id, out var e) ? e : null;

                body(seams);
            }
            finally
            {
                ThreadEncounterPrize.CreateObject = oCreate;
                ThreadEncounterPrize.WeenieExists = oExists;
                KnotDrops.FiberFactory = oFiber;
                KnotDrops.PatternFactory = oPattern;
                ThreadPeddlerPayment.EntryLookup = oLookup;
                ThreadPeddlerPayment.KnotsInputs = oKnots;
            }
        }

        /// <summary>A delivery world that records every form call; nobody is inside unless named.</summary>
        public static ThreadGroupCacheDelivery.DeliveryWorld World(List<uint> formCalls, params uint[] inside)
        {
            var insideSet = new HashSet<uint>(inside);

            return new ThreadGroupCacheDelivery.DeliveryWorld
            {
                IsInside = g => insideSet.Contains(g),
                FormAt = (g, hasMore) => { formCalls.Add(g); return null; },
                MoveFor = g => { },
                Tell = (g, t) => { },
                Deliver = (c, r) => { },
                Materialize = _ => new List<WorldObject>(),
                BuildBonus = (r, b) => throw new AssertFailedException("no bonus is pending"),
            };
        }
    }
}
