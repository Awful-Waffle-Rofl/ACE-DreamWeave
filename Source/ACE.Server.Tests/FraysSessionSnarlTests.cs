using System.Linq;
using System.Text.Json;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Session log version 4: the Snarls a camp saw, their kill times, the time while one was up, and their XP.</summary>
    [TestClass]
    public class FraysSessionSnarlTests
    {
        [TestMethod]
        public void Record_CarriesTheSnarls()
        {
            var c = new FraysCamper { Guid = 1, Name = "Tester", StartedAt = 0 };
            c.Play.Seconds[FraysSessions.Combat] = 300;
            c.Play.Seconds[FraysSessions.Idle] = 100;
            c.Play.SnarlsSpawned["brute"] = 2;
            c.Play.SnarlsSpawned["swarm"] = 1;
            c.Play.SnarlKills.Add(("brute", 80));
            c.Play.SnarlKills.Add(("brute", 120));
            c.Play.SnarlKills.Add(("swarm", 95.04));
            c.Play.SnarlUpSeconds[FraysSessions.Combat] = 250;
            c.Play.SnarlUpSeconds[FraysSessions.Idle] = 40;
            c.Play.XpFromSnarls = 12345;

            var r = FraysSessions.Record("vc1", c, 400, "test");

            Assert.AreEqual(4, r.LogVersion);
            Assert.AreEqual(2, r.SnarlsSpawned["brute"]);
            Assert.AreEqual(3, r.SnarlKills.Count);
            Assert.AreEqual(100, r.SnarlSecondsByKind["brute"], 1e-9);
            Assert.AreEqual(95, r.SnarlSecondsByKind["swarm"], 1e-9);
            Assert.AreEqual(250, r.SnarlUpSeconds[FraysSessions.Combat]);
            Assert.AreEqual(0, r.SnarlUpSeconds[FraysSessions.Walk], "every time kind is listed");
            Assert.AreEqual(12345, r.XpFromSnarls);

            var line = JsonSerializer.Serialize(r, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            Assert.IsTrue(line.Contains("snarl_kills") && line.Contains("xp_from_snarls"), line);
        }
    }
}
