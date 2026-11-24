using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// A Wayfarer's Shrine use (ShrineEncounterType.Activate over ThreadDungeonRun.TryUseShrine): exactly one winner when
    /// two uses land together, a non-member or outside user refused without spending it, spent after one use, and the
    /// recipients exactly the members inside at the moment of use. The Player half of OnUse (who is inside, the chat
    /// lines, the landblock-queued guardians and destroy) needs a live world, so its wiring is pinned on the source.
    /// Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ShrineActivationTests
    {
        private static ShrineEncounterType.ShrineState State()
            => new ShrineEncounterType.ShrineState(null, new ShrineConfig { Boons = AllKinds() });

        [TestMethod]
        public void Two_uses_in_the_same_tick_make_exactly_one_grant()
        {
            for (var round = 0; round < 200; round++)
            {
                using var scope = new Releaser();
                var run = scope.Track(Run(Member));
                var state = State();
                var outcomes = new ConcurrentBag<ShrineUseOutcome>();

                using (var gate = new Barrier(2))
                {
                    Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
                    {
                        gate.SignalAndWait();
                        outcomes.Add(ShrineEncounterType.Activate(run, state, true, new[] { Owner, Member }, out _, out _));
                    });
                }

                Assert.AreEqual(1, outcomes.Count(o => o == ShrineUseOutcome.Granted), $"round {round}");
                Assert.AreEqual(1, outcomes.Count(o => o == ShrineUseOutcome.Spent), $"round {round}");
                Assert.AreEqual(1, run.GrantedBoonKinds.Count, $"round {round}: one kind granted");
            }
        }

        [TestMethod]
        public void A_user_who_is_not_a_member_inside_is_refused_and_the_shrine_stays_usable()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member));
            var state = State();

            Assert.AreEqual(ShrineUseOutcome.NotInside, ShrineEncounterType.Activate(run, state, false, new[] { Owner }, out _, out _));
            Assert.IsFalse(state.Latch.IsSpent(run), "a refused use does not spend it");
            Assert.AreEqual(0, run.GrantedBoonKinds.Count);

            // The inside predicate the production use passes in is the one presence test: a stranger standing in the copy,
            // a member elsewhere and a puzzle-removed member are all "not inside".
            Assert.IsFalse(ThreadRunPresence.IsMemberInside(run, Stranger, Inside(run)));
            Assert.IsFalse(ThreadRunPresence.IsMemberInside(run, Owner, OtherInstance(run)));
            Assert.IsTrue(ThreadRunPresence.IsMemberInside(run, Member, Inside(run)), "control");
            run.MarkPuzzleRemoved(Member);
            Assert.IsFalse(ThreadRunPresence.IsMemberInside(run, Member, Inside(run)));

            Assert.AreEqual(ShrineUseOutcome.Granted, ShrineEncounterType.Activate(run, state, true, new[] { Owner }, out _, out _));
        }

        [TestMethod]
        public void A_used_shrine_is_spent()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run());
            var state = State();

            Assert.AreEqual(ShrineUseOutcome.Granted, ShrineEncounterType.Activate(run, state, true, new[] { Owner }, out var kind, out var magnitude));
            Assert.IsTrue(state.Latch.IsSpent(run));
            Assert.AreEqual(1.25, magnitude, 1e-12);

            Assert.AreEqual(ShrineUseOutcome.Spent, ShrineEncounterType.Activate(run, state, true, new[] { Owner }, out _, out _));
            Assert.AreEqual(1, run.BoonsHeldBy(Owner).Count);
            Assert.AreEqual(kind, run.BoonsHeldBy(Owner).Single().Kind);
        }

        [TestMethod]
        public void The_recipients_are_the_members_inside_at_the_moment_of_use()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member, Member2));

            Assert.AreEqual(ShrineUseOutcome.Granted, ShrineEncounterType.Activate(run, State(), true, new[] { Owner, Member2 }, out var kind, out _));

            Assert.AreEqual(1, run.BoonsHeldBy(Owner).Count);
            Assert.AreEqual(1, run.BoonsHeldBy(Member2).Count);
            Assert.AreEqual(0, run.BoonsHeldBy(Member).Count, "outside at the use");
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Member, Inside(run), kind), "and nothing once they walk in");

            // Production computes that set once, at the use, from the one presence test, and passes the guids in.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ShrineEncounterType.cs");
            var use = PooledLootSourceText.MethodBody(src, "public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)");

            var inside = use.IndexOf("var userInside = ThreadRunPresence.IsMemberInside(run, player);", StringComparison.Ordinal);
            var recipients = use.IndexOf("var recipients = userInside ? ThreadRunPresence.OnlineMembersInside(run) : new List<Player>();", StringComparison.Ordinal);
            var activate = use.IndexOf("Activate(run, state, userInside, recipients.Select(p => p.Guid.Full).ToList(), out var kind, out var magnitude);", StringComparison.Ordinal);

            Assert.IsTrue(inside >= 0 && inside < recipients && recipients < activate);
        }

        [TestMethod]
        public void Using_a_guardian_is_not_a_shrine_use()
        {
            var shrine = ThreadLootTestFixtures.Item(1007950);
            var guardian = ThreadLootTestFixtures.Item(42);
            var state = State();

            Assert.IsFalse(ShrineEncounterType.IsTheShrine(state, shrine), "no guid recorded yet");

            state.ShrineGuid = shrine.Guid.Full;

            Assert.IsTrue(ShrineEncounterType.IsTheShrine(state, shrine));
            Assert.IsFalse(ShrineEncounterType.IsTheShrine(state, guardian), "the instance tracks its guardians too");
            Assert.IsFalse(ShrineEncounterType.IsTheShrine(state, null));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ShrineEncounterType.cs");
            var use = PooledLootSourceText.MethodBody(src, "public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)");
            Assert.IsTrue(use.IndexOf("if (!IsTheShrine(state, used))", StringComparison.Ordinal) < use.IndexOf("Activate(run, state", StringComparison.Ordinal));
            StringAssert.Contains(src, "request.Instance.State = new ShrineState(request, config) { ShrineGuid = shrine.Guid.Full };");
        }

        [TestMethod]
        public void A_use_sends_the_owner_lines_and_queues_the_world_work_on_the_shrine()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ShrineEncounterType.cs");
            var use = PooledLootSourceText.MethodBody(src, "public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)");

            StringAssert.Contains(use, "Send(player, ThreadBoons.SpentLine);");
            StringAssert.Contains(use, "Send(player, ThreadBoons.SilentLine);\n                    ScheduleAfterUse(instance, state, used, spawnGuardians: false);");

            // Every recipient gets both lines, blessing first, then the one-shot visual; the user gets no separate guardians line.
            var each = use.IndexOf("foreach (var recipient in recipients)\n                    {\n                        Send(recipient, line);\n                        Send(recipient, ThreadBoons.GuardiansLine);\n                        PlayBlessingVisual(recipient, kind);\n                    }", StringComparison.Ordinal);
            var schedule = use.IndexOf("ScheduleAfterUse(instance, state, used, spawnGuardians: true);", StringComparison.Ordinal);
            Assert.IsTrue(each >= 0 && each < schedule, "blessing line then guardians line to each recipient, then the world work");
            Assert.AreEqual(-1, use.IndexOf("Send(player, ThreadBoons.GuardiansLine);", StringComparison.Ordinal));

            // A refused use: a member outside, a visitor or a puzzle-removed member is told why; a use after the run
            // ended says nothing.
            var refused = use.IndexOf("case ShrineUseOutcome.NotInside:", StringComparison.Ordinal);
            var refusedLine = use.IndexOf("Send(player, ThreadBoons.RefusedLine);", StringComparison.Ordinal);
            var runOver = use.IndexOf("case ShrineUseOutcome.RunOver:", StringComparison.Ordinal);
            Assert.IsTrue(refused >= 0 && refused < refusedLine && refusedLine < runOver);
            Assert.AreEqual(1, use.Split("ThreadBoons.RefusedLine").Length - 1, "sent on NotInside only");
            Assert.AreEqual("Only members of this thread can use the shrine.", ThreadBoons.RefusedLine);
            Assert.IsTrue(ThreadBoons.RefusedLine.Length < 255);

            var after = PooledLootSourceText.MethodBody(src, "private static void ScheduleAfterUse(ThreadEncounterInstance instance, ShrineState state, WorldObject shrine, bool spawnGuardians)");
            var spawn = after.IndexOf("chain.AddAction(shrine, () => SpawnGuardians(instance, state));", StringComparison.Ordinal);
            var delay = after.IndexOf("chain.AddDelaySeconds(SpentDestroyDelaySeconds);", StringComparison.Ordinal);
            var destroy = after.IndexOf("shrine.Destroy();", StringComparison.Ordinal);
            Assert.IsTrue(spawn >= 0 && spawn < delay && delay < destroy, "guardians, then about 3 s, then the destroy, all on the shrine's landblock");
            Assert.AreEqual(3.0f, ShrineEncounterType.SpentDestroyDelaySeconds);

            // The use reaches the type only through the generic encounter hook, after the use-requirements gate.
            var activate = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Use.cs"), "public virtual void OnActivate(WorldObject activator)");
            var requirements = activate.IndexOf("var result = CheckUseRequirements(activator);", StringComparison.Ordinal);
            var hook = activate.IndexOf("ACE.Server.ThreadDungeons.ThreadEncounterPass.OnActivated(this, player);", StringComparison.Ordinal);
            Assert.IsTrue(requirements >= 0 && requirements < hook);
        }
    }
}
