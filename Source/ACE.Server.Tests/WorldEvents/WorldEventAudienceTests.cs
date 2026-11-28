using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars;
using ACE.Server.WorldEvents;

namespace ACE.Server.Tests.WorldEvents
{
    /// <summary>
    /// Who hears a run's announcements: a quiet private Avatar event goes to the Avatar's allegiance only, and never to Discord;
    /// every other run is untouched. The announcer sinks are swapped and restored in a finally.
    /// </summary>
    [TestClass]
    public class WorldEventAudienceTests
    {
        private const double Start = 1_800_000_000.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly AvatarTuning T = AvatarTuning.Defaults;
        private const uint AvatarId = 7;
        private const uint Monarch = 1000;

        private sealed class Capture
        {
            public readonly List<string> Broadcasts = new List<string>();
            public readonly List<(uint Monarch, string Text)> Allegiance = new List<(uint, string)>();
            public readonly List<string> Discord = new List<string>();
            public readonly List<string> Local = new List<string>();
            public readonly List<ISet<uint>> LocalExclude = new List<ISet<uint>>();
            public ISet<uint> Reached = new HashSet<uint>();
            public readonly List<(uint AvatarId, ulong Price, bool Commit)> Settled = new List<(uint, ulong, bool)>();
        }

        private static void WithSinks(Action<Capture> body)
        {
            var c = new Capture();
            var b = WorldEventAnnouncer.BroadcastSink;
            var a = WorldEventAnnouncer.AllegianceSink;
            var d = WorldEventAnnouncer.DiscordRelaySink;
            var l = WorldEventAnnouncer.LocalAnnounceSink;
            var st = WorldEventHooks.AvatarFavorSettleSink;

            try
            {
                WorldEventAnnouncer.BroadcastSink = c.Broadcasts.Add;
                WorldEventAnnouncer.AllegianceSink = (m, t) => { c.Allegiance.Add((m, t)); return c.Reached; };
                WorldEventAnnouncer.DiscordRelaySink = c.Discord.Add;
                WorldEventAnnouncer.LocalAnnounceSink = (e, m, x) => { c.Local.Add(m); c.LocalExclude.Add(x); };
                WorldEventHooks.AvatarFavorSettleSink = (id, price, commit) => c.Settled.Add((id, price, commit));
                body(c);
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = b;
                WorldEventAnnouncer.AllegianceSink = a;
                WorldEventAnnouncer.DiscordRelaySink = d;
                WorldEventAnnouncer.LocalAnnounceSink = l;
                WorldEventHooks.AvatarFavorSettleSink = st;
                AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            }
        }

        private static WorldEvent Run(uint? avatarId, bool serverWide, ulong price = 0) =>
            new WorldEvent(1, null, new WorldEventRequest { AvatarId = avatarId, AnnounceServerWide = serverWide, AvatarFavorPrice = price }, null, clock: () => 1234d);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Transition")]
        private static extern bool Transition(WorldEvent run, WorldEventState to, string reason);

        private static void GoActive(WorldEvent run)
        {
            Assert.IsTrue(Transition(run, WorldEventState.Staged, "t"));
            Assert.IsTrue(Transition(run, WorldEventState.Announced, "t"));
            Assert.IsTrue(Transition(run, WorldEventState.Active, "t"));
        }

