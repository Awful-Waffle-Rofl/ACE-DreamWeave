using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>Every Avatar chat line goes out as ChatMessageType.Allegiance (0x12), pinned on the message the send actually builds.</summary>
    [TestClass]
    public class AvatarAnnouncerTests
    {
        [TestMethod]
        public void Line_IsSentAsAllegianceChatType()
        {
            // WriteString16L needs the 1252 code page, which the server registers at startup.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var msg = AvatarAnnouncer.Line("hello");

            Assert.AreEqual("hello", msg.Text);

            // GameMessageSystemChat writes: opcode (u32), String16L text, then the chat type as an int, which is the last four bytes.
            var bytes = msg.Data.ToArray();
            var type = BitConverter.ToInt32(bytes, bytes.Length - 4);

            Assert.AreEqual((int)ChatMessageType.Allegiance, type);
            Assert.AreEqual(0x12, type);
        }

        [TestMethod]
        public void AvatarAnnouncerSource_BuildsEveryMessageThroughLine_AndNeverUsesBroadcast()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;

            while (dir != null && path == null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "AllegianceAvatar", "AvatarAnnouncer.cs");
                if (File.Exists(candidate))
                    path = candidate;
                dir = dir.Parent;
            }

            Assert.IsNotNull(path, "Could not find AvatarAnnouncer.cs by walking up from " + AppContext.BaseDirectory);

            var src = File.ReadAllText(path);

            Assert.IsFalse(src.Contains("ChatMessageType.Broadcast"), "an Avatar line must not go out as Broadcast");
            Assert.AreEqual(1, Regex.Matches(src, @"new GameMessageSystemChat\(").Count, "only Line() may build the chat message");
            Assert.AreEqual(2, Regex.Matches(src, @"EnqueueSend\(Line\(text\)\)").Count, "the monarch and the member sends must both send Line(text)");
            Assert.AreEqual(2, Regex.Matches(src, @"EnqueueSend\(").Count, "no other send site");
        }
    }
}
