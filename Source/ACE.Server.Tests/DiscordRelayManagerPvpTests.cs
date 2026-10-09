using System.Collections;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The PvP Discord feed's enqueue gate (Docs/Pvp/DESIGN.md "Tunables"): discord_webhook_url_pvp defaults
    /// empty, so QueuePvp must be a no-op until BOTH discord_relay_enabled and discord_webhook_url_pvp are set -
    /// an empty URL means no post and no work, exactly like the General/Trade/Audit/Events feeds it mirrors.
    /// Reads the private pvpChannel field's queue by reflection, the only way to observe this static manager's
    /// internal state without a live HTTP endpoint.
    /// </summary>
    [TestClass]
    public class DiscordRelayManagerPvpTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static int PvpQueueCount()
        {
            var field = typeof(DiscordRelayManager).GetField("pvpChannel", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "DiscordRelayManager.pvpChannel was not found by reflection - has it been renamed?");

            var channel = field.GetValue(null);
            var queueProperty = channel.GetType().GetProperty("Queue", BindingFlags.Public | BindingFlags.Instance);
            var queue = (ICollection)queueProperty.GetValue(channel);

            return queue.Count;
        }

        [TestMethod]
        public void QueuePvp_EmptyWebhookUrl_EnqueuesNothing()
        {
            PropertyManager.ModifyBool("discord_relay_enabled", true);
            PropertyManager.ModifyString("discord_webhook_url_pvp", "");

            try
            {
                var before = PvpQueueCount();

                DiscordRelayManager.QueuePvp("[Arena] test match: A defeated B.");

                Assert.AreEqual(before, PvpQueueCount(), "an empty discord_webhook_url_pvp must enqueue nothing");
            }
            finally
            {
                PropertyManager.ModifyBool("discord_relay_enabled", false);
                PropertyManager.ModifyString("discord_webhook_url_pvp", "");
            }
        }

        /// <summary>DISCRIMINATES: with both the relay enabled and a webhook URL configured, the line IS enqueued.</summary>
        [TestMethod]
        public void QueuePvp_RelayEnabledAndUrlConfigured_Enqueues()
        {
            PropertyManager.ModifyBool("discord_relay_enabled", true);
            PropertyManager.ModifyString("discord_webhook_url_pvp", "https://discord.com/api/webhooks/1/test");

            try
            {
                var before = PvpQueueCount();

                DiscordRelayManager.QueuePvp("[Arena] test match: A defeated B.");

                Assert.AreEqual(before + 1, PvpQueueCount(), "relay enabled + webhook configured must enqueue the line");
            }
            finally
            {
                PropertyManager.ModifyBool("discord_relay_enabled", false);
                PropertyManager.ModifyString("discord_webhook_url_pvp", "");
            }
        }

        [TestMethod]
        public void QueuePvp_RelayDisabled_EnqueuesNothing_EvenWithUrlConfigured()
        {
            PropertyManager.ModifyBool("discord_relay_enabled", false);
            PropertyManager.ModifyString("discord_webhook_url_pvp", "https://discord.com/api/webhooks/1/test");

            try
            {
                var before = PvpQueueCount();

                DiscordRelayManager.QueuePvp("[Arena] test match: A defeated B.");

                Assert.AreEqual(before, PvpQueueCount(), "relay disabled must enqueue nothing even with a URL configured");
            }
            finally
            {
                PropertyManager.ModifyString("discord_webhook_url_pvp", "");
            }
        }
    }
}
