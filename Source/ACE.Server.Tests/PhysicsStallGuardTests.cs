using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using ACE.Server.Physics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards against a world freeze where a Force Bolt's physics update took 28 s. The physics update budget.
    /// </summary>
    [TestClass]
    public class PhysicsStallGuardTests
    {
        [TestMethod]
        public void StepBudget_SpentOnlyPastTheBudget()
        {
            var start = Stopwatch.GetTimestamp();
            var budgetTicks = (long)(PhysicsUpdateBudget.StepBudgetSeconds * Stopwatch.Frequency);

            Assert.IsFalse(PhysicsUpdateBudget.StepBudgetSpent(start, start));
            Assert.IsFalse(PhysicsUpdateBudget.StepBudgetSpent(start, start + budgetTicks));
            Assert.IsTrue(PhysicsUpdateBudget.StepBudgetSpent(start, start + budgetTicks + 1));
            Assert.IsTrue(PhysicsUpdateBudget.StepBudgetSpent(start, start + 28 * Stopwatch.Frequency));
        }

        [TestMethod]
        public void StepBudget_IsUnderOneFullCatchUp()
        {
            // the budget has to bite before a full catch-up (HugeQuantum / MaxQuantum steps) could add up to seconds
            var steps = (int)(PhysicsGlobals.HugeQuantum / PhysicsGlobals.MaxQuantum);
            Assert.IsTrue(steps >= 2);
            Assert.IsTrue(PhysicsUpdateBudget.StepBudgetSeconds < PhysicsUpdateBudget.ProjectileDestroySeconds);
        }

        [TestMethod]
        public void SlowProjectile_OneLongUpdateEndsIt()
        {
            // the 28 s bolt: its first slow update
            Assert.IsTrue(PhysicsUpdateBudget.ShouldEndSlowProjectile(28.1769, 1));
            Assert.IsTrue(PhysicsUpdateBudget.ShouldEndSlowProjectile(PhysicsUpdateBudget.ProjectileDestroySeconds, 1));

            // an ordinary slow impact tick (10-100 ms on the live server) is left alone until the stock 5 repeats
            Assert.IsFalse(PhysicsUpdateBudget.ShouldEndSlowProjectile(0.054, 1));
            Assert.IsFalse(PhysicsUpdateBudget.ShouldEndSlowProjectile(0.5, PhysicsUpdateBudget.ProjectileSlowHitsToDestroy - 1));
            Assert.IsTrue(PhysicsUpdateBudget.ShouldEndSlowProjectile(0.011, PhysicsUpdateBudget.ProjectileSlowHitsToDestroy));
        }
    }
}
