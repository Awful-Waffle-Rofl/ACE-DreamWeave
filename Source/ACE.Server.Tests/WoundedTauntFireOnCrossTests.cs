using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Band-selection tests for the WoundedTaunt fire-on-cross fix (EmoteManager). These cover the
    /// pure logic that decides which HP-milestone bands a health drop skipped - the core of the fix
    /// for the "boss stalls / leaves a global event stuck when killed too fast" class of bug
    /// (Aerbax's Shadow and the other ~33 HP-banded milestone bosses). The emote *execution* reuses
    /// existing engine machinery and is exercised in-game, not here.
    /// </summary>
    [TestClass]
    public class WoundedTauntFireOnCrossTests
    {
        private static PropertiesEmote Band(float? min, float? max, EmoteCategory cat = EmoteCategory.WoundedTaunt, float prob = 1.0f)
            => new PropertiesEmote { Category = cat, MinHealth = min, MaxHealth = max, Probability = prob };

        // ---- ShouldFireInBand (in-band pass) ----

        [TestMethod]
        public void InBand_MilestoneBand_FiresOnTheCrossingHit()
        {
            var band = Band(0.25f, 0.50f);
            Assert.IsTrue(EmoteManager.ShouldFireInBand(band, from: 0.60f, to: 0.45f));
        }

        [TestMethod]
        public void InBand_MilestoneBand_DoesNotRefireWhileHealthSitsInside()
        {
            // the Grulsk bug: health already inside the band, another hit lands, band must stay quiet
            var band = Band(0.25f, 0.50f);
            Assert.IsFalse(EmoteManager.ShouldFireInBand(band, from: 0.45f, to: 0.40f));
            Assert.IsFalse(EmoteManager.ShouldFireInBand(band, from: 0.40f, to: 0.35f));
            Assert.IsFalse(EmoteManager.ShouldFireInBand(band, from: 0.35f, to: 0.30f));
        }

        [TestMethod]
        public void InBand_GrulskContiguousBands_EachFireExactlyOnceAsHealthDescends()
        {
            // 1000083's three contiguous 25-point phase bands, walked down in 5-point chip hits the way a
            // low-damage character actually fights him. Each band must fire on exactly one hit.
            var high = Band(0.50f, 0.75f);
            var mid = Band(0.25f, 0.50f);
            var low = Band(0.00f, 0.25f);
            var bands = new[] { high, mid, low };

            var fireCounts = new Dictionary<PropertiesEmote, int> { [high] = 0, [mid] = 0, [low] = 0 };

            var from = 1.00f;
            for (var to = 0.95f; to > 0.0f; to -= 0.05f)
            {
                foreach (var band in bands)
                {
                    if (EmoteManager.ShouldFireInBand(band, from, to))
                        fireCounts[band]++;
                }
                from = to;
            }

            Assert.AreEqual(1, fireCounts[high], "0.50-0.75 band");
            Assert.AreEqual(1, fireCounts[mid], "0.25-0.50 band");
            Assert.AreEqual(1, fireCounts[low], "0.00-0.25 band");
        }

        [TestMethod]
        public void InBand_FullRangeBand_StillFiresEveryHit()
        {
            // always-on spawner (e.g. Coral Tower) - "whenever wounded", not a milestone
            var fullRange = Band(0.0f, 1.0f);
            Assert.IsTrue(EmoteManager.ShouldFireInBand(fullRange, from: 0.90f, to: 0.80f));
            Assert.IsTrue(EmoteManager.ShouldFireInBand(fullRange, from: 0.80f, to: 0.70f));
            Assert.IsTrue(EmoteManager.ShouldFireInBand(fullRange, from: 0.20f, to: 0.10f));
        }

        [TestMethod]
        public void InBand_AerbaxWideStopBand_FiresOnceOnCrossingNotEveryHit()
        {
            // Aerbax's Shadow's [0.01,0.90] stop band is 89 points wide - under the old stateless check it
            // re-fired on every hit from 90% down to 1%
            var stopBand = Band(0.01f, 0.90f);
            Assert.IsTrue(EmoteManager.ShouldFireInBand(stopBand, from: 0.95f, to: 0.88f));
            Assert.IsFalse(EmoteManager.ShouldFireInBand(stopBand, from: 0.88f, to: 0.70f));
            Assert.IsFalse(EmoteManager.ShouldFireInBand(stopBand, from: 0.70f, to: 0.05f));
        }

        [TestMethod]
        public void InBand_OutsideBand_DoesNotFire()
        {
            var band = Band(0.25f, 0.50f);
            Assert.IsFalse(EmoteManager.ShouldFireInBand(band, from: 0.90f, to: 0.60f), "still above the band");
            Assert.IsFalse(EmoteManager.ShouldFireInBand(band, from: 0.60f, to: 0.10f), "jumped past - GetSkippedWoundedTaunts' job");
        }

        [TestMethod]
        public void InBand_NullBand_NeverFires()
        {
            // lifted float? comparison is always false; PreflightCommand warns about authoring this
            var unbanded = Band(null, null);
            Assert.IsFalse(EmoteManager.ShouldFireInBand(unbanded, from: 1.0f, to: 0.5f));
        }

        [TestMethod]
        public void InBand_AndSkipRecovery_AreDisjoint_NoDoubleFire()
        {
            // every band, for the same hit, must be claimed by at most one of the two paths
            var bands = new[] { Band(0.50f, 0.75f), Band(0.25f, 0.50f), Band(0.00f, 0.25f), Band(0.49f, 0.51f) };

            foreach (var (from, to) in new[] { (1.00f, 0.70f), (0.70f, 0.45f), (0.90f, 0.10f), (0.55f, 0.50f), (0.30f, 0.05f) })
            {
                var skipped = EmoteManager.GetSkippedWoundedTaunts(bands, from, to).ToList();

                foreach (var band in bands)
                {
                    var firedInBand = EmoteManager.ShouldFireInBand(band, from, to);
                    Assert.IsFalse(firedInBand && skipped.Contains(band),
                        $"band [{band.MinHealth},{band.MaxHealth}] double-fired for {from} -> {to}");
                }
            }
        }

        // ---- GetSkippedWoundedTaunts (alive path) ----

        [TestMethod]
        public void Skipped_TightBandJumpedOver_IsRecovered()
        {
            // the core bug: a hit from 55% -> 45% jumps clean over a 49%-51% milestone band
            var band = Band(0.49f, 0.51f);
            var result = EmoteManager.GetSkippedWoundedTaunts(new[] { band }, from: 0.55f, to: 0.45f).ToList();

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(band, result[0]);
        }

        [TestMethod]
        public void Skipped_LandedInsideBand_IsNotRecovered()
        {
            // hit from 55% -> 50% lands inside the 49%-51% band; the normal in-band pass handles it,
            // so it must NOT also be recovered here (no double-fire)
            var band = Band(0.49f, 0.51f);
            var result = EmoteManager.GetSkippedWoundedTaunts(new[] { band }, from: 0.55f, to: 0.50f).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Skipped_BandNotYetReached_IsNotRecovered()
        {
            // hit from 80% -> 60% is still entirely above a 49%-51% band
            var band = Band(0.49f, 0.51f);
            var result = EmoteManager.GetSkippedWoundedTaunts(new[] { band }, from: 0.80f, to: 0.60f).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Skipped_MultipleBands_OrderedHighestFirst()
        {
            // a huge hit from 90% -> 10% jumps three milestone bands; they must replay high -> low so
            // phased start/stop-event chains land in the right order
            var low = Band(0.19f, 0.21f);
            var mid = Band(0.49f, 0.51f);
            var high = Band(0.75f, 0.80f);
            var result = EmoteManager.GetSkippedWoundedTaunts(new[] { low, mid, high }, from: 0.90f, to: 0.10f).ToList();

            CollectionAssert.AreEqual(new[] { high, mid, low }, result);
        }

        [TestMethod]
        public void Skipped_UnbandedAndFullRangeAndOtherCategory_AreIgnored()
        {
            var unbanded = Band(null, null);                                   // ordinary wounded taunt (no band)
            var fullRange = Band(0.0f, 1.0f);                                  // always-on spawner (e.g. Coral Tower)
            var otherCat = Band(0.49f, 0.51f, EmoteCategory.HeartBeat);        // not a wounded taunt
            var result = EmoteManager.GetSkippedWoundedTaunts(new[] { unbanded, fullRange, otherCat }, from: 1.0f, to: 0.0f).ToList();

            Assert.AreEqual(0, result.Count, "only HP-banded WoundedTaunt emotes should ever be recovered");
        }

        [TestMethod]
        public void Skipped_NoHealthDrop_ReturnsEmpty()
        {
            var band = Band(0.49f, 0.51f);
            Assert.AreEqual(0, EmoteManager.GetSkippedWoundedTaunts(new[] { band }, from: 0.50f, to: 0.50f).Count());
            Assert.AreEqual(0, EmoteManager.GetSkippedWoundedTaunts(new[] { band }, from: 0.40f, to: 0.60f).Count()); // healed
        }

        // ---- GetPendingWoundedTaunts (death path) ----

        [TestMethod]
        public void Pending_OnDeath_FiresAllBandsBelowLastSeenHealth_IncludingMinZero()
        {
            // killing blow from 100% skips OnDamage entirely; every band below last-seen health must flush,
            // including a Min==0 band (which the alive-path skip check deliberately excludes)
            var wide = Band(0.0f, 0.75f);      // Min == 0
            var tight = Band(0.19f, 0.21f);
            var result = EmoteManager.GetPendingWoundedTaunts(new[] { wide, tight }, from: 1.0f).ToList();

            CollectionAssert.AreEqual(new[] { wide, tight }, result); // 0.75 before 0.21
        }

        [TestMethod]
        public void Pending_BandAlreadyPassedWhileAlive_IsNotReFired()
        {
            // boss lingered down to 50% (the 91%-98% band already fired in-band); a burst kill from there
            // must not re-fire that higher band
            var high = Band(0.91f, 0.98f);
            var low = Band(0.19f, 0.21f);
            var result = EmoteManager.GetPendingWoundedTaunts(new[] { high, low }, from: 0.50f).ToList();

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(low, result[0]);
        }

        [TestMethod]
        public void Pending_AerbaxShadowOneShot_ReplaysStartThenStopBandsInOrder()
        {
            // Aerbax's Shadow: [0.91,0.98] starts the phase event, [0.01,0.90] stops it + starts the next.
            // A one-shot from full health must replay both, high band first, so the stop/cleanup runs and
            // no server-global event is left stuck.
            var startBand = Band(0.91f, 0.98f);
            var stopBand = Band(0.01f, 0.90f);
            var result = EmoteManager.GetPendingWoundedTaunts(new[] { stopBand, startBand }, from: 1.0f).ToList();

            CollectionAssert.AreEqual(new[] { startBand, stopBand }, result);
        }

        [TestMethod]
        public void Pending_UnbandedTaunt_IsIgnored()
        {
            var unbanded = Band(null, null);
            Assert.AreEqual(0, EmoteManager.GetPendingWoundedTaunts(new[] { unbanded }, from: 1.0f).Count());
        }
    }
}