        private static AvatarState InitAvatar()
        {
            var store = new Store();
            store.Avatars.Add(new AllegianceAvatar { AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound, Favor = 5000, LifetimeXp = 10_000, LastDecayDay = Today });
            AvatarManager.Initialize(store, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            AvatarManager.Tick();
            var state = AvatarManager.Get(AvatarId);
            Assert.IsNotNull(state);
            return state;
        }

        [TestMethod]
        public void QuietAvatarEvent_StartLine_GoesToAllegianceOnly_NoDiscord() => WithSinks(c =>
        {
            InitAvatar();

            Run(AvatarId, serverWide: false).Announce("[World Event] begins!", relayToDiscord: true);

            Assert.AreEqual(0, c.Broadcasts.Count, "a quiet event never goes server-wide");
            Assert.AreEqual(0, c.Discord.Count, "a quiet event never reaches Discord, even when the call site asks");
            CollectionAssert.AreEqual(new[] { (Monarch, "[Allegiance Event] begins!") }, c.Allegiance, "the allegiance copy swaps the prefix");
        });

        [TestMethod]
        public void QuietAvatarEvent_EveryLineKind_NeverBroadcastLocalOrDiscord() => WithSinks(c =>
        {
            InitAvatar();
            var run = Run(AvatarId, serverWide: false);

            run.Announce("[World Event] headline", relayToDiscord: true);
            run.AnnounceInRunWide("[World Event] boss");
            run.AnnounceLocal("[World Event] wave");
            run.AnnounceInRun("[World Event] progress");

            Assert.AreEqual(0, c.Local.Count, "no radius-local bystander leg for a quiet event");
            Assert.AreEqual(0, c.Broadcasts.Count);
            Assert.AreEqual(0, c.Discord.Count);
            CollectionAssert.AreEqual(
                new[] { "[Allegiance Event] headline", "[Allegiance Event] boss", "[Allegiance Event] wave", "[Allegiance Event] progress" },
                c.Allegiance.Select(a => a.Text).ToList());
        });

        [TestMethod]
        public void AnnouncedAvatarEvent_HeadlineIsServerWide_InRunIsAllegiancePlusLocal() => WithSinks(c =>
        {
            InitAvatar();
            c.Reached = new HashSet<uint> { 1000, 1001 };
            var run = Run(AvatarId, serverWide: true);

            run.Announce("[World Event] teaser", relayToDiscord: true);

            CollectionAssert.AreEqual(new[] { "[World Event] teaser" }, c.Broadcasts, "headline stays server-wide in the World Event look");
            CollectionAssert.AreEqual(new[] { "[World Event] teaser" }, c.Discord, "the teaser still reaches Discord");
            Assert.AreEqual(0, c.Allegiance.Count);
            Assert.AreEqual(0, c.Local.Count);

            run.AnnounceInRunWide("[World Event] boss");
            run.AnnounceLocal("[World Event] wave");

            Assert.AreEqual(1, c.Broadcasts.Count, "in-run lines are not broadcast");
            CollectionAssert.AreEqual(new[] { "[Allegiance Event] boss", "[Allegiance Event] wave" }, c.Allegiance.Select(a => a.Text).ToList());
            CollectionAssert.AreEqual(new[] { "[World Event] boss", "[World Event] wave" }, c.Local, "the radius copy keeps today's look");
            Assert.AreEqual(2, c.LocalExclude.Count);
            CollectionAssert.AreEquivalent(new uint[] { 1000, 1001 }, c.LocalExclude[0].ToList(), "members the allegiance send reached do not hear it twice");
        });

        [TestMethod]
        public void PublicEvent_InRunLines_Unchanged() => WithSinks(c =>
        {
            var run = Run(null, serverWide: true);

            run.AnnounceInRunWide("[World Event] boss");
            run.AnnounceLocal("[World Event] wave");

            CollectionAssert.AreEqual(new[] { "[World Event] boss" }, c.Broadcasts);
            Assert.AreEqual(0, c.Allegiance.Count);
            CollectionAssert.AreEqual(new[] { "[World Event] wave" }, c.Local, "a public run's local line goes through the local leg, unprefixed and unexcluded");
            Assert.IsNull(c.LocalExclude[0]);
        });

        [TestMethod]
        public void AllegianceLook_SwapsTheLeadingPrefix_AndPrependsWhenAbsent()
        {
            Assert.AreEqual("[Allegiance Event] x [World Event] y", WorldEventAnnouncer.AllegianceLook("[World Event] x [World Event] y"));
            Assert.AreEqual("[Allegiance Event] ambient wave line", WorldEventAnnouncer.AllegianceLook("ambient wave line"), "a line without the prefix gets the allegiance prefix");
            Assert.AreEqual("", WorldEventAnnouncer.AllegianceLook(""));
            Assert.AreEqual(WorldEventAnnouncer.Global("body").Replace("[World Event] ", "[Allegiance Event] "), WorldEventAnnouncer.AllegianceLook(WorldEventAnnouncer.Global("body")));
        }

        [TestMethod]
        public void PublicAndAnnouncedRuns_DoNotUseTheLocalSend() => WithSinks(c =>
        {
            InitAvatar();

            Run(null, serverWide: true).Announce("a");
            Run(AvatarId, serverWide: true).Announce("b");

            Assert.AreEqual(0, c.Local.Count);
        });

        // ---- Favor settlement -----------------------------------------------------------------------------------

        [TestMethod]
        public void Settle_ReachingActive_CommitsOnce() => WithSinks(c =>
        {
            var run = Run(AvatarId, serverWide: false, price: 2000);

            GoActive(run);
            Transition(run, WorldEventState.Resolved, "t");
            Transition(run, WorldEventState.Cleanup, "t");
            Transition(run, WorldEventState.Done, "t");

            CollectionAssert.AreEqual(new[] { (AvatarId, 2000UL, true) }, c.Settled, "committed exactly once, and not released when it ends");
        });

        [TestMethod]
        public void Settle_AbortedBeforeActive_Releases() => WithSinks(c =>
        {
            var run = Run(AvatarId, serverWide: false, price: 2000);
            Assert.IsTrue(Transition(run, WorldEventState.Staged, "t"));
            Assert.IsTrue(Transition(run, WorldEventState.Announced, "t"));
            Assert.AreEqual(0, c.Settled.Count, "still held while the run is only announced");

            run.Finish(WorldEventOutcome.AbortedAdmin);

            CollectionAssert.AreEqual(new[] { (AvatarId, 2000UL, false) }, c.Settled, "released exactly once");
        });

        [TestMethod]
        public void Settle_AbortedAfterActive_StaysCommitted() => WithSinks(c =>
        {
            var run = Run(AvatarId, serverWide: false, price: 2000);
            GoActive(run);

            run.Finish(WorldEventOutcome.AbortedError);

            CollectionAssert.AreEqual(new[] { (AvatarId, 2000UL, true) }, c.Settled);
        });

        /// <summary>
        /// A private event priced at 0 still settles: the sink also settles the daily claim (AvatarEventPurchase.Settle), so a
        /// free event that skipped it would hold the day's slot until a restart.
        /// </summary>
        [TestMethod]
        public void Settle_PriceZero_StillCommitsAtActive() => WithSinks(c =>
        {
            var run = Run(AvatarId, serverWide: false, price: 0);

            GoActive(run);
            run.Finish(WorldEventOutcome.AbortedAdmin);

            CollectionAssert.AreEqual(new[] { (AvatarId, 0UL, true) }, c.Settled, "committed exactly once at price 0");
        });

        [TestMethod]
        public void Settle_PriceZero_AbortedBeforeActive_StillReleases() => WithSinks(c =>
        {
            var run = Run(AvatarId, serverWide: false, price: 0);
            Assert.IsTrue(Transition(run, WorldEventState.Staged, "t"));

            run.Finish(WorldEventOutcome.AbortedAdmin);

            CollectionAssert.AreEqual(new[] { (AvatarId, 0UL, false) }, c.Settled, "released exactly once at price 0");
        });

        [TestMethod]
        public void Settle_OrdinaryRun_NeverTouchesFavor() => WithSinks(c =>
        {
            var run = Run(null, serverWide: true);
            GoActive(run);
            run.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(0, c.Settled.Count);
        });

        [TestMethod]
        public void AnnouncedAvatarEvent_UsesBroadcast() => WithSinks(c =>
        {
            InitAvatar();

            Run(AvatarId, serverWide: true).Announce("[World Event] begins!", relayToDiscord: true);

            CollectionAssert.AreEqual(new[] { "[World Event] begins!" }, c.Broadcasts);
            Assert.AreEqual(0, c.Allegiance.Count);
        });

        [TestMethod]
        public void PublicEvent_Unchanged() => WithSinks(c =>
        {
            Run(null, serverWide: true).Announce("[World Event] a", relayToDiscord: true);
            Run(null, serverWide: true).Announce("[World Event] b");

            CollectionAssert.AreEqual(new[] { "[World Event] a", "[World Event] b" }, c.Broadcasts);
            CollectionAssert.AreEqual(new[] { "[World Event] a" }, c.Discord);
            Assert.AreEqual(0, c.Allegiance.Count);
        });

        [TestMethod]
        public void QuietAvatarEvent_MonarchChangedMidEvent_GoesToNewMonarchsAllegiance() => WithSinks(c =>
        {
            var state = InitAvatar();
            var run = Run(AvatarId, serverWide: false);

            run.Announce("first");
            state.MonarchGuid = 2000;
            run.Announce("second");

            CollectionAssert.AreEqual(new[] { (Monarch, "[Allegiance Event] first"), (2000u, "[Allegiance Event] second") }, c.Allegiance);
        });

        [TestMethod]
        public void QuietAvatarEvent_NeverResolved_LineIsLoggedOnly() => WithSinks(c =>
        {
            Run(99, serverWide: false).Announce("[World Event] begins!", relayToDiscord: true);

            Assert.AreEqual(0, c.Broadcasts.Count);
            Assert.AreEqual(0, c.Allegiance.Count, "no monarch to send to");
            Assert.AreEqual(0, c.Discord.Count);
            Assert.AreEqual(0, c.Local.Count, "no bystander fallback either");
        });

        [TestMethod]
        public void QuietAvatarEvent_UnboundMidRun_StillGoesToTheLastMonarch() => WithSinks(c =>
        {
            var state = InitAvatar();
            var run = Run(AvatarId, serverWide: false);

            run.Announce("[World Event] first");
            state.MonarchGuid = null;
            run.Announce("[World Event] second");
            run.AnnounceLocal("wave");

            CollectionAssert.AreEqual(
                new[] { (Monarch, "[Allegiance Event] first"), (Monarch, "[Allegiance Event] second"), (Monarch, "[Allegiance Event] wave") },
                c.Allegiance, "an unbound Avatar must not silence the rest of the run");
            Assert.AreEqual(0, c.Local.Count);
        });

        // A minimal objective that announces locally, to drive WorldEvent.AnnounceInRun's local branch.
        private sealed class LocalObjective : ACE.Server.WorldEvents.Objectives.IWorldEventObjective
        {
            public bool IsComplete { get; set; }
            public string ProgressText { get; set; } = "";
            public void OnCreatureDied(ACE.Server.WorldObjects.Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager, ACE.Server.Entity.DamageHistoryInfo topDamager) { }
            public void Tick(double now) { }
            public ACE.Server.WorldEvents.Objectives.WorldEventMvp Mvp() => ACE.Server.WorldEvents.Objectives.WorldEventMvp.None;
            bool ACE.Server.WorldEvents.Objectives.IWorldEventObjective.AnnouncesLocally => true;
        }

        [TestMethod]
        public void AnnounceInRun_WithALocalObjective_FollowsTheAudienceRulePerRunKind() => WithSinks(c =>
        {
            InitAvatar();

            var pub = Run(null, serverWide: true);
            Assert.IsTrue(pub.SetObjective(new LocalObjective()));
            pub.AnnounceInRun("[World Event] p");
            CollectionAssert.AreEqual(new[] { "[World Event] p" }, c.Local, "public: local leg as before");
            Assert.AreEqual(0, c.Broadcasts.Count);

            c.Local.Clear();
            var quiet = Run(AvatarId, serverWide: false);
            Assert.IsTrue(quiet.SetObjective(new LocalObjective()));
            quiet.AnnounceInRun("[World Event] q");
            Assert.AreEqual(0, c.Local.Count);
            CollectionAssert.AreEqual(new[] { (Monarch, "[Allegiance Event] q") }, c.Allegiance);

            var announced = Run(AvatarId, serverWide: true);
            Assert.IsTrue(announced.SetObjective(new LocalObjective()));
            announced.AnnounceInRun("[World Event] a");
            CollectionAssert.AreEqual(new[] { "[World Event] a" }, c.Local);
            Assert.AreEqual((Monarch, "[Allegiance Event] a"), c.Allegiance[1]);
            Assert.AreEqual(0, c.Broadcasts.Count);
        });

        [TestMethod]
        public void LocalRecipientFilter_ExcludesReachedGuids_AndKeepsInRadiusSameInstance()
        {
            var anchor = new ACE.Entity.Position((0x0090u << 16) | 0x0011, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 3);
            var near = new ACE.Entity.Position((0x0090u << 16) | 0x0011, 55f, 50f, 0f, 0f, 0f, 0f, 1f, 3);
            var otherInstance = new ACE.Entity.Position((0x0090u << 16) | 0x0011, 55f, 50f, 0f, 0f, 0f, 0f, 1f, 4);
            var far = new ACE.Entity.Position((0x0090u << 16) | 0x0011, 150f, 50f, 0f, 0f, 0f, 0f, 1f, 3);
            var reached = new HashSet<uint> { 7 };

            Assert.IsTrue(WorldEventAnnouncer.IncludesLocalRecipient(1, near, anchor, 30f, reached));
            Assert.IsTrue(WorldEventAnnouncer.IncludesLocalRecipient(1, near, anchor, 30f, null));
            Assert.IsFalse(WorldEventAnnouncer.IncludesLocalRecipient(7, near, anchor, 30f, reached), "already reached by the allegiance send");
            Assert.IsTrue(WorldEventAnnouncer.IncludesLocalRecipient(7, near, anchor, 30f, null), "no exclusion set, nobody skipped");
            Assert.IsFalse(WorldEventAnnouncer.IncludesLocalRecipient(1, otherInstance, anchor, 30f, reached));
            Assert.IsFalse(WorldEventAnnouncer.IncludesLocalRecipient(1, far, anchor, 30f, reached));
            Assert.IsFalse(WorldEventAnnouncer.IncludesLocalRecipient(1, null, anchor, 30f, reached));
        }

        // ---- production defaults --------------------------------------------------------------------------------

        [TestMethod]
        public void ProductionDefaults_SettleThroughTheRealSink_AndLocalIsTheRealLocalSend()
        {
            try
            {
                Assert.AreEqual("Settle", WorldEventHooks.AvatarFavorSettleSink.Method.Name, "the default settle sink must be AvatarEventPurchase.Settle");
                Assert.AreEqual("Local", WorldEventAnnouncer.LocalAnnounceSink.Method.Name, "the default local sink must be WorldEventAnnouncer.Local");
                Assert.AreEqual("ToAllegiance", WorldEventAnnouncer.AllegianceSink.Method.Name);

                var state = InitAvatar();
                Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 2000));
                var run = Run(AvatarId, serverWide: false, price: 2000);

                GoActive(run);

                Assert.AreEqual(3000UL, state.Favor, "reaching Active debits through the shipped sink");
                Assert.AreEqual(0UL, state.ReservedFavor);
            }
            finally
            {
                AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            }
        }

