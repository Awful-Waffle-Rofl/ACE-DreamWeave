using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>The level pool counts Kill, Quest, Fellowship and Proficiency XP; the allegiance pass-up, admin and emote XP never count.</summary>
    [TestClass]
    public class AvatarLevelFeedRulesTests
    {
        [TestMethod]
        public void Counts_TruthTable_CoversEveryXpType()
        {
            var expected = new System.Collections.Generic.Dictionary<XpType, bool>
            {
                { XpType.Kill, true },
                { XpType.Quest, true },
                { XpType.Fellowship, true },
                { XpType.Proficiency, true },
                { XpType.Allegiance, false },
                { XpType.Admin, false },
                { XpType.Emote, false },
            };

            // A new XpType must be decided here, not silently dropped or counted.
            CollectionAssert.AreEquivalent(Enum.GetValues<XpType>().ToList(), expected.Keys.ToList(), "XpType gained or lost a member: decide whether it feeds the pool");

            foreach (var (type, counts) in expected)
                Assert.AreEqual(counts, AvatarLevelFeedRules.Counts(type), type.ToString());
        }

        [TestMethod]
        public void Counts_UndefinedValue_DoesNotCount() => Assert.IsFalse(AvatarLevelFeedRules.Counts((XpType)99));
    }
}
