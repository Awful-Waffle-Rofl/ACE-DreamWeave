using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WorldObject.ShouldCastFellowshipSolo: which item casters may cast a fellowship spell on a lone user.
    /// Only the type matters, so uninitialized instances are enough (no constructor runs).
    /// </summary>
    [TestClass]
    public class FellowshipSoloCastTests
    {
        private static T Bare<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        [TestMethod]
        public void GemFoodVendor_CastSolo()
        {
            Assert.IsTrue(WorldObject.ShouldCastFellowshipSolo(Bare<Gem>()));
            Assert.IsTrue(WorldObject.ShouldCastFellowshipSolo(Bare<Food>()));
            Assert.IsTrue(WorldObject.ShouldCastFellowshipSolo(Bare<Vendor>()));
        }

        [TestMethod]
        public void NullCreatureAndOtherObjects_DoNot()
        {
            Assert.IsFalse(WorldObject.ShouldCastFellowshipSolo(null));
            Assert.IsFalse(WorldObject.ShouldCastFellowshipSolo(Bare<Creature>()), "an emote-casting creature is not an item");
            Assert.IsFalse(WorldObject.ShouldCastFellowshipSolo(Bare<GenericObject>()), "cloaks, weapons and other objects are procs, not item use");
        }
    }
}