        // ---- source pin -----------------------------------------------------------------------------------------

        private static string WorldEventsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents");

                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail("Could not find Source/ACE.Server/WorldEvents by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        [TestMethod]
        public void RunSources_NeverCallBroadcastDirectly()
        {
            var dir = WorldEventsDir();
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            Assert.IsTrue(files.Length > 20, "sanity: expected the whole WorldEvents tree, found " + files.Length);

            // WorldEventAnnouncer.cs defines Broadcast; WorldEvent.Audience.cs is the one run-side caller. HoldObjective.cs keeps
            // exactly one fallback for an objective with no run (evt == null), which can never be an Avatar's run.
            var offenders = new List<string>();
            var holdFallbackHits = 0;

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                var hits = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"WorldEventAnnouncer\.Broadcast\(").Count;

                if (name == "WorldEventAnnouncer.cs" || name == "WorldEvent.Audience.cs")
                    continue;

                if (name == "HoldObjective.cs")
                {
                    holdFallbackHits = hits;
                    continue;
                }

                if (hits > 0)
                    offenders.Add(Path.GetRelativePath(dir, path));
            }

            Assert.AreEqual(0, offenders.Count, "these must send through WorldEvent.Announce, or a quiet Avatar event leaks server-wide: " + string.Join(", ", offenders));
            Assert.AreEqual(1, holdFallbackHits, "HoldObjective.cs keeps exactly its one evt == null fallback");

