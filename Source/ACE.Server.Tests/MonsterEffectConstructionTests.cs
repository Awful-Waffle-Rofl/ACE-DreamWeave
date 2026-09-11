using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Attachment half of the monster combat effect system: what a Creature ends up holding when its weenie
    /// carries PropertyString 9015 MonsterCombatEffects. Bare creatures from TestCreatures - no database, no
    /// dat files, no landblock.
    ///
    /// The two invariants worth the test are the ones a later phase would quietly break: MonsterEffects is
    /// NULL for a monster that authored nothing (every hot path is one null check on a field), and two
    /// creatures of one wcid SHARE the immutable set while holding DISTINCT state arrays (one drudge's ramp
    /// stacks must not be the drudge next to it).
    /// </summary>
    [TestClass]
    public class MonsterEffectConstructionTests
    {
        private const string ThreeEffects =
            "flatdamage type=fire amount=30 chance=0.35; leech vital=health pct=0.12; ramp axis=attackspeed on=hit per=0.04 max=8 window=12";

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // monster_effect_max_per_monster is read while resolving; seeds the tunable cache from
            // hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void Creature_WithoutTheProperty_HasNullEffects()
        {
            var creature = TestCreatures.CreateEffectCarrier();

            Assert.IsNull(creature.MonsterEffects);
            Assert.IsNull(creature.MonsterEffectStates);
        }

        /// <summary>
        /// Entry order is the DISPATCH ORDER, not the authored order, and this pins both halves of the rule
        /// against ThreeEffects ("flatdamage ...; leech ...; ramp ..."): ramp is a DamageMutator so it sorts
        /// ahead of both, leech is a DamageReader so it sorts behind both, and flatdamage - Neutral, and the
        /// record authored first - keeps its relative place in the middle because the sort is stable.
        ///
        /// It used to assert the authored order verbatim, which was the same list only because the authored
        /// string happened to be written that way. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        [TestMethod]
        public void Creature_WithTheProperty_BuildsASetInDispatchOrderAndAMatchingStateArray()
        {
            var creature = TestCreatures.CreateEffectCarrier(ThreeEffects);

            Assert.IsNotNull(creature.MonsterEffects);
            Assert.AreEqual(3, creature.MonsterEffects.Count);
            Assert.AreEqual(3, creature.MonsterEffectStates.Length);

            Assert.AreEqual("ramp", creature.MonsterEffects[0].Spec.Kind, "a damage mutator runs first, whatever the authored order");
            Assert.AreEqual("flatdamage", creature.MonsterEffects[1].Spec.Kind, "neutral, and authored first of the neutrals");
            Assert.AreEqual("leech", creature.MonsterEffects[2].Spec.Kind, "a damage reader runs last, so it sees the final figure");
        }

        /// <summary>
        /// All sixteen kinds ship a handler now (MonsterEffectRegistry.reservedKinds is empty), so every one
        /// of the ThreeEffects records - including "ramp" - is ACTIVE and lands in the hook buckets its
        /// interfaces declare. Renamed from the phase-0-era Phase0ReservedEntries_AreInertAndInNoHookBucket,
        /// which asserted the opposite and would now fail: ramp was the last reserved kind this fixture used.
        /// </summary>
        [TestMethod]
        public void AllThreeEffects_AreActiveAndInTheirDeclaredHookBuckets()
        {
            var set = TestCreatures.CreateEffectCarrier(ThreeEffects).MonsterEffects;

            Assert.AreEqual(3, set.ActiveCount, "flatdamage, leech and ramp are all shipped handlers");
            Assert.AreEqual(3, set.OutgoingHitIndexes.Count, "all three implement IMonsterOutgoingHit (ramp accrues on=hit by default)");
            Assert.AreEqual(0, set.IncomingDamageIndexes.Count);
            Assert.AreEqual(1, set.AvoidanceIndexes.Count, "ramp also implements IMonsterAvoidance (a pure 0.0 GetAvoidChance)");
            Assert.AreEqual(1, set.SpellHitIndexes.Count, "ramp also implements IMonsterSpellHit");
            Assert.AreEqual(0, set.SpeedModIndexes.Count);
            Assert.AreEqual(0, set.CastHookIndexes.Count);
            Assert.AreEqual(1, set.HeartbeatIndexes.Count, "ramp also implements IMonsterHeartbeat, for its window lapse");
            Assert.AreEqual(1, set.RampSourceIndexes.Count, "ramp publishes through IMonsterRampSource");
        }

        /// <summary>
        /// The registry-level invariant the whole sixteen-kind rollout was building toward:
        /// MonsterEffectRegistry.reservedKinds is empty, and every one of the sixteen designed kinds
        /// resolves to a real handler rather than sitting inert.
        /// </summary>
        [TestMethod]
        public void AllSixteenKinds_ResolveToAHandler()
        {
            var kinds = new[]
            {
                "flatdamage", "leech", "execute", "rangeramp", "speed", "reflect", "avoid", "dot",
                "debuff", "castspell", "dispel", "recast", "ramp", "ward", "manabarrier", "riposte",
            };

            Assert.AreEqual(16, kinds.Length, "this test's own list must cover all sixteen designed kinds");
            Assert.AreEqual(16, MonsterEffectRegistry.KnownKinds.Count, "reservedKinds must be empty - every known kind is a shipped handler");

            foreach (var kind in kinds)
            {
                Assert.IsTrue(MonsterEffectRegistry.IsKnownKind(kind), $"'{kind}' must be a known kind");
                Assert.IsTrue(MonsterEffectRegistry.TryGetHandler(kind, out var handler), $"'{kind}' must resolve to a real handler, not a reserved (inert) entry");
                Assert.IsNotNull(handler);
            }
        }

        /// <summary>
        /// ThreeEffects DOES carry a real ramp source now (axis=attackspeed), but a freshly built creature
        /// holds zero stacks, so the multiplier is still neutral until something accrues a stack.
        /// </summary>
        [TestMethod]
        public void GetRampMultiplier_WithNoStacksAccruedYet_IsNeutral()
        {
            var creature = TestCreatures.CreateEffectCarrier(ThreeEffects);

            Assert.AreEqual(1.0, creature.MonsterEffects.GetRampMultiplier(MonsterRampAxis.AttackSpeed, creature.MonsterEffectStates), 0.0001);

            // a different axis than the one ThreeEffects' ramp record publishes on must also stay neutral
            Assert.AreEqual(1.0, creature.MonsterEffects.GetRampMultiplier(MonsterRampAxis.MagicDamage, creature.MonsterEffectStates), 0.0001);
        }

        /// <summary>
        /// The caching invariant: one parse per (wcid, string), shared immutably; state per creature.
        /// </summary>
        [TestMethod]
        public void TwoCreaturesOfOneWcid_ShareTheSetButNotTheState()
        {
            var wcid = TestCreatures.NextWcid();

            var first = TestCreatures.CreateEffectCarrier(ThreeEffects, wcid);
            var second = TestCreatures.CreateEffectCarrier(ThreeEffects, wcid);

            Assert.AreSame(first.MonsterEffects, second.MonsterEffects, "the resolved set must be parsed once per wcid and shared");
            Assert.AreNotSame(first.MonsterEffectStates, second.MonsterEffectStates, "each creature must own its state array");

            first.MonsterEffectStates[2].Stacks = 5;

            Assert.AreEqual(0, second.MonsterEffectStates[2].Stacks, "one creature's stacks must not be visible on another");
        }

        [TestMethod]
        public void DifferentWcids_DoNotShareASet()
        {
            var first = TestCreatures.CreateEffectCarrier(ThreeEffects);
            var second = TestCreatures.CreateEffectCarrier(ThreeEffects);

            Assert.AreNotSame(first.MonsterEffects, second.MonsterEffects);
        }

        /// <summary>
        /// A GamePiece attack deals target.Health.Current and returns before the damage pipeline entirely
        /// (Monster_Melee), so its authored effects would never fire. Refused at build time rather than
        /// shipped inert.
        /// </summary>
        [TestMethod]
        public void GamePiece_IsRejectedAndLeavesEffectsNull()
        {
            var creature = TestCreatures.CreateEffectCarrier(ThreeEffects, weenieType: WeenieType.GamePiece);

            Assert.IsNull(creature.MonsterEffects);
            Assert.IsNull(creature.MonsterEffectStates);
        }

        /// <summary>
        /// A string that parses to nothing usable leaves MonsterEffects null, so a typo costs the monster its
        /// effects rather than giving every hot path an empty set to walk.
        /// </summary>
        [TestMethod]
        public void AllRecordsUnknown_LeavesEffectsNull()
        {
            var creature = TestCreatures.CreateEffectCarrier("notaneffect amount=1; alsonotone pct=0.5");

            Assert.IsNull(creature.MonsterEffects);
            Assert.IsNull(creature.MonsterEffectStates);
        }

        /// <summary>
        /// monster_effect_max_per_monster truncates the authored list. Default is 12, so 14 records resolve
        /// to 12 - and the surviving entries are the FIRST twelve, so an author reading the weenie top-down
        /// sees which ones were dropped.
        /// </summary>
        [TestMethod]
        public void MoreEffectsThanTheCap_AreTruncatedToTheCap()
        {
            var cap = (int)PropertyManager.GetLong("monster_effect_max_per_monster").Item;
            Assert.AreEqual(12, cap, "this test is written against the shipped default");

            // amount starts at 1 (not 0): flatdamage now ships a handler whose Validate rejects amount <= 0,
            // and this test is about the truncation cap, not about validation rejection
            var records = new string[cap + 2];
            for (var i = 0; i < records.Length; i++)
                records[i] = $"flatdamage type=fire amount={i + 1}";

            var creature = TestCreatures.CreateEffectCarrier(string.Join("; ", records));

            Assert.AreEqual(cap, creature.MonsterEffects.Count);
            Assert.AreEqual(cap, creature.MonsterEffectStates.Length);
            Assert.AreEqual("1", creature.MonsterEffects[0].Spec.GetString("amount"));
            Assert.AreEqual(cap.ToString(), creature.MonsterEffects[cap - 1].Spec.GetString("amount"));
        }
    }
}
