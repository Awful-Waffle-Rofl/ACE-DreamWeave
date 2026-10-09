using System.Linq;

using ACE.Entity;
using ACE.Entity.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers ObjDesc.MergeItemTextureChange, the merge rule Creature.CalculateObjDesc uses to fold an
    /// equipped item's own texture_map rows into the wearer's ObjDesc.
    ///
    /// The helper is tested rather than CalculateObjDesc itself because that method reads
    /// client_portal.dat and a live EquippedObjects collection; the merge rule is the whole of the new
    /// behaviour and is pure.
    /// </summary>
    [TestClass]
    public class WornItemObjDescMergeTests
    {
        private static PropertiesTextureMap Row(byte part, uint oldTex, uint newTex)
        {
            return new PropertiesTextureMap { PartIndex = part, OldTexture = oldTex, NewTexture = newTex };
        }

        [TestMethod]
        public void MergeItemTextureChange_NoExistingEntry_AddsRow()
        {
            var objDesc = new ObjDesc();

            objDesc.MergeItemTextureChange(Row(16, 0x05000AAA, 0x05000BBB));

            Assert.AreEqual(1, objDesc.TextureChanges.Count);
            Assert.AreEqual((byte)16, objDesc.TextureChanges[0].PartIndex);
            Assert.AreEqual(0x05000AAAu, objDesc.TextureChanges[0].OldTexture);
            Assert.AreEqual(0x05000BBBu, objDesc.TextureChanges[0].NewTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_ChainsOntoClothingTableSwap_CollapsesToOneEntry()
        {
            var objDesc = new ObjDesc();

            // What the ClothingTable already put on the wearer's head.
            objDesc.AddTextureChange(Row(16, 0x05000111, 0x05000222));

            // The item's own row picks the ClothingTable's result up and swaps it again.
            objDesc.MergeItemTextureChange(Row(16, 0x05000222, 0x05000333));

            Assert.AreEqual(1, objDesc.TextureChanges.Count);
            Assert.AreEqual(0x05000111u, objDesc.TextureChanges[0].OldTexture);
            Assert.AreEqual(0x05000333u, objDesc.TextureChanges[0].NewTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_SamePartDifferentSourceTexture_DoesNotChain()
        {
            var objDesc = new ObjDesc();

            objDesc.AddTextureChange(Row(16, 0x05000111, 0x05000222));
            objDesc.MergeItemTextureChange(Row(16, 0x05000999, 0x05000333));

            Assert.AreEqual(2, objDesc.TextureChanges.Count);
            Assert.AreEqual(0x05000222u, objDesc.TextureChanges[0].NewTexture);
            Assert.AreEqual(0x05000999u, objDesc.TextureChanges[1].OldTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_MatchingTextureOnAnotherPart_DoesNotChain()
        {
            var objDesc = new ObjDesc();

            objDesc.AddTextureChange(Row(9, 0x05000111, 0x05000222));
            objDesc.MergeItemTextureChange(Row(16, 0x05000222, 0x05000333));

            Assert.AreEqual(2, objDesc.TextureChanges.Count);
            Assert.AreEqual(0x05000222u, objDesc.TextureChanges.Single(t => t.PartIndex == 9).NewTexture);
            Assert.AreEqual(0x05000333u, objDesc.TextureChanges.Single(t => t.PartIndex == 16).NewTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_DoesNotMutateExistingEntryInstance()
        {
            var objDesc = new ObjDesc();

            // Stands in for a row instance owned by a biota: the merge must not write through it.
            var owned = Row(16, 0x05000111, 0x05000222);
            objDesc.TextureChanges.Add(owned);

            objDesc.MergeItemTextureChange(Row(16, 0x05000222, 0x05000333));

            Assert.AreEqual(0x05000222u, owned.NewTexture);
            Assert.AreEqual(0x05000333u, objDesc.TextureChanges[0].NewTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_DuplicateRow_IsNotAddedTwice()
        {
            var objDesc = new ObjDesc();

            objDesc.MergeItemTextureChange(Row(16, 0x05000AAA, 0x05000BBB));
            objDesc.MergeItemTextureChange(Row(16, 0x05000AAA, 0x05000BBB));

            Assert.AreEqual(1, objDesc.TextureChanges.Count);
        }

        [TestMethod]
        public void MergeItemTextureChange_ChainCandidateBelowStartIndex_IsNotSpliced()
        {
            var objDesc = new ObjDesc();

            // Stands in for an EARLIER equipped piece's entry on the same part. It ends at the same
            // texture the incoming row starts from, which is exactly the accidental-chain trap.
            objDesc.AddTextureChange(Row(16, 0x05000111, 0x05000222));

            var startIndex = objDesc.TextureChanges.Count;

            // This item's own ClothingTable contributed nothing on this part, so there is nothing
            // legitimate to chain onto and the row must land as its own entry.
            objDesc.MergeItemTextureChange(Row(16, 0x05000222, 0x05000333), startIndex);

            Assert.AreEqual(2, objDesc.TextureChanges.Count);
            Assert.AreEqual(0x05000111u, objDesc.TextureChanges[0].OldTexture);
            Assert.AreEqual(0x05000222u, objDesc.TextureChanges[0].NewTexture, "the earlier piece's entry must be left alone");
            Assert.AreEqual(0x05000222u, objDesc.TextureChanges[1].OldTexture);
            Assert.AreEqual(0x05000333u, objDesc.TextureChanges[1].NewTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_ChainCandidateAtOrAfterStartIndex_StillSplices()
        {
            var objDesc = new ObjDesc();

            objDesc.AddTextureChange(Row(16, 0x05000111, 0x05000222));

            var startIndex = objDesc.TextureChanges.Count;

            // This item's OWN ClothingTable entry, added after the mark.
            objDesc.AddTextureChange(Row(16, 0x05000AAA, 0x05000BBB));

            objDesc.MergeItemTextureChange(Row(16, 0x05000BBB, 0x05000CCC), startIndex);

            Assert.AreEqual(2, objDesc.TextureChanges.Count);
            Assert.AreEqual(0x05000222u, objDesc.TextureChanges[0].NewTexture);
            Assert.AreEqual(0x05000AAAu, objDesc.TextureChanges[1].OldTexture);
            Assert.AreEqual(0x05000CCCu, objDesc.TextureChanges[1].NewTexture);
        }

        [TestMethod]
        public void MergeItemTextureChange_TwoRowsOntoSameSourceTexture_ChainThenAdd()
        {
            var objDesc = new ObjDesc();

            objDesc.AddTextureChange(Row(9, 0x05000111, 0x05000222));

            // First chains. Second finds nothing ending at 0x05000444 and lands as its own entry.
            objDesc.MergeItemTextureChange(Row(9, 0x05000222, 0x05000333));
            objDesc.MergeItemTextureChange(Row(9, 0x05000444, 0x05000555));

            Assert.AreEqual(2, objDesc.TextureChanges.Count);
            Assert.AreEqual(0x05000333u, objDesc.TextureChanges[0].NewTexture);
            Assert.AreEqual(0x05000444u, objDesc.TextureChanges[1].OldTexture);
        }
    }
}
