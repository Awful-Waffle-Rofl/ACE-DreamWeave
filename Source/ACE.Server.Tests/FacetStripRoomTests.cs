using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins <see cref="Player.ComposeStripRoomRefusal"/>, the pure free-slot decision behind the facet
    /// gear strip (<see cref="Player.TryStripForFacetSwitch"/>).
    ///
    /// WHAT THIS COVERS: that the two capacity pools are checked INDEPENDENTLY rather than as one combined
    /// total, the exactly-enough boundary in both pools, and the shortfall arithmetic and pluralisation in
    /// the player-facing refusals. The bug being guarded against is a set that fits "in total" but not in
    /// the pool each item is actually charged to, which is what a single-number check waves through.
    ///
    /// WHAT THIS DOES NOT COVER, stated plainly rather than implied: the call site's choice of
    /// GetFreeInventorySlots(includeSidePacks: false) over the default. That is the other half of the same
    /// fix and it lives outside these parameters, on a live Player this assembly cannot construct (there is
    /// no database here). Nothing in this file, and nothing testable in this repo, would go red if that
    /// argument were dropped back to the default - only the live sequence in the strip does. Nor does
    /// anything here reach the equip path itself.
    /// </summary>
    [TestClass]
    public class FacetStripRoomTests
    {
        [TestMethod]
        public void ComposeStripRoomRefusal_RoomInBothPools_ReturnsNull()
        {
            Assert.IsNull(Player.ComposeStripRoomRefusal(
                mainPackItemCount: 5, packSlotItemCount: 1, freeMainPackSlots: 9, freeContainerSlots: 2));
        }

        [TestMethod]
        public void ComposeStripRoomRefusal_NothingEquipped_ReturnsNull()
        {
            Assert.IsNull(Player.ComposeStripRoomRefusal(
                mainPackItemCount: 0, packSlotItemCount: 0, freeMainPackSlots: 0, freeContainerSlots: 0));
        }

        [TestMethod]
        public void ComposeStripRoomRefusal_ExactlyEnoughInBothPools_ReturnsNull()
        {
            Assert.IsNull(Player.ComposeStripRoomRefusal(
                mainPackItemCount: 12, packSlotItemCount: 2, freeMainPackSlots: 12, freeContainerSlots: 2));
        }

        [TestMethod]
        public void ComposeStripRoomRefusal_OneShortInMainPack_Refuses()
        {
            var refusal = Player.ComposeStripRoomRefusal(
                mainPackItemCount: 12, packSlotItemCount: 0, freeMainPackSlots: 11, freeContainerSlots: 0);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "1 more free slot ");
        }

        [TestMethod]
        public void ComposeStripRoomRefusal_SeveralShortInMainPack_ReportsTheShortfallAndPluralises()
        {
            var refusal = Player.ComposeStripRoomRefusal(
                mainPackItemCount: 12, packSlotItemCount: 0, freeMainPackSlots: 8, freeContainerSlots: 0);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "4 more free slots");
        }

        /// <summary>
        /// The core of the fix. Container slots are a separate pool and cannot absorb main-pack items, so a
        /// set that fits only when the two totals are added together must still be refused. A combined
        /// free-slot check passes this case and then fails partway through the strip.
        /// </summary>
        [TestMethod]
        public void ComposeStripRoomRefusal_ContainerSlotsDoNotCoverAMainPackShortfall_Refuses()
        {
            var refusal = Player.ComposeStripRoomRefusal(
                mainPackItemCount: 12, packSlotItemCount: 0, freeMainPackSlots: 6, freeContainerSlots: 6);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "6 more free slots");
        }

        /// <summary>
        /// The same independence in the other direction: spare main-pack room does not buy a pack slot for a
        /// wielded container or a RequiresPackSlot item.
        /// </summary>
        [TestMethod]
        public void ComposeStripRoomRefusal_MainPackRoomDoesNotCoverAContainerSlotShortfall_Refuses()
        {
            var refusal = Player.ComposeStripRoomRefusal(
                mainPackItemCount: 0, packSlotItemCount: 2, freeMainPackSlots: 50, freeContainerSlots: 1);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "1 more free pack slot ");
        }

        /// <summary>
        /// A main-pack shortfall must say so explicitly. The player's only remedy is to clear the MAIN pack,
        /// and a message that says "pack slots" sends someone with an empty side pack looking in the wrong
        /// place - which is the confusion this fix exists to end.
        /// </summary>
        [TestMethod]
        public void ComposeStripRoomRefusal_MainPackShortfall_NamesTheMainPackAndExcludesSidePacks()
        {
            var refusal = Player.ComposeStripRoomRefusal(
                mainPackItemCount: 10, packSlotItemCount: 0, freeMainPackSlots: 3, freeContainerSlots: 99);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "MAIN pack");
            StringAssert.Contains(refusal, "side pack does not count");
        }

        /// <summary>
        /// When both pools are short the main pack is reported first, because it is the one that fills up in
        /// ordinary play; the container-slot line would otherwise mask it.
        /// </summary>
        [TestMethod]
        public void ComposeStripRoomRefusal_BothPoolsShort_ReportsTheMainPackFirst()
        {
            var refusal = Player.ComposeStripRoomRefusal(
                mainPackItemCount: 10, packSlotItemCount: 2, freeMainPackSlots: 3, freeContainerSlots: 0);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "MAIN pack");
        }
    }
}
