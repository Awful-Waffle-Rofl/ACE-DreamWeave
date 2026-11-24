using System;
using System.Linq;
using System.Runtime.CompilerServices;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Echo Cast wisp rework, plan step 1 ("engine scope, inert"; Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-PLAN.md
    /// section 1). Nothing arms Player.EchoWispCast in production yet, so the launch path cannot be driven here; the
    /// pure rules are pinned by value, and every read site is pinned as an EXACT source line (trimmed, whole-line
    /// equality, exactly once in its member) so a reverted line fails rather than a substring surviving.
    ///
    /// Dat-free and PropertyManager-free: no key is read, so none is seeded.
    /// </summary>
    [TestClass]
    public class EchoWispEngineTests
    {
        private const string MagicFile = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string ProjectileFile = "Source/ACE.Server/WorldObjects/SpellProjectile.cs";
        private const string BuffsFile = "Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs";
        private const string EchoWispFile = "Source/ACE.Server/WorldObjects/Player_EchoWisp.cs";

        // UnsafeAccessor, never FieldInfo.SetValue on a Player (its static ctor poisons the test process)
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<EchoWispCast>k__BackingField")]
        private static extern ref EchoWispCastScope EchoWispCastField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<PhysicsObj>k__BackingField")]
        private static extern ref ACE.Server.Physics.PhysicsObj PhysicsObjField(WorldObject wo);

        /// <summary>A projectile with a bare physics object (a fresh one has none) and no landblock.</summary>
        private static SpellProjectile WithPhysics()
        {
            var wo = NewProjectile();
            PhysicsObjField(wo) = new ACE.Server.Physics.PhysicsObj();
            return wo;
        }

        private static uint nextWcid = 0x7E200000;

        private static SpellProjectile NewProjectile()
        {
            var weenie = new Weenie { WeenieClassId = nextWcid++, WeenieType = WeenieType.ProjectileSpell };
            return new SpellProjectile(weenie, new ACE.Entity.ObjectGuid(0x7F200000 + nextWcid));
        }

        private static Player UninitializedPlayer() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        /// <summary>A scope armed on THIS thread, as CastAsEchoWisp would stamp it.</summary>
        private static EchoWispCastScope ArmedHere(WorldObject origin) =>
            new EchoWispCastScope(origin, 0, false) { ArmingThreadId = Environment.CurrentManagedThreadId };

        /// <summary>A wisp origin that passes CastAsEchoWisp's in-the-world guard: a physics object and a landblock.</summary>
        private static SpellProjectile LiveOrigin()
        {
            var origin = WithPhysics();
            origin.CurrentLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            return origin;
        }

        private static string Body(string file, string signature) =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read(file), signature);

        /// <summary>Asserts <paramref name="line"/> appears as a whole (trimmed) line exactly once in <paramref name="body"/>; returns its offset.</summary>
        private static int AssertLine(string body, string line, string why)
        {
            var lines = body.Split('\n');
            var hits = lines.Count(l => l.Trim() == line);

            Assert.AreEqual(1, hits, $"expected exactly one line `{line}`: {why}");

            return body.IndexOf(line, StringComparison.Ordinal);
        }

        // ---- pure rules, by value ------------------------------------------------------------------------

        [TestMethod]
        public void ResolveLaunchStamps_OrdinaryCast_PassesTheLiveStampsThrough()
        {
            var live = new LifeProjectileCastStamp(1.2f, 2.05f, BloodChargeCastOutcome.Burst);

            var (spellweave, stamp) = WorldObject.ResolveLaunchStamps(false, 1.25f, live);

            Assert.AreEqual(1.25f, spellweave);
            Assert.AreEqual(1.2f, stamp.BloodPriceMod);
            Assert.AreEqual(2.05f, stamp.ChargeMod);
            Assert.AreEqual(BloodChargeCastOutcome.Burst, stamp.Outcome);
        }

        [TestMethod]
        public void ResolveLaunchStamps_EchoWispCast_IsNeutral_WhateverThePlayerHolds()
        {
            var live = new LifeProjectileCastStamp(1.2f, 2.05f, BloodChargeCastOutcome.Burst);

            var (spellweave, stamp) = WorldObject.ResolveLaunchStamps(true, 1.25f, live);

            Assert.AreEqual(1.0f, spellweave, "a wisp cast never takes the Spellweave charge the player paid for");
            Assert.AreEqual(1.0f, stamp.DamageMultiplier, "no Blood Price, no Blood Charge ramp or burst");
            Assert.IsFalse(stamp.Bursts, "a wisp cast never carries an Exsanguinate burst or its resistance-ignore");
            Assert.AreEqual(LifeProjectileCastStamp.None.Outcome, stamp.Outcome);
        }

        [TestMethod]
        public void GrantsLifeProjectileCharge_NeverForAnEchoWispCast()
        {
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: true, isEchoWispCast: true),
                "the aimed bolt of a wisp cast grants no Blood Charge");
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: false, isEchoWispCast: true));

            // every non-wisp answer is the aimed-projectile rule alone
            Assert.IsTrue(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: true, isEchoWispCast: false));
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: false, isEchoWispCast: false));
        }

        [TestMethod]
        public void EchoWispBlocksHit_OnlyAWispProjectileOnAPlayer()
        {
            Assert.IsTrue(SpellProjectile.EchoWispBlocksHit(isEchoWispCast: true, targetIsPlayer: true), "a wisp never lands on a player");
            Assert.IsFalse(SpellProjectile.EchoWispBlocksHit(isEchoWispCast: true, targetIsPlayer: false));
            Assert.IsFalse(SpellProjectile.EchoWispBlocksHit(isEchoWispCast: false, targetIsPlayer: true), "an ordinary cast is untouched");
            Assert.IsFalse(SpellProjectile.EchoWispBlocksHit(isEchoWispCast: false, targetIsPlayer: false));
        }

        [TestMethod]
        public void InheritEchoWispMark_AoeChildOfAWispCastKeepsTheMark_AndAnOrdinaryChildDoesNot()
        {
            var wispParent = NewProjectile();
            wispParent.IsEchoWispCast = true;

            var child = NewProjectile();
            Assert.IsFalse(child.IsEchoWispCast, "precondition: a fresh projectile is not a wisp cast");

            SpellProjectile.InheritEchoWispMark(child, wispParent);
            Assert.IsTrue(child.IsEchoWispCast);
            Assert.IsFalse(child.IsClassAbilitySpawned, "the wisp mark is not the no-cascade mark");
            Assert.IsFalse(child.FromProc, "the mark leaves FromProc to the spawn site");

            var ordinaryChild = NewProjectile();
            ordinaryChild.IsEchoWispCast = true;
            SpellProjectile.InheritEchoWispMark(ordinaryChild, NewProjectile());
            Assert.IsFalse(ordinaryChild.IsEchoWispCast, "the child takes the parent's value, never keeps a stale one");

            SpellProjectile.InheritEchoWispMark(null, wispParent);
            SpellProjectile.InheritEchoWispMark(child, null);
        }

        [TestMethod]
        public void EchoWispCastScope_CarriesOriginBasisAndProcSkill()
        {
            var origin = NewProjectile();

            var scope = new EchoWispCastScope(origin, 812, inheritsProcCasterSkill: true);

            Assert.AreSame(origin, scope.Origin);
            Assert.AreEqual(812u, scope.LifeProjectileDamage);
            Assert.IsTrue(scope.InheritsProcCasterSkill);
        }

        /// <summary>
        /// What the launch geometry RECEIVES: the wisp while the latch is armed, the object itself otherwise (and
        /// always for a non-player).
        /// </summary>
        [TestMethod]
        public void ProjectileGeometrySource_IsTheWispOnlyWhileTheLatchIsArmed()
        {
            var player = UninitializedPlayer();
            var wisp = NewProjectile();

            Assert.AreSame(player, player.ProjectileGeometrySource, "latch null: the player launches its own projectiles");

            EchoWispCastField(player) = ArmedHere(wisp);
            Assert.AreSame(wisp, player.ProjectileGeometrySource, "latch armed: the projectiles leave the wisp");

            EchoWispCastField(player) = new EchoWispCastScope(wisp, 0, false) { ArmingThreadId = Environment.CurrentManagedThreadId + 1 };
            Assert.AreSame(player, player.ProjectileGeometrySource, "a latch armed on another thread is invisible here");

            EchoWispCastField(player) = null;
            Assert.AreSame(player, player.ProjectileGeometrySource);

            var monster = NewProjectile();
            Assert.AreSame(monster, monster.ProjectileGeometrySource, "a non-player object is always its own source");
        }

        [TestMethod]
        public void CastAsEchoWisp_RefusesInvalidCalls_APlayerTarget_AndNesting_WithoutDisturbingTheLatch()
        {
            var player = UninitializedPlayer();
            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            var scope = new EchoWispCastScope(LiveOrigin(), 0, false);

            Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(null, spell, null));
            Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(new EchoWispCastScope(null, 0, false), spell, null));
            Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(scope, null, null));

            // a harmful spell (the target guard reads its type and flags: owner 2026-11-23, Restore spells may reach a fellow)
            var harmful = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            harmful._spellBase = (ACE.DatLoader.Entity.SpellBase)RuntimeHelpers.GetUninitializedObject(typeof(ACE.DatLoader.Entity.SpellBase));
            Assert.IsTrue(harmful.IsHarmful, "precondition: no Beneficial flag");

            Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(scope, harmful, UninitializedPlayer()),
                "class abilities never reach PvP: a harmful wisp cast never lands on a player");
            Assert.IsNull(player.EchoWispCast, "a refused call never arms the latch");

            var outer = ArmedHere(NewProjectile());
            EchoWispCastField(player) = outer;
            try
            {
                Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(scope, spell, null), "not reentrant");
                Assert.AreSame(outer, player.EchoWispCast, "a refused nested call must not disarm the outer cast");
            }
            finally
            {
                EchoWispCastField(player) = null;
            }
        }

        [TestMethod]
        public void CastAsEchoWisp_RefusesAnOriginNotInTheWorld()
        {
            var player = UninitializedPlayer();
            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));

            var noPhysics = NewProjectile();
            noPhysics.CurrentLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            Assert.IsNull(noPhysics.PhysicsObj, "precondition: only the physics object is missing");
            var noPhysicsScope = new EchoWispCastScope(noPhysics, 0, false);
            Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(noPhysicsScope, spell, null), "an origin without a physics object");

            var noLandblock = WithPhysics();
            Assert.IsNotNull(noLandblock.PhysicsObj, "precondition: only the landblock is missing");
            Assert.IsNull(noLandblock.CurrentLandblock, "precondition");
            var noLandblockScope = new EchoWispCastScope(noLandblock, 0, false);
            Assert.AreEqual(EchoWispCastOutcome.Refused, player.CastAsEchoWisp(noLandblockScope, spell, null), "an origin not on a landblock");

            Assert.IsNull(player.EchoWispCast, "a refused call never arms the latch");
            Assert.AreEqual(0, noPhysicsScope.ArmingThreadId, "a refused call never stamps the arming thread");
            Assert.AreEqual(0, noLandblockScope.ArmingThreadId);
        }

        [TestMethod]
        public void ActiveOn_HonoursTheScopeOnlyOnItsArmingThread()
        {
            var scope = new EchoWispCastScope(NewProjectile(), 0, false) { ArmingThreadId = 17 };

            Assert.AreSame(scope, EchoWispCastScope.ActiveOn(scope, 17), "the arming thread sees the latch");
            Assert.IsNull(EchoWispCastScope.ActiveOn(scope, 18), "any other thread sees null");
            Assert.IsNull(EchoWispCastScope.ActiveOn(null, 17));
            Assert.IsNull(EchoWispCastScope.ActiveOn(new EchoWispCastScope(NewProjectile(), 0, false), 5), "an unstamped scope is honoured nowhere");
        }

        [TestMethod]
        public void ActiveEchoWispCast_IsNullOnAnotherThread()
        {
            var player = UninitializedPlayer();
            var scope = ArmedHere(NewProjectile());

            EchoWispCastField(player) = scope;
            try
            {
                Assert.AreSame(scope, player.ActiveEchoWispCast, "the arming thread sees the latch");

                EchoWispCastScope seenElsewhere = scope;
                var other = new System.Threading.Thread(() => seenElsewhere = player.ActiveEchoWispCast);
                other.Start();
                Assert.IsTrue(other.Join(2000), "the reader thread finished");

                Assert.IsNull(seenElsewhere, "another thread sees no wisp cast");
                Assert.AreSame(scope, player.EchoWispCast, "the raw latch itself is untouched");
            }
            finally
            {
                EchoWispCastField(player) = null;
            }
        }

        [TestMethod]
        public void RunWithEchoWispSuspended_ReadsNullInside_RestoresAfter_EvenWhenItThrows()
        {
            var player = UninitializedPlayer();
            var scope = ArmedHere(NewProjectile());

            EchoWispCastField(player) = scope;
            try
            {
                EchoWispCastScope rawInside = scope, activeInside = scope;
                WorldObject geometryInside = null;

                var result = player.RunWithEchoWispSuspended(40, two =>
                {
                    rawInside = player.EchoWispCast;
                    activeInside = player.ActiveEchoWispCast;
                    geometryInside = player.ProjectileGeometrySource;
                    return two + 2;
                });

                Assert.AreEqual(42, result, "the action gets the argument and its result comes back");
                Assert.IsNull(rawInside, "suspended inside");
                Assert.IsNull(activeInside, "suspended inside");
                Assert.AreSame(player, geometryInside, "an impact inside AddObject launches from the player");
                Assert.AreSame(scope, player.EchoWispCast, "restored afterwards");

                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    player.RunWithEchoWispSuspended<int, bool>(0, _ => throw new InvalidOperationException("impact threw")));
                Assert.AreSame(scope, player.EchoWispCast, "restored even when the action throws");
            }
            finally
            {
                EchoWispCastField(player) = null;
            }
        }

        // ---- exact-line source pins: the geometry reads ----------------------------------------------------

        [TestMethod]
        public void ProjectileGeometrySource_IsTheLatchOriginElseThis()
        {
            var src = PooledLootSourceText.Read(MagicFile);

            AssertLine(src, "internal WorldObject ProjectileGeometrySource => (this as Player)?.ActiveEchoWispCast?.Origin ?? this;",
                "the geometry source is the wisp only while the latch is armed");
        }

        [TestMethod]
        public void CreateSpellProjectiles_TakesOriginsAndVelocityFromTheGeometrySource()
        {
            var body = Body(MagicFile, "public List<SpellProjectile> CreateSpellProjectiles(");

            AssertLine(body, "var geometrySource = ProjectileGeometrySource;", "one read of the geometry source");
            AssertLine(body, "var origins = geometrySource.CalculateProjectileOrigins(spell, spellType, target);", "origins from the wisp");
            AssertLine(body, "var velocity = geometrySource.CalculateProjectileVelocity(spell, target, spellType, origins[0]);", "velocity from the wisp");
        }

        [TestMethod]
        public void LaunchSpellProjectiles_ReadsPositionHeadingAndSightFromTheGeometrySource()
        {
            var body = Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles(");

            AssertLine(body, "var geometrySource = ProjectileGeometrySource;", "one read of the geometry source");
            AssertLine(body, "var casterLoc = geometrySource.PhysicsObj.Position.ACEPosition(geometrySource.PhysicsObj.CurInstance);", "launch position from the wisp");
            AssertLine(body, "var qDir = geometrySource.PhysicsObj.Position.GetOffset(target.PhysicsObj.Position);", "heading from the wisp");
            AssertLine(body, "if (!geometrySource.IsProjectileVisible(sp))", "line of sight from the wisp");
        }

        /// <summary>The caster the projectile RECEIVES stays the player, and the cast stays first-hand.</summary>
        [TestMethod]
        public void LaunchSpellProjectiles_KeepsThePlayerAsSourceAndTheCastFirstHand()
        {
            var body = Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles(");

            AssertLine(body, "sp.ProjectileSource = this;", "the player stays the caster, never the wisp");
            AssertLine(body, "sp.FromProc = fromProc;", "a wisp cast is first-hand: FromProc is the caller's, never forced on");
        }

        [TestMethod]
        public void LaunchSpellProjectiles_StampsTheWispRulesBeforeAddObject()
        {
            var body = Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles(");

            var addObject = AssertLine(body, "? ((Player)this).RunWithEchoWispSuspended(sp, static projectile => LandblockManager.AddObject(projectile))", "the launch");

            var stamps = new[]
            {
                "var echoWispCast = (this as Player)?.ActiveEchoWispCast;",
                "sp.IsEchoWispCast = echoWispCast != null;",
                "sp.InheritsProcCasterSkill = echoWispCast?.InheritsProcCasterSkill ?? false;",
                "var launchStamps = ResolveLaunchStamps(sp.IsEchoWispCast, (this as Player)?.GetSpellweaveSpellDamageMod() ?? 1.0f, (this as Player)?.GetLifeProjectileCastStamp() ?? LifeProjectileCastStamp.None);",
                "sp.SpellweaveDamageMod = launchStamps.Spellweave;",
                "sp.LifeProjectileStamp = launchStamps.Stamp;",
            };

            foreach (var stamp in stamps)
            {
                var at = AssertLine(body, stamp, "a wisp stamp");
                Assert.IsTrue(at < addObject, $"`{stamp}` must come BEFORE AddObject: an overlapping spawn collides inside AddObject");
            }
        }

        // ---- exact-line source pins: the cast-time read sites ---------------------------------------------

        [TestMethod]
        public void HandleCastSpell_SkipsSpellweaveAndPlaysTheCastOnTheWisp()
        {
            var body = Body(MagicFile, "protected bool HandleCastSpell(Spell spell");

            AssertLine(body, "if (this is Player spellweavePlayer && !spellweavePlayer.ClassAbilityProcCastActive && spellweavePlayer.ActiveEchoWispCast == null)",
                "a wisp cast neither consumes nor arms the weave");
            AssertLine(body, "DoSpellEffects(spell, ProjectileGeometrySource, target);", "the caster effect plays on the wisp");
        }

        [TestMethod]
        public void HandleCastSpell_Projectile_WispCastUsesTheSnapshottedBasis_BeforeAnyDrain()
        {
            var body = Body(MagicFile, "private void HandleCastSpell_Projectile(");

            var latch = AssertLine(body, "var echoWispCast = (this as Player)?.ActiveEchoWispCast;", "the latch read");
            var launch = AssertLine(body, "CreateSpellProjectiles(spell, target, weapon, isWeaponSpell, fromProc, echoWispCast.LifeProjectileDamage);",
                "the wisp launches with the original cast's basis");
            var drain = AssertLine(body, "uint damage = 0;", "the hand-cast drain path");
            var bloodCharge = body.IndexOf("ApplyLifeProjectileBloodCharge(spell, target);", StringComparison.Ordinal);

            Assert.IsTrue(latch < launch && launch < drain && drain < bloodCharge,
                "the wisp branch must return before the vital drain, Sanguine Ward, self damage history, Blood Charge resolve and self-death check");

            var branch = body.Substring(launch, drain - launch);
            Assert.IsTrue(branch.Contains("return;"), "the wisp branch must return, not fall through into the drain");
        }

        [TestMethod]
        public void ApplyHarmClassAbilityDamage_WispCastNeutralisesTheOneShotTerms()
        {
            var body = Body(BuffsFile, "public float ApplyHarmClassAbilityDamage()");

            AssertLine(body, "var echoWispCast = ActiveEchoWispCast != null;", "Harm resolves synchronously, so the latch is read directly");
            AssertLine(body, "var mod = echoWispCast ? 1.0f : GetBloodPriceDamageMod();", "Blood Price neutral");
            AssertLine(body, "mod *= echoWispCast ? 1.0f : GetSpellweaveSpellDamageMod();", "Spellweave neutral");
            var gate = AssertLine(body, "if (!echoWispCast)", "the Blood Charge term and grant are skipped");
            var charge = AssertLine(body, "mod *= ApplyBloodChargeDamage(exsanguinateEligible: false, out var outcome);", "the Blood Charge term");
            var grant = AssertLine(body, "TryGrantBloodCharge();", "the Blood Charge grant");

            Assert.IsTrue(gate < charge && charge < grant, "the Blood Charge term and its grant must both sit inside the wisp gate");
        }

        [TestMethod]
        public void TransferDrain_WispCastGrantsNoBloodCharge()
        {
            var src = PooledLootSourceText.Read(MagicFile);

            AssertLine(src, "if (!isSecondaryStrike && drainCaster.ActiveEchoWispCast == null)", "a wisp drain builds no pool");
        }

        // ---- exact-line source pins: the impact-time read sites -------------------------------------------

        [TestMethod]
        public void GetClassAbilitySpellDamageMod_WispCastTakesNoBloodPriceAndSpendsNoAdrenaline()
        {
            var body = Body(BuffsFile, "public float GetClassAbilitySpellDamageMod(Spell spell, float spellweaveDamageMod, bool echoWispCast = false)");

            AssertLine(body, "var mod = echoWispCast ? 1.0f : GetBloodPriceDamageMod();", "Blood Price 1.0 for a wisp projectile");
            AssertLine(body, "mod *= echoWispCast ? 1.0f : AdrenalineAbility.ConsumeSpellDamageMultiplier(this);", "a wisp projectile never spends Adrenaline");
        }

        [TestMethod]
        public void SpellProjectile_ImpactSitesPassIsEchoWispCast()
        {
            var src = PooledLootSourceText.Read(ProjectileFile);

            AssertLine(src, "finalDamage *= classAbilityCaster.GetClassAbilitySpellDamageMod(Spell, SpellweaveDamageMod, IsEchoWispCast);",
                "the war/void site hands the wisp mark to the damage mod");
            AssertLine(src, "grantCharge: GrantsLifeProjectileCharge(ProjectileTarget != null, IsEchoWispCast), castStamp: LifeProjectileStamp,",
                "the Life projectile site grants no Blood Charge for a wisp");
            AssertLine(src, "aimedNonEchoProjectile: GrantsLifeProjectileCharge(ProjectileTarget != null, IsEchoWispCast), castStamp: LifeProjectileStamp);",
                "the life health bolt site grants no Blood Charge for a wisp");
        }

        [TestMethod]
        public void SpawnClassAbilityAoeChild_InheritsTheWispMark()
        {
            var body = Body(ProjectileFile, "public void SpawnClassAbilityAoeChild(");

            AssertLine(body, "InheritEchoWispMark(sp, this);       // a child of a wisp cast keeps the wisp rules", "AOE children inherit the wisp mark");
        }

        [TestMethod]
        public void OnCollideObject_AWispProjectileLandsNothingOnAPlayer_BeforeAnyDamageOrPkMessage()
        {
            var body = Body(ProjectileFile, "public override void OnCollideObject(WorldObject target)");

            var guard = AssertLine(body, "if (EchoWispBlocksHit(IsEchoWispCast, creatureTarget is Player))", "the PvP guard");
            var pk = body.IndexOf("CheckPKStatusVsTarget(creatureTarget, Spell)", StringComparison.Ordinal);
            var damage = body.IndexOf("CalculateDamage(ProjectileSource, creatureTarget", StringComparison.Ordinal);

            Assert.IsTrue(pk > 0 && damage > 0, "anchors not found");
            Assert.IsTrue(guard < pk && guard < damage, "the guard must run before the PK check and before any damage");

            var after = body.Substring(guard, 120);
            StringAssert.StartsWith(after.Substring(after.IndexOf('\n') + 1).TrimStart(), "return;", "the guard must return");
        }

        // ---- exact-line source pins: the latch itself ----------------------------------------------------

        [TestMethod]
        public void CastAsEchoWisp_ArmsTheLatchOnlyAroundTheCast_AndRunsProcsAfterIt()
        {
            var body = Body(EchoWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");

            var arm = AssertLine(body, "EchoWispCast = scope;", "armed for the cast");
            var tryAt = body.IndexOf("try", arm, StringComparison.Ordinal);
            var finallyAt = body.IndexOf("finally", arm, StringComparison.Ordinal);
            var disarm = AssertLine(body, "EchoWispCast = null;", "cleared in the finally");
            var cast = AssertLine(body, "if (outcome == EchoWispCastOutcome.Cast && !HandleCastSpell(spell, target, null, launcher, false))", "the cast itself, no item caster; the launcher is the proc weapon or the wand");
            var targetProcs = AssertLine(body, "TryProcEquippedItems(this, target, false, caster);", "target procs");
            var selfProcs = AssertLine(body, "TryProcEquippedItems(this, this, true, caster);", "self procs");

            Assert.IsTrue(arm < tryAt && tryAt < cast && cast < finallyAt && finallyAt < disarm, "the cast runs inside try, the latch clears in finally");
            Assert.IsTrue(disarm < targetProcs && disarm < selfProcs, "procs run with the latch cleared, as at a wisp projectile's impact");

            AssertLine(body, "if (TryResistSpell(target, spell, null, casterSkillOverride: scope.InheritsProcCasterSkill ? Skill.ItemEnchantment : (Skill?)null))",
                "a proc-spawned wisp resists with the proc's caster skill");
            AssertLine(body, "var targetRefusal = EchoWispMath.CastTargetRefusal(restore, target != null, restoreTarget != null,",
                "never a player target for a harmful spell (EchoWispMath.CastTargetRefusal)");

            var stamp = AssertLine(body, "scope.ArmingThreadId = Environment.CurrentManagedThreadId;", "the arming thread is stamped");
            Assert.IsTrue(stamp < arm, "the arming thread must be stamped before the latch is armed");

            var originGuard = AssertLine(body, "if (scope.Origin.PhysicsObj == null || scope.Origin.CurrentLandblock == null)", "the origin must be in the world");
            Assert.IsTrue(originGuard < arm, "the origin guard must run before the latch is armed");
        }

        [TestMethod]
        public void LaunchSpellProjectiles_SuspendsTheLatchAcrossAddObject()
        {
            var body = Body(MagicFile, "public List<SpellProjectile> LaunchSpellProjectiles(");

            var choose = AssertLine(body, "var added = echoWispCast != null", "a wisp cast takes the suspended path");
            var suspended = AssertLine(body, "? ((Player)this).RunWithEchoWispSuspended(sp, static projectile => LandblockManager.AddObject(projectile))",
                "AddObject runs with the latch suspended, through a static lambda so the loop body captures nothing");
            var plain = AssertLine(body, ": LandblockManager.AddObject(sp);", "every other cast is the plain call");
            var check = AssertLine(body, "if (!added)", "the result is what the old call returned");

            Assert.IsTrue(choose < suspended && suspended < plain && plain < check, "suspend, AddObject, then the result check");

            var helper = Body(EchoWispFile, "internal TResult RunWithEchoWispSuspended<TArg, TResult>(TArg arg, Func<TArg, TResult> action)");

            var save = AssertLine(helper, "var suspended = EchoWispCast;", "save the latch");
            var clear = AssertLine(helper, "EchoWispCast = null;", "suspend it");
            var run = AssertLine(helper, "return action(arg);", "run the action");
            var fin = helper.IndexOf("finally", StringComparison.Ordinal);
            var restore = AssertLine(helper, "EchoWispCast = suspended;", "restore it");

            Assert.IsTrue(save < clear && clear < run && run < fin && fin < restore, "save, suspend, run, then restore in the finally");
        }

        [TestMethod]
        public void EveryReadSiteGoesThroughTheThreadAwareAccessor()
        {
            var accessor = Body(EchoWispFile, "public EchoWispCastScope ActiveEchoWispCast");

            var read = AssertLine(accessor, "var scope = EchoWispCast;", "one read of the raw latch");
            var nullCheck = AssertLine(accessor, "if (scope == null)", "an ordinary cast returns at once");
            var thread = AssertLine(accessor, "return EchoWispCastScope.ActiveOn(scope, Environment.CurrentManagedThreadId);", "the one place the thread rule is applied");
            Assert.IsTrue(read < nullCheck && nullCheck < thread, "the null latch must return before the thread id is read");

            // TREE-WIDE over Source/ACE.Server: the raw latch only in Player_EchoWisp.cs; the thread rule (ArmingThreadId,
            // ActiveOn) only there and in EchoWispCast.cs, which defines it; and no file that touches the wisp latch reads
            // the thread id itself. CurrentManagedThreadId has unrelated users elsewhere (AccountVaultStore, WorldManager,
            // ...), so that check covers the wisp-touching files rather than the whole tree.
            var raw = new System.Text.RegularExpressions.Regex(@"(?<![\w.])EchoWispCast\b(?!Scope|Outcome)|\.EchoWispCast\b(?!Scope|Outcome)");
            var rule = new System.Text.RegularExpressions.Regex(@"\bArmingThreadId\b|\bActiveOn\s*\(");
            var wisp = new System.Text.RegularExpressions.Regex(@"EchoWispCast");

            var serverDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(FindServerFile(EchoWispFile)));
            var scanned = 0;

            foreach (var path in System.IO.Directory.EnumerateFiles(serverDir, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                var rel = System.IO.Path.GetRelativePath(serverDir, path).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/"))
                    continue;

                scanned++;
                var code = AccountVaultPurgeTests.StripComments(System.IO.File.ReadAllText(path));
                var isLatchFile = rel == "WorldObjects/Player_EchoWisp.cs";
                var isRuleFile = rel == "ClassAbilities/EchoWispCast.cs";

                if (!isLatchFile)
                    Assert.IsFalse(raw.IsMatch(code), $"{rel} reads the raw EchoWispCast latch; use ActiveEchoWispCast");

                if (!isLatchFile && !isRuleFile)
                {
                    Assert.IsFalse(rule.IsMatch(code), $"{rel} applies the wisp thread rule itself; use ActiveEchoWispCast");

                    if (wisp.IsMatch(code))
                        Assert.IsFalse(code.Contains("CurrentManagedThreadId"), $"{rel} touches the wisp latch and compares thread ids itself; use ActiveEchoWispCast");
                }
            }

            Assert.IsTrue(scanned > 500, $"the scan must cover the whole ACE.Server tree, saw {scanned} files");
        }

        /// <summary>The absolute path of a repo-relative file, found the way PooledLootSourceText.Read finds it.</summary>
        private static string FindServerFile(string repoRelativePath)
        {
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, repoRelativePath);
                if (System.IO.File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
