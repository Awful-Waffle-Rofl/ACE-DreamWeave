using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Hunter's Echo owner parity (2026-11-11): the Echo shows its owner's level, attributes, vitals and defenses
    /// (CombatPet.MatchHuntersEchoStats, driven here with two plain creatures on the synthetic TestGameTables
    /// formulas), shoots at the owner's last-shot bar and reload speed (HuntersEchoMath.ShotAccuracyLevel /
    /// ShotAnimSpeed / ShotCycleSeconds, HuntersEchoShot.Resolve), and reports its hits as its own
    /// (HuntersEchoMath.FormatEchoHitMessage). The live wiring is pinned by HuntersEchoWiringSourceScanTests. No
    /// PropertyManager key is read here.
    /// </summary>
    [TestClass]
    public class HuntersEchoParityTests
    {
        private static uint nextGuid = 0x7F00E501;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        // ------------------------------------------------------------ the spawn weapon (owner ruling 2026-11-11)

        [TestMethod]
        public void VolleyAmmo_PrefersTheSnapshotStack_ThenTheEquippedOne_ElseNone()
        {
            var snapshot = new object();
            var equipped = new object();

            Assert.AreSame(snapshot, HuntersEchoMath.SelectVolleyAmmo(snapshot, true, equipped, true), "the snapshot stack while usable");
            Assert.AreSame(equipped, HuntersEchoMath.SelectVolleyAmmo(snapshot, false, equipped, true), "snapshot shot empty or gone: the equipped stack");
            Assert.AreSame(equipped, HuntersEchoMath.SelectVolleyAmmo(null, true, equipped, true), "no snapshot stack: the equipped stack");
            Assert.IsNull(HuntersEchoMath.SelectVolleyAmmo(snapshot, false, equipped, false), "neither usable: no volley");
            Assert.IsNull(HuntersEchoMath.SelectVolleyAmmo<object>(null, true, null, true), "neither present: no volley");
        }

        [TestMethod]
        public void Weapons_FromProjectile_ReadsTheLauncherAndAmmoTheArrowWasFiredWith()
        {
            var bow = NewItem(CombatStyle.Bow);
            var arrows = NewItem(null);

            var launched = NewItem(null);
            launched.ProjectileLauncher = bow;
            launched.ProjectileAmmo = arrows;

            var fromArrow = HuntersEchoWeapons.FromProjectile(launched);
            Assert.AreSame(bow, fromArrow.Weapon);
            Assert.AreSame(bow, fromArrow.Launcher);
            Assert.AreSame(arrows, fromArrow.AmmoSlot);
            Assert.AreSame(arrows, fromArrow.Ammo, "a launcher fires its ammo slot");

            var dart = NewItem(null);
            var thrown = NewItem(null);
            thrown.ProjectileAmmo = dart;

            var fromThrown = HuntersEchoWeapons.FromProjectile(thrown);
            Assert.AreSame(dart, fromThrown.Weapon, "a thrown weapon is its own weapon");
            Assert.IsNull(fromThrown.Launcher);
            Assert.IsNull(fromThrown.AmmoSlot);
            Assert.AreSame(dart, fromThrown.Ammo, "a thrown weapon is its own ammo");

            Assert.IsNull(HuntersEchoWeapons.FromProjectile(NewItem(null)), "a projectile carrying neither");
            Assert.IsNull(HuntersEchoWeapons.FromProjectile(null));
        }

        [TestMethod]
        public void Weapons_WithAmmo_ReplacesOnlyTheAmmoSlot()
        {
            var bow = NewItem(CombatStyle.Bow);
            var oldArrows = NewItem(null);
            var newArrows = NewItem(null);

            var set = new HuntersEchoWeapons(bow, bow, oldArrows).WithAmmo(newArrows);

            Assert.AreSame(bow, set.Weapon);
            Assert.AreSame(bow, set.Launcher);
            Assert.AreSame(newArrows, set.AmmoSlot);
            Assert.AreSame(newArrows, set.Ammo);
        }

        [TestMethod]
        public void WeaponScope_AnswersTheGetters_ForItsCreatureOnItsThreadOnly_AndRestores()
        {
            var owner = NewOwner();
            var other = NewOwner();
            var bow = NewItem(CombatStyle.Bow);
            var arrows = NewItem(null);
            var set = new HuntersEchoWeapons(bow, bow, arrows);

            Assert.IsNull(owner.GetEquippedMissileWeapon(), "POSITIVE CONTROL: nothing wielded, nothing returned");

            var previous = owner.EnterHuntersEchoWeaponScope(set);

            try
            {
                Assert.AreSame(bow, owner.GetEquippedWeapon());
                Assert.AreSame(bow, owner.GetEquippedMissileWeapon());
                Assert.AreSame(bow, owner.GetEquippedMissileLauncher());
                Assert.AreSame(arrows, owner.GetEquippedAmmo());
                Assert.IsNull(owner.GetEquippedMeleeWeapon());
                Assert.IsNull(owner.GetDualWieldWeapon());

                Assert.IsNull(other.HuntersEchoWeaponScope, "another creature on the same thread is not scoped");
                Assert.IsNull(other.GetEquippedMissileWeapon());

                WorldObject seenOnAnotherThread = bow;
                var thread = new System.Threading.Thread(() => seenOnAnotherThread = owner.GetEquippedMissileWeapon());
                thread.Start();
                thread.Join();

                Assert.IsNull(seenOnAnotherThread, "the owner read from another thread is not scoped");
            }
            finally
            {
                Creature.RestoreHuntersEchoWeaponScope(previous);
            }

            Assert.IsNull(owner.HuntersEchoWeaponScope, "restored");
            Assert.IsNull(owner.GetEquippedMissileWeapon(), "restored: back to the real (empty) equipment");
        }

        [TestMethod]
        public void WeaponScope_Suspended_ReadsTheRealEquipment_ThenRestoresTheSet()
        {
            var owner = NewOwner();
            var bow = NewItem(CombatStyle.Bow);
            var set = new HuntersEchoWeapons(bow, bow, null);

            var outer = owner.EnterHuntersEchoWeaponScope(set);

            try
            {
                Assert.AreSame(bow, owner.GetEquippedMissileWeapon(), "POSITIVE CONTROL: the set answers");

                // the raw form the helper uses: Enter(null) nested inside Enter(set)
                var inner = owner.EnterHuntersEchoWeaponScope(null);
                Assert.IsNull(owner.HuntersEchoWeaponScope, "suspended");
                Assert.IsNull(owner.GetEquippedMissileWeapon(), "suspended: the real (empty) equipment");
                Creature.RestoreHuntersEchoWeaponScope(inner);
                Assert.AreSame(set, owner.HuntersEchoWeaponScope, "the set is back after the suspend");

                // the helper
                Assert.IsNull(owner.WithoutHuntersEchoWeaponScope(owner.GetEquippedMissileWeapon), "the helper suspends");
                Assert.AreSame(set, owner.HuntersEchoWeaponScope, "the helper restores the set");

                Assert.ThrowsExactly<System.InvalidOperationException>(() =>
                    owner.WithoutHuntersEchoWeaponScope<bool>(() => throw new System.InvalidOperationException()));
                Assert.AreSame(set, owner.HuntersEchoWeaponScope, "restored even when the action throws");
            }
            finally
            {
                Creature.RestoreHuntersEchoWeaponScope(outer);
            }

            Assert.IsNull(owner.HuntersEchoWeaponScope);
        }

        [TestMethod]
        public void Retaliation_OutsideAnEchoHit_IsTheAttacker_InsideOne_NeverTheOwner()
        {
            var owner = NewOwner();
            var monster = NewOwner();

            Assert.AreSame(owner, Creature.ResolveHuntersEchoRetaliationTarget(owner, monster), "no Echo hit: the attacker, unchanged");
            Assert.IsNull(Creature.ResolveHuntersEchoRetaliationTarget(null, monster));

            var bow = NewItem(CombatStyle.Bow);
            var notAnEcho = NewEcho();   // a CombatPet never initialised as an Echo (IsHuntersEcho false)

            foreach (var shooter in new WorldObject[] { null, monster, notAnEcho })
            {
                var previous = owner.EnterHuntersEchoWeaponScope(new HuntersEchoWeapons(bow, bow, null, shooter));

                try
                {
                    Assert.IsNull(Creature.ResolveHuntersEchoRetaliationTarget(owner, monster),
                        $"inside an Echo hit with shooter [{shooter?.Name ?? "null"}]: dropped, never the owner");
                }
                finally
                {
                    Creature.RestoreHuntersEchoWeaponScope(previous);
                }
            }

            Assert.AreSame(monster, Creature.ResolveHuntersEchoRetaliationTarget(monster, owner), "an unscoped creature is unchanged");
        }

        [TestMethod]
        public void Weapons_CarryTheirShooter_ThroughFromProjectileAndWithAmmo()
        {
            var echo = NewEcho();
            var bow = NewItem(CombatStyle.Bow);
            var arrows = NewItem(null);

            var launched = NewItem(null);
            launched.ProjectileLauncher = bow;
            launched.ProjectileAmmo = arrows;
            launched.ProjectileSource = echo;

            Assert.AreSame(echo, HuntersEchoWeapons.FromProjectile(launched).Shooter, "an arrow's shooter is its source");

            var thrown = NewItem(null);
            thrown.ProjectileAmmo = arrows;
            thrown.ProjectileSource = echo;
            Assert.AreSame(echo, HuntersEchoWeapons.FromProjectile(thrown).Shooter);

            var set = new HuntersEchoWeapons(bow, bow, arrows).WithShooter(echo);
            Assert.AreSame(echo, set.Shooter);
            Assert.AreSame(bow, set.Weapon);
            Assert.AreSame(arrows, set.AmmoSlot);
            Assert.AreSame(echo, set.WithAmmo(NewItem(null)).Shooter, "a volley's ammo swap keeps the shooter");
        }

        [TestMethod]
        public void LongDrawOrigin_IsTheEchosLaunchSpot_ForAnEchoArrow_OnlyForOne()
        {
            var launchSpot = new Position(0x12340001, 10.0f, 20.0f, 30.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            var echoArrow = NewItem(null);
            echoArrow.HuntersEchoLaunchOrigin = launchSpot;

            Assert.AreSame(launchSpot, ACE.Server.ClassAbilities.Abilities.LongDrawAbility.ShotOrigin(null, new ACE.Server.Entity.DamageEvent { DamageSource = echoArrow }),
                "an Echo arrow measures from where the Echo stood");

            var plainArrow = NewItem(null);
            Assert.IsNull(ACE.Server.ClassAbilities.Abilities.LongDrawAbility.ShotOrigin(null, new ACE.Server.Entity.DamageEvent { DamageSource = plainArrow }),
                "any other arrow falls back to the attacker's location (here none)");
            Assert.IsNull(ACE.Server.ClassAbilities.Abilities.LongDrawAbility.ShotOrigin(null, (ACE.Server.Entity.DamageEvent)null));
        }

        [TestMethod]
        public void WeaponGoneRecheck_IsOneSecond()
        {
            Assert.AreEqual(1.0, HuntersEchoMath.WeaponGoneRecheckSeconds);
        }

        [TestMethod]
        public void WeaponScope_Nested_RestoresTheOuterScope()
        {
            var owner = NewOwner();
            var outer = new HuntersEchoWeapons(NewItem(CombatStyle.Bow), null, null);
            var inner = new HuntersEchoWeapons(NewItem(CombatStyle.Bow), null, null);

            var first = owner.EnterHuntersEchoWeaponScope(outer);
            var second = owner.EnterHuntersEchoWeaponScope(inner);

            Assert.AreSame(inner, owner.HuntersEchoWeaponScope);

            Creature.RestoreHuntersEchoWeaponScope(second);
            Assert.AreSame(outer, owner.HuntersEchoWeaponScope);

            Creature.RestoreHuntersEchoWeaponScope(first);
            Assert.IsNull(owner.HuntersEchoWeaponScope);
        }

        // ------------------------------------------------------------ the shot

        [TestMethod]
        public void ShotAccuracy_IsTheOwnersLastBar_ClampedToTheClientRange()
        {
            Assert.AreEqual(0.37f, HuntersEchoMath.ShotAccuracyLevel(0.37f));
            Assert.AreEqual(0.0f, HuntersEchoMath.ShotAccuracyLevel(0.0f), "a zero bar (fastest fire) is kept, not read as none");
            Assert.AreEqual(0.0f, HuntersEchoMath.ShotAccuracyLevel(-0.5f));
            Assert.AreEqual(1.0f, HuntersEchoMath.ShotAccuracyLevel(1.7f));
        }

        [TestMethod]
        public void ShotAccuracy_WithNoShotThisSession_IsAFullBar()
        {
            Assert.AreEqual(1.0f, HuntersEchoMath.DefaultShotAccuracyLevel);
            Assert.AreEqual(HuntersEchoMath.DefaultShotAccuracyLevel, HuntersEchoMath.ShotAccuracyLevel(null));
            Assert.AreEqual(HuntersEchoMath.DefaultShotAccuracyLevel, HuntersEchoMath.ShotAccuracyLevel(float.NaN));
            Assert.AreEqual(HuntersEchoMath.DefaultShotAccuracyLevel, HuntersEchoMath.ShotAccuracyLevel(float.PositiveInfinity));
        }

        [TestMethod]
        public void TheFullBarFallback_RefillTerm_EqualsTheOldMonsterDelay()
        {
            // only the refill TERM: the full-bar refill (1.0 s) equals the Creature.MissileDelay the Echo waited after a
            // shot before it took its owner's cadence. The whole cycle also reloads at the owner's speed, so a fallback
            // Echo can still cycle faster than before - by design, it matches the owner.
            Assert.AreEqual(Creature.MissileDelay, HuntersEchoMath.DefaultShotAccuracyLevel);
        }

        [TestMethod]
        public void ShotAnimSpeed_IsTheLastShots_ElseTheLiveSpeed_ElseOne()
        {
            Assert.AreEqual(1.85f, HuntersEchoMath.ShotAnimSpeed(1.85f, 1.2f), "the owner's last shot wins");
            Assert.AreEqual(1.2f, HuntersEchoMath.ShotAnimSpeed(null, 1.2f), "no shot this session: the live speed for the bow held now");
            Assert.AreEqual(1.2f, HuntersEchoMath.ShotAnimSpeed(0.0f, 1.2f));
            Assert.AreEqual(1.2f, HuntersEchoMath.ShotAnimSpeed(float.NaN, 1.2f));
            Assert.AreEqual(1.0f, HuntersEchoMath.ShotAnimSpeed(null, -3.0f));
            Assert.AreEqual(1.0f, HuntersEchoMath.ShotAnimSpeed(null, float.PositiveInfinity));
        }

        [TestMethod]
        public void Resolve_TakesTheOwnersShot_ElseTheFallback()
        {
            var fromShot = HuntersEchoShot.Resolve("Awfulwaffle's Echo", 0.4f, 1.6f, 0.0f);
            Assert.AreEqual("Awfulwaffle's Echo", fromShot.EchoName);
            Assert.AreEqual(0.4f, fromShot.AccuracyLevel);
            Assert.AreEqual(1.6f, fromShot.AnimSpeed);

            var fallback = HuntersEchoShot.Resolve("Awfulwaffle's Echo", null, null, 1.3f);
            Assert.AreEqual(1.0f, fallback.AccuracyLevel);
            Assert.AreEqual(1.3f, fallback.AnimSpeed);
        }

        [TestMethod]
        public void ShotCycle_IsThePlayersRepeatCycle_TermForTerm()
        {
            // aim 0.5 + reload 0.8 + ready 0.1 + link 0.2 + the bar's refill (AccuracyLevel seconds) 0.6
            Assert.AreEqual(2.2, HuntersEchoMath.ShotCycleSeconds(0.5f, 0.8f, 0.1f, 0.2f, 0.6f), 1e-6);

            // a zero bar refills at once
            Assert.AreEqual(1.6, HuntersEchoMath.ShotCycleSeconds(0.5f, 0.8f, 0.1f, 0.2f, 0.0f), 1e-6);

            // a bad term contributes nothing rather than poisoning the schedule
            Assert.AreEqual(1.0, HuntersEchoMath.ShotCycleSeconds(-1.0f, float.NaN, 0.0f, float.PositiveInfinity, 1.0f), 1e-6);
        }

        // ------------------------------------------------------------ the hit line

        [TestMethod]
        public void HitLine_NamesTheEcho()
        {
            Assert.AreEqual("Awfulwaffle's Echo deals 143 points of fire damage to Drudge Ravener!",
                HuntersEchoMath.FormatEchoHitMessage("Awfulwaffle's Echo", 143, DamageType.Fire, "Drudge Ravener", false, AttackConditions.None));

            Assert.AreEqual("Awfulwaffle's Echo deals 88 points of piercing damage to Drudge Ravener!",
                HuntersEchoMath.FormatEchoHitMessage("Awfulwaffle's Echo", 88, DamageType.Pierce, "Drudge Ravener", false, AttackConditions.None));
        }

        [TestMethod]
        public void HitLine_CarriesTheServerHitLinePrefixes_InTheirOrder()
        {
            Assert.AreEqual("Critical hit! Sneak Attack! Awfulwaffle's Echo deals 300 points of fire damage to Drudge Ravener!",
                HuntersEchoMath.FormatEchoHitMessage("Awfulwaffle's Echo", 300, DamageType.Fire, "Drudge Ravener", true, AttackConditions.SneakAttack));

            Assert.AreEqual("Overpower! Recklessness! Awfulwaffle's Echo deals 12 points of cold damage to Drudge Ravener!",
                HuntersEchoMath.FormatEchoHitMessage("Awfulwaffle's Echo", 12, DamageType.Cold, "Drudge Ravener", false, AttackConditions.Overpower | AttackConditions.Recklessness));

            Assert.AreEqual("Awfulwaffle's Echo deals 40 points of acid damage to Drudge Ravener! Your critical hit was avoided with their augmentation!",
                HuntersEchoMath.FormatEchoHitMessage("Awfulwaffle's Echo", 40, DamageType.Acid, "Drudge Ravener", false, AttackConditions.CriticalProtectionAugmentation));
        }

        [TestMethod]
        public void HitLine_ATypeWithNoSingleName_DropsTheTypeWord()
        {
            Assert.AreEqual("Awfulwaffle's Echo deals 5 points of damage to Drudge Ravener!",
                HuntersEchoMath.FormatEchoHitMessage("Awfulwaffle's Echo", 5, DamageType.Slash | DamageType.Pierce, "Drudge Ravener", false, AttackConditions.None));
        }

        // ------------------------------------------------------------ the stats

        private static readonly PropertyAttribute[] Six =
        {
            PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination,
            PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        private static readonly Skill[] Defenses = { Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense };

        /// <summary>A plain item; with a combat style of Bow it is an ammo launcher (WorldObject.IsAmmoLauncher).</summary>
        private static WorldObject NewItem(CombatStyle? style)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 0x7F00E5FF,
                ClassName = "echoparityitem",
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Echo Parity Item" },
            };

            if (style != null)
                weenie.PropertiesInt[PropertyInt.DefaultCombatStyle] = (int)style.Value;

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>The owner stand-in: a level 275 creature with high attributes (part of them in Ranks) and defenses.</summary>
        private static Creature NewOwner()
        {
            var attributes = new Dictionary<PropertyAttribute, PropertiesAttribute>();
            uint init = 180;

            foreach (var attribute in Six)
                attributes[attribute] = new PropertiesAttribute { InitLevel = init += 7, LevelFromCP = 190 };

            var weenie = new Weenie
            {
                WeenieClassId = 0x7F00E501,
                ClassName = "echoparityowner",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 275 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Awfulwaffle" },
                PropertiesAttribute = attributes,
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 0, LevelFromCP = 196 },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 0, LevelFromCP = 196 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 0, LevelFromCP = 196 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.MeleeDefense] = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 10, LevelFromPP = 226 },
                    [Skill.MissileDefense] = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 10, LevelFromPP = 226 },
                    [Skill.MagicDefense] = new PropertiesSkill { SAC = SkillAdvancementClass.Trained, InitLevel = 5, LevelFromPP = 208 },
                },
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>The Echo as its weenie ships it (Content/sql/weenies/1008050 Hunters Echo.sql): level 1, 100 in every attribute.</summary>
        private static CombatPet NewEcho()
        {
            var attributes = new Dictionary<PropertyAttribute, PropertiesAttribute>();

            foreach (var attribute in Six)
                attributes[attribute] = new PropertiesAttribute { InitLevel = 100 };

            var weenie = new Weenie
            {
                WeenieClassId = CombatPet.HuntersEchoWcid,
                ClassName = "ace1008050-huntersecho",
                WeenieType = WeenieType.CombatPet,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 1 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Hunter's Echo" },
                PropertiesAttribute = attributes,
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100, CurrentLevel = 150 },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 100, CurrentLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 100, CurrentLevel = 200 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.MeleeDefense] = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 100 },
                    [Skill.MissileDefense] = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 100 },
                    [Skill.MagicDefense] = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 100 },
                },
            };

            return new CombatPet(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void MatchStats_LevelAttributesVitalsAndDefenses_EqualTheOwners()
        {
            var owner = NewOwner();
            var echo = NewEcho();

            // controls: the fixture starts where the owner reported it (level 1, 100 everywhere), and the owner's
            // attributes really do move the attribute-derived terms, so the order of the writes below is observable
            Assert.AreEqual(1, echo.Level);
            Assert.AreEqual(100u, echo.Strength.Current);
            Assert.AreNotEqual(owner.Quickness.Current + owner.Coordination.Current, echo.Quickness.Current + echo.Coordination.Current,
                "POSITIVE CONTROL: Melee Defense's formula term (Quickness + Coordination) / 3 differs between the two, so matching defenses before attributes would leave them unequal");
            Assert.AreNotEqual(owner.Endurance.Current, echo.Endurance.Current, "POSITIVE CONTROL: health's formula term (Endurance / 2) differs");

            echo.MatchHuntersEchoStats(owner);

            Assert.AreEqual(275, echo.Level);

            foreach (var attribute in Six)
                Assert.AreEqual(owner.Attributes[attribute].Current, echo.Attributes[attribute].Current, attribute.ToString());

            Assert.AreEqual(owner.Health.MaxValue, echo.Health.MaxValue, "max health, AFTER the attributes moved its formula term");
            Assert.AreEqual(owner.Stamina.MaxValue, echo.Stamina.MaxValue, "max stamina");
            Assert.AreEqual(owner.Mana.MaxValue, echo.Mana.MaxValue, "max mana");

            Assert.AreEqual(echo.Health.MaxValue, echo.Health.Current, "health is full");
            Assert.AreEqual(echo.Stamina.MaxValue, echo.Stamina.Current, "stamina is full");
            Assert.AreEqual(echo.Mana.MaxValue, echo.Mana.Current, "mana is full");

            foreach (var skill in Defenses)
                Assert.AreEqual(owner.GetCreatureSkill(skill).Current, echo.GetCreatureSkill(skill).Current, skill.ToString());
        }

        [TestMethod]
        public void MatchStats_IsStableOnARefresh()
        {
            var owner = NewOwner();
            var echo = NewEcho();

            echo.MatchHuntersEchoStats(owner);
            echo.Health.Current = 1;

            // the refresh path runs the same match again: nothing drifts, and health is full again
            echo.MatchHuntersEchoStats(owner);

            foreach (var attribute in Six)
                Assert.AreEqual(owner.Attributes[attribute].Current, echo.Attributes[attribute].Current, attribute.ToString());

            Assert.AreEqual(owner.Health.MaxValue, echo.Health.MaxValue);
            Assert.AreEqual(echo.Health.MaxValue, echo.Health.Current);

            foreach (var skill in Defenses)
                Assert.AreEqual(owner.GetCreatureSkill(skill).Current, echo.GetCreatureSkill(skill).Current, skill.ToString());
        }
    }
}
