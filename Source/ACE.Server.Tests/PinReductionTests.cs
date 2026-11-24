using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pinning Shot's defensive rider: the pure magnitude rule (PinReductionMath), the victim-side slot
    /// (Creature.TryApplyPinDamageReduction / GetPinOutgoingDamageMod) on explicit clocks, its relationship to the
    /// pin and the post-pin slow window, the shared read gate, and the Pinning Shot description. The four read
    /// sites are pinned separately by PinReductionWiringSourceScanTests.
    ///
    /// The physical read site is exercised end to end (DamageEvent.CalculateDamage with a monster attacker and a
    /// CombatPet defender, below). The other three (SpellProjectile.DamageTarget, Harm, Drain Health) need a live
    /// landblock, a Spell and a vital write, which this harness cannot build; they are pinned by the source-scan
    /// class instead. A Player defender cannot be constructed here at all (the Player static initializer does not
    /// run under the test host), so the Player half of the gate is a source pin. TryPin itself needs a Player
    /// attacker, so the window arithmetic is tested through the Creature entry points it calls.
    ///
    /// The description test reads PropertyManager, so the defaults are seeded in ClassInitialize (no database).
    /// The state tests pass every magnitude and clock explicitly.
    /// </summary>
    [TestClass]
    public class PinReductionTests
    {
        private const double Tolerance = 1e-9;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static int nextPetGuid = 0x7F3D0000;

        private static CombatPet NewPet()
        {
            TestGameTables.EnsureInitialized();

            var guid = (uint)System.Threading.Interlocked.Increment(ref nextPetGuid);
            return new CombatPet(new ACE.Entity.Models.Weenie { WeenieClassId = 42, WeenieType = WeenieType.CombatPet }, new ACE.Entity.ObjectGuid(guid));
        }

        private static Creature LiveMonster()
        {
            var monster = TestCreatures.CreateDefender(maxHealth: 50);

            Assert.IsFalse(monster.IsDead, "the fixture monster must be alive, or a refusal proves nothing");

            return monster;
        }

        // ---- magnitude -------------------------------------------------------------------------------

        [TestMethod]
        public void Magnitude_AtNeutralAffinity_IsTheFlatBase()
        {
            Assert.AreEqual(0.30, PinReductionMath.Magnitude(0.30, 1.0, 0.10), Tolerance);
        }

        [TestMethod]
        public void Magnitude_NonPositiveBase_IsZero()
        {
            Assert.AreEqual(0.0, PinReductionMath.Magnitude(0.0, 1.5, 0.10), Tolerance);
            Assert.AreEqual(0.0, PinReductionMath.Magnitude(-0.3, 1.5, 0.10), Tolerance);
        }

        [TestMethod]
        public void Magnitude_AffinityMultipliesTheBase_BelowTheCap()
        {
            // 0.30 x 1.2 adds 0.06, under the 0.10 cap
            Assert.AreEqual(0.36, PinReductionMath.Magnitude(0.30, 1.2, 0.10), Tolerance);
        }

        [TestMethod]
        public void Magnitude_AffinityAddedAmountIsCapped_SoTheMaximumIsFortyPercent()
        {
            // 0.30 x 1.5 would add 0.15, capped to 0.10 -> 0.40
            Assert.AreEqual(0.40, PinReductionMath.Magnitude(0.30, 1.5, 0.10), Tolerance);

            // a huge multiplier still adds only the cap
            Assert.AreEqual(0.40, PinReductionMath.Magnitude(0.30, 40.0, 0.10), Tolerance);
        }

        [TestMethod]
        public void Magnitude_CapOfZeroMeansUncapped()
        {
            Assert.AreEqual(0.45, PinReductionMath.Magnitude(0.30, 1.5, 0.0), Tolerance);
        }

        [TestMethod]
        public void Magnitude_MultiplierBelowOne_DegradesToTheBase()
        {
            // the neutral value of the OLD additive primitive is 0.0; it must not multiply the base away
            Assert.AreEqual(0.30, PinReductionMath.Magnitude(0.30, 0.0, 0.10), Tolerance);
            Assert.AreEqual(0.30, PinReductionMath.Magnitude(0.30, 0.5, 0.10), Tolerance);
        }

        [TestMethod]
        public void Magnitude_NeverReachesFullImmunity()
        {
            Assert.AreEqual(PinReductionMath.MaxReduction, PinReductionMath.Magnitude(5.0, 1.0, 0.0), Tolerance);
            Assert.IsTrue(PinReductionMath.MaxReduction < 1.0);
        }

        [TestMethod]
        public void ToMultiplier_IsOneMinusTheReduction()
        {
            Assert.AreEqual(1.0f, PinReductionMath.ToMultiplier(0.0), 0.0f);
            Assert.AreEqual(1.0f, PinReductionMath.ToMultiplier(-0.2), 0.0f);
            Assert.AreEqual(0.70f, PinReductionMath.ToMultiplier(0.30), 1e-6f);
            Assert.AreEqual(0.1f, PinReductionMath.ToMultiplier(5.0), 1e-6f, "an out-of-range reduction is clamped to the maximum");
        }

        [TestMethod]
        public void Tunables_MatchSignedOffDefaults()
        {
            Assert.AreEqual(0.30, PropertyManager.GetDouble("class_ability_pinningshot_damage_reduction").Item, Tolerance);
            Assert.AreEqual(0.20, PropertyManager.GetDouble("class_ability_pinningshot_damage_reduction_affinity_cap").Item, Tolerance);
        }

        // ---- the shared read gate --------------------------------------------------------------------

        [TestMethod]
        public void AppliesTo_MonsterOnCombatPet_IsTrue()
        {
            Assert.IsTrue(PinReductionMath.AppliesTo(LiveMonster(), NewPet()));
        }

        [TestMethod]
        public void AppliesTo_MonsterOnMonster_IsFalse()
        {
            Assert.IsFalse(PinReductionMath.AppliesTo(LiveMonster(), LiveMonster()));
        }

        [TestMethod]
        public void AppliesTo_PetAttackingAMonster_IsFalse()
        {
            Assert.IsFalse(PinReductionMath.AppliesTo(NewPet(), LiveMonster()));
        }

        [TestMethod]
        public void AppliesTo_SelfAndNulls_AreFalse()
        {
            var pet = NewPet();

            Assert.IsFalse(PinReductionMath.AppliesTo(pet, pet));
            Assert.IsFalse(PinReductionMath.AppliesTo(null, pet));
            Assert.IsFalse(PinReductionMath.AppliesTo(LiveMonster(), null));
        }

        // ---- victim-side slot ------------------------------------------------------------------------

        [TestMethod]
        public void UntouchedCreature_ReadsExactlyOne()
        {
            Assert.AreEqual(1.0f, LiveMonster().GetPinOutgoingDamageMod(100.0), 0.0f);
        }

        [TestMethod]
        public void FirstApplication_IsANewLanding_RefreshIsNot()
        {
            var monster = LiveMonster();

            Assert.IsTrue(monster.TryApplyPinDamageReduction(0.30, 115.0, 100.0), "first application is a new landing");
            Assert.IsFalse(monster.TryApplyPinDamageReduction(0.30, 115.0, 105.0), "an identical refresh of a live pin reduction is not");

            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(105.0), 1e-6f);
        }

        [TestMethod]
        public void Entry_LapsesAtItsEndTime_AndThenReadsOne()
        {
            var monster = LiveMonster();

            monster.TryApplyPinDamageReduction(0.30, 115.0, 100.0);

            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(114.9), 1e-6f, "still live just before the end");
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(115.0), 0.0f, "lapsed at the end time");
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(90.0), 0.0f, "state was cleared once it lapsed");
        }

        [TestMethod]
        public void LapsedEntry_CountsAsAFreshLandingOnTheNextApplication()
        {
            var monster = LiveMonster();

            monster.TryApplyPinDamageReduction(0.30, 115.0, 100.0);

            Assert.IsTrue(monster.TryApplyPinDamageReduction(0.30, 215.0, 200.0), "the earlier entry lapsed, so this lands fresh");
        }

        [TestMethod]
        public void PinWindow_ReductionIsActiveDuringThePinAndThroughTheSlow_ThenLapsesWhenTheSlowEnds()
        {
            // the defaults: a 5 second pin at t=100, then the 10 second slow window, so the slow (and the
            // reduction) end at 100 + 5 + 10 = 115. TryPin hands the creature exactly that end time.
            var monster = LiveMonster();
            const double pinStart = 100.0, pinSeconds = 5.0, slowSeconds = 10.0;

            monster.TryApplyPinDamageReduction(0.30, pinStart + pinSeconds + slowSeconds, pinStart);

            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(102.0), 1e-6f, "during the pin");
            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(105.5), 1e-6f, "after the pin ended, during the slow");
            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(114.9), 1e-6f, "right up to the slow's end");
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(115.0), 0.0f, "lapses when the slow ends");
        }

        [TestMethod]
        public void PinSlowWindowEnd_IsThePinEndPlusTheSlowSeconds_TheSameSumTheSlowIsWrittenWith()
        {
            var monster = LiveMonster();

            monster.ClassAbilityPinnedUntil = 105.0;

            Assert.AreEqual(115.0, monster.PinSlowWindowEnd(10.0), Tolerance);

            // the slow's own write (no running slow, pin live at `now`) ends at that same instant
            Assert.IsTrue(monster.ApplyPinSlowState(10.0, 0.2f, 101.0));
            Assert.AreEqual(monster.PinSlowWindowEnd(10.0), monster.ClassAbilityMoveSlowUntil, Tolerance);
        }

        [TestMethod]
        public void RePinDuringTheSlow_ExtendsTheWindow()
        {
            var monster = LiveMonster();

            // first pin t=100: pin 5s + slow 10s -> window ends 115
            monster.TryApplyPinDamageReduction(0.30, 115.0, 100.0);

            // re-pinned at t=113, after the 8s immunity from the first pin's end (105 + 8) passed, while the slow
            // still runs: new pin ends 118, new window ends 128
            monster.TryApplyPinDamageReduction(0.30, 128.0, 113.0);

            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(120.0), 1e-6f, "the re-pin extended the window past the first end");
            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(127.9), 1e-6f);
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(128.0), 0.0f);
        }

        [TestMethod]
        public void Refresh_NeverShortensAnExistingEnd_AndKeepsTheStrongerReduction()
        {
            var monster = LiveMonster();

            monster.TryApplyPinDamageReduction(0.40, 130.0, 100.0);

            // a later, weaker, earlier-ending write (e.g. a pin whose slow was refused) must change nothing
            Assert.IsFalse(monster.TryApplyPinDamageReduction(0.30, 110.0, 105.0));

            Assert.AreEqual(0.60f, monster.GetPinOutgoingDamageMod(125.0), 1e-6f, "neither the strength nor the end was reduced");
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(130.0), 0.0f);
        }

        [TestMethod]
        public void SlowRefused_ReductionEndsWithThePin()
        {
            // the caller (TryPin) passes the pin's own end when no slow was applied
            var monster = LiveMonster();

            monster.TryApplyPinDamageReduction(0.30, 105.0, 100.0);

            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(104.9), 1e-6f);
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(105.0), 0.0f, "no slow tail when no slow was applied");
        }

        [TestMethod]
        public void PinReductionEnd_AfterARealSlowRefusal_EndsWithThePin_AndAfterARealSlowIsThePinsSlowEnd()
        {
            const double now = 100.0;

            // pinned until 105; a stronger (0.1) slow is already running, so the pin's weaker (0.2) slow is refused
            var refused = LiveMonster();
            refused.ClassAbilityPinnedUntil = 105.0;
            Assert.IsTrue(refused.ApplyPinSlowState(10.0, 0.1f, now), "positive control: the first, stronger slow lands");
            Assert.IsFalse(refused.ApplyPinSlowState(10.0, 0.2f, now), "the weaker pin slow is refused by the running stronger one");

            Assert.AreEqual(105.0, PinningShotAbility.PinReductionEnd(refused, slowApplied: false, slowSeconds: 10.0), Tolerance,
                "a refused slow ends the reduction with the pin");

            // control: a slow that lands ends the reduction where the slow's window ends
            var landed = LiveMonster();
            landed.ClassAbilityPinnedUntil = 105.0;
            Assert.IsTrue(landed.ApplyPinSlowState(10.0, 0.2f, now));

            Assert.AreEqual(115.0, PinningShotAbility.PinReductionEnd(landed, slowApplied: true, slowSeconds: 10.0), Tolerance);
            Assert.AreEqual(landed.ClassAbilityMoveSlowUntil, PinningShotAbility.PinReductionEnd(landed, true, 10.0), Tolerance,
                "the same instant the slow itself ends");
        }

        [TestMethod]
        public void StrongerWrite_RaisesTheMagnitudeOfALiveEntry()
        {
            var monster = LiveMonster();

            monster.TryApplyPinDamageReduction(0.30, 115.0, 100.0);
            monster.TryApplyPinDamageReduction(0.40, 115.0, 101.0);

            Assert.AreEqual(0.60f, monster.GetPinOutgoingDamageMod(102.0), 1e-6f);
        }

        [TestMethod]
        public void RunawayReduction_IsClampedOnWrite()
        {
            var monster = LiveMonster();

            monster.TryApplyPinDamageReduction(7.0, 115.0, 100.0);

            Assert.AreEqual((float)(1.0 - PinReductionMath.MaxReduction), monster.GetPinOutgoingDamageMod(101.0), 1e-6f);
        }

        [TestMethod]
        public void ConcurrentWriters_NeverLoseTheStrongestOrLatestWrite()
        {
            for (var round = 0; round < 50; round++)
            {
                var monster = LiveMonster();

                System.Threading.Tasks.Parallel.For(1, 9, i =>
                    monster.TryApplyPinDamageReduction(0.05 * i, 100.0 + 10.0 * i, 100.0));

                // eight racing writers: the strongest (0.40) and the latest end (180) must both survive
                Assert.AreEqual(0.60f, monster.GetPinOutgoingDamageMod(101.0), 1e-6f, $"round {round}: strongest lost");
                Assert.AreEqual(0.60f, monster.GetPinOutgoingDamageMod(179.9), 1e-6f, $"round {round}: latest end lost");
                Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(180.0), 0.0f);
            }
        }

        // ---- refusals --------------------------------------------------------------------------------

        [TestMethod]
        public void NonPositiveReductionOrAnEndInThePast_IsRefused()
        {
            var monster = LiveMonster();

            Assert.IsFalse(monster.TryApplyPinDamageReduction(0.0, 115.0, 100.0));
            Assert.IsFalse(monster.TryApplyPinDamageReduction(-0.1, 115.0, 100.0));
            Assert.IsFalse(monster.TryApplyPinDamageReduction(0.3, 100.0, 100.0), "an end that is not in the future");
            Assert.IsFalse(monster.TryApplyPinDamageReduction(0.3, 90.0, 100.0));

            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(101.0), 0.0f, "nothing was written");
        }

        [TestMethod]
        public void CombatPet_IsRefused()
        {
            var pet = NewPet();

            Assert.IsFalse(pet.IsDead, "the fixture pet must be alive, or a refusal proves nothing");
            Assert.IsFalse(pet.TryApplyPinDamageReduction(0.30, 115.0, 100.0), "a player's own summon can never carry the debuff");
            Assert.AreEqual(1.0f, pet.GetPinOutgoingDamageMod(101.0), 0.0f);
        }

        [TestMethod]
        public void DeadMonster_IsRefused()
        {
            var monster = LiveMonster();

            monster.UpdateVitalDelta(monster.Health, -(int)(monster.Health.MaxValue + 500));

            Assert.IsTrue(monster.IsDead, "positive control: the fixture really is dead");
            Assert.IsFalse(monster.TryApplyPinDamageReduction(0.30, 115.0, 100.0));
            Assert.AreEqual(1.0f, monster.GetPinOutgoingDamageMod(101.0), 0.0f, "nothing was written");
        }

        [TestMethod]
        public void GamePiece_IsRefused()
        {
            var piece = ElementalProficiencyTestFixtures.LiveGamePiece();

            Assert.IsFalse(piece.TryApplyPinDamageReduction(0.30, 115.0, 100.0));
        }

        [TestMethod]
        public void RealClockOverload_AppliesAndReadsOnTheSameClock()
        {
            var monster = LiveMonster();

            Assert.IsTrue(monster.TryApplyPinDamageReduction(0.30, Timers.RunningTime + 30.0));
            Assert.IsTrue(monster.HasPinDamageReduction);
            Assert.AreEqual(0.70f, monster.GetPinOutgoingDamageMod(), 1e-6f);
        }

        // ---- physical read site, end to end ----------------------------------------------------------

        private static CombatPet NewTargetPet()
        {
            TestGameTables.EnsureInitialized();

            // a single body part reachable from every quadrant, as TestCreatures.CreateDefender builds, so the
            // hit-location roll always lands and the defender half runs for real
            var chest = new ACE.Entity.Models.PropertiesBodyPart
            {
                DType = DamageType.Bludgeon,
                DVal = 1,
                DVar = 0.5f,
                BaseArmor = 0,
                HLF = 1, MLF = 1, LLF = 1, HRF = 1, MRF = 1, LRF = 1,
                HLB = 1, MLB = 1, LLB = 1, HRB = 1, MRB = 1, LRB = 1,
            };

            var wcid = TestCreatures.NextWcid();

            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.CombatPet,
                PropertiesAttribute2nd = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute2nd, ACE.Entity.Models.PropertiesAttribute2nd>
                {
                    { ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth, new ACE.Entity.Models.PropertiesAttribute2nd { InitLevel = 5000 } },
                },
                PropertiesBodyPart = new System.Collections.Generic.Dictionary<CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart>
                {
                    { CombatBodyPart.Chest, chest },
                },
            };

            var guid = (uint)System.Threading.Interlocked.Increment(ref nextPetGuid);
            var pet = new CombatPet(weenie, new ACE.Entity.ObjectGuid(guid));

            pet.Location = new ACE.Entity.Position(0x00010064, 55.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            Creature.SetBodyPartTable(wcid, new BodyPartTable(weenie));

            return pet;
        }

        private static float DefenderHalfProduct(DamageEvent e) =>
            e.DamageBeforeMitigation * e.ArmorMod * e.ShieldMod * e.ResistanceMod * e.DamageResistanceRatingMod;

        [TestMethod]
        public void CalculateDamage_PinnedMonsterOnCombatPet_DealsTheReducedHit_UnpinnedIsUnchanged()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 100, strength: 100);
            var pet = NewTargetPet();

            // control first: no entry, so the hit is the plain defender-half product
            for (var i = 0; i < 25; i++)
            {
                var plain = DamageEvent.CalculateDamage(attacker, pet, null);

                Assert.IsFalse(plain.Evaded, "overpower attacker must connect");
                Assert.IsTrue(plain.Damage > 0.0f, "positive control: the fixture hit does damage");
                Assert.AreEqual(DefenderHalfProduct(plain), plain.Damage, 0.01f, "an unpinned monster's hit is untouched");
            }

            Assert.IsTrue(attacker.TryApplyPinDamageReduction(0.30, Timers.RunningTime + 3600.0), "the reduction lands on the monster");

            for (var i = 0; i < 25; i++)
            {
                var hit = DamageEvent.CalculateDamage(attacker, pet, null);

                Assert.IsFalse(hit.Evaded);
                Assert.AreEqual(DefenderHalfProduct(hit) * 0.70f, hit.Damage, 0.01f, "the pinned hit is 30% lighter");
                Assert.AreEqual(hit.DamageBeforeMitigation - hit.Damage, hit.DamageMitigated, 0.01f, "DamageMitigated is rewritten with Damage");
            }
        }

        [TestMethod]
        public void CalculateDamage_PinnedMonsterOnAnotherMonster_IsNotReduced()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 100, strength: 100);
            var victim = TestCreatures.CreateDefender(maxHealth: 5000);

            attacker.TryApplyPinDamageReduction(0.30, Timers.RunningTime + 3600.0);

            var hit = DamageEvent.CalculateDamage(attacker, victim, null);

            Assert.IsFalse(hit.Evaded);
            Assert.AreEqual(DefenderHalfProduct(hit), hit.Damage, 0.01f, "the gate is player / combat pet defenders only");
        }

        // ---- Pinning Shot text -----------------------------------------------------------------------

        [TestMethod]
        public void PinningShotDescription_StatesTheReductionTheAffinityDegreeAndTheDotExclusion()
        {
            var text = ClassAbilityText.Describe(new PinningShotAbility().Definition, ClassAbilityText.Defaults);

            StringAssert.Contains(text, "deal 30% less damage while it is pinned and while it is slowed afterwards");
            StringAssert.Contains(text, "damage over time is not reduced");
            StringAssert.Contains(text, "Higher Assess Creature adds to that reduction, by at most 20 percentage points");
        }
    }
}
