using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RecastEffect: after a monster finishes casting a spell, a chance to immediately cast that same spell
    /// again, bounded by max= (this record's own chain depth) and by Creature.CanChainMonsterEffectCast /
    /// monster_effect_recast_cap (the shared system-wide latch).
    ///
    /// HARNESS LIMIT: OnCastComplete's actual recast calls caster.CastSpell(spell), and the dispatch site
    /// that reaches it (Creature.OnMonsterEffectCastComplete) is documented to take a REAL Spell - one this
    /// test assembly cannot construct (new Spell(...) needs DatManager.PortalDat, never loaded here; see
    /// TestGameTables). RecastEffect splits the guard-and-roll decision into the pure, dat-free ShouldChain,
    /// and Magnitude/CapClamp below exercise that directly. The Disabled test goes through the real
    /// dispatch with a NULL spell argument: Creature.OnMonsterEffectCastComplete never dereferences its
    /// spell parameter before the master-switch/index checks, so this is safe specifically because the
    /// switch is off and the handler is never reached.
    /// </summary>
    [TestClass]
    public class MonsterEffectRecastTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);

            PropertyManager.ModifyLong("monster_effect_recast_cap",
                DefaultPropertyManager.DefaultLongProperties["monster_effect_recast_cap"].Item);
        }

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"recast {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(new RecastEffect().Validate(Spec("chance=0.3"), out _));
            Assert.IsTrue(new RecastEffect().Validate(Spec("chance=0.3 max=2"), out _), "max= defaults to 1");

            Assert.IsFalse(new RecastEffect().Validate(Spec("chance=0"), out var error), "chance= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(new RecastEffect().Validate(Spec("chance=1.5"), out _), "chance= must be <= 1");
            Assert.IsFalse(new RecastEffect().Validate(Spec("chance=0.3 max=0"), out _), "max= must be > 0");
        }

        [TestMethod]
        public void Magnitude_ChancePassesAndMaxBoundsTheChainDepth()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            // ShouldChain reads the caster's own ramp axis=procchance before the cap; a creature with no
            // effects attached contributes a neutral 1.0
            var caster = TestCreatures.CreateDefender();

            var spec = Spec("chance=1.0 max=2");
            var state = new MonsterEffectState();

            Assert.IsTrue(RecastEffect.ShouldChain(caster, spec, ref state), "depth 0 of a max=2 budget must be allowed");

            state.Stacks = 1;
            Assert.IsTrue(RecastEffect.ShouldChain(caster, spec, ref state), "depth 1 of a max=2 budget must still be allowed");

            state.Stacks = 2;
            Assert.IsFalse(RecastEffect.ShouldChain(caster, spec, ref state), "depth 2 of a max=2 budget must be refused");
        }

        [TestMethod]
        public void CapClamp_ChanceIsClampedByTheProcChanceCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 0.0));

            var caster = TestCreatures.CreateDefender();

            var spec = Spec("chance=1.0 max=1");
            var state = new MonsterEffectState();

            Assert.IsFalse(RecastEffect.ShouldChain(caster, spec, ref state), "a cap of zero must let nothing through");
        }

        /// <summary>
        /// monster_effect_recast_cap says "maximum extra casts one completed cast may chain into", and it
        /// only means that if the chain ADDS per record rather than MULTIPLYING. Chaining used to work by
        /// re-entering Creature.OnMonsterEffectCastComplete, which re-ran every cast-hook record the monster
        /// carried, so two records branched by two per level: at a cap of 2 they produced six extra casts
        /// between them, not four.
        ///
        /// Both budgets are exercised: the shared depth (raised here to 3) is what bounds each record, and
        /// two records must cost exactly twice one record rather than some power of it. The dispatcher's own
        /// level is claimed explicitly so the depth arithmetic matches a real cast completion, which is
        /// where OnCastComplete is actually called from.
        /// </summary>
        [TestMethod]
        public void Chain_IsLinearInTheRecordCountRatherThanCombinatorial()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));
            Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_recast_cap", 3));

            var caster = TestCreatures.CreateDefender();
            var effect = new RecastEffect();

            var a = Spec("chance=1.0 max=99");
            var b = Spec("chance=1.0 max=99");

            caster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((effect, a), (effect, b)));

            var states = caster.MonsterEffectStates;

            // stand in for the level Creature.OnMonsterEffectCastComplete claims around a real dispatch
            Assert.IsTrue(Creature.TryEnterMonsterEffectCastChain());

            int one, two;

            try
            {
                one = RecastEffect.Chain(caster, a, ref states[0], () => { });
                two = one + RecastEffect.Chain(caster, b, ref states[1], () => { });
            }
            finally
            {
                Creature.ExitMonsterEffectCastChain();
            }

            Assert.AreEqual(3, one, "one record chains up to monster_effect_recast_cap and no further");
            Assert.AreEqual(6, two, "two records cost twice one record - added, not multiplied");
        }

        /// <summary>
        /// The chain must leave both budgets exactly as it found them, or the next completed cast on this
        /// thread inherits a spent depth and silently refuses to chain at all.
        /// </summary>
        [TestMethod]
        public void Chain_UnwindsBothBudgetsWhenItEnds()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));
            Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_recast_cap", 2));

            var caster = TestCreatures.CreateDefender();
            var effect = new RecastEffect();

            var spec = Spec("chance=1.0 max=99");

            caster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((effect, spec)));

            var states = caster.MonsterEffectStates;

            var first = RecastEffect.Chain(caster, spec, ref states[0], () => { });

            Assert.AreEqual(0, states[0].Stacks, "the record's own depth budget must be handed back");
            Assert.IsTrue(Creature.CanChainMonsterEffectCast, "the shared depth must be handed back too");

            var second = RecastEffect.Chain(caster, spec, ref states[0], () => { });

            Assert.AreEqual(first, second, "a second completed cast must chain exactly as far as the first");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheHandlerFromEverRunning()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var caster = TestCreatures.CreateDefender();

            var effect = new RecastEffect();

            caster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((effect, Spec("chance=1.0 max=1"))));

            // safe ONLY because the master switch stops this before OnCastComplete (and therefore
            // caster.CastSpell(null)) ever runs - see the class doc comment
            caster.OnMonsterEffectCastComplete(null);

            Assert.AreEqual(0, caster.MonsterEffectStates[0].Stacks, "the handler must never have been entered");
        }
    }
}
