using System.IO;

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
    /// The pet Mana Rush (Revenant Lich, 2026-11-02): ramp on=cast accrues a stack per completed cast
    /// (RampEffect.OnCastComplete through Creature.OnMonsterEffectCastComplete), the caster-pet cast path dispatches that
    /// hook with chaining off (a recast would re-cast at AttackTarget), and for a COMBAT PET ONLY the magic-delay gate is
    /// divided by the cast-axis multiplier (CombatPet.CasterMagicDelay). An ordinary monster's NextMagicAttackTime keeps
    /// the unscaled delay, which is what keeps the live castspeed ramps (Aun Shaman and friends) bit-identical.
    /// Every tunable read is seeded here and restored after each test.
    /// </summary>
    [TestClass]
    public class PetManaRushTests
    {
        private const double Epsilon = 1e-6;

        private const string LichManaRush = "axis=castspeed on=cast per=0.066 max=5 window=20";

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables() => RestoreTunables();

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_speed_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_speed_cap"].Item);

            PropertyManager.ModifyLong("monster_effect_ramp_stack_cap",
                DefaultPropertyManager.DefaultLongProperties["monster_effect_ramp_stack_cap"].Item);

            PropertyManager.ModifyLong("monster_effect_recast_cap",
                DefaultPropertyManager.DefaultLongProperties["monster_effect_recast_cap"].Item);
        }

        private static readonly RampEffect Ramp = new RampEffect();

        private static MonsterEffectSpec Spec(string text)
        {
            MonsterEffectParser.Parse(text, out var specs, out _);
            return specs[0];
        }

        private static int nextGuid = 0x7F2B0000;

        private static CombatPet NewPet()
        {
            TestGameTables.EnsureInitialized();

            var guid = (uint)System.Threading.Interlocked.Increment(ref nextGuid);
            return new CombatPet(new ACE.Entity.Models.Weenie { WeenieClassId = 42, WeenieType = WeenieType.CombatPet }, new ACE.Entity.ObjectGuid(guid));
        }

        // ---- the trigger ----

        [TestMethod]
        public void Validate_AcceptsOnCast_AndRejectsAnUnknownTrigger_NamingCast()
        {
            Assert.IsTrue(Ramp.Validate(Spec("ramp " + LichManaRush), out _), "the Lich's own string must validate");
            Assert.IsTrue(Ramp.Validate(Spec("ramp axis=attackspeed on=hit,avoid per=0.04 max=5 window=8"), out _), "existing multi-trigger strings stay valid");
            Assert.IsTrue(Ramp.Validate(Spec("ramp axis=magicdamage on=spellhit per=0.1 max=5 window=10"), out _));

            Assert.IsFalse(Ramp.Validate(Spec("ramp axis=castspeed on=cats per=0.066 max=5 window=20"), out var error), "a misspelt trigger must not fall back to hit");
            StringAssert.Contains(error, "hit|spellhit|avoid|cast");

            Assert.IsFalse(Ramp.Validate(Spec("ramp axis=castspeed on=spawn per=0.066 max=5 window=20"), out _), "a real trigger a ramp never answers is refused too");
        }

        [TestMethod]
        public void OnCast_AccruesOneStackPerCompletedCast_UpToMax_AndOtherTriggersDoNot()
        {
            var caster = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            caster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ramp, Spec("ramp " + LichManaRush))));

            // a landed melee hit is not a cast
            caster.ApplyOutgoingHitMonsterEffects(defender, new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee });
            Assert.AreEqual(0, caster.MonsterEffectStates[0].Stacks, "on=cast must not accrue from a hit");

            for (var i = 0; i < 7; i++)
                caster.OnMonsterEffectCastComplete(null);

            Assert.AreEqual(5, caster.MonsterEffectStates[0].Stacks, "seven casts, clamped at max=5");

            // an on=hit ramp ignores the cast hook
            var hitRamp = TestCreatures.CreateAttacker();
            hitRamp.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ramp, Spec("ramp axis=castspeed on=hit per=0.066 max=5 window=20"))));
            hitRamp.OnMonsterEffectCastComplete(null);
            Assert.AreEqual(0, hitRamp.MonsterEffectStates[0].Stacks, "on=hit must not accrue from a cast");
        }

        [TestMethod]
        public void FiveStacks_Of0066_AreExactly133_OnTheCastAxis()
        {
            var caster = TestCreatures.CreateAttacker();
            caster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ramp, Spec("ramp " + LichManaRush))));
            caster.MonsterEffectStates[0].Stacks = 5;

            // ScaleMonsterEffectCastTime divides by the multiplier: 1.33 / 1.33 is 1 to float precision
            Assert.AreEqual(1.0f, caster.ScaleMonsterEffectCastTime(1.33f), 1e-5f, "1 + 5 * 0.066 must be 1.33 (below the speed cap of 2)");
        }

        // ---- the gate: pet scaled, non-pet unchanged ----

        [TestMethod]
        public void NonPet_WithACastSpeedRamp_KeepsTheUnscaled3sMagicDelay()
        {
            var monster = TestCreatures.CreateAttacker();
            monster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ramp, Spec("ramp " + LichManaRush))));
            monster.MonsterEffectStates[0].Stacks = 5;

            monster.PrevAttackTime = 100.0;

            Assert.AreEqual(103.0, monster.NextMagicAttackTime, Epsilon, "a non-pet's magic gate ignores the cast-speed ramp");

            // the ramp still shortens that monster's ANIMATIONS, as it always did
            Assert.AreEqual(3.0f / 1.33f, monster.ScaleMonsterEffectCastTime(3.0f), 1e-4f);
        }

        [TestMethod]
        public void Pet_CasterMagicDelay_IsDividedByTheCastMultiplier_AndUnscaledWithoutStacks()
        {
            var pet = NewPet();

            Assert.AreEqual(3.0, pet.CasterMagicDelay, Epsilon, "no effects: exactly the 3 s default");
            pet.PrevAttackTime = 50.0;
            Assert.AreEqual(pet.NextMagicAttackTime - pet.PrevAttackTime, pet.CasterMagicDelay, Epsilon, "no effects: identical to the stock gate");

            pet.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ramp, Spec("ramp " + LichManaRush))));

            Assert.AreEqual(3.0, pet.CasterMagicDelay, Epsilon, "a ramp holding no stacks changes nothing");

            pet.MonsterEffectStates[0].Stacks = 5;

            Assert.AreEqual(3.0 / 1.33, pet.CasterMagicDelay, 1e-5, "five stacks divide the gate by 1.33");
            Assert.AreEqual(53.0, pet.NextMagicAttackTime, Epsilon, "the stock NextMagicAttackTime itself is untouched, even on a pet");
        }

        [TestMethod]
        public void Pet_CasterMagicDelay_RespectsTheSpeedCap()
        {
            var pet = NewPet();
            pet.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ramp, Spec("ramp axis=castspeed on=cast per=1.0 max=5 window=20"))));
            pet.MonsterEffectStates[0].Stacks = 5;

            Assert.AreEqual(1.5, pet.CasterMagicDelay, Epsilon, "6x is clamped to monster_effect_speed_cap 2");

            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_speed_cap", 1.25));
            Assert.AreEqual(3.0 / 1.25, pet.CasterMagicDelay, Epsilon, "the cap is read live");
        }

        // ---- the pet cast path: chaining off ----

        /// <summary>A cast hook that says it chains, recording whether it was asked.</summary>
        private sealed class ChainingHook : IMonsterCastHook
        {
            public string Kind => "recast";
            public bool StartsCasts => true;
            public int Calls;

            public bool Validate(MonsterEffectSpec spec, out string error)
            {
                error = null;
                return true;
            }

            public void OnCastComplete(Creature caster, Spell spell, MonsterEffectSpec spec, ref MonsterEffectState state) => Calls++;
        }

        [TestMethod]
        public void ChainingOff_SkipsTheRecastHook_ButStillAccruesManaRush()
        {
            var chainer = new ChainingHook();
            var pet = NewPet();

            pet.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (chainer, Spec("recast chance=1.0 max=1")),
                (Ramp, Spec("ramp " + LichManaRush))));

            var rampSlot = TestCreatures.MonsterEffectSlot(pet, Ramp);

            pet.OnMonsterEffectCastComplete(null, allowChaining: false);

            Assert.AreEqual(0, chainer.Calls, "the pet path never runs a handler that starts casts");
            Assert.AreEqual(1, pet.MonsterEffectStates[rampSlot].Stacks, "Mana Rush still counts the cast");

            pet.OnMonsterEffectCastComplete(null);

            Assert.AreEqual(1, chainer.Calls, "the default (MagicAttack) path still runs it");
            Assert.AreEqual(2, pet.MonsterEffectStates[rampSlot].Stacks);

            Assert.IsTrue(new RecastEffect() is IMonsterCastHook recast && recast.StartsCasts, "the real recast declares that it chains");
            Assert.IsFalse(((IMonsterCastHook)Ramp).StartsCasts, "a ramp never starts a cast");
        }

        /// <summary>
        /// The wiring, read from source: the pet cast path dispatches the cast hook with chaining off, the pet gate reads
        /// CasterMagicDelay, and MagicAttack's own dispatch is unchanged.
        /// </summary>
        [TestMethod]
        public void Wiring_PetCastPathAndGate_AreConnected()
        {
            var magic = File.ReadAllText(SourcePath("Monster_Magic.cs"));
            var caster = File.ReadAllText(SourcePath("CombatPet_Caster.cs"));

            var explicitStart = magic.IndexOf("internal bool TryStartExplicitCast(");
            Assert.IsTrue(explicitStart >= 0);
            var explicitBody = magic.Substring(explicitStart, magic.IndexOf("private const float PreCastSpeed", explicitStart) - explicitStart);

            StringAssert.Contains(explicitBody, "OnMonsterEffectCastComplete(spell, allowChaining: false);");

            var magicAttack = magic.Substring(magic.IndexOf("private void MagicAttack()"), explicitStart - magic.IndexOf("private void MagicAttack()"));
            StringAssert.Contains(magicAttack, "OnMonsterEffectCastComplete(spell);");
            Assert.IsFalse(magicAttack.Contains("allowChaining"), "the monster path keeps chaining on");

            StringAssert.Contains(caster, "now >= PrevAttackTime + CasterMagicDelay");
            Assert.IsFalse(caster.Contains("now >= NextMagicAttackTime"), "the pet gate must not read the unscaled delay");
        }

        private static string SourcePath(string file)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", file)) && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", file)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {file} by walking up from {System.AppContext.BaseDirectory}");

            var a = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", file);
            return File.Exists(a) ? a : Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", file);
        }
    }
}
