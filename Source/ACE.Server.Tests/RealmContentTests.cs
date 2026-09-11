using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Database;
using ACE.Database.Adapter;
using ACE.Database.Models.World;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Realms Phase 3: per-realm content rows must map losslessly into the base
    /// LandblockInstance shape that WorldObjectFactory consumes.
    /// </summary>
    [TestClass]
    public class RealmContentTests
    {
        [TestMethod]
        public void RealmContentConverter_MapsAllSpawnFields()
        {
            var row = new LandblockInstanceRealm
            {
                RealmId = 1,
                Guid = 0x7019EFFF,
                Landblock = 0x019E,
                WeenieClassId = 3113,
                ObjCellId = 0x019E0109,
                OriginX = 10.5f,
                OriginY = -20.25f,
                OriginZ = 0.005f,
                AnglesW = 0.707107f,
                AnglesX = 0f,
                AnglesY = 0f,
                AnglesZ = -0.707107f,
                IsLinkChild = true,
                LastModified = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            };

            var instance = RealmContentConverter.ConvertToLandblockInstance(row);

            Assert.AreEqual(row.Guid, instance.Guid);
            Assert.AreEqual(row.Landblock, instance.Landblock);
            Assert.AreEqual(row.WeenieClassId, instance.WeenieClassId);
            Assert.AreEqual(row.ObjCellId, instance.ObjCellId);
            Assert.AreEqual(row.OriginX, instance.OriginX);
            Assert.AreEqual(row.OriginY, instance.OriginY);
            Assert.AreEqual(row.OriginZ, instance.OriginZ);
            Assert.AreEqual(row.AnglesW, instance.AnglesW);
            Assert.AreEqual(row.AnglesX, instance.AnglesX);
            Assert.AreEqual(row.AnglesY, instance.AnglesY);
            Assert.AreEqual(row.AnglesZ, instance.AnglesZ);
            Assert.AreEqual(row.IsLinkChild, instance.IsLinkChild);
        }

        [TestMethod]
        public void RealmContentConverter_MapsGeneratorLinks()
        {
            var row = new LandblockInstanceRealm
            {
                RealmId = 1,
                Guid = 0x7019EFFE,
                WeenieClassId = 5085,
                LandblockInstanceLinkRealm = new List<LandblockInstanceLinkRealm>
                {
                    new LandblockInstanceLinkRealm { RealmId = 1, ParentGuid = 0x7019EFFE, ChildGuid = 0x7019EFFD },
                    new LandblockInstanceLinkRealm { RealmId = 1, ParentGuid = 0x7019EFFE, ChildGuid = 0x7019EFFC },
                },
            };

            var instance = RealmContentConverter.ConvertToLandblockInstance(row);

            Assert.AreEqual(2, instance.LandblockInstanceLink.Count);

            foreach (var link in instance.LandblockInstanceLink)
                Assert.AreEqual(instance.Guid, link.ParentGuid);
        }


        // =====================================
        // Realms Phase 4: realm_landblock_rule
        // =====================================

        private const ushort TestLandblock = 0x019E;
        private const ushort OtherLandblock = 0x019F;
        private const ushort TestRealm = 1;
        private const ushort OtherRealm = 2;

        /// <summary>
        /// Stands the realm content resolution up without a database: every method that
        /// would open a WorldDbContext is overridden with a fixture.
        /// </summary>
        private class FakeWorldDatabase : WorldDatabaseWithEntityCache
        {
            public readonly List<RealmLandblockRule> Rules = new List<RealmLandblockRule>();
            public readonly List<Realm> Realms = new List<Realm>();
            public readonly Dictionary<ushort, List<LandblockInstance>> BaseInstances = new Dictionary<ushort, List<LandblockInstance>>();
            public readonly Dictionary<uint, List<LandblockInstance>> RealmInstances = new Dictionary<uint, List<LandblockInstance>>();
            public readonly Dictionary<ushort, List<Encounter>> BaseEncounters = new Dictionary<ushort, List<Encounter>>();

            /// <summary>How many times the whole rule table has been read.</summary>
            public int RuleLoadCount;

            /// <summary>How many times the whole realm table has been read.</summary>
            public int RealmLoadCount;

            /// <summary>
            /// Optional latch pair used to hold a resolution in flight inside the base-content
            /// leaf, so a rule reload can be raced against it. Both null in ordinary tests.
            /// </summary>
            public ManualResetEventSlim BaseInstancesEntered;
            public ManualResetEventSlim BaseInstancesRelease;

            /// <summary>
            /// The same idea on the realm read, which lets a test park a RELOAD partway through
            /// and inspect what a concurrent resolver sees while it is in progress.
            /// </summary>
            public ManualResetEventSlim RealmsEntered;
            public ManualResetEventSlim RealmsRelease;

            public override List<RealmLandblockRule> GetAllRealmLandblockRules()
            {
                RuleLoadCount++;
                return new List<RealmLandblockRule>(Rules);
            }

            public override List<Realm> GetAllRealms()
            {
                RealmLoadCount++;

                RealmsEntered?.Set();
                RealmsRelease?.Wait(TimeSpan.FromSeconds(10));

                return new List<Realm>(Realms);
            }

            public override List<LandblockInstance> GetCachedInstancesByLandblock(ushort landblock)
            {
                BaseInstancesEntered?.Set();
                BaseInstancesRelease?.Wait(TimeSpan.FromSeconds(10));

                return BaseInstances.TryGetValue(landblock, out var value) ? value : new List<LandblockInstance>();
            }

            public override List<LandblockInstance> GetRealmInstancesByLandblock(ushort landblock, ushort realmId)
            {
                return RealmInstances.TryGetValue(((uint)realmId << 16) | landblock, out var value) ? value : new List<LandblockInstance>();
            }

            public override List<Encounter> GetCachedEncountersByLandblock(ushort landblock)
            {
                return BaseEncounters.TryGetValue(landblock, out var value) ? value : new List<Encounter>();
            }
        }

        private static FakeWorldDatabase NewFake()
        {
            var db = new FakeWorldDatabase();

            db.BaseInstances[TestLandblock] = new List<LandblockInstance>
            {
                new LandblockInstance { Guid = 0x7019E001, WeenieClassId = 1234 },
                new LandblockInstance { Guid = 0x7019E002, WeenieClassId = 5678 },
            };
            db.BaseInstances[OtherLandblock] = new List<LandblockInstance>
            {
                new LandblockInstance { Guid = 0x7019F001, WeenieClassId = 1234 },
            };

            db.BaseEncounters[TestLandblock] = new List<Encounter>
            {
                new Encounter { Id = 1, Landblock = TestLandblock, WeenieClassId = 7788, CellX = 2, CellY = 3 },
            };

            return db;
        }

        private static RealmLandblockRule Rule(ushort realmId, ushort landblock, bool stripStatics, bool stripEncounters)
        {
            return new RealmLandblockRule
            {
                RealmId = realmId,
                Landblock = landblock,
                StripStatics = stripStatics,
                StripEncounters = stripEncounters,
            };
        }

        /// <summary>
        /// A realm registry row carrying the realm-level strip defaults. Absent from the fake by
        /// default, which is the "no realm row at all" case and must resolve to inherit.
        /// </summary>
        private static Realm RealmRow(ushort id, bool stripStatics, bool stripEncounters)
        {
            return new Realm
            {
                Id = id,
                Name = $"test realm {id}",
                DefaultStripStatics = stripStatics,
                DefaultStripEncounters = stripEncounters,
            };
        }

        [TestMethod]
        public void Instances_Realm0_ShortCircuitsBeforeAnyRuleLookup()
        {
            var db = NewFake();
            db.Rules.Add(Rule(0, TestLandblock, stripStatics: true, stripEncounters: true));
            db.Realms.Add(RealmRow(0, stripStatics: true, stripEncounters: true));

            var result = db.GetCachedInstancesByLandblock(TestLandblock, 0);

            Assert.AreEqual(2, result.Count, "realm 0 must return base content even when a bogus rule and defaults row name it");
            Assert.AreEqual(0, db.RuleLoadCount, "realm 0 must short-circuit before the rule table is touched");
            Assert.AreEqual(0, db.RealmLoadCount, "realm 0 must short-circuit before the realm defaults are touched");
        }

        [TestMethod]
        public void Instances_AuthoredRealmRowsWinOverStripStatics()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: false));
            db.RealmInstances[((uint)TestRealm << 16) | TestLandblock] = new List<LandblockInstance>
            {
                new LandblockInstance { Guid = 0x7019EFFF, WeenieClassId = 3113 },
            };

            var result = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0x7019EFFFu, result[0].Guid);
        }

        [TestMethod]
        public void Instances_StripStatics_YieldsEmpty()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: false));

            var result = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Instances_AbsentRule_InheritsBaseContent()
        {
            var db = NewFake();

            var result = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm);

            Assert.AreEqual(2, result.Count, "an absent rule means not stripped - existing realm landblocks must be unaffected");
            Assert.AreEqual(1, db.RuleLoadCount, "the rule table is read in bulk, once");
        }

        [TestMethod]
        public void Instances_RuleWithoutStripStatics_InheritsBaseContent()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: false, stripEncounters: true));

            var result = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm);

            Assert.AreEqual(2, result.Count, "strip_encounters alone must not strip statics");
        }

        [TestMethod]
        public void Instances_RuleAppliesOnlyToItsOwnRealmAndLandblock()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: false));

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, OtherRealm).Count, "another realm must not pick up this realm's rule");
            Assert.AreEqual(1, db.GetCachedInstancesByLandblock(OtherLandblock, TestRealm).Count, "another landblock in the same realm must not pick up the rule");
        }

        [TestMethod]
        public void Encounters_Realm0_ShortCircuitsBeforeAnyRuleLookup()
        {
            var db = NewFake();
            db.Rules.Add(Rule(0, TestLandblock, stripStatics: true, stripEncounters: true));
            db.Realms.Add(RealmRow(0, stripStatics: true, stripEncounters: true));

            var result = db.GetCachedEncountersByLandblock(TestLandblock, 0);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, db.RuleLoadCount, "realm 0 must short-circuit before the rule table is touched");
            Assert.AreEqual(0, db.RealmLoadCount, "realm 0 must short-circuit before the realm defaults are touched");
        }

        [TestMethod]
        public void Encounters_StripEncounters_YieldsEmpty()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: false, stripEncounters: true));

            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
        }

        [TestMethod]
        public void Encounters_AbsentRule_InheritsBaseEncounters()
        {
            var db = NewFake();

            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
        }

        [TestMethod]
        public void Encounters_RuleWithoutStripEncounters_InheritsBaseEncounters()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: false));

            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count, "strip_statics alone must not strip encounters");
        }

        [TestMethod]
        public void CacheAllRealmLandblockRules_InvalidatesAlreadyResolvedInstances()
        {
            var db = NewFake();

            // resolve first, with no rule in place - this is the ordering hazard: the pair
            // is now cached as "inherits base"
            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);

            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));

            Assert.AreEqual(1, db.CacheAllRealmLandblockRules());

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "the reload must drop instance cache entries resolved against the old rules");
            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
        }

        [TestMethod]
        public void CacheAllRealmLandblockRules_RemovingARuleRestoresBaseContent()
        {
            var db = NewFake();
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);

            db.Rules.Clear();

            Assert.AreEqual(0, db.CacheAllRealmLandblockRules());

            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
        }

        /// <summary>
        /// A live /reload-realms can land in the middle of a landblock activation. The resolver
        /// reads the rules and only afterwards writes its answer into the instance cache, so
        /// without a version stamp it can insert a result derived from the OLD rules after the
        /// reload has already cleared the cache, and that landblock then serves the wrong
        /// content until the next reload or a restart.
        /// </summary>
        [TestMethod]
        public void ReloadDuringResolution_DoesNotPoisonTheInstanceCache()
        {
            var db = NewFake();

            db.BaseInstancesEntered = new ManualResetEventSlim(false);
            db.BaseInstancesRelease = new ManualResetEventSlim(false);

            // start a resolution with NO rule in place: it resolves to the base content and
            // then parks inside the base-content leaf, about to cache that answer
            List<LandblockInstance> inFlightResult = null;

            var resolver = new Thread(() => inFlightResult = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm))
            {
                IsBackground = true,
            };

            resolver.Start();

            Assert.IsTrue(db.BaseInstancesEntered.Wait(TimeSpan.FromSeconds(10)), "the resolver never reached the base content leaf");

            // the reload lands mid-resolution, and now strips this landblock
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: false));

            Assert.AreEqual(1, db.CacheAllRealmLandblockRules());

            // let the in-flight resolution finish and write its now superseded answer
            db.BaseInstancesRelease.Set();

            Assert.IsTrue(resolver.Join(TimeSpan.FromSeconds(10)), "the resolver thread did not finish");
            Assert.AreEqual(2, inFlightResult.Count, "the in-flight call itself legitimately returns what it resolved");

            // the latches are only for the racing call - the assertion below must not block
            db.BaseInstancesEntered = null;
            db.BaseInstancesRelease = null;

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "a reload during resolution must not leave the landblock serving un-stripped content");
        }

        // =====================================
        // Realms Phase 4: realm-level strip defaults
        // =====================================

        [TestMethod]
        public void Defaults_StripStatics_AppliesToAnUnruledBlock()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: false));

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "the realm default must strip a block that has no rule row");
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count, "default_strip_statics alone must not strip encounters");
        }

        [TestMethod]
        public void Defaults_StripEncounters_AppliesToAnUnruledBlock()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: true));

            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count, "the realm default must strip encounters on a block that has no rule row");
            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "default_strip_encounters alone must not strip statics");
        }

        [TestMethod]
        public void Defaults_RealmRowWithBothFlagsOff_Inherits()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));

            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
        }

        [TestMethod]
        public void Defaults_RuleRowPunchesThroughAStripDefault()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));

            // an EXISTING rule row decides in both directions: 0/0 means "inherit retail here"
            // even though the realm strips everything else
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: false, stripEncounters: false));

            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "a rule row with strip_statics = 0 must punch through the realm default");
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count, "a rule row with strip_encounters = 0 must punch through the realm default");

            // the punch-through is scoped to its own block: the rest of the realm still strips
            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(OtherLandblock, TestRealm).Count);
        }

        [TestMethod]
        public void Defaults_RuleRowStillStripsWhenTheDefaultInherits()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);

            // and only on its own block
            Assert.AreEqual(1, db.GetCachedInstancesByLandblock(OtherLandblock, TestRealm).Count);
        }

        [TestMethod]
        public void Defaults_AuthoredRowsBeatBothRuleAndDefault()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));
            db.RealmInstances[((uint)TestRealm << 16) | TestLandblock] = new List<LandblockInstance>
            {
                new LandblockInstance { Guid = 0x7019EFFF, WeenieClassId = 3113 },
            };

            var result = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm);

            Assert.AreEqual(1, result.Count, "authored realm content outranks both the rule row and the realm default");
            Assert.AreEqual(0x7019EFFFu, result[0].Guid);
        }

        [TestMethod]
        public void Defaults_ApplyOnlyToTheirOwnRealm()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, OtherRealm).Count, "a realm with no row of its own must inherit");
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, OtherRealm).Count);
        }

        [TestMethod]
        public void Defaults_Realm0Row_IsNeverConsulted()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(0, stripStatics: true, stripEncounters: true));

            // force a load, so the realm 0 row is genuinely in the loaded set rather than unread
            Assert.AreEqual(0, db.CacheAllRealmLandblockRules());

            Assert.AreEqual((false, false), db.GetRealmStripDefaults(0), "realm 0 must never acquire defaults, even from its own row");
            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, 0).Count);
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, 0).Count);
        }

        [TestMethod]
        public void Defaults_AreLoadedInTheSameBulkPassAsTheRules()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));

            db.GetCachedInstancesByLandblock(TestLandblock, TestRealm);
            db.GetCachedEncountersByLandblock(TestLandblock, TestRealm);
            db.GetCachedInstancesByLandblock(OtherLandblock, TestRealm);

            Assert.AreEqual(1, db.RuleLoadCount, "the rule table is read in bulk, once");
            Assert.AreEqual(1, db.RealmLoadCount, "the realm defaults ride along on the same bulk pass");
        }

        [TestMethod]
        public void CacheAllRealmLandblockRules_FlippingADefaultTakesEffectOnAResolvedPair()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));

            Assert.AreEqual(2, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(1, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);

            db.Realms.Clear();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));

            Assert.AreEqual(0, db.CacheAllRealmLandblockRules(), "no per-block rules, only a default change");

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "a default flip must invalidate an already-resolved pair exactly like a rule change does");
            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
        }

        /// <summary>
        /// The version-stamp regression test, mirrored onto the realm defaults: the defaults share
        /// the rules' bulk load and version stamp precisely so that a reload flipping a DEFAULT is
        /// as safe against an in-flight resolution as one changing a rule row.
        /// </summary>
        [TestMethod]
        public void ReloadDuringResolution_DefaultFlip_DoesNotPoisonTheInstanceCache()
        {
            var db = NewFake();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));

            db.BaseInstancesEntered = new ManualResetEventSlim(false);
            db.BaseInstancesRelease = new ManualResetEventSlim(false);

            List<LandblockInstance> inFlightResult = null;

            var resolver = new Thread(() => inFlightResult = db.GetCachedInstancesByLandblock(TestLandblock, TestRealm))
            {
                IsBackground = true,
            };

            resolver.Start();

            Assert.IsTrue(db.BaseInstancesEntered.Wait(TimeSpan.FromSeconds(10)), "the resolver never reached the base content leaf");

            // the reload lands mid-resolution and flips the realm's default to strip
            db.Realms.Clear();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));

            Assert.AreEqual(0, db.CacheAllRealmLandblockRules());

            db.BaseInstancesRelease.Set();

            Assert.IsTrue(resolver.Join(TimeSpan.FromSeconds(10)), "the resolver thread did not finish");
            Assert.AreEqual(2, inFlightResult.Count, "the in-flight call itself legitimately returns what it resolved");

            db.BaseInstancesEntered = null;
            db.BaseInstancesRelease = null;

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count, "a default flip during resolution must not leave the landblock serving un-stripped content");
        }

        /// <summary>
        /// The rules and the defaults are published as one immutable snapshot precisely so that a
        /// reader can never pair one generation's rules with another's defaults. The scenario that
        /// makes a mixed read visible: a content edit DELETES a block's rule row and flips the
        /// realm default, leaving the net behaviour unchanged - so only a torn read can produce a
        /// third answer (full retail) that is wrong under both generations.
        /// </summary>
        [TestMethod]
        public void ReloadInProgress_NeverExposesAMixedGeneration()
        {
            var db = NewFake();

            // generation A: the block is stripped by its RULE ROW; the realm default inherits
            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));

            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);

            // generation B: the rule row is gone and the realm default strips instead. Same net
            // behaviour for this landblock under either generation, taken whole.
            db.Rules.Clear();
            db.Realms.Clear();
            db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));

            db.RealmsEntered = new ManualResetEventSlim(false);
            db.RealmsRelease = new ManualResetEventSlim(false);

            var reloader = new Thread(() => db.CacheAllRealmLandblockRules())
            {
                IsBackground = true,
            };

            reloader.Start();

            Assert.IsTrue(db.RealmsEntered.Wait(TimeSpan.FromSeconds(10)), "the reload never reached the realm read");

            // mid-reload, a reader must still see generation A entire - both halves of it
            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count, "a reload in progress must never resolve this landblock to retail content");
            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
            Assert.IsNotNull(db.GetRealmLandblockRule(TestRealm, TestLandblock), "still generation A: the rule row is what strips the block");
            Assert.AreEqual((false, false), db.GetRealmStripDefaults(TestRealm), "still generation A: the realm default still inherits");

            db.RealmsRelease.Set();

            Assert.IsTrue(reloader.Join(TimeSpan.FromSeconds(10)), "the reload thread did not finish");

            // and afterwards, generation B entire
            Assert.IsNull(db.GetRealmLandblockRule(TestRealm, TestLandblock));
            Assert.AreEqual((true, true), db.GetRealmStripDefaults(TestRealm));
            Assert.AreEqual(0, db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count);
            Assert.AreEqual(0, db.GetCachedInstancesByLandblock(TestLandblock, TestRealm).Count);
        }

        /// <summary>
        /// The same invariant hammered rather than latched. Two generations that both strip the
        /// landblock, by opposite mechanisms, are swapped repeatedly while a resolver reads
        /// continuously; any retail answer means it saw half of each.
        /// <para />
        /// Best-effort DETECTION, not proof: with the snapshot design the mixed state does not
        /// exist to be caught, so this test cannot fail. It earns its place by having real power
        /// against the incremental-publish shape it replaced, where clearing the rules map ahead
        /// of the defaults map opens exactly this window.
        /// </summary>
        [TestMethod]
        public void ReloadStress_ConcurrentResolutionNeverSeesAMixedGeneration()
        {
            var db = NewFake();

            db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));
            db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));

            db.CacheAllRealmLandblockRules();

            var stop = false;
            var mixedReads = 0;
            Exception resolverFailure = null;

            var resolver = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        // the encounter path caches nothing of its own, so every call is a fresh
                        // read of whatever snapshot is current at that instant
                        if (db.GetCachedEncountersByLandblock(TestLandblock, TestRealm).Count != 0)
                            Interlocked.Increment(ref mixedReads);
                    }
                }
                catch (Exception ex)
                {
                    resolverFailure = ex;
                }
            })
            {
                IsBackground = true,
            };

            resolver.Start();

            for (var i = 0; i < 500; i++)
            {
                db.Rules.Clear();
                db.Realms.Clear();

                if (i % 2 == 0)
                {
                    // stripped by the realm default, no rule row at all
                    db.Realms.Add(RealmRow(TestRealm, stripStatics: true, stripEncounters: true));
                }
                else
                {
                    // stripped by the rule row, with the realm default inheriting
                    db.Rules.Add(Rule(TestRealm, TestLandblock, stripStatics: true, stripEncounters: true));
                    db.Realms.Add(RealmRow(TestRealm, stripStatics: false, stripEncounters: false));
                }

                db.CacheAllRealmLandblockRules();
            }

            Volatile.Write(ref stop, true);

            Assert.IsTrue(resolver.Join(TimeSpan.FromSeconds(10)), "the resolver thread did not finish");
            Assert.IsNull(resolverFailure, $"the resolver threw: {resolverFailure}");
            Assert.AreEqual(0, mixedReads, "a concurrent resolution observed a mixed generation - the snapshot was not published atomically");
        }

        [TestMethod]
        public void GetRealmLandblockRule_Realm0_IsAlwaysNull()
        {
            var db = NewFake();
            db.Rules.Add(Rule(0, TestLandblock, stripStatics: true, stripEncounters: true));

            Assert.IsNull(db.GetRealmLandblockRule(0, TestLandblock));
            Assert.AreEqual(0, db.RuleLoadCount);
        }
    }
}
