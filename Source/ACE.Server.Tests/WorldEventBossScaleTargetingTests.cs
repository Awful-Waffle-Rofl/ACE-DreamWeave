using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for World Events boss scale and nearest-player targeting (2026-10-08): the scale
    /// multiplier resolution (<see cref="WorldEventSpawner.ResolveBossScaleMultiplier"/>), the per-object
    /// scale recipe (<see cref="Creature.ScaleBy"/>), the retarget interval resolution
    /// (<see cref="Creature.ResolveBossRetargetSeconds"/>) and the target choice
    /// (<see cref="Creature.SelectNearestPlayerTarget{T}"/>).
    ///
    /// Plus the spawner wiring (<see cref="WorldEventSpawner.ApplyBossTraits"/>) and the loot restore, driven on a
    /// bare in-memory Creature with no landblock, no physics object and no PropertyManager read. That Spawn calls
    /// the wiring, that FindNextTarget routes a flagged boss through the selection, that a stale MoveTo is dropped,
    /// and that the shrink shows on the client need a running world and are queued for live verification.
    /// </summary>
    [TestClass]
    public class WorldEventBossScaleTargetingTests
    {
        // ---- ResolveBossScaleMultiplier ----

        [TestMethod]
        public void ScaleMultiplier_ShippedDefault_IsTwoThirds()
        {
            Assert.AreEqual(0.67f, WorldEventSpawner.ResolveBossScaleMultiplier(0.67), 0.0001f);
        }

        [TestMethod]
        public void ScaleMultiplier_UnusableValue_MeansNoChange()
        {
            Assert.AreEqual(1.0f, WorldEventSpawner.ResolveBossScaleMultiplier(0.0), "zero would collapse the boss");
            Assert.AreEqual(1.0f, WorldEventSpawner.ResolveBossScaleMultiplier(-0.5), "negative would invert it");
            Assert.AreEqual(1.0f, WorldEventSpawner.ResolveBossScaleMultiplier(double.NaN));
            Assert.AreEqual(1.0f, WorldEventSpawner.ResolveBossScaleMultiplier(double.PositiveInfinity));
            Assert.AreEqual(1.0f, WorldEventSpawner.ResolveBossScaleMultiplier(double.NegativeInfinity));
        }

        [TestMethod]
        public void ScaleMultiplier_ValidValue_PassesThrough()
        {
            // discriminates against a resolver that ignores the config and always returns the default
            Assert.AreEqual(1.5f, WorldEventSpawner.ResolveBossScaleMultiplier(1.5), 0.0001f);
            Assert.AreEqual(0.25f, WorldEventSpawner.ResolveBossScaleMultiplier(0.25), 0.0001f);
        }

        // ---- ScaleBy ----

        [TestMethod]
        public void ScaleBy_UnscaledObject_TakesTheMultiplier()
        {
            Assert.AreEqual(0.67f, Creature.ScaleBy(null, 0.67f), 0.0001f);
        }

        [TestMethod]
        public void ScaleBy_AlreadyScaledObject_Multiplies()
        {
            // a DefaultScale 5.3 boss shrinks to 5.3 * 0.67, not to 0.67
            Assert.AreEqual(3.551f, Creature.ScaleBy(5.3f, 0.67f), 0.0001f);
        }

        [TestMethod]
        public void ScaleBy_ItemKeepsItsRatioToTheBody()
        {
            // a ScaleWieldedToBody boss at 5.3 holding a shield authored at 0.75 (so pre-multiplied to 3.975)
            const float body = 5.3f;
            const float item = 5.3f * 0.75f;
            const float mult = 0.67f;

            var newBody = Creature.ScaleBy(body, mult);
            var newItem = Creature.ScaleBy(item, mult);

            Assert.AreEqual(item / body, newItem / newBody, 0.0001f, "the item must stay the same size relative to the body");
            Assert.AreNotEqual(item, newItem, "the item must actually shrink, not keep its old size");
        }

        // ---- ResolveBossRetargetSeconds ----

        [TestMethod]
        public void RetargetSeconds_ValidValue_PassesThrough()
        {
            Assert.AreEqual(1.0, Creature.ResolveBossRetargetSeconds(1.0), 1e-9);
            Assert.AreEqual(0.5, Creature.ResolveBossRetargetSeconds(0.5), 1e-9);
        }

        [TestMethod]
        public void RetargetSeconds_UnusableValue_FallsBackToRetailFive()
        {
            Assert.AreEqual(5.0, Creature.ResolveBossRetargetSeconds(0.0), 1e-9, "zero would retarget every tick");
            Assert.AreEqual(5.0, Creature.ResolveBossRetargetSeconds(-1.0), 1e-9);
            Assert.AreEqual(5.0, Creature.ResolveBossRetargetSeconds(double.NaN), 1e-9);
            Assert.AreEqual(5.0, Creature.ResolveBossRetargetSeconds(double.PositiveInfinity), 1e-9);
            Assert.AreEqual(Creature.DefaultRetargetSeconds, Creature.ResolveBossRetargetSeconds(0.0), 1e-9);
        }

        // ---- SelectNearestPlayerTarget ----

        [TestMethod]
        public void NearestPlayer_PicksTheClosestPlayer_NotTheFirstListed()
        {
            var candidates = new List<(string, float, bool)>
            {
                ("far player", 12f, true),
                ("near player", 3f, true),
                ("mid player", 7f, true),
            };

            Assert.AreEqual("near player", Creature.SelectNearestPlayerTarget(candidates));
        }

        [TestMethod]
        public void NearestPlayer_ACloserPetLosesToAFartherPlayer()
        {
            var candidates = new List<(string, float, bool)>
            {
                ("pet", 1.5f, false),
                ("player", 10f, true),
            };

            Assert.AreEqual("player", Creature.SelectNearestPlayerTarget(candidates));
        }

        [TestMethod]
        public void NearestPlayer_NoPlayers_FallsBackToTheNearestPet()
        {
            var candidates = new List<(string, float, bool)>
            {
                ("far pet", 9f, false),
                ("near pet", 2f, false),
            };

            Assert.AreEqual("near pet", Creature.SelectNearestPlayerTarget(candidates));
        }

        [TestMethod]
        public void NearestPlayer_Empty_IsNull()
        {
            Assert.IsNull(Creature.SelectNearestPlayerTarget(new List<(string, float, bool)>()));
        }

        // ---- wiring: AppliesBossScaleAndTargeting / ApplyBossTraits ----

        private static uint nextGuid = 0x7E100000;

        private static Creature BareCreature(float? scale)
        {
            // The Creature constructor reads the vital formulas (Creature.SetEphemeralValues -> GameTables).
            TestGameTables.EnsureInitialized();

            var creature = new Creature(new Weenie { WeenieClassId = 42, WeenieType = WeenieType.Creature }, new ObjectGuid(nextGuid++));

            creature.ObjScale = scale;

            return creature;
        }

        private static WorldObject BareItem(float? scale)
        {
            var item = WorldObjectFactory.CreateWorldObject(new Weenie { WeenieClassId = 43, WeenieType = WeenieType.Generic }, new ObjectGuid(nextGuid++));

            item.ObjScale = scale;

            return item;
        }

        [TestMethod]
        public void BossTraits_OnlyTheBossKindQualifies()
        {
            foreach (WorldEventSpawnKind kind in Enum.GetValues(typeof(WorldEventSpawnKind)))
                Assert.AreEqual(kind == WorldEventSpawnKind.Boss, WorldEventSpawner.AppliesBossScaleAndTargeting(kind), kind.ToString());
        }

        [TestMethod]
        public void BossTraits_BossKind_FlagsAndScalesBodyAndEveryItem()
        {
            var boss = BareCreature(2.0f);
            var wielded = BareItem(null);
            var carried = BareItem(0.75f);

            boss.EquippedObjects[wielded.Guid] = wielded;
            boss.Inventory[carried.Guid] = carried;

            var items = WorldEventSpawner.ApplyBossTraits(boss, WorldEventSpawnKind.Boss, 0.5f);

            Assert.AreEqual(2, items);
            Assert.IsTrue(boss.ForceNearestPlayerTarget, "a boss must be flagged for nearest-player targeting");
            Assert.AreEqual(1.0f, boss.ObjScale.Value, 0.0001f, "body 2.0 x 0.5");
            Assert.AreEqual(0.5f, wielded.ObjScale.Value, 0.0001f, "an equipped item with no scale of its own takes the multiplier");
            Assert.AreEqual(0.375f, carried.ObjScale.Value, 0.0001f, "an inventory item keeps its ratio");
        }

        [TestMethod]
        public void BossTraits_NonBossKind_TouchesNothing()
        {
            var monster = BareCreature(2.0f);
            var wielded = BareItem(null);
            monster.EquippedObjects[wielded.Guid] = wielded;

            Assert.AreEqual(-1, WorldEventSpawner.ApplyBossTraits(monster, WorldEventSpawnKind.Wave, 0.5f));
            Assert.IsFalse(monster.ForceNearestPlayerTarget);
            Assert.AreEqual(2.0f, monster.ObjScale.Value, 0.0001f);
            Assert.IsNull(wielded.ObjScale);
        }

        [TestMethod]
        public void BossTraits_MultiplierOfOne_StillFlagsButLeavesScaleAlone()
        {
            var boss = BareCreature(null);

            Assert.AreEqual(0, WorldEventSpawner.ApplyBossTraits(boss, WorldEventSpawnKind.Boss, 1.0f));
            Assert.IsTrue(boss.ForceNearestPlayerTarget, "targeting must not depend on the scale lever");
            Assert.IsNull(boss.ObjScale, "no DefaultScale row may be added at 1.0");
        }

        // ---- loot restore ----

        [TestMethod]
        public void Restore_RoundTrip_PutsBackNullAndSetOriginals()
        {
            var boss = BareCreature(null);
            var noScale = BareItem(null);
            var ownScale = BareItem(0.75f);

            boss.EquippedObjects[noScale.Guid] = noScale;
            boss.Inventory[ownScale.Guid] = ownScale;

            WorldEventSpawner.ApplyBossTraits(boss, WorldEventSpawnKind.Boss, 0.67f);

            Assert.IsNotNull(noScale.ObjScale, "precondition: the item was scaled");

            boss.RestoreRuntimeItemScale(noScale);
            boss.RestoreRuntimeItemScale(ownScale);

            Assert.IsNull(noScale.ObjScale, "an item with no scale of its own must get no DefaultScale row, not 1.0");
            Assert.IsNull(noScale.GetProperty(PropertyFloat.DefaultScale));
            Assert.AreEqual(0.75f, ownScale.ObjScale.Value, 0.0001f);
        }

        [TestMethod]
        public void Restore_ItemThatWasNeverScaled_IsLeftAlone()
        {
            var boss = BareCreature(null);
            var held = BareItem(null);
            boss.EquippedObjects[held.Guid] = held;
            WorldEventSpawner.ApplyBossTraits(boss, WorldEventSpawnKind.Boss, 0.67f);

            var stranger = BareItem(1.3f);
            boss.RestoreRuntimeItemScale(stranger);

            Assert.AreEqual(1.3f, stranger.ObjScale.Value, 0.0001f);
        }

        [TestMethod]
        public void TryResolveRestoredScale_Rules()
        {
            Assert.IsFalse(Creature.TryResolveRestoredScale(null, 1, out _), "never-scaled creature");

            var map = new Dictionary<uint, float?> { { 1, null }, { 2, 0.75f } };

            Assert.IsFalse(Creature.TryResolveRestoredScale(map, 3, out _), "unrecorded item");

            Assert.IsTrue(Creature.TryResolveRestoredScale(map, 1, out var restoredNull));
            Assert.IsNull(restoredNull, "a null original stays null");

            Assert.IsTrue(Creature.TryResolveRestoredScale(map, 2, out var restoredSet));
            Assert.AreEqual(0.75f, restoredSet.Value, 0.0001f);

            Assert.IsFalse(Creature.TryResolveRestoredScale(map, 2, out _), "an entry is consumed by its restore");
        }

        // ---- ResolveRetargetSeconds ----

        [TestMethod]
        public void RetargetSeconds_UnflaggedCreature_IsRetailFiveWithoutReadingTheTunable()
        {
            Assert.AreEqual(5.0, Creature.ResolveRetargetSeconds(false, () => throw new InvalidOperationException("must not read")), 1e-9);
        }

        [TestMethod]
        public void RetargetSeconds_FlaggedCreature_UsesTheTunable()
        {
            Assert.AreEqual(1.0, Creature.ResolveRetargetSeconds(true, () => 1.0), 1e-9);
            Assert.AreEqual(5.0, Creature.ResolveRetargetSeconds(true, () => 0.0), 1e-9, "an unusable tunable still falls back");
        }

        // ---- SelectBossTarget: engagement lock + hysteresis (owner ruling 2026-10-08) ----

        private const string Prev = "prev player";
        private const string Near = "near player";

        private static List<(string, float, bool)> PrevAt(float prevDist, float nearDist)
        {
            return new List<(string, float, bool)> { (Prev, prevDist, true), (Near, nearDist, true) };
        }

        [TestMethod]
        public void Lock_PrevInMeleeRange_IsHeldEvenWhenAnotherPlayerIsMuchCloser()
        {
            // no margin at all, and the other player is 9 m nearer - only the lock can keep prev
            Assert.AreEqual(Prev, Creature.SelectBossTarget(PrevAt(10f, 1f), Prev, true, 0f));
        }

        [TestMethod]
        public void Lock_ReleasedOnceOutOfMeleeRange_NearestWins()
        {
            Assert.AreEqual(Near, Creature.SelectBossTarget(PrevAt(10f, 1f), Prev, false, 1f));
        }

        [TestMethod]
        public void Lock_NeverHoldsATargetThatIsNoLongerACandidate()
        {
            var candidates = new List<(string, float, bool)> { (Near, 4f, true) };

            Assert.AreEqual(Near, Creature.SelectBossTarget(candidates, Prev, true, 1f), "dead, out of tether or gone");
        }

        [TestMethod]
        public void Lock_NeverHoldsANonPlayer()
        {
            var candidates = new List<(string, float, bool)> { ("pet", 0.5f, false), (Near, 4f, true) };

            Assert.AreEqual(Near, Creature.SelectBossTarget(candidates, "pet", true, 1f));
        }

        [TestMethod]
        public void Hysteresis_KeepsPrevJustInsideTheMargin()
        {
            Assert.AreEqual(Prev, Creature.SelectBossTarget(PrevAt(5.9f, 5f), Prev, false, 1f), "nearest + 0.9");
        }

        [TestMethod]
        public void Hysteresis_SwitchesJustOutsideTheMargin()
        {
            Assert.AreEqual(Near, Creature.SelectBossTarget(PrevAt(6.1f, 5f), Prev, false, 1f), "nearest + 1.1");
        }

        [TestMethod]
        public void Hysteresis_ExactlyAtTheMargin_KeepsPrev()
        {
            Assert.AreEqual(Prev, Creature.SelectBossTarget(PrevAt(6.0f, 5.0f), Prev, false, 1f), "nearest + margin is inclusive");
        }

        [TestMethod]
        public void Hysteresis_ExactTieWithZeroMargin_KeepsPrev()
        {
            // the other player is listed FIRST, so the nearest-player scan picks it on the tie and only the
            // hysteresis rule can keep prev
            var candidates = new List<(string, float, bool)> { (Near, 5.0f, true), (Prev, 5.0f, true) };

            Assert.AreEqual(Prev, Creature.SelectBossTarget(candidates, Prev, false, 0f), "a tie never switches");
        }

        [TestMethod]
        public void Hysteresis_PrevNotACandidate_TakesTheNearest()
        {
            var candidates = new List<(string, float, bool)> { ("far player", 9f, true), (Near, 5f, true) };

            Assert.AreEqual(Near, Creature.SelectBossTarget(candidates, Prev, false, 1f));
        }

        [TestMethod]
        public void NoPlayers_FallsBackToNearestPetEvenWithAPrev()
        {
            var candidates = new List<(string, float, bool)> { ("far pet", 9f, false), ("near pet", 2f, false) };

            Assert.AreEqual("near pet", Creature.SelectBossTarget(candidates, "far pet", true, 1f));
        }

        // ---- ResolveBossRetargetMargin ----

        [TestMethod]
        public void RetargetMargin_ValidValue_PassesThrough()
        {
            Assert.AreEqual(1.0, Creature.ResolveBossRetargetMargin(1.0), 1e-9);
            Assert.AreEqual(2.5, Creature.ResolveBossRetargetMargin(2.5), 1e-9);
            Assert.AreEqual(0.0, Creature.ResolveBossRetargetMargin(0.0), 1e-9, "zero is a valid margin");
        }

        [TestMethod]
        public void RetargetMargin_UnusableValue_FallsBackToOne()
        {
            Assert.AreEqual(1.0, Creature.ResolveBossRetargetMargin(-0.5), 1e-9);
            Assert.AreEqual(1.0, Creature.ResolveBossRetargetMargin(double.NaN), 1e-9);
            Assert.AreEqual(1.0, Creature.ResolveBossRetargetMargin(double.PositiveInfinity), 1e-9);
            Assert.AreEqual(Creature.DefaultRetargetMargin, Creature.ResolveBossRetargetMargin(-1.0), 1e-9);
        }
    }
}
