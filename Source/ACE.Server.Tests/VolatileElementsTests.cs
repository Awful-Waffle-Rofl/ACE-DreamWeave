using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Volatile Elements (Spellsword T2, owner rulings 2026-10-30): every landed PvE melee/missile weapon hit is
    /// re-typed to one of eight types - the target's weakest innate resist with chance rank x 0.08 x the Item
    /// Tinkering affinity (added amount capped), otherwise a uniform pick of the eight. Rend takes the better of
    /// the original and rolled type, on the hit and on the same hit's war procs.
    ///
    /// WHAT IS PINNED BY BEHAVIOUR AND WHAT ONLY BY SOURCE TEXT. The pure decision (VolatileElementsMath), the rend
    /// max on a live weapon, the projectile stamp inheritance and the latch's arm/run/always-reset shape
    /// (ClassAbilityProcLatch, including a throwing cast) are exercised directly. Three integration properties
    /// are pinned by SOURCE SCANS ONLY, because this harness cannot construct a live Player (both Player
    /// constructors call DatabaseManager.Authentication.GetAccountById): the seam's position in
    /// DamageEvent.DoCalculateDamage (after GetBaseDamage, before ResolveDefenderHalf), that
    /// Player.CastClassAbilityProc's three-argument overload routes through the tested latch, and that a
    /// non-owner or non-PvE hit returns from Player.RollVolatileElement before any draw. A behavioural test of
    /// those needs a Player-capable harness (a DB-backed test project).
    /// </summary>
    [TestClass]
    public class VolatileElementsTests
    {
        private static uint nextWcid = 0x7E300000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();

            // the live Creature / weapon constructors read the portal dat tables; inject the synthetic ones
            TestGameTables.EnsureInitialized();
        }

        // ---- chance ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Chance_IsEightSixteenTwentyFourPercent_AtMultiplierOne()
        {
            var perRank = PropertyManager.GetDouble("class_ability_volatileelements_chance_per_rank").Item;

            Assert.AreEqual(0.08, perRank, 1e-12, "registered default");
            Assert.AreEqual(0.08, VolatileElementsMath.Chance(1, perRank, 1.0, 0.0), 1e-12);
            Assert.AreEqual(0.16, VolatileElementsMath.Chance(2, perRank, 1.0, 0.0), 1e-12);
            Assert.AreEqual(0.24, VolatileElementsMath.Chance(3, perRank, 1.0, 0.0), 1e-12);
        }

        [TestMethod]
        public void Chance_IsZeroAtRankZero_WhateverTheAffinity()
        {
            Assert.AreEqual(0.0, VolatileElementsMath.Chance(0, 0.08, 1.0, 0.0));
            Assert.AreEqual(0.0, VolatileElementsMath.Chance(0, 0.08, 5.0, 0.0));
            Assert.AreEqual(0.0, VolatileElementsMath.Chance(-1, 0.08, 5.0, 0.0));
        }

        [TestMethod]
        public void Chance_AffinityMultiplies_AndItsAddedAmountIsCapped()
        {
            // rank 3, multiplier 1.5: 0.24 base, +0.12 added uncapped
            Assert.AreEqual(0.36, VolatileElementsMath.Chance(3, 0.08, 1.5, 0.0), 1e-12, "cap 0 = uncapped");
            Assert.AreEqual(0.34, VolatileElementsMath.Chance(3, 0.08, 1.5, 0.10), 1e-12, "the added 0.12 clamps to the 0.10 cap");
            Assert.AreEqual(0.36, VolatileElementsMath.Chance(3, 0.08, 1.5, 0.50), 1e-12, "a cap above the added amount does nothing");

            // a huge multiplier cannot run past rankBonus + cap
            Assert.AreEqual(0.24 + 0.10, VolatileElementsMath.Chance(3, 0.08, 100.0, 0.10), 1e-12);

            // a multiplier below 1 never subtracts
            Assert.AreEqual(0.24, VolatileElementsMath.Chance(3, 0.08, 0.5, 0.10), 1e-12);
        }

        [TestMethod]
        public void Chance_UsesTheLiveAffinityCapTunable_ShapeMatchesSundermark()
        {
            var cap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            // the same added-amount clamp SundermarkAbility.Chance applies, with its step = per-rank and no gear
            var sundermarkShape = SundermarkAbility.Chance(3, 0.24, 0.0, 4.0, cap);
            Assert.AreEqual(sundermarkShape, VolatileElementsMath.Chance(3, 0.08, 4.0, cap), 1e-6);
        }

        // ---- the roll -------------------------------------------------------------------------------------

        private static Func<DamageType, double> Innate(params (DamageType Type, double Value)[] overrides)
        {
            var map = overrides.ToDictionary(o => o.Type, o => o.Value);
            return t => map.TryGetValue(t, out var v) ? v : 1.0;
        }

        private static readonly Func<int, int, int> NeverCalled = (a, b) => throw new AssertFailedException("no integer draw expected");

        [TestMethod]
        public void Success_ReturnsTheWeakestInnateType()
        {
            var innate = Innate((DamageType.Acid, 1.6), (DamageType.Fire, 0.5));

            var result = VolatileElementsMath.Decide(true, DamageType.Slash, 0.24, () => 0.0, NeverCalled, innate);

            Assert.AreEqual(DamageType.Acid, result, "the HIGHEST innate multiplier is the weakest resist");
        }

        [TestMethod]
        public void Success_NetherCanBeTheWeakest()
        {
            var innate = Innate((DamageType.Nether, 1.3));

            Assert.AreEqual(DamageType.Nether, VolatileElementsMath.Decide(true, DamageType.Pierce, 0.24, () => 0.0, NeverCalled, innate));
            CollectionAssert.AreEqual(new[] { DamageType.Nether }, VolatileElementsMath.WeakestTypes(innate));
        }

        [TestMethod]
        public void Success_ATiePicksAmongAllTiedTypes_ThroughTheInclusiveSource()
        {
            var innate = Innate((DamageType.Cold, 2.0), (DamageType.Electric, 2.0), (DamageType.Nether, 2.0));

            CollectionAssert.AreEqual(new[] { DamageType.Cold, DamageType.Electric, DamageType.Nether }, VolatileElementsMath.WeakestTypes(innate));

            var seen = new HashSet<DamageType>();

            for (var pick = 0; pick < 3; pick++)
            {
                var captured = pick;
                var result = VolatileElementsMath.Decide(true, DamageType.Bludgeon, 0.24, () => 0.0, (lo, hi) =>
                {
                    Assert.AreEqual(0, lo);
                    Assert.AreEqual(2, hi, "inclusive upper bound = last tied index");
                    return captured;
                }, innate);

                seen.Add(result.Value);
            }

            CollectionAssert.AreEquivalent(new[] { DamageType.Cold, DamageType.Electric, DamageType.Nether }, seen.ToArray());
        }

        [TestMethod]
        public void WeakestTypes_AllEqual_ReturnsAllEightInPoolOrder()
        {
            CollectionAssert.AreEqual(VolatileElementsMath.Pool, VolatileElementsMath.WeakestTypes(_ => 1.0));
        }

        [TestMethod]
        public void Failure_UniformPick_ReachesAllEightIndices_IncludingSeven()
        {
            Assert.AreEqual(8, VolatileElementsMath.Pool.Length);
            CollectionAssert.AllItemsAreUnique(VolatileElementsMath.Pool);

            var innate = Innate((DamageType.Fire, 2.0));
            var seen = new List<DamageType>();

            for (var index = 0; index < 8; index++)
            {
                var captured = index;
                var result = VolatileElementsMath.Decide(true, DamageType.Slash, 0.08, () => 0.99, (lo, hi) =>
                {
                    Assert.AreEqual(0, lo);
                    Assert.AreEqual(7, hi, "ThreadSafeRandom.Next(int, int) is inclusive, so the upper bound is index 7");
                    return captured;
                }, innate);

                seen.Add(result.Value);
            }

            CollectionAssert.AreEqual(VolatileElementsMath.Pool, seen.ToArray(), "every index, Nether at 7 included");
            Assert.AreEqual(DamageType.Nether, seen[7]);
        }

        [TestMethod]
        public void Failure_CanAlsoLandOnTheWeakest_AndOnAnImmuneType()
        {
            // Fire is the weakest; Cold is immune (0). A failed roll is a plain uniform pick, so both are reachable.
            var innate = Innate((DamageType.Fire, 2.0), (DamageType.Cold, 0.0));

            var fireIndex = Array.IndexOf(VolatileElementsMath.Pool, DamageType.Fire);
            var coldIndex = Array.IndexOf(VolatileElementsMath.Pool, DamageType.Cold);

            Assert.AreEqual(DamageType.Fire, VolatileElementsMath.Decide(true, DamageType.Slash, 0.08, () => 0.99, (a, b) => fireIndex, innate));
            Assert.AreEqual(DamageType.Cold, VolatileElementsMath.Decide(true, DamageType.Slash, 0.08, () => 0.99, (a, b) => coldIndex, innate),
                "immune types are NOT skipped");
        }

        [TestMethod]
        public void Success_IsDecidedAgainstTheChance()
        {
            var innate = Innate((DamageType.Acid, 2.0));
            var pierceIndex = Array.IndexOf(VolatileElementsMath.Pool, DamageType.Pierce);

            Assert.AreEqual(DamageType.Acid, VolatileElementsMath.Decide(true, DamageType.Slash, 0.24, () => 0.2399, (a, b) => pierceIndex, innate));
            Assert.AreEqual(DamageType.Pierce, VolatileElementsMath.Decide(true, DamageType.Slash, 0.24, () => 0.24, (a, b) => pierceIndex, innate));
        }

        [TestMethod]
        public void NoRollIsDrawn_ForANonPveHit_OrAnIneligibleOriginal()
        {
            var draws = 0;
            Func<double> roll = () => { draws++; return 0.0; };
            Func<int, int, int> next = (a, b) => { draws++; return a; };

            Assert.IsNull(VolatileElementsMath.Decide(false, DamageType.Slash, 0.24, roll, next, _ => 1.0), "PvP / no target");

            foreach (var original in new[] { DamageType.Undef, DamageType.Health, DamageType.Stamina, DamageType.Mana, DamageType.Base,
                         DamageType.Slash | DamageType.Health })
            {
                Assert.IsFalse(VolatileElementsMath.IsEligibleOriginal(original), $"{original} is not eligible");
                Assert.IsNull(VolatileElementsMath.Decide(true, original, 0.24, roll, next, _ => 1.0), $"{original} is left alone");
            }

            Assert.AreEqual(0, draws, "neither the chance roll nor the integer source may be drawn");
        }

        [TestMethod]
        public void EveryPoolType_AndACombinedPhysicalType_IsEligible()
        {
            foreach (var type in VolatileElementsMath.Pool)
                Assert.IsTrue(VolatileElementsMath.IsEligibleOriginal(type), $"{type}");

            Assert.IsTrue(VolatileElementsMath.IsEligibleOriginal(DamageType.Slash | DamageType.Pierce));
            Assert.IsNotNull(VolatileElementsMath.Decide(true, DamageType.Slash | DamageType.Pierce, 0.0, () => 0.5, (a, b) => a, _ => 1.0));
        }

        [TestMethod]
        public void ACertainChance_DrawsNoChanceRoll()
        {
            Func<double> roll = () => throw new AssertFailedException("a chance of 1 must not draw the roll");

            Assert.AreEqual(DamageType.Electric, VolatileElementsMath.Decide(true, DamageType.Slash, 1.0, roll, NeverCalled,
                Innate((DamageType.Electric, 3.0))));
        }

        // ---- rend follows the roll ------------------------------------------------------------------------

        [TestMethod]
        public void ResolveWeaponResistanceMod_FireRendRollingCold_KeepsTheFireRend()
        {
            const float fireRend = 2.25f;
            Func<DamageType, float> rendFor = t => t == DamageType.Fire ? fireRend : 1.0f;

            Assert.AreEqual(fireRend, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, DamageType.Fire, DamageType.Cold));
            Assert.AreEqual(fireRend, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, DamageType.Cold, DamageType.Fire), "rolling ONTO the rend type uses it too");
            Assert.AreEqual(1.0f, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, DamageType.Slash, DamageType.Cold), "no rend on either type");
        }

        [TestMethod]
        public void ResolveWeaponResistanceMod_NotReTyped_IsThePlainLookupOnly()
        {
            var asked = new List<DamageType>();
            Func<DamageType, float> rendFor = t => { asked.Add(t); return t == DamageType.Acid ? 1.5f : 1.0f; };

            Assert.AreEqual(1.5f, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, null, DamageType.Acid));
            CollectionAssert.AreEqual(new[] { DamageType.Acid }, asked, "no original: exactly one lookup on the hit's type");
        }

        /// <summary>
        /// The live lookup: a weapon whose fixed resistance cleaving is keyed on Fire (the same type-keyed branch
        /// of GetWeaponResistanceModifier the elemental rends share) keeps that value on a hit re-typed to Cold.
        /// </summary>
        [TestMethod]
        public void ResolveWeaponResistanceMod_OnALiveWeapon_FireKeyedWeaponRollingCold_KeepsItsFireValue()
        {
            var wielder = new Creature(new Weenie { WeenieClassId = nextWcid++, WeenieType = WeenieType.Creature }, new ACE.Entity.ObjectGuid(0x7F300000 + nextWcid));

            var weenie = new Weenie { WeenieClassId = nextWcid++, WeenieType = WeenieType.MeleeWeapon };
            var weapon = new MeleeWeapon(weenie, new ACE.Entity.ObjectGuid(0x7F300000 + nextWcid));
            weapon.ItemType = ItemType.MeleeWeapon;
            weapon.ResistanceModifierType = DamageType.Fire;
            weapon.ResistanceModifier = 1.0;

            Func<DamageType, float> rendFor = t => WorldObject.GetWeaponResistanceModifier(weapon, wielder, null, t);

            var fireValue = rendFor(DamageType.Fire);
            Assert.IsTrue(fireValue > 1.0f, "positive control: the weapon's Fire value is above neutral");
            Assert.AreEqual(1.0f, rendFor(DamageType.Cold), "control: the plain Cold lookup has no rend");

            Assert.AreEqual(fireValue, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, DamageType.Fire, DamageType.Cold));

            // the direct overload the two live call sites use agrees with the pure one
            Assert.AreEqual(fireValue, VolatileElementsMath.ResolveWeaponResistanceMod(weapon, wielder, null, DamageType.Fire, DamageType.Cold));
            Assert.AreEqual(1.0f, VolatileElementsMath.ResolveWeaponResistanceMod(weapon, wielder, null, null, DamageType.Cold), "not re-typed: plain Cold lookup");
            Assert.AreEqual(fireValue, VolatileElementsMath.ResolveWeaponResistanceMod(weapon, wielder, null, null, DamageType.Fire), "not re-typed: plain Fire lookup");
        }

        // ---- procs carry the original type for their rend -------------------------------------------------

        private static SpellProjectile NewProjectile()
        {
            var weenie = new Weenie { WeenieClassId = nextWcid++, WeenieType = WeenieType.ProjectileSpell };
            return new SpellProjectile(weenie, new ACE.Entity.ObjectGuid(0x7F300000 + nextWcid));
        }

        [TestMethod]
        public void CopyPerCastStamps_ChildInheritsRendSourceDamageType()
        {
            var parent = NewProjectile();
            parent.RendSourceDamageType = DamageType.Fire;

            var child = NewProjectile();
            Assert.IsNull(child.RendSourceDamageType, "precondition: a fresh projectile carries no rend source");

            SpellProjectile.CopyPerCastStamps(child, parent);

            Assert.AreEqual(DamageType.Fire, child.RendSourceDamageType, "a Cascade / Echo / AOE child of a re-typed proc keeps the original type");

            var unstamped = NewProjectile();
            var child2 = NewProjectile();
            child2.RendSourceDamageType = DamageType.Acid;
            SpellProjectile.CopyPerCastStamps(child2, unstamped);
            Assert.IsNull(child2.RendSourceDamageType, "an unstamped parent clears the child, it never leaks a stale type");
        }

        /// <summary>
        /// The proc rend max: a Spellblade fired off a fire-rend weapon's hit that rolled cold fires a COLD
        /// streak; the projectile is stamped RendSourceDamageType = Fire, so its rend is the fire value.
        /// </summary>
        [TestMethod]
        public void ProcRend_ColdProcOffAFireRendHit_IsTheFireRendValue()
        {
            const float fireRend = 2.0f;
            Func<DamageType, float> rendFor = t => t == DamageType.Fire ? fireRend : 1.0f;

            var proc = NewProjectile();
            proc.RendSourceDamageType = DamageType.Fire;

            Assert.AreEqual(fireRend, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, proc.RendSourceDamageType, DamageType.Cold));

            var ordinary = NewProjectile();
            Assert.AreEqual(1.0f, VolatileElementsMath.ResolveWeaponResistanceMod(rendFor, ordinary.RendSourceDamageType, DamageType.Cold),
                "an unstamped projectile keeps the plain lookup");
        }

        [TestMethod]
        public void ProcRend_WiredAtTheWarVoidSite_StampedAtLaunch_AndLatchedOnThePlayer()
        {
            var projectileSrc = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs");
            StringAssert.Contains(projectileSrc, "weaponResistanceMod = VolatileElementsMath.ResolveWeaponResistanceMod(weapon, sourceCreature, attackSkill, RendSourceDamageType, Spell.DamageType);");
            StringAssert.Contains(PooledLootSourceText.MethodBody(projectileSrc, "public static void CopyPerCastStamps("),
                "child.RendSourceDamageType = parent.RendSourceDamageType;");

            var magicSrc = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");
            StringAssert.Contains(magicSrc, "sp.RendSourceDamageType = (this as Player)?.ClassAbilityProcRendSourceDamageType;");

            var buffsSrc = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            var overload = PooledLootSourceText.MethodBody(buffsSrc, "public void CastClassAbilityProc(Action cast, float procDamageScale, DamageType? rendSourceDamageType)");
            StringAssert.Contains(overload, "ClassAbilityProcLatch.Run<DamageType?>(v => ClassAbilityProcRendSourceDamageType = v,");
            StringAssert.Contains(overload, "rendSourceDamageType, null,");
            StringAssert.Contains(overload, "() => CastClassAbilityProc(cast, procDamageScale));");
        }

        // ---- the latch, by behaviour ------------------------------------------------------------------

        [TestMethod]
        public void ProcLatch_IsArmedDuringTheCast_AndDisarmedAfter()
        {
            DamageType? latch = null;
            DamageType? seenInside = null;

            ClassAbilityProcLatch.Run<DamageType?>(v => latch = v, DamageType.Fire, null, () => seenInside = latch);

            Assert.AreEqual(DamageType.Fire, seenInside, "the projectiles built inside the cast see the original type");
            Assert.IsNull(latch, "disarmed after the cast");
        }

        [TestMethod]
        public void ProcLatch_ResetsEvenWhenTheCastThrows()
        {
            DamageType? latch = null;

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ClassAbilityProcLatch.Run<DamageType?>(v => latch = v, DamageType.Acid, null, () => throw new InvalidOperationException("cast failed")));

            Assert.IsNull(latch, "a throwing cast must not leave a later ordinary cast stamped");
        }

        [TestMethod]
        public void ProcLatch_ANullArm_LeavesTheStampNull()
        {
            DamageType? latch = DamageType.Cold;
            DamageType? seenInside = DamageType.Cold;

            ClassAbilityProcLatch.Run<DamageType?>(v => latch = v, null, null, () => seenInside = latch);

            Assert.IsNull(seenInside, "a hit Volatile Elements did not re-type stamps nothing");
            Assert.IsNull(latch);
        }

        /// <summary>
        /// All three war procs hand the new overload damageEvent.OriginalDamageType, identically, and none adds rend
        /// logic of its own or reads the weapon's declared type.
        /// </summary>
        [TestMethod]
        public void TheThreeWarProcs_PassOriginalDamageTypeIdentically()
        {
            const string call = "attacker.CastClassAbilityProc(() =>\n                attacker.CreateSpellProjectiles(spell, target, damageEvent.Weapon, false, fromProc: true, suppressPetAssist: true),\n                procDamageScale,\n                damageEvent.OriginalDamageType);";

            foreach (var file in new[] { "SpellbladeAbility.cs", "RunebladeAbility.cs", "SpellstormAbility.cs" })
            {
                var src = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/" + file);

                StringAssert.Contains(src, call, $"{file} must pass damageEvent.OriginalDamageType as the overload's third argument");
                Assert.IsFalse(src.Contains(".W_DamageType"), $"{file} must not read the weapon's declared type");
                Assert.IsFalse(src.Contains("GetWeaponResistanceModifier") || src.Contains("ResolveWeaponResistanceMod"), $"{file} must add no rend logic of its own");
                StringAssert.Contains(src, "var damageType = damageEvent.DamageType;", $"{file} still reads the dealt (rolled) type");
            }
        }

        // ---- the seam -------------------------------------------------------------------------------------

        [TestMethod]
        public void Seam_SitsAfterGetBaseDamage_AndBeforeResolveDefenderHalf()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageEvent.cs");
            var body = PooledLootSourceText.MethodBody(src, "private float DoCalculateDamage(");

            var baseDamage = body.IndexOf("GetBaseDamage(playerAttacker);", StringComparison.Ordinal);
            var failure = body.IndexOf("if (GeneralFailure) return 0.0f;", StringComparison.Ordinal);
            var roll = body.IndexOf("playerAttacker.RollVolatileElement(defender, DamageType)", StringComparison.Ordinal);
            var defenderHalf = body.IndexOf("return ResolveDefenderHalf(", StringComparison.Ordinal);

            Assert.IsTrue(baseDamage >= 0 && failure > baseDamage, "precondition: GetBaseDamage then the failure return");
            Assert.IsTrue(roll > failure, "the roll sits after GetBaseDamage(playerAttacker) and its GeneralFailure return - the launcher's flat elemental bonus was already computed from the original type");
            Assert.IsTrue(defenderHalf > roll, "the roll sits before ResolveDefenderHalf, so everything type-keyed sees the rolled type");
            Assert.AreEqual(1, CountOf(body, "RollVolatileElement("), "one roll per DamageEvent");

            StringAssert.Contains(body, "if (playerAttacker != null && playerDefender == null)");
            StringAssert.Contains(body, "OriginalDamageType = DamageType;");

            var half = PooledLootSourceText.MethodBody(src, "private float ResolveDefenderHalf(");
            StringAssert.Contains(half, "WeaponResistanceMod = VolatileElementsMath.ResolveWeaponResistanceMod(Weapon, attacker, attackSkill, OriginalDamageType, DamageType);");

            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public static DamageEvent CalculateReflectedMissile("),
                "reflected.OriginalDamageType = incoming.OriginalDamageType;");
        }

        [TestMethod]
        public void PlayerHalf_GatesOnPveAndRank_AndReadsTheTunablesAndItemTinkering()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_VolatileElements.cs");
            var body = PooledLootSourceText.MethodBody(src, "public DamageType? RollVolatileElement(");

            StringAssert.Contains(body, "ArchmageResistanceMath.IsPveTarget(target)");
            StringAssert.Contains(body, "TryGetClassAbility(ClassAbilityId.VolatileElements, out var rank)");
            StringAssert.Contains(body, "\"class_ability_volatileelements_chance_per_rank\"");
            StringAssert.Contains(body, "\"class_ability_affinity_chance_cap\"");
            StringAssert.Contains(body, "GetClassAbilityAffinityMultiplier(Skill.ItemTinkering)");
            StringAssert.Contains(body, "return RollVolatileElementCore(target, original, chance);");

            // NO lambda in the per-hit method: a captured `target` would allocate a closure at method entry on
            // every player-vs-monster hit, owners or not. The delegates live in the Core helper.
            Assert.IsFalse(body.Contains("=>"), "RollVolatileElement must build no delegate");

            var early = body.IndexOf("ArchmageResistanceMath.IsPveTarget(target)", StringComparison.Ordinal);
            var rankRead = body.IndexOf("TryGetClassAbility(", StringComparison.Ordinal);
            var tunable = body.IndexOf("PropertyManager.GetDouble(", StringComparison.Ordinal);
            Assert.IsTrue(early >= 0 && early < rankRead && rankRead < tunable, "PvE early out, then the rank gate, then the tunable reads");

            var core = PooledLootSourceText.MethodBody(src, "private static DamageType? RollVolatileElementCore(");
            StringAssert.Contains(core, "ArchmageResistanceMath.InnateResistance(target, Creature.GetResistanceType(type))");
        }

        [TestMethod]
        public void InnateLookup_ReadsNetherToo()
        {
            var creature = new Creature(new Weenie { WeenieClassId = nextWcid++, WeenieType = WeenieType.Creature }, new ACE.Entity.ObjectGuid(0x7F300000 + nextWcid));
            creature.ResistNether = 1.7;

            Func<DamageType, double> innate = t => ArchmageResistanceMath.InnateResistance(creature, Creature.GetResistanceType(t));

            Assert.AreEqual(1.7, innate(DamageType.Nether), 1e-12);
            CollectionAssert.AreEqual(new[] { DamageType.Nether }, VolatileElementsMath.WeakestTypes(innate));
        }

        // ---- definition, registry, token ------------------------------------------------------------------

        [TestMethod]
        public void Definition_MatchesTheRuling()
        {
            var def = ClassAbilityRegistry.Abilities[ClassAbilityId.VolatileElements];

            Assert.AreEqual(96, (int)ClassAbilityId.VolatileElements);
            Assert.AreEqual(ClassAbilityClass.Spellsword, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);
            Assert.AreEqual("volatile_elements", def.Name);
            Assert.AreEqual("Volatile Elements", def.DisplayName);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            Assert.IsTrue(def.Implemented);
            Assert.AreEqual(Skill.ItemTinkering, def.AffinitySkill);
            Assert.AreEqual("Your melee and missile weapon hits deal a random damage type - slash, pierce, bludgeon, fire, cold, acid, " +
                            "electric or nether - with an 8/16/24% chance (by rank) to be the element the target is weakest to. Your " +
                            "weapon's rend applies to whichever element is dealt, and the element carries through to your Spellblade, " +
                            "Runeblade, Spellstorm and Sundermark procs. Higher Item Tinkering increases the chance.", def.Description);
        }

        [TestMethod]
        public void Handler_IsAPassiveReadoutAbility_ThatHooksNoCombatEvent()
        {
            var handler = new VolatileElementsAbility();

            Assert.IsInstanceOfType(handler, typeof(IPassiveStatAbility));
            Assert.IsInstanceOfType(handler, typeof(IAbilityReadout));
            Assert.IsNotInstanceOfType(handler, typeof(IOutgoingDamageAbility));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/VolatileElementsAbility.cs");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public ClassAbilityReadout GetReadout("),
                "GetClassAbilityAffinityMultiplier(Skill.ItemTinkering)");
        }

        [TestMethod]
        public void Token_MintsAtCatalogIndex114()
        {
            Assert.AreEqual(1002210u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.VolatileElements, 1));
            Assert.AreEqual(1002211u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.VolatileElements, 2));
            Assert.AreEqual(1002212u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.VolatileElements, 3));
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
