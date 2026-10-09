using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Behavioural coverage for the pure cores behind BandUplift.Apply and CombatTraitGuard.Apply. Each test
    /// drives a core over plain dictionaries and asserts the VALUES it writes, so removing a write fails a
    /// test. What stays outside the cores: the Creature plumbing in the two Apply wrappers (delegates that
    /// forward to creature.GetProperty/SetProperty/GetCreatureSkill and the body-part biota), and the per-item
    /// sweep, which needs WorldObjects (DungeonCreatureNormalizerItemTraitStripTests covers its strip).
    /// </summary>
    [TestClass]
    public class BandUpliftCoreTests
    {
        // ---- BandUplift cores -----------------------------------------------------------------------------

        [TestMethod]
        public void LevelFloor_RaisesOnlyUpward()
        {
            Assert.AreEqual(275, BandUplift.LevelFloor(180, 275));
            Assert.AreEqual(275, BandUplift.LevelFloor(null, 275), "a missing level counts as 0");
            Assert.IsNull(BandUplift.LevelFloor(300, 275), "never lowers a creature authored above the band");
            Assert.IsNull(BandUplift.LevelFloor(275, 275));
        }

        [TestMethod]
        public void RaiseSkillFloors_RaisesCarriedSkillsBelowTheMedian_AndNothingElse()
        {
            var medians = new Dictionary<Skill, uint>
            {
                [Skill.MeleeDefense] = 300,   // creature has 100: raised
                [Skill.MagicDefense] = 200,   // creature has 250: already above, untouched
                [Skill.MissileDefense] = 0,   // median 0 = no standard: skipped
                [Skill.WarMagic] = 400,       // creature does not carry it: never added
            };
            var init = new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 100, [Skill.MagicDefense] = 250, [Skill.MissileDefense] = 50 };

            var raised = BandUplift.RaiseSkillFloors(medians, s => init.TryGetValue(s, out var v) ? v : (uint?)null, (s, v) => init[s] = v);

            Assert.AreEqual(1, raised);
            Assert.AreEqual(300u, init[Skill.MeleeDefense]);
            Assert.AreEqual(250u, init[Skill.MagicDefense]);
            Assert.AreEqual(50u, init[Skill.MissileDefense]);
            Assert.IsFalse(init.ContainsKey(Skill.WarMagic), "a skill the creature was not authored with is never created");
        }

        // ---- CombatTraitGuard.ApplyCore --------------------------------------------------------------------

        private sealed class Fake
        {
            public readonly Dictionary<PropertyBool, bool> Bools = new Dictionary<PropertyBool, bool>();
            public readonly Dictionary<PropertyFloat, double> Floats = new Dictionary<PropertyFloat, double>();
            public readonly Dictionary<PropertyInt, int> Ints = new Dictionary<PropertyInt, int>();
            public readonly Dictionary<Skill, uint> Bases = new Dictionary<Skill, uint>();
            public readonly Dictionary<Skill, uint> Ceilings = new Dictionary<Skill, uint>();
            public bool? Ethereal;
            public bool? IgnoreCollisions;
            public List<string> Items;

            public CombatTraitGuard.GuardTarget Target() => new CombatTraitGuard.GuardTarget
            {
                GetBool = p => Bools.TryGetValue(p, out var v) ? v : (bool?)null,
                RemoveBool = p => Bools.Remove(p),
                GetFloat = p => Floats.TryGetValue(p, out var v) ? v : (double?)null,
                RemoveFloat = p => Floats.Remove(p),
                SetFloat = (p, v) => Floats[p] = v,
                GetInt = p => Ints.TryGetValue(p, out var v) ? v : (int?)null,
                RemoveInt = p => Ints.Remove(p),
                SweepItems = Items == null ? (System.Func<IEnumerable<string>>)null : () => Items,
                GetEthereal = () => Ethereal,
                SetEthereal = v => Ethereal = v,
                GetIgnoreCollisions = () => IgnoreCollisions,
                SetIgnoreCollisions = v => IgnoreCollisions = v,
                BaseOf = s => Bases.TryGetValue(s, out var v) ? v : (uint?)null,
                SetCeiling = (s, c) => Ceilings[s] = c,
            };
        }

        private static readonly CombatGuardDials Dials = new CombatGuardDials(0.5, 2.0, 50);

        private static DungeonBandStandard Standard(uint meleeMedian)
            => DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 100, 100,
                effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = meleeMedian });

        [TestMethod]
        public void ApplyCore_RemovesEveryBypassTraitTheCreatureCarries()
        {
            var f = new Fake();
            f.Bools[PropertyBool.IgnoreMagicResist] = true;
            f.Bools[PropertyBool.Invincible] = true;
            f.Floats[PropertyFloat.IgnoreShield] = 0.5;
            f.Floats[PropertyFloat.AbsorbMagicDamage] = 0.3;
            f.Ints[PropertyInt.Overpower] = 400;

            var changes = CombatTraitGuard.ApplyCore(f.Target(), Dials, DungeonBandStandard.Empty, isBoss: false);

            Assert.AreEqual(0, f.Bools.Count, "bypass bools removed");
            Assert.IsFalse(f.Floats.ContainsKey(PropertyFloat.IgnoreShield));
            Assert.IsFalse(f.Floats.ContainsKey(PropertyFloat.AbsorbMagicDamage));
            Assert.IsFalse(f.Ints.ContainsKey(PropertyInt.Overpower));
            CollectionAssert.IsSubsetOf(new[] { "IgnoreMagicResist", "Invincible", "IgnoreShield", "AbsorbMagicDamage", "Overpower" }, changes);
        }

        [TestMethod]
        public void ApplyCore_LeavesAFalseBypassBoolAlone()
        {
            var f = new Fake();
            f.Bools[PropertyBool.Invincible] = false;

            var changes = CombatTraitGuard.ApplyCore(f.Target(), Dials, DungeonBandStandard.Empty, false);

            Assert.IsTrue(f.Bools.ContainsKey(PropertyBool.Invincible), "only a TRUE flag is a trait to strip");
            Assert.IsFalse(changes.Contains("Invincible"));
        }

        [TestMethod]
        public void ApplyCore_WritesExplicitFalsePassThroughPhysics()
        {
            var f = new Fake { Ethereal = true };
            f.Ints[PropertyInt.PhysicsState] = (int)PhysicsState.IgnoreCollisions;

            var changes = CombatTraitGuard.ApplyCore(f.Target(), Dials, DungeonBandStandard.Empty, false);

            Assert.AreEqual(false, f.Ethereal, "an explicit false, not a removal");
            Assert.AreEqual(false, f.IgnoreCollisions, "defaulted from the authored PhysicsState bit when the bool is unset");
            CollectionAssert.IsSubsetOf(new[] { "Ethereal", "IgnoreCollisions" }, changes);
        }

        [TestMethod]
        public void ApplyCore_RaisesTheWorstCategoryResistsToTheFloor()
        {
            var f = new Fake();
            f.Floats[PropertyFloat.ResistSlash] = 0.1;
            f.Floats[PropertyFloat.ResistPierce] = 0.2;
            f.Floats[PropertyFloat.ResistBludgeon] = 0.3;

            CombatTraitGuard.ApplyCore(f.Target(), Dials, DungeonBandStandard.Empty, false);

            Assert.AreEqual(0.5, f.Floats[PropertyFloat.ResistSlash], 1e-9, "best physical resist 0.3 < floor 0.5: all three raised to it");
            Assert.AreEqual(0.5, f.Floats[PropertyFloat.ResistPierce], 1e-9);
            Assert.AreEqual(0.5, f.Floats[PropertyFloat.ResistBludgeon], 1e-9);
        }

        [TestMethod]
        public void ApplyCore_ScalesArmorModsDownToTheCeiling()
        {
            var f = new Fake();
            f.Floats[PropertyFloat.ArmorModVsSlash] = 4.0;
            f.Floats[PropertyFloat.ArmorModVsPierce] = 6.0;
            f.Floats[PropertyFloat.ArmorModVsBludgeon] = 8.0;

            CombatTraitGuard.ApplyCore(f.Target(), Dials, DungeonBandStandard.Empty, false);

            Assert.AreEqual(2.0, f.Floats[PropertyFloat.ArmorModVsSlash], 1e-9, "smallest 4.0 scaled to the ceiling 2.0 ...");
            Assert.AreEqual(3.0, f.Floats[PropertyFloat.ArmorModVsPierce], 1e-9, "... by one factor (x0.5) across all three");
            Assert.AreEqual(4.0, f.Floats[PropertyFloat.ArmorModVsBludgeon], 1e-9);
        }

        [TestMethod]
        public void ApplyCore_CeilingsOnlyTheDefenseAboveTheBandCap()
        {
            var f = new Fake();
            f.Bases[Skill.MeleeDefense] = 600;   // cap = 400 + 50 = 450: ceiling written
            f.Bases[Skill.MagicDefense] = 100;   // no band standard for it: untouched

            var changes = CombatTraitGuard.ApplyCore(f.Target(), Dials, Standard(400), isBoss: false);

            Assert.AreEqual(450u, f.Ceilings[Skill.MeleeDefense]);
            Assert.AreEqual(1, f.Ceilings.Count);
            Assert.IsTrue(changes.Contains("MeleeDefense 600->450 (ceiling)"));
        }

        [TestMethod]
        public void ApplyCore_NoCeiling_WhenBelowTheCap_AbsentSkill_OrBoss()
        {
            var below = new Fake();
            below.Bases[Skill.MeleeDefense] = 440;
            CombatTraitGuard.ApplyCore(below.Target(), Dials, Standard(400), false);
            Assert.AreEqual(0, below.Ceilings.Count, "440 <= 450");

            var absent = new Fake();
            CombatTraitGuard.ApplyCore(absent.Target(), Dials, Standard(400), false);
            Assert.AreEqual(0, absent.Ceilings.Count, "a skill the creature does not carry is never ceilinged");

            var boss = new Fake();
            boss.Bases[Skill.MeleeDefense] = 900;
            CombatTraitGuard.ApplyCore(boss.Target(), Dials, Standard(400), isBoss: true);
            Assert.AreEqual(0, boss.Ceilings.Count, "the boss is exempt from the ceiling");

            var disabled = new Fake();
            disabled.Bases[Skill.MeleeDefense] = 900;
            CombatTraitGuard.ApplyCore(disabled.Target(), new CombatGuardDials(0.5, 2.0, -1), Standard(400), false);
            Assert.AreEqual(0, disabled.Ceilings.Count, "a negative offset disables the ceiling");
        }

        [TestMethod]
        public void ApplyCore_AppendsTheItemSweepChanges()
        {
            var f = new Fake { Items = new List<string> { "IgnoreMagicResist@24567" } };

            var changes = CombatTraitGuard.ApplyCore(f.Target(), Dials, DungeonBandStandard.Empty, false);

            Assert.IsTrue(changes.Contains("IgnoreMagicResist@24567"));
        }

        // ---- Threads uplift attributes: pure cores ---------------------------------------------------------

        private static IReadOnlyDictionary<PropertyAttribute, uint> AttrMedians(uint str = 0, uint end = 0, uint coord = 0, uint quick = 0, uint focus = 0, uint self = 0)
            => new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = str, [PropertyAttribute.Endurance] = end, [PropertyAttribute.Coordination] = coord,
                [PropertyAttribute.Quickness] = quick, [PropertyAttribute.Focus] = focus, [PropertyAttribute.Self] = self,
            };

        [TestMethod]
        public void RaiseAttributeFloors_IsUpwardOnly_AndNeverAddsAnAttribute()
        {
            var medians = AttrMedians(str: 500, end: 400, coord: 300, quick: 200, focus: 0, self: 600);
            var bases = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = 100,       // below: raised
                [PropertyAttribute.Endurance] = 900,      // above: untouched
                [PropertyAttribute.Coordination] = 300,   // equal: untouched
                [PropertyAttribute.Quickness] = 0,        // zero base = not authored: never raised from nothing
                [PropertyAttribute.Focus] = 50,           // median 0 = no standard: untouched
                // Self: not carried at all
            };

            var raised = BandUplift.RaiseAttributeFloors(medians, a => bases.TryGetValue(a, out var v) ? v : (uint?)null, (a, v) => bases[a] = v);

            Assert.AreEqual(1, raised);
            Assert.AreEqual(500u, bases[PropertyAttribute.Strength]);
            Assert.AreEqual(900u, bases[PropertyAttribute.Endurance], "never lowered");
            Assert.AreEqual(300u, bases[PropertyAttribute.Coordination]);
            Assert.AreEqual(0u, bases[PropertyAttribute.Quickness], "a zero placeholder is not an authored attribute");
            Assert.AreEqual(50u, bases[PropertyAttribute.Focus]);
            Assert.IsFalse(bases.ContainsKey(PropertyAttribute.Self), "an attribute the creature lacks is never added");
        }

        [TestMethod]
        public void EffectiveSkillFloor_LandsTheEffectiveSkillOnTheMedian_AndNeverLowers()
        {
            // median 800, attribute term 300: InitLevel must reach 500.
            Assert.AreEqual(500u, BandUplift.EffectiveSkillFloor(800, 300, 100, 0));
            // already above the needed InitLevel: untouched, never lowered.
            Assert.AreEqual(700u, BandUplift.EffectiveSkillFloor(800, 300, 700, 0));
            // the attribute term alone already exceeds the median: the target is 0, current wins.
            Assert.AreEqual(40u, BandUplift.EffectiveSkillFloor(800, 900, 40, 0));
        }

        [TestMethod]
        public void EffectiveSkillFloor_NeverPushesTheEffectiveSkillPastTheCap()
        {
            // A cap BELOW the median (a configuration that should not occur, but the clamp is the contract):
            // target = min(800 - 300, 600 - 300) = 300.
            Assert.AreEqual(300u, BandUplift.EffectiveSkillFloor(800, 300, 100, 600));
            // cap at/above the median is inert.
            Assert.AreEqual(500u, BandUplift.EffectiveSkillFloor(800, 300, 100, 900));
            // never below the current InitLevel even when the cap would imply less.
            Assert.AreEqual(450u, BandUplift.EffectiveSkillFloor(800, 300, 450, 600));
        }

        private static DungeonBandStandard EffStandard(params (Skill, uint)[] effective)
            => DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 0, 0, effectiveSkillMedians: effective.ToDictionary(e => e.Item1, e => e.Item2));

        [TestMethod]
        public void TopUpEffectiveSkills_RaisesCarriedSkills_NeverAddsOne_AndFallsBackForAttackOnly()
        {
            var std = EffStandard((Skill.HeavyWeapons, 900), (Skill.MeleeDefense, 600));
            var init = new Dictionary<Skill, uint>
            {
                [Skill.HeavyWeapons] = 100,   // own median 900, term 200 -> 700
                [Skill.Axe] = 100,            // no median of its own: falls back to the highest attack median (900) -> 700
                [Skill.MeleeDefense] = 50,    // 600 - 200 = 400
                // MissileDefense not carried (no median either), WarMagic not carried: never added
            };

            var raised = BandUplift.TopUpEffectiveSkills(std, s => init.TryGetValue(s, out var v) ? v : (uint?)null, s => 200u, s => 0u, (s, v) => init[s] = v);

            Assert.AreEqual(3, raised);
            Assert.AreEqual(700u, init[Skill.HeavyWeapons]);
            Assert.AreEqual(700u, init[Skill.Axe], "attack skill with no median falls back to the highest effective attack median");
            Assert.AreEqual(400u, init[Skill.MeleeDefense]);
            Assert.IsFalse(init.ContainsKey(Skill.WarMagic));
            Assert.IsFalse(init.ContainsKey(Skill.MissileDefense), "a skill the creature does not carry is never created");
        }

        [TestMethod]
        public void TopUpEffectiveSkills_DefenseNeverBorrowsAnotherSkillsMedian()
        {
            var std = EffStandard((Skill.MeleeDefense, 600), (Skill.HeavyWeapons, 900));
            var init = new Dictionary<Skill, uint> { [Skill.MissileDefense] = 10 };

            var raised = BandUplift.TopUpEffectiveSkills(std, s => init.TryGetValue(s, out var v) ? v : (uint?)null, s => 0u, s => 0u, (s, v) => init[s] = v);

            Assert.AreEqual(0, raised);
            Assert.AreEqual(10u, init[Skill.MissileDefense]);
        }

        [TestMethod]
        public void TopUpEffectiveSkills_ClampsDefenseToTheCap_ButNotAttack()
        {
            var std = EffStandard((Skill.MeleeDefense, 800), (Skill.HeavyWeapons, 800));
            var init = new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 0, [Skill.HeavyWeapons] = 0 };

            // capOf returns 500 for every skill; only the DEFENSE skill may consult it.
            BandUplift.TopUpEffectiveSkills(std, s => init.TryGetValue(s, out var v) ? v : (uint?)null, s => 100u, s => 500u, (s, v) => init[s] = v);

            Assert.AreEqual(400u, init[Skill.MeleeDefense], "defense: min(800 - 100, 500 - 100) = 400, so effective = 500 = the cap");
            Assert.AreEqual(700u, init[Skill.HeavyWeapons], "attack skills carry no ceiling");
        }

        [TestMethod]
        public void TopUpEffectiveSkills_SkipsASkillTheCreatureCannotUse_AndAnEmptyStandard()
        {
            var std = EffStandard((Skill.HeavyWeapons, 900));
            var init = new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 100 };

            // initOf returns null for a skill that is carried but not usable (the glue's convention).
            Assert.AreEqual(0, BandUplift.TopUpEffectiveSkills(std, s => null, s => 0u, s => 0u, (s, v) => init[s] = v));
            Assert.AreEqual(0, BandUplift.TopUpEffectiveSkills(DungeonBandStandard.Empty, s => init.TryGetValue(s, out var v) ? v : (uint?)null, s => 0u, s => 0u, (s, v) => init[s] = v));
            Assert.AreEqual(100u, init[Skill.HeavyWeapons]);
        }

        // ---- defense-ceiling decision (CeilingsToSet) ------------------------------------------------------

        // effective median 500 + offset 100 = cap 600 for MeleeDefense.
        private static System.Collections.Generic.List<(Skill Skill, uint Cap)> Ceilings(uint? raw, uint? existing = null, double offset = 100, bool applyCap = true)
            => BandUplift.CeilingsToSet(EffStandard((Skill.MeleeDefense, 500)), offset, applyCap,
                s => s == Skill.MeleeDefense ? raw : null, s => s == Skill.MeleeDefense ? existing : null);

        [TestMethod]
        public void CeilingsToSet_SetsACeilingOnlyWhenRawIsStrictlyAboveTheCap()
        {
            Assert.AreEqual(0, Ceilings(550).Count, "below the cap");
            Assert.AreEqual(0, Ceilings(600).Count, "exactly at the cap is not over it");

            var over = Ceilings(601);
            Assert.AreEqual(1, over.Count);
            Assert.AreEqual((Skill.MeleeDefense, 600u), over[0]);
        }

        [TestMethod]
        public void CeilingsToSet_DoesNotResetACeilingAlreadyEqualToTheCap()
        {
            Assert.AreEqual(0, Ceilings(900, existing: 600).Count, "the strip-time ceiling already stands");
            Assert.AreEqual(1, Ceilings(900, existing: 700).Count, "a different existing ceiling is rewritten to the cap");
        }

        [TestMethod]
        public void CeilingsToSet_SkipsUnusableSkills_NegativeOffsets_AndAStripThatIsOff()
        {
            Assert.AreEqual(0, Ceilings(null).Count, "absent or unusable skill (rawOf null)");
            Assert.AreEqual(0, Ceilings(900, offset: -1).Count, "a negative offset means no cap");
            Assert.AreEqual(0, Ceilings(900, applyCap: false).Count, "strip off: no ceiling is introduced");
            Assert.AreEqual(0, BandUplift.CeilingsToSet(DungeonBandStandard.Empty, 100, true, s => 900u, s => null).Count, "empty standard");
        }

        [TestMethod]
        public void RaisedEndurance_MirrorsTheAttributeFloor()
        {
            Assert.AreEqual(400u, BandUplift.RaisedEndurance(370, 400));
            Assert.AreEqual(500u, BandUplift.RaisedEndurance(500, 400), "never lowered");
            Assert.AreEqual(0u, BandUplift.RaisedEndurance(0, 400), "a zero base is not authored and is not raised");
            Assert.AreEqual(370u, BandUplift.RaisedEndurance(370, 0), "no median");
        }
    }
}
