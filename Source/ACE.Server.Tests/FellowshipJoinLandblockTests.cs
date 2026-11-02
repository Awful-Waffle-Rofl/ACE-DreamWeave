using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>/fship joinlandblock must never auto-create a fellowship.</summary>
    [TestClass]
    public class FellowshipJoinLandblockTests
    {
        [TestMethod]
        public void JoinLandblock_NoMatch_MessagePointsAtCreate()
        {
            StringAssert.Contains(FellowshipCommands.NoOpenFellowshipMessage, "/fship create");
        }

        [TestMethod]
        public void JoinLandblock_Handler_DoesNotCreateAFellowship()
        {
            // Player cannot be constructed in a unit test (its constructor reaches the auth DB), so pin the source.
            var dir = Path.GetDirectoryName(typeof(FellowshipJoinLandblockTests).Assembly.Location);
            string path = null;

            while (dir != null)
            {
                var candidate = Path.Combine(dir, "Source", "ACE.Server", "Command", "Handlers", "FellowshipCommands.cs");

                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }

            Assert.IsNotNull(path, "Could not find FellowshipCommands.cs by walking up from the test assembly");

            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            var start = text.IndexOf("private static void HandleJoinLandblock(", System.StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "HandleJoinLandblock not found");

            var end = text.IndexOf("private static void HandleLeech(", start, System.StringComparison.Ordinal);
            Assert.IsTrue(end > start, "end of HandleJoinLandblock not found");

            Assert.IsFalse(text.Substring(start, end - start).Contains("FellowshipCreate("),
                "HandleJoinLandblock must not create a fellowship");
        }
    }
}
