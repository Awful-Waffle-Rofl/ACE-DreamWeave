using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Entity.Facets;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The PK facet (/facet pk). ACE.Server.Tests has no database and cannot construct a live Player, so the
    /// coverage here is the pure deciders every live site delegates to: FacetPk (status rules, respite tick
    /// level, altar and class ability point freeze), the command parse, the labels, the help and list text,
    /// the ability-set override that keeps the class ability point pool in delta form, and the pick-up
    /// speed composition the PK facet feeds zeros into. Nothing here reads PropertyManager.
    ///
    /// Every truth table carries CONTROL rows - the cases where the rule must NOT act - because a decider
    /// that always answered "force it" would pass every positive row.
    /// </summary>
    [TestClass]
    public class FacetPkTests
    {
        // ------------------------------------------------------------------ RulesActive / RequiredStatus

        [TestMethod]
        public void RulesActive_RequiresBothSwitches()
        {
            Assert.IsTrue(FacetPk.RulesActive(true, true));
            Assert.IsFalse(FacetPk.RulesActive(true, false), "facet_pk_enabled off must switch the rule off");
            Assert.IsFalse(FacetPk.RulesActive(false, true), "facet_enabled is the master switch");
            Assert.IsFalse(FacetPk.RulesActive(false, false));
        }

        [TestMethod]
        public void RequiredStatus_TruthTable()
        {
            // rules active
            Assert.AreEqual(FacetPkRequirement.PK, FacetPk.RequiredStatus(true, isOnPkFacet: true, isOlthoi: false));
            Assert.AreEqual(FacetPkRequirement.NPK, FacetPk.RequiredStatus(true, isOnPkFacet: false, isOlthoi: false),
                "every non-PK facet, slot 1 included, is NPK while the rule is active");

            // CONTROL: rules inactive - the rule says nothing, whatever the slot
            Assert.AreEqual(FacetPkRequirement.None, FacetPk.RequiredStatus(false, isOnPkFacet: true, isOlthoi: false));
            Assert.AreEqual(FacetPkRequirement.None, FacetPk.RequiredStatus(false, isOnPkFacet: false, isOlthoi: false));

            // CONTROL: an Olthoi is exempt either way
            Assert.AreEqual(FacetPkRequirement.None, FacetPk.RequiredStatus(true, isOnPkFacet: false, isOlthoi: true));
            Assert.AreEqual(FacetPkRequirement.None, FacetPk.RequiredStatus(true, isOnPkFacet: true, isOlthoi: true));
        }

        // ------------------------------------------------------------------ EffectivePkLevel

        [TestMethod]
        public void EffectivePkLevel_RuleActive_OverridesLastingLevelAndServerFlags()
        {
            // PK facet counts as PK even with a lasting level of NPK - the load-bearing case for PK_DeathTick.
            Assert.AreEqual(PKLevel.PK, FacetPk.EffectivePkLevel(FacetPkRequirement.PK, PKLevel.NPK, false, false));
            Assert.AreEqual(PKLevel.PK, FacetPk.EffectivePkLevel(FacetPkRequirement.PK, PKLevel.NPK, false, true), "the rule wins over pkl_server");

            // Non-PK facet counts as NPK even with a lasting level of PK, and over pk_server / pkl_server.
            Assert.AreEqual(PKLevel.NPK, FacetPk.EffectivePkLevel(FacetPkRequirement.NPK, PKLevel.PK, false, false));
            Assert.AreEqual(PKLevel.NPK, FacetPk.EffectivePkLevel(FacetPkRequirement.NPK, PKLevel.NPK, true, false), "the rule wins over pk_server");
            Assert.AreEqual(PKLevel.NPK, FacetPk.EffectivePkLevel(FacetPkRequirement.NPK, PKLevel.PKLite, false, true), "the rule wins over pkl_server");
        }

        [TestMethod]
        public void EffectivePkLevel_NoRule_IsTheRetailDerivationUnchanged()
        {
            // CONTROL: with the rule off, exactly what PK_DeathTick computed before facets existed.
            Assert.AreEqual(PKLevel.NPK, FacetPk.EffectivePkLevel(FacetPkRequirement.None, PKLevel.NPK, false, false));
            Assert.AreEqual(PKLevel.PK, FacetPk.EffectivePkLevel(FacetPkRequirement.None, PKLevel.PK, false, false));
            Assert.AreEqual(PKLevel.Free, FacetPk.EffectivePkLevel(FacetPkRequirement.None, PKLevel.Free, false, false));
            Assert.AreEqual(PKLevel.PK, FacetPk.EffectivePkLevel(FacetPkRequirement.None, PKLevel.NPK, true, false), "pk_server forces PK");
            Assert.AreEqual(PKLevel.PKLite, FacetPk.EffectivePkLevel(FacetPkRequirement.None, PKLevel.PK, false, true), "pkl_server forces PKLite");
            Assert.AreEqual(PKLevel.PK, FacetPk.EffectivePkLevel(FacetPkRequirement.None, PKLevel.NPK, true, true), "pk_server beats pkl_server");
        }

        // ------------------------------------------------------------------ ShouldReassert

        private static bool Reassert(FacetPkRequirement required, PlayerKillerStatus current, bool inRespite, bool pkTimerActive, out PlayerKillerStatus target)
            => FacetPk.ShouldReassert(required, current, inRespite, pkTimerActive, out target);

        [TestMethod]
        public void ShouldReassert_PkFacet_ForcesPkOutsideRespite()
        {
            Assert.IsTrue(Reassert(FacetPkRequirement.PK, PlayerKillerStatus.NPK, false, false, out var target));
            Assert.AreEqual(PlayerKillerStatus.PK, target);

            Assert.IsTrue(Reassert(FacetPkRequirement.PK, PlayerKillerStatus.PKLite, false, false, out target));
            Assert.AreEqual(PlayerKillerStatus.PK, target);

            // CONTROL: already PK - nothing to do
            Assert.IsFalse(Reassert(FacetPkRequirement.PK, PlayerKillerStatus.PK, false, false, out _));
        }

        [TestMethod]
        public void ShouldReassert_PkFacetInRespite_IsLeftNpk()
        {
            // CONTROL: the post-death respite keeps its NPK window on the PK facet (owner ruling).
            Assert.IsFalse(Reassert(FacetPkRequirement.PK, PlayerKillerStatus.NPK, inRespite: true, pkTimerActive: false, out var target));
            Assert.AreEqual(PlayerKillerStatus.NPK, target);
        }

        [TestMethod]
        public void ShouldReassert_NonPkFacet_ForcesNpk_IncludingASlotOnePkPlayer()
        {
            // A slot-1 player whose status is PK is reasserted to NPK while the rule is active.
            Assert.IsTrue(Reassert(FacetPkRequirement.NPK, PlayerKillerStatus.PK, false, false, out var target));
            Assert.AreEqual(PlayerKillerStatus.NPK, target);

            Assert.IsTrue(Reassert(FacetPkRequirement.NPK, PlayerKillerStatus.PKLite, false, false, out target));
            Assert.AreEqual(PlayerKillerStatus.NPK, target);

            // CONTROL: a slot-1 NPK player is NOT touched
            Assert.IsFalse(Reassert(FacetPkRequirement.NPK, PlayerKillerStatus.NPK, false, false, out _));
        }

        [TestMethod]
        public void ShouldReassert_NonPkFacetWithPkTimerActive_IsNotFlippedMidFight()
        {
            // CONTROL (owner ruling): an active PK timer defers the NPK reassert until it clears.
            Assert.IsFalse(Reassert(FacetPkRequirement.NPK, PlayerKillerStatus.PK, false, pkTimerActive: true, out var target));
            Assert.AreEqual(PlayerKillerStatus.PK, target);

            Assert.IsFalse(Reassert(FacetPkRequirement.NPK, PlayerKillerStatus.PKLite, false, pkTimerActive: true, out _));
        }

        [TestMethod]
        public void ShouldReassert_RulesInactive_NeverActs()
        {
            // CONTROL: with no rule, nothing is ever reasserted, whatever the status or flags.
            foreach (var status in new[] { PlayerKillerStatus.NPK, PlayerKillerStatus.PK, PlayerKillerStatus.PKLite, PlayerKillerStatus.Free })
            {
                foreach (var respite in new[] { false, true })
                {
                    foreach (var timer in new[] { false, true })
                        Assert.IsFalse(Reassert(FacetPkRequirement.None, status, respite, timer, out _), $"None must never act ({status}, respite {respite}, timer {timer})");
                }
            }
        }

        [TestMethod]
        public void ShouldReassert_FreeAndOtherStatuses_AreNeverTouched()
        {
            // CONTROL: Free (and any non-ordinary status an admin set) is left alone in both directions.
            foreach (var status in new[] { PlayerKillerStatus.Free, PlayerKillerStatus.Protected, PlayerKillerStatus.Undef, PlayerKillerStatus.RubberGlue })
            {
                Assert.IsFalse(Reassert(FacetPkRequirement.PK, status, false, false, out _), $"PK rule must not touch {status}");
                Assert.IsFalse(Reassert(FacetPkRequirement.NPK, status, false, false, out _), $"NPK rule must not touch {status}");
            }
        }

        // ------------------------------------------------------------------ StatusOnLeavingPkFacet

        [TestMethod]
        public void StatusOnLeavingPkFacet_RuleActive_IsAlwaysNpk()
        {
            foreach (var lasting in new[] { PKLevel.NPK, PKLevel.PK, PKLevel.PKLite })
            {
                Assert.AreEqual(PlayerKillerStatus.NPK, FacetPk.StatusOnLeavingPkFacet(FacetPkRequirement.NPK, lasting, false, false), $"lasting {lasting}");
                Assert.AreEqual(PlayerKillerStatus.NPK, FacetPk.StatusOnLeavingPkFacet(FacetPkRequirement.NPK, lasting, true, false), $"lasting {lasting}, pk_server");
            }
        }

        [TestMethod]
        public void StatusOnLeavingPkFacet_NoRule_FallsBackToTheRetailDerivation()
        {
            // CONTROL: facet_pk_enabled switched off while the player stood on the PK facet.
            Assert.AreEqual(PlayerKillerStatus.NPK, FacetPk.StatusOnLeavingPkFacet(FacetPkRequirement.None, PKLevel.NPK, false, false));
            Assert.AreEqual(PlayerKillerStatus.PK, FacetPk.StatusOnLeavingPkFacet(FacetPkRequirement.None, PKLevel.PK, false, false));
            Assert.AreEqual(PlayerKillerStatus.PK, FacetPk.StatusOnLeavingPkFacet(FacetPkRequirement.None, PKLevel.NPK, true, false));
            Assert.AreEqual(PlayerKillerStatus.PKLite, FacetPk.StatusOnLeavingPkFacet(FacetPkRequirement.None, PKLevel.NPK, false, true));
        }

        // ------------------------------------------------------------------ altars and the point freeze

        [TestMethod]
        public void AltarRefusal_TruthTable()
        {
            Assert.AreEqual(FacetPk.PkFacetAltarRefusal, FacetPk.AltarRefusal(FacetPkRequirement.PK, altarMakesPk: true));
            Assert.AreEqual(FacetPk.PkFacetAltarRefusal, FacetPk.AltarRefusal(FacetPkRequirement.PK, altarMakesPk: false));
            Assert.AreEqual(FacetPk.NonPkFacetPkRefusal, FacetPk.AltarRefusal(FacetPkRequirement.NPK, altarMakesPk: true));

            // CONTROL: the NPK altar is harmless on a non-PK facet, and with no rule nothing is refused here.
            Assert.IsNull(FacetPk.AltarRefusal(FacetPkRequirement.NPK, altarMakesPk: false));
            Assert.IsNull(FacetPk.AltarRefusal(FacetPkRequirement.None, altarMakesPk: true));
            Assert.IsNull(FacetPk.AltarRefusal(FacetPkRequirement.None, altarMakesPk: false));
        }

        [TestMethod]
        public void CanSpendClassAbilityPoints_FrozenOnPkFacet()
        {
            Assert.IsFalse(FacetPk.CanSpendClassAbilityPoints(FacetPk.SuppressionActive(true, () => true)));
        }

        [TestMethod]
        public void CanSpendClassAbilityPoints_Control_AnyOtherFacetCanStillSpend()
        {
            Assert.IsTrue(FacetPk.CanSpendClassAbilityPoints(FacetPk.SuppressionActive(false, () => true)));
        }

        // ------------------------------------------------------------------ the ability set and the pool

        [TestMethod]
        public void FacetAbilitySetForSlot_PkFacet_ForcesEmptySetsBothDirections_AndCommitsZeroPoints()
        {
            var stored = new Dictionary<string, int> { ["Frenzy"] = 3, ["Thorns"] = 2 };

            // entering: a non-empty stored row is discarded
            var incoming = Player.FacetAbilitySetForSlot(Player.PkFacetSlot, true, stored, out var discardedIncoming);
            Assert.AreEqual(0, incoming.Count);
            Assert.IsTrue(discardedIncoming);

            // leaving: live ranks are discarded from the stored set too (erased by the switch; the refund of
            // their cost is PlanFacetAbilitySwap's job - see FacetPkRankRefundTests)
            var outgoing = Player.FacetAbilitySetForSlot(Player.PkFacetSlot, true, new Dictionary<string, int> { ["Frenzy"] = 1 }, out var discardedOutgoing);
            Assert.AreEqual(0, outgoing.Count);
            Assert.IsTrue(discardedOutgoing);

            // Both sides empty => the delta-form pool is unchanged: the PK facet commits and releases 0.
            var after = FacetPools.AvailableClassAbilityPointsAfterSwap(17, outgoingSpent: 0, incomingSpent: 0, out var shortfall);
            Assert.AreEqual(17, after);
            Assert.AreEqual(0, shortfall);
        }

        [TestMethod]
        public void FacetAbilitySetForSlot_Control_NumberedSlotsPassThroughUntouched()
        {
            var stored = new Dictionary<string, int> { ["Frenzy"] = 3 };

            var result = Player.FacetAbilitySetForSlot(2, true, stored, out var discarded);

            Assert.AreSame(stored, result);
            Assert.IsFalse(discarded);

            Player.FacetAbilitySetForSlot(Player.PkFacetSlot, true, new Dictionary<string, int>(), out discarded);
            Assert.IsFalse(discarded, "an already-empty set is not a discard worth logging");
        }

        // ------------------------------------------------------------------ rule inactive: no suppression

        [TestMethod]
        public void SuppressionActive_RequiresThePkFacetAndTheRule()
        {
            Assert.IsTrue(FacetPk.SuppressionActive(true, () => true));

            // CONTROL: a player LEFT on the PK facet after facet_enabled / facet_pk_enabled was switched off
            // is an ordinary player - nothing is suppressed (and with facet_enabled off they cannot leave).
            Assert.IsFalse(FacetPk.SuppressionActive(true, () => false));
            Assert.IsFalse(FacetPk.SuppressionActive(false, () => true));
        }

        [TestMethod]
        public void SuppressionActive_OffThePkFacet_NeverReadsTheRule()
        {
            // The hot-path cost claim: a player off the PK facet never pays the rule read.
            var reads = 0;

            Assert.IsFalse(FacetPk.SuppressionActive(false, () => { reads++; return true; }));
            Assert.AreEqual(0, reads);
        }

        [TestMethod]
        public void PkFacetWithRuleInactive_GetsNormalValues_Control()
        {
            // The exact inputs each suppression site feeds its helper, for a player standing on the PK facet
            // with the rule OFF: every value passes through unchanged.
            var suppressed = FacetPk.SuppressionActive(true, () => false);

            Assert.AreEqual(3, FacetPk.PickupBonusCount(suppressed, 3), "pick-up boons");
            Assert.AreEqual(2, FacetPk.PickupBonusCount(suppressed, 2), "pick-up augmentations");
            Assert.AreEqual(1.6, FacetPk.TurnSpeed(suppressed, 1.6), 0.0001, "turn speed");
            Assert.AreEqual(0.25, FacetPk.EquipmentModValue(suppressed, 0.25), 0.0001, "equipment mod value");
            Assert.IsTrue(FacetPk.CanSpendClassAbilityPoints(suppressed), "class ability point spending");

            var stored = new Dictionary<string, int> { ["Frenzy"] = 2 };
            Assert.AreSame(stored, Player.FacetAbilitySetForSlot(Player.PkFacetSlot, false, stored, out var discarded),
                "a paid rank bought on the PK facet while the rule was off is priced and stored like any slot's");
            Assert.IsFalse(discarded);
        }

        [TestMethod]
        public void PkFacetWithRuleActive_SuppressesEveryValue()
        {
            var suppressed = FacetPk.SuppressionActive(true, () => true);

            Assert.AreEqual(0, FacetPk.PickupBonusCount(suppressed, 3));
            Assert.AreEqual(1.0, FacetPk.TurnSpeed(suppressed, 1.6), 0.0001);
            Assert.AreEqual(0.0, FacetPk.EquipmentModValue(suppressed, 0.25), 0.0001);
            Assert.IsFalse(FacetPk.CanSpendClassAbilityPoints(suppressed));
        }

        // ------------------------------------------------------------------ entry popup

        [TestMethod]
        public void ComposeEntryPopup_IsTheOwnerTextVerbatim()
        {
            const string expected =
                "You are now on your PK facet.\n" +
                "\n" +
                "While on this facet:\n" +
                "- You are always a player killer, and cannot become non-PK until you switch away. A PK death still grants the usual respite.\n" +
                "- Class abilities do not work, and class ability points cannot be spent.\n" +
                "- Equipment mods and weapon mods do not work.\n" +
                "- Pickup speed bonuses do not apply.\n" +
                "- Turn speed bonuses do not apply.\n" +
                "- Your other facets are always non-PK.\n" +
                "\n" +
                "Use /facet with a facet number to switch away.";

            Assert.AreEqual(expected, FacetPk.ComposeEntryPopup());
        }

        [TestMethod]
        public void ComposeEntryPopup_IsAsciiAndFitsThePopupString_Control()
        {
            var text = FacetPk.ComposeEntryPopup();

            foreach (var c in text)
                Assert.IsTrue(c == '\n' || (c >= 0x20 && c <= 0x7E), $"non-ASCII or control character U+{(int)c:X4} in the PK facet popup");

            Assert.IsFalse(text.Contains("\r"), "the server's popups use bare \\n");

            // WriteString16L writes a ushort character count, then Windows-1252 bytes: the text must fit the
            // count and survive the encoding byte-for-byte.
            Assert.IsTrue(text.Length <= ushort.MaxValue);
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var cp1252 = System.Text.Encoding.GetEncoding(1252);
            Assert.AreEqual(text, cp1252.GetString(cp1252.GetBytes(text)));
        }

        // ------------------------------------------------------------------ respite

        private const double Beat = 5.0;
        private const double Respite = 300.0;

        [TestMethod]
        public void RespiteTick_LeavingThePkFacetMidRespite_KeepsCounting()
        {
            // DISCRIMINATING: died on the PK facet, then /facet 1 after the PK timer - the rule now requires
            // NPK. The respite must stay set and keep counting (f21f44358 cleared it on this beat).
            var next = FacetPk.RespiteTick(100.0, Beat, Respite, FacetPkRequirement.NPK, PKLevel.NPK, false, false, out var ended, out _);

            Assert.IsNotNull(next, "the respite was cleared early by a facet hop");
            Assert.AreEqual(105.0, next.Value, 0.0001, "the respite must keep counting");
            Assert.IsFalse(ended);

            // Also with a lasting PK level and pk_server set - neither may clear it early under the rule.
            Assert.AreEqual(105.0, FacetPk.RespiteTick(100.0, Beat, Respite, FacetPkRequirement.NPK, PKLevel.PK, true, false, out _, out _).Value, 0.0001);
        }

        [TestMethod]
        public void RespiteTick_RuleActive_EndsOnTheFacetRulesLevel()
        {
            // Ends on a non-PK facet: resolves to NPK.
            Assert.IsNull(FacetPk.RespiteTick(298.0, Beat, Respite, FacetPkRequirement.NPK, PKLevel.PK, false, false, out var ended, out var level));
            Assert.IsTrue(ended);
            Assert.AreEqual(PKLevel.NPK, level);

            // Ends on the PK facet: resolves to PK even with a lasting level of NPK.
            Assert.IsNull(FacetPk.RespiteTick(298.0, Beat, Respite, FacetPkRequirement.PK, PKLevel.NPK, false, false, out ended, out level));
            Assert.IsTrue(ended);
            Assert.AreEqual(PKLevel.PK, level);

            // On the PK facet mid-respite it keeps counting.
            Assert.AreEqual(55.0, FacetPk.RespiteTick(50.0, Beat, Respite, FacetPkRequirement.PK, PKLevel.NPK, false, false, out ended, out _).Value, 0.0001);
            Assert.IsFalse(ended);
        }

        [TestMethod]
        public void RespiteTick_RuleInactive_IsTheRetailTickUnchanged()
        {
            // CONTROL: retail early clear for an NPK player (no server flag) - cleared at once, no status change.
            Assert.IsNull(FacetPk.RespiteTick(100.0, Beat, Respite, FacetPkRequirement.None, PKLevel.NPK, false, false, out var ended, out _));
            Assert.IsFalse(ended, "the retail early clear is not an ending: the status is left alone");

            // CONTROL: a lasting PK counts up and ends on PK; pk_server keeps an NPK player counting; pkl_server ends on PKLite.
            Assert.AreEqual(105.0, FacetPk.RespiteTick(100.0, Beat, Respite, FacetPkRequirement.None, PKLevel.PK, false, false, out _, out _).Value, 0.0001);
            Assert.AreEqual(105.0, FacetPk.RespiteTick(100.0, Beat, Respite, FacetPkRequirement.None, PKLevel.NPK, true, false, out _, out _).Value, 0.0001);

            Assert.IsNull(FacetPk.RespiteTick(298.0, Beat, Respite, FacetPkRequirement.None, PKLevel.PK, false, false, out ended, out var level));
            Assert.IsTrue(ended);
            Assert.AreEqual(PKLevel.PK, level);

            Assert.IsNull(FacetPk.RespiteTick(298.0, Beat, Respite, FacetPkRequirement.None, PKLevel.NPK, false, true, out ended, out level));
            Assert.IsTrue(ended);
            Assert.AreEqual(PKLevel.PKLite, level);

            // CONTROL: no respite, nothing happens.
            Assert.IsNull(FacetPk.RespiteTick(null, Beat, Respite, FacetPkRequirement.PK, PKLevel.PK, false, false, out ended, out _));
            Assert.IsFalse(ended);
        }
        // ------------------------------------------------------------------ parse

        [TestMethod]
        public void TryParseSwitchArgs_AcceptsPkCaseInsensitively_WithAndWithoutTrim()
        {
            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "pk" }, out var slot, out var trim));
            Assert.AreEqual((int)Player.PkFacetSlot, slot);
            Assert.IsFalse(trim);

            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "PK" }, out slot, out trim));
            Assert.AreEqual((int)Player.PkFacetSlot, slot);
            Assert.IsFalse(trim);

            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "pk", "trim" }, out slot, out trim));
            Assert.AreEqual((int)Player.PkFacetSlot, slot);
            Assert.IsTrue(trim);

            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "Pk", "TRIM" }, out slot, out trim));
            Assert.AreEqual((int)Player.PkFacetSlot, slot);
            Assert.IsTrue(trim);
        }

        [TestMethod]
        public void TryParseSwitchArgs_Control_NumbersStillParse_AndTheReservedNumberDoesNot()
        {
            Assert.IsTrue(FacetCommands.TryParseSwitchArgs(new[] { "2" }, out var slot, out _));
            Assert.AreEqual(2, slot);

            Assert.IsFalse(FacetCommands.TryParseSwitchArgs(new[] { Player.PkFacetSlot.ToString() }, out _, out _),
                "the reserved slot number must not be a typed alias for the PK facet");
            Assert.IsFalse(FacetCommands.TryParseSwitchArgs(new[] { "pk", "trm" }, out _, out _));
            Assert.IsFalse(FacetCommands.TryParseSwitchArgs(new[] { "pvp" }, out _, out _));
        }

        // ------------------------------------------------------------------ labels never say "100"

        [TestMethod]
        public void Labels_RenderThePkFacetByName_NeverItsNumber()
        {
            var number = Player.PkFacetSlot.ToString();

            Assert.AreEqual("the PK facet", Player.FacetSlotDisplay(Player.PkFacetSlot));
            Assert.AreEqual("The PK facet", Player.FacetSlotDisplayCapitalized(Player.PkFacetSlot));
            Assert.AreEqual("pk", Player.FacetSlotArgument(Player.PkFacetSlot));

            var warning = Player.ComposeFacetOutOfReachWarning(new FacetReachLoss(Player.PkFacetSlot, null, 500));
            StringAssert.Contains(warning, "put the PK facet out of reach");
            StringAssert.Contains(warning, "/facet pk trim");
            Assert.IsFalse(warning.Contains(number), $"out-of-reach warning leaked the slot number: {warning}");

            var named = Player.ComposeFacetOutOfReachWarning(new FacetReachLoss(Player.PkFacetSlot, "Arena", 500));
            StringAssert.Contains(named, "the PK facet (\"Arena\")");
            Assert.IsFalse(named.Contains(number));

            var refusal = Player.ComposeXpShortfallRefusal(Player.PkFacetSlot, 250, null);
            StringAssert.Contains(refusal, "for the PK facet");
            Assert.IsFalse(refusal.Contains(number), $"shortfall refusal leaked the slot number: {refusal}");

            var prompt = FacetCommands.ComposeFirstVisitPrompt(Player.PkFacetSlot);
            StringAssert.Contains(prompt, "PK facet");
            Assert.IsFalse(prompt.Contains(number));
        }

        [TestMethod]
        public void Labels_Control_NumberedSlotsKeepTheirNumbers()
        {
            Assert.AreEqual("facet 2", Player.FacetSlotDisplay(2));
            Assert.AreEqual("Facet 3", Player.FacetSlotDisplayCapitalized(3));
            Assert.AreEqual("4", Player.FacetSlotArgument(4));
            StringAssert.Contains(Player.ComposeFacetOutOfReachWarning(new FacetReachLoss(2, null, 1)), "/facet 2 trim");
            StringAssert.Contains(FacetCommands.ComposeFirstVisitPrompt(2), "Turn to facet 2?");
        }

        // ------------------------------------------------------------------ list line

        private static FacetDials Dials(bool pkEnabled = true, long pkLevel = 150)
            => new FacetDials(true, 300, 400, 500, LandblockRealmList.Parse("01F5@1", "facet_allowlist"), "the Marketplace", pkEnabled, pkLevel);

        [TestMethod]
        public void ComposePkListLine_UsesTheOwnerWording_AndTheDialsLevel()
        {
            var line = FacetCommands.ComposePkListLine(Dials(pkLevel: 175), null, active: false, unlocked: true);

            Assert.AreEqual("  PK facet - always player killer, no class abilities or speed bonuses (unlocks at level 175)", line);
        }

        [TestMethod]
        public void ComposePkListLine_CarriesTheActiveNameAndLockedMarkers()
        {
            StringAssert.StartsWith(FacetCommands.ComposePkListLine(Dials(), "Arena", active: true, unlocked: true), "  PK facet \"Arena\" (active) - ");
            StringAssert.EndsWith(FacetCommands.ComposePkListLine(Dials(), null, active: false, unlocked: false), " - locked");

            // CONTROL: the player standing on it is never shown as locked out of it
            Assert.IsFalse(FacetCommands.ComposePkListLine(Dials(), null, active: true, unlocked: false).EndsWith(" - locked"));
        }

        // ------------------------------------------------------------------ pick-up speed

        [TestMethod]
        public void PickupSpeed_PkFacetZeroesBoonsAndAugs_LeavingTheServerBase()
        {
            // What GetPickupAnimationSpeed passes on the PK facet: boonCount 0 and augCount 0.
            Assert.AreEqual(1.25, PickupSpeed.Compute(1.25, 0, 0.5, 0, 0.1, 3.0), 0.0001, "the server base is not a bonus and stays");

            // CONTROL: the same character off the PK facet does get its bonuses.
            Assert.AreEqual(1.25 * (1 + 2 * 0.5 + 3 * 0.1), PickupSpeed.Compute(1.25, 2, 0.5, 3, 0.1, 3.0), 0.0001);
        }

        // ------------------------------------------------------------------ the status chat line

        [TestMethod]
        public void ComposeFacetPkStatusLine_NamesTheDirection()
        {
            StringAssert.Contains(Player.ComposeFacetPkStatusLine(PlayerKillerStatus.PK), "keeps you a player killer");
            StringAssert.Contains(Player.ComposeFacetPkStatusLine(PlayerKillerStatus.NPK), "non-player killer");
        }
    }
}
