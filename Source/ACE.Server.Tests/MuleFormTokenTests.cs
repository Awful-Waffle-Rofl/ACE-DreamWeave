using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The mule form token's decisions, as pure statics.
    ///
    /// WHAT IS NOT COVERED, and cannot be from a unit test: Player.ApplyMuleFormTokenCreatureDeath and
    /// the two Gem intercepts. Every client-visible write on the token goes through
    /// Player.UpdateProperty, whose SendNetwork dereferences Session with no null check, and
    /// constructing a Player at all pulls in the world database. That is the same split
    /// KillFillVessel/Player_KillFillVessel already uses, and it is why the Player-side glue is kept
    /// as thin as it can be and is verified live from Docs/VERIFY-QUEUE.md instead.
    ///
    /// No wcid and no property id is a literal in MuleFormToken: recognition is PropertyBool
    /// MuleFormToken and attunement is PropertyInt MuleFormWcid, so a second token weenie with
    /// different art and a different kill count is pure content.
    /// </summary>
    [TestClass]
    public class MuleFormTokenTests
    {
        private static uint nextGuid = 0x7D300000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 994000;

        private const uint DonorWcid = 1234;
        private const uint OtherWcid = 5678;

        private const uint ValidSetup = 0x0200004E;
        private const uint UnmeasurableSetup = 0x02009999;

        private static float Height(uint setupId) => setupId == ValidSetup ? 4.0f : 0f;

        private const float MaxEffectScale = 8.0f;

        /// <summary>Bakes nothing, by far the common case - matches DatSetupEffectScale's 0f for DefaultScript == 0.</summary>
        private static float NoEffectScale(uint setupId) => 0f;

        /// <summary>
        /// A token built from an in-memory weenie. WeenieType.Gem's SetEphemeralValues touches neither
        /// the database nor the dat files, which is what lets these tests run without either.
        /// </summary>
        private static WorldObject MakeToken(uint? formWcid = null, int structure = 0, int maxStructure = 100, bool marked = true, string name = "Beast Effigy")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Gem },
                    { PropertyInt.Structure, structure },
                    { PropertyInt.MaxStructure, maxStructure },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
            };

            if (marked)
                weenie.PropertiesBool[PropertyBool.MuleFormToken] = true;

            if (formWcid != null)
                weenie.PropertiesInt[PropertyInt.MuleFormWcid] = (int)formWcid.Value;

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject MakeOrdinaryGem()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Gem } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Lockpick" } },
            };

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        private static Container MakeContainer(string name = "Pack")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Container },
                    { PropertyInt.ItemsCapacity, 24 },
                    { PropertyInt.ContainersCapacity, 7 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        private static void Put(Container container, WorldObject item) => container.Inventory[item.Guid] = item;

        // ---------------- recognition ----------------

        [TestMethod]
        public void IsToken_NeedsTheMarkerBool()
        {
            Assert.IsFalse(MuleFormToken.IsToken(null));
            Assert.IsFalse(MuleFormToken.IsToken(MakeOrdinaryGem()));
            Assert.IsFalse(MuleFormToken.IsToken(MakeToken(marked: false)));

            Assert.IsTrue(MuleFormToken.IsToken(MakeToken()));
        }

        [TestMethod]
        public void IsAttuned_ReadsTheFormWcid()
        {
            Assert.IsFalse(MuleFormToken.IsAttuned(MakeToken()));
            Assert.IsNull(MuleFormToken.GetFormWcid(MakeToken()));

            var attuned = MakeToken(DonorWcid);

            Assert.IsTrue(MuleFormToken.IsAttuned(attuned));
            Assert.AreEqual(DonorWcid, MuleFormToken.GetFormWcid(attuned));
        }

        [TestMethod]
        public void IsComplete_ComparesStructureToMaxStructure()
        {
            Assert.IsFalse(MuleFormToken.IsComplete(MakeToken(DonorWcid, structure: 99)));
            Assert.IsTrue(MuleFormToken.IsComplete(MakeToken(DonorWcid, structure: 100)));

            // A token with no capacity at all is not "complete"; it is malformed content, and treating
            // it as complete would let it write a look it never earned.
            Assert.IsFalse(MuleFormToken.IsComplete(MakeToken(DonorWcid, structure: 0, maxStructure: 0)));
        }

        // ---------------- target validation ----------------

        private static bool Valid(bool isCreature = true, bool isPlayer = false, bool isPet = false,
                                  WeenieType weenieType = WeenieType.Creature, bool attackable = true,
                                  uint setupId = ValidSetup, Func<uint, float> effectScale = null,
                                  float maxEffectScale = MaxEffectScale)
        {
            return MuleFormToken.IsValidAttunementTarget(isCreature, isPlayer, isPet, weenieType, attackable, setupId,
                Height, effectScale ?? NoEffectScale, maxEffectScale, out _);
        }

        [TestMethod]
        public void ValidTarget_AcceptsAnOrdinaryMonster()
        {
            Assert.IsTrue(Valid());
        }

        [TestMethod]
        public void ValidTarget_RefusesANonCreature()
        {
            Assert.IsFalse(Valid(isCreature: false));
        }

        [TestMethod]
        public void ValidTarget_RefusesAPlayer()
        {
            Assert.IsFalse(Valid(isPlayer: true));
        }

        [TestMethod]
        public void ValidTarget_RefusesAPet()
        {
            // CombatPet derives from Pet, so this covers both.
            Assert.IsFalse(Valid(isPet: true));
        }

        [TestMethod]
        public void ValidTarget_RefusesSomethingUnattackable()
        {
            Assert.IsFalse(Valid(attackable: false));
        }

        [TestMethod]
        public void ValidTarget_RefusesAVendor()
        {
            Assert.IsFalse(Valid(weenieType: WeenieType.Vendor));
        }

        [TestMethod]
        public void ValidTarget_RefusesABodyWithNoMeasurableHeight()
        {
            // IsValidSetupId answers true with no dat loaded, so the injected height is the only thing
            // that can discriminate here - and a zero height is exactly what would make the scale
            // formula undefined.
            Assert.IsFalse(Valid(setupId: UnmeasurableSetup));
            Assert.IsFalse(Valid(setupId: 0));
        }

        [TestMethod]
        public void ValidTarget_RefusalIsTheOneSpecString()
        {
            MuleFormToken.IsValidAttunementTarget(true, true, false, WeenieType.Creature, true, ValidSetup,
                Height, NoEffectScale, MaxEffectScale, out var refusal);

            Assert.AreEqual("The effigy cannot take that shape.", refusal);
        }

        // ---------------- effect scale gate ----------------

        [TestMethod]
        public void ValidTarget_AcceptsAnEffectScaleUnderTheThreshold()
        {
            Assert.IsTrue(Valid(effectScale: setupId => MaxEffectScale - 1f));
        }

        [TestMethod]
        public void ValidTarget_AcceptsAnEffectScaleExactlyAtTheThreshold()
        {
            // Pins the strictly-greater comparison: a Setup measuring exactly the threshold passes.
            Assert.IsTrue(Valid(effectScale: setupId => MaxEffectScale));
        }

        [TestMethod]
        public void ValidTarget_RefusesAnEffectScaleAboveTheThreshold()
        {
            Assert.IsFalse(Valid(effectScale: setupId => MaxEffectScale + 0.01f));
        }

        [TestMethod]
        public void ValidTarget_RefusalStringForAnOversizedEffectScale()
        {
            MuleFormToken.IsValidAttunementTarget(true, false, false, WeenieType.Creature, true, ValidSetup,
                Height, setupId => MaxEffectScale + 1f, MaxEffectScale, out var refusal);

            StringAssert.Contains(refusal, MuleFormToken.CannotTakeThatShapeMessage);
        }

        [TestMethod]
        public void ValidTarget_RefusesAnUnreadableScript()
        {
            // float.PositiveInfinity is DatSetupEffectScale's answer for a Setup/script/emitter it
            // cannot read, matching this file's existing stance that unmeasurable means refused.
            Assert.IsFalse(Valid(effectScale: setupId => float.PositiveInfinity));
        }

        [TestMethod]
        public void ValidTarget_AcceptsABodyThatBakesNoScript()
        {
            // 0f is DatSetupEffectScale's answer for DefaultScript == 0 - the overwhelmingly common
            // case - and it must pass regardless of how low the threshold is set.
            Assert.IsTrue(Valid(effectScale: setupId => 0f, maxEffectScale: 0f));
        }

        [TestMethod]
        public void ValidTarget_UnmeasurableHeightStillRefuses_EvenWithAFineEffectScale()
        {
            // The height check runs BEFORE the effect-scale check, so a body with no measurable height
            // is refused for that reason even when its effect scale is well under the threshold.
            Assert.IsFalse(Valid(setupId: UnmeasurableSetup, effectScale: setupId => 0f));
        }

        [TestMethod]
        public void DatSetupEffectScale_DoesNotCacheATransientFailure()
        {
            // A float.PositiveInfinity answer means the dat READ THREW, not that the body is genuinely
            // unmeasurable - a static, no-eviction cache must never pin that failure in for the process
            // lifetime, or one disk hiccup bars a perfectly good donor until a restart. GetOrComputeEffectScale
            // is the internal seam that lets this be proven without real dat data: the same setup id is
            // asked twice through a compute delegate that fails once then succeeds, and both calls must
            // actually reach the delegate for the second answer to differ from the first.
            const uint setupId = 0x02001234;
            MuleFormToken.ClearEffectScaleCacheForTest(setupId);

            var callCount = 0;
            float Compute(uint id)
            {
                callCount++;
                return callCount == 1 ? float.PositiveInfinity : 5f;
            }

            var first = MuleFormToken.GetOrComputeEffectScale(setupId, Compute);
            var second = MuleFormToken.GetOrComputeEffectScale(setupId, Compute);

            Assert.IsTrue(float.IsPositiveInfinity(first));
            Assert.AreEqual(5f, second);
            Assert.AreEqual(2, callCount, "the second call must have reached the compute delegate rather than answering from a cached failure");
        }

        [TestMethod]
        public void DatSetupEffectScale_DoesCacheAFiniteResult()
        {
            // The flip side of the case above: a genuine, successfully-read answer IS memoized, so a
            // second call does not re-invoke the (expensive) compute delegate at all.
            const uint setupId = 0x02005678;
            MuleFormToken.ClearEffectScaleCacheForTest(setupId);

            var callCount = 0;
            float Compute(uint id)
            {
                callCount++;
                return 12.5f;
            }

            var first = MuleFormToken.GetOrComputeEffectScale(setupId, Compute);
            var second = MuleFormToken.GetOrComputeEffectScale(setupId, Compute);

            Assert.AreEqual(12.5f, first);
            Assert.AreEqual(12.5f, second);
            Assert.AreEqual(1, callCount, "a finite result must be served from the cache on the second call");
        }

        // ---------------- the fill search ----------------

        [TestMethod]
        public void FindFillableToken_FindsATokenInTheMainPack()
        {
            var pack = MakeContainer();
            var token = MakeToken(DonorWcid);
            Put(pack, token);

            Assert.AreSame(token, MuleFormToken.FindFillableToken(pack, DonorWcid));
        }

        [TestMethod]
        public void FindFillableToken_FindsATokenInASidePack()
        {
            var pack = MakeContainer();
            var side = MakeContainer("Side Pouch");
            var token = MakeToken(DonorWcid);

            Put(side, token);
            Put(pack, side);

            Assert.AreSame(token, MuleFormToken.FindFillableToken(pack, DonorWcid));
        }

        [TestMethod]
        public void FindFillableToken_IgnoresANonMatchingWcid()
        {
            var pack = MakeContainer();
            Put(pack, MakeToken(OtherWcid));
            Put(pack, MakeOrdinaryGem());
            Put(pack, MakeToken());   // unattuned

            Assert.IsNull(MuleFormToken.FindFillableToken(pack, DonorWcid));
        }

        [TestMethod]
        public void FindFillableToken_SkipsAFullToken_InFavourOfALaterUnfilledOne()
        {
            var pack = MakeContainer();
            var full = MakeToken(DonorWcid, structure: 100);
            var open = MakeToken(DonorWcid, structure: 3);

            Put(pack, full);
            Put(pack, open);

            Assert.AreSame(open, MuleFormToken.FindFillableToken(pack, DonorWcid));
        }

        [TestMethod]
        public void FindFillableToken_WithEveryTokenFull_FindsNothing()
        {
            var pack = MakeContainer();
            Put(pack, MakeToken(DonorWcid, structure: 100));

            Assert.IsNull(MuleFormToken.FindFillableToken(pack, DonorWcid));
        }

        [TestMethod]
        public void FindFillableToken_PrefersTheMainPackOverASidePack()
        {
            // Two passes, direct items first, matching KillFillVessel.FindFillable - so "first found"
            // means the same thing in both features.
            var pack = MakeContainer();
            var side = MakeContainer("Side Pouch");
            var inSide = MakeToken(DonorWcid);
            var inPack = MakeToken(DonorWcid);

            Put(side, inSide);
            Put(pack, side);
            Put(pack, inPack);

            Assert.AreSame(inPack, MuleFormToken.FindFillableToken(pack, DonorWcid));
        }

        [TestMethod]
        public void FindFillableToken_HandlesANullContainer()
        {
            Assert.IsNull(MuleFormToken.FindFillableToken(null, DonorWcid));
        }

        // ---------------- counting ----------------

        [TestMethod]
        public void NextStructure_AddsOneAndClamps()
        {
            Assert.AreEqual(1, MuleFormToken.NextStructure(0, 100));
            Assert.AreEqual(100, MuleFormToken.NextStructure(99, 100));
            Assert.AreEqual(100, MuleFormToken.NextStructure(100, 100));
            Assert.AreEqual(100, MuleFormToken.NextStructure(120, 100));
        }

        // ---------------- text ----------------

        [TestMethod]
        public void ComposeName_MatchesTheSpecLiterals()
        {
            Assert.AreEqual("Beast Effigy (Tusker Guard)", MuleFormToken.ComposeName("Beast Effigy", "Tusker Guard", complete: false));
            Assert.AreEqual("Beast Effigy of the Tusker Guard", MuleFormToken.ComposeName("Beast Effigy", "Tusker Guard", complete: true));
        }

        [TestMethod]
        public void ComposeLongDesc_MatchesTheSpecLiterals()
        {
            Assert.AreEqual(
                "Attuned to Tusker Guard. 0 of 100 slain. Strike the killing blow on 100 of its kind to fill it, then use the filled effigy in the Marketplace to change the look of your /mule. Your /mule can be reshaped again as you collect more.",
                MuleFormToken.ComposeLongDesc("Tusker Guard", 0, 100));
            Assert.AreEqual(
                "Attuned to Tusker Guard. 37 of 100 slain. Strike the killing blow on 100 of its kind to fill it, then use the filled effigy in the Marketplace to change the look of your /mule. Your /mule can be reshaped again as you collect more.",
                MuleFormToken.ComposeLongDesc("Tusker Guard", 37, 100));
            Assert.AreEqual("Attuned to Tusker Guard. Complete. Use in the Marketplace to change the look of your /mule.",
                MuleFormToken.ComposeLongDesc("Tusker Guard", 100, 100));
        }

        [TestMethod]
        public void ComposeStatus_MatchesTheSpecLiterals()
        {
            Assert.AreEqual("Attuned to Tusker Guard. 0 of 100 slain.", MuleFormToken.ComposeStatus("Tusker Guard", 0, 100));
            Assert.AreEqual("Attuned to Tusker Guard. 37 of 100 slain.", MuleFormToken.ComposeStatus("Tusker Guard", 37, 100));
            Assert.AreEqual("Attuned to Tusker Guard. Complete.", MuleFormToken.ComposeStatus("Tusker Guard", 100, 100));
        }

        [TestMethod]
        public void ComposeFullMessage_NamesTheTokensBaseName()
        {
            Assert.AreEqual("Your Beast Effigy has taken all it will hold.", MuleFormToken.ComposeFullMessage("Beast Effigy"));
        }

        [TestMethod]
        public void BaseName_ComesFromTheWeenie_NotTheRenamedInstance()
        {
            // The instance name is rewritten on attune and again on completion, so composing the
            // completed name from the instance would produce "Beast Effigy (Tusker Guard) of the
            // Tusker Guard". The weenie template is never mutated and is the stable source.
            var token = MakeToken(DonorWcid);
            token.Name = "Beast Effigy (Tusker Guard)";

            Assert.AreEqual("Beast Effigy", MuleFormToken.GetBaseName(token));
        }

        // ---------------- tunable registration ----------------

        [TestMethod]
        public void MuleFormMaxEffectScale_IsRegisteredWithTheDefaultOf8()
        {
            // Mirrors MuleSummonTests's precedent for mule_form_enabled: the shipped default is
            // asserted directly against DefaultDoubleProperties rather than through PropertyManager,
            // which reads throw with no database in a unit test host.
            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("mule_form_max_effect_scale"),
                "mule_form_max_effect_scale is missing from DefaultDoubleProperties");
            Assert.AreEqual(8.0, ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties["mule_form_max_effect_scale"].Item);
        }
    }
}
