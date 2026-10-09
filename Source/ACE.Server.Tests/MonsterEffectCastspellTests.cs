using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CastspellEffect: on a landed hit or landed spell hit, casts the authored spell= at the target through
    /// WorldObject.TryCastSpell.
    ///
    /// HARNESS LIMIT: the actual cast constructs a live Spell (new Spell(spellId)), which reads
    /// DatManager.PortalDat - the client dat this test assembly never loads (see TestGameTables, which
    /// installs only synthetic skill/vital formula tables, not a real PortalDat). Exercising OnOutgoingHit/
    /// OnSpellHit end-to-end with a chance high enough to actually fire would therefore NPE. CastspellEffect
    /// splits the roll-and-select decision into the pure, dat-free TryResolveCast, and the Magnitude/
    /// CapClamp tests below exercise that directly instead of the full dispatch. The Disabled test DOES go
    /// through the real dispatch, because the master switch short-circuits before the handler (and
    /// therefore before any Spell construction) ever runs.
    /// </summary>
    [TestClass]
    public class MonsterEffectCastspellTests
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
        }

        private static readonly CastspellEffect Effect = new CastspellEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"castspell {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("spell=FlameBolt1 chance=0.3"), out _));

            Assert.IsFalse(Effect.Validate(Spec("chance=0.3"), out var error), "spell= is required");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("spell=NotARealSpellName chance=0.3"), out _), "spell= must be a known SpellId name");
            Assert.IsFalse(Effect.Validate(Spec("spell=FlameBolt1 chance=0"), out _), "chance= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("spell=FlameBolt1 chance=1.5"), out _), "chance= must be <= 1");
        }

        [TestMethod]
        public void Magnitude_ResolvesTheAuthoredSpellOnlyForTheConfiguredTrigger()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            // TryResolveCast reads the caster's own ramp axis=procchance before the cap; a creature with no
            // effects attached contributes a neutral 1.0
            var caster = TestCreatures.CreateDefender();

            var hitSpec = Spec("spell=FlameBolt1 chance=1.0");

            Assert.IsTrue(CastspellEffect.TryResolveCast(caster, hitSpec, MonsterEffectTrigger.Hit, out var spellId));
            Assert.AreEqual(SpellId.FlameBolt1, spellId);

            // default on= is Hit only, so the spellhit trigger must not resolve
            Assert.IsFalse(CastspellEffect.TryResolveCast(caster, hitSpec, MonsterEffectTrigger.SpellHit, out _));

            var spellHitSpec = Spec("spell=FlameBolt1 chance=1.0 on=spellhit");

            Assert.IsTrue(CastspellEffect.TryResolveCast(caster, spellHitSpec, MonsterEffectTrigger.SpellHit, out var spellId2));
            Assert.AreEqual(SpellId.FlameBolt1, spellId2);
        }

        [TestMethod]
        public void CapClamp_ChanceIsClampedByTheProcChanceCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 0.0));

            var caster = TestCreatures.CreateDefender();

            var spec = Spec("spell=FlameBolt1 chance=1.0");

            Assert.IsFalse(CastspellEffect.TryResolveCast(caster, spec, MonsterEffectTrigger.Hit, out _), "a cap of zero must let nothing through");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheHandlerFromEverRunning()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("spell=FlameBolt1 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            // the master switch must stop this before OnOutgoingHit (and therefore new Spell(...)) ever
            // runs - if it did not, this call would throw (see the class doc comment), so a green test here
            // is itself proof the switch works, not just an absence of a thrown exception by luck
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
        }
    }
}
