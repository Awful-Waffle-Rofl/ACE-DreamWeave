using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Echo Cast wisp rework, plan step 3 ("controller and trigger swap"; Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-PLAN.md
    /// sections 2-3). The pure rules (EchoWispMath, the basis note, the spawn choice, the body preparation) are pinned by
    /// value; the wiring a live Player is needed to drive - the owner-queue routing, the trigger sites, the school body
    /// reaching the spawn - is pinned as EXACT source lines (trimmed whole-line equality, exactly once in its member).
    ///
    /// Dat-free. The one PropertyManager key a test reads (class_abilities_enabled, in the Echo Cast rank gate) is
    /// seeded and restored in a finally. The one dat-backed test
    /// (RealBodies_EnterTheWorldBesideTheCaster_AtItsHeight) is tagged RequiresDatFiles and skips in CI.
    /// </summary>
    [TestClass]
    public class EchoWispControllerTests
    {
        private const string WispFile = "Source/ACE.Server/ClassAbilities/EchoWisp.cs";
        private const string PlayerWispFile = "Source/ACE.Server/WorldObjects/Player_EchoWisp.cs";
        private const string PlayerMagicFile = "Source/ACE.Server/WorldObjects/Player_Magic.cs";
        private const string MagicFile = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string ProjectileFile = "Source/ACE.Server/WorldObjects/SpellProjectile.cs";
        private const string PlayerFile = "Source/ACE.Server/WorldObjects/Player.cs";

        private static readonly string[] ProcFiles =
        {
            "Source/ACE.Server/ClassAbilities/Abilities/SpellbladeAbility.cs",
            "Source/ACE.Server/ClassAbilities/Abilities/RunebladeAbility.cs",
            "Source/ACE.Server/ClassAbilities/Abilities/SpellstormAbility.cs",
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            // before anything here touches a Player-declared member (its static initializer reads the world database)
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        // UnsafeAccessor, never FieldInfo.SetValue on a Player (its static ctor poisons the test process)
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "classAbilityCache")]
        private static extern ref Dictionary<ClassAbilityId, int> ClassAbilityCacheField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pvpBinding")]
        private static extern ref PvpPlayerBinding PvpBindingField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "echoWisps")]
        private static extern ref System.Collections.Concurrent.ConcurrentDictionary<uint, EchoWisp> EchoWispsField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref ACE.Entity.Models.Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Inventory>k__BackingField")]
        private static extern ref Dictionary<ObjectGuid, WorldObject> InventoryField(Container container);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<EquippedObjects>k__BackingField")]
        private static extern ref Dictionary<ObjectGuid, WorldObject> EquippedObjectsField(Creature creature);

        /// <summary>A bare Player with an empty biota, an Echo Cast rank in its ability cache and an empty wisp registry.</summary>
        private static Player SeededPlayer(int echoCastRank)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            BiotaField(player) = new ACE.Entity.Models.Biota();
            BiotaLockField(player) = new ReaderWriterLockSlim();
            ClassAbilityCacheField(player) = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.EchoCast] = echoCastRank };
            EchoWispsField(player) = new System.Collections.Concurrent.ConcurrentDictionary<uint, EchoWisp>();
            return player;
        }

        private static string Body(string file, string signature) =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read(file), signature);

        /// <summary>Asserts <paramref name="line"/> appears as a whole (trimmed) line exactly once in <paramref name="body"/>; returns its offset.</summary>
        private static int AssertLine(string body, string line, string why)
        {
            var hits = body.Split('\n').Count(l => l.Trim() == line);
            Assert.AreEqual(1, hits, $"expected exactly one line `{line}`: {why}");
            return body.IndexOf(line, StringComparison.Ordinal);
        }

        // ---- bodies by school ---------------------------------------------------------------------------

        [TestMethod]
        public void BodyWcidFor_MapsEverySchoolToItsColour()
        {
            Assert.AreEqual(1008600u, EchoWispMath.BodyWcidFor(MagicSchool.WarMagic), "War - red");
            Assert.AreEqual(1008601u, EchoWispMath.BodyWcidFor(MagicSchool.LifeMagic), "Life - blue");
            Assert.AreEqual(1008602u, EchoWispMath.BodyWcidFor(MagicSchool.VoidMagic), "Void - purple");
            Assert.AreEqual(1008603u, EchoWispMath.BodyWcidFor(MagicSchool.CreatureEnchantment), "Creature - green");
            Assert.AreEqual(1008604u, EchoWispMath.BodyWcidFor(MagicSchool.ItemEnchantment), "Item Enchantment - white (owner, 2026-11-22)");
        }

        [TestMethod]
        public void EveryBodyWcid_HasItsCommittedWeenie()
        {
            foreach (var school in new[] { MagicSchool.WarMagic, MagicSchool.LifeMagic, MagicSchool.VoidMagic, MagicSchool.CreatureEnchantment, MagicSchool.ItemEnchantment })
            {
                var wcid = EchoWispMath.BodyWcidFor(school);
                var sql = ReadWeenieSql(wcid);
                StringAssert.Contains(sql, $"VALUES ({wcid}, 'ace{wcid}-echowisp-", $"{school}: the weenie row");
                StringAssert.Contains(sql, $"({wcid},  19, False) /* Attackable */", $"{school}: never attackable");
                StringAssert.Contains(sql, $"({wcid},  13, True ) /* Ethereal */", $"{school}: ethereal");
            }
        }

        [TestMethod]
        public void TrySpawn_CreatesTheBodyOfTheSpellsSchool_AndPreparesItBeforeEnterWorld()
        {
            var body = Body(WispFile, "internal static EchoWisp TrySpawn(");

            var create = AssertLine(body, "var body = WorldObjectFactory.CreateNewWorldObject(EchoWispMath.BodyWcidFor(spell.School));",
                "the school's body wcid must be what the spawn creates");
            var prepare = AssertLine(body, "PrepareBody(body, at, durationSeconds, translucencyTunable, owner.CastHeight);",
                "the body is placed and its translucency, TimeToRot and cast height set");
            var enter = AssertLine(body, "entered = body.EnterWorld();", "the hazard spawn pattern: EnterWorld last");

            Assert.IsTrue(create < prepare && prepare < enter, "create, then prepare, then EnterWorld");

            AssertLine(body, "var at = ChooseSpawnPosition(physics.Position.Frame.Origin, physics.Position.Frame.Orientation, instance,",
                "the spawn point is chosen beside the caster's own physics position and heading");
        }

        // ---- qualifying casts ---------------------------------------------------------------------------

        [TestMethod]
        public void Qualifies_HarmfulAllowlistedSpellsAtANonPlayerCreature()
        {
            foreach (var type in new[] { SpellType.Projectile, SpellType.LifeProjectile, SpellType.EnchantmentProjectile,
                SpellType.Enchantment, SpellType.Boost, SpellType.Transfer, SpellType.Dispel })
            {
                Assert.IsTrue(EchoWispMath.Qualifies(type, isHarmful: true, isProjectile: false, hasTarget: true, targetIsCreature: true, targetIsPlayer: false, targetIsPet: false), type.ToString());
                Assert.IsFalse(EchoWispMath.Qualifies(type, isHarmful: false, isProjectile: false, hasTarget: true, targetIsCreature: true, targetIsPlayer: false, targetIsPet: false), $"{type}: never a beneficial spell");
            }

            foreach (var type in new[] { SpellType.PortalLink, SpellType.PortalRecall, SpellType.PortalSummon, SpellType.PortalSending,
                SpellType.FellowBoost, SpellType.FellowEnchantment, SpellType.FellowDispel, SpellType.FellowPortalSending, SpellType.Undef })
            {
                Assert.IsFalse(EchoWispMath.Qualifies(type, isHarmful: true, isProjectile: false, hasTarget: true, targetIsCreature: true, targetIsPlayer: false, targetIsPet: false),
                    $"{type}: IsHarmful is only !IsBeneficial, so the allowlist must refuse it");
            }
        }

        [TestMethod]
        public void Qualifies_NeverAtAPlayerOrAPet_AndUntargetedOnlyForAProjectile()
        {
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Projectile, true, true, hasTarget: true, targetIsCreature: true, targetIsPlayer: true, targetIsPet: false),
                "class abilities never reach PvP: a cast at a player never spawns a wisp");
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Projectile, true, true, hasTarget: true, targetIsCreature: true, targetIsPlayer: false, targetIsPet: true));
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Enchantment, true, false, hasTarget: true, targetIsCreature: false, targetIsPlayer: false, targetIsPet: false),
                "an item or door is not a creature");

            Assert.IsTrue(EchoWispMath.Qualifies(SpellType.Projectile, true, isProjectile: true, hasTarget: false, false, false, false), "a ring or wall");
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Enchantment, true, isProjectile: false, hasTarget: false, false, false, false));
        }

        [TestMethod]
        public void KindOf_SplitsDamageDebuffAndDispel()
        {
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.Projectile));
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.LifeProjectile));
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.Boost), "Harm");
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.Transfer), "drains");
            Assert.AreEqual(EchoWispSpellKind.Debuff, EchoWispMath.KindOf(SpellType.Enchantment));
            Assert.AreEqual(EchoWispSpellKind.Debuff, EchoWispMath.KindOf(SpellType.EnchantmentProjectile));
            Assert.AreEqual(EchoWispSpellKind.Dispel, EchoWispMath.KindOf(SpellType.Dispel));
        }

        // ---- tunables, defensively ----------------------------------------------------------------------

        [TestMethod]
        public void ClampIntervals_FloorsMinAtTheWatchTick_AndKeepsMaxAtLeastMin()
        {
            Assert.AreEqual((2.0, 3.0), EchoWispMath.ClampIntervals(2.0, 3.0), "defaults pass through");
            Assert.AreEqual((0.5, 0.5), EchoWispMath.ClampIntervals(0.0, 0.0), "a zero min would cast every tick");
            Assert.AreEqual((0.5, 2.0), EchoWispMath.ClampIntervals(-4.0, 2.0));
            Assert.AreEqual((3.0, 3.0), EchoWispMath.ClampIntervals(3.0, 1.0), "max below min reads as min");
            Assert.AreEqual((0.5, 0.5), EchoWispMath.ClampIntervals(double.NaN, double.NaN));
            Assert.AreEqual((2.0, 2.0), EchoWispMath.ClampIntervals(2.0, double.PositiveInfinity));
        }

        [TestMethod]
        public void Interval_IsUniformAcrossTheClampedPair()
        {
            Assert.AreEqual(2.0, EchoWispMath.Interval(2.0, 3.0, 0.0), 1e-12);
            Assert.AreEqual(3.0, EchoWispMath.Interval(2.0, 3.0, 1.0), 1e-12);
            Assert.AreEqual(2.5, EchoWispMath.Interval(2.0, 3.0, 0.5), 1e-12);
            Assert.AreEqual(3.0, EchoWispMath.Interval(2.0, 3.0, 7.0), 1e-12, "the unit draw is clamped");
            Assert.AreEqual(0.5, EchoWispMath.Interval(0.0, 0.0, 0.5), 1e-12, "the clamp applies to the draw too");
        }

        [TestMethod]
        public void MaxActive_IsClampedAtZero_AndTheCapIsStrict()
        {
            Assert.AreEqual(0, EchoWispMath.ClampMaxActive(-5));
            Assert.AreEqual(3, EchoWispMath.ClampMaxActive(3));
            Assert.AreEqual(int.MaxValue, EchoWispMath.ClampMaxActive(long.MaxValue));

            Assert.IsTrue(EchoWispMath.UnderCap(2, 3));
            Assert.IsFalse(EchoWispMath.UnderCap(3, 3), "a trigger at the cap spawns nothing (it does not replace the oldest)");
            Assert.IsFalse(EchoWispMath.UnderCap(0, -1), "a negative cap reads as 0: no wisps");
            Assert.IsFalse(EchoWispMath.UnderCap(0, 0));
        }

        [TestMethod]
        public void TryTriggerEchoWisp_ReadsTheCapThroughTheClamp_AgainstTheLiveCount()
        {
            var body = Body(PlayerWispFile, "internal EchoWisp TryTriggerEchoWisp(");

            AssertLine(body, "if (!EchoWispMath.UnderCap(echoWisps.Count, PropertyManager.GetLong(\"class_ability_echocast_wisp_max_active\").Item))",
                "the cap gate: the live wisp count against the clamped tunable");
            AssertLine(body, "if (EchoWispCast != null || spell == null)", "backstop: no trigger while a wisp cast is armed");
            AssertLine(body, "return EchoWisp.TrySpawn(this, spell, target as Creature, lifeProjectileDamage, inheritsProcCasterSkill, procDamageScale, rendSourceDamageType, procWeapon,",
                "the proc stamps reach the wisp");
            AssertLine(body, "if (!EchoWispTriggerQualifies(spell, ref target))", "the qualifying-cast gate");
            AssertLine(Body(PlayerWispFile, "internal bool EchoWispTriggerQualifies("),
                "return EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, spell.IsProjectile, target != null, target is Creature, target is Player, target is Pet,",
                "with the PvP exclusion (and the Restore arm, EchoWispRestoreTests)");
        }

        [TestMethod]
        public void Duration_AndTimeToRot_AreDurationPlusThirty()
        {
            Assert.AreEqual(39.0, EchoWispMath.TimeToRot(9.0), 1e-12, "plan ruling: TimeToRot = duration + 30 s");
            Assert.AreEqual(30.0, EchoWispMath.TimeToRot(-2.0), 1e-12);
            Assert.AreEqual(0.0, EchoWispMath.ClampDuration(double.NaN));
            Assert.AreEqual(0.0, EchoWispMath.ClampDuration(-1.0));
        }

        // ---- spawn: beside the caster, at its height, never on it -------------------------------------

        // the spawn order and geometry (eight spots, owner 2026-11-23): EchoWispRestoreTests.SpawnCandidates_AreRightLeftBehindFront_ThenTheFourDiagonals_All1Point5mAtTheCastersHeight

        [TestMethod]
        public void ChooseSpawnPosition_TakesTheFirstPointWithACell_CarriesCellHeadingAndInstance()
        {
            var origin = new Vector3(100f, 50f, 12.5f);
            var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
            var candidates = EchoWispMath.SpawnCandidates(origin, heading);

            // the right side is a wall; the left side resolves
            var asked = new List<Vector3>();
            var pos = EchoWisp.ChooseSpawnPosition(origin, heading, 7u, p =>
            {
                asked.Add(p);
                return asked.Count == 1 ? null : ((uint, Vector3)?)(0x01F501B3u, p);
            });

            Assert.IsNotNull(pos);
            Assert.AreEqual(2, asked.Count, "stops at the first resolved point");
            AssertNear(candidates[1], pos.Pos, "the left side");
            Assert.AreEqual(0x01F501B3u, pos.Cell);
            Assert.AreEqual(7u, pos.Instance);
            Assert.AreEqual(heading, pos.Rotation);
            Assert.AreNotEqual(origin, pos.Pos, "never the caster's own spot");

            Assert.IsNull(EchoWisp.ChooseSpawnPosition(origin, heading, 0u, p => null), "no cell beside the caster: no wisp, never a fallback onto the caster");
        }

        [TestMethod]
        public void PrepareBody_SetsWhatTheRuntimeReads()
        {
            var at = new Position(0x01F501B3u, new Vector3(10f, 20f, 30f), Quaternion.Identity) { Instance = 3 };

            var body = NewGeneric();
            EchoWisp.PrepareBody(body, at, durationSeconds: 9.0, translucencyTunable: 0.5, castHeight: 1.8f);

            Assert.AreSame(at, body.Location);
            Assert.AreEqual(0.5f, body.Translucency.Value, 1e-6f);
            Assert.AreEqual(39.0, body.TimeToRot.Value, 1e-9, "duration + 30, or it rots at 300 s");
            Assert.IsTrue(body.IsTransientSpawn);
            Assert.AreEqual(1.8f, body.ProjectileCastHeight.Value, "bolts leave the wisp at the caster's cast height");
            Assert.AreEqual(1.8f, body.CastHeight);

            var opaqueTunable = NewGeneric();
            EchoWisp.PrepareBody(opaqueTunable, at, 9.0, translucencyTunable: 1.0, castHeight: 1.0f);
            Assert.AreEqual(0.95f, opaqueTunable.Translucency.Value, 1e-6f, "a tunable of 1.0 is clamped: a wisp can never be fully invisible");

            var solid = NewGeneric();
            EchoWisp.PrepareBody(solid, at, 9.0, translucencyTunable: 0.0, castHeight: 1.0f);
            Assert.IsNull(solid.Translucency, "below the wire threshold the property is left unset (hazard precedent)");

            var nan = NewGeneric();
            EchoWisp.PrepareBody(nan, at, 9.0, translucencyTunable: double.NaN, castHeight: 1.0f);
            Assert.IsNull(nan.Translucency);
        }

        // ---- end conditions -----------------------------------------------------------------------------

        private static string End(double now = 1.0, bool body = true, bool owner = true, bool dead = false, bool portal = false,
            bool sameLb = true, bool sameInst = true, float dist = 5f, bool suppressed = false) =>
            EchoWispMath.EndReason(now, 9.0, body, owner, dead, portal, suppressed, sameLb, sameInst, dist);

        [TestMethod]
        public void EndReason_CoversEveryEnd_AndNullWhileItMayStay()
        {
            Assert.IsNull(End(), "a live wisp beside its caster stays");
            Assert.AreEqual("expire", End(now: 9.0));
            Assert.AreEqual("bodylost", End(body: false));
            Assert.AreEqual("ownerlost", End(owner: false), "logout or a destroyed caster");
            Assert.AreEqual("ownerdeath", End(dead: true));
            Assert.AreEqual("ownerportal", End(portal: true));
            Assert.AreEqual("suppressed", End(suppressed: true), "class abilities never reach PvP: a suppressed caster loses every wisp");
            Assert.AreEqual("ownerlost", End(owner: false, suppressed: true), "a caster who is gone is gone first");
            Assert.AreEqual("ownerlandblock", End(sameLb: false));
            Assert.AreEqual("leash", End(sameInst: false));
            Assert.AreEqual("leash", End(dist: 40.01f));
            Assert.IsNull(End(dist: 40f), "exactly 40 m is still in range");
            Assert.AreEqual("leash", End(dist: float.NaN));
            Assert.AreEqual("expire", End(now: 10.0, owner: false), "expiry is judged first");
        }

        [TestMethod]
        public void Watch_EndsOnTheBodyThread_AndQueuesEveryCastOnTheOwner()
        {
            var watch = Body(WispFile, "private void Watch(");

            AssertLine(watch, "var reason = CurrentEndReason();", "the tick ends the wisp when its time or its caster says so");
            AssertLine(watch, "End(reason, onBodyThread: true);", "the body thread destroys its own body");
            AssertLine(watch, "Owner.EnqueueAction(new ActionEventDelegate(CastOnOwnerThread));",
                "every wisp cast runs on the OWNER's action queue: the latch's check-then-set is not atomic");
            AssertLine(watch, "ScheduleWatch();", "self-rescheduling");

            Assert.IsFalse(watch.Contains("CastAsEchoWisp("), "the body thread must never cast itself");
        }

        [TestMethod]
        public void CastOnOwnerThread_RechecksTheEndConditionsBeforeItCasts()
        {
            var cast = Body(WispFile, "private void CastOnOwnerThread(");

            var recheck = AssertLine(cast, "var reason = CurrentEndReason();", "re-checked inside the owner's action");
            var end = AssertLine(cast, "End(reason, onBodyThread: false);", "an owner-thread end queues the body's destroy");
            var scope = AssertLine(cast, "var scope = new EchoWispCastScope(Body, LifeProjectileDamage, InheritsProcCasterSkill, ProcDamageScale, RendSourceDamageType, ProcWeapon);",
                "a fresh scope per cast with the wisp body as origin, the snapshotted basis and the proc-skill flag");
            var fire = AssertLine(cast, "var outcome = Owner.CastAsEchoWisp(scope, Spell, Targetless ? null : target);",
                "the cast, with that scope; a ring or wall launches untargeted");
            var done = AssertLine(cast, "if (EchoWispMath.IsDoneAfter(Kind, outcome))", "the stay-or-move-on rule reads the outcome");

            Assert.IsTrue(recheck < end && end < scope && scope < fire && fire < done);
        }

        [TestMethod]
        public void CastAsEchoWisp_IsCalledOnlyFromTheWispController()
        {
            var callers = ServerSources().Where(f => f.Text.Contains("CastAsEchoWisp(")).Select(f => f.Name).OrderBy(n => n).ToArray();

            CollectionAssert.AreEqual(new[] { "EchoWisp.cs", "Player_EchoWisp.cs" }, callers,
                "only the controller (from the owner's queue) calls CastAsEchoWisp; Player_EchoWisp.cs is its definition");

            var wisp = PooledLootSourceText.Read(WispFile);
            Assert.AreEqual(1, Regex.Matches(wisp, @"CastAsEchoWisp\(").Count, "one call site, in CastOnOwnerThread");
        }

        // ---- targeting and stay-or-move-on --------------------------------------------------------------

        private static bool Eligible(EchoWispSpellKind kind = EchoWispSpellKind.Damage, bool player = false, bool pet = false, bool dead = false,
            bool attackable = true, bool hidden = false, bool sameFaction = false, bool engaged = true, bool aggro = false, bool carries = false, bool done = false) =>
            EchoWispMath.IsEligible(kind, player, pet, dead, attackable, hidden, sameFaction, engaged, aggro, carries, done);

        [TestMethod]
        public void IsEligible_NeverAHiddenCreature_NorTheCastersOwnFactionUnlessItRetaliates()
        {
            Assert.IsFalse(Eligible(hidden: true), "Visibility == true is never a target (the IsPetTargetCandidate rule)");
            Assert.IsFalse(Eligible(hidden: true, aggro: true));
            Assert.IsFalse(Eligible(sameFaction: true), "same faction as the caster and not retaliating: the combat-pet rule");
            Assert.IsFalse(Eligible(sameFaction: true, aggro: true), "/summonaggro does not lift the faction rule");

            Assert.IsTrue(EchoWispMath.PassesCheapChecks(false, false, false, true, false, false));
            Assert.IsFalse(EchoWispMath.PassesCheapChecks(false, false, false, true, hidden: true, false));
            Assert.IsFalse(EchoWispMath.PassesCheapChecks(true, false, false, true, false, false));
            Assert.IsFalse(EchoWispMath.PassesCheapChecks(false, true, false, true, false, false));
        }

        [TestMethod]
        public void IsEligible_NeverAPlayerOrPet_AndAggressiveUnlessSummonAggro()
        {
            Assert.IsTrue(Eligible());
            Assert.IsFalse(Eligible(player: true), "a wisp never targets a player");
            Assert.IsFalse(Eligible(player: true, aggro: true), "not even with /summonaggro on");
            Assert.IsFalse(Eligible(pet: true));
            Assert.IsFalse(Eligible(dead: true));
            Assert.IsFalse(Eligible(attackable: false));
            Assert.IsFalse(Eligible(done: true));
            Assert.IsFalse(Eligible(engaged: false), "aggressive enemies only by default");
            Assert.IsTrue(Eligible(engaged: false, aggro: true), "any attackable creature with /summonaggro on");
        }

        [TestMethod]
        public void IsEligible_ADebuffMovesOnToEnemiesThatLackIt_ADamageSpellDoesNot()
        {
            Assert.IsFalse(Eligible(EchoWispSpellKind.Debuff, carries: true), "a debuff wisp moves off a target carrying it");
            Assert.IsTrue(Eligible(EchoWispSpellKind.Debuff, carries: false));
            Assert.IsTrue(Eligible(EchoWispSpellKind.Damage, carries: true), "a damage wisp stays until the target dies");
            Assert.IsTrue(Eligible(EchoWispSpellKind.Dispel, carries: true));
        }

        [TestMethod]
        public void IneligibleReason_IsNoneExactlyWhenIsEligible_OverTheWholeTruthTable()
        {
            var checkedCases = 0;

            foreach (EchoWispSpellKind kind in Enum.GetValues(typeof(EchoWispSpellKind)))
            {
                for (var bits = 0; bits < 1 << 10; bits++)
                {
                    bool B(int i) => (bits & (1 << i)) != 0;

                    var eligible = EchoWispMath.IsEligible(kind, B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7), B(8), B(9));
                    var reason = EchoWispMath.IneligibleReason(kind, B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7), B(8), B(9));

                    Assert.AreEqual(eligible, reason == EchoWispIneligible.None, $"{kind} bits {bits}: the reason form must agree with the rule ({reason})");
                    checkedCases++;
                }
            }

            Assert.AreEqual(4 * 1024, checkedCases, "Damage, Debuff, Dispel and Restore (owner, 2026-11-23)");

            // the reason named is the first rule that fails, in the controller's order
            Assert.AreEqual(EchoWispIneligible.NotInCastersFight, EchoWispMath.IneligibleReason(EchoWispSpellKind.Damage, false, false, false, true, false, false, false, false, false, false));
            Assert.AreEqual(EchoWispIneligible.Dead, EchoWispMath.IneligibleReason(EchoWispSpellKind.Damage, false, false, true, true, false, false, false, false, false, false));
            Assert.AreEqual(EchoWispIneligible.SameFaction, EchoWispMath.IneligibleReason(EchoWispSpellKind.Damage, false, false, false, true, false, true, false, false, false, false));
            Assert.AreEqual(EchoWispIneligible.CarriesDebuff, EchoWispMath.IneligibleReason(EchoWispSpellKind.Debuff, false, false, false, true, false, false, true, false, true, false));
        }

        [TestMethod]
        public void PickTally_DescribesTheHeldTargetAndEveryCount()
        {
            var tally = new EchoWispPickTally { Range = 75f, Candidates = 6, HadHeld = true, HeldIneligible = EchoWispIneligible.Dead, OutOfRange = 1, InRange = 3, SightTested = 2, SightFailed = 2 };
            tally.RecordIneligible(EchoWispIneligible.NotInCastersFight);
            tally.RecordIneligible(EchoWispIneligible.NotInCastersFight);
            tally.RecordIneligible(EchoWispIneligible.None);

            Assert.AreEqual(2, tally.IneligibleTotal, "None is not a reason");
            Assert.AreEqual("held: dropped (Dead); candidates 6: ineligible 2 (NotInCastersFight 2), out of range 1 (range 75.0 m), in range 3, sight failed 2 of 2 tested, 1 untested",
                tally.Describe());

            Assert.AreEqual("held: none; candidates 0: ineligible 0, out of range 0 (range 4.5 m), in range 0, sight failed 0 of 0 tested, 0 untested",
                new EchoWispPickTally { Range = 4.5f }.Describe());
            Assert.IsTrue(new EchoWispPickTally { HadHeld = true }.Describe().StartsWith("held: kept;"));
            Assert.IsTrue(new EchoWispPickTally { HadHeld = true, HeldOutOfRange = true }.Describe().StartsWith("held: dropped (out of range);"));
            Assert.IsTrue(new EchoWispPickTally { HadHeld = true, HeldSightFailed = true }.Describe().StartsWith("held: dropped (no sight);"));
        }

        /// <summary>
        /// Stage bug 2026-11-23: a wisp that never cast left only its "ended: expire" line, so the log could not say which
        /// step stopped. Every pass through CastOnOwnerThread past the end re-check now logs exactly one line: idle with the
        /// pick tally, or the cast with its outcome, refusal reason and projectile counts.
        /// </summary>
        [TestMethod]
        public void CastOnOwnerThread_LogsEveryOutcome_OnceEach()
        {
            var cast = Body(WispFile, "private void CastOnOwnerThread(");

            AssertLine(cast, "var target = Kind == EchoWispSpellKind.Restore ? PickRestoreTarget(out restoreIdle) : PickTarget(tally);", "the pick fills the tally (a Restore pick its idle reason)");
            AssertLine(cast, "log.Debug($\"[ECHO WISP] {Owner.Name}'s wisp ({Spell.Name}) idle, no target: {(tally != null ? tally.Describe() : restoreIdle)}\");", "no target: why");
            AssertLine(cast, "log.Debug($\"[ECHO WISP] {Owner.Name}'s wisp ({Spell.Name}) cast at {target.Name} 0x{target.Guid.Full:X8}: {outcome}\"", "a cast: at whom and what happened");
            AssertLine(cast, "+ (scope.RefusedBecause != null ? $\" ({scope.RefusedBecause})\" : \"\")", "with the refusal reason");
            AssertLine(cast, "+ $\", projectiles {scope.ProjectilesLaunched} launched {scope.ProjectilesBlocked} blocked; \"", "and what flew");
            AssertLine(cast, "+ (tally != null ? tally.Describe() : $\"restore {RestoreVital} at {VitalFractionOf(target, RestoreVital):0%} before the cast\"));", "and the pick behind it");
            Assert.AreEqual(2, Regex.Matches(cast, @"log\.Debug\(").Count, "one line per outcome, so at most one per cast");

            // every refusal names its reason
            var castAs = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");
            Assert.AreEqual(0, Regex.Matches(castAs, @"return EchoWispCastOutcome\.Refused;").Count, "no bare refusal: each goes through Refuse(scope, reason)");
            Assert.AreEqual(6, Regex.Matches(castAs, @"return Refuse\(scope, ""[^""]+""\);").Count, "six pre-cast refusals with a fixed reason");
            AssertLine(castAs, "return Refuse(scope, targetRefusal);", "and the target guard's, whose reason EchoWispMath.CastTargetRefusal names");
            AssertLine(castAs, "scope.RefusedBecause = \"HandleCastSpell refused (no live target, Threads perimeter, or an unhandled spell type)\";", "and the handler's");

            var launch = Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles(");
            AssertLine(launch, "echoWispCast.ProjectilesLaunched++;", "projectiles that entered the world");
            AssertLine(launch, "echoWispCast.ProjectilesBlocked++;", "and those culled for no sight from the wisp");
        }

        [TestMethod]
        public void CarriesDebuff_IsAnEqualOrStrongerEntryInTheCategory()
        {
            Assert.IsFalse(EchoWispMath.CarriesDebuff(new uint[0], 300));
            Assert.IsFalse(EchoWispMath.CarriesDebuff(null, 300));
            Assert.IsFalse(EchoWispMath.CarriesDebuff(new uint[] { 250, 299 }, 300), "a weaker one would be surpassed");
            Assert.IsTrue(EchoWispMath.CarriesDebuff(new uint[] { 250, 300 }, 300), "an equal one holds it");
            Assert.IsTrue(EchoWispMath.CarriesDebuff(new uint[] { 400 }, 300));
        }

        [TestMethod]
        public void IsDoneAfter_ImmuneRefusedAndALandedDispel_ButNotAResist()
        {
            foreach (var kind in new[] { EchoWispSpellKind.Damage, EchoWispSpellKind.Debuff, EchoWispSpellKind.Dispel })
            {
                Assert.IsTrue(EchoWispMath.IsDoneAfter(kind, EchoWispCastOutcome.Immune), $"{kind}: an immune target counts as done");
                Assert.IsTrue(EchoWispMath.IsDoneAfter(kind, EchoWispCastOutcome.Refused), $"{kind}");
                Assert.IsFalse(EchoWispMath.IsDoneAfter(kind, EchoWispCastOutcome.Resisted), $"{kind}: a resisted cast tries again");
            }

            Assert.IsTrue(EchoWispMath.IsDoneAfter(EchoWispSpellKind.Dispel, EchoWispCastOutcome.Cast), "one successful dispel per target");
            Assert.IsFalse(EchoWispMath.IsDoneAfter(EchoWispSpellKind.Damage, EchoWispCastOutcome.Cast), "damage stays until death");
            Assert.IsFalse(EchoWispMath.IsDoneAfter(EchoWispSpellKind.Debuff, EchoWispCastOutcome.Cast), "a debuff is judged by CarriesDebuff on the next pick");
        }

        [TestMethod]
        public void PickTarget_RunsTheRulesFromTheWisp()
        {
            var pick = Body(WispFile, "private Creature PickTarget(");
            AssertLine(pick, "var range = EchoWispMath.CastRange(Player.MaxSpellRange(Spell.BaseRangeConstant, Spell.BaseRangeMod, skill.InitLevel + skill.Ranks));",
                "the player's own range formula");
            AssertLine(pick, "var distance = creature.Location == null || body.Location == null ? float.NaN : body.Location.Distance2D(creature.Location);",
                "range measured from the wisp, not the caster");
            AssertLine(pick, "var visible = body.IsDirectVisible(creature);", "line of sight from the wisp");
            AssertLine(pick, "var verdict = LegalTargetVerdict(creature, summonAggro, isStartingOrHeldTarget: held);", "the legality rule");
            AssertLine(pick, "return verdict == EchoWispIneligible.None;", "eligible exactly when no reason turns it away");
            AssertLine(pick, "var candidates = owner.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeNonPlayerCreature();", "never a player candidate");

            var legal = Body(WispFile, "private EchoWispIneligible LegalTargetVerdict(");
            AssertLine(legal, "return EchoWispMath.IneligibleReason(Kind, isPlayer, isPet, isDead, attackable, hidden, sameFactionNotRetaliating, engaged, summonAggro, carries, isDone);",
                "the eligibility rule the runtime applies");
            AssertLine(legal, "var engaged = isStartingOrHeldTarget || summonAggro || EchoWispEngagement.IsEngaged(creature, Owner);",
                "the starting target is the original cast's, engaged or not; the wisp's own widened rule (owner, 2026-11-23); the read is skipped when it cannot matter");
        }

        [TestMethod]
        public void LegalTargetVerdict_DerivesEachInputFromTheCreature_CheapChecksFirst()
        {
            var legal = Body(WispFile, "private EchoWispIneligible LegalTargetVerdict(");

            AssertLine(legal, "var isPlayer = creature is Player;", "a player is never a wisp target");
            AssertLine(legal, "var isPet = creature is Pet;", "nor a pet (CombatPet derives from Pet)");
            AssertLine(legal, "var hidden = creature.Visibility;", "a hidden creature is never a target");
            var cheap = AssertLine(legal, "if (!EchoWispMath.PassesCheapChecks(isPlayer, isPet, isDead, attackable, hidden, isDone))",
                "the cheap rejections first");
            var teleporting = AssertLine(legal, "if (creature.Teleporting)", "then a creature mid-portal");
            var faction = AssertLine(legal, "var sameFactionNotRetaliating = Owner.SameFaction(creature) && !(creature.PhysicsObj != null && creature.HasRetaliateTarget(Owner));",
                "the combat-pet faction rule, against the caster");
            var engaged = legal.IndexOf("EchoWispEngagement.IsEngaged(", StringComparison.Ordinal);
            var debuffOnly = AssertLine(legal, "if (Kind == EchoWispSpellKind.Debuff)", "the enchantment read only for a debuff");
            var holder = AssertLine(legal, "var holder = DebuffHolder(creature);", "judged on what the cast lands on");
            var registry = legal.IndexOf("EnchantmentManager.GetEnchantments(", StringComparison.Ordinal);

            Assert.IsTrue(cheap < teleporting && teleporting < faction && faction < engaged && engaged < debuffOnly && debuffOnly < holder && holder < registry,
                "cheap checks, then faction, then engagement, then (debuff only) the enchantment read");

            StringAssert.Contains(PooledLootSourceText.ExpressionBody(PooledLootSourceText.Read(WispFile), "private WorldObject DebuffHolder("),
                "Spell.School == MagicSchool.ItemEnchantment ? WorldObject.ItemEnchantmentLandingItem(Spell, creature) : creature",
                "an Item Enchantment debuff is judged on its redirect item");
        }

        // ---- Item Enchantment: one landing rule for the real cast and the wisp -------------------------

        private static Creature NewCreature(params WorldObject[] equipped)
        {
            TestGameTables.EnsureInitialized();
            var creature = new Creature(new Weenie { WeenieClassId = 999951, ClassName = "echowisptestcreature", WeenieType = WeenieType.Creature }, new ObjectGuid(nextGuid++));
            EquippedObjectsField(creature) = equipped.ToDictionary(i => i.Guid, i => i);
            return creature;
        }

        private static WorldObject NewEquipped(EquipMask slot, CombatUse? combatUse, bool enchantable = true)
        {
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.CurrentWieldedLocation, (int)slot } };
            if (slot == EquipMask.MeleeWeapon)
                ints[PropertyInt.ParentLocation] = (int)ParentLocation.RightHand;
            if (combatUse.HasValue)
                ints[PropertyInt.CombatUse] = (int)combatUse.Value;
            if (!enchantable)
                ints[PropertyInt.ResistMagic] = 9999;

            var weenieType = slot == EquipMask.MeleeWeapon ? WeenieType.MeleeWeapon : WeenieType.Generic;
            return new GenericObject(new Weenie { WeenieClassId = 999952, ClassName = "echowisptestitem", WeenieType = weenieType, PropertiesInt = ints }, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void ItemEnchantmentLandingItem_ImpenBaneLandsOnTheShield_OrNothing()
        {
            var shield = NewEquipped(EquipMask.Shield, CombatUse.Shield);
            var armed = NewCreature(shield);

            Assert.AreSame(shield, WorldObject.ItemEnchantmentLandingItem(impenBaneType: true, weaponRedirectType: false, ItemType.None, armed), "shield present");
            Assert.IsNull(WorldObject.ItemEnchantmentLandingItem(true, false, ItemType.None, NewCreature()), "no shield: the cast fails to affect it");
            Assert.IsNull(WorldObject.ItemEnchantmentLandingItem(true, false, ItemType.None, NewCreature(NewEquipped(EquipMask.Shield, CombatUse.Shield, enchantable: false))),
                "an unenchantable shield is not a landing");
        }

        [TestMethod]
        public void ItemEnchantmentLandingItem_WeaponRedirectLandsOnTheNamedWeapon_AndOtherSpellsOnTheCreature()
        {
            var sword = NewEquipped(EquipMask.MeleeWeapon, null);
            var armed = NewCreature(sword);

            Assert.AreSame(sword, WorldObject.ItemEnchantmentLandingItem(false, weaponRedirectType: true, ItemType.MeleeWeapon, armed), "weapon redirect");
            Assert.IsNull(WorldObject.ItemEnchantmentLandingItem(false, true, ItemType.MeleeWeapon, NewCreature()), "no weapon");
            Assert.IsNull(WorldObject.ItemEnchantmentLandingItem(false, true, ItemType.MeleeWeapon, NewCreature(NewEquipped(EquipMask.MeleeWeapon, null, enchantable: false))), "an unenchantable weapon is nothing to land on");
            Assert.IsNull(WorldObject.ItemEnchantmentLandingItem(false, true, ItemType.None, armed), "a target type that names no slot");
            Assert.AreSame(armed, WorldObject.ItemEnchantmentLandingItem(false, false, ItemType.None, armed), "any other item spell lands on the creature itself");
            Assert.IsNull(WorldObject.ItemEnchantmentLandingItem(true, false, ItemType.None, null));
        }

        [TestMethod]
        public void ItemEnchantment_TheRealCastAndTheWispShareTheLandingRule()
        {
            var real = Body(MagicFile, "public void TryCastItemEnchantment_WithRedirects(");
            AssertLine(real, "var item = ItemEnchantmentLandingItem(spell, targetCreature);", "the shield redirect of a real cast");
            AssertLine(real, "var weapon = ItemEnchantmentLandingItem(spell, targetCreature);", "the weapon redirect of a real cast");
            Assert.IsFalse(real.Contains("IsShield && i.IsEnchantable"), "the shield rule lives only in the shared helper");
            Assert.IsFalse(real.Contains("GetEquippedMeleeWeapon("), "the weapon rule lives only in the shared helper");

            var cast = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");
            var none = AssertLine(cast, "if (ItemEnchantmentLandingItem(spell, target) == null)", "nothing to land on");
            var lines = cast.Substring(none).Split('\n').Select(l => l.Trim()).ToArray();
            Assert.AreEqual("outcome = EchoWispCastOutcome.Immune;", lines[1], "is reported Immune, so the wisp marks the target done");
            Assert.IsTrue(none < cast.IndexOf("TryCastItemEnchantment_WithRedirects(", StringComparison.Ordinal), "checked before the cast");
        }

        // ---- the Echo Cast rank gate (F1) ----------------------------------------------------------------

        [TestMethod]
        public void TryGetEchoCastRank_HonoursTheSwitchAndTheSuppression()
        {
            var previous = PropertyManager.GetBool("class_abilities_enabled").Item;

            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("class_abilities_enabled", true));

                var player = SeededPlayer(3);
                Assert.IsTrue(player.TryGetEchoCastRank(out var rank), "control: a rank-3 Archmage holds Echo Cast");
                Assert.AreEqual(3, rank);

                Assert.IsFalse(SeededPlayer(0).TryGetEchoCastRank(out rank), "not learned");

                var suppressed = SeededPlayer(3);
                PvpBindingField(suppressed) = SummonerFocusTests.LiveBinding();
                Assert.IsTrue(suppressed.ClassAbilitySuppressed, "harness: the arena mask is active");
                Assert.IsFalse(suppressed.TryGetEchoCastRank(out rank), "suppressed (arena mask / PK facet): no wisp");
                Assert.AreEqual(0, rank);

                Assert.IsTrue(PropertyManager.ModifyBool("class_abilities_enabled", false));
                Assert.IsFalse(SeededPlayer(3).TryGetEchoCastRank(out rank), "class_abilities_enabled off: no wisp");
                Assert.AreEqual(0, rank);
            }
            finally
            {
                PropertyManager.ModifyBool("class_abilities_enabled", previous);
            }
        }

        [TestMethod]
        public void TryTriggerEchoWisp_ReadsTheSuppressedRank()
        {
            var body = Body(PlayerWispFile, "internal EchoWisp TryTriggerEchoWisp(");
            AssertLine(body, "if (!TryGetEchoCastRank(out var rank))", "the trigger reads the suppressed rank");
            Assert.IsFalse(body.Contains("GetClassAbilityRank("), "never the raw rank, which ignores the switch and PvP suppression");

            StringAssert.Contains(PooledLootSourceText.ExpressionBody(PooledLootSourceText.Read(PlayerWispFile), "internal bool TryGetEchoCastRank("),
                "TryGetClassAbility(ClassAbilityId.EchoCast, out rank)", "the shared, suppressed gate");
        }

        // ---- the wisp registry (F4, F6) -------------------------------------------------------------------

        [TestMethod]
        public void AddAndRemoveEchoWisp_TrackTheLiveCount_AndEndFreesTheSlot()
        {
            var owner = SeededPlayer(3);
            var first = new EchoWisp(owner, NewGeneric(), null, null, 0, false, 1.0f, null, null, 0.0, 9.0, 2.0, 3.0, 0.5);
            var second = new EchoWisp(owner, NewGeneric(), null, null, 0, false, 1.0f, null, null, 0.0, 9.0, 2.0, 3.0, 0.5);

            Assert.AreEqual(0, owner.ActiveEchoWispCount);
            owner.AddEchoWisp(first);
            owner.AddEchoWisp(second);
            Assert.AreEqual(2, owner.ActiveEchoWispCount);

            owner.RemoveEchoWisp(first);
            Assert.AreEqual(1, owner.ActiveEchoWispCount);
            owner.RemoveEchoWisp(first);
            Assert.AreEqual(1, owner.ActiveEchoWispCount, "removing twice is harmless");

            second.End("expire", onBodyThread: true);
            Assert.AreEqual(0, owner.ActiveEchoWispCount, "End frees the cap slot");
            Assert.IsTrue(second.IsEnded);
            Assert.AreEqual("expire", second.EndedReason);

            second.End("bodylost", onBodyThread: true);
            Assert.AreEqual("expire", second.EndedReason, "End runs once");
        }

        [TestMethod]
        public void End_UnlinksFromTheOwner_AndADestroyedBodyStillEndsTheWisp()
        {
            AssertLine(Body(WispFile, "internal void End("), "Owner?.RemoveEchoWisp(this);", "End must free the cap slot");

            var watch = Body(WispFile, "private void Watch(");
            AssertLine(watch, "End(\"bodylost\", onBodyThread: true);", "a body destroyed under the wisp ends it (no leaked cap slot)");

            var count = Regex.Matches(PooledLootSourceText.Read(PlayerWispFile), @"TryTriggerEchoWisp\(").Count;
            Assert.AreEqual(1, count, "Player_EchoWisp.cs holds only the definition of TryTriggerEchoWisp, never a call");
        }

        // ---- a proc wisp launches with the proc's weapon (ruling on #2301) -----------------------------------

        [TestMethod]
        public void Launcher_IsTheProcWeaponWhileOwned_ElseTheHandCastLauncher()
        {
            var sword = NewGeneric();
            var wand = NewGeneric();

            Assert.AreSame(sword, EchoWispMath.Launcher(sword, procWeaponStillOwned: true, wand), "a proc wisp launches with the proc's weapon");
            Assert.AreSame(wand, EchoWispMath.Launcher(sword, procWeaponStillOwned: false, wand), "a weapon that left the owner falls back (Hunter's Echo)");
            Assert.AreSame(wand, EchoWispMath.Launcher<WorldObject>(null, true, wand), "a hand-cast wisp keeps the wand");
            Assert.IsNull(EchoWispMath.Launcher<WorldObject>(null, false, null), "a hand-cast wisp with no wand launches with none, as today");
        }

        [TestMethod]
        public void EchoWispLauncher_ChecksTheOwnerStillHoldsTheProcWeapon()
        {
            var sword = NewEquipped(EquipMask.MeleeWeapon, null);
            var wand = NewGeneric();

            var holding = SeededPlayer(3);
            EquippedObjectsField(holding) = new Dictionary<ObjectGuid, WorldObject> { [sword.Guid] = sword };
            InventoryField(holding) = new Dictionary<ObjectGuid, WorldObject>();
            Assert.AreSame(sword, holding.EchoWispLauncher(sword, wand), "wielded: the proc's sword");

            var packed = SeededPlayer(3);
            EquippedObjectsField(packed) = new Dictionary<ObjectGuid, WorldObject>();
            InventoryField(packed) = new Dictionary<ObjectGuid, WorldObject> { [sword.Guid] = sword };
            Assert.AreSame(sword, packed.EchoWispLauncher(sword, wand), "swapped into a pack: still the owner's");

            var dropped = SeededPlayer(3);
            EquippedObjectsField(dropped) = new Dictionary<ObjectGuid, WorldObject>();
            InventoryField(dropped) = new Dictionary<ObjectGuid, WorldObject>();
            Assert.AreSame(wand, dropped.EchoWispLauncher(sword, wand), "dropped, traded or destroyed: the hand-cast launcher");

            Assert.AreSame(wand, dropped.EchoWispLauncher(null, wand), "a hand-cast wisp");
        }

        [TestMethod]
        public void TheProcWeaponReachesLaunchSpellProjectilesAsTheLauncher()
        {
            // proc site -> trigger -> spawn -> wisp -> every cast's scope (pinned in ProcSites_... and ProcStamps_...)
            AssertLine(Body(WispFile, "internal static EchoWisp TrySpawn("),
                "var wisp = new EchoWisp(owner, body, spell, target, lifeProjectileDamage, inheritsProcCasterSkill, procDamageScale, rendSourceDamageType, procWeapon,",
                "the spawn keeps the proc weapon");

            // scope -> CastAsEchoWisp's launcher -> HandleCastSpell's weapon
            var cast = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");
            var resolve = AssertLine(cast, "var launcher = EchoWispLauncher(scope.ProcWeapon, caster);", "the proc weapon while owned, else the wand");
            var handle = AssertLine(cast, "if (outcome == EchoWispCastOutcome.Cast && !HandleCastSpell(spell, target, null, launcher, false))", "handed on as HandleCastSpell's weapon");
            Assert.IsTrue(resolve < handle);

            // HandleCastSpell's weapon -> HandleCastSpell_Projectile -> CreateSpellProjectiles -> ProjectileLauncher
            AssertLine(Body(MagicFile, "protected bool HandleCastSpell("), "HandleCastSpell_Projectile(spell, targetCreature, itemCaster, weapon, isWeaponSpell, fromProc);", "the projectile path");
            AssertLine(Body(MagicFile, "private void HandleCastSpell_Projectile("), "CreateSpellProjectiles(spell, target, weapon, isWeaponSpell, fromProc, echoWispCast.LifeProjectileDamage);", "the wisp branch");
            AssertLine(Body(MagicFile, "public List<SpellProjectile> CreateSpellProjectiles("), "return LaunchSpellProjectiles(spell, target, spellType, weapon, isWeaponSpell, fromProc, origins, velocity, lifeProjectileDamage, suppressPetAssist);", "on to the launch");
            AssertLine(Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles("), "sp.ProjectileLauncher = weapon;", "the projectile's launcher");
        }

        // ---- a proc wisp repeats its proc (F5) ------------------------------------------------------------

        [TestMethod]
        public void ResolveProcStamps_AProcWispCarriesItsProcsScaleAndRendSource()
        {
            // a Nether Spellblade at a full bar: parity 1.0 x PowerProcScale(1.0, 0.5) = 1.5, Volatile Elements re-typed from Fire
            var procScale = 1.0f * SpellswordSpellTables.PowerProcScale(1.0f, 0.5);
            Assert.AreEqual(1.5f, procScale, 1e-6f, "golden: the proc's own scale");

            var procWisp = new EchoWispCastScope(null, 0, true, procScale, DamageType.Fire);
            Assert.AreEqual((1.5f, (DamageType?)DamageType.Fire), WorldObject.ResolveProcStamps(procWisp, 1.0f, null),
                "a proc wisp stamps its proc's scale and rend source, whatever the live latch holds");

            var plainWisp = new EchoWispCastScope(null, 0, false);
            Assert.AreEqual((1.0f, (DamageType?)null), WorldObject.ResolveProcStamps(plainWisp, 2.0f, DamageType.Cold), "an ordinary wisp is unscaled");

            Assert.AreEqual((2.0f, (DamageType?)DamageType.Cold), WorldObject.ResolveProcStamps(null, 2.0f, DamageType.Cold), "an ordinary cast takes the live proc latch");
        }

        [TestMethod]
        public void ProcStamps_ReachTheScope_AndTheLaunch()
        {
            var launch = Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles(");
            AssertLine(launch, "var procStamps = ResolveProcStamps(echoWispCast, (this as Player)?.ClassAbilityProcDamageScale ?? 1.0f, (this as Player)?.ClassAbilityProcRendSourceDamageType);",
                "the launch resolves the proc stamps from the wisp scope");
            AssertLine(launch, "sp.ProcDamageScale = procStamps.Scale;", "stamped on the projectile");
            AssertLine(launch, "sp.RendSourceDamageType = procStamps.RendSource;", "stamped on the projectile");

            AssertLine(Body(WispFile, "private void CastOnOwnerThread("),
                "var scope = new EchoWispCastScope(Body, LifeProjectileDamage, InheritsProcCasterSkill, ProcDamageScale, RendSourceDamageType, ProcWeapon);",
                "every cast's scope carries the proc stamps");
        }

        [TestMethod]
        public void MaxSpellRange_IsThePlayerFormula_GoldenValues()
        {
            // BaseRangeConstant + skill x BaseRangeMod, capped at the 75 m outdoor radar range
            Assert.AreEqual(20.0f, Player.MaxSpellRange(5f, 0.05f, 300), 1e-4f);
            Assert.AreEqual(75.0f, Player.MaxSpellRange(5f, 0.25f, 600), 1e-4f, "capped");
            Assert.AreEqual(4.5f, Player.MaxSpellRange(4.5f, 0f, 600), 1e-4f, "a ring: constant only");
            Assert.AreEqual(0f, Player.MaxSpellRange(0f, 0f, 600), 1e-4f);

            Assert.AreEqual(4.5f, EchoWispMath.CastRange(0f), "no range data: the caster-pet 4.5 m reach");
            Assert.AreEqual(20f, EchoWispMath.CastRange(20f));

            AssertLine(Body(PlayerMagicFile, "public bool VerifySpellRange("), "var maxRange = MaxSpellRange(spell.BaseRangeConstant, spell.BaseRangeMod, magicSkill);",
                "the hand cast checks the same helper");
        }

        [TestMethod]
        public void FacingToward_IsTheLaunchYaw()
        {
            var east = EchoWispMath.FacingToward(Vector3.Zero, new Vector3(5f, 0f, 3f)).Value;
            AssertNear(Vector3.UnitX, Vector3.Transform(Vector3.UnitY, east), "facing +X");

            var north = EchoWispMath.FacingToward(Vector3.Zero, new Vector3(0f, 2f, 0f)).Value;
            AssertNear(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, north), "facing +Y");

            Assert.IsNull(EchoWispMath.FacingToward(Vector3.One, Vector3.One + Vector3.UnitZ), "straight above: no heading");
        }

        // ---- triggers: hand cast and the three procs, never from an impact -------------------------------

        [TestMethod]
        public void BasisNote_FirstWriteWins_InsideAnArmedWindowOnly()
        {
            var note = new EchoWispBasisNote();

            note.Note(99);
            note.Arm();
            Assert.AreEqual(0u, note.Take(), "a note outside an armed window is ignored");

            note.Arm();
            note.Note(420);
            note.Note(7);
            Assert.AreEqual(420u, note.Take(), "first write wins: a proc cast inside the same call cannot overwrite it");
            Assert.AreEqual(0u, note.Take(), "Take disarms and clears");

            note.Note(5);
            Assert.AreEqual(0u, note.Take());
        }

        [TestMethod]
        public void HandCast_ArmsTheNote_CastsThenTriggers()
        {
            var inner = Body(PlayerMagicFile, "public void DoCastSpell_Inner(");

            var arm = AssertLine(inner, "EchoWispBasis.Arm();", "commit-armed");
            var cast = inner.IndexOf("CreatePlayerSpell(target, spell, isWeaponSpell);", arm, StringComparison.Ordinal);
            var trigger = AssertLine(inner, "TryTriggerEchoWisp(spell, target, EchoWispBasis.Take(), inheritsProcCasterSkill: false);",
                "the hand cast rolls once, after the cast, with the noted basis");

            Assert.IsTrue(arm < cast && cast < trigger, "arm, cast, trigger");

            var projectile = Body(MagicFile, "private void HandleCastSpell_Projectile(");
            var note = AssertLine(projectile, "(this as Player)?.EchoWispBasis.Note(damage);", "the hand cast's drained basis");
            var launch = AssertLine(projectile, "CreateSpellProjectiles(spell, target, weapon, isWeaponSpell, fromProc, damage);", "the hand cast's launch");
            var wispBranch = AssertLine(projectile, "CreateSpellProjectiles(spell, target, weapon, isWeaponSpell, fromProc, echoWispCast.LifeProjectileDamage);",
                "a wisp cast returns before the note");
            Assert.IsTrue(wispBranch < note && note < launch);
        }

        [TestMethod]
        public void ProcSites_CarryTheIdenticalTrigger_AfterTheProcCast_BeforeSpellsurge()
        {
            const string trigger = "attacker.TryTriggerEchoWisp(spell, target, 0, inheritsProcCasterSkill: true, procDamageScale: procDamageScale, rendSourceDamageType: damageEvent.OriginalDamageType, procWeapon: damageEvent.Weapon);";

            foreach (var file in ProcFiles)
            {
                var body = Body(file, "public void ModifyOutgoingDamage(");

                var proc = body.IndexOf("attacker.CastClassAbilityProc(", StringComparison.Ordinal);
                var at = AssertLine(body, trigger, file);
                var surge = body.IndexOf("attacker.AddSpellsurgeStack();", StringComparison.Ordinal);

                Assert.IsTrue(proc >= 0 && proc < at && at < surge, $"{file}: directly after CastClassAbilityProc and before AddSpellsurgeStack");
            }
        }

        [TestMethod]
        public void TryTriggerEchoWisp_IsUnreachableFromAProjectileImpact()
        {
            // the trigger's only callers: the hand-cast commit and the three melee-triggered Spellsword procs
            var callers = ServerSources().Where(f => f.Text.Contains("TryTriggerEchoWisp(")).ToDictionary(f => f.Name, f => Regex.Matches(f.Text, @"TryTriggerEchoWisp\(").Count);

            CollectionAssert.AreEquivalent(new[] { "Player_EchoWisp.cs", "Player_Magic.cs", "SpellbladeAbility.cs", "RunebladeAbility.cs", "SpellstormAbility.cs" },
                callers.Keys.ToArray());
            foreach (var name in new[] { "Player_Magic.cs", "SpellbladeAbility.cs", "RunebladeAbility.cs", "SpellstormAbility.cs" })
                Assert.AreEqual(1, callers[name], name);

            // the Spellsword procs are IOutgoingDamageAbility, dispatched only from a melee/missile Player.DamageTarget;
            // a spell projectile never reaches that dispatch, nor a hand cast
            var projectile = PooledLootSourceText.Read(ProjectileFile);
            Assert.IsFalse(projectile.Contains("ApplyOutgoingDamageClassAbilities("), "a projectile impact must not reach the proc dispatch");
            Assert.IsFalse(projectile.Contains("DoCastSpell_Inner("), "a projectile impact must not reach the hand-cast commit");

            // and a wisp's own cast path passes neither site
            var cast = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");
            foreach (var forbidden in new[] { "TryTriggerEchoWisp(", "DoCastSpell_Inner(", "CreatePlayerSpell(", "CastClassAbilityProc(" })
                Assert.IsFalse(cast.Contains(forbidden), $"CastAsEchoWisp must not call {forbidden}");
        }

        [TestMethod]
        public void Logout_EndsEveryWisp()
        {
            var player = PooledLootSourceText.Read(PlayerFile);
            var echo = player.IndexOf("EndHuntersEcho(\"ownerlogout\");", StringComparison.Ordinal);
            var wisps = player.IndexOf("EndAllEchoWisps(\"ownerlogout\");", StringComparison.Ordinal);
            Assert.IsTrue(echo >= 0 && wisps > echo, "beside EndHuntersEcho on logout");
        }

        // ---- the dat-backed spawn ---------------------------------------------------------------------------

        private const uint MarketCell = 0x01F501B3;                  // ExactPlacementTests' Marketplace floor cell
        private static readonly Vector3 CasterOrigin = new Vector3(16.75f, -72.45f, -17.995f);

        /// <summary>
        /// The real wisp bodies (Setup, PhysicsState and bool rows read from their committed SQL) go through the real
        /// spawn path - ChooseSpawnPosition with a resolver over the dungeon's AdjustCell, then
        /// WorldObject.InitPhysicsObj and AddPhysicsObj (enter_world with SpawnAtExactPosition) - and land exactly at the
        /// chosen point: 1.5 m from the caster's spot, at the caster's height.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void RealBodies_EnterTheWorldBesideTheCaster_AtItsHeight()
        {
            var skip = DatHarness.Require();
            if (skip != null)
                Assert.Inconclusive(skip);

            try
            {
                foreach (var wcid in new[] { 1008600u, 1008601u, 1008602u, 1008603u, 1008604u })
                {
                    var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
                    var adjust = ACE.Server.Physics.Util.AdjustCell.Get(MarketCell >> 16, 0);

                    var at = EchoWisp.ChooseSpawnPosition(CasterOrigin, heading, 0u, p =>
                    {
                        var cell = adjust.GetCell(p);
                        return cell.HasValue ? ((uint, Vector3)?)(cell.Value, p) : null;
                    });

                    Assert.IsNotNull(at, $"{wcid}: some side of the caster has a cell");

                    var body = LoadBody(wcid);
                    EchoWisp.PrepareBody(body, at, 9.0, 0.5, 1.8f);

                    var placed = DatHarness.Place(body);

                    try
                    {
                        Assert.IsTrue(placed, $"{wcid}: the body entered the world");

                        var final = body.PhysicsObj.Position.Frame.Origin;
                        AssertNear(at.Pos, final, $"{wcid}: exactly where it was put (SpawnAtExactPosition)");
                        Assert.AreEqual(CasterOrigin.Z, final.Z, 1e-3f, $"{wcid}: at the caster's height");

                        var horizontal = new Vector2(final.X - CasterOrigin.X, final.Y - CasterOrigin.Y).Length();
                        Assert.AreEqual(1.5f, horizontal, 1e-3f, $"{wcid}: beside the caster, never on it");
                    }
                    finally
                    {
                        DatHarness.Remove(body);
                    }
                }
            }
            finally
            {
                DatHarness.Release(MarketCell);
            }
        }

        /// <summary>
        /// Spacing on real geometry (owner, 2026-11-23): real wisp bodies spawned one after another beside a caster on the
        /// Marketplace floor, each choosing with the bodies already placed as the occupied spots (their live physics
        /// positions, in the caster's frame, as Player.EchoWispSpots reads them). Each lands on the first open spot of the
        /// eight that has a cell, 1.5 m from the caster at its height, and every pair stands more than 1 m apart.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void RealBodies_SpaceOutAroundTheCaster_FirstOpenSpotFirst()
        {
            var skip = DatHarness.Require();
            if (skip != null)
                Assert.Inconclusive(skip);

            var placed = new List<WorldObject>();

            try
            {
                var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
                var adjust = ACE.Server.Physics.Util.AdjustCell.Get(MarketCell >> 16, 0);
                var spots = EchoWispMath.SpawnCandidates(CasterOrigin, heading);

                (uint, Vector3)? Resolve(Vector3 p)
                {
                    var cell = adjust.GetCell(p);
                    return cell.HasValue ? ((uint, Vector3)?)(cell.Value, p) : null;
                }

                var resolvable = spots.Where(p => Resolve(p).HasValue).ToList();
                Assert.IsTrue(resolvable.Count >= 3, $"POSITIVE CONTROL: at least three of the eight spots have a cell here (saw {resolvable.Count})");

                foreach (var wcid in new[] { 1008600u, 1008601u, 1008602u })
                {
                    var occupied = placed.Select(b => b.PhysicsObj.Position.Frame.Origin).ToList();

                    var at = EchoWisp.ChooseSpawnPosition(CasterOrigin, heading, 0u, Resolve, occupied);
                    Assert.IsNotNull(at, $"{wcid}: an open spot");

                    var expected = resolvable.First(p => !EchoWispMath.IsSpotTaken(p, occupied));
                    AssertNear(expected, at.Pos, $"{wcid}: the first open spot that has a cell, in the owner's order");

                    var body = LoadBody(wcid);
                    EchoWisp.PrepareBody(body, at, 9.0, 0.5, 1.8f);
                    Assert.IsTrue(DatHarness.Place(body), $"{wcid}: the body entered the world");
                    placed.Add(body);

                    var final = body.PhysicsObj.Position.Frame.Origin;
                    Assert.AreEqual(CasterOrigin.Z, final.Z, 1e-3f, $"{wcid}: at the caster's height");
                    Assert.AreEqual(1.5f, new Vector2(final.X - CasterOrigin.X, final.Y - CasterOrigin.Y).Length(), 1e-3f, $"{wcid}: 1.5 m from the caster");
                }

                for (var i = 0; i < placed.Count; i++)
                    for (var j = i + 1; j < placed.Count; j++)
                        Assert.IsTrue(Vector3.Distance(placed[i].PhysicsObj.Position.Frame.Origin, placed[j].PhysicsObj.Position.Frame.Origin) > EchoWispMath.SpotTakenRadius,
                            $"wisps {i + 1} and {j + 1} more than 1 m apart");
            }
            finally
            {
                foreach (var body in placed)
                    DatHarness.Remove(body);

                DatHarness.Release(MarketCell);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------------------

        private static uint nextGuid = 0x7F2E0001;

        private static GenericObject NewGeneric() =>
            new GenericObject(new Weenie { WeenieClassId = 999950, ClassName = "echowisptestbody", WeenieType = WeenieType.Generic }, new ObjectGuid(nextGuid++));

        /// <summary>A body shaped from its committed SQL: weenie type, bool rows, PhysicsState and Setup.</summary>
        private static GenericObject LoadBody(uint wcid)
        {
            var sql = ReadWeenieSql(wcid);

            var bools = new Dictionary<PropertyBool, bool>();
            foreach (Match m in Regex.Matches(sql, $@"\({wcid},\s*(\d+),\s*(True|False)\s*\)"))
                bools[(PropertyBool)ushort.Parse(m.Groups[1].Value)] = m.Groups[2].Value == "True";

            var physicsState = int.Parse(Regex.Match(sql, $@"\({wcid},\s*93,\s*(\d+)\)").Groups[1].Value);
            var setup = Convert.ToUInt32(Regex.Match(sql, $@"\({wcid},\s*1,\s*(0x[0-9A-Fa-f]{{8}})\)").Groups[1].Value, 16);

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "echowispshape" + wcid,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Echo Wisp" } },
                PropertiesBool = bools,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, physicsState } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static string ReadWeenieSql(uint wcid)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", $"{wcid} Echo Wisp.sql");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
            }

            Assert.Fail($"Could not find Content/sql/weenies/{wcid} Echo Wisp.sql by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static IEnumerable<(string Name, string Text)> ServerSources()
        {
            string root = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && root == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");
                if (Directory.Exists(candidate))
                    root = candidate;
            }

            Assert.IsNotNull(root, "Could not find Source/ACE.Server by walking up from the test output directory");

            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();

            Assert.IsTrue(files.Count > 500, $"the scan must cover the whole ACE.Server tree, saw {files.Count} files");

            return files.Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        }

        private static void AssertNear(Vector3 expected, Vector3 actual, string why)
        {
            Assert.AreEqual(0f, Vector3.Distance(expected, actual), 1e-3f, $"{why}: expected {expected}, got {actual}");
        }

        /// <summary>
        /// The cell-dat bootstrap ExactPlacementTests uses: the cell dat is opened ONCE into a private instance and
        /// swapped into DatManager.CellDat only for the duration of the test (other classes depend on it being null), with
        /// PhysicsEngine.Server off so LScape builds the landblock from the dat.
        /// </summary>
        private static class DatHarness
        {
            private static readonly object initLock = new object();
            private static bool initialized;
            private static string skipReason;
            private static ACE.DatLoader.CellDatDatabase ownCellDat;
            private static ACE.DatLoader.CellDatDatabase previous;

            private static readonly System.Reflection.PropertyInfo cellDatProperty = typeof(ACE.DatLoader.DatManager).GetProperty(nameof(ACE.DatLoader.DatManager.CellDat));

            public static string Require()
            {
                lock (initLock)
                {
                    if (!initialized)
                    {
                        initialized = true;
                        skipReason = Initialize();
                    }
                }

                if (skipReason != null)
                    return skipReason;

                previous = ACE.DatLoader.DatManager.CellDat;
                cellDatProperty.SetValue(null, ownCellDat);
                return null;
            }

            public static bool Place(WorldObject wo)
            {
                var engine = ACE.Server.Physics.PhysicsEngine.Instance;
                var wasServer = engine.Server;
                engine.Server = false;
                try
                {
                    wo.InitPhysicsObj();
                    return wo.AddPhysicsObj();
                }
                finally
                {
                    engine.Server = wasServer;
                }
            }

            public static void Remove(WorldObject wo)
            {
                var engine = ACE.Server.Physics.PhysicsEngine.Instance;
                var wasServer = engine.Server;
                engine.Server = false;
                try
                {
                    wo.PhysicsObj?.leave_world();
                }
                finally
                {
                    engine.Server = wasServer;
                }
            }

            public static void Release(uint cell)
            {
                var engine = ACE.Server.Physics.PhysicsEngine.Instance;
                var wasServer = engine.Server;
                engine.Server = false;
                try
                {
                    ACE.Server.Physics.Common.LScape.unload_landblock(cell | 0xFFFF, 0);
                    ACE.Server.Physics.Util.AdjustCell.TryRemove(cell >> 16, 0);
                }
                finally
                {
                    engine.Server = wasServer;
                    cellDatProperty.SetValue(null, previous);
                    previous = null;
                }
            }

            private static string Initialize()
            {
                if (ACE.Server.Physics.PhysicsEngine.Instance == null)
                {
                    var engine = new ACE.Server.Physics.PhysicsEngine(new ACE.Server.Physics.Common.ObjectMaint(), new ACE.Server.Physics.Common.SmartBox());
                    engine.Server = true;
                }

                var candidates = new List<string>();
                var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
                if (!string.IsNullOrWhiteSpace(fromEnv))
                    candidates.Add(fromEnv);
                candidates.Add(@"C:\ACE\Dats\");
                candidates.Add(@"C:\Turbine\Asheron's Call\");

                var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")) && File.Exists(Path.Combine(c, "client_cell_1.dat")));
                if (datDir == null)
                    return $"Skipped: client_portal.dat + client_cell_1.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH.";

                try
                {
                    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                    if (ACE.DatLoader.DatManager.PortalDat == null)
                        ACE.DatLoader.DatManager.Initialize(datDir, true, false);

                    ownCellDat = new ACE.DatLoader.CellDatDatabase(Path.Combine(datDir, "client_cell_1.dat"), true);
                }
                catch (Exception ex)
                {
                    return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
                }

                return ACE.DatLoader.DatManager.PortalDat == null || ownCellDat == null ? $"Skipped: {datDir} did not produce both dats." : null;
            }
        }
    }
}
