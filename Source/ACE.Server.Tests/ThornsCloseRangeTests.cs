using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Thorns as a close-range punish for every attack type (user ruling 2026-10-07, mirroring the monster
    /// reflect effect's on=hit ruling): melee, missile, direct magic and blocked hits all reflect, but only
    /// from an attacker within class_ability_thorns_max_range metres (edge-to-edge GetCylinderDistance).
    ///
    /// THESE DRIVE THE REAL Player.ApplyThornsReflect, not a copy of its gate. No test in this project can
    /// construct a live Player (its constructor reaches the auth database), so the defender is an
    /// uninitialized Player seeded with exactly what the reflect path reads: a Biota, an EquippedObjects
    /// dictionary holding a real shield GenericObject that carries the Thorns equipment mod, an empty
    /// class-ability cache, and a bare PhysicsObj. That is the RANK-0 EQUIPMENT-MOD shape of Thorns: the
    /// learned-rank shape additionally reads the Shield skill (Creature.Skills, a field initializer this
    /// harness cannot run), but the range gate both shapes share is the single check inside
    /// ApplyThornsReflect, which sits after the ownership check and before the shield lookup - so it is the
    /// same line of code for both.
    ///
    /// Every negative case has a positive control that differs from it in one input (the distance, the
    /// tunable, the origin), so a harness defect cannot pass a "no reflect" assertion for the wrong reason.
    /// </summary>
    [TestClass]
    public class ThornsCloseRangeTests
    {
        private static readonly string[] SeededDoubles = { "class_ability_thorns_max_range", "class_ability_thorns_percent_per_rank" };
        private static readonly string[] SeededBools = { "class_abilities_enabled", "equipment_mods_enabled" };

        private const int ShieldArmorLevel = 2000;

        private static uint nextGuid = 0x7E200000;
        private static uint nextWcid = 995000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            EnsurePlayerTypeInitializable();
        }

        /// <summary>
        /// MUST RUN BEFORE ANYTHING HERE TOUCHES A PLAYER-DECLARED MEMBER. Seeding classAbilityCache below is a
        /// reflection write to a field Player declares directly, which forces the CLR to run Player's static
        /// type initializer - and its static field initializers (Player_Location.cs, the PK arena spawn
        /// points) call DatabaseManager.World.GetCachedWeenie("<portal name>"), which falls through to a live
        /// World database and NREs. A type initializer that fails once fails for the rest of the process, so an
        /// unseeded run here would break every later Player-touching test in the assembly, not just these.
        /// Same remedy and same name list as MuleSummonTests.EnsureVendorConstructible (re-derived from a grep
        /// of GetCachedWeenie("...") over Player*.cs): each seeded weenie has no destination, so every call
        /// site's own ?? fallback supplies the position production would use.
        /// </summary>
        private static void EnsurePlayerTypeInitializable()
        {
            var weenieCache = (ConcurrentDictionary<uint, Weenie>)typeof(WorldDatabaseWithEntityCache)
                .GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(DatabaseManager.World);
            var nameCache = (ConcurrentDictionary<string, uint>)typeof(WorldDatabaseWithEntityCache)
                .GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(DatabaseManager.World);

            var wcid = 996900u;

            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                // never displace a seed another class already made for the same name
                if (nameCache.ContainsKey(name))
                    continue;

                weenieCache[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
                nameCache[name] = wcid;
                wcid++;
            }
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

        // ---- the tunable ----------------------------------------------------------------------------------

        [TestMethod]
        public void MaxRange_DefaultsToEightMetres()
        {
            Assert.AreEqual(8.0, DefaultPropertyManager.DefaultDoubleProperties["class_ability_thorns_max_range"].Item, 1e-12);
        }

        // ---- physical hits (Player.TakeDamage -> ApplyEquipmentModIncomingDamage -> ApplyThornsReflect) -------

        [TestMethod]
        public void Physical_WithinRangeReflects_BeyondRangeDoesNot()
        {
            // the same attacker type at two distances; the physical rank-0 call Player.TakeDamage makes
            var (nearPlayer, nearAttacker) = Pair(distance: 1.0f);
            var (edgePlayer, edgeAttacker) = Pair(distance: 7.5f);
            var (farPlayer, farAttacker) = Pair(distance: 9.0f);

            nearPlayer.ApplyEquipmentModIncomingDamage(nearAttacker, DamageType.Slash);
            edgePlayer.ApplyEquipmentModIncomingDamage(edgeAttacker, DamageType.Slash);
            farPlayer.ApplyEquipmentModIncomingDamage(farAttacker, DamageType.Slash);

            var expected = ExpectedReflect(nearPlayer);
            Assert.IsTrue(expected > 0, "harness control: the seeded shield and mod must produce a nonzero reflect");

            Assert.AreEqual(100u - expected, nearAttacker.Health.Current, "1 m: a melee-range hit reflects");
            Assert.AreEqual(100u - expected, edgeAttacker.Health.Current, "7.5 m: still inside the 8 m default");
            Assert.AreEqual(100u, farAttacker.Health.Current, "9 m: beyond the 8 m default, no reflect");
        }

        [TestMethod]
        public void Missile_FromLongRangeDoesNotReflect_ButPointBlankDoes()
        {
            // the deliberate nerf: an archer at 30 m used to be answered and must not be now
            var (sniped, archer) = Pair(distance: 30.0f);
            var (pointBlank, closeArcher) = Pair(distance: 3.0f);

            sniped.ApplyEquipmentModIncomingDamage(archer, DamageType.Pierce);
            pointBlank.ApplyEquipmentModIncomingDamage(closeArcher, DamageType.Pierce);

            Assert.AreEqual(100u, archer.Health.Current, "30 m: no reflect");
            Assert.AreEqual(100u - ExpectedReflect(pointBlank), closeArcher.Health.Current, "3 m: reflect");
        }

        [TestMethod]
        public void Range_FollowsTheLiveMaxRangeTunable()
        {
            var (player, attacker) = Pair(distance: 12.0f);

            // CONTROL: 12 m against the 8 m default
            player.ApplyEquipmentModIncomingDamage(attacker, DamageType.Slash);
            Assert.AreEqual(100u, attacker.Health.Current);

            Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_thorns_max_range", 15.0));

            player.ApplyEquipmentModIncomingDamage(attacker, DamageType.Slash);
            Assert.AreEqual(100u - ExpectedReflect(player), attacker.Health.Current, "the same 12 m hit reflects once the range is 15 m");
        }

        [TestMethod]
        public void Range_AttackerWithoutPhysicsObjFailsClosed()
        {
            var (player, attacker) = Pair(distance: 1.0f);
            var (controlPlayer, controlAttacker) = Pair(distance: 1.0f);

            // the attacker is taken out of the world; the control keeps its placement
            typeof(WorldObject).GetProperty(nameof(WorldObject.PhysicsObj)).SetValue(attacker, null);

            player.ApplyEquipmentModIncomingDamage(attacker, DamageType.Slash);
            controlPlayer.ApplyEquipmentModIncomingDamage(controlAttacker, DamageType.Slash);

            Assert.AreEqual(100u, attacker.Health.Current, "no PhysicsObj: fail closed");
            Assert.AreEqual(100u - ExpectedReflect(controlPlayer), controlAttacker.Health.Current, "control: the placed attacker is reflected");
        }

        // ---- the shield-block path (OnClassAbilityAttackAvoided -> ApplyThornsReflect(attacker, type, 1.0)) ----

        [TestMethod]
        public void Block_OutOfRangeDoesNotReflect_InRangeDoes()
        {
            // OnClassAbilityAttackAvoided's Block branch makes exactly this call; a blocked missile from range
            // must not be answered any more than a landed one is. (The branch itself also sends chat and rolls
            // Kinetic Charge / Pocket Sand, which this harness cannot host - the reflect is the call below.)
            var (farBlocker, farAttacker) = Pair(distance: 20.0f);
            var (nearBlocker, nearAttacker) = Pair(distance: 2.0f);

            farBlocker.ApplyThornsReflect(farAttacker, DamageType.Pierce, 1.0);
            nearBlocker.ApplyThornsReflect(nearAttacker, DamageType.Pierce, 1.0);

            Assert.AreEqual(100u, farAttacker.Health.Current, "a block at 20 m reflects nothing");
            Assert.AreEqual(100u - ExpectedReflect(nearBlocker), nearAttacker.Health.Current, "a block at 2 m reflects");
        }

        // ---- direct magic hits (SpellProjectile / Harm / Drain -> ApplyThornsOnMagicHit) ------------------

        [TestMethod]
        public void Magic_DirectHitWithinRangeReflects_BeyondRangeDoesNot()
        {
            var (nearPlayer, nearCaster) = Pair(distance: 4.0f);
            var (farPlayer, farCaster) = Pair(distance: 25.0f);

            nearPlayer.ApplyThornsOnMagicHit(nearCaster, DamageType.Fire, IncomingDamageOrigin.DirectHit);
            farPlayer.ApplyThornsOnMagicHit(farCaster, DamageType.Fire, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u - ExpectedReflect(nearPlayer), nearCaster.Health.Current, "a war bolt from 4 m reflects");
            Assert.AreEqual(100u, farCaster.Health.Current, "a war bolt from 25 m does not");
        }

        [TestMethod]
        public void Magic_SecondaryOriginNeverReflects_EvenAtPointBlank()
        {
            var (procPlayer, procCaster) = Pair(distance: 1.0f);
            var (controlPlayer, controlCaster) = Pair(distance: 1.0f);

            procPlayer.ApplyThornsOnMagicHit(procCaster, DamageType.Fire, IncomingDamageOrigin.Secondary);
            controlPlayer.ApplyThornsOnMagicHit(controlCaster, DamageType.Fire, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u, procCaster.Health.Current, "a proc / splash / cascade projectile is Secondary");
            Assert.AreEqual(100u - ExpectedReflect(controlPlayer), controlCaster.Health.Current, "control: the same hit as a DirectHit reflects");
        }

        [TestMethod]
        public void MagicReflectDamageType_PassesSingleRealTypes_FallsBackToBludgeon()
        {
            foreach (var real in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Cold, DamageType.Fire, DamageType.Acid, DamageType.Electric, DamageType.Nether })
                Assert.AreEqual(real, Player.GetThornsMagicReflectDamageType(real), real.ToString());

            foreach (var vital in new[] { DamageType.Health, DamageType.Stamina, DamageType.Mana, DamageType.Undef, DamageType.Base, DamageType.Fire | DamageType.Cold, DamageType.Fire | DamageType.Health })
                Assert.AreEqual(DamageType.Bludgeon, Player.GetThornsMagicReflectDamageType(vital), vital.ToString());
        }

        [TestMethod]
        public void Magic_SelfSourceNeverReflects()
        {
            // A self-cast Harm/Drain names the player as its own source. The player is in range of itself
            // (distance 0) and carries the shield and mod, so only the self filter stops the reflect. This
            // harness's Player has no vitals, so a reflect that got past the filter reaches the player's own
            // Creature.TakeDamage and throws there - which is the failure this test would report.
            var (player, _) = Pair(distance: 1.0f);

            try
            {
                player.ApplyThornsOnMagicHit(player, DamageType.Fire, IncomingDamageOrigin.DirectHit);
            }
            catch (Exception ex)
            {
                Assert.Fail($"the self filter let a reflect through to the player's own TakeDamage: {ex.GetType().Name}");
            }
        }

        [TestMethod]
        public void Magic_DeadAttackerNeverReflects()
        {
            // IsDead derives from Health.Current <= 0. A dead caster cannot show a health drop (the vital
            // clamps at 0), so the observable is its death sequence: a reflect reaching Creature.TakeDamage on
            // a 0-health creature runs OnDeath, which latches the private onDeathEntered flag first thing.
            // The dead check is defended twice by design (ApplyThornsOnMagicHit's attacker filter and
            // ApplyThornsReflect's own entry guard), so only removing BOTH makes this fail.
            var (player, caster) = Pair(distance: 1.0f);
            caster.Health.Current = 0;
            Assert.IsTrue(caster.IsDead, "harness: IsDead must follow Health.Current");

            var onDeathEntered = typeof(Creature).GetField("onDeathEntered", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(onDeathEntered, "Creature.onDeathEntered was not found by reflection - has it been renamed?");

            try
            {
                player.ApplyThornsOnMagicHit(caster, DamageType.Fire, IncomingDamageOrigin.DirectHit);
            }
            catch (Exception ex)
            {
                Assert.Fail($"a reflect reached the dead caster: {ex.GetType().Name}");
            }

            Assert.IsFalse((bool)onDeathEntered.GetValue(caster), "a reflect reached the dead caster's death sequence");
        }

        // ---- wiring: the three direct magic sites call Thorns after their writes; the DoT site does not --------

        /// <summary>
        /// Source-binding check on the three call sites, which this harness cannot drive (each needs a live
        /// Player target with vitals). Full-line comments are stripped and whitespace is collapsed before
        /// searching, and each site's EXACT guarded statement is asserted - the gate and the origin argument
        /// included - between two anchors that pin it to the right place, with exactly one Thorns call per
        /// method. So a commented-out call, a dropped gate, a hard-coded origin, a call moved out of its
        /// branch, or (Transfer) a call put back ahead of the caster's heal-back all fail here.
        /// </summary>
        [TestMethod]
        public void Wiring_EachDirectMagicSiteCallsThornsInTheRightPlace()
        {
            var spellProjectile = ReadCode("Source/ACE.Server/WorldObjects/SpellProjectile.cs");
            var magic = ReadCode("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");

            // SpellProjectile.DamageTarget: inside the Health branch (after its vital write, before the
            // post-branch reassignment of `amount`), passing the projectile's own origin
            var damageTarget = MethodBody(spellProjectile, "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)");
            AssertSingleThornsCall(damageTarget, "SpellProjectile.DamageTarget");
            AssertBetween(damageTarget, "SpellProjectile.DamageTarget",
                after: "amount = (uint)-target.UpdateVitalDelta(target.Health, (int)-Math.Round(damage)); target.DamageHistory.Add(ProjectileSource, Spell.DamageType, amount);",
                statement: "targetPlayer?.ApplyThornsOnMagicHit(ProjectileSource, Spell.DamageType, IncomingDamageOrigin);",
                before: "amount = (uint)Math.Round(damage);");

            // HandleCastSpell_Boost (Harm): gated on the authored harmful-Health direction, proc casts Secondary
            var boost = MethodBody(magic, "private void HandleCastSpell_Boost(Spell spell, Creature targetCreature, bool fromProc = false)");
            AssertSingleThornsCall(boost, "HandleCastSpell_Boost");
            AssertBetween(boost, "HandleCastSpell_Boost",
                after: "targetCreature.DamageHistory.Add(this, DamageType.Health, (uint)-boost);",
                statement: "if (isHarmfulLifeBoost && targetCreature is Player thornsTarget) thornsTarget.ApplyThornsOnMagicHit(this, spell.DamageType, fromProc ? IncomingDamageOrigin.Secondary : IncomingDamageOrigin.DirectHit);",
                before: "HandleBoostTransferDeath(creature, targetCreature);");

            // HandleCastSpell_Transfer (Drain Health): the target is captured in the Health case of the SOURCE
            // switch, and Thorns runs only after the DESTINATION switch has healed the caster - a reflect that
            // killed the caster before the heal-back would be undone by it
            var transfer = MethodBody(magic, "private void HandleCastSpell_Transfer(Spell spell, Creature targetCreature, bool isSecondaryStrike = false)");
            AssertSingleThornsCall(transfer, "HandleCastSpell_Transfer");
            AssertBetween(transfer, "HandleCastSpell_Transfer (capture)",
                after: "transferSource.DamageHistory.Add(this, DamageType.Health, srcVitalChange);",
                statement: "thornsDrainTarget = transferSource as Player;",
                before: "switch (spell.Destination)");
            AssertBetween(transfer, "HandleCastSpell_Transfer (call)",
                after: "destVitalChange = (uint)destination.UpdateVitalDelta(destination.Health, destVitalChange);",
                statement: "if (thornsDrainTarget != null) thornsDrainTarget.ApplyThornsOnMagicHit(this, spell.DamageType, IncomingDamageOrigin.DirectHit);",
                before: "HandleBoostTransferDeath(creature, targetCreature);");

            // the DoT tick carries no attacker and must never reach Thorns
            var enchantments = ReadCode("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");
            Assert.IsFalse(enchantments.Contains("ApplyThornsOnMagicHit", StringComparison.Ordinal), "EnchantmentManager (DoT ticks) must not call Thorns");
        }

        // ---- harness ----------------------------------------------------------------------------------------

        /// <summary>
        /// A Thorns-mod player at x=50 wearing a shield of <see cref="ShieldArmorLevel"/> with a full-potency
        /// Thorns equipment mod, and a monster attacker at 100 of 500 health standing
        /// <paramref name="distance"/> metres away along x. Both carry a bare PhysicsObj (radius 0), so the
        /// cylinder distance is exactly the centre distance.
        /// </summary>
        private static (Player player, Creature attacker) Pair(float distance)
        {
            var player = SeededThornsModPlayer();
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            attacker.Health.Current = 100;

            TestCreatures.AttachBarePhysics(player, 50.0f);
            TestCreatures.AttachBarePhysics(attacker, 50.0f + distance);

            return (player, attacker);
        }

        private static uint ExpectedReflect(Player player) =>
            ThornsAbility.ComputeReflectDamage(ShieldArmorLevel, 0,
                PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item, 0.0,
                player.GetEquippedModValue(EquipmentModId.Thorns));

        private static Player SeededThornsModPlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetField(typeof(WorldObject), player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetField(typeof(WorldObject), player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetField(typeof(Player), player, "classAbilityCache", new Dictionary<ClassAbilityId, int>());

            var shield = CreateThornsShield();
            var equipped = new Dictionary<ObjectGuid, WorldObject> { { shield.Guid, shield } };
            SetField(typeof(Creature), player, "<EquippedObjects>k__BackingField", equipped);

            Assert.AreSame(shield, player.GetEquippedShield(), "harness: the seeded shield must be what GetEquippedShield finds");

            return player;
        }

        private static WorldObject CreateThornsShield()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                    { PropertyInt.ArmorLevel, ShieldArmorLevel },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.GearModThorns, 1.0 },
                },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static void SetField(Type owner, object target, string name, object value)
        {
            var field = owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        /// <summary>
        /// A source file with every full-line // comment removed and every whitespace run collapsed to one
        /// space, so a commented-out statement cannot satisfy a search and a multi-line statement can be
        /// matched as one string. Only FULL-line comments go: a trailing // after code is kept, because a
        /// naive strip would also cut string literals that contain "//".
        /// </summary>
        private static string ReadCode(string repoRelativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, repoRelativePath)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");

            var lines = File.ReadAllLines(Path.Combine(dir.FullName, repoRelativePath));
            var code = string.Join(" ", Array.FindAll(lines, l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            return Regex.Replace(code, @"\s+", " ");
        }

        /// <summary>The text of one method, from its exact signature to its matching closing brace.</summary>
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

        private static void AssertSingleThornsCall(string body, string site)
        {
            var count = Regex.Matches(body, @"ApplyThornsOnMagicHit\(").Count;
            Assert.AreEqual(1, count, $"{site}: expected exactly one Thorns call, found {count}");
        }

        private static void AssertBetween(string body, string site, string after, string statement, string before)
        {
            var afterAt = body.IndexOf(after, StringComparison.Ordinal);
            Assert.IsTrue(afterAt >= 0, $"{site}: anchor not found - has it changed? {after}");

            var statementAt = body.IndexOf(statement, StringComparison.Ordinal);
            Assert.IsTrue(statementAt >= 0, $"{site}: the guarded Thorns statement is missing or has changed: {statement}");

            var beforeAt = body.IndexOf(before, afterAt, StringComparison.Ordinal);
            Assert.IsTrue(beforeAt >= 0, $"{site}: closing anchor not found after the opening one - has it changed? {before}");

            Assert.IsTrue(statementAt > afterAt, $"{site}: the Thorns statement must come after [{after}]");
            Assert.IsTrue(statementAt < beforeAt, $"{site}: the Thorns statement must come before [{before}]");
        }
    }
}