            Assert.IsTrue(File.ReadAllText(Path.Combine(dir, "WorldEvent.Audience.cs")).Contains("WorldEventAnnouncer.Broadcast("),
                "WorldEvent.Audience.cs is the one allowed caller");
        }

        [TestMethod]
        public void ServerSources_UseTheLocalAndBroadcastSendsOnlyFromTheAnnouncerAndAudienceFiles()
        {
            var serverDir = Directory.GetParent(WorldEventsDir()).FullName;
            var files = Directory.GetFiles(serverDir, "*.cs", SearchOption.AllDirectories);
            Assert.IsTrue(files.Length > 500, "sanity: expected the whole ACE.Server tree, found " + files.Length);

            // WorldEventAnnouncer.cs defines Local; WorldEvent.Audience.cs (WorldEvent.AnnounceLocal) is the one caller. Any other
            // direct call would send a private Avatar run's line to the people around the anchor and skip the allegiance rule.
            var pattern = new System.Text.RegularExpressions.Regex(@"WorldEventAnnouncer\.(Local|LocalAt|LocalAnnounceSink|BroadcastSink)\b");
            var offenders = files
                .Where(f => Path.GetFileName(f) != "WorldEventAnnouncer.cs" && Path.GetFileName(f) != "WorldEvent.Audience.cs")
                .Where(f => pattern.IsMatch(File.ReadAllText(f)))
                .Select(f => Path.GetRelativePath(serverDir, f))
                .ToList();

            Assert.AreEqual(0, offenders.Count, "route these through WorldEvent.AnnounceLocal: " + string.Join(", ", offenders));
            Assert.IsTrue(File.ReadAllText(Path.Combine(WorldEventsDir(), "WorldEvent.Audience.cs")).Contains("WorldEventAnnouncer.LocalAnnounceSink("));
        }

        // ---- rewards: a quiet private event pays its allegiance's members only (owner ruling 2026-11-26) ---------
        //
        // Membership is frozen once at Rewarding entry (SnapshotRewardMembers) from WorldEventHooks.CharacterMonarchSource and
        // AllegianceMemberSource; the claim gate and the pooled-loot seats both read only that snapshot.

        private const uint Vassal = 1001;
        private const uint Outsider = 2000;

        private static uint DefaultMonarchOf(uint guid) => guid == Monarch || guid == Vassal ? Monarch : guid;

        private static void WithMembership(Func<uint, uint> monarchOf, Action body)
        {
            var c = WorldEventHooks.CharacterMonarchSource;
            var r = WorldEventHooks.AllegianceMemberSource;

            try
            {
                WorldEventHooks.CharacterMonarchSource = monarchOf;
                WorldEventHooks.AllegianceMemberSource = m => new uint[] { Vassal };
                body();
            }
            finally
            {
                WorldEventHooks.CharacterMonarchSource = c;
                WorldEventHooks.AllegianceMemberSource = r;
            }
        }

        private static WorldEvent RunWith(uint? avatarId, bool serverWide, uint? monarch = null, bool pool = false)
        {
            var run = new WorldEvent(1, null, new WorldEventRequest { AvatarId = avatarId, AnnounceServerWide = serverWide, AvatarMonarchGuid = monarch }, null, clock: () => 1234d);

            if (pool)
                run.LatchPooledLoot(true);

            foreach (var guid in new[] { Monarch, Vassal, Outsider })
                Assert.IsTrue(run.Participation.CreditCombatContact(guid, guid + 1, null, "P" + guid));

            return run;
        }

        [TestMethod]
        public void QuietPrivateEvent_Snapshot_ExcludesAnOutsider_NotAMember() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            InitAvatar();
            var run = RunWith(AvatarId, serverWide: false);
            run.SnapshotRewardMembers();

            Assert.IsFalse(run.ExcludesFromRewards(Monarch), "the monarch is a member");
            Assert.IsFalse(run.ExcludesFromRewards(Vassal), "a vassal is a member");
            Assert.IsTrue(run.ExcludesFromRewards(Outsider), "a character of another allegiance, or none, is excluded");
            Assert.IsTrue(run.ExcludesFromRewards(9999), "and so is a character the run never saw");
        }));

        [TestMethod]
        public void QuietPrivateEvent_Snapshot_IsFrozen_JoiningOrLeavingAfterItChangesNothing() => WithSinks(c =>
        {
            InitAvatar();
            var run = RunWith(AvatarId, serverWide: false);

            WithMembership(DefaultMonarchOf, () => run.SnapshotRewardMembers());

            // After the snapshot the outsider swears in and the vassal leaves.
            WithMembership(g => g == Outsider ? Monarch : g == Vassal ? 5555u : DefaultMonarchOf(g), () =>
            {
                run.SnapshotRewardMembers(); // a second call is a no-op

                Assert.IsTrue(run.ExcludesFromRewards(Outsider), "swearing in during the claim window does not make a member");
                Assert.IsFalse(run.ExcludesFromRewards(Vassal), "leaving after the snapshot does not take a member out");
            });
        });

        [TestMethod]
        public void QuietPrivateEvent_WithoutASnapshot_FailsClosed() => WithSinks(c =>
        {
            InitAvatar();
            var run = RunWith(AvatarId, serverWide: false);

            Assert.IsTrue(run.ExcludesFromRewards(Monarch), "no snapshot, no confirmed member");
        });

        [TestMethod]
        public void AnnouncedPrivateEvent_AndPublicRun_ExcludeNobody() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            InitAvatar();

            foreach (var run in new[] { RunWith(AvatarId, serverWide: true), RunWith(null, serverWide: true), RunWith(null, serverWide: false) })
            {
                run.SnapshotRewardMembers();

                foreach (var guid in new[] { Monarch, Vassal, Outsider })
                    Assert.IsFalse(run.ExcludesFromRewards(guid), $"{guid}: announced and public runs withhold from nobody");
            }
        }));

        [TestMethod]
        public void QuietPrivateEvent_MonarchFromTheRequest_WorksWhenTheAvatarWasNeverBound() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            // No Avatar in the manager at all (MonarchGuid null from the start): the purchase's monarch is what names the members.
            AvatarManager.Initialize(new Store(), () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            var run = RunWith(AvatarId, serverWide: false, monarch: Monarch);
            run.SnapshotRewardMembers();

            Assert.IsFalse(run.ExcludesFromRewards(Monarch));
            Assert.IsFalse(run.ExcludesFromRewards(Vassal));
            Assert.IsTrue(run.ExcludesFromRewards(Outsider));
        }));

        [TestMethod]
        public void QuietPrivateEvent_MonarchUnknownEverywhere_AndNoCommonParticipantMonarch_FailsClosed() => WithSinks(c => WithMembership(g => g, () =>
        {
            AvatarManager.Initialize(new Store(), () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            var run = RunWith(AvatarId, serverWide: false);
            run.SnapshotRewardMembers();

            foreach (var guid in new[] { Monarch, Vassal, Outsider })
                Assert.IsTrue(run.ExcludesFromRewards(guid), $"{guid}: nobody is confirmed");
        }));

        [TestMethod]
        public void QuietPrivateEvent_AnUnboundAvatarBeforeTheSnapshot_KeepsTheRequestsMonarch() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            var state = InitAvatar();
            var run = RunWith(AvatarId, serverWide: false, monarch: Monarch);

            state.MonarchGuid = null; // absorbed or retired mid-run
            run.SnapshotRewardMembers();

            Assert.IsFalse(run.ExcludesFromRewards(Vassal));
            Assert.IsTrue(run.ExcludesFromRewards(Outsider));
        }));

        [TestMethod]
        public void QuietPrivateEvent_ANewMonarchMidRun_IsTheOneFrozenAtRewardTime() => WithSinks(c => WithMembership(g => g == 5000 ? 3000u : DefaultMonarchOf(g), () =>
        {
            var state = InitAvatar();
            var run = RunWith(AvatarId, serverWide: false, monarch: Monarch);
            run.Participation.CreditCombatContact(5000, 5001, null, "New");

            state.MonarchGuid = 3000;
            run.SnapshotRewardMembers();

            Assert.IsTrue(run.ExcludesFromRewards(Monarch), "the old monarch's allegiance is no longer the Avatar's");
            Assert.IsFalse(run.ExcludesFromRewards(5000), "the current monarch's is");
        }));

        [TestMethod]
        public void QuietPrivateEvent_MonarchSucceededMidEvent_ManagerNotRebound_MembersOfTheNewMonarchGetSeats() => WithSinks(c =>
        {
            InitAvatar(); // the manager still holds the OLD monarch (1000)
            var run = RunWith(AvatarId, serverWide: false, pool: true);
            const uint NewMonarch = 4000;
            var roster = new List<uint>();

            var oldC = WorldEventHooks.CharacterMonarchSource;
            var oldR = WorldEventHooks.AllegianceMemberSource;

            try
            {
                // The succession is already live: everyone reports the new monarch, and the old monarch has no roster.
                WorldEventHooks.CharacterMonarchSource = g => g == Outsider ? Outsider : NewMonarch;
                WorldEventHooks.AllegianceMemberSource = m => m == NewMonarch ? roster : Array.Empty<uint>();

                run.DealLootPool();
            }
            finally
            {
                WorldEventHooks.CharacterMonarchSource = oldC;
                WorldEventHooks.AllegianceMemberSource = oldR;
            }

            CollectionAssert.AreEquivalent(new[] { Monarch, Vassal }, run.LootPool.SharesForTest().Keys.ToArray(), "the successor's allegiance is paid, the outsider is not");
            Assert.IsFalse(run.ExcludesFromRewards(Vassal));
            Assert.IsTrue(run.ExcludesFromRewards(Outsider));
        });

        [TestMethod]
        public void QuietPrivateEvent_NoMembersFoundAnywhere_StaysClosed() => WithSinks(c =>
        {
            AvatarManager.Initialize(new Store(), () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            var run = RunWith(AvatarId, serverWide: false, monarch: 7777); // a monarch nobody is sworn to
            var oldC = WorldEventHooks.CharacterMonarchSource;

            try
            {
                WorldEventHooks.CharacterMonarchSource = g => g; // nobody is sworn to anybody
                run.SnapshotRewardMembers();
            }
            finally
            {
                WorldEventHooks.CharacterMonarchSource = oldC;
            }

            foreach (var guid in new[] { Monarch, Vassal, Outsider })
                Assert.IsTrue(run.ExcludesFromRewards(guid), "no retry candidate: fail closed");
        });

        [TestMethod]
        public void LootPoolSeats_QuietPrivate_DealOnlyToMembers() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            InitAvatar();
            var run = RunWith(AvatarId, serverWide: false, pool: true);

            run.DealLootPool();

            CollectionAssert.AreEquivalent(new[] { Monarch, Vassal }, run.LootPool.SharesForTest().Keys.ToArray(), "the outsider is dealt no seat");
        }));

        [TestMethod]
        public void LootPoolSeats_AnnouncedAndPublic_DealToEveryParticipant() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            InitAvatar();

            foreach (var run in new[] { RunWith(AvatarId, serverWide: true, pool: true), RunWith(null, serverWide: true, pool: true) })
            {
                run.DealLootPool();

                CollectionAssert.AreEquivalent(new[] { Monarch, Vassal, Outsider }, run.LootPool.SharesForTest().Keys.ToArray());
            }
        }));

        [TestMethod]
        public void TryWithhold_WithholdsFromAnOutsider_SendsTheLineOnce_AndNeverBotherAMember() => WithSinks(c => WithMembership(DefaultMonarchOf, () =>
        {
            InitAvatar();
            var run = RunWith(AvatarId, serverWide: false);
            run.SnapshotRewardMembers();
            var sent = new List<string>();

            Assert.IsTrue(WorldEventCacheHandler.TryWithhold(run, Outsider, sent.Add));
            Assert.IsTrue(WorldEventCacheHandler.TryWithhold(run, Outsider, sent.Add), "still withheld on the second click");
            CollectionAssert.AreEqual(new[] { "This was a private allegiance event. Only its members receive the spoils." }, sent, "the line goes once");

            Assert.IsFalse(WorldEventCacheHandler.TryWithhold(run, Monarch, sent.Add));
            Assert.AreEqual(1, sent.Count, "a member is told nothing");

            var announced = RunWith(AvatarId, serverWide: true);
            Assert.IsFalse(WorldEventCacheHandler.TryWithhold(announced, Outsider, sent.Add));
            Assert.IsFalse(WorldEventCacheHandler.TryWithhold(null, Outsider, sent.Add));
            Assert.AreEqual(1, sent.Count);
        }));

        [TestMethod]
        public void RewardExclusionLine_IsTheOwnerWording()
        {
            Assert.AreEqual("This was a private allegiance event. Only its members receive the spoils.", WorldEventCacheHandler.PrivateEventExclusionLine);
        }

        // Secondary pins: the call sites exist, in the right order. The runtime tests above own the behavior; these only catch a
        // deleted or reordered call, since the handler needs a live Player and cache.
        [TestMethod]
        public void ClaimHandler_CallsTheGate_AfterTheWindow_BeforeAnythingIsGivenOrMarked()
        {
            var body = ThreadDungeons.PooledLootSourceText.MethodBody(
                ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventCacheHandler.cs"),
                "private static bool Handle(WorldObject cache, Player player)");

            var window = body.IndexOf("ClaimDenial.NotRewarding", StringComparison.Ordinal);
            var gate = body.IndexOf("if (TryWithhold(evt, characterGuid,", StringComparison.Ordinal);
            var firstGate = body.IndexOf("var gateByCharacter", StringComparison.Ordinal);
            var claimLock = body.IndexOf("lock (evt.ClaimedCharacters)", StringComparison.Ordinal);
            var create = body.IndexOf("WorldObjectFactory.CreateNewWorldObject(", StringComparison.Ordinal);
            var share = body.IndexOf("WorldEventShareDelivery.TryDeliver(evt, cache, player);", StringComparison.Ordinal);

            Assert.IsTrue(window >= 0 && gate > window, "the gate sits after the claim-window check");
            Assert.IsTrue(gate < firstGate && gate < claimLock && gate < create && gate < share, "before any gate, lock, crate or pooled share");
            Assert.IsTrue(body.Substring(gate, firstGate - gate).Contains("return true;"), "an excluded claim ends the handler");
        }

        [TestMethod]
        public void Finish_FreezesTheMembers_BeforeDealingTheLootPool()
        {
            var finish = ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEvent.cs");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(finish, @"SnapshotRewardMembers\(\);\s*DealLootPool\(\);"),
                "Finish snapshots the members right before DealLootPool");
        }

        [TestMethod]
        public void MonarchSeams_DefaultToTheAdmissionReader()
        {
            Assert.AreSame(typeof(AvatarRights).GetMethod("AdmissionMonarchGuidOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
                WorldEventHooks.CharacterMonarchSource.Method);
        }

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
