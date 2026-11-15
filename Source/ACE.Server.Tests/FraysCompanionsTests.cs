using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Effigy companions (FraysCompanions): one per collected form, a locked style, its own level, a paid respec.</summary>
    [TestClass]
    public class FraysCompanionsTests
    {
        private static string A => FraysFamilies.All[0].Key;
        private static string B => FraysFamilies.All[1].Key;

        private static FraysPlayerRecord WithForms(params string[] keys)
        {
            var rec = new FraysPlayerRecord();

            foreach (var k in keys)
                FraysCodex.Note(rec, FraysCodex.Effigy + k);

            return rec;
        }

        [TestMethod]
        public void Style_NeedsTheForm()
        {
            var rec = WithForms(A);
            FraysCompanions.SetStyle(rec, B, "striker", () => true);
            Assert.IsFalse(rec.Companions.ContainsKey(B));
        }

        [TestMethod]
        public void Style_IsLocked_UntilARebindingEmberIsSpent()
        {
            var rec = WithForms(A);
            FraysCompanions.SetStyle(rec, A, "striker", () => true);
            Assert.AreEqual("striker", rec.Companions[A].Role);

            var spent = 0;
            FraysCompanions.SetStyle(rec, A, "warder", () => false);
            Assert.AreEqual("striker", rec.Companions[A].Role, "no Ember, no change");

            FraysCompanions.SetStyle(rec, A, "striker", () => { spent++; return true; });
            Assert.AreEqual(0, spent, "the same style spends nothing");

            rec.Companions[A].Xp = 900;
            FraysCompanions.SetStyle(rec, A, "mender", () => { spent++; return true; });
            Assert.AreEqual(1, spent);
            Assert.AreEqual("mender", rec.Companions[A].Role);
            Assert.AreEqual(900, rec.Companions[A].Xp, "a respec keeps the level");
        }

        [TestMethod]
        public void EachForm_HasItsOwnLevel_AndKindlingFeedsTheActiveOne()
        {
            var rec = WithForms(A, B);
            FraysCompanions.SetStyle(rec, A, "striker", () => true);
            FraysCompanions.SetStyle(rec, B, "warder", () => true);
            Assert.AreEqual(B, rec.ActiveCompanion, "styling a form makes it active");

            Assert.IsTrue(FraysCompanions.Feed(rec, 400, out _));
            Assert.AreEqual(400, rec.Companions[B].Xp);
            Assert.AreEqual(0, rec.Companions[A].Xp);

            FraysCompanions.SetActive(rec, A);
            Assert.IsTrue(FraysCompanions.Feed(rec, 100, out _));
            Assert.AreEqual(100, rec.Companions[A].Xp);
            Assert.AreEqual(("striker", 1), FraysCompanions.Bonus(rec));
        }

        [TestMethod]
        public void Feed_StopsAtLevelTen_AndNeedsAStyledActiveForm()
        {
            var rec = WithForms(A);
            Assert.IsFalse(FraysCompanions.Feed(rec, 250, out _));

            FraysCompanions.SetStyle(rec, A, "mender", () => true);
            rec.Companions[A].Xp = 10_000;
            Assert.IsFalse(FraysCompanions.Feed(rec, 250, out _));
            Assert.AreEqual(10_000, rec.Companions[A].Xp);
        }

        [TestMethod]
        public void SetActive_RefusesAnUnstyledForm()
        {
            var rec = WithForms(A, B);
            FraysCompanions.SetStyle(rec, A, "striker", () => true);
            FraysCompanions.SetActive(rec, B);
            Assert.AreEqual(A, rec.ActiveCompanion);
        }

        [TestMethod]
        public void OldCompanion_AppliesUntilAFormIsStyled_ThenItsExperienceMoves()
        {
            var rec = WithForms(A, B);
            rec.CompanionRole = "striker";
            rec.CompanionXp = 1250;
            Assert.AreEqual(("striker", 3), FraysCompanions.Bonus(rec));

            FraysCompanions.SetStyle(rec, A, "warder", () => true);
            Assert.AreEqual(1250, rec.Companions[A].Xp);
            Assert.AreEqual(0, rec.CompanionXp);
            Assert.AreEqual("", rec.CompanionRole);
            Assert.AreEqual(("warder", 3), FraysCompanions.Bonus(rec));

            FraysCompanions.SetStyle(rec, B, "mender", () => true);
            Assert.AreEqual(0, rec.Companions[B].Xp, "only the first form gets the old experience");
        }

        [TestMethod]
        public void Companions_RoundTrip()
        {
            var rec = WithForms(A);
            FraysCompanions.SetStyle(rec, A, "striker", () => true);
            rec.Companions[A].Xp = 640;

            var back = FraysPlayerRecord.Parse(rec.ToJson());
            Assert.AreEqual(A, back.ActiveCompanion);
            Assert.AreEqual("striker", back.Companions[A].Role);
            Assert.AreEqual(640, back.Companions[A].Xp);
            Assert.AreEqual(("striker", 2), FraysCompanions.Bonus(back));
        }
    }
}
