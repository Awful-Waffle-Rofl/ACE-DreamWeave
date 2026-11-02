using System.Collections.Generic;
using System.Linq;

using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Outbound
{
    /// <summary>
    /// NetworkSession.TagFinalPiece on a message split across several fragments: exactly one piece carries the ring
    /// index, and it is the piece that brought DataRemaining to 0, whichever order SendBundle places the pieces in
    /// (the tail first when the message follows a small one in the bundle, or the pieces in index order when it is
    /// the first, large, message).
    /// </summary>
    [TestClass]
    public class OutboundRingFragmentTests
    {
        private sealed class LargeMessage : GameMessage
        {
            public LargeMessage(int bodyBytes) : base(GameMessageOpcode.ObjectCreate, GameMessageGroup.SmartboxQueue)
            {
                for (var i = 0; i < bodyBytes; i++)
                    Writer.Write((byte)i);
            }
        }

        private const long RingIndex = 4242;

        private static List<(ServerPacketFragment piece, int remainingAfter)> Place(MessageFragment fragment, bool tailFirst)
        {
            var placed = new List<(ServerPacketFragment, int)>();

            if (tailFirst)
            {
                // SendBundle's small-message branch: the tail goes as soon as it fits, then the rest in index order
                Assert.IsFalse(fragment.TailSent);
                var tail = fragment.GetTailFragment();
                NetworkSession.TagFinalPiece(fragment, tail);
                placed.Add((tail, fragment.DataRemaining));
            }

            while (fragment.DataRemaining > 0)
            {
                var next = fragment.GetNextFragment();
                NetworkSession.TagFinalPiece(fragment, next);
                placed.Add((next, fragment.DataRemaining));
            }

            return placed;
        }

        private static void AssertOnlyTheFinalPieceIsTagged(List<(ServerPacketFragment piece, int remainingAfter)> placed)
        {
            Assert.IsTrue(placed.Count >= 3, "the message really spans three or more fragments");

            var tagged = placed.Where(p => p.piece.RingIndex != 0).ToList();

            Assert.AreEqual(1, tagged.Count, "exactly one piece carries the ring index");
            Assert.AreEqual(RingIndex, tagged[0].piece.RingIndex);
            Assert.AreEqual(0, tagged[0].remainingAfter, "the tagged piece is the one that brought DataRemaining to 0");
            Assert.AreSame(placed.Last().piece, tagged[0].piece, "and it is the last piece placed");
            Assert.IsTrue(placed.Take(placed.Count - 1).All(p => p.remainingAfter > 0));
        }

        private static MessageFragment Fragment()
        {
            var message = new LargeMessage(PacketFragment.MaxFragmentDataSize * 2 + 100);
            Assert.IsTrue(message.Data.Length > PacketFragment.MaxFragmentDataSize * 2);

            return new MessageFragment(message, 1) { RingIndex = RingIndex };
        }

        [TestMethod]
        public void TailFirst_TheLastIndexedPieceIsTagged()
        {
            var placed = Place(Fragment(), tailFirst: true);

            AssertOnlyTheFinalPieceIsTagged(placed);
            Assert.AreEqual(0, placed[0].piece.RingIndex, "the tail went first and is not the final piece");
            Assert.AreEqual((ushort)(placed.Count - 1), placed[0].piece.Header.Index, "the first piece placed was the tail");
        }

        [TestMethod]
        public void IndexOrder_TheTailIsTagged()
        {
            var placed = Place(Fragment(), tailFirst: false);

            AssertOnlyTheFinalPieceIsTagged(placed);
            Assert.AreEqual((ushort)(placed.Count - 1), placed.Last().piece.Header.Index);
        }

        [TestMethod]
        public void UnrecordedMessage_TagsNothing()
        {
            var fragment = new MessageFragment(new LargeMessage(PacketFragment.MaxFragmentDataSize * 2 + 100), 1);

            Assert.IsTrue(Place(fragment, tailFirst: true).All(p => p.piece.RingIndex == 0));
        }
    }
}
