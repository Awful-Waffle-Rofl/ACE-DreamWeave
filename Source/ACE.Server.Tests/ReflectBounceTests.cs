using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Reflect (Vanguard T3) bounce rework, owner rulings 2026-10-23: the bounce resolves against the FIRER's
    /// defenses, a cosmetic copy flies back and the damage lands when it arrives, the chat line reports the
    /// damage actually applied, the shot's DoT / debuff riders land on the firer and its leech is mirrored,
    /// and the firer's wards and immunities apply to the bounce and to its Thorns answer.
    ///
    /// The arrival body (Player.ApplyReflectBounce) and the calculations are driven directly - the ActionChain
    /// that schedules the arrival never runs in this harness, and no test here can construct a live Player
    /// (its constructor reaches the auth database), so the reflector is ThornsCloseRangeTests' uninitialized,
    /// session-less Player. The launch half (cosmetic projectiles, which need the dats and a landblock) is
    /// pinned by source scans. Every negative case carries a positive control that differs in one input.
    /// </summary>
    [TestClass]
    public class ReflectBounceTests
    {
        private static readonly string[] SeededDoubles =
        {
            "class_ability_thorns_max_range", "class_ability_thorns_percent_per_rank",
            "monster_effect_avoidance_cap", "monster_effect_proc_chance_cap", "monster_effect_leech_cap",
            "monster_effect_ward_cap", "monster_effect_damage_rider_cap", "monster_effect_debuff_cap",
        };

        private static readonly string[] SeededBools = { "class_abilities_enabled", "equipment_mods_enabled", "monster_effects_enabled", "shield_applies_in_noncombat" };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void SeedTunables() => RestoreTunables();

        [TestCleanup]
        public void RestoreTunables()
        {
            foreach (var key in SeededDoubles)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

            foreach (var key in SeededBools)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);
        }

        private static MonsterEffectSpec Spec(string record)
        {
            MonsterEffectParser.Parse(record, out var specs, out var errors);
            Assert.AreEqual(0, errors.Count, $"fixture record did not parse: {record}");
            return specs[0];
        }

        /// <summary>The shot as the firer rolled it against the player: only the attack half is read by the bounce.</summary>
        private static DamageEvent IncomingShot(Creature firer, float damageBeforeMitigation = 100.0f) => new DamageEvent
        {
            Attacker = firer,
            CombatType = CombatType.Missile,
            DamageSource = firer,
            DamageType = DamageType.Slash,
            AttackHeight = AttackHeight.Medium,
            DamageBeforeMitigation = damageBeforeMitigation,
            Damage = damageBeforeMitigation,
        };

        // ---- the damage calculation: the firer's defenses, not the player's --------------------------------

        [TestMethod]
        public void ReflectedMissile_ArmoredFirerTakesLessThanUnarmored()
        {
            var reflector = TestCreatures.CreateDefender();

            var unarmored = TestCreatures.CreateDefender(baseArmor: 0, maxHealth: 500);
            var armored = TestCreatures.CreateDefender(baseArmor: 300, maxHealth: 500);

            var open = DamageEvent.CalculateReflectedMissile(IncomingShot(unarmored), reflector);
            var plated = DamageEvent.CalculateReflectedMissile(IncomingShot(armored), reflector);

            // positive control: with no armor the bounce carries the firer's whole attack through
            Assert.IsTrue(open.Damage > 0.0f, "control: an unarmored firer must take the bounce");
            Assert.AreSame(unarmored, open.Defender, "the bounce resolves against the FIRER");

            Assert.IsTrue(plated.Damage < open.Damage,
                $"the firer's own armor must mitigate its bounced shot ({plated.Damage} vs {open.Damage} unarmored)");
            Assert.IsTrue(plated.ArmorMod < 1.0f, "the armored firer's armor mod must actually bite");
        }

        [TestMethod]
        public void ReflectedMissile_IgnoresTheFirersAvoidance()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            var evader = TestCreatures.CreateDefender(maxHealth: 500);
            evader.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((new AvoidEffect(), Spec("avoid pct=1.0"))));

            // CONTROL: the same firer, attacked normally, avoids every hit (no overpower, so the avoidance
            // roll runs)
            var attacker = TestCreatures.CreateAttacker(overpower: false);
            var normal = DamageEvent.CalculateDamage(attacker, evader, attacker);
            Assert.IsTrue(normal.Evaded, "control: a full-chance avoid must evade an ordinary hit");

            // the bounce runs no evade and no avoidance: it lands exactly as on a twin with no effects
            var twin = TestCreatures.CreateDefender(maxHealth: 500);
            var reflector = TestCreatures.CreateDefender();

            var bounced = DamageEvent.CalculateReflectedMissile(IncomingShot(evader), reflector);
            var twinBounced = DamageEvent.CalculateReflectedMissile(IncomingShot(twin), reflector);

            Assert.IsFalse(bounced.Evaded, "a bounce is never evaded");
            Assert.IsTrue(bounced.Damage > 0.0f, "a firer that avoids everything still takes its own bounced shot");
            Assert.AreEqual(twinBounced.Damage, bounced.Damage, 1e-4f, "the avoid effect must not touch the bounce at all");
        }

        [TestMethod]
        public void ReflectedMissile_InvincibleFirerTakesNothing()
        {
            var reflector = TestCreatures.CreateDefender();
            var mortal = TestCreatures.CreateDefender(maxHealth: 500);
            var invincible = TestCreatures.CreateDefender(maxHealth: 500);
            invincible.Invincible = true;

            Assert.IsTrue(DamageEvent.CalculateReflectedMissile(IncomingShot(mortal), reflector).Damage > 0.0f, "control");
            Assert.AreEqual(0.0f, DamageEvent.CalculateReflectedMissile(IncomingShot(invincible), reflector).Damage, "an Invincible firer is honored");
        }

        // ---- the arrival: exact damage, wards apply, the line reports what landed ----------------------------

        [TestMethod]
        public void ApplyReflectBounce_LowersHealthByExactlyItsReturnValue()
        {
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();
            var firer = TestCreatures.CreateDefender(maxHealth: 500);
            firer.Health.Current = 400;

            var applied = player.ApplyReflectBounce(firer, DamageType.Slash, 123.4f, "Arrow", MonsterEffectTrigger.Hit, DamageType.Slash, 50.0f);

            Assert.AreEqual(123u, applied, "the rounded bounce lands in full on a firer with no filters");
            Assert.AreEqual(400u - applied, firer.Health.Current, "health drops by exactly the reported amount");

            // a firer that died before the bounce arrived takes nothing and reports nothing
            var dead = TestCreatures.CreateDefender(maxHealth: 500);
            dead.Health.Current = 0;

            Assert.AreEqual(0u, player.ApplyReflectBounce(dead, DamageType.Slash, 80.0f, "Arrow", MonsterEffectTrigger.Hit, DamageType.Slash, 50.0f));
        }

        [TestMethod]
        public void ApplyReflectBounce_AWardedFirerAbsorbsPart()
        {
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();

            var warded = TestCreatures.CreateDefender(maxHealth: 500);
            warded.Health.Current = 400;
            warded.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((new WardEffect(), Spec("ward amount=50 secs=300 on=spawn"))));
            warded.MonsterEffectHeartbeat();

            Assert.IsTrue(warded.TryGetMonsterWardRemaining(out var before) && before == 50, "harness: the ward must be up at 50");

            // CONTROL: the identical unwarded firer takes the bounce in full
            var plain = TestCreatures.CreateDefender(maxHealth: 500);
            plain.Health.Current = 400;

            var plainApplied = player.ApplyReflectBounce(plain, DamageType.Slash, 120.0f, "Arrow", MonsterEffectTrigger.Hit, DamageType.Slash, 120.0f);
            var wardedApplied = player.ApplyReflectBounce(warded, DamageType.Slash, 120.0f, "Arrow", MonsterEffectTrigger.Hit, DamageType.Slash, 120.0f);

            Assert.AreEqual(120u, plainApplied, "control: no ward, the whole bounce lands");
            Assert.AreEqual(70u, wardedApplied, "the 50-point ward absorbs 50 of the 120 - the bounce no longer skips the firer's wards");
            Assert.AreEqual(330u, warded.Health.Current);
        }

        [TestMethod]
        public void FormatReflectBounceLine_ReportsTheFirerTheShotAndTheDamage()
        {
            Assert.AreEqual("You reflect Drudge Slinker's Arrow back at it for 1,234 points of damage!",
                Player.FormatReflectBounceLine("Drudge Slinker", "Arrow", 1234));

            Assert.AreEqual("You reflect Olthoi Warrior's Flame Bolt VI back at it for 0 points of damage!",
                Player.FormatReflectBounceLine("Olthoi Warrior", "Flame Bolt VI", 0));
        }

        [TestMethod]
        public void ReflectBounceDelay_IsTheFlightTimeClampedToThreeSeconds()
        {
            Assert.AreEqual(1.25, Player.ReflectBounceDelay(1.25f), 1e-6);
            Assert.AreEqual(Player.MaxReflectBounceDelaySeconds, Player.ReflectBounceDelay(40.0f), 0.0, "a degenerate estimate is clamped");
            Assert.AreEqual(0.0, Player.ReflectBounceDelay(-1.0f), 0.0);
            Assert.AreEqual(0.0, Player.ReflectBounceDelay(float.NaN), 0.0, "a non-finite estimate lands at once");
            Assert.AreEqual(3.0, Player.MaxReflectBounceDelaySeconds, 0.0);
        }

        [TestMethod]
        public void GetAimHeight_PlayerWhoHasNotAttacked_AimsMedium()
        {
            // the return arrow is aimed from a player whose AttackHeight may never have been set
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();

            Assert.IsNull(player.AttackHeight, "harness: an uninitialized player has no attack height");
            Assert.AreEqual(2.0f, player.GetAimHeight(null), 0.0f, "null must aim Medium, not throw");

            // control: a set height still reads through
            player.AttackHeight = AttackHeight.High;
            Assert.AreEqual(1.0f, player.GetAimHeight(null), 0.0f);
        }

        // ---- the riders: reflected onto the firer, never onto the player --------------------------------------

        private static MonsterEffectSet RiderSet(string debuffOn = "hit") => MonsterEffectSet.BuildFromHandlers(
            (new DotEffect(), Spec("dot type=fire amount=7 ticks=3 interval=3 chance=1.0")),
            (new DebuffEffect(), Spec($"debuff what=attackskills mag=30 secs=20 chance=1.0 on={debuffOn}")),
            (new LeechEffect(), Spec("leech vital=health pct=0.25")));

        [TestMethod]
        public void ReflectedMissileRiders_LandOnTheFirer_AndTheLeechIsMirrored()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var firer = TestCreatures.CreateDefender(maxHealth: 500);
            firer.Health.Current = 300;
            firer.AttachMonsterEffectsForTest(RiderSet());

            var reflector = TestCreatures.CreateDefender(maxHealth: 500);
            reflector.Health.Current = 300;

            firer.ApplyReflectedMonsterEffectRiders(reflector, MonsterEffectTrigger.Hit, DamageType.Fire, 40.0f);

            // DoT: on the firer, keyed and ticking as the reflector's
            Assert.AreNotEqual(0L, DotEffect.CurrentToken(reflector, firer.Guid.Full), "the reflected DoT must be scheduled on the FIRER, attributed to the reflector");
            Assert.AreEqual(0L, DotEffect.CurrentToken(firer, reflector.Guid.Full), "nothing may be scheduled on the reflector");

            // debuff: on the firer, cast by the reflector
            var firerDebuff = firer.Biota.PropertiesEnchantmentRegistry.SingleOrDefault();
            Assert.IsNotNull(firerDebuff, "the reflected debuff must land on the firer");
            Assert.AreEqual(reflector.Guid.Full, firerDebuff.CasterObjectId, "and be attributed to the reflector");
            Assert.AreEqual(0, reflector.Biota.PropertiesEnchantmentRegistry.Count, "the reflector must receive no debuff");

            // leech: mirrored - the reflector gains 25% of the 40 it would have taken, the firer nothing
            Assert.AreEqual(310u, reflector.Health.Current, "the reflector gains what the monster would have leeched");
            Assert.AreEqual(300u, firer.Health.Current, "the monster gains nothing");
        }

        [TestMethod]
        public void NormalHitRiders_StillLandOnTheDefender_Control()
        {
            // the positive control for the test above: the same rider set through the ordinary outgoing-hit
            // dispatch lands on the DEFENDER and heals the attacker, so the reversal above is the bounce's doing
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            attacker.Health.Current = 300;
            attacker.AttachMonsterEffectsForTest(RiderSet());

            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.ApplyOutgoingHitMonsterEffects(defender, new DamageEvent { Damage = 40.0f, CombatType = CombatType.Missile, DamageType = DamageType.Fire });

            Assert.AreNotEqual(0L, DotEffect.CurrentToken(attacker, defender.Guid.Full));
            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.AreEqual(0, attacker.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.AreEqual(310u, attacker.Health.Current, "an ordinary hit heals the attacker");
        }

        [TestMethod]
        public void ReflectedSpellRiders_AnswerOnlyTheSpellHitTrigger()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var reflector = TestCreatures.CreateDefender(maxHealth: 500);

            // on=spellhit: a bounced bolt lays it on the caster
            var spellCaster = TestCreatures.CreateDefender(maxHealth: 500);
            spellCaster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (new DebuffEffect(), Spec("debuff what=attackskills mag=30 secs=20 chance=1.0 on=spellhit"))));

            spellCaster.ApplyReflectedMonsterEffectRiders(reflector, MonsterEffectTrigger.SpellHit, DamageType.Fire, 40.0f);

            Assert.AreEqual(1, spellCaster.Biota.PropertiesEnchantmentRegistry.Count, "a spellhit debuff answers a bounced bolt");

            // on=hit only: a bounced bolt is not a melee/missile hit, so it lays nothing (control: the same
            // record answers a bounced missile, and an on=hit,spellhit record answers the bolt)
            var both = TestCreatures.CreateDefender(maxHealth: 500);
            both.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (new DebuffEffect(), Spec("debuff what=attackskills mag=30 secs=20 chance=1.0 on=hit,spellhit"))));
            var missileOnly = TestCreatures.CreateDefender(maxHealth: 500);
            missileOnly.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (new DebuffEffect(), Spec("debuff what=attackskills mag=30 secs=20 chance=1.0 on=hit"))));

            missileOnly.ApplyReflectedMonsterEffectRiders(reflector, MonsterEffectTrigger.SpellHit, DamageType.Fire, 40.0f);
            Assert.AreEqual(0, missileOnly.Biota.PropertiesEnchantmentRegistry.Count, "an on=hit debuff ignores a bounced bolt");

            missileOnly.ApplyReflectedMonsterEffectRiders(reflector, MonsterEffectTrigger.Hit, DamageType.Fire, 40.0f);
            Assert.AreEqual(1, missileOnly.Biota.PropertiesEnchantmentRegistry.Count, "control: the same record answers a bounced missile");

            both.ApplyReflectedMonsterEffectRiders(reflector, MonsterEffectTrigger.SpellHit, DamageType.Fire, 40.0f);
            Assert.AreEqual(1, both.Biota.PropertiesEnchantmentRegistry.Count);

            Assert.AreEqual(0, reflector.Biota.PropertiesEnchantmentRegistry.Count, "the reflector receives nothing from any of them");
        }

        [TestMethod]
        public void DeferredOutgoingHitEffects_HoldTheRidersUntilApplied()
        {
            // the missile site's restructure: a shot at a player is calculated with its riders held back, so
            // Reflect can decide first. Held back means NOT applied until the caller says so.
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker(maxHealth: 500);
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (new DebuffEffect(), Spec("debuff what=attackskills mag=30 secs=20 chance=1.0"))));

            var deferredTarget = TestCreatures.CreateDefender(maxHealth: 500);
            var held = DamageEvent.CalculateDamage(attacker, deferredTarget, attacker, deferOutgoingHitEffects: true);

            Assert.IsTrue(held.HasDamage && held.Damage > 0.0f, "harness: the overpowering attacker must land");
            Assert.AreEqual(0, deferredTarget.Biota.PropertiesEnchantmentRegistry.Count, "a deferred hit must not have applied its riders");

            held.ApplyDeferredOutgoingHitEffects();
            Assert.AreEqual(1, deferredTarget.Biota.PropertiesEnchantmentRegistry.Count, "applying the deferred riders lands them");

            // control: the default path still applies them inside CalculateDamage
            var immediateTarget = TestCreatures.CreateDefender(maxHealth: 500);
            DamageEvent.CalculateDamage(attacker, immediateTarget, attacker);
            Assert.AreEqual(1, immediateTarget.Biota.PropertiesEnchantmentRegistry.Count, "control: the default call applies its riders itself");
        }

        // ---- Thorns on a bounce: wards apply now that no latch is held -------------------------------------

        [TestMethod]
        public void Thorns_WardAbsorbs_UnlessAReflectLatchIsHeld()
        {
            // TryReflectProjectile used to call ApplyThornsReflect INSIDE the reflect latch, which makes
            // AbsorbMonsterEffectDamage skip every incoming-damage effect. ClassAbilityReflectAndPinTests pins
            // that it no longer claims the latch; this pins what that buys - the firer's ward absorbs the
            // Thorns hit - and, as the control, that a held latch really does bypass the ward.
            var expected = ThornsCloseRangeTests.ExpectedReflect(ThornsCloseRangeTests.SeededThornsModPlayer());
            Assert.IsTrue(expected >= 2, "harness: the seeded Thorns reflect must be large enough to split");

            var ward = expected / 2;

            Creature Warded(out Player player)
            {
                player = ThornsCloseRangeTests.SeededThornsModPlayer();
                var firer = TestCreatures.CreateDefender(maxHealth: 500 + expected * 2);
                firer.Health.Current = firer.Health.MaxValue;
                firer.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((new WardEffect(), Spec($"ward amount={ward} secs=300 on=spawn"))));
                firer.MonsterEffectHeartbeat();

                TestCreatures.AttachBarePhysics(player, 50.0f);
                TestCreatures.AttachBarePhysics(firer, 51.0f);
                return firer;
            }

            var free = Warded(out var freePlayer);
            var freeBefore = free.Health.Current;
            freePlayer.ApplyThornsReflect(free, DamageType.Pierce, 1.0);
            Assert.AreEqual(expected - ward, freeBefore - free.Health.Current, "no latch held: the ward absorbs its share of the Thorns hit");

            var latched = Warded(out var latchedPlayer);
            var latchedBefore = latched.Health.Current;

            Assert.IsTrue(Creature.TryEnterReflect(), "harness: the latch must be free");
            try
            {
                Assert.IsTrue(Creature.IsReflectResolving, "IsReflectResolving must read the held latch");
                latchedPlayer.ApplyThornsReflect(latched, DamageType.Pierce, 1.0);
            }
            finally
            {
                Creature.ExitReflect();
            }

            Assert.IsFalse(Creature.IsReflectResolving, "and read it released");
            Assert.AreEqual(expected, latchedBefore - latched.Health.Current, "control: inside a held latch the ward is skipped and the full hit lands");
        }

        // ---- source scans for the launch half, which this harness cannot drive -------------------------------

        [TestMethod]
        public void CosmeticProjectiles_AreRefusedAtBothCollisionHandlers()
        {
            // SpellProjectile: straight after the impact visuals, before anything resolves against the target
            var spell = MethodBody(Code("Source/ACE.Server/WorldObjects/SpellProjectile.cs"), "public override void OnCollideObject(WorldObject target)");

            Assert.IsTrue(spell.Contains("ProjectileImpact(); if (IsCosmeticProjectile) return; var creatureTarget = target as Creature;", StringComparison.Ordinal),
                "SpellProjectile.OnCollideObject must return for a cosmetic projectile immediately after ProjectileImpact()");

            // ProjectileCollisionHelper: before the target test and before any damage calculation
            var missile = MethodBody(Code("Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs"), "public static void OnCollideObject(WorldObject worldObject, WorldObject target)");

            var guard = missile.IndexOf("if (worldObject.IsCosmeticProjectile) { OnCollideEnvironment(worldObject); return; }", StringComparison.Ordinal);
            var targetTest = missile.IndexOf("if (worldObject.ProjectileTarget == null || worldObject.ProjectileTarget != target)", StringComparison.Ordinal);
            var damage = missile.IndexOf("DamageEvent.CalculateDamage(", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "ProjectileCollisionHelper.OnCollideObject must route a cosmetic projectile to the environment path");
            Assert.IsTrue(guard < targetTest && guard < damage, "the cosmetic guard must come before the target test and the damage calculation");
        }

        [TestMethod]
        public void CosmeticProjectiles_AreFlaggedBeforeTheyEnterTheWorld()
        {
            var launch = MethodBody(Code("Source/ACE.Server/WorldObjects/Creature_Missile.cs"), "public WorldObject LaunchProjectile(");

            var flag = launch.IndexOf("proj.IsCosmeticProjectile = cosmetic;", StringComparison.Ordinal);
            var noTarget = launch.IndexOf("proj.ProjectileTarget = cosmetic ? null : target;", StringComparison.Ordinal);
            var add = launch.IndexOf("LandblockManager.AddObject(proj)", StringComparison.Ordinal);

            Assert.IsTrue(flag >= 0 && noTarget >= 0, "LaunchProjectile must flag a cosmetic projectile and clear its ProjectileTarget");
            Assert.IsTrue(flag < add && noTarget < add, "both must happen BEFORE AddObject - an overlapping spawn collides during AddObject");

            var spell = MethodBody(Code("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs"), "public SpellProjectile LaunchCosmeticSpellProjectile(");

            var spellFlag = spell.IndexOf("sp.IsCosmeticProjectile = true;", StringComparison.Ordinal);
            var spellAdd = spell.IndexOf("LandblockManager.AddObject(sp)", StringComparison.Ordinal);

            Assert.IsTrue(spellFlag >= 0 && spellFlag < spellAdd, "LaunchCosmeticSpellProjectile must flag the bolt BEFORE AddObject");

            // the reflect launches its return arrow through the cosmetic overload
            var reflect = Code("Source/ACE.Server/WorldObjects/Player_ClassAbilityReflectAndPin.cs");
            Assert.IsTrue(reflect.Contains("LaunchProjectile(incoming.ProjectileLauncher, incoming, firer, origin, orientation, velocity, cosmetic: true)", StringComparison.Ordinal),
                "the return arrow must be launched cosmetic");
        }

        [TestMethod]
        public void BothSites_DecideTheReflectBeforeTheRidersRun()
        {
            // missile: riders held back for a shot at a player, applied only when it was NOT reflected
            var missile = Code("Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs");

            Assert.IsTrue(missile.Contains("DamageEvent.CalculateDamage(sourceCreature, targetCreature, worldObject, deferOutgoingHitEffects: targetPlayer != null);", StringComparison.Ordinal),
                "the monster missile site must defer its riders for a shot at a player");
            Assert.IsTrue(missile.Contains("if (reflectedProjectile) targetPlayer.LaunchReflectedMissile(sourceCreature, worldObject, damageEvent); else damageEvent.ApplyDeferredOutgoingHitEffects();", StringComparison.Ordinal),
                "a reflected shot bounces; any other shot applies its held-back riders");

            // spell: the reflect roll precedes the monster spell-hit dispatch, which runs only when not reflected
            var spell = MethodBody(Code("Source/ACE.Server/WorldObjects/SpellProjectile.cs"), "public override void OnCollideObject(WorldObject target)");

            var roll = spell.IndexOf("targetPlayer.TryReflectProjectile(", StringComparison.Ordinal);
            var bounce = spell.IndexOf("if (reflectedProjectile) { targetPlayer.LaunchReflectedSpell(sourceCreature, this, critical, spellDamage); } else {", StringComparison.Ordinal);
            var riders = spell.IndexOf("sourceCreature.ApplySpellHitMonsterEffects(creatureTarget, this, ref spellDamage);", StringComparison.Ordinal);

            Assert.IsTrue(roll >= 0 && bounce > roll, "the spell site must branch on the reflect");
            Assert.IsTrue(riders > bounce, "the monster spell-hit riders must run only in the not-reflected branch");

            // nothing of a bounced bolt reaches the player - its caster's on-hit item procs included
            Assert.IsTrue(spell.Contains("if (sourceCreature != null && ProjectileTarget != null && !FromProc && !reflectedProjectile)", StringComparison.Ordinal),
                "the caster's item procs must not fire against a player who reflected the bolt");
        }

        // ---- the arrival gate: a dead or departing reflector lands nothing ----------------------------------

        /// <summary>The harness player with a Health vital at <paramref name="health"/> (IsDead reads it).</summary>
        private static Player PlayerWithHealth(uint health)
        {
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();

            player.Biota.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();

            var vitals = new Dictionary<PropertyAttribute2nd, ACE.Server.WorldObjects.Entity.CreatureVital>();
            typeof(Creature).GetField(nameof(Creature.Vitals), BindingFlags.Public | BindingFlags.Instance).SetValue(player, vitals);
            vitals[PropertyAttribute2nd.MaxHealth] = new ACE.Server.WorldObjects.Entity.CreatureVital(player, PropertyAttribute2nd.MaxHealth);

            player.Health.Current = health;
            return player;
        }

        private static Landblock BareLandblock() => (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        [TestMethod]
        public void CanLandReflectBounce_DropsTheBounce_WhenTheReflectorIsDeadDyingOrTeleporting()
        {
            Assert.IsFalse(LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded, "harness: no parallel tick runs in tests");

            var landblock = BareLandblock();

            var firer = TestCreatures.CreateDefender(maxHealth: 500);
            firer.Health.Current = 400;
            firer.CurrentLandblock = landblock;

            // POSITIVE CONTROL: a live, present, non-teleporting reflector lands the bounce
            var live = PlayerWithHealth(100);
            live.CurrentLandblock = landblock;
            Assert.IsFalse(live.IsDead, "harness: the control player must be alive");
            Assert.IsTrue(live.CanLandReflectBounce(firer), "control: a live reflector in the world lands the bounce");

            var dead = PlayerWithHealth(0);
            dead.CurrentLandblock = landblock;
            Assert.IsTrue(dead.IsDead, "harness: zero health must read as dead");
            Assert.IsFalse(dead.CanLandReflectBounce(firer), "a reflector who died while the bounce flew lands nothing");

            var dying = PlayerWithHealth(100);
            dying.CurrentLandblock = landblock;
            dying.IsInDeathProcess = true;
            Assert.IsFalse(dying.CanLandReflectBounce(firer), "a reflector in the death process lands nothing");

            var porting = PlayerWithHealth(100);
            porting.CurrentLandblock = landblock;
            porting.Teleporting = true;
            Assert.IsFalse(porting.CanLandReflectBounce(firer), "a teleporting reflector lands nothing");

            // the firer-side gates still hold: a firer that left the world drops the bounce for a live reflector
            var gone = TestCreatures.CreateDefender(maxHealth: 500);
            Assert.IsFalse(live.CanLandReflectBounce(gone), "a firer with no landblock is out of the world");
        }

        // ---- the shield facing seam: angleSource replaces the attacker in the angle test only --------------

        private static uint nextShieldGuid = 0x7E180000;

        private static Creature At(Creature creature, float x, float y)
        {
            creature.Location = new Position(0x00010064, x, y, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            return creature;
        }

        /// <summary>A Melee-stance defender at (55, 45) with an equipped shield, facing +Y (CreatureCombatTests' frame).</summary>
        private static Creature ShieldedMeleeDefender()
        {
            var defender = At(TestCreatures.CreateDefender(shieldSkill: 400), 55.0f, 45.0f);

            var weenie = new Weenie
            {
                WeenieClassId = 991300,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.ArmorLevel, 100 },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ArmorModVsSlash, 1.0 } },
            };

            var shield = new Clothing(weenie, new ObjectGuid(nextShieldGuid++));
            defender.EquippedObjects[shield.Guid] = shield;

            typeof(Creature).GetProperty(nameof(Creature.CombatMode)).GetSetMethod(true).Invoke(defender, new object[] { CombatMode.Melee });

            return defender;
        }

        [TestMethod]
        public void GetShieldMod_AngleSource_DecidesTheFacing_AndNullMeansTheAttacker()
        {
            var defender = ShieldedMeleeDefender();

            var front = At(TestCreatures.CreateAttacker(), 55.0f, 60.0f);
            var behind = At(TestCreatures.CreateAttacker(), 55.0f, 30.0f);

            // POSITIVE CONTROL: an attacker in front, no angleSource - the shield bites
            var frontal = defender.GetShieldMod(front, DamageType.Slash, null);
            Assert.IsTrue(frontal < 1.0f, $"control: a frontal blow on a shielded melee defender is reduced, got {frontal}");

            // the SAME attacker, with the angle taken from a source behind the defender: no shield
            Assert.AreEqual(1.0f, defender.GetShieldMod(front, DamageType.Slash, null, angleSource: behind), 0.0f,
                "an angleSource behind the defender must put the blow outside the shield cone");

            // and the reverse: an attacker behind, with a frontal angleSource, is shielded exactly as the frontal blow
            Assert.AreEqual(1.0f, defender.GetShieldMod(behind, DamageType.Slash, null), 0.0f, "control: a blow from behind is unshielded");
            Assert.AreEqual(frontal, defender.GetShieldMod(behind, DamageType.Slash, null, angleSource: front), 1e-6f,
                "a frontal angleSource shields a blow whose attacker stands behind");

            // angleSource == null is exactly the attacker case
            Assert.AreEqual(defender.GetShieldMod(front, DamageType.Slash, null, angleSource: front),
                defender.GetShieldMod(front, DamageType.Slash, null, angleSource: null), 0.0f, "null angleSource == the attacker");
        }

        // ---- the caster's magic-damage ramp: every record multiplies ----------------------------------------

        [TestMethod]
        public void GetMonsterEffectRampMultiplier_TwoMagicDamageRamps_MultiplyTogether()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", true));

            var plain = TestCreatures.CreateDefender(maxHealth: 500);
            Assert.AreEqual(1.0, plain.GetMonsterEffectRampMultiplier(MonsterRampAxis.MagicDamage), 0.0, "control: no ramp record, no multiplier");

            var ramped = TestCreatures.CreateDefender(maxHealth: 500);
            ramped.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (new RampEffect(), Spec("ramp axis=magicdamage on=hit per=0.1 max=8 window=12")),
                (new RampEffect(), Spec("ramp axis=magicdamage on=hit per=0.05 max=8 window=12")),
                (new RampEffect(), Spec("ramp axis=attackspeed on=hit per=0.5 max=8 window=12"))));

            // no stacks held yet: every record contributes exactly 1.0
            Assert.AreEqual(1.0, ramped.GetMonsterEffectRampMultiplier(MonsterRampAxis.MagicDamage), 0.0, "control: unstacked ramps are neutral");

            for (var i = 0; i < ramped.MonsterEffectStates.Length; i++)
                ramped.MonsterEffectStates[i].Stacks = 2;

            // (1 + 2 * 0.1) * (1 + 2 * 0.05); the attackspeed record is a different axis and stays out
            Assert.AreEqual(1.2 * 1.1, ramped.GetMonsterEffectRampMultiplier(MonsterRampAxis.MagicDamage), 1e-5,
                "two magicdamage ramps give the product of both");
            Assert.AreEqual(2.0, ramped.GetMonsterEffectRampMultiplier(MonsterRampAxis.AttackSpeed), 1e-5, "control: the other axis reads only its own record");

            // the master switch off neutralises the whole read
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
            Assert.AreEqual(1.0, ramped.GetMonsterEffectRampMultiplier(MonsterRampAxis.MagicDamage), 0.0, "monster_effects_enabled off: no ramp");
        }

        // ---- the reflected spell: the caster's resistances and Invincible ------------------------------------

        private static uint nextBoltGuid = 0x7F280000;

        /// <summary>A fire bolt whose last CalculateDamage left <paramref name="attackDamage"/> as its attack half.</summary>
        private static SpellProjectile FireBolt(float attackDamage)
        {
            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spell = new ACE.Database.Models.World.Spell { EType = (uint)DamageType.Fire };

            var bolt = new SpellProjectile(new Weenie { WeenieClassId = 991400, WeenieType = WeenieType.ProjectileSpell }, new ObjectGuid(nextBoltGuid++));
            bolt.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            bolt.Spell = spell;

            typeof(SpellProjectile).GetProperty(nameof(SpellProjectile.ReflectAttackDamage)).SetValue(bolt, attackDamage);

            Assert.AreEqual(DamageType.Fire, bolt.Spell.DamageType, "harness: the bolt must read as fire");
            return bolt;
        }

        [TestMethod]
        public void CalculateReflectedDamage_AFireResistantCasterTakesLess_AndAnInvincibleOneNothing()
        {
            var open = TestCreatures.CreateDefender(maxHealth: 500);
            var resistant = TestCreatures.CreateDefender(maxHealth: 500);
            resistant.SetProperty(PropertyFloat.ResistFire, 0.5);

            var openDamage = FireBolt(100.0f).CalculateReflectedDamage(open, false, 1.0);
            var resistantDamage = FireBolt(100.0f).CalculateReflectedDamage(resistant, false, 1.0);

            // POSITIVE CONTROL: the open caster takes its own bolt's attack half
            Assert.IsTrue(openDamage > 0.0f, $"control: a caster with no fire resistance takes the bounce, got {openDamage}");

            Assert.IsTrue(resistantDamage < openDamage, $"the caster's own fire resistance must mitigate ({resistantDamage} vs {openDamage})");
            Assert.AreEqual(0.5f, resistantDamage / openDamage, 1e-4f, "a 0.5 fire resistance halves the bounce, nothing else differing");

            // the caster's magic-damage ramp scales the bounce
            Assert.AreEqual(openDamage * 1.5f, FireBolt(100.0f).CalculateReflectedDamage(open, false, 1.5), 1e-3f, "the ramp multiplier is applied");

            // Invincible: the same open caster, flagged, takes nothing (matching CalculateReflectedMissile)
            var invincible = TestCreatures.CreateDefender(maxHealth: 500);
            Assert.IsTrue(FireBolt(100.0f).CalculateReflectedDamage(invincible, false, 1.0) > 0.0f, "control: the twin before the flag");
            invincible.Invincible = true;
            Assert.AreEqual(0.0f, FireBolt(100.0f).CalculateReflectedDamage(invincible, false, 1.0), 0.0f, "an Invincible caster takes no reflected spell");
        }

        // ---- the reflected DoT ticks on the monster ------------------------------------------------------------

        [TestMethod]
        public void ReflectedDot_TicksOnTheMonster_NormalDotOnTheAttacker()
        {
            var dot = Code("Source/ACE.Server/MonsterEffects/Effects/DotEffect.cs");

            var normal = MethodBody(dot, "public void OnOutgoingHit(");
            var reflected = MethodBody(dot, "public void OnReflected(");
            var schedule = MethodBody(dot, "private static void ScheduleTick(");

            // control: the normal path still queues on its attacker
            Assert.IsTrue(normal.Contains("TryProc(attacker, attacker, defender, spec, ref state, tickActor: attacker);", StringComparison.Ordinal),
                "a normal DoT is unchanged: keyed under, sourced from and ticked on the attacker");

            // the reflected one keys and sources the player, but ticks on the monster it lands on
            Assert.IsTrue(reflected.Contains("TryProc(monster, reflector, monster, spec, ref state, tickActor: monster);", StringComparison.Ordinal),
                "a reflected DoT is ticked on the monster, the player kept as source and key");

            Assert.IsTrue(schedule.Contains("chain.AddAction(tickActor,", StringComparison.Ordinal), "the tick chain runs on the tick actor");
            Assert.IsTrue(schedule.Contains("RunTick(attacker, defender,", StringComparison.Ordinal), "the tick body still takes the attacker as source");
            Assert.IsFalse(schedule.Contains("chain.AddAction(attacker,", StringComparison.Ordinal), "no tick queues on the attacker directly any more");
        }

        // ---- source helpers ---------------------------------------------------------------------------------

        /// <summary>
        /// A source file with every full-line // comment removed and every whitespace run collapsed to one
        /// space, so prose cannot satisfy a search and a multi-line statement matches as one string.
        /// </summary>
        private static string Code(string repoRelativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, repoRelativePath)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");

            var lines = File.ReadAllLines(Path.Combine(dir.FullName, repoRelativePath));
            var code = string.Join(" ", Array.FindAll(lines, l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            return Regex.Replace(code, @"\s+", " ");
        }

        private static string MethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found - has it changed? {signature}");

            var open = source.IndexOf('{', start + signature.Length);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }
    }
}
