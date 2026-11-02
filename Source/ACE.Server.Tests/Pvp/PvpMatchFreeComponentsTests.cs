using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// pvp_match_free_components (Docs/Pvp/TEMPLATES.md "Issued kit"): the computed waiver on a seeded Player. The setting is read
    /// through PvpArenaHookSettings.MatchFreeComponentsSource, seeded here and restored in Cleanup. The cast-path call sites
    /// (HasComponentsForSpell, TryBurnComponents) need a Spell and inventory that the harness cannot build, so they are pinned on
    /// source; the decision they share is PvpMatchComponentsWaived, tested directly.
    /// </summary>
    [TestClass]
    public class PvpMatchFreeComponentsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedSource;

        [TestInitialize]
        public void Setup() => savedSource = PvpArenaHookSettings.MatchFreeComponentsSource;

        [TestCleanup]
        public void Cleanup() => PvpArenaHookSettings.MatchFreeComponentsSource = savedSource;

        private static PvpPlayerBinding Bind() =>
            new PvpPlayerBinding(new PvpMatch(Guid.NewGuid(), "bg_ad", new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            }, T0), 0, PvpMatchState.Live, true, true, true, true, true);

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static Player Seeded(PvpPlayerBinding binding)
        {
            var wo = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            wo.SetPvpBindingForTests(binding);
            return wo;
        }

        [TestMethod]
        public void Bound_SettingOn_IsWaived_AndClientIsToldNotRequired()
        {
            PvpArenaHookSettings.MatchFreeComponentsSource = () => true;
            var p = Seeded(Bind());

            Assert.IsTrue(p.PvpMatchComponentsWaived);
            Assert.IsFalse(p.ClientSpellComponentsRequired, "waived: the client must hold false (short-circuits before any PropertyManager read)");
        }

        [TestMethod]
        public void Unbound_SettingOn_IsNotWaived()
        {
            PvpArenaHookSettings.MatchFreeComponentsSource = () => true;

            Assert.IsFalse(Seeded(null).PvpMatchComponentsWaived);
        }

        [TestMethod]
        public void Bound_SettingOff_IsNotWaived()
        {
            PvpArenaHookSettings.MatchFreeComponentsSource = () => false;

            Assert.IsFalse(Seeded(Bind()).PvpMatchComponentsWaived);
        }

        [TestMethod]
        public void SettingIsReadAtEachCall_NotCached()
        {
            var on = true;
            PvpArenaHookSettings.MatchFreeComponentsSource = () => on;
            var p = Seeded(Bind());

            Assert.IsTrue(p.PvpMatchComponentsWaived);
            on = false;
            Assert.IsFalse(p.PvpMatchComponentsWaived);
        }

        [TestMethod]
        public void Waiver_WritesNoPersistedProperty()
        {
            PvpArenaHookSettings.MatchFreeComponentsSource = () => true;
            var p = Seeded(Bind());

            _ = p.PvpMatchComponentsWaived;
            _ = p.ClientSpellComponentsRequired;

            Assert.AreEqual(0, p.Biota.PropertiesBool?.Count ?? 0, "the waiver is computed; SpellComponentsRequired is a saved PropertyBool and must not be written");
        }

        [TestMethod]
        public void CastPath_BothSites_ShortCircuitOnTheWaiver()
        {
            var src = System.IO.File.ReadAllText(FindFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));

            StringAssert.Contains(src, "if (PvpMatchComponentsWaived || SafeSpellComponents");
            StringAssert.Contains(src, "if (PvpMatchComponentsWaived || !SpellComponentsRequired");
        }

        [TestMethod]
        public void ClientSync_BindAndExit_UseTheHelper_AfterTheBindingChanges()
        {
            var arena = System.IO.File.ReadAllText(FindFile("Source", "ACE.Server", "WorldObjects", "Player_PvpArena.cs")).Replace("\r\n", "\n");

            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(arena, @"\n\s*SendClientSpellComponentsRequired\(\);").Count, "bind and exit");
            Assert.IsFalse(arena.Contains("PropertyBool.SpellComponentsRequired, true"), "never writes the saved property");

            var enter = Slice(arena, "internal void EnterPvpMatchNow(", "public void ExitPvpMatch(");
            var enterBind = enter.IndexOf("pvpBinding = binding;", StringComparison.Ordinal);
            var enterSend = enter.IndexOf("SendClientSpellComponentsRequired();", StringComparison.Ordinal);
            Assert.IsTrue(enterBind >= 0 && enterSend > enterBind, "bind: the binding must be set BEFORE the send, or the helper reads the unbound state");

            var exit = Slice(arena, "internal bool ExitPvpMatchNow(", "public void RestorePvpMatchStatusAtLogin(");
            var exitClear = exit.IndexOf("pvpBinding = null;", StringComparison.Ordinal);
            var exitSend = exit.IndexOf("SendClientSpellComponentsRequired();", StringComparison.Ordinal);
            Assert.IsTrue(exitClear >= 0 && exitSend > exitClear, "exit: the binding must be cleared BEFORE the send, or the helper still reads waived");
        }

        [TestMethod]
        public void Login_OnlyEverSendsFalse_UnderTheUnboundCondition()
        {
            var net = System.IO.File.ReadAllText(FindFile("Source", "ACE.Server", "WorldObjects", "Player_Networking.cs"));

            StringAssert.Contains(net, "if (PvpMatchComponentsWaived || !PropertyManager.GetBool(\"require_spell_comps\").Item)");
            StringAssert.Contains(net, "PropertyBool.SpellComponentsRequired, false)");
            Assert.IsFalse(net.Contains("PropertyBool.SpellComponentsRequired, true"), "login never sends true");
        }

        [TestMethod]
        public void RequireComps_Command_SendsTheEffectiveValue()
        {
            var dev = System.IO.File.ReadAllText(FindFile("Source", "ACE.Server", "Command", "Handlers", "DeveloperCommands.cs"));
            var body = Slice(dev, "public static void HandleRequireComps(", "public static void");

            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(body, @"PropertyBool\.SpellComponentsRequired, session\.Player\.ClientSpellComponentsRequired\)").Count);
            Assert.IsFalse(body.Contains("PropertyBool.SpellComponentsRequired, session.Player.SpellComponentsRequired)"), "the raw property would override the waiver");
        }

        [TestMethod]
        public void ClientValue_TruthTable()
        {
            var savedReq = PvpArenaHookSettings.RequireSpellCompsSource;
            try
            {
                PvpArenaHookSettings.MatchFreeComponentsSource = () => true;
                PvpArenaHookSettings.RequireSpellCompsSource = () => true;

                var waived = Seeded(Bind());
                waived.SpellComponentsRequired = true;
                Assert.IsFalse(waived.ClientSpellComponentsRequired, "waived: false even with stored true and require on");

                var unbound = Seeded(null);
                unbound.SpellComponentsRequired = true;
                Assert.IsTrue(unbound.ClientSpellComponentsRequired, "unbound, stored true, require on: true");

                unbound.SpellComponentsRequired = false;
                Assert.IsFalse(unbound.ClientSpellComponentsRequired, "stored false: false");

                unbound.SpellComponentsRequired = true;
                PvpArenaHookSettings.RequireSpellCompsSource = () => false;
                Assert.IsFalse(unbound.ClientSpellComponentsRequired, "require_spell_comps off: false");
            }
            finally
            {
                PvpArenaHookSettings.RequireSpellCompsSource = savedReq;
            }
        }

        private static string Slice(string src, string from, string to)
        {
            var a = src.IndexOf(from, StringComparison.Ordinal);
            Assert.IsTrue(a >= 0, $"'{from}' not found");
            var b = src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            Assert.IsTrue(b > a, $"'{to}' not found after '{from}'");
            return src.Substring(a, b - a);
        }
        private static string FindFile(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, System.IO.Path.Combine(parts));
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            Assert.Fail($"Could not find {string.Join("/", parts)} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}