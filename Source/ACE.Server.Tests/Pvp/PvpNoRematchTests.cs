using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

using static ACE.Server.Command.Handlers.PvpArenaCommands;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// /arena norematch: the argument parse, the chat lines, the usage and idle-status mentions, and the Player property
    /// (PropertyBool 9075) round trip on a seeded, uninitialized Player (the PvpPlayerHookTests recipe: only inherited WorldObject state
    /// is seeded, so Player's type initializer never runs). Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PvpNoRematchTests
    {
        [TestMethod]
        public void ParseNoRematch_NoArgs_ShowsTheState()
        {
            Assert.AreEqual(NoRematchParseOutcome.Show, ParseNoRematch(new string[0]));
            Assert.AreEqual(NoRematchParseOutcome.Show, ParseNoRematch(null));
        }

        [TestMethod]
        public void ParseNoRematch_OnAndOff_AnyCase()
        {
            Assert.AreEqual(NoRematchParseOutcome.On, ParseNoRematch(new[] { "on" }));
            Assert.AreEqual(NoRematchParseOutcome.On, ParseNoRematch(new[] { "ON" }));
            Assert.AreEqual(NoRematchParseOutcome.Off, ParseNoRematch(new[] { "Off" }));
        }

        [TestMethod]
        public void ParseNoRematch_AnythingElse_IsBadUsage()
        {
            Assert.AreEqual(NoRematchParseOutcome.BadUsage, ParseNoRematch(new[] { "maybe" }));
            Assert.AreEqual(NoRematchParseOutcome.BadUsage, ParseNoRematch(new[] { "on", "off" }));
        }

        [TestMethod]
        public void StateText_DiffersByState_AndIsAscii()
        {
            Assert.AreEqual(PvpArenaText.NoRematchOn, NoRematchStateText(true));
            Assert.AreEqual(PvpArenaText.NoRematchOff, NoRematchStateText(false));

            foreach (var text in new[] { PvpArenaText.NoRematchOn, PvpArenaText.NoRematchOff, PvpArenaText.NoRematchUsage, PvpArenaText.WaitingForFreshOpponent })
                foreach (var ch in text)
                    Assert.IsTrue(ch < 128, $"non-ASCII character in: {text}");
        }

        [TestMethod]
        public void WaitingNotice_NamesTheCommand()
        {
            Assert.AreEqual("[Arena] Waiting for a fresh opponent. Use /arena norematch off to accept an unrewarded rematch.", PvpArenaText.WaitingForFreshOpponent);
        }

        [TestMethod]
        public void UsageAndIdleStatus_MentionNoRematch()
        {
            StringAssert.Contains(ArenaUsage, "norematch [on|off]");
            StringAssert.Contains(PvpArenaText.StatusIdle, "/arena norematch");
        }

        // ---------------- Player property ----------------

        private static Player SeededPlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, Position>());

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        [TestMethod]
        public void Property_AbsentReadsOff_SetOnRoundTrips_OffRemovesIt()
        {
            var player = SeededPlayer();

            Assert.IsFalse(player.PvpArenaRefuseUnpaidRematch, "absent = OFF");
            Assert.IsNull(player.GetProperty(PropertyBool.PvpArenaRefuseUnpaidRematch));

            player.PvpArenaRefuseUnpaidRematch = true;
            Assert.IsTrue(player.PvpArenaRefuseUnpaidRematch);
            Assert.AreEqual(true, player.GetProperty(PropertyBool.PvpArenaRefuseUnpaidRematch));

            player.PvpArenaRefuseUnpaidRematch = false;
            Assert.IsFalse(player.PvpArenaRefuseUnpaidRematch);
            Assert.IsNull(player.GetProperty(PropertyBool.PvpArenaRefuseUnpaidRematch), "OFF removes the property rather than storing false");
        }

        [TestMethod]
        public void PropertyId_Is9075()
        {
            Assert.AreEqual(9075, (int)PropertyBool.PvpArenaRefuseUnpaidRematch);
        }
    }
}
