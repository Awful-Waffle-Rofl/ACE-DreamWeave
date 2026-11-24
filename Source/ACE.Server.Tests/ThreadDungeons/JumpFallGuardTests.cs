using System;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Jump encounter's fall-damage exemption (JumpFallGuard): the pure decision, the zone lifecycle (open while the
    /// course runs, the 5 s grace after it despawns, then lapsed), and a source-scan pin on the hook in
    /// Player.TakeDamage_Falling. The zone list is process-wide, so every test clears it in a finally.
    /// </summary>
    [TestClass]
    public class JumpFallGuardTests
    {
        private const uint Instance = 0x80005678u;

        [TestMethod]
        public void The_decision_needs_the_instance_the_radius_and_the_time()
        {
            var now = DateTime.UtcNow.Ticks;

            Assert.IsTrue(JumpFallGuard.Decide(true, 0, 12f, long.MaxValue, now));
            Assert.IsTrue(JumpFallGuard.Decide(true, 17, 12f, long.MaxValue, now), "the course radius plus 5 m");
            Assert.IsFalse(JumpFallGuard.Decide(true, 17.01, 12f, long.MaxValue, now), "past the margin");
            Assert.IsFalse(JumpFallGuard.Decide(false, 0, 12f, long.MaxValue, now), "another instance");
            Assert.IsTrue(JumpFallGuard.Decide(true, 0, 12f, now, now), "at the expiry");
            Assert.IsFalse(JumpFallGuard.Decide(true, 0, 12f, now - 1, now), "after the expiry");
        }

        [TestMethod]
        public void A_zone_covers_the_running_course_and_the_grace_after_it()
        {
            try
            {
                JumpFallGuard.Clear();
                var t0 = DateTime.UtcNow;

                Assert.IsFalse(JumpFallGuard.IsExempt(Instance, 1000, 1000, t0), "no course, no exemption");

                var zone = JumpFallGuard.Open(Instance, 1000, 1000, 12f, t0);

                Assert.IsTrue(JumpFallGuard.IsExempt(Instance, 1000, 1000, t0));
                Assert.IsTrue(JumpFallGuard.IsExempt(Instance, 1016.9, 1000, t0.AddMinutes(10)), "while it runs, however long");
                Assert.IsFalse(JumpFallGuard.IsExempt(Instance, 1017.1, 1000, t0), "outside the radius plus 5 m");
                Assert.IsFalse(JumpFallGuard.IsExempt(Instance + 1, 1000, 1000, t0), "the same spot in another copy");

                var t1 = t0.AddSeconds(60);
                JumpFallGuard.Close(zone, t1);

                Assert.IsTrue(JumpFallGuard.IsExempt(Instance, 1000, 1000, t1.AddSeconds(4.9)), "a player on the top when time runs out lands safely");
                Assert.IsFalse(JumpFallGuard.IsExempt(Instance, 1000, 1000, t1.AddSeconds(5.1)), "the grace lapses");

                // Close is idempotent and never extends an earlier expiry.
                JumpFallGuard.Close(zone, t1.AddSeconds(3));
                Assert.IsFalse(JumpFallGuard.IsExempt(Instance, 1000, 1000, t1.AddSeconds(5.1)));

                // An expired zone is pruned by the next write.
                JumpFallGuard.Open(Instance, 5000, 5000, 12f, t1.AddSeconds(10));
                Assert.AreEqual(1, JumpFallGuard.Zones.Count);
            }
            finally
            {
                JumpFallGuard.Clear();
            }
        }

        [TestMethod]
        public void The_hook_sits_in_TakeDamage_Falling_before_any_damage()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Move.cs");
            var body = PooledLootSourceText.MethodBody(src, "public void TakeDamage_Falling(float amount)");

            var hook = body.IndexOf("if (ACE.Server.ThreadDungeons.JumpFallGuard.IsExempt(this)) return;", StringComparison.Ordinal);
            Assert.IsTrue(hook >= 0, "the exemption is checked in TakeDamage_Falling");
            Assert.IsTrue(hook < body.IndexOf("UnderLifestoneProtection", StringComparison.Ordinal), "before lifestone protection");
            Assert.IsTrue(hook < body.IndexOf("UpdateVitalDelta(Health", StringComparison.Ordinal), "before the health change");
            Assert.IsTrue(hook < body.IndexOf("SendMessage(msg", StringComparison.Ordinal), "before the fall message");

            // The guard is a read-time check, never an enchantment.
            var guard = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/JumpFallGuard.cs");
            Assert.IsFalse(guard.Contains("EnchantmentManager"), "no enchantment layer");
        }
    }
}
