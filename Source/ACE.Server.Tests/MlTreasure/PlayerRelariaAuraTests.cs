using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// The pure half of resolving ml_relaria_aura_script (Player.ResolveRelariaAuraScript,
    /// Player_RelariaAura.cs) - testable without a live Player or PropertyManager, exactly the same
    /// shape as WorldEventRewardDelivery.ResolveCacheEffectScript.
    /// </summary>
    [TestClass]
    public class PlayerRelariaAuraTests
    {
        /// <summary>A defined PlayScript member round-trips and reports valid.</summary>
        [TestMethod]
        public void ResolveRelariaAuraScript_DefinedMember_Valid()
        {
            var script = Player.ResolveRelariaAuraScript((long)PlayScript.LayingofHands, out var valid);

            Assert.IsTrue(valid);
            Assert.AreEqual(PlayScript.LayingofHands, script);
        }

        /// <summary>0 is not a defined PlayScript.Invalid substitute here - unlike the WorldEvent cache
        /// effect, the Relaria aura has no "disabled" value of its own (RelariaAuraEnabled and
        /// HasRelariaAura already gate whether the pulse fires at all), so 0 is refused and falls back
        /// to the shipped default like any other undefined value.</summary>
        [TestMethod]
        public void ResolveRelariaAuraScript_Zero_FallsBackToDefault()
        {
            var script = Player.ResolveRelariaAuraScript(0, out var valid);

            Assert.IsFalse(valid);
            Assert.AreEqual(PlayScript.LayingofHands, script);
        }

        /// <summary>A value with no matching PlayScript member is refused rather than cast blindly.</summary>
        [TestMethod]
        public void ResolveRelariaAuraScript_UndefinedMember_FallsBackToDefault()
        {
            var script = Player.ResolveRelariaAuraScript(0xFFFFFF, out var valid);

            Assert.IsFalse(valid);
            Assert.AreEqual(PlayScript.LayingofHands, script);
        }

        /// <summary>A negative or too-large configured value never gets cast to (uint), which would
        /// otherwise wrap into some other defined member.</summary>
        [TestMethod]
        public void ResolveRelariaAuraScript_OutOfRange_FallsBackToDefault()
        {
            var negative = Player.ResolveRelariaAuraScript(-1, out var negativeValid);
            Assert.IsFalse(negativeValid);
            Assert.AreEqual(PlayScript.LayingofHands, negative);

            var tooLarge = Player.ResolveRelariaAuraScript((long)uint.MaxValue + 1, out var tooLargeValid);
            Assert.IsFalse(tooLargeValid);
            Assert.AreEqual(PlayScript.LayingofHands, tooLarge);
        }

        /// <summary>
        /// ml_relaria_pulse_effects_enabled gates both the repeat-kill aura pulse (this file) and the
        /// Tally of the Unburied glow pulse (Player_RelariaTallyGlow.cs). Owner ruling 2026-09-24: off by
        /// default - the pulses were judged too visually intrusive. Pinned against
        /// DefaultPropertyManager rather than PropertyManager.GetBool, since a PropertyManager read
        /// throws outside a live server (see CLAUDE.md's "PropertyManager reads THROW in unit tests").
        /// </summary>
        [TestMethod]
        public void RelariaPulseEffectsEnabled_DefaultsOff()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("ml_relaria_pulse_effects_enabled"),
                "ml_relaria_pulse_effects_enabled is not registered");

            Assert.IsFalse(DefaultPropertyManager.DefaultBooleanProperties["ml_relaria_pulse_effects_enabled"].Item,
                "ml_relaria_pulse_effects_enabled must default OFF per owner ruling 2026-09-24");
        }
    }
}
