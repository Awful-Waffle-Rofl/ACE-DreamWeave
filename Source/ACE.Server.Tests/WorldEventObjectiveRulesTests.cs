using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for WP-23's <see cref="WorldEventObjectiveRules"/>: the rule that keeps an objective
    /// creature (a Rift or Element Portal pillar - PropertyBool 9026 WorldEventObjective) from thinking once
    /// a player's attack wakes it. A live Creature cannot be built in tests (no live world), so the decision
    /// is split out as a pure static and exercised directly - Monster_Tick just calls it and returns early.
    /// </summary>
    [TestClass]
    public class WorldEventObjectiveRulesTests
    {
        [TestMethod]
        public void SkipsMonsterTick_TrueForAnObjectiveCreature()
        {
            Assert.IsTrue(WorldEventObjectiveRules.SkipsMonsterTick(worldEventObjective: true),
                "a Rift/pillar must never search for a target, move or attack");
        }

        [TestMethod]
        public void SkipsMonsterTick_FalseForAnOrdinaryMonster()
        {
            Assert.IsFalse(WorldEventObjectiveRules.SkipsMonsterTick(worldEventObjective: false),
                "every non-objective creature's Monster_Tick must be unaffected by this rule");
        }
    }
}
