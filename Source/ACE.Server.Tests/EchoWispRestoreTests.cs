using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

using ACE.DatLoader.Entity;
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

using DbSpell = ACE.Database.Models.World.Spell;
using Spell = ACE.Server.Entity.Spell;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Echo Cast wisp, owner changes of 2026-11-23 (Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-DESIGN.md):
    ///  1. Heal and Revitalize (the Restore kind) spawn a wisp, aimed only at the caster or a fellow; a Self spell recasts
    ///     on the caster until it is full, an Other spell moves to the most hurt legal target once its target is full.
    ///  2. Spacing: each new wisp takes the first open spot of eight around the caster at 1.5 m.
    ///  3. Drains: harmful Transfer spells qualify, stay on their target, and send the vital to the caster.
    /// Dat-free: the pure rules in EchoWispMath, the side test in EchoWispEngagement, the controller's static legality
    /// test, and source pins on the wiring. The real-spell-data and real-geometry halves are EchoWispSpellDataDatTests and
    /// EchoWispControllerTests.RealBodies_SpaceOutAroundTheCaster.
    /// </summary>
    [TestClass]
    public class EchoWispRestoreTests
    {
        private const string WispFile = "Source/ACE.Server/ClassAbilities/EchoWisp.cs";
        private const string PlayerWispFile = "Source/ACE.Server/WorldObjects/Player_EchoWisp.cs";
        private const string MagicFile = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";

        private static uint nextGuid = 0x7F3E0001;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            // before anything here touches a Player-declared member (its static initializer reads the world database)
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        // UnsafeAccessor, never FieldInfo.SetValue on a Player (its static ctor poisons the test process)
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<MetaSpellType>k__BackingField")]
        private static extern ref SpellType MetaSpellTypeField(SpellBase spellBase);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Bitfield>k__BackingField")]
        private static extern ref uint BitfieldField(SpellBase spellBase);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref ACE.Entity.Models.Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref System.Threading.ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        /// <summary>A bare Player with a biota (so its PK status, facet and PK timer read), a guid and a fellowship (or none).</summary>
        private static Player NewPlayer(uint guid, ACE.Server.Entity.Fellowship fellowship = null, PlayerKillerStatus? pk = null)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(player) = new ObjectGuid(guid);
            BiotaField(player) = new ACE.Entity.Models.Biota { PropertiesInt = new Dictionary<PropertyInt, int>(), PropertiesFloat = new Dictionary<PropertyFloat, double>() };
            BiotaLockField(player) = new System.Threading.ReaderWriterLockSlim();
            if (pk.HasValue)
                player.PlayerKillerStatus = pk.Value;
            player.Fellowship = fellowship;
            return player;
        }

        private static ACE.Server.Entity.Fellowship NewFellowship() =>
            (ACE.Server.Entity.Fellowship)RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.Entity.Fellowship));

        private static Creature NewCreature(uint maxHealth = 0, uint maxStamina = 0)
        {
            TestGameTables.EnsureInitialized();

            var vitals = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();
            if (maxHealth > 0)
                vitals[PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = maxHealth, CurrentLevel = maxHealth };
            if (maxStamina > 0)
                vitals[PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = maxStamina, CurrentLevel = maxStamina };

            return new Creature(new Weenie
            {
                WeenieClassId = 999952,
                ClassName = "echowisprestorecreature",
                WeenieType = WeenieType.Creature,
                PropertiesAttribute2nd = vitals,
            }, new ObjectGuid(nextGuid++));
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<PhysicsObj>k__BackingField")]
        private static extern ref ACE.Server.Physics.PhysicsObj PhysicsObjField(WorldObject wo);

        /// <summary>A wisp origin that passes CastAsEchoWisp's in-the-world guard: a physics object and a landblock (EchoWispEngineTests' shape).</summary>
        private static SpellProjectile LiveOrigin()
        {
            var origin = new SpellProjectile(new Weenie { WeenieClassId = 0x7E300000 + nextGuid, WeenieType = WeenieType.ProjectileSpell }, new ObjectGuid(nextGuid++));
            PhysicsObjField(origin) = new ACE.Server.Physics.PhysicsObj();
            origin.CurrentLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            return origin;
        }

        /// <summary>A Spell carrying just the dat fields (type and flags) and the database vital the rules read.</summary>
        private static Spell FakeSpell(SpellType meta, SpellFlags flags, DamageType vital)
        {
            var spellBase = (SpellBase)RuntimeHelpers.GetUninitializedObject(typeof(SpellBase));
            MetaSpellTypeField(spellBase) = meta;
            BitfieldField(spellBase) = (uint)flags;

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            spell._spell = new DbSpell { DamageType = (int)vital };
            return spell;
        }

        private static string Body(string file, string signature) =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read(file), signature);

        private static int AssertLine(string body, string line, string why)
        {
            var hits = body.Split('\n').Count(l => l.Trim() == line);
            Assert.AreEqual(1, hits, $"expected exactly one line `{line}`: {why}");
            return body.IndexOf(line, StringComparison.Ordinal);
        }

        // ===================== 1. Restore: which spells =====================

        [TestMethod]
        public void IsRestore_IsABeneficialBoostOfHealthOrStamina_AndNothingElse()
        {
            Assert.IsTrue(EchoWispMath.IsRestore(SpellType.Boost, true, DamageType.Health), "Heal");
            Assert.IsTrue(EchoWispMath.IsRestore(SpellType.Boost, true, DamageType.Stamina), "Revitalize");

            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.Boost, true, DamageType.Mana), "Mana Boost stays out");
            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.Boost, false, DamageType.Health), "Harm is a harmful Boost: a Damage spell");
            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.Boost, true, DamageType.Undef), "a Boost with no vital row");
            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.FellowBoost, true, DamageType.Health), "only MetaSpellType Boost");
            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.Transfer, true, DamageType.Health), "Infuse is a Transfer");
            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.Enchantment, true, DamageType.Health), "a buff");
            Assert.IsFalse(EchoWispMath.IsRestore(SpellType.PortalRecall, true, DamageType.Health), "a portal");
        }

        [TestMethod]
        public void KindOf_GivesRestoreOnlyToARestoreSpell()
        {
            Assert.AreEqual(EchoWispSpellKind.Restore, EchoWispMath.KindOf(SpellType.Boost, true, DamageType.Health));
            Assert.AreEqual(EchoWispSpellKind.Restore, EchoWispMath.KindOf(SpellType.Boost, true, DamageType.Stamina));
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.Boost, false, DamageType.Health), "Harm stays Damage");
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.Transfer, false, DamageType.Undef), "a drain stays Damage");
            Assert.AreEqual(EchoWispSpellKind.Debuff, EchoWispMath.KindOf(SpellType.Enchantment, false, DamageType.Undef));
            Assert.AreEqual(EchoWispSpellKind.Dispel, EchoWispMath.KindOf(SpellType.Dispel, false, DamageType.Undef));
        }

        [TestMethod]
        public void Qualifies_ARestore_OnlyAtTheCasterOrAFellow_NeverAMonsterOrAStranger()
        {
            bool Restore(bool hasTarget, bool creature, bool player, bool pet, bool casterOrFellow, DamageType vital = DamageType.Health) =>
                EchoWispMath.Qualifies(SpellType.Boost, isHarmful: false, isProjectile: false, hasTarget, creature, player, pet, vital, casterOrFellow);

            Assert.IsTrue(Restore(true, true, true, false, true), "a heal at the caster or a fellow");
            Assert.IsTrue(Restore(true, true, true, false, true, DamageType.Stamina), "a revitalize at the caster or a fellow");

            Assert.IsFalse(Restore(true, true, true, false, false), "a player outside the fellowship");
            Assert.IsFalse(Restore(true, true, false, false, false), "a monster");
            Assert.IsFalse(Restore(true, true, false, true, false), "a pet");
            Assert.IsFalse(Restore(false, false, false, false, false), "no target");
            Assert.IsFalse(Restore(true, true, true, false, true, DamageType.Mana), "Mana Boost never qualifies, even at the caster");

            // every other beneficial spell still never qualifies
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Enchantment, false, false, true, true, true, false, DamageType.Undef, true), "a buff at a fellow");
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Transfer, false, false, true, true, true, false, DamageType.Undef, true), "Infuse at a fellow");
        }

        [TestMethod]
        public void Qualifies_AHarmfulSpell_NeverAtAPlayer_EvenAFellow()
        {
            foreach (var type in new[] { SpellType.Projectile, SpellType.LifeProjectile, SpellType.Boost, SpellType.Transfer, SpellType.Enchantment, SpellType.Dispel })
            {
                Assert.IsFalse(EchoWispMath.Qualifies(type, isHarmful: true, isProjectile: false, hasTarget: true, targetIsCreature: true, targetIsPlayer: true, targetIsPet: false,
                    vital: DamageType.Health, targetIsCasterOrFellow: true), $"{type}: class abilities never reach PvP, a fellow included");
            }
        }

        // ===================== 1. Restore: the cast-time target guard =====================

        [TestMethod]
        public void CastTargetRefusal_KeepsEveryHarmfulPvpGuard_AndLetsARestoreReachOnlyTheCasterOrAFellow()
        {
            Assert.AreEqual("player target", EchoWispMath.CastTargetRefusal(false, true, true, false, false), "a harmful cast at a stranger");
            Assert.AreEqual("player target", EchoWispMath.CastTargetRefusal(false, true, true, true, true), "a harmful cast at a PvP-safe fellow: still refused");
            Assert.IsNull(EchoWispMath.CastTargetRefusal(false, true, false, false, false), "a harmful cast at a monster");
            Assert.IsNull(EchoWispMath.CastTargetRefusal(false, false, false, false, false), "an untargeted ring or wall");

            Assert.IsNull(EchoWispMath.CastTargetRefusal(true, true, true, true, true), "a heal at the caster or a PvP-safe fellow");
            Assert.AreEqual("restore target is not PvP-safe", EchoWispMath.CastTargetRefusal(true, true, true, true, false), "a heal at a fellow in a PvP state");
            Assert.AreEqual("restore target is not the caster or a fellow", EchoWispMath.CastTargetRefusal(true, true, true, false, true));
            Assert.AreEqual("restore target is not a player", EchoWispMath.CastTargetRefusal(true, true, false, false, true), "a heal at a monster");
            Assert.AreEqual("restore target is not a player", EchoWispMath.CastTargetRefusal(true, false, false, false, true), "a heal with no target");
        }

        [TestMethod]
        public void CastAsEchoWisp_RefusesARestoreAtAStrangerOrAMonster_AndAHarmfulSpellAtAFellow()
        {
            var owner = NewPlayer(0x50000B01);
            var fellowship = NewFellowship();
            owner.Fellowship = fellowship;

            var fellow = NewPlayer(0x50000B02, fellowship);
            var stranger = NewPlayer(0x50000B03);

            var scope = () => new EchoWispCastScope(LiveOrigin(), 0, false);

            var heal = FakeSpell(SpellType.Boost, SpellFlags.Beneficial, DamageType.Health);
            var harm = FakeSpell(SpellType.Boost, SpellFlags.Resistable, DamageType.Health);

            var s1 = scope();
            Assert.AreEqual(EchoWispCastOutcome.Refused, owner.CastAsEchoWisp(s1, heal, stranger));
            Assert.AreEqual("restore target is not the caster or a fellow", s1.RefusedBecause);

            var s2 = scope();
            Assert.AreEqual(EchoWispCastOutcome.Refused, owner.CastAsEchoWisp(s2, heal, NewCreature(100)));
            Assert.AreEqual("restore target is not a player", s2.RefusedBecause, "a heal never lands on a monster");

            var s3 = scope();
            Assert.AreEqual(EchoWispCastOutcome.Refused, owner.CastAsEchoWisp(s3, harm, fellow));
            Assert.AreEqual("player target", s3.RefusedBecause, "a harmful wisp cast never reaches a player, a fellow included");

            // a solo caster (no fellowship) and a stranger in none: two null fellowships are not a fellowship
            var solo = NewPlayer(0x50000B04);
            var loneStranger = NewPlayer(0x50000B05);
            var s4 = scope();
            Assert.AreEqual(EchoWispCastOutcome.Refused, solo.CastAsEchoWisp(s4, heal, loneStranger));
            Assert.AreEqual("restore target is not the caster or a fellow", s4.RefusedBecause, "a solo caster's heal never reaches a stranger");

            Assert.IsNull(owner.EchoWispCast, "no refusal arms the latch");
            Assert.IsNull(solo.EchoWispCast);
        }

        // ===================== F1: class abilities never reach PvP =====================

        [TestMethod]
        public void IsPvpSafeRestoreTarget_TheCasterAlways_AFellowOnlyNpkWithNoTimerMatchOrSuppression()
        {
            Assert.IsTrue(EchoWispMath.IsPvpSafeRestoreTarget(false, true, false, false, false), "an NPK fellow at peace");
            Assert.IsFalse(EchoWispMath.IsPvpSafeRestoreTarget(false, false, false, false, false), "not NPK (PK, PK Lite, Free)");
            Assert.IsFalse(EchoWispMath.IsPvpSafeRestoreTarget(false, true, true, false, false), "a running PK timer");
            Assert.IsFalse(EchoWispMath.IsPvpSafeRestoreTarget(false, true, false, true, false), "in a PvP match");
            Assert.IsFalse(EchoWispMath.IsPvpSafeRestoreTarget(false, true, false, false, true), "class abilities suppressed (PK facet rule, arena mask)");

            for (var bits = 0; bits < 16; bits++)
                Assert.IsTrue(EchoWispMath.IsPvpSafeRestoreTarget(true, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0), $"the caster always ({bits})");
        }

        [TestMethod]
        public void IsPvpSafeRestoreTarget_ReadsTheLivePlayerState()
        {
            var fellowship = NewFellowship();
            var owner = NewPlayer(0x50000E01, fellowship, PlayerKillerStatus.PK);

            Assert.IsTrue(EchoWispEngagement.IsPvpSafeRestoreTarget(NewPlayer(0x50000E02, fellowship), owner), "an NPK fellow (the default status)");
            Assert.IsFalse(EchoWispEngagement.IsPvpSafeRestoreTarget(NewPlayer(0x50000E03, fellowship, PlayerKillerStatus.PK), owner), "a PK fellow");
            Assert.IsFalse(EchoWispEngagement.IsPvpSafeRestoreTarget(NewPlayer(0x50000E04, fellowship, PlayerKillerStatus.PKLite), owner), "a PK Lite fellow");

            var inMatch = NewPlayer(0x50000E05, fellowship);
            inMatch.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.IsTrue(inMatch.IsInPvpMatch && inMatch.ClassAbilitySuppressed, "harness: bound to a live match with the class-ability mask");
            Assert.IsFalse(EchoWispEngagement.IsPvpSafeRestoreTarget(inMatch, owner), "a fellow in a PvP match");

            Assert.IsTrue(EchoWispEngagement.IsPvpSafeRestoreTarget(owner, owner), "the caster itself, PK or not (a suppressed caster's wisps end instead)");
            Assert.IsFalse(EchoWispEngagement.IsPvpSafeRestoreTarget(null, owner));
        }

        [TestMethod]
        public void CastAsEchoWisp_RefusesARestoreAtAPkFlaggedFellow()
        {
            var fellowship = NewFellowship();
            var owner = NewPlayer(0x50000F01, fellowship);
            var pkFellow = NewPlayer(0x50000F02, fellowship, PlayerKillerStatus.PK);
            Assert.IsTrue(EchoWispEngagement.IsCasterOrFellow(pkFellow, owner), "control: a fellow by side");

            var heal = FakeSpell(SpellType.Boost, SpellFlags.Beneficial, DamageType.Health);
            var scope = new EchoWispCastScope(LiveOrigin(), 0, false);

            Assert.AreEqual(EchoWispCastOutcome.Refused, owner.CastAsEchoWisp(scope, heal, pkFellow));
            Assert.AreEqual("restore target is not PvP-safe", scope.RefusedBecause, "class abilities never reach PvP");
            Assert.IsNull(owner.EchoWispCast, "the refusal never arms the latch");

            var inMatch = NewPlayer(0x50000F03, fellowship);
            inMatch.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            var scope2 = new EchoWispCastScope(LiveOrigin(), 0, false);
            Assert.AreEqual(EchoWispCastOutcome.Refused, owner.CastAsEchoWisp(scope2, heal, inMatch));
            Assert.AreEqual("restore target is not PvP-safe", scope2.RefusedBecause, "a fellow in a PvP match");
        }

        [TestMethod]
        public void RestoreVerdict_TurnsAwayAPkFellow_WithTheCastGuardsPredicate()
        {
            var fellowship = NewFellowship();
            var owner = NewPlayer(0x50001001, fellowship);
            var pkFellow = NewPlayer(0x50001002, fellowship, PlayerKillerStatus.PK);

            Assert.AreEqual(EchoWispIneligible.NotPvpSafe, EchoWisp.RestoreVerdict(pkFellow, owner, new HashSet<uint>()), "the pick turns a PK fellow away");

            var verdict = Body(WispFile, "internal static EchoWispIneligible RestoreVerdict(");
            AssertLine(verdict, "if (!EchoWispEngagement.IsPvpSafeRestoreTarget(player, owner))", "the pick reads the cast guard's predicate");
            var castAs = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");
            AssertLine(castAs, "restore && restoreTarget != null && EchoWispEngagement.IsPvpSafeRestoreTarget(restoreTarget, this));", "and so does the cast guard");
            AssertLine(Body(PlayerWispFile, "internal bool EchoWispTriggerQualifies("),
                "vital, target is Player casterOrFellow && EchoWispEngagement.IsCasterOrFellow(casterOrFellow, this) && EchoWispEngagement.IsPvpSafeRestoreTarget(casterOrFellow, this));",
                "and so does the trigger");
        }

        [TestMethod]
        public void CurrentEndReason_EndsEveryWispWhenTheCastersClassAbilitiesAreSuppressed()
        {
            var body = Body(WispFile, "internal string CurrentEndReason(");
            AssertLine(body, "ownerPresent && owner.IsDead, ownerPresent && owner.Teleporting, ownerPresent && owner.ClassAbilitySuppressed, sameLandblock, sameInstance, distance);",
                "the caster's live suppression reaches EndReason for every kind of wisp");

            Assert.AreEqual("suppressed", EchoWispMath.EndReason(1.0, 9.0, true, true, false, false, true, true, true, 5f));
            Assert.IsNull(EchoWispMath.EndReason(1.0, 9.0, true, true, false, false, false, true, true, 5f), "control");
        }

        // ===================== F2: the trigger's gate =====================

        [TestMethod]
        public void TriggerQualifies_ARestore_AtTheCasterAndAnNpkFellow_NeverAStrangerAPkFellowOrAMonster()
        {
            var heal = FakeSpell(SpellType.Boost, SpellFlags.Beneficial, DamageType.Health);
            var selfHeal = FakeSpell(SpellType.Boost, SpellFlags.Beneficial | SpellFlags.SelfTargeted, DamageType.Health);

            bool Qualifies(Player caster, Spell spell, WorldObject target) => caster.EchoWispTriggerQualifies(spell, ref target);

            // a solo caster (no fellowship) and a stranger in none
            var solo = NewPlayer(0x50001101);
            var loneStranger = NewPlayer(0x50001102);
            Assert.IsFalse(Qualifies(solo, heal, loneStranger), "a solo caster's heal at a stranger in no fellowship never spawns a wisp");
            Assert.IsTrue(Qualifies(solo, heal, solo), "control: a solo caster's Heal Other on itself");

            WorldObject untargeted = null;
            Assert.IsTrue(solo.EchoWispTriggerQualifies(selfHeal, ref untargeted), "a Self heal arrives untargeted");
            Assert.AreSame(solo, untargeted, "and is re-aimed at the caster");

            var fellowship = NewFellowship();
            var owner = NewPlayer(0x50001103, fellowship);
            Assert.IsTrue(Qualifies(owner, heal, NewPlayer(0x50001104, fellowship)), "an NPK fellow");
            Assert.IsFalse(Qualifies(owner, heal, NewPlayer(0x50001105, fellowship, PlayerKillerStatus.PK)), "a PK fellow: class abilities never reach PvP");
            Assert.IsFalse(Qualifies(owner, heal, NewPlayer(0x50001106)), "a stranger");
            Assert.IsFalse(Qualifies(owner, heal, NewCreature(100)), "a monster");
        }

        [TestMethod]
        public void CastAsEchoWisp_WiresTheTargetGuard_AndSkipsOnAttackMonsterForARestore()
        {
            var body = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");

            AssertLine(body, "var restore = EchoWispMath.IsRestore(spell.MetaSpellType, spell.IsBeneficial, EchoWisp.RestoreVitalOf(spell));", "the Restore test reads spell data");
            var guard = AssertLine(body, "var targetRefusal = EchoWispMath.CastTargetRefusal(restore, target != null, restoreTarget != null,",
                "one guard decides every player target");
            var pk = AssertLine(body, "if (target != null && CheckPKStatusVsTarget(target, spell) != null)", "the PK rules still run after it, beneficial rules included");
            var arm = AssertLine(body, "EchoWispCast = scope;", "the latch");
            Assert.IsTrue(guard < pk && pk < arm, "the target guard, then the PK check, both before the latch is armed");

            AssertLine(body, "if (!restore)", "OnAttackMonster is a harmful cast's");
            Assert.AreEqual(0, body.Split('\n').Count(l => l.Trim() == "if (target is Player)"), "the old blanket refusal is replaced by the guard, not kept beside it");
        }

        // ===================== 1. Restore: who is legal =====================

        [TestMethod]
        public void IsCasterOrFellow_ComparesFellowshipReferences()
        {
            var fellowship = NewFellowship();
            var caster = NewPlayer(0x50000C01, fellowship);
            var fellow = NewPlayer(0x50000C02, fellowship);
            var other = NewPlayer(0x50000C03, NewFellowship());
            var loner = NewPlayer(0x50000C04);

            Assert.IsTrue(EchoWispEngagement.IsCasterOrFellow(caster, caster), "the caster");
            Assert.IsTrue(EchoWispEngagement.IsCasterOrFellow(fellow, caster), "a fellow");
            Assert.IsFalse(EchoWispEngagement.IsCasterOrFellow(other, caster), "a player in another fellowship");
            Assert.IsFalse(EchoWispEngagement.IsCasterOrFellow(loner, caster), "a player in none");

            var soloCaster = NewPlayer(0x50000C05);
            Assert.IsTrue(EchoWispEngagement.IsCasterOrFellow(soloCaster, soloCaster), "a solo caster heals itself");
            Assert.IsFalse(EchoWispEngagement.IsCasterOrFellow(loner, soloCaster), "two players in no fellowship are not fellows (null == null must not count)");
        }

        [TestMethod]
        public void RestoreVerdict_TurnsAwayMonstersPetsAndStrangers_BeforeAnyVitalIsRead()
        {
            var fellowship = NewFellowship();
            var owner = NewPlayer(0x50000D01, fellowship);
            var stranger = NewPlayer(0x50000D02);

            // an uninitialized Player has no vitals: reading one would throw, so these verdicts prove the order
            Assert.AreEqual(EchoWispIneligible.NotCasterOrFellow, EchoWisp.RestoreVerdict(stranger, owner, new HashSet<uint>()));
            Assert.AreEqual(EchoWispIneligible.NotPlayer, EchoWisp.RestoreVerdict(NewCreature(100), owner, new HashSet<uint>()), "a monster");

            var pet = (Pet)RuntimeHelpers.GetUninitializedObject(typeof(Pet));
            Assert.AreEqual(EchoWispIneligible.NotPlayer, EchoWisp.RestoreVerdict(pet, owner, new HashSet<uint>()), "a pet");
            Assert.AreEqual(EchoWispIneligible.NotPlayer, EchoWisp.RestoreVerdict(null, owner, new HashSet<uint>()));
        }

        [TestMethod]
        public void RestoreIneligible_OrdersTheChecks_AndIsNoneOnlyForALiveVisibleCasterOrFellow()
        {
            Assert.AreEqual(EchoWispIneligible.None, EchoWispMath.RestoreIneligible(true, true, true, false, false, false, false));
            Assert.AreEqual(EchoWispIneligible.NotPlayer, EchoWispMath.RestoreIneligible(false, true, false, true, true, true, true), "type first");
            Assert.AreEqual(EchoWispIneligible.NotCasterOrFellow, EchoWispMath.RestoreIneligible(true, false, false, true, true, true, true), "side second");
            Assert.AreEqual(EchoWispIneligible.NotPvpSafe, EchoWispMath.RestoreIneligible(true, true, false, true, true, true, true), "PvP state third");
            Assert.AreEqual(EchoWispIneligible.Done, EchoWispMath.RestoreIneligible(true, true, true, true, false, false, true));
            Assert.AreEqual(EchoWispIneligible.Dead, EchoWispMath.RestoreIneligible(true, true, true, true, false, false, false));
            Assert.AreEqual(EchoWispIneligible.Hidden, EchoWispMath.RestoreIneligible(true, true, true, false, true, false, false));
            Assert.AreEqual(EchoWispIneligible.Teleporting, EchoWispMath.RestoreIneligible(true, true, true, false, false, true, false));
        }

        [TestMethod]
        public void VitalFractionOf_ReadsTheRestoredVital()
        {
            var c = NewCreature(maxHealth: 200, maxStamina: 400);
            c.Health.Current = 50;
            c.Stamina.Current = c.Stamina.MaxValue;

            Assert.IsTrue(c.Health.MaxValue >= 200 && c.Stamina.MaxValue >= 400, "control: the vitals exist");
            Assert.AreEqual(50f / c.Health.MaxValue, EchoWisp.VitalFractionOf(c, DamageType.Health), 1e-6f, "Heal reads Health");
            Assert.IsTrue(EchoWisp.VitalFractionOf(c, DamageType.Health) < 0.26f);
            Assert.AreEqual(1.0f, EchoWisp.VitalFractionOf(c, DamageType.Stamina), 1e-6f, "Revitalize reads Stamina");
            Assert.AreEqual(1.0f, EchoWisp.VitalFractionOf(c, DamageType.Mana), "anything else reads as full");

            Assert.AreEqual(1.0f, EchoWispMath.VitalFraction(0, 0), "a vital with no max is never hurt");
            Assert.IsTrue(EchoWispMath.IsHurt(EchoWispMath.VitalFraction(199, 200)));
            Assert.IsFalse(EchoWispMath.IsHurt(EchoWispMath.VitalFraction(200, 200)));
        }

        // ===================== 1. Restore: the pick =====================

        private sealed class Body2
        {
            public string Name;
            public float Fraction = 1.0f;
            public bool Legal = true;
            public bool Reach = true;
            public override string ToString() => Name;
        }

        private static Body2 Pick(Body2 current, IEnumerable<Body2> candidates, out string idle) =>
            EchoWispMath.SelectRestoreTarget(current, candidates, b => b.Legal, b => b.Fraction, b => b.Reach, out idle);

        [TestMethod]
        public void SelfRestore_RecastsWhileTheCasterIsHurt_AndIdlesAtFull()
        {
            var caster = new Body2 { Name = "caster", Fraction = 0.6f };

            Assert.AreSame(caster, Pick(caster, null, out var idle), "hurt: recast on the caster");
            Assert.IsNull(idle);

            caster.Fraction = 1.0f;
            Assert.IsNull(Pick(caster, null, out idle), "full: idle");
            Assert.AreEqual(EchoWispMath.IdleNobodyHurt, idle);
            Assert.AreEqual("nobody hurt", idle, "the DEBUG idle reason");

            caster.Fraction = 0.99f;
            Assert.AreSame(caster, Pick(caster, null, out _), "hurt again: the next cast re-checks and casts");
        }

        [TestMethod]
        public void OtherRestore_StaysOnItsTargetUntilFull_ThenMovesToTheMostHurtInReach_CasterIncluded()
        {
            var caster = new Body2 { Name = "caster", Fraction = 0.7f };
            var start = new Body2 { Name = "start", Fraction = 0.9f };
            var fellowA = new Body2 { Name = "fellow A", Fraction = 0.5f };
            var fellowB = new Body2 { Name = "fellow B", Fraction = 0.3f, Reach = false };
            var stranger = new Body2 { Name = "stranger", Fraction = 0.1f, Legal = false };

            var all = new[] { caster, start, fellowA, fellowB, stranger };

            Assert.AreSame(start, Pick(start, all, out _), "the held target is kept while it is hurt, even with others more hurt");

            start.Fraction = 1.0f;
            Assert.AreSame(fellowA, Pick(start, all, out _),
                "start full: the most hurt LEGAL target in reach (the stranger is more hurt but illegal; fellow B is out of reach)");

            fellowA.Fraction = 0.8f;
            Assert.AreSame(fellowA, Pick(fellowA, all, out _), "fellow A is still hurt: kept, though the caster is now more hurt");

            fellowA.Fraction = 1.0f;
            Assert.AreSame(caster, Pick(fellowA, all, out _), "fellow A full: the caster is a candidate, and now the most hurt in reach");

            fellowB.Reach = true;
            Assert.AreSame(caster, Pick(caster, all, out _), "the held caster is still hurt: kept, though fellow B is more hurt and back in reach");

            caster.Fraction = 1.0f;
            Assert.AreSame(fellowB, Pick(caster, all, out _), "the caster full: the most hurt legal target in reach");
        }

        [TestMethod]
        public void OtherRestore_KeepsAHurtHeldTarget_OverAMoreHurtOne()
        {
            var held = new Body2 { Name = "held", Fraction = 0.9f };
            var worse = new Body2 { Name = "worse", Fraction = 0.2f };

            Assert.AreSame(held, Pick(held, new[] { held, worse }, out _), "stay on the target until it is full");
        }

        [TestMethod]
        public void OtherRestore_IdlesWhileNobodyIsHurt_AndSaysWhenTheHurtAreOutOfReach()
        {
            var caster = new Body2 { Name = "caster" };
            var fellow = new Body2 { Name = "fellow" };
            var stranger = new Body2 { Name = "stranger", Fraction = 0.1f, Legal = false };

            Assert.IsNull(Pick(fellow, new[] { caster, fellow, stranger }, out var idle), "nobody legal is hurt: idle, the hurt stranger is never a target");
            Assert.AreEqual(EchoWispMath.IdleNobodyHurt, idle);

            fellow.Fraction = 0.4f;
            fellow.Reach = false;
            Assert.IsNull(Pick(fellow, new[] { caster, fellow, stranger }, out idle));
            Assert.AreEqual(EchoWispMath.IdleHurtOutOfReach, idle);

            fellow.Reach = true;
            Assert.AreSame(fellow, Pick(null, new[] { caster, fellow, stranger }, out idle), "re-checked at the next cast");
            Assert.IsNull(idle);
        }

        [TestMethod]
        public void OtherRestore_ADroppedHeldTargetIsStillACandidate_AndTiesKeepCandidateOrder()
        {
            var a = new Body2 { Name = "a", Fraction = 0.5f };
            var b = new Body2 { Name = "b", Fraction = 0.5f };

            Assert.AreSame(a, Pick(null, new[] { a, b }, out _), "equal fractions: the first listed (the caster is listed first)");
            Assert.AreSame(b, Pick(null, new[] { b, a }, out _));
        }

        [TestMethod]
        public void PickRestoreTarget_WiresTheLegalityFractionAndReach_ThroughTheController()
        {
            var body = Body(WispFile, "private Creature PickRestoreTarget(");

            Assert.AreEqual(2, body.Split('\n').Count(l => l.Trim() == "c => RestoreVerdict(c, owner, done) == EchoWispIneligible.None,") +
                body.Split('\n').Count(l => l.Trim().StartsWith("var self = EchoWispMath.SelectRestoreTarget<Creature>(owner, null, c => RestoreVerdict(c, owner, done) == EchoWispIneligible.None,")),
                "both the Self and the Other pick judge legality with RestoreVerdict");
            AssertLine(body, "if (Spell.IsSelfTargeted)", "a Self spell's only candidate is the caster");
            AssertLine(body, "return EchoWispMath.SelectRestoreTarget(currentTarget, RestoreCandidates(owner),", "an Other spell starts from the held target");
            AssertLine(body, "return !float.IsNaN(distance) && distance <= range && body.IsDirectVisible(c);", "in range and in sight of the wisp");

            var candidates = Body(WispFile, "private static List<Creature> RestoreCandidates(");
            AssertLine(candidates, "var candidates = new List<Creature> { owner };", "the caster included");

            var cast = Body(WispFile, "private void CastOnOwnerThread(");
            AssertLine(cast, "var target = Kind == EchoWispSpellKind.Restore ? PickRestoreTarget(out restoreIdle) : PickTarget(tally);", "a Restore wisp picks by the Restore rule");
            AssertLine(cast, "log.Debug($\"[ECHO WISP] {Owner.Name}'s wisp ({Spell.Name}) idle, no target: {(tally != null ? tally.Describe() : restoreIdle)}\");",
                "the idle line carries the Restore reason (nobody hurt)");
        }

        [TestMethod]
        public void TryTriggerEchoWisp_AimsASelfRestoreAtTheCaster_AndPassesTheFellowTest()
        {
            AssertLine(Body(PlayerWispFile, "internal EchoWisp TryTriggerEchoWisp("), "if (!EchoWispTriggerQualifies(spell, ref target))", "the trigger runs the gate");

            var body = Body(PlayerWispFile, "internal bool EchoWispTriggerQualifies(");

            var self = AssertLine(body, "if (spell.IsSelfTargeted && !spell.IsHarmful)", "a beneficial Self spell arrives untargeted");
            AssertLine(body, "target = this;", "and counts as aimed at the caster");
            var qualifies = AssertLine(body, "vital, target is Player casterOrFellow && EchoWispEngagement.IsCasterOrFellow(casterOrFellow, this) && EchoWispEngagement.IsPvpSafeRestoreTarget(casterOrFellow, this));",
                "the Restore arm of the qualifying gate");
            Assert.IsTrue(self < qualifies);
        }

        [TestMethod]
        public void TheWispKind_ReadsTheVitalOnlyForABeneficialBoost()
        {
            Assert.AreEqual(DamageType.Health, EchoWisp.RestoreVitalOf(FakeSpell(SpellType.Boost, SpellFlags.Beneficial, DamageType.Health)));
            Assert.AreEqual(DamageType.Undef, EchoWisp.RestoreVitalOf(FakeSpell(SpellType.Boost, SpellFlags.Resistable, DamageType.Health)), "Harm");

            var noRow = FakeSpell(SpellType.Boost, SpellFlags.Beneficial, DamageType.Health);
            noRow._spell = null;
            Assert.AreEqual(DamageType.Undef, EchoWisp.RestoreVitalOf(noRow), "a dat-only Spell never throws");

            var kindLine = PooledLootSourceText.Read(WispFile).Split('\n').Count(l =>
                l.Trim() == "Kind = spell != null ? EchoWispMath.KindOf(spell.MetaSpellType, spell.IsBeneficial, RestoreVitalOf(spell)) : EchoWispSpellKind.Damage;");
            Assert.AreEqual(1, kindLine, "the controller's kind comes from the Restore-aware KindOf");
        }

        // ===================== 2. Spacing =====================

        [TestMethod]
        public void SpawnCandidates_AreRightLeftBehindFront_ThenTheFourDiagonals_All1Point5mAtTheCastersHeight()
        {
            var origin = new Vector3(100f, 50f, 12.5f);
            var c = EchoWispMath.SpawnCandidates(origin, Quaternion.Identity);

            Assert.AreEqual(8, c.Count);

            var d = 1.5f / MathF.Sqrt(2f);

            // identity heading faces +Y; the right hand is +X
            AssertNear(new Vector3(101.5f, 50f, 12.5f), c[0], "right");
            AssertNear(new Vector3(98.5f, 50f, 12.5f), c[1], "left");
            AssertNear(new Vector3(100f, 48.5f, 12.5f), c[2], "behind");
            AssertNear(new Vector3(100f, 51.5f, 12.5f), c[3], "in front");
            AssertNear(new Vector3(100f + d, 50f + d, 12.5f), c[4], "front-right");
            AssertNear(new Vector3(100f - d, 50f + d, 12.5f), c[5], "front-left");
            AssertNear(new Vector3(100f + d, 50f - d, 12.5f), c[6], "back-right");
            AssertNear(new Vector3(100f - d, 50f - d, 12.5f), c[7], "back-left");

            foreach (var p in c)
            {
                Assert.AreEqual(origin.Z, p.Z, "at the caster's height");
                Assert.AreEqual(1.5f, Vector3.Distance(origin, p), 1e-4f, "1.5 m from the caster, never on it");
            }

            // a turned caster: facing +X (yaw -90 degrees), the right hand is -Y and front-right is (+X, -Y)
            var turned = EchoWispMath.SpawnCandidates(origin, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -(float)Math.PI / 2f));
            AssertNear(new Vector3(100f, 48.5f, 12.5f), turned[0], "right of a caster facing +X");
            AssertNear(new Vector3(100f + d, 50f - d, 12.5f), turned[4], "front-right of a caster facing +X");
        }

        [TestMethod]
        public void IsSpotTaken_ByALiveWispWithin1m()
        {
            var spot = new Vector3(10f, 10f, 0f);

            Assert.IsTrue(EchoWispMath.IsSpotTaken(spot, new[] { spot }));
            Assert.IsTrue(EchoWispMath.IsSpotTaken(spot, new[] { new Vector3(10.99f, 10f, 0f) }), "within 1 m");
            Assert.IsTrue(EchoWispMath.IsSpotTaken(spot, new[] { new Vector3(11f, 10f, 0f) }), "exactly 1 m is within");
            Assert.IsFalse(EchoWispMath.IsSpotTaken(spot, new[] { new Vector3(11.01f, 10f, 0f) }), "past 1 m is free");
            Assert.IsTrue(EchoWispMath.IsSpotTaken(spot, new[] { new Vector3(10f, 10f, 2.0f) }), "horizontal only: a wisp 2 m above or below still holds the spot");
            Assert.IsTrue(EchoWispMath.IsSpotTaken(spot, new[] { new Vector3(10.5f, 10f, -2.0f) }), "horizontal only, below");
            Assert.IsFalse(EchoWispMath.IsSpotTaken(spot, null));
            Assert.IsFalse(EchoWispMath.IsSpotTaken(spot, Array.Empty<Vector3>()));
        }

        [TestMethod]
        public void ChooseSpawnPosition_TakesTheFirstOpenSpot_InTheOwnersOrder()
        {
            var origin = new Vector3(100f, 50f, 12.5f);
            var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
            var spots = EchoWispMath.SpawnCandidates(origin, heading);

            (uint, Vector3)? Everywhere(Vector3 p) => (0x01F501B3u, p);

            var occupied = new List<Vector3>();
            var order = new List<Vector3>();

            for (var i = 0; i < 8; i++)
            {
                var at = EchoWisp.ChooseSpawnPosition(origin, heading, 0u, Everywhere, occupied);
                Assert.IsNotNull(at, $"wisp {i + 1} of 8 finds a spot");
                order.Add(at.Pos);
                occupied.Add(at.Pos);
            }

            for (var i = 0; i < 8; i++)
                AssertNear(spots[i], order[i], $"wisp {i + 1}: right, left, behind, front, front-right, front-left, back-right, back-left");

            for (var i = 0; i < 8; i++)
                for (var j = i + 1; j < 8; j++)
                    Assert.IsTrue(Vector3.Distance(order[i], order[j]) > EchoWispMath.SpotTakenRadius, $"wisps {i + 1} and {j + 1} stand more than 1 m apart");

            Assert.IsNull(EchoWisp.ChooseSpawnPosition(origin, heading, 0u, Everywhere, occupied), "all eight taken: no wisp, never a fallback onto the caster");
        }

        [TestMethod]
        public void ChooseSpawnPosition_SkipsATakenSpot_AndAnUnresolvedOne()
        {
            var origin = new Vector3(100f, 50f, 12.5f);
            var spots = EchoWispMath.SpawnCandidates(origin, Quaternion.Identity);

            // right is held by a wisp 0.5 m off it, left is a wall: behind is next
            var asked = new List<Vector3>();
            var at = EchoWisp.ChooseSpawnPosition(origin, Quaternion.Identity, 0u, p =>
            {
                asked.Add(p);
                return Vector3.Distance(p, spots[1]) < 1e-3f ? null : ((uint, Vector3)?)(0x01F501B3u, p);
            }, new[] { spots[0] + new Vector3(0f, 0.5f, 0f) });

            AssertNear(spots[2], at.Pos, "behind");
            Assert.AreEqual(2, asked.Count, "a taken spot is skipped before the cell is resolved");
            AssertNear(spots[1], asked[0], "the left was asked and failed");

            // a wisp of another caster far away holds nothing here
            AssertNear(spots[0], EchoWisp.ChooseSpawnPosition(origin, Quaternion.Identity, 0u, p => (0u, p), new[] { new Vector3(300f, 300f, 0f) }).Pos, "right");
        }

        [TestMethod]
        public void TrySpawn_PassesTheOwnersLiveWispSpots_InTheCastersFrame()
        {
            var spawn = Body(WispFile, "internal static EchoWisp TrySpawn(");
            AssertLine(spawn, "owner.EchoWispSpots(physics.Position));", "the occupied spots reach the spawn choice");

            var spots = Body(PlayerWispFile, "internal List<System.Numerics.Vector3> EchoWispSpots(");
            AssertLine(spots, "spots.Add(casterPosition.Frame.Origin + casterPosition.GetOffset(physics.Position));", "measured across a landblock seam");
            AssertLine(spots, "if (wisp.IsEnded || physics?.Position == null || wisp.Body.IsDestroyed)", "an ended wisp holds no spot");
        }

        // ===================== 3. Drains =====================

        /// <summary>
        /// A drain's vital goes to the PLAYER, not the wisp body: CastAsEchoWisp calls HandleCastSpell on the player itself
        /// (the scope's Origin is only the launch geometry, ProjectileGeometrySource), and HandleCastSpell_Transfer takes its
        /// destination from `this` for a CasterDestination drain (transfer_Bitfield 6 = TargetSource | CasterDestination on
        /// every Drain Other row, EchoWispSpellDataDatTests) and never reads the geometry source.
        /// </summary>
        [TestMethod]
        public void Drain_SendsTheVitalToTheCaster_NotTheWisp()
        {
            var castAs = Body(PlayerWispFile, "internal EchoWispCastOutcome CastAsEchoWisp(");
            AssertLine(castAs, "if (outcome == EchoWispCastOutcome.Cast && !HandleCastSpell(spell, target, null, launcher, false))", "the player's own HandleCastSpell");
            Assert.IsFalse(castAs.Contains("scope.Origin.HandleCastSpell") || castAs.Contains("Origin.TryCastSpell"), "the wisp body never casts");

            var transfer = Body(MagicFile, "private void HandleCastSpell_Transfer(");
            AssertLine(transfer, "var caster = this as Creature;", "the caster is this");
            AssertLine(transfer, "var destination = spell.TransferFlags.HasFlag(TransferFlags.CasterDestination) ? caster : targetCreature;", "a drain's destination is the caster");
            Assert.IsFalse(transfer.Contains("ProjectileGeometrySource") || transfer.Contains("ActiveEchoWispCast?.Origin") || transfer.Contains("scope.Origin"),
                "the transfer never resolves the wisp body");

            Assert.IsTrue(((TransferFlags)6).HasFlag(TransferFlags.CasterDestination) && ((TransferFlags)6).HasFlag(TransferFlags.TargetSource), "6 is target to caster");
        }

        [TestMethod]
        public void Drain_QualifiesAtAMonster_StaysUntilItDies_AndNeverAtAPlayer()
        {
            Assert.IsTrue(EchoWispMath.Qualifies(SpellType.Transfer, isHarmful: true, isProjectile: false, hasTarget: true, targetIsCreature: true, targetIsPlayer: false, targetIsPet: false));
            Assert.IsFalse(EchoWispMath.Qualifies(SpellType.Transfer, true, false, true, true, true, false, DamageType.Undef, true), "never at a player, a fellow included");
            Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(SpellType.Transfer, false, DamageType.Undef), "stays on its target until it dies");
            Assert.IsFalse(EchoWispMath.IsDoneAfter(EchoWispSpellKind.Damage, EchoWispCastOutcome.Cast));
            Assert.IsFalse(EchoWispMath.IsDoneAfter(EchoWispSpellKind.Damage, EchoWispCastOutcome.Resisted), "a resisted drain tries again");
        }

        private static void AssertNear(Vector3 expected, Vector3 actual, string why)
        {
            Assert.AreEqual(expected.X, actual.X, 1e-3f, why + " (X)");
            Assert.AreEqual(expected.Y, actual.Y, 1e-3f, why + " (Y)");
            Assert.AreEqual(expected.Z, actual.Z, 1e-3f, why + " (Z)");
        }
    }
}
