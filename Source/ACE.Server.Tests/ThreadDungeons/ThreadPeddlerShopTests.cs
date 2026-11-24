using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadPeddlerVendorTests;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The clear-only Wandering Peddler's flow through the delivery seams (ThreadPeddlerShop): the hold gate in the solo seam
    /// (ThreadCachePlacer.SoloPeddlerHoldsDelivery) and the group seam (ThreadGroupCacheDelivery.ServeMember), presentation
    /// (one NPC per run, only for a member who can afford something, never when nobody can), and the release that
    /// then forms the rest of the pile (each purchase was already paid and handed over at the buy). The world is the group DeliveryWorld seam plus an in-memory createAt; ThreadPeddlerShop.RequestPlacement
    /// and every object factory are swapped and restored in a finally (ThreadPeddlerVendorTests.WithReleases,
    /// PeddlerTestFixtures.WithSeams). The vendor needs the ThreadPeddlerVendorTests constructibility seed and its one
    /// PropertyManager key, restored in TestCleanup.
    /// </summary>
    [TestClass]
    public class ThreadPeddlerShopTests
    {
        [ClassInitialize]
        public static void PrimePlayerType(TestContext _)
        {
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Setup()
        {
            SeedConstructible();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ACE.Server.Managers.PropertyManager.ModifyBool("vendor_shop_uses_generator", ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
        }

        /// <summary>The presentation world: who was told what, and every NPC createAt built (and for whom).</summary>
        private sealed class Scene
        {
            public ThreadDungeonRun Run;
            public readonly List<(uint Guid, string Text)> Told = new List<(uint, string)>();
            public readonly List<uint> CreatedFor = new List<uint>();
            public readonly List<ThreadPeddlerVendor> Npcs = new List<ThreadPeddlerVendor>();
            public readonly List<uint> FormCalls = new List<uint>();
            public readonly HashSet<uint> Inside = new HashSet<uint>();
            public bool CreateFails;

            /// <summary>A percent plan: each NPC built gets the production member builder for it.</summary>
            public PeddlerPlan Plan;

            public ThreadPeddlerVendor CreateAt(Player member, PeddlerPlan plan)
            {
                CreatedFor.Add(member.Guid.Full);

                if (CreateFails)
                    return null;

                var npc = BoundStall(Run).Peddler;

                if (Plan != null)
                    npc.MemberListings = g => PeddlerEncounterType.BuildMemberListings(Run, Plan, g);

                Npcs.Add(npc);
                return npc;
            }

            public PeddlerPresentResult Present(uint guid)
                => ThreadPeddlerShop.Present(Run, Viewer(guid), CreateAt, (p, t) => Told.Add((p.Guid.Full, t)));

            public ThreadGroupCacheDelivery.DeliveryWorld World()
            {
                var world = PeddlerTestFixtures.World(FormCalls, Inside.ToArray());
                world.IsInside = g => Inside.Contains(g);
                world.PresentPeddler = Present;
                return world;
            }

            public bool ToldNothingAffordable(uint guid) => Told.Contains((guid, ThreadPeddlerVendor.NothingAffordableMessage));
        }

        /// <summary>A cleared group run with a plan (cheapest offer 5), each member given the pile size named.</summary>
        private static Scene GroupScene(params (uint Guid, int Plain)[] members)
        {
            var others = members.Select(m => m.Guid).Where(g => g != Owner).ToArray();
            var run = ActiveGroupRun(others);
            GivePlan(run, TestPlan(5, 20));
            Clear(run);
            DrainLedger(run);

            var scene = new Scene { Run = run };

            foreach (var (guid, plain) in members)
            {
                AddPlain(run, guid, plain);
                scene.Inside.Add(guid);
            }

            return scene;
        }

        private static Scene SoloScene(int plain)
        {
            var run = ActiveSoloRun();
            GivePlan(run, TestPlan(5, 20));
            Clear(run);
            DrainLedger(run);
            AddPlain(run, run.OwnerGuid, plain);
            return new Scene { Run = run };
        }

        // ======================================================================================
        // solo seam
        // ======================================================================================

        [TestMethod]
        public void Solo_hold_forms_no_cache_until_done_and_rearms_no_retry()
        {
            WithReleases(requests =>
            {
                var scene = SoloScene(30);
                var run = scene.Run;
                var owner = run.OwnerGuid;

                Assert.IsTrue(ThreadCachePlacer.SoloPeddlerHoldsDelivery(run, true, () => scene.Present(owner)), "presented: Execute stops before any cache");
                Assert.IsTrue(run.IsPeddlerShopOpen(owner));
                CollectionAssert.AreEqual(new[] { (owner, ThreadPeddlerVendor.PresentedMessage), (owner, ThreadPeddlerVendor.ExclusionMessage) }, scene.Told, "the two lines, in order");
                CollectionAssert.AreEqual(new[] { owner }, scene.CreatedFor, "the NPC forms at the owner");

                for (var i = 0; i < 5; i++)
                    Assert.IsTrue(ThreadCachePlacer.SoloPeddlerHoldsDelivery(run, true, () => scene.Present(owner)), "every later step while held stops too");

                Assert.IsFalse(run.IsPlacementWanted, "a held owner never re-arms the Tick retry");
                Assert.AreEqual(1, scene.CreatedFor.Count, "and never builds a second NPC");
                Assert.AreEqual(0, run.PeddlerBought(owner).Count);
                Assert.AreEqual(0, requests.Count);
            });
        }

        [TestMethod]
        public void Solo_buy_pays_at_once_then_the_release_forms_from_the_rest()
        {
            WithSeams(_ => WithReleases(requests =>
            {
                var scene = SoloScene(30);
                var run = scene.Run;
                var owner = run.OwnerGuid;

                Assert.IsTrue(ThreadCachePlacer.SoloPeddlerHoldsDelivery(run, true, () => scene.Present(owner)));
                Assert.IsTrue(run.TryChargePeddlerPurchase(owner, new[] { new PeddlerPurchase(PlainEntry.Id, 4) }, (c, n) => ThreadPeddlerPayment.PickPaymentItems(c, n, new Random(3)), out var paid).IsCharged);
                Assert.AreEqual(4, paid.Count, "four paid at the buy");
                Assert.AreEqual(30 - 4, Pile(run, owner).Count, "while the shop is still open");

                Assert.IsTrue(ThreadPeddlerShop.Release(run, owner, "test"));
                CollectionAssert.AreEqual(new[] { owner }, requests.ToArray(), "the release requests the placement");

                Assert.IsFalse(ThreadCachePlacer.SoloPeddlerHoldsDelivery(run, true, () => scene.Present(owner)), "released: Execute goes on");

                var pile = Pile(run, owner);
                Assert.AreEqual(30 - 4, pile.Count, "the pile the caches then form from: net of the payment, no purchase in it");
                Assert.AreEqual(0, pile.Count(h => h.PeddlerPurchase), "the purchase went to the pack at the buy");
            }));
        }

        [TestMethod]
        public void Solo_owner_outside_waits_for_arrival_and_an_incomplete_pile_waits()
        {
            var scene = SoloScene(30);
            var run = scene.Run;

            Assert.IsTrue(ThreadCachePlacer.SoloPeddlerHoldsDelivery(run, false, () => scene.Present(run.OwnerGuid)), "outside: wait");
            Assert.IsTrue(run.IsPlacementWanted, "retried when the owner is seen inside, as an undelivered pile is");
            Assert.AreEqual(0, scene.CreatedFor.Count, "nothing is presented to an absent owner");
            Assert.IsFalse(run.IsPeddlerShopDone(run.OwnerGuid), "and nothing is decided for them");

            var early = ActiveSoloRun();
            GivePlan(early);
            Bank(early, 3);
            Clear(early);
            Assert.IsTrue(ThreadCachePlacer.SoloPeddlerHoldsDelivery(early, true, () => throw new AssertFailedException("never presented on an incomplete pile")));
            Assert.IsTrue(early.IsPlacementWanted);
        }

        [TestMethod]
        public void SpawnCache_releases_an_open_shop_once()
        {
            WithReleases(requests =>
            {
                var scene = SoloScene(30);
                var owner = scene.Run.OwnerGuid;
                Assert.IsFalse(ACE.Server.Command.Handlers.ThreadDungeonCommands.SpawnCacheEndsPeddlerShop(scene.Run, owner), "no shop open yet: /spawncache takes its normal path");

                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(owner));
                Assert.IsTrue(ACE.Server.Command.Handlers.ThreadDungeonCommands.SpawnCacheEndsPeddlerShop(scene.Run, owner));
                Assert.IsFalse(ACE.Server.Command.Handlers.ThreadDungeonCommands.SpawnCacheEndsPeddlerShop(scene.Run, owner), "a second /spawncache is the normal path again");

                CollectionAssert.AreEqual(new[] { owner }, requests.ToArray());
                Assert.IsTrue(scene.Run.EncounterOutcomes.Any(l => l.Contains("done (/spawncache)")));
            });
        }

        // ======================================================================================
        // group seam
        // ======================================================================================

        [TestMethod]
        public void Group_hold_is_per_member()
        {
            WithReleases(_ =>
            {
                var scene = GroupScene((Owner, 30), (Member, 30));
                var world = scene.World();

                ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
                Assert.IsTrue(scene.Run.IsPeddlerShopOpen(Owner));
                Assert.AreEqual(0, scene.FormCalls.Count, "a held member forms nothing");

                Assert.IsTrue(ThreadPeddlerShop.Release(scene.Run, Owner, "test"));
                ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
                ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);

                CollectionAssert.AreEqual(new[] { Owner }, scene.FormCalls, "the released member forms; the other is still held");
                Assert.IsTrue(scene.Run.IsPeddlerShopOpen(Member), "the second member was presented and is shopping");
                Assert.AreEqual(1, scene.CreatedFor.Count, "the same NPC serves both");
            });
        }

        [TestMethod]
        public void A_held_member_does_not_retrigger_the_tick()
        {
            var scene = GroupScene((Owner, 30), (Member, 30));
            var world = scene.World();
            scene.Run.MarkMemberDeliveryWanted(Owner);
            Assert.IsTrue(scene.Run.IsMemberDeliveryWanted(Owner), "fixture: the member was waiting");

            for (var i = 0; i < 5; i++)
            {
                ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
                Assert.IsFalse(scene.Run.IsMemberDeliveryWanted(Owner), $"pass {i}: held clears the member's wanted flag and nothing re-arms it");
            }

            Assert.IsFalse(scene.Run.IsPlacementWanted);
            Assert.AreEqual(0, scene.FormCalls.Count);
        }

        [TestMethod]
        public void Group_buy_pays_at_once_then_the_release_forms_from_the_rest()
        {
            WithSeams(_ => WithReleases(requests =>
            {
                var scene = GroupScene((Owner, 30), (Member, 30));
                var world = scene.World();

                ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);
                Assert.IsTrue(scene.Run.TryChargePeddlerPurchase(Member, new[] { new PeddlerPurchase(PlainEntry.Id, 6) }, (c, n) => ThreadPeddlerPayment.PickPaymentItems(c, n, new Random(3)), out var paid).IsCharged);

                Assert.IsTrue(ThreadPeddlerShop.Release(scene.Run, Member, "test"));
                ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);

               CollectionAssert.AreEqual(new[] { Member }, scene.FormCalls, "then formed");
                Assert.AreEqual(30 - 6, Pile(scene.Run, Member).Count, "FormAt is a recorder here, so the pile, net of the payment, is still held");
            }));
        }

        [TestMethod]
        public void A_member_outside_waits_for_arrival_then_is_presented()
        {
            var scene = GroupScene((Owner, 30), (Member, 30));
            scene.Inside.Remove(Member);

            ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, scene.World());
            Assert.IsTrue(scene.Run.IsMemberDeliveryWanted(Member), "waits for the arrival, as any undelivered pile does");
            Assert.AreEqual(0, scene.FormCalls.Count, "no top-up, no form");
            Assert.AreEqual(0, scene.CreatedFor.Count);

            scene.Inside.Add(Member);
            ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, scene.World());
            Assert.IsTrue(scene.Run.IsPeddlerShopOpen(Member), "presented on arrival");
            Assert.IsFalse(scene.Run.IsMemberDeliveryWanted(Member));
        }

        [TestMethod]
        public void An_incomplete_pile_holds_every_seated_member()
        {
            var run = ActiveGroupRun(Member);
            GivePlan(run);
            Bank(run, 4);
            Clear(run);
            AddPlain(run, Member, 10);

            var formCalls = new List<uint>();
            var world = PeddlerTestFixtures.World(formCalls, Owner, Member);
            world.PresentPeddler = g => throw new AssertFailedException("never presented on an incomplete pile");

            ThreadGroupCacheDelivery.ServeMember(run, Member, true, world);
            Assert.AreEqual(0, formCalls.Count, "a partial pile is never delivered ahead of the shop");
            Assert.AreEqual(10, Pile(run, Member).Count);
            Assert.IsTrue(run.PeddlerDebtorWaiting == false, "the at-clear rolls are not claimed yet");

            DrainLedger(run);
            Assert.IsTrue(run.PeddlerDebtorWaiting, "an undecided seated member asks the deal buffer to flush");
        }

        // ======================================================================================
        // presentation
        // ======================================================================================

        [TestMethod]
        public void No_npc_when_nothing_is_affordable_for_anyone()
        {
            var scene = GroupScene((Owner, 4), (Member, 2));
            var world = scene.World();

            ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
            ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);

            Assert.AreEqual(0, scene.CreatedFor.Count, "the cheapest offer is 5: the NPC is never created");
            Assert.IsTrue(scene.ToldNothingAffordable(Owner) && scene.ToldNothingAffordable(Member), "each is told once");
            Assert.AreEqual(2, scene.Told.Count);
            CollectionAssert.AreEqual(new[] { Owner, Member }, scene.FormCalls, "and both caches form as normal");
            Assert.IsTrue(scene.Run.IsPeddlerShopDone(Owner) && scene.Run.IsPeddlerShopDone(Member));
        }

        /// <summary>
        /// A plain offer flat 5 at 100% with jitter factor 1.1: a pile of 10 costs max(5, ceil(11)) = 11 at that member's
        /// price, so they can afford nothing although the FLAT price (5) fits. The presentation check is per viewer.
        /// </summary>
        [TestMethod]
        public void Percent_pricing_nothing_affordable_is_judged_at_the_members_own_price()
        {
            var run = ActiveSoloRun();
            var offers = new List<PeddlerOffer> { new PeddlerOffer(new ThreadEncounterPrizePick(PlainEntry, new[] { PlainWcid }), 5, true, 1.1) };
            GivePlan(run, new PeddlerPlan(null, new PeddlerConfig { PricePercent = new Dictionary<string, double> { { PlainEntry.Id, 100 } } }, offers, 1.0));
            Clear(run);
            DrainLedger(run);
            AddPlain(run, run.OwnerGuid, 10);

            var scene = new Scene { Run = run };
            Assert.AreEqual(PeddlerPresentResult.Skipped, scene.Present(run.OwnerGuid));
            Assert.AreEqual(0, scene.CreatedFor.Count, "no NPC for a member who can afford nothing at their own price");
            Assert.IsTrue(scene.ToldNothingAffordable(run.OwnerGuid));
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("balance 10, cheapest 11")), string.Join("\n", run.EncounterOutcomes));
        }

        [TestMethod]
        public void Percent_pricing_the_open_line_names_the_basis_and_every_price()
        {
            var run = ActiveSoloRun();
            var plan = PercentPlan();
            GivePlan(run, plan);
            Clear(run);
            DrainLedger(run);
            AddPlain(run, run.OwnerGuid, 2000);

            var scene = new Scene { Run = run, Plan = plan };

            PeddlerEncounterTypeTests.WithDisplaySeams(new Dictionary<uint, Weenie>(), _ =>
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(run.OwnerGuid)));

            var line = run.EncounterOutcomes.Single(l => l.Contains("shop open for"));
            StringAssert.Contains(line, "(balance 2000, basis 2000, percent pricing): notes 125, plain 600, pattern 1600", line);
            Assert.AreEqual(2000, run.PeddlerBasis(run.OwnerGuid));
        }

        /// <summary>
        /// F3 (3): Present for one member, then another on the SAME NPC: the second is armed with their own displays, priced
        /// from their own pile (2000: 125 / 600 / 1600), while the first keeps theirs (400: the flat 50 / 240 / 320).
        /// </summary>
        [TestMethod]
        public void Percent_pricing_a_second_member_on_the_same_npc_gets_their_own_prices()
        {
            var run = ActiveGroupRun(Member);
            var plan = PercentPlan();
            GivePlan(run, plan);
            Clear(run);
            DrainLedger(run);
            AddPlain(run, Owner, 400);
            AddPlain(run, Member, 2000);

            var scene = new Scene { Run = run, Plan = plan };

            PeddlerEncounterTypeTests.WithDisplaySeams(new Dictionary<uint, Weenie>(), _ =>
            {
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(Owner));
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(Member));
            });

            var npc = scene.Npcs.Single();
            Assert.AreEqual(1, scene.CreatedFor.Count, "one NPC; the second member is armed on it");

            int[] Shown(uint g) => npc.RowsFor(Viewer(g)).Select(r => r.Value ?? -1).ToArray();
            CollectionAssert.AreEqual(new[] { 50, 240, 320 }, Shown(Owner));
            CollectionAssert.AreEqual(new[] { 125, 600, 1600 }, Shown(Member));
            Assert.IsFalse(npc.RowsFor(Viewer(Owner)).Intersect(npc.RowsFor(Viewer(Member))).Any(), "separate display objects");
            Assert.AreEqual(2000, run.PeddlerBasis(Member));
        }

        [TestMethod]
        public void Flat_pricing_the_open_line_names_the_flat_prices()
        {
            var scene = SoloScene(40);
            Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(scene.Run.OwnerGuid));
            StringAssert.Contains(scene.Run.EncounterOutcomes.Single(l => l.Contains("shop open for")), "(balance 40, basis 40, flat pricing): plain 5, plain 20");
        }

        [TestMethod]
        public void The_npc_appears_beside_the_first_seated_member_who_can_afford_something()
        {
            var scene = GroupScene((Owner, 3), (Member, 40));
            var world = scene.World();

            ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
            ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);

            CollectionAssert.AreEqual(new[] { Member }, scene.CreatedFor, "not beside the member who could afford nothing");
            Assert.IsTrue(scene.ToldNothingAffordable(Owner));
            CollectionAssert.AreEqual(new[] { Owner }, scene.FormCalls);
            Assert.AreSame(scene.Npcs.Single(), scene.Run.PeddlerNpc);
        }

        [TestMethod]
        public void Only_one_npc_per_run_ever()
        {
            WithReleases(_ =>
            {
                var run = ActiveGroupRun(Member, Member2);
                GivePlan(run, TestPlan(5));
                Clear(run);
                DrainLedger(run);

                foreach (var g in new[] { Owner, Member, Member2 })
                    AddPlain(run, g, 20);

                var scene = new Scene { Run = run };

                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(Owner));
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(Member));
                Assert.AreEqual(1, scene.CreatedFor.Count, "the second member walks to the first NPC");
                CollectionAssert.AreEquivalent(new[] { Owner, Member }, scene.Npcs.Single().ArmedMembers.ToList());

                scene.Npcs.Single().Depart("test");
                Assert.IsNull(run.PeddlerNpc);

                Assert.AreEqual(PeddlerPresentResult.Skipped, scene.Present(Member2), "after it left, nobody is presented");
                Assert.AreEqual(1, scene.CreatedFor.Count, "and no second NPC is ever built");
                Assert.IsTrue(run.IsPeddlerShopDone(Member2));

                var other = BoundStall(run).Peddler;
                Assert.IsFalse(run.TrySetPeddlerNpc(other), "the run refuses a second NPC outright");
            });
        }

        [TestMethod]
        public void A_peddler_that_cannot_appear_closes_the_shop_for_everyone()
        {
            var scene = GroupScene((Owner, 30), (Member, 30));
            scene.CreateFails = true;
            var world = scene.World();

            ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
            ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);

            Assert.AreEqual(1, scene.CreatedFor.Count, "one attempt, then closed");
            Assert.IsTrue(scene.Run.PeddlerClosed);
            CollectionAssert.AreEqual(new[] { Owner, Member }, scene.FormCalls, "both deliver as normal");
            Assert.AreEqual(0, scene.Told.Count, "nobody was promised a shop");
        }

        [TestMethod]
        public void An_npc_destroyed_without_departing_never_holds_a_pile()
        {
            WithReleases(requests =>
            {
                var scene = GroupScene((Owner, 30), (Member, 30), (Member2, 30));
                var world = scene.World();

                ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
                ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);
                Assert.IsTrue(scene.Run.IsPeddlerShopOpen(Owner) && scene.Run.IsPeddlerShopOpen(Member));

                scene.Npcs.Single().Destroy(); // its landblock went away: no Depart, no poll

                ThreadGroupCacheDelivery.ServeMember(scene.Run, Owner, true, world);
                Assert.IsTrue(scene.Run.IsPeddlerShopDone(Owner) && scene.Run.IsPeddlerShopDone(Member), "every open shop is released");
                CollectionAssert.AreEqual(new[] { Owner }, scene.FormCalls, "this member delivers in the same pass");
                CollectionAssert.AreEqual(new[] { Member }, requests.ToArray(), "the other open shop gets its placement request");

                ThreadGroupCacheDelivery.ServeMember(scene.Run, Member2, true, world);
                Assert.AreEqual(1, scene.CreatedFor.Count, "no second NPC");
                CollectionAssert.AreEqual(new[] { Owner, Member2 }, scene.FormCalls, "a later member just delivers");
            });
        }

        /// <summary>
        /// Review of #2237, finding 1: a release that lands Busy between the Gate returning Held and the seam's
        /// TryClaimMemberDeliveryWanted had its re-flag cleared by that claim, stranding the member. Deterministic: the
        /// world's PresentPeddler presents, then releases the member with a Busy placement, before the seam claims.
        /// Mutation-proved: removing the recheck after the claim fails this.
        /// </summary>
        [TestMethod]
        public void A_busy_release_racing_the_held_claim_keeps_the_member_flagged()
        {
            WithReleases(requests =>
            {
                var scene = GroupScene((Owner, 30), (Member, 30));
                var world = scene.World();
                world.PresentPeddler = g =>
                {
                    var result = scene.Present(g);
                    Assert.AreEqual(PeddlerPresentResult.Held, result, "fixture: presented");
                    Assert.IsTrue(ThreadPeddlerShop.Release(scene.Run, g, "a Yes on the player's queue"), "fixture: released");
                    return result;
                };

                ThreadGroupCacheDelivery.ServeMember(scene.Run, Member, true, world);

                CollectionAssert.AreEqual(new[] { Member }, requests.ToArray(), "the release asked for placement and got Busy");
                Assert.IsTrue(scene.Run.IsPeddlerShopDone(Member));
                Assert.IsTrue(scene.Run.IsMemberDeliveryWanted(Member), "the Busy re-flag survives the seam's claim, so the Tick retries the member");
                Assert.AreEqual(0, scene.FormCalls.Count, "this pass still formed nothing for them");
            }, CacheRequestOutcome.Busy);
        }

        /// <summary>Review finding 3: an NPC destroyed without Depart (or whose poll stopped) is swept by the Tick.</summary>
        [TestMethod]
        public void The_tick_sweep_releases_every_open_shop_when_the_npc_is_gone()
        {
            WithReleases(requests =>
            {
                var scene = GroupScene((Owner, 30), (Member, 30));
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(Owner));
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(Member));
                Assert.IsFalse(ThreadPeddlerShop.SweepLiveness(scene.Run), "a live NPC: nothing to sweep");

                scene.Npcs.Single().Destroy(); // no Depart, no poll

                Assert.IsTrue(ThreadPeddlerShop.SweepLiveness(scene.Run));
                Assert.IsTrue(scene.Run.IsPeddlerShopDone(Owner) && scene.Run.IsPeddlerShopDone(Member), "every open member is done");
                CollectionAssert.AreEquivalent(new[] { Owner, Member }, requests.ToArray(), "and a placement was requested for each");
                Assert.IsTrue(scene.Run.PeddlerClosed);
                Assert.IsFalse(ThreadPeddlerShop.SweepLiveness(scene.Run), "idempotent");

                var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs").Replace("\r\n", "\n");
                StringAssert.Contains(source, "if (state == ThreadDungeonRunState.Cleared)\n                    ThreadPeddlerShop.SweepLiveness(run);", "the Tick runs the sweep for every Cleared run");
            });
        }

        [TestMethod]
        public void The_tick_sweep_releases_when_the_poll_has_stopped()
        {
            WithReleases(requests =>
            {
                var scene = SoloScene(30);
                var owner = scene.Run.OwnerGuid;
                Assert.AreEqual(PeddlerPresentResult.Held, scene.Present(owner));

                var npc = scene.Npcs.Single();
                var now = 5000.0;
                npc.Clock = () => now;
                npc.LastPollAt = 5000;

                now = 5010;
                Assert.IsFalse(ThreadPeddlerShop.SweepLiveness(scene.Run), "polled 10 s ago: alive");

                now = 5015;
                Assert.IsTrue(ThreadPeddlerShop.SweepLiveness(scene.Run), "no poll for 15 s: swept");
                CollectionAssert.AreEqual(new[] { owner }, requests.ToArray());
            });
        }

        [TestMethod]
        public void Present_skips_silently_at_or_after_the_expiry_cap()
        {
            var original = ThreadPeddlerShop.Clock;

            try
            {
                var scene = SoloScene(30);
                var owner = scene.Run.OwnerGuid;
                ThreadPeddlerShop.Clock = () => ThreadPeddlerVendor.ExpiryCapUnix(scene.Run);

                Assert.AreEqual(PeddlerPresentResult.Skipped, scene.Present(owner), "exactly at the cap");
                Assert.AreEqual(0, scene.Told.Count, "no lines sent");
                Assert.AreEqual(0, scene.CreatedFor.Count, "no NPC");
                Assert.IsTrue(scene.Run.IsPeddlerShopDone(owner));

                var poor = SoloScene(2);
                ThreadPeddlerShop.Clock = () => ThreadPeddlerVendor.ExpiryCapUnix(poor.Run) + 1;
                Assert.AreEqual(PeddlerPresentResult.Skipped, poor.Present(poor.Run.OwnerGuid));
                Assert.AreEqual(0, poor.Told.Count, "the cap is checked before affordability, so not even the nothing-affordable line");

                var early = SoloScene(30);
                ThreadPeddlerShop.Clock = () => ThreadPeddlerVendor.ExpiryCapUnix(early.Run) - 1;
                Assert.AreEqual(PeddlerPresentResult.Held, early.Present(early.Run.OwnerGuid), "control: one second before the cap the shop opens");
            }
            finally
            {
                ThreadPeddlerShop.Clock = original;
            }
        }

        [TestMethod]
        public void A_throwing_presentation_skips_the_member_and_never_holds_the_pile()
        {
            var scene = SoloScene(30);
            var owner = scene.Run.OwnerGuid;

            var result = ThreadPeddlerShop.Present(scene.Run, Viewer(owner), (m, p) => throw new InvalidOperationException("boom"), null);

            Assert.AreEqual(PeddlerPresentResult.Skipped, result);
            Assert.IsTrue(scene.Run.IsPeddlerShopDone(owner));
            Assert.IsFalse(ThreadCachePlacer.SoloPeddlerHoldsDelivery(scene.Run, true, () => throw new AssertFailedException("decided")));
        }
    }
}
