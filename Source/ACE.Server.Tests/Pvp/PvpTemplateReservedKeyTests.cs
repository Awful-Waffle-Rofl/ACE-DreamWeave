using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Command.Web;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// L4 (2026-10-03 review): "duo" and "group" are the flags /arena join reads, so a template keyed with one could
    /// never be chosen. ValidateKey reserves them, and /pvptemplate snapshot refuses them before anything is queued.
    /// </summary>
    [TestClass]
    public class PvpTemplateReservedKeyTests
    {
        [TestMethod]
        public void ValidateKey_RefusesTheJoinFlagWords_ButNotOrdinaryKeys()
        {
            StringAssert.Contains(PvpTemplateSnapshotService.ValidateKey("duo"), "reserved");
            StringAssert.Contains(PvpTemplateSnapshotService.ValidateKey("group"), "reserved");

            Assert.IsNull(PvpTemplateSnapshotService.ValidateKey("duelist"), "control: an ordinary key");
            Assert.IsNull(PvpTemplateSnapshotService.ValidateKey("duo-mage"), "control: a key that only contains a flag word");
            Assert.IsNull(PvpTemplateSnapshotService.ValidateKey("groups"));
        }

        /// <summary>
        /// Through the command: /pvptemplate snapshot with a reserved key (any case; the handler lowercases it) replies
        /// with the reserved line and stops there. Replies are captured through the web command context, the null-session
        /// output path every console reply takes.
        /// </summary>
        [TestMethod]
        public void Snapshot_ReservedKey_IsRefusedByTheCommand()
        {
            foreach (var key in new[] { "duo", "GROUP" })
            {
                var ctx = new WebCommandContext(42, "test", "test", null, null);

                using (WebCommandContext.Enter(ctx))
                    PvpTemplateAdminCommands.HandlePvpTemplate(null, "snapshot", key, "Somebody");

                var (lines, _) = ctx.Snapshot();
                var texts = lines.Select(l => l.Text).ToList();

                Assert.AreEqual(1, texts.Count, $"{key}: exactly one reply, nothing queued after it");
                StringAssert.StartsWith(texts[0], "[PvpTemplate] '" + key.ToLowerInvariant() + "' is reserved", key);
            }
        }
    }
}
