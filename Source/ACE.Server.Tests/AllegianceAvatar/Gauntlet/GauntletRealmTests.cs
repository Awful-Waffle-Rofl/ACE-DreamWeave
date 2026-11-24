using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Avatars;
using ACE.Server.Entity;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The run-less Gauntlet arena copy's realm (Task 5): the outdoor-site resolution the content filter receives, the
    /// admission rule a hold decides, and the footprint member hold. Thread copies are pinned against the expressions the
    /// code had before OutdoorEntry, IEphemeralAdmission and IFootprintHold existed. No PropertyManager key is read; the
    /// AvatarRights allegiance seam is swapped and restored.
    /// </summary>
    [TestClass]
    public class GauntletRealmTests
    {
        private const uint Monarch = 0x50000A01;
        private const uint Member = 0x50000A02;
        private const uint Outsider = 0x50000A03;
        private const uint OtherMonarch = 0x50000A04;

        private static readonly TimeSpan Window = TimeSpan.FromHours(3);

        /// <summary>
        /// Membership is seeded into the REAL guid-keyed node map (AllegianceManager.Players), the one the login reroute can
        /// read before the player is online. The AvatarRights.AllegianceFacts seam is deliberately NOT swapped: swapping it
        /// is what hid the relog bug (review F1). Every key touched is restored in cleanup.
        /// </summary>
        private readonly Dictionary<ObjectGuid, AllegianceNode> savedNodes = new Dictionary<ObjectGuid, AllegianceNode>();
        private readonly HashSet<ObjectGuid> touched = new HashSet<ObjectGuid>();

        [TestInitialize]
        public void Setup()
        {
            AvatarRights.ResetSeams();
            Seat(Monarch, Monarch);
            Seat(Member, Monarch);
            Seat(OtherMonarch, OtherMonarch);
            Seat(Outsider, OtherMonarch);
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (var g in touched)
            {
                if (savedNodes.TryGetValue(g, out var old))
                    AllegianceManager.Players[g] = old;
                else
                    AllegianceManager.Players.Remove(g);
            }

            touched.Clear();
            savedNodes.Clear();
            AvatarRights.ResetSeams();
        }

        private void Remember(ObjectGuid g)
        {
            if (touched.Add(g) && AllegianceManager.Players.TryGetValue(g, out var old))
                savedNodes[g] = old;
        }

        /// <summary>Puts <paramref name="guid"/> in the node map under <paramref name="monarch"/>, as AllegianceManager.AddPlayers does.</summary>
        private void Seat(uint guid, uint monarch)
        {
            var g = new ObjectGuid(guid);
            Remember(g);

            var monarchNode = guid == monarch ? null : new AllegianceNode(new ObjectGuid(monarch), null);
            AllegianceManager.Players[g] = new AllegianceNode(g, null, monarchNode, monarchNode);
        }

        /// <summary>Takes <paramref name="guid"/> out of the node map, as a rebuild after a break does.</summary>
        private void Unseat(uint guid)
        {
            var g = new ObjectGuid(guid);
            Remember(g);
            AllegianceManager.Players.Remove(g);
        }

        // ---- fixture ---------------------------------------------------------------------------------------------

        private static DungeonEntryDef OutdoorRow(string id, ushort primary = 0x5456) => new DungeonEntryDef
        {
            Id = id,
            Kind = DungeonEntryDef.KindOutdoor,
            Landblock = primary,
            ExitPortalWcid = 1003601,
            Outdoor = new OutdoorSiteDef { Primary = primary, Footprint = new List<ushort> { primary, (ushort)(primary + 0x0100) } },
        };

        private static DungeonEntryDef DungeonRow() => new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };

        private static ThreadDungeonRun Run(DungeonEntryDef dungeon, uint owner = Member)
        {
            var spec = new DungeonGemSpec(dungeon?.Id ?? "none", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            return new ThreadDungeonRun(0x8000AB01u, owner, "Owner", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        private static Player MakePlayer(uint guid) => SalvageRollerVendorTests.MakePlayer(guid);

        private static AvatarState Avatar() => new AvatarState { AvatarId = 77, MonarchGuid = Monarch, State = AvatarBindState.Bound };

        /// <summary>A plain hold, as Pinnacle's: no admission, no member hold.</summary>
        private sealed class PlainHold : IEphemeralHold
        {
            public TimeSpan? UnloadIntervalOverride => TimeSpan.FromMinutes(11);
        }

        /// <summary>A hold that admits everyone, for proving what runs BEFORE the admission rule.</summary>
        private sealed class OpenHold : IEphemeralHold, IEphemeralAdmission
        {
            public TimeSpan? UnloadIntervalOverride => null;
            public bool Admits(Player player) => player != null;
        }

        /// <summary>The site resolution exactly as Landblock.CreateWorldObjects wrote it before OutdoorEntry (origin/master).</summary>
        private static OutdoorSiteDef OldResolution(EphemeralRealm realm)
        {
            var threadCopyExit = realm?.ThreadCopyExitPortalWcid;
            var outdoorRun = realm?.Run;
            return threadCopyExit != null && outdoorRun?.Dungeon?.IsOutdoor == true ? outdoorRun.Dungeon.Outdoor : null;
        }

        /// <summary>HoldsFootprintMembers exactly as it was before IFootprintHold (origin/master).</summary>
        private static bool OldHold(EphemeralRealm realm) => realm.Run != null && realm.Run.State != ThreadDungeonRunState.Ended;

        // ---- the content filter's site ---------------------------------------------------------------------------

        [TestMethod]
        public void OutdoorEntry_ThreadCopyResolution_Unchanged()
        {
            var outdoorRun = Run(OutdoorRow("ow_thread"));
            var dungeonRun = Run(DungeonRow());

            var threadCopies = new[]
            {
                new EphemeralRealm(null) { Run = outdoorRun },
                new EphemeralRealm(null) { Run = dungeonRun },
                new EphemeralRealm(null) { RunlessThreadCopyExitPortalWcid = 1003601 },      // puzzlegate-sweep's copy
                new EphemeralRealm(null),                                                     // /enter-instance and friends
            };

            foreach (var realm in threadCopies)
                Assert.AreSame(OldResolution(realm), EphemeralRealm.OutdoorSiteFor(realm), "a copy with no OutdoorEntry resolves as before");

            Assert.AreSame(outdoorRun.Dungeon.Outdoor, EphemeralRealm.OutdoorSiteFor(threadCopies[0]), "control: the outdoor run resolves to its own site");
            Assert.IsNull(EphemeralRealm.OutdoorSiteFor(threadCopies[1]), "control: a dungeon run takes the dungeon filter");

            // A Run set means Run.Dungeon wins, even if an OutdoorEntry were (wrongly) set beside it.
            var stray = OutdoorRow("ow_stray", 0x6060);
            Assert.AreSame(outdoorRun.Dungeon.Outdoor, EphemeralRealm.OutdoorSiteFor(new EphemeralRealm(null) { Run = outdoorRun, OutdoorEntry = stray }));
            Assert.IsNull(EphemeralRealm.OutdoorSiteFor(new EphemeralRealm(null) { Run = dungeonRun, OutdoorEntry = stray }),
                "a dungeon run's row is read first and is not outdoor: no fallback to the entry");

            // The run-less arena copy resolves through OutdoorEntry, and only when it is Thread-shaped.
            var site = OutdoorRow("ow_arena");
            var arena = RealmManager.NewGauntletOutdoorRealm(site, new OpenHold());
            Assert.AreSame(site.Outdoor, EphemeralRealm.OutdoorSiteFor(arena));
            Assert.IsNull(OldResolution(arena), "control: before OutdoorEntry this copy would have taken the dungeon filter");
            Assert.IsNull(EphemeralRealm.OutdoorSiteFor(new EphemeralRealm(null) { OutdoorEntry = site }), "no exit wcid: not a Thread-shaped copy, no filter");
            Assert.IsNull(EphemeralRealm.OutdoorSiteFor(null));
        }

        [TestMethod]
        public void NewGauntletOutdoorRealm_IsRunless_OwnerlessAndThreadShaped()
        {
            var site = OutdoorRow("ow_arena");
            var hold = new GauntletArenaHold(_ => true, Window);
            var realm = RealmManager.NewGauntletOutdoorRealm(site, hold);

            Assert.IsNull(realm.Owner, "no Owner, as the sweep copy");
            Assert.IsNull(realm.Run, "no Thread run");
            Assert.IsFalse(realm.OpenToFellowship);
            Assert.AreEqual(1003604u, RealmManager.GauntletExitPortalWcid);
            Assert.AreEqual(1003604u, realm.ThreadCopyExitPortalWcid, "the filter, the shard-static skip and the encounter suppression key on this");
            Assert.AreSame(site, realm.OutdoorEntry);
            Assert.AreSame(hold, realm.Hold, "attached before the copy loads");
            Assert.AreEqual(site.Outdoor.Primary, realm.Footprint.Primary);
            CollectionAssert.AreEquivalent(site.Outdoor.Footprint, realm.Footprint.Landblocks.ToList());

            Assert.IsNull(RealmManager.NewGauntletOutdoorRealm(DungeonRow(), hold), "a dungeon row is refused");
            Assert.IsNull(RealmManager.NewGauntletOutdoorRealm(new DungeonEntryDef { Id = "x", Kind = DungeonEntryDef.KindOutdoor }, hold), "an outdoor row with no merged site is refused");
            Assert.IsNull(RealmManager.NewGauntletOutdoorRealm(site, null), "no hold, no copy");
            Assert.IsNull(RealmManager.NewGauntletOutdoorRealm(null, hold));
        }

        // ---- admission -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Accepts_GauntletHold_MemberAdmitted_NonMemberRefused()
        {
            var hold = GauntletArenaHold.ForAvatar(Avatar(), Window, GauntletState.Active);
            var realm = RealmManager.NewGauntletOutdoorRealm(OutdoorRow("ow_arena"), hold);
            var member = MakePlayer(Member);
            var monarch = MakePlayer(Monarch);
            var outsider = MakePlayer(Outsider);

            Assert.IsTrue(realm.Accepts(member), "a member of the bound allegiance");
            Assert.IsTrue(realm.Accepts(monarch), "the monarch is a member");
            Assert.IsFalse(realm.Accepts(outsider), "another allegiance");
            Assert.IsFalse(realm.Accepts(null), "null never matches the null Owner here");

            // The hold's answer is final: the allowed list and the fellowship rule are never reached.
            realm.Admit(outsider);
            Assert.IsFalse(realm.Accepts(outsider), "an allowed-list entry does not override the hold");

            // Membership is re-read live on every check.
            Seat(Outsider, Monarch);
            Assert.IsTrue(realm.Accepts(outsider), "joined the allegiance: admitted");
            Seat(Member, OtherMonarch);
            Assert.IsFalse(realm.Accepts(member), "swore to another allegiance: refused");
        }

        [TestMethod]
        public void Accepts_GauntletHold_RelogBeforeOnline_MemberAdmitted_FormerMemberRefused()
        {
            // Review F1: on login, Accepts runs before PlayerEnterWorld, so Player.Allegiance is still null. The guid-keyed
            // node map is what is populated then (the allegiance was loaded while the member was online and stays cached).
            var realm = RealmManager.NewGauntletOutdoorRealm(OutdoorRow("ow_arena"), GauntletArenaHold.ForAvatar(Avatar(), Window, GauntletState.Active));
            var relogging = MakePlayer(Member);

            Assert.IsNull(relogging.Allegiance, "fixture: the pre-online state, no Allegiance object");
            Assert.AreNotEqual(Monarch, AvatarRights.AllegianceFacts(relogging).MonarchGuid, "control: the online-only reader cannot see the membership here");

            Assert.IsTrue(realm.Accepts(relogging), "a member relogging inside the arena is admitted");
            Assert.AreEqual(Monarch, AvatarRights.AdmissionMonarchGuid(relogging));

            Unseat(Member);
            Assert.IsFalse(realm.Accepts(MakePlayer(Member)), "a former member (no longer in the map) is refused");
            Assert.AreEqual(Member, AvatarRights.AdmissionMonarchGuid(MakePlayer(Member)), "with no node and no Allegiance, the player's own guid");

            Unseat(Monarch);
            Assert.IsTrue(realm.Accepts(MakePlayer(Monarch)), "the monarch, with no node, still matches by own guid");
        }

        [TestMethod]
        public void Accepts_GauntletHold_OnlyInTheAdmittingStates()
        {
            var hold = GauntletArenaHold.ForAvatar(Avatar(), Window);
            var realm = RealmManager.NewGauntletOutdoorRealm(OutdoorRow("ow_arena"), hold);
            var member = MakePlayer(Member);

            var admitting = new[] { GauntletState.Staging, GauntletState.Active, GauntletState.Intermission, GauntletState.Rewarding };

            foreach (GauntletState s in Enum.GetValues(typeof(GauntletState)))
            {
                hold.State = s;
                Assert.AreEqual(admitting.Contains(s), realm.Accepts(member), $"state {s}");
                Assert.AreEqual(admitting.Contains(s), GauntletArenaHold.AdmitsIn(s), $"pure rule, state {s}");
            }

            Assert.AreEqual(GauntletState.Purchased, GauntletArenaHold.ForAvatar(Avatar(), Window).State, "a new hold starts Purchased: nobody enters a loading copy");
        }

        [TestMethod]
        public void Accepts_AvatarWithNoMonarch_AdmitsNobody()
        {
            var avatar = Avatar();
            avatar.MonarchGuid = null;
            var realm = RealmManager.NewGauntletOutdoorRealm(OutdoorRow("ow_arena"), GauntletArenaHold.ForAvatar(avatar, Window, GauntletState.Active));

            Assert.IsFalse(realm.Accepts(MakePlayer(Member)), "an absorbed or retired Avatar has no members (AvatarRights.IsMember)");
        }

        [TestMethod]
        public void Accepts_PuzzleRefusalStillFirst()
        {
            var player = MakePlayer(Member);
            var run = Run(DungeonRow(), owner: Member);
            var realm = new EphemeralRealm(null) { Run = run, Hold = new OpenHold() };

            Assert.IsTrue(realm.Accepts(player), "control: the admitting hold lets the roster owner in");

            Assert.IsTrue(run.MarkPuzzleRemoved(Member), "fixture: the puzzle policy removes them");
            Assert.IsFalse(realm.Accepts(player), "the puzzle refusal runs before any hold may admit");

            // And in the source: refusal, then the hold, then every older admitting rule.
            var accepts = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Realms/EphemeralRealm.cs"), "public bool Accepts(Player player)");
            var refusal = accepts.IndexOf("if (Run != null && ACE.Server.ThreadDungeons.ThreadPuzzleFailPolicy.RefusesEntry(Run, player))", StringComparison.Ordinal);
            var admission = accepts.IndexOf("if (Hold is IEphemeralAdmission admission)", StringComparison.Ordinal);
            var decides = accepts.IndexOf("return admission.Admits(player);", StringComparison.Ordinal);
            var owner = accepts.IndexOf("if (player == Owner)", StringComparison.Ordinal);

            Assert.IsTrue(refusal >= 0 && admission > refusal && decides > admission && owner > decides, $"refusal={refusal} admission={admission} decides={decides} owner={owner}");
        }

        [TestMethod]
        public void Accepts_PlainHold_KeepsTheOldRules()
        {
            var owner = MakePlayer(Monarch);
            var other = MakePlayer(Outsider);
            var realm = new EphemeralRealm(owner) { OpenToFellowship = false, Hold = new PlainHold() };

            Assert.IsTrue(realm.Accepts(owner), "a Pinnacle-style hold leaves the Owner rule in force");
            Assert.IsFalse(realm.Accepts(other));
            realm.Admit(other);
            Assert.IsTrue(realm.Accepts(other), "and the allowed list");
        }

        // ---- keep-alive ------------------------------------------------------------------------------------------

        [TestMethod]
        public void HoldsFootprintMembers_ThreadRuleUnchanged()
        {
            var run = Run(OutdoorRow("ow_thread"));
            var realms = new[]
            {
                new EphemeralRealm(null) { Run = run },
                new EphemeralRealm(null),
                new EphemeralRealm(null) { Hold = new PlainHold() },
                new EphemeralRealm(null) { Hold = new OpenHold() },
            };

            foreach (var realm in realms)
                Assert.AreEqual(OldHold(realm), realm.HoldsFootprintMembers, "no IFootprintHold: the old rule");

            Assert.IsTrue(realms[0].HoldsFootprintMembers, "control: a live run holds");

            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            Assert.AreEqual(OldHold(realms[0]), realms[0].HoldsFootprintMembers);
            Assert.IsTrue(realms[0].HoldsFootprintMembers, "Active holds");

            Assert.IsTrue(run.MarkEnded("test"));
            Assert.AreEqual(OldHold(realms[0]), realms[0].HoldsFootprintMembers);
            Assert.IsFalse(realms[0].HoldsFootprintMembers, "Ended releases");

            Assert.IsFalse(realms[2].HoldsFootprintMembers, "a Pinnacle hold still never holds members");
        }

        [TestMethod]
        public void HoldsFootprintMembers_GauntletHoldTrueUntilClosed()
        {
            var hold = new GauntletArenaHold(_ => true, Window);
            var realm = RealmManager.NewGauntletOutdoorRealm(OutdoorRow("ow_arena"), hold);

            foreach (GauntletState s in Enum.GetValues(typeof(GauntletState)))
            {
                hold.State = s;
                var live = !GauntletStateMachine.IsTerminal(s);

                Assert.AreEqual(live, realm.HoldsFootprintMembers, $"member hold, state {s}");
                Assert.AreEqual(live ? Window : (TimeSpan?)null, realm.UnloadIntervalOverride, $"idle window, state {s}");
            }

            hold.State = GauntletState.Active;
            Assert.IsTrue(realm.HoldsFootprintMembers);
            realm.Hold = null;
            Assert.IsFalse(realm.HoldsFootprintMembers, "a released hold releases the members");
            Assert.IsNull(realm.UnloadIntervalOverride);
        }

        [TestMethod]
        public void GauntletArenaHold_RefusesNonsense()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new GauntletArenaHold(null, Window));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GauntletArenaHold(_ => true, TimeSpan.Zero));
        }
    }
}
