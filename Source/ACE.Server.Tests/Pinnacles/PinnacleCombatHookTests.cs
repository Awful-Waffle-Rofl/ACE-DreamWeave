using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The three Harbinger combat hooks, each scoped so an unflagged creature or player is untouched: the per-creature
    /// melee reach (null = retail 0.75), the hazard hit slow's Run debuff (a hand-made Leaden Feet VI layer), and the
    /// fall-damage waiver inside a waiving fight copy. The hot-path call sites need a live world, so their PRESENCE and
    /// ORDER are pinned against the source text. No PropertyManager is read.
    /// </summary>
    [TestClass]
    public class PinnacleCombatHookTests
    {
        private static string Body(string file, string signature)
            => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/" + file), signature);

        // ================================================================ melee reach

        [TestMethod]
        public void MeleeRangeFor_NullOrSmall_IsRetail075()
        {
            Assert.AreEqual(0.75f, Creature.MeleeRangeFor(null));
            Assert.AreEqual(0.75f, Creature.MeleeRangeFor(0.5f));
            Assert.AreEqual(0.75f, Creature.MeleeRangeFor(float.NaN));
            Assert.AreEqual(9.0f, Creature.MeleeRangeFor(9.0f));
        }

        [TestMethod]
        public void MeleeRangeFor_InfinityIsRetail()
            => Assert.AreEqual(0.75f, Creature.MeleeRangeFor(float.PositiveInfinity));

        [TestMethod]
        public void MeleeRange_AnUnflaggedCreatureIsRetail_AFlaggedOneReadsItsReach()
        {
            var plain = TestCreatures.CreateQuestBearer("Plain");
            var reach = TestCreatures.CreateQuestBearer("Reach");
            reach.PinnacleMeleeReach = 9.0f;

            Assert.IsNull(plain.PinnacleMeleeReach, "unset by default");
            Assert.AreEqual(Creature.MaxMeleeRange, plain.MeleeRange);
            Assert.AreEqual(9.0f, reach.MeleeRange);

            reach.PinnacleMeleeReach = null;
            Assert.AreEqual(Creature.MaxMeleeRange, reach.MeleeRange, "clearing it restores retail");
        }

        [TestMethod]
        public void IsMeleeRange_ReadsMeleeRange()
        {
            var b = Body("Monster_Navigation.cs", "public bool IsMeleeRange()");
            StringAssert.Contains(b, "return GetDistanceToTarget() <= MeleeRange;");
        }

        [TestMethod]
        public void GetMaxRange_MeleeBranchReturnsMeleeRange()
        {
            var b = Body("Monster_Combat.cs", "public float GetMaxRange()");
            StringAssert.Contains(b, "return MeleeRange;");
            Assert.IsFalse(b.Contains("return MaxMeleeRange;"));
        }

        // ================================================================ hit slow

        [TestMethod]
        public void GetAnimSpeed_AppliesTheSlowAfterTheClassAbilityCap()
        {
            var b = Body("Creature_Combat.cs", "public float GetAnimSpeed()");
            var cap = b.IndexOf("animSpeed = player.ApplyClassAbilityAttackSpeed(animSpeed, includeConditional: true);", StringComparison.Ordinal);
            var slow = b.IndexOf("animSpeed = (float)(animSpeed * player.GetPinnacleAttackSlowFactor());", StringComparison.Ordinal);
            var els = b.IndexOf("else", cap, StringComparison.Ordinal);
            Assert.IsTrue(cap > 0 && slow > cap && slow < els, $"cap {cap} slow {slow} else {els}");
        }

        private static (Creature Target, EnchantmentTypeFlags Flags) NewSlowTarget() =>
            (TestCreatures.CreateQuestBearer("Slow Target"), PinnacleHitSlowMath.RunSlowStatModType);

        private static List<PropertiesEnchantmentRegistry> RunLowering(Creature c) =>
            c.Biota.PropertiesEnchantmentRegistry.Where(e => e.SpellCategory == SpellCategory.RunLowering).ToList();

        [TestMethod]
        public void RunSlow_LaysAHandMadeLeadenFeetLayerSizedToTheFraction()
        {
            var (target, flags) = NewSlowTarget();

            var entry = Player.ApplyPinnacleRunSlow(target.EnchantmentManager, 500, 0.25, 4.0);

            Assert.IsNotNull(entry);
            Assert.AreEqual((int)SpellId.LeadenFeetOther6, entry.SpellId);
            Assert.AreEqual(SpellCategory.RunLowering, entry.SpellCategory);
            Assert.AreEqual(flags, entry.StatModType);
            Assert.AreEqual((uint)Skill.Run, entry.StatModKey);
            Assert.AreEqual(-PinnacleHitSlowMath.RunPointsFor(500, 0.25), entry.StatModValue, 1e-6);
            Assert.AreEqual(4.0, entry.Duration, 1e-9);
            Assert.AreEqual(0u, entry.CasterObjectId, "no caster: the hazard is not a creature");
        }

        [TestMethod]
        public void RunSlow_ARepeatHitRefreshesFromTheUnslowedSkill_NeverCompounds()
        {
            var (target, _) = NewSlowTarget();

            var first = Player.ApplyPinnacleRunSlow(target.EnchantmentManager, 500, 0.25, 4.0);
            var points = -(int)first.StatModValue;

            // the second hit sees the already-slowed skill (500 - points); the own layer's magnitude is added back
            var second = Player.ApplyPinnacleRunSlow(target.EnchantmentManager, 500 - points, 0.25, 4.0);

            Assert.AreSame(first, second, "refreshed in place");
            Assert.AreEqual(1, RunLowering(target).Count);
            Assert.AreEqual(-points, second.StatModValue, 1e-6, "sized from 500 again, not from the slowed skill");
        }

        [TestMethod]
        public void RunSlow_ARealLeadenFeetCastIsNeverClipped()
        {
            var (target, flags) = NewSlowTarget();
            var caster = TestCreatures.CreateQuestBearer("Leaden Caster");

            // a monster's real Leaden Feet VI: same spell id, same category, a real caster
            var real = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)SpellId.LeadenFeetOther6,
                SpellCategory = SpellCategory.RunLowering,
                LayerId = 1,
                PowerLevel = 300,
                CasterObjectId = caster.Guid.Full,
                StartTime = 0,
                Duration = 60.0,
                StatModType = flags,
                StatModKey = (uint)Skill.Run,
                StatModValue = -40.0f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };
            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(real, target.BiotaDatabaseLock);

            // the real cast is the top layer and already counted in Current; it is not added back
            var ours = Player.ApplyPinnacleRunSlow(target.EnchantmentManager, 460, 0.25, 4.0);

            Assert.AreNotSame(real, ours, "an independent layer");
            Assert.AreEqual(2, RunLowering(target).Count);
            Assert.AreEqual(-40.0f, real.StatModValue, "the real cast's magnitude is untouched");
            Assert.AreEqual(60.0, real.Duration, 1e-9, "the real cast's duration is untouched");
            Assert.AreEqual(caster.Guid.Full, real.CasterObjectId);
            Assert.AreEqual(-PinnacleHitSlowMath.RunPointsFor(460, 0.25), ours.StatModValue, 1e-6, "sized from Current, nothing added back");
            Assert.AreNotEqual(real.LayerId, ours.LayerId);
        }

        [TestMethod]
        public void RunSlow_ZeroFractionLaysNothing()
        {
            var (target, _) = NewSlowTarget();

            Assert.IsNull(Player.ApplyPinnacleRunSlow(target.EnchantmentManager, 500, 0.0, 4.0));
            Assert.IsNull(Player.ApplyPinnacleRunSlow(target.EnchantmentManager, 500, 0.25, 0.0), "no duration, no slow");
            Assert.AreEqual(0, RunLowering(target).Count);
        }

        // ================================================================ fall waiver

        [TestMethod]
        public void TakeDamageFalling_WaiverAfterJumpGuard_BeforeLifestone()
        {
            var b = Body("Player_Move.cs", "public void TakeDamage_Falling(float amount)");
            var j = b.IndexOf("if (ACE.Server.ThreadDungeons.JumpFallGuard.IsExempt(this)) return;", StringComparison.Ordinal);
            var w = b.IndexOf("if (ACE.Server.Pinnacles.PinnacleFightManager.WaivesFallDamage(this)) return;", StringComparison.Ordinal);
            var l = b.IndexOf("if (UnderLifestoneProtection)", StringComparison.Ordinal);
            Assert.IsTrue(j > 0 && w > j && l > w, $"jump {j} waiver {w} lifestone {l}");
        }

        private sealed class StubScript : IPinnacleBossScript
        {
            private readonly PinnacleScriptData data;
            public StubScript(string kind, bool waive) { Kind = kind; data = new PinnacleScriptData { WaiveFallDamage = waive }; }
            public string Kind { get; }
            public PinnacleScriptData Data => data;
            public int PhaseCount => 1;
            public void OnStart(IPinnacleFightPort port) { }
            public void EnterPhase(int phase, IPinnacleFightPort port) { }
            public void OnEvent(PinnacleEvent e, IPinnacleFightPort port) { }
            public void RestartPhase(IPinnacleFightPort port) { }
            public string Describe() => Kind;
        }

        private static PinnacleFight NewFight(string kind, bool waive, uint instance)
            => new PinnacleFight(new StubScript(kind, waive), new FakePinnacleWorld(), new PinnacleFightConfig(), ownerGuid: 0x50000001,
                knotGuid: 0x80000002, instance: instance, knotLevel: 250, seed: 1);

        [TestMethod]
        public void WaivesFall_OnlyAnUnendedWaivingFightOnThatInstance()
        {
            var waiving = NewFight("harbinger", true, 0x7001);
            var matron = NewFight("matron", false, 0x7002);
            var ended = NewFight("harbinger", true, 0x7003);
            ended.Reset("test");
            Assert.IsTrue(ended.IsEnded, "control: Reset ends the fight");

            var fights = new List<PinnacleFight> { waiving, matron, ended, null };

            Assert.IsTrue(PinnacleFightManager.WaivesFall(0x7001, fights), "a waiving fight's copy");
            Assert.IsFalse(PinnacleFightManager.WaivesFall(0x7002, fights), "a fight that does not waive");
            Assert.IsFalse(PinnacleFightManager.WaivesFall(0x7003, fights), "an ended waiving fight");
            Assert.IsFalse(PinnacleFightManager.WaivesFall(0, fights), "instance 0 is never a copy");
            Assert.IsFalse(PinnacleFightManager.WaivesFall(0x7004, fights), "another instance");
            Assert.IsFalse(PinnacleFightManager.WaivesFall(0x7001, null), "no registry");
        }

        [TestMethod]
        public void WaivesFallDamage_NullPlayerIsFalse_NeverThrows()
            => Assert.IsFalse(PinnacleFightManager.WaivesFallDamage(null));
    }
}
