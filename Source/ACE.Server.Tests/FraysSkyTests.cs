using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Server.Dye;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Sky Seals: group Snarls pay Seals, Seals buy cosmetics only.</summary>
    [TestClass]
    public class FraysSkyTests
    {
        [TestMethod]
        public void Seals_OnlyFromGroupCamps()
        {
            Assert.AreEqual(1, FraysSky.SealsFor(true, 1));
            Assert.AreEqual(0, FraysSky.SealsFor(false, 1), "a solo camp pays no Seals");
            Assert.AreEqual(0, FraysSky.SealsFor(true, -3));
        }

        [TestMethod]
        public void Tokens_UnlockForGood()
        {
            var rec = new FraysPlayerRecord();

            Assert.AreEqual(FraysSky.SparksUnlock, FraysSky.UnlockOf(FraysSky.SparksToken));
            Assert.AreEqual(FraysSky.DyeUnlock, FraysSky.UnlockOf(FraysSky.DyeToken));
            Assert.AreEqual(FraysSky.EpithetUnlock, FraysSky.UnlockOf(FraysSky.EpithetToken));
            Assert.IsNull(FraysSky.UnlockOf(FraysSky.Seal), "the Seal itself is a currency, not a token");

            Assert.IsTrue(FraysSky.Unlock(rec, FraysSky.EpithetUnlock));
            Assert.IsFalse(FraysSky.Unlock(rec, FraysSky.EpithetUnlock), "a second use does nothing");
            CollectionAssert.Contains(rec.Epithets, FraysSky.Epithet);
            Assert.AreEqual(1, rec.Epithets.Count(e => e == FraysSky.Epithet));

            var back = FraysPlayerRecord.Parse(rec.ToJson());
            Assert.IsTrue(FraysSky.Has(back, FraysSky.EpithetUnlock), "unlocks survive a save");
        }

        [TestMethod]
        public void SkySparks_NeedTheUnlock()
        {
            var rec = new FraysPlayerRecord();

            Assert.IsTrue(FraysCodex.SparkColours.ContainsKey(FraysSky.SkySpark));
            Assert.IsTrue(FraysSky.SparkAllowed(rec, "red"));
            Assert.IsFalse(FraysSky.SparkAllowed(rec, FraysSky.SkySpark));
            Assert.IsFalse(FraysSky.SparkChoices(rec).Contains(FraysSky.SkySpark));

            FraysSky.Unlock(rec, FraysSky.SparksUnlock);
            Assert.IsTrue(FraysSky.SparkAllowed(rec, FraysSky.SkySpark));
            Assert.IsTrue(FraysSky.SparkChoices(rec).Contains(FraysSky.SkySpark));
        }

        [TestMethod]
        public void SkyDyes_NeedTheCharter()
        {
            var rec = new FraysPlayerRecord();
            var sky = DyeColours.InGroup(FraysSky.DyeGroup).ToList();

            Assert.IsTrue(sky.Count >= 3, $"{sky.Count} Sky colours");
            Assert.IsTrue(FraysSky.DyeAllowed(rec, "Reds"));
            Assert.IsFalse(FraysSky.DyeAllowed(rec, sky[0].Group));

            FraysSky.Unlock(rec, FraysSky.DyeUnlock);
            Assert.IsTrue(FraysSky.DyeAllowed(rec, sky[0].Group));
        }

        [TestMethod]
        public void PlayerText_HasNoUnicodeDashes()
        {
            var text = new[] { FraysSky.UnlockText(FraysSky.SparksUnlock), FraysSky.UnlockText(FraysSky.DyeUnlock), FraysSky.UnlockText(FraysSky.EpithetUnlock), FraysSky.Epithet };
            Assert.IsFalse(text.Any(t => t.Contains('–') || t.Contains('—')));
        }

        // ---- bundles, the Sky Banner and more ways to earn Seals ----

        [TestMethod]
        public void Bundles_HoldKeysKindlingAndShards()
        {
            Assert.AreEqual(FraysLongTerm.DreamKey, FraysSky.BundleOf(FraysSky.KeyBundle)?.Wcid);
            Assert.AreEqual(FraysLongTerm.Kindling, FraysSky.BundleOf(FraysSky.KindlingBundle)?.Wcid);
            Assert.AreEqual(FraysLongTerm.SigilShard, FraysSky.BundleOf(FraysSky.ShardBundle)?.Wcid);
            Assert.IsNull(FraysSky.BundleOf(FraysSky.Banner), "the banner is not a bundle");
            Assert.IsNull(FraysSky.BundleOf(FraysSky.SparksToken));

            Assert.AreEqual(5, FraysSettings.Int(FraysSettings.SkyBundleKeys));
            Assert.AreEqual(2, FraysSettings.Int(FraysSettings.SkyBundleKindling));
            Assert.AreEqual(5, FraysSettings.Int(FraysSettings.SkyBundleShards));
        }

        [TestMethod]
        public void Banner_OnlyOnAGroupCampWhileItFlies()
        {
            var m = new FraysCampMods();
            FraysSky.ApplyBanner(m, group: true, now: 100, until: 200);
            Assert.AreEqual(1.10, m.Embers, 1e-9);
            Assert.AreEqual(1.10, m.KeyBar, 1e-9);

            var ended = new FraysCampMods();
            FraysSky.ApplyBanner(ended, group: true, now: 200, until: 200);
            Assert.AreEqual(1, ended.Embers, 1e-9);
            Assert.AreEqual(1, ended.KeyBar, 1e-9);

            var solo = new FraysCampMods();
            FraysSky.ApplyBanner(solo, group: false, now: 100, until: 200);
            Assert.AreEqual(1, solo.Embers, 1e-9, "a solo camp gets nothing from a banner");
            Assert.AreEqual(1, solo.KeyBar, 1e-9);
        }

        [TestMethod]
        public void HeatSeals_FirstTimeAGroupCampReachesTheHeat()
        {
            Assert.IsTrue(FraysSky.HeatSealsDue(true, 19, 19, 20, 20));
            Assert.IsTrue(FraysSky.HeatSealsDue(true, 0, 18, 21, 20), "a jump past it counts");
            Assert.IsFalse(FraysSky.HeatSealsDue(false, 19, 19, 20, 20), "solo camps get none");
            Assert.IsFalse(FraysSky.HeatSealsDue(true, 20, 19, 20, 20), "only the first time this camp");
            Assert.IsFalse(FraysSky.HeatSealsDue(true, 18, 18, 19, 20));
            Assert.IsFalse(FraysSky.HeatSealsDue(true, 19, 19, 20, 0), "0 = off");

            var rec = new FraysPlayerRecord();
            Assert.IsTrue(FraysSky.HeatSealsFree(rec, 700));
            rec.SkyHeatDay = 700;
            var back = FraysPlayerRecord.Parse(rec.ToJson());
            Assert.IsFalse(FraysSky.HeatSealsFree(back, 700), "once a day, kept through a save");
            Assert.IsTrue(FraysSky.HeatSealsFree(back, 701));
            Assert.IsTrue(FraysSky.HeatSealsFree(FraysPlayerRecord.Parse("{}"), 0), "an old record has not taken them");
        }

        [TestMethod]
        public void WeeklyGroupTask_PaysSealsNotEmbers()
        {
            Assert.AreEqual(5, FraysLongTerm.TaskSeals("w_group"));
            Assert.AreEqual(0, FraysLongTerm.TaskSeals("w_great"));
            var def = FraysLongTerm.BountyPool.Single(b => b.Key == "w_group");
            Assert.IsTrue(def.Weekly);
            Assert.AreEqual(120, def.Target);

            var rec = new FraysPlayerRecord();
            rec.TaskReady.Add(new FraysTaskReady { Giver = FraysHunts.WeekWardenWcid, Text = def.Text, Seals = 5 });
            Assert.AreEqual(5, FraysPlayerRecord.Parse(rec.ToJson()).TaskReady[0].Seals);
        }

        [TestMethod]
        public void NewSealSettings_HaveTheAgreedDefaults()
        {
            Assert.AreEqual(2, FraysSettings.Int(FraysSettings.SkySealsGreatSnarl));
            Assert.AreEqual(20, FraysSettings.Int(FraysSettings.SkySealsHeat));
            Assert.AreEqual(5, FraysSettings.Int(FraysSettings.SkySealsAtHeat));
            Assert.AreEqual(5, FraysSettings.Int(FraysSettings.SkySealsWeeklyTask));
            Assert.AreEqual(60, FraysSettings.Number(FraysSettings.SkyBannerMinutes), 1e-9);
            Assert.IsFalse(FraysSky.BannerText().Contains('–') || FraysSky.BannerText().Contains('—'));
        }
    }
}
