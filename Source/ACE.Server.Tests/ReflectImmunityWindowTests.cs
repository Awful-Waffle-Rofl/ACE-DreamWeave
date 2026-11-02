using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Reflect's creature-spell immunity window (Vanguard T3, owner ruling 2026-10-17): after a non-player
    /// creature's spell damages the Vanguard, further creature spell damage is blocked for 0.15/0.30/0.45s by
    /// rank, multiplied by the Magic Defense affinity with no cap.
    ///
    /// EXERCISED THROUGH THE PURE STATIC HELPERS, never through a live Player, because Player's static
    /// initializer cannot run under this test host (see PreWriteDamageHookTests's class summary). The
    /// helpers are the ones the runtime path calls: Player.ApplyPreWriteDamageClassAbilities sizes the share
    /// with ClassAbilityAttackerFilter.SpellDamageShare, the DoT tick site with EnemyCreatureShareOfTick, and
    /// Player.ApplyReflectImmunityWindow applies ReflectMagicAbility.DecideImmunity.
    /// </summary>
    [TestClass]
    public class ReflectImmunityWindowTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved - every
            // key this class reads is a registered default
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double SecondsBase => PropertyManager.GetDouble("class_ability_reflectmagic_immunity_seconds_base").Item;
        private static double SecondsStep => PropertyManager.GetDouble("class_ability_reflectmagic_immunity_seconds_step").Item;

        // ---- the window length ------------------------------------------------------------------------

        [TestMethod]
        public void WindowLadder_Is015_030_045_AtNeutralAffinity()
        {
            Assert.AreEqual(0.15, SecondsBase, 1e-9, "registered default moved - the 0.15/0.30/0.45 ladder is the owner ruling");
            Assert.AreEqual(0.15, SecondsStep, 1e-9, "registered default moved - the 0.15/0.30/0.45 ladder is the owner ruling");

            Assert.AreEqual(0.15, ReflectMagicAbility.ImmunityWindowSeconds(1, SecondsBase, SecondsStep, 1.0), 1e-9);
            Assert.AreEqual(0.30, ReflectMagicAbility.ImmunityWindowSeconds(2, SecondsBase, SecondsStep, 1.0), 1e-9);
            Assert.AreEqual(0.45, ReflectMagicAbility.ImmunityWindowSeconds(3, SecondsBase, SecondsStep, 1.0), 1e-9);

            Assert.AreEqual(0.0, ReflectMagicAbility.ImmunityWindowSeconds(0, SecondsBase, SecondsStep, 5.0), 0.0, "rank 0 has no window");
            Assert.AreEqual(0.0, ReflectMagicAbility.ImmunityWindowSeconds(-1, SecondsBase, SecondsStep, 5.0), 0.0);
        }

        [TestMethod]
        public void WindowAffinity_MultipliesAtTheNaturalRate_Uncapped()
        {
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            // a multiplier large enough that the added seconds dwarf the chance cap: if any cap were applied
            // to the window, this would come back short
            const double multiplier = 12.0;
            var expected = 0.45 * multiplier;

            Assert.IsTrue(expected - 0.45 > affinityCap, "control: the added amount must exceed the chance cap for this test to mean anything");

            Assert.AreEqual(expected, ReflectMagicAbility.ImmunityWindowSeconds(3, SecondsBase, SecondsStep, multiplier), 1e-9,
                "the window is multiplied by the affinity factor with NO cap");

            // 0.0 is the neutral value of the old additive primitive and must floor to 1.0, not zero the window
            Assert.AreEqual(0.45, ReflectMagicAbility.ImmunityWindowSeconds(3, SecondsBase, SecondsStep, 0.0), 1e-9);
        }

        // ---- what triggers it -------------------------------------------------------------------------

        [TestMethod]
        public void MeleeAndPvpHits_ArmNoWindow()
        {
            const double now = 1000.0;

            // melee / missile / hotspot: Player.TakeDamage passes no isSpell, so the share is 0 even from a
            // live monster
            var meleeShare = ClassAbilityAttackerFilter.SpellDamageShare(isSpell: false, hasEnemyCreatureAttacker: true, explicitShare: null, incomingDamage: 100);
            Assert.AreEqual(0u, meleeShare, "a weapon hit is never spell damage");

            // PvP: the shared filter leaves the attacker null for a player caster
            var pvpShare = ClassAbilityAttackerFilter.SpellDamageShare(isSpell: true, hasEnemyCreatureAttacker: false, explicitShare: null, incomingDamage: 100);
            Assert.AreEqual(0u, pvpShare, "a player's spell is not creature spell damage");

            foreach (var share in new[] { meleeShare, pvpShare })
            {
                var decision = ReflectMagicAbility.DecideImmunity(now, 0.0, share, 100, 0.45);

                Assert.AreEqual(0.0, decision.WindowEnd, 0.0, "no window may arm");
                Assert.AreEqual(100u, decision.Damage);
                Assert.AreEqual(0u, decision.Blocked);
            }

            // control: the same hit as a creature's spell DOES arm
            var creatureShare = ClassAbilityAttackerFilter.SpellDamageShare(isSpell: true, hasEnemyCreatureAttacker: true, explicitShare: null, incomingDamage: 100);
            Assert.AreEqual(100u, creatureShare);
            Assert.AreEqual(now + 0.45, ReflectMagicAbility.DecideImmunity(now, 0.0, creatureShare, 100, 0.45).WindowEnd, 1e-9);
        }

        [TestMethod]
        public void MeleeAndPvpHits_InsideALiveWindow_AreNotBlocked()
        {
            var decision = ReflectMagicAbility.DecideImmunity(1000.1, 1000.3, 0, 80, 0.45);

            Assert.AreEqual(80u, decision.Damage, "only creature spell damage is blocked");
            Assert.AreEqual(0u, decision.Blocked);
            Assert.AreEqual(1000.3, decision.WindowEnd, 0.0, "and the window is untouched");
        }

        [TestMethod]
        public void TheArmingHit_IsNotBlocked()
        {
            var decision = ReflectMagicAbility.DecideImmunity(1000.0, 0.0, 120, 120, 0.30);

            Assert.AreEqual(120u, decision.Damage, "the hit that arms the window lands in full");
            Assert.AreEqual(0u, decision.Blocked);
            Assert.AreEqual(1000.30, decision.WindowEnd, 1e-9);
        }

        // ---- blocking -------------------------------------------------------------------------------

        [TestMethod]
        public void ABlockedHit_DoesNotReArmOrExtendTheWindow()
        {
            const double windowEnd = 1000.30;

            var blocked = ReflectMagicAbility.DecideImmunity(1000.25, windowEnd, 60, 60, 0.30);

            Assert.AreEqual(0u, blocked.Damage, "a creature spell inside the window is blocked to 0");
            Assert.AreEqual(60u, blocked.Blocked);
            Assert.AreEqual(windowEnd, blocked.WindowEnd, 0.0, "a blocked hit must neither re-arm nor extend the window");

            // the first creature spell after the window lapses lands in full and arms a fresh one
            var after = ReflectMagicAbility.DecideImmunity(1000.31, blocked.WindowEnd, 60, 60, 0.30);

            Assert.AreEqual(60u, after.Damage);
            Assert.AreEqual(0u, after.Blocked);
            Assert.AreEqual(1000.61, after.WindowEnd, 1e-9);
        }

        [TestMethod]
        public void TheWindowEnd_IsExclusive()
        {
            var atEnd = ReflectMagicAbility.DecideImmunity(1000.30, 1000.30, 60, 60, 0.30);

            Assert.AreEqual(0u, atEnd.Blocked, "the window has closed at its end time");
            Assert.AreEqual(60u, atEnd.Damage);
        }

        [TestMethod]
        public void AMixedDotTick_BlocksOnlyTheCreatureShare()
        {
            // one tick from a monster's two enchantments plus a player's: 30.4 + 9.8 creature, 50 player
            var contributions = new[] { (true, 30.4f), (false, 50.0f), (true, 9.8f) };

            const uint tick = 90;

            var share = ClassAbilityAttackerFilter.EnemyCreatureShareOfTick(contributions, tick);
            Assert.AreEqual(40u, share, "the rounded sum of the creature contributions only");

            var dispatched = ClassAbilityAttackerFilter.SpellDamageShare(isSpell: true, hasEnemyCreatureAttacker: false, explicitShare: share, incomingDamage: tick);
            Assert.AreEqual(40u, dispatched, "the tick's explicit share wins over its null source");

            var decision = ReflectMagicAbility.DecideImmunity(1000.1, 1000.3, dispatched, tick, 0.45);

            Assert.AreEqual(50u, decision.Damage, "the player's part of the tick still lands");
            Assert.AreEqual(40u, decision.Blocked);
        }

        [TestMethod]
        public void TheDotShare_IsClampedToTheTick_AndIgnoresJunk()
        {
            Assert.AreEqual(10u, ClassAbilityAttackerFilter.EnemyCreatureShareOfTick(new[] { (true, 25.0f) }, 10),
                "the share can never exceed the tick that carries it");

            Assert.AreEqual(5u, ClassAbilityAttackerFilter.EnemyCreatureShareOfTick(new[] { (true, 5.0f), (true, -3.0f), (true, float.NaN) }, 10),
                "negative and non-finite contributions add nothing");

            Assert.AreEqual(0u, ClassAbilityAttackerFilter.EnemyCreatureShareOfTick(new[] { (false, 25.0f) }, 10));
            Assert.AreEqual(0u, ClassAbilityAttackerFilter.EnemyCreatureShareOfTick(null, 10));

            Assert.AreEqual(10u, ClassAbilityAttackerFilter.SpellDamageShare(true, false, 25u, 10),
                "the dispatch clamps an explicit share to the hit as well");
        }

        // ---- one DoT heartbeat is one hit (owner ruling 2026-10-17) -----------------------------------

        /// <summary>
        /// Runs one heartbeat's ticks through DecideDotPassTick the way Player.ApplyReflectImmunityWindow
        /// does inside a pass: snapshot the window at the start, decide each tick against the snapshot, and
        /// record "armed" when a tick moves the live window. Returns the damages left and the final window.
        /// </summary>
        private static (uint[] Landed, uint[] Blocked, double WindowEnd) RunHeartbeat(double now, double windowEndBefore, (uint Share, uint Damage)[] ticks, double seconds)
        {
            var passStart = windowEndBefore;
            var current = windowEndBefore;
            var armed = false;

            var landed = new uint[ticks.Length];
            var blocked = new uint[ticks.Length];

            for (var i = 0; i < ticks.Length; i++)
            {
                var decision = ReflectMagicAbility.DecideDotPassTick(now, passStart, current, armed, ticks[i].Share, ticks[i].Damage, seconds);

                if (decision.WindowEnd != current)
                    armed = true;

                current = decision.WindowEnd;
                landed[i] = decision.Damage;
                blocked[i] = decision.Blocked;
            }

            return (landed, blocked, current);
        }

        [TestMethod]
        public void FireAndNetherCreatureDots_InOneHeartbeat_BothLand_AndArmOnce()
        {
            const double now = 2000.0;

            // the Undef (fire) tick, then the Nether tick, both from a creature, in one heartbeat
            var result = RunHeartbeat(now, 0.0, new[] { (40u, 40u), (25u, 25u) }, 0.45);

            CollectionAssert.AreEqual(new[] { 40u, 25u }, result.Landed, "neither tick may be blocked by a window the other armed");
            CollectionAssert.AreEqual(new[] { 0u, 0u }, result.Blocked);
            Assert.AreEqual(now + 0.45, result.WindowEnd, 1e-9, "the window arms once for the heartbeat");

            // the second tick on its own must not re-arm: with the first tick having armed, the decision
            // leaves the window exactly where the first tick put it
            var second = ReflectMagicAbility.DecideDotPassTick(now + 0.001, 0.0, now + 0.45, true, 25, 25, 0.45);
            Assert.AreEqual(now + 0.45, second.WindowEnd, 0.0, "a later tick in the same pass does not re-arm");
            Assert.AreEqual(25u, second.Damage);

            // control: WITHOUT the pass, the plain decision blocks the nether tick - the defect the pass fixes
            var armedByFire = ReflectMagicAbility.DecideImmunity(now, 0.0, 40, 40, 0.45);
            Assert.AreEqual(25u, ReflectMagicAbility.DecideImmunity(now, armedByFire.WindowEnd, 25, 25, 0.45).Blocked,
                "control: the unscoped rule would have blocked the second tick");
        }

        [TestMethod]
        public void AWindowArmedBeforeTheHeartbeat_BlocksBothTicks()
        {
            const double now = 2000.0;
            const double armedByABolt = now + 0.20;

            var result = RunHeartbeat(now, armedByABolt, new[] { (40u, 40u), (25u, 25u) }, 0.45);

            CollectionAssert.AreEqual(new[] { 0u, 0u }, result.Landed, "a window armed earlier still blocks every tick in the heartbeat");
            CollectionAssert.AreEqual(new[] { 40u, 25u }, result.Blocked);
            Assert.AreEqual(armedByABolt, result.WindowEnd, 0.0, "and is neither re-armed nor extended by them");
        }

        [TestMethod]
        public void AMixedHeartbeat_BlocksOnlyCreatureShares_UnderAnEarlierWindow()
        {
            // a creature-and-player Undef tick, then a player-only Nether tick, under a live earlier window
            var result = RunHeartbeat(2000.0, 2000.3, new[] { (30u, 70u), (0u, 20u) }, 0.45);

            CollectionAssert.AreEqual(new[] { 40u, 20u }, result.Landed);
            CollectionAssert.AreEqual(new[] { 30u, 0u }, result.Blocked);
        }

        // ---- the readout -----------------------------------------------------------------------------

        [TestMethod]
        public void ImmunityReadout_TermsSumToEffective()
        {
            foreach (var multiplier in new[] { 1.0, 1.37, 4.0 })
            {
                for (var rank = 1; rank <= 3; rank++)
                {
                    var readout = ReflectMagicAbility.ImmunitySecondary(rank, multiplier);

                    Assert.AreEqual(readout.Effective, readout.Skill + readout.Affinity + readout.Gear, 1e-9,
                        $"rank {rank} x{multiplier}: Skill + Affinity + Gear must equal Effective");

                    Assert.AreEqual(ReflectMagicAbility.ImmunityWindowSeconds(rank, SecondsBase, SecondsStep, multiplier) * 1000.0, readout.Effective, 1e-6,
                        "the readout must mirror the combat expression, in milliseconds");

                    Assert.AreEqual(ReflectMagicAbility.ImmunityWindowSeconds(rank, SecondsBase, SecondsStep, 1.0) * 1000.0, readout.Skill, 1e-6);
                    Assert.AreEqual(0.0, readout.Gear, 0.0);
                    Assert.IsNull(readout.CapNote, "nothing caps the window");
                    Assert.IsTrue(readout.Unit == "ms", $"unit must be milliseconds, found {readout.Unit}");
                    Assert.IsTrue(readout.Label == "spell immunity", $"label must be spell immunity, found {readout.Label}");
                }
            }
        }

        [TestMethod]
        public void Readout_IsSafeWithANullPlayer_AndCarriesTheWindowAsASecondary()
        {
            var handler = (IAbilityReadout)ClassAbilityRegistry.GetHandler(ClassAbilityId.ReflectMagic);

            var readout = handler.GetReadout(null, 3);

            Assert.IsTrue(readout.Label == "projectile reflect", $"the primary line stays the bounce chance, found {readout.Label}");
            Assert.AreEqual(18.0, readout.Effective, 1e-9, "a null player reads as the neutral 1.0 factor");

            Assert.IsNotNull(readout.Secondary);
            Assert.AreEqual(1, readout.Secondary.Count);

            var window = readout.Secondary.Single();
            Assert.AreEqual(450.0, window.Effective, 1e-6, "rank 3 is 450 ms at the neutral factor");
            Assert.AreEqual(0.0, window.Affinity, 1e-9, "a null player adds nothing to the window");

            Assert.IsNull(handler.GetReadout(null, 0).Secondary, "no window line at rank 0");
        }

        /// <summary>
        /// The ladder as the player reads it: whole milliseconds, so the one-decimal /abilities formatter
        /// shows 150/300/450 rather than rounding seconds to 0.2/0.3/0.5.
        /// </summary>
        [TestMethod]
        public void ImmunityReadout_ShowsTheLadderInWholeMilliseconds()
        {
            var expected = new[] { 150.0, 300.0, 450.0 };

            for (var rank = 1; rank <= 3; rank++)
            {
                var readout = ReflectMagicAbility.ImmunitySecondary(rank, 1.0);

                Assert.AreEqual(expected[rank - 1], readout.Effective, 1e-6, $"rank {rank}");
                Assert.AreEqual(expected[rank - 1], readout.Skill, 1e-6, $"rank {rank}");
                Assert.AreEqual(0.0, readout.Affinity, 1e-6, $"rank {rank}: a neutral factor adds nothing");
            }
        }
    }
}
