using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The staff @teleto admission into a private (ephemeral) instance. A Player cannot be constructed in this
    /// harness, so uninitialized instances stand in: Accepts only compares them by reference. The live lookup
    /// through LandblockManager is not exercised; the pure half (AdmitStaff, ClassifyAdmission), the
    /// non-ephemeral no-op and a source-scan of the HandleTeleto wiring are.
    /// </summary>
    [TestClass]
    public class StaffInstanceAdmitTests
    {
        private const uint InCell = 0x01F50229;
        private const uint OutCell = 0x02F50229;

        private static Player NewPlayer() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        private static EphemeralFootprint Footprint() => EphemeralFootprint.Single(0x01F5);

        [TestMethod]
        public void NonAdmittedNonOwner_IsRefused()
        {
            var realm = new EphemeralRealm(NewPlayer()) { OpenToFellowship = false };

            Assert.IsFalse(realm.Accepts(NewPlayer()));
        }

        [TestMethod]
        public void Admit_MakesAcceptsTrueForANonOwner_AndOnlyForThatPlayer()
        {
            var realm = new EphemeralRealm(NewPlayer()) { OpenToFellowship = false };
            var staff = NewPlayer();
            var other = NewPlayer();

            Assert.IsFalse(realm.Accepts(staff), "control: refused before admission");

            Assert.AreEqual(InstanceRouting.StaffAdmitResult.Admitted, InstanceRouting.AdmitStaff(realm, Footprint(), InCell, staff));

            Assert.IsTrue(realm.Accepts(staff));
            Assert.IsFalse(realm.Accepts(other), "admission is per player, not a blanket bypass");
        }

        [TestMethod]
        public void AdmitStaff_NullRealm_ReportsNotLive()
        {
            Assert.AreEqual(InstanceRouting.StaffAdmitResult.NotLive, InstanceRouting.AdmitStaff(null, Footprint(), InCell, NewPlayer()));
        }

        [TestMethod]
        public void AdmitStaff_CellOutsideTheFootprint_ReportsOutsideFootprint_AndAdmitsNobody()
        {
            var realm = new EphemeralRealm(NewPlayer()) { OpenToFellowship = false };
            var staff = NewPlayer();

            Assert.AreEqual(InstanceRouting.StaffAdmitResult.OutsideFootprint, InstanceRouting.AdmitStaff(realm, Footprint(), OutCell, staff));

            Assert.IsFalse(realm.Accepts(staff), "an outside-footprint request must not admit");
            Assert.AreEqual(0, realm.AllowedPlayers.Count);
        }

        [TestMethod]
        public void AdmitStaff_NullFootprint_AdmitsNobody()
        {
            var realm = new EphemeralRealm(NewPlayer()) { OpenToFellowship = false };
            var staff = NewPlayer();

            Assert.AreEqual(InstanceRouting.StaffAdmitResult.OutsideFootprint, InstanceRouting.AdmitStaff(realm, null, InCell, staff));
            Assert.IsFalse(realm.Accepts(staff));
        }

        [TestMethod]
        public void ClassifyAdmission_AllThreeResults()
        {
            var now = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(InstanceRouting.StaffAdmitResult.Admitted, InstanceRouting.ClassifyAdmission(true, now, now.AddHours(1)));
            Assert.AreEqual(InstanceRouting.StaffAdmitResult.Admitted, InstanceRouting.ClassifyAdmission(true, now, now.AddHours(-1)), "Accepts is authoritative");
            Assert.AreEqual(InstanceRouting.StaffAdmitResult.Expired, InstanceRouting.ClassifyAdmission(false, now, now.AddHours(-1)));
            Assert.AreEqual(InstanceRouting.StaffAdmitResult.RefusedByRunPolicy, InstanceRouting.ClassifyAdmission(false, now, now.AddHours(1)));
        }

        [TestMethod]
        public void TryAdmitStaffToInstance_NonEphemeralPosition_IsANoOp()
        {
            var persistent = new Position(InCell, 10f, 10f, 0f, 0f, 0f, 0f, 1f, Position.InstanceIDFromVars(1, 0, false));

            Assert.AreEqual(InstanceRouting.StaffAdmitResult.NotEphemeral, InstanceRouting.TryAdmitStaffToInstance(persistent, NewPlayer()));
            Assert.AreEqual(InstanceRouting.StaffAdmitResult.NotEphemeral, InstanceRouting.TryAdmitStaffToInstance(null, NewPlayer()));
        }

        [TestMethod]
        public void TryAdmitStaffToInstance_EphemeralInstanceNotLive_ReportsNotLive()
        {
            var ghost = new Position(InCell, 10f, 10f, 0f, 0f, 0f, 0f, 1f, Position.InstanceIDFromVars(1, 0x7ABC, true));

            Assert.AreEqual(InstanceRouting.StaffAdmitResult.NotLive, InstanceRouting.TryAdmitStaffToInstance(ghost, NewPlayer()));
        }

        // ------------------------------------------------------------------ wiring (source scan)

        [TestMethod]
        public void HandleTeleto_AdmitsOnTheSnapshotBeforeTeleporting()
        {
            var body = Method(Source("Command", "Handlers", "AdminCommands.cs"), "public static void HandleTeleto(Session session, params string[] parameters)");

            var snapshot = body.IndexOf("var dest = new Position(player.Location);", StringComparison.Ordinal);
            var admit = body.IndexOf("InstanceRouting.TryAdmitStaffToInstance(dest, session.Player)", StringComparison.Ordinal);
            var teleport = body.IndexOf("session.Player.Teleport(dest);", StringComparison.Ordinal);

            Assert.IsTrue(snapshot >= 0, "HandleTeleto must snapshot the destination once");
            Assert.IsTrue(admit > snapshot, "HandleTeleto must admit on the snapshot");
            Assert.IsTrue(teleport > admit, "HandleTeleto must admit before it teleports, on the same snapshot");
        }

        // ------------------------------------------------------------------ helpers

        private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ");

        /// <summary>The file, with whole-line // and /// comments dropped and whitespace collapsed.</summary>
        private static string Source(params string[] parts)
        {
            var relative = Path.Combine("Source", Path.Combine("ACE.Server", Path.Combine(parts)));

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (File.Exists(candidate))
                {
                    var text = File.ReadAllText(candidate).Replace("\r\n", "\n");
                    text = Regex.Replace(text, @"^[ \t]*//.*\n", string.Empty, RegexOptions.Multiline);
                    return Normalize(text);
                }
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(Normalize(signature), StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }
    }
}
