using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>The StubbornMagic no-line-of-sight gate (Monster_Combat.GetMaxRange): equality, like StubbornMissile.</summary>
    [TestClass]
    public class MonsterNoLineOfSightTests
    {
        [TestMethod]
        public void StubbornMagic_KeepsMagic()
        {
            Assert.AreEqual(CombatType.Magic, Creature.NoLineOfSightFallbackAttack(CombatStyle.StubbornMagic));
            Assert.AreEqual(65536, (int)CombatStyle.StubbornMagic);
        }

        [TestMethod]
        public void EveryOtherStyle_FallsBackToMelee()
        {
            foreach (var style in new[] { (CombatStyle)0, CombatStyle.Magic, CombatStyle.StubbornMissile, CombatStyle.StubbornProjectile,
                CombatStyle.StubbornMelee, CombatStyle.StubbornMagic | CombatStyle.Magic, CombatStyle.Melee })
                Assert.AreEqual(CombatType.Melee, Creature.NoLineOfSightFallbackAttack(style), style.ToString());
        }
    }
}
