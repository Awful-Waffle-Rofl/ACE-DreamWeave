using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Jewelry Aspect Fortitude (Task B): the band placement and eligibility helpers, Player.GetFortitude's gates, and the
    /// pre-write dispatcher itself, driven on a bare Player whose handler cache is a test-owned stub array. Every
    /// PropertyManager key this class reads is seeded per test and restored in TestCleanup.
    /// </summary>
    [TestClass]
    public class JewelryFortitudeTests
    {
        private static uint nextGuid = 0x7EC00000;

        private static readonly string[] BoolKeys = { "jewelry_aspects_enabled", "class_abilities_enabled" };
        private static readonly string[] DoubleKeys = { "jewelry_aspects_fortitude_per_slot", "jewelry_aspects_fortitude_cap", "jewelry_aspects_penetration_per_slot", "jewelry_aspects_penetration_cap", "jewelry_aspects_penetration_amplify", "jewelry_aspects_resolve_per_slot", "jewelry_aspects_resolve_cap" };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables()
        {
            PropertyManager.ModifyBool("jewelry_aspects_enabled", true);
            PropertyManager.ModifyBool("class_abilities_enabled", true);
            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_per_slot", 0.01);
            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_cap", 0.50);
            PropertyManager.ModifyDouble("jewelry_aspects_penetration_per_slot", 0.008);
            PropertyManager.ModifyDouble("jewelry_aspects_penetration_cap", 0.40);
            PropertyManager.ModifyDouble("jewelry_aspects_penetration_amplify", 1.0);
            PropertyManager.ModifyDouble("jewelry_aspects_resolve_per_slot", 0.02);
            PropertyManager.ModifyDouble("jewelry_aspects_resolve_cap", 1.0);
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            foreach (var key in BoolKeys)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            foreach (var key in DoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        // ---------------- pure helpers ----------------

        [TestMethod]
        public void Band_SitsBetweenImmunityAndAbsorbPool()
        {
            Assert.AreEqual(90, DamageMitigationOrder.GearFortitude);
            Assert.IsTrue(DamageMitigationOrder.Immunity < DamageMitigationOrder.GearFortitude);
            Assert.IsTrue(DamageMitigationOrder.GearFortitude < DamageMitigationOrder.AbsorbPool);
        }

        [TestMethod]
        public void RunsFortitudeBefore_PlacementByBand()
        {
            Assert.IsFalse(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.Observe, false), "observers see the raw hit");
            Assert.IsFalse(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.Immunity, false), "the immunity window runs first");
            Assert.IsTrue(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.AbsorbPool, false), "absorb pools see the reduced hit");
            Assert.IsTrue(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.Divert, false));
            Assert.IsTrue(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.DeathSave, false));
            Assert.IsTrue(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.GearFortitude, false), "a handler exactly at the band still follows it");
            Assert.IsFalse(DamageMitigationOrder.RunsFortitudeBefore(DamageMitigationOrder.AbsorbPool, true), "only once");
        }

        [TestMethod]
        public void EligibleSource_NullAndCreatureYes_PlayerPairAndSelfNo()
        {
            var defender = BarePlayer(out _);
            var otherPlayer = BarePlayer(out _);
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            Assert.IsTrue(ClassAbilityAttackerFilter.IsFortitudeEligibleSource(null, defender), "null source is PvE");
            Assert.IsTrue(ClassAbilityAttackerFilter.IsFortitudeEligibleSource(creature, defender));
            Assert.IsFalse(ClassAbilityAttackerFilter.IsFortitudeEligibleSource(otherPlayer, defender), "PvP pair");
            Assert.IsFalse(ClassAbilityAttackerFilter.IsFortitudeEligibleSource(defender, defender), "self-inflicted");
        }

        [TestMethod]
        public void EligibleDamage_WholeHitOrZero_ExplicitShareClamped()
        {
            Assert.AreEqual(100u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(true, 100, null));
            Assert.AreEqual(0u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(false, 100, null));
            Assert.AreEqual(40u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(false, 100, 40), "an explicit share wins over the source flag");
            Assert.AreEqual(100u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(true, 100, 400), "clamped to the hit");
            Assert.AreEqual(0u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(true, 100, 0), "an explicit zero share means nothing is eligible");
        }

        [TestMethod]
        public void DotShare_MixedTick_CountsOnlyEligibleContributions()
        {
            var defender = BarePlayer(out _);
            var rival = BarePlayer(out _);
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            // the same predicate EnchantmentManager.ApplyDamageTick passes: not a PvP pair, not the target itself
            var contributions = new List<KeyValuePair<WorldObject, float>>
            {
                new KeyValuePair<WorldObject, float>(creature, 30f),
                new KeyValuePair<WorldObject, float>(rival, 50f),
                new KeyValuePair<WorldObject, float>(defender, 20f),
            };

            var share = ClassAbilityAttackerFilter.EnemyCreatureShareOfTick(
                System.Linq.Enumerable.Select(contributions, c => (!ACE.Server.Pvp.Rules.PvpClassifier.IsPvp(c.Key, defender) && c.Key != defender, c.Value)),
                100);

            Assert.AreEqual(30u, share, "only the creature's contribution is eligible");
            Assert.AreEqual(100u - (uint)Math.Round(30 * 0.5), JewelryAspectMath.ApplyFortitude(100, share, 0.5), "a 50% layer removes half of the eligible share only");
        }

        // ---------------- Player.GetFortitude gates ----------------

        [TestMethod]
        public void GetFortitude_WornDefenseSlots_ScaleAndCap()
        {
            var player = BarePlayer(out var equipped);
            Wear(equipped, Piece(2, 3, 5, EquipMask.FingerWearLeft));

            Assert.AreEqual(0.03, player.GetFortitude(null), 1e-9, "3 Defense slots x 0.01");

            for (var i = 0; i < 5; i++)
                Wear(equipped, Piece(0, 10, 0, EquipMask.WristWearRight));

            Assert.AreEqual(0.50, player.GetFortitude(null), 1e-9, "53 slots x 0.01 caps at 0.50");
        }

        [TestMethod]
        public void GetFortitude_Gates_DisabledPvpSelfAndNoJewelry()
        {
            var player = BarePlayer(out var equipped);
            var rival = BarePlayer(out _);
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            Assert.AreEqual(0.0, player.GetFortitude(null), "no jewelry worn");

            Wear(equipped, Piece(0, 10, 0, EquipMask.NeckWear));

            Assert.AreEqual(0.10, player.GetFortitude(creature), 1e-9, "harness control: a creature source gets the layer");
            Assert.AreEqual(0.0, player.GetFortitude(rival), "PvP pair");
            Assert.AreEqual(0.0, player.GetFortitude(player), "self-inflicted");

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);
            Assert.AreEqual(0.0, player.GetFortitude(creature), "kill switch");
        }

        [TestMethod]
        public void GetFortitude_IgnoresAPieceNotInAJewelrySlot_AndAnUnaspectedPiece()
        {
            var player = BarePlayer(out var equipped);

            Wear(equipped, Piece(0, 10, 0, EquipMask.Cloak));
            Assert.AreEqual(0.0, player.GetFortitude(null), "a cloak slot does not count");

            Wear(equipped, Piece(0, 0, 0, EquipMask.NeckWear));
            Assert.AreEqual(0.0, player.GetFortitude(null), "an unaspected piece contributes nothing");

            Wear(equipped, Piece(0, 11, 0, EquipMask.WristWearLeft));
            Assert.AreEqual(0.0, player.GetFortitude(null), "a total over 10 is invalid and contributes nothing");

            Wear(equipped, Piece(0, 4, 0, EquipMask.FingerWearLeft));
            Assert.AreEqual(0.04, player.GetFortitude(null), 1e-9, "a partly filled piece (4 of 10) counts");
        }

        // ---------------- Player.GetPenetrationAgainst and GetResolveChance gates ----------------

        [TestMethod]
        public void GetPenetrationAgainst_AttackSlots_ScaleAndCap()
        {
            var player = BarePlayer(out var equipped);
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            Assert.AreEqual(0.0, player.GetPenetrationAgainst(creature), "no jewelry worn");

            Wear(equipped, Piece(10, 0, 0, EquipMask.NeckWear));
            Assert.AreEqual(0.08, player.GetPenetrationAgainst(creature), 1e-9, "10 Attack slots x 0.008");

            for (var i = 0; i < 9; i++)
                Wear(equipped, Piece(10, 0, 0, EquipMask.FingerWearLeft));

            Assert.AreEqual(0.40, player.GetPenetrationAgainst(creature), 1e-9, "100 slots x 0.008 caps at 0.40");
        }

        [TestMethod]
        public void GetPenetrationAgainst_Gates_PvpPairAndDisabled()
        {
            var player = BarePlayer(out var equipped);
            var rival = BarePlayer(out _);
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            Wear(equipped, Piece(10, 0, 0, EquipMask.NeckWear));

            Assert.AreEqual(0.08, player.GetPenetrationAgainst(creature), 1e-9, "harness control: a creature target gets the layer");
            Assert.AreEqual(0.0, player.GetPenetrationAgainst(rival), "PvP pair");

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);
            Assert.AreEqual(0.0, player.GetPenetrationAgainst(creature), "kill switch");
        }

        [TestMethod]
        public void GetResolveChance_UtilitySlots_ScaleAndCap()
        {
            var player = BarePlayer(out var equipped);

            Assert.AreEqual(0.0, player.GetResolveChance(), "no jewelry worn");

            Wear(equipped, Piece(0, 0, 10, EquipMask.NeckWear));
            Assert.AreEqual(0.20, player.GetResolveChance(), 1e-9, "10 Utility slots x 0.02");

            for (var i = 0; i < 4; i++)
                Wear(equipped, Piece(0, 0, 10, EquipMask.WristWearLeft));

            Assert.AreEqual(1.0, player.GetResolveChance(), 1e-9, "50 slots x 0.02 reaches the 1.0 cap exactly");

            for (var i = 0; i < 5; i++)
                Wear(equipped, Piece(0, 0, 10, EquipMask.FingerWearLeft));

            Assert.AreEqual(1.0, player.GetResolveChance(), 1e-9, "100 slots stay capped at 1.0");
        }

        [TestMethod]
        public void GetResolveChance_Gates_DisabledAndOtherTypes_AndIgnoresClassAbilities()
        {
            var player = BarePlayer(out var equipped);

            Wear(equipped, Piece(10, 10, 0, EquipMask.NeckWear));
            Assert.AreEqual(0.0, player.GetResolveChance(), "Attack and Defense aspects give no Resolve");

            Wear(equipped, Piece(0, 0, 10, EquipMask.WristWearLeft));
            Assert.AreEqual(0.20, player.GetResolveChance(), 1e-9, "harness control");

            PropertyManager.ModifyBool("class_abilities_enabled", false);
            Assert.AreEqual(0.20, player.GetResolveChance(), 1e-9, "class_abilities_enabled is not a gate");

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);
            Assert.AreEqual(0.0, player.GetResolveChance(), "kill switch");
        }

        // ---------------- the dispatcher ----------------

        [TestMethod]
        public void Dispatch_EmptyCache_StillAppliesFortitude()
        {
            var player = DispatchPlayer(10, out _);

            Assert.AreEqual(90u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100), "10 Defense slots = 10%");
        }

        [TestMethod]
        public void Dispatch_KillSwitchOff_NoHandlerRuns_ButFortitudeStillApplies()
        {
            var player = DispatchPlayer(10, out var equipped);
            var stub = new StubMitigation(DamageMitigationOrder.AbsorbPool);
            SetCache(player, stub);
            PropertyManager.ModifyBool("class_abilities_enabled", false);

            Assert.AreEqual(90u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100));
            Assert.AreEqual(0, stub.Calls, "no class-ability handler may run with the kill switch off");

            // positive control: switched on, the same stub does run
            PropertyManager.ModifyBool("class_abilities_enabled", true);
            player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100);
            Assert.AreEqual(1, stub.Calls, "control: the stub is dispatched when the switch is on");
        }

        [TestMethod]
        public void Dispatch_AbsorbPoolSeesTheReducedHit_ObserverSeesTheRawHit()
        {
            var player = DispatchPlayer(10, out _);
            var observer = new StubMitigation(DamageMitigationOrder.Observe);
            var pool = new StubMitigation(DamageMitigationOrder.AbsorbPool);
            SetCache(player, observer, pool);

            var result = player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100);

            Assert.AreEqual(100u, observer.SeenDamage, "observer band runs before Fortitude");
            Assert.AreEqual(90u, pool.SeenDamage, "absorb pool band runs after Fortitude");
            Assert.AreEqual(90u, result);
        }

        [TestMethod]
        public void Dispatch_ImmunityZeroedHit_IsNeverTouched()
        {
            var player = DispatchPlayer(10, out _);
            var immunity = new StubMitigation(DamageMitigationOrder.Immunity) { SetTo = 0 };
            var pool = new StubMitigation(DamageMitigationOrder.AbsorbPool);
            SetCache(player, immunity, pool);

            Assert.AreEqual(0u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100));
            // proves the zeroed hit STAYS zero; the damage == 0 early-out in ApplyGearFortitude is redundant by arithmetic
            // (ApplyFortitude returns damage unchanged for 0), so this does not pin that guard
            Assert.AreEqual(0u, pool.SeenDamage, "Fortitude left a zeroed hit at zero");
        }

        [TestMethod]
        public void Dispatch_DotImmunityRemovedPart_ComesOutOfTheEligibleShareFirst()
        {
            var player = DispatchPlayer(50, out _);   // 50% Fortitude
            var immunity = new StubMitigation(DamageMitigationOrder.Immunity) { SetTo = 40 };
            SetCache(player, immunity);

            // 100 tick, 60 eligible (creature) / 40 PvP; the window blocks the 60 creature part, leaving the 40 PvP remainder
            Assert.AreEqual(40u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isDamageOverTime: true, isSpell: true, fortitudeEligibleDamage: 60), "post-loop path");
        }

        [TestMethod]
        public void Dispatch_DotImmunityRemovedPart_InLoopPath()
        {
            var player = DispatchPlayer(50, out _);   // 50% Fortitude
            var immunity = new StubMitigation(DamageMitigationOrder.Immunity) { SetTo = 40 };
            var pool = new StubMitigation(DamageMitigationOrder.AbsorbPool);
            SetCache(player, immunity, pool);

            Assert.AreEqual(40u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isDamageOverTime: true, isSpell: true, fortitudeEligibleDamage: 60), "in-loop path");
            Assert.AreEqual(40u, pool.SeenDamage);
        }

        [TestMethod]
        public void Dispatch_DotImmunityPartialRemoval_ReducesOnlyWhatIsLeftOfTheShare()
        {
            // Immunity removes 20 of the 60 eligible: 40 eligible remain, 50% of that is 20, so 80 -> 60
            foreach (var withPool in new[] { false, true })
            {
                var player = DispatchPlayer(50, out _);
                var immunity = new StubMitigation(DamageMitigationOrder.Immunity) { SetTo = 80 };

                if (withPool)
                    SetCache(player, immunity, new StubMitigation(DamageMitigationOrder.AbsorbPool));
                else
                    SetCache(player, immunity);

                Assert.AreEqual(60u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isDamageOverTime: true, isSpell: true, fortitudeEligibleDamage: 60), withPool ? "in-loop path" : "post-loop path");
            }
        }

        [TestMethod]
        public void Dispatch_MixedTick_CreatureShareShrinksWithFortitude()
        {
            var player = DispatchPlayer(50, out _);   // 50% Fortitude
            var pool = new StubMitigation(DamageMitigationOrder.AbsorbPool);
            SetCache(player, pool);

            // 100 tick, 60 creature (all eligible) / 40 PvP: 30 removed, so 70 left of which 30 is creature damage
            var result = player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isDamageOverTime: true, isSpell: true, enemyCreatureSpellDamage: 60, fortitudeEligibleDamage: 60);

            Assert.AreEqual(70u, result);
            Assert.AreEqual(70u, pool.SeenDamage);
            Assert.AreEqual(30u, pool.SeenCreatureShare, "the absorb pool sees the creature share scaled with the removal");
        }

        [TestMethod]
        public void Dispatch_SingleSourceSpell_ShareEqualsTheNewDamage()
        {
            var player = DispatchPlayer(50, out _);
            var pool = new StubMitigation(DamageMitigationOrder.AbsorbPool);
            SetCache(player, pool);

            // an uninitialized Creature is not safe to dead-check, so the single-source shape is modelled with the explicit share
            var result = player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isSpell: true, enemyCreatureSpellDamage: 100, fortitudeEligibleDamage: 100);

            Assert.AreEqual(50u, result);
            Assert.AreEqual(50u, pool.SeenCreatureShare);
        }

        [TestMethod]
        public void ReduceShare_Pure()
        {
            Assert.AreEqual(30u, ClassAbilityAttackerFilter.ReduceShareByFortitude(60, 60, 30));
            Assert.AreEqual(60u, ClassAbilityAttackerFilter.ReduceShareByFortitude(60, 0, 30), "no eligible part: unchanged");
            Assert.AreEqual(60u, ClassAbilityAttackerFilter.ReduceShareByFortitude(60, 100, 0));
            Assert.AreEqual(0u, ClassAbilityAttackerFilter.ReduceShareByFortitude(10, 10, 10));
        }

        [TestMethod]
        public void EligibleDamage_RemovalBeyondTheShare_FloorsAtZero()
        {
            Assert.AreEqual(0u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(false, 40, 60, 100), "removal beyond the share floors at 0");
            Assert.AreEqual(20u, ClassAbilityAttackerFilter.FortitudeEligibleDamage(false, 100, 60, 40), "partial removal leaves the remainder");
        }

        [TestMethod]
        public void Dispatch_NoHandlerAtOrAboveTheBand_AppliesAfterTheLoop()
        {
            var player = DispatchPlayer(10, out _);
            var observer = new StubMitigation(DamageMitigationOrder.Observe);
            SetCache(player, observer);

            Assert.AreEqual(90u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100));
            Assert.AreEqual(1, observer.Calls);
        }

        [TestMethod]
        public void Dispatch_Hazard_IsNeverReduced_OnEitherPath()
        {
            var player = DispatchPlayer(10, out _);

            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100, isHazard: true), "empty cache path");

            SetCache(player, new StubMitigation(DamageMitigationOrder.AbsorbPool));
            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100, isHazard: true), "loop path");

            PropertyManager.ModifyBool("class_abilities_enabled", false);
            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100, isHazard: true), "kill-switch path");
        }

        [TestMethod]
        public void Dispatch_PvpPairAndSelf_AreNeverReduced()
        {
            var player = DispatchPlayer(10, out _);
            var rival = BarePlayer(out _);

            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(rival, DamageType.Slash, 100), "PvP pair");
            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(player, DamageType.Slash, 100), "self-inflicted");

            SetCache(player, new StubMitigation(DamageMitigationOrder.AbsorbPool));
            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(rival, DamageType.Slash, 100), "PvP pair, loop path");
        }

        [TestMethod]
        public void Dispatch_DotMixedTick_ReducesOnlyTheEligibleShare()
        {
            var player = DispatchPlayer(50, out _);   // 50 slots x 0.01 caps at 0.50

            // 100 damage, 40 of it eligible: 40 * 0.5 = 20 off
            Assert.AreEqual(80u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isDamageOverTime: true, isSpell: true, fortitudeEligibleDamage: 40));
            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Fire, 100, isDamageOverTime: true, isSpell: true, fortitudeEligibleDamage: 0), "an all-PvP tick is untouched");
        }

        [TestMethod]
        public void Dispatch_JewelryDisabled_IsANoOp()
        {
            var player = DispatchPlayer(10, out _);
            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);

            Assert.AreEqual(100u, player.ApplyPreWriteDamageClassAbilities(null, DamageType.Slash, 100));
        }

        // ---------------- source scans ----------------

        [TestMethod]
        public void SourceScan_DispatcherAppliesFortitudeOnEveryPath()
        {
            var body = Flat(PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilities.cs"),
                "public uint ApplyPreWriteDamageClassAbilities("));

            // both early returns hand back the Fortitude-reduced hit
            StringAssert.Contains(body, "if (learned.Length == 0) return ApplyGearFortitude(");
            StringAssert.Contains(body, "if (!PropertyManager.GetBool(\"class_abilities_enabled\").Item) return ApplyGearFortitude(");

            // the loop applies it before the first handler at or above the band, and a post-loop fallback exists
            StringAssert.Contains(body, "DamageMitigationOrder.RunsFortitudeBefore(skill.MitigationOrder, fortitudeApplied)");
            StringAssert.Contains(body, "if (!fortitudeApplied) context.Damage = ApplyGearFortitude(");
        }

        [TestMethod]
        public void SourceScan_DotSiteCarriesTheEligibleShare()
        {
            var text = Flat(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs"));

            StringAssert.Contains(text, "!PvpClassifier.IsPvp(c.Key, targetPlayer) && c.Key != targetPlayer");
            StringAssert.Contains(text, "fortitudeEligibleDamage: fortitudeShare");
        }

        // ---------------- helpers ----------------

        private static string Flat(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ");

        /// <summary>A bare Player wearing a single neck piece with <paramref name="defenseSlots"/> Defense slots (capped at 10 per piece, so more pieces are added).</summary>
        private static Player DispatchPlayer(int defenseSlots, out Dictionary<ObjectGuid, WorldObject> equipped)
        {
            var player = BarePlayer(out equipped);
            var masks = new[] { EquipMask.NeckWear, EquipMask.WristWearLeft, EquipMask.WristWearRight, EquipMask.FingerWearLeft, EquipMask.FingerWearRight };

            for (var i = 0; defenseSlots > 0; i++, defenseSlots -= 10)
                Wear(equipped, Piece(0, 10, 0, masks[i % masks.Length]));

            // leave the cache as an EMPTY array: the dispatcher then takes the empty-cache path unless a test installs stubs
            SetCache(player);

            return player;
        }

        private static Player BarePlayer(out Dictionary<ObjectGuid, WorldObject> equipped)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            var field = typeof(Creature).GetField("<EquippedObjects>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Creature.EquippedObjects backing field not found");

            equipped = new Dictionary<ObjectGuid, WorldObject>();
            field.SetValue(player, equipped);

            return player;
        }

        private static void Wear(Dictionary<ObjectGuid, WorldObject> equipped, WorldObject item) => equipped[item.Guid] = item;

        private static WorldObject Piece(int attack, int defense, int utility, EquipMask worn)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1008099,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Jewelry },
                    { PropertyInt.ValidLocations, (int)EquipMask.NeckWear },
                    { PropertyInt.JewelryAspectAttack, attack },
                    { PropertyInt.JewelryAspectDefense, defense },
                    { PropertyInt.JewelryAspectUtility, utility },
                },
            };

            var wo = ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            wo.CurrentWieldedLocation = worn;
            return wo;
        }

        private static void SetCache(Player player, params IPreWriteDamageAbility[] handlers)
        {
            var cache = new (IPreWriteDamageAbility skill, int rank)[handlers.Length];

            for (var i = 0; i < handlers.Length; i++)
                cache[i] = (handlers[i], 1);

            CacheField(player) = cache;
        }

        // FieldInfo.SetValue on a Player runs its static constructor, which needs a database in this harness and poisons the
        // process; an UnsafeAccessor reaches the same private instance field without touching the type initializer.
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "preWriteDamageClassAbilities")]
        private static extern ref (IPreWriteDamageAbility skill, int rank)[] CacheField(Player player);

        /// <summary>A pre-write handler that records what it saw and optionally overwrites the damage.</summary>
        private sealed class StubMitigation : IPreWriteDamageAbility
        {
            public StubMitigation(int order) { MitigationOrder = order; }

            public int Calls;
            public uint SeenDamage;
            public uint SeenCreatureShare;
            public uint? SetTo;

            public ClassAbilityDefinition Definition => null;
            public int MitigationOrder { get; }
            public bool RunsWithoutLearnedRank => false;

            public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
            {
                Calls++;
                SeenDamage = context.Damage;
                SeenCreatureShare = context.SpellDamageFromEnemyCreature;

                if (SetTo.HasValue)
                    context.Damage = SetTo.Value;
            }
        }
    }
}
