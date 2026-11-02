using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Source pins for the turned-host lift's WIRING (the rule itself is TurnedHostLiftTests). The lift only happens for a
    /// spawn marked grounded, and a call site that turns a host with MemoryLayout.FloorOrientation but forgets
    /// "grounded: true" compiles, passes every rule test, and has its markers refused in game (2026-10-25: every rain
    /// marker). These scans fail the build of the suite instead. Precedent: HazardReconciliationTests' source scans.
    /// </summary>
    [TestClass]
    public class TurnedHostWiringTests
    {
        private static string ServerRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "Source", "ACE.Server");

                if (Directory.Exists(Path.Combine(candidate, "HazardMechanics")))
                    return candidate;
            }

            throw new AssertFailedException("could not find Source/ACE.Server/HazardMechanics by walking up from " + AppContext.BaseDirectory);
        }

        /// <summary>The full argument text of every call to <paramref name="method"/>( in <paramref name="text"/>, parens balanced.</summary>
        private static List<string> Calls(string text, string method)
        {
            var calls = new List<string>();

            foreach (Match m in Regex.Matches(text, Regex.Escape(method) + @"\("))
            {
                var depth = 0;

                for (var i = m.Index + method.Length; i < text.Length; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')' && --depth == 0)
                    {
                        calls.Add(text.Substring(m.Index, i - m.Index + 1));
                        break;
                    }
                }
            }

            return calls;
        }

        private static IEnumerable<(string File, string Call)> AllCalls(string method)
            => Directory.GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => Calls(File.ReadAllText(f), method).Select(c => (Path.GetFileName(f), c)));

        [TestMethod]
        public void Every_SpawnTimedEffect_turned_by_FloorOrientation_is_grounded()
        {
            var floorTurned = AllCalls(".SpawnTimedEffect").Where(c => c.Call.Contains("FloorOrientation(")).ToList();

            // rain marker, two ball gap markers, /hazard show, wisp dest-mark (2026-10-25)
            Assert.IsTrue(floorTurned.Count >= 5, $"the scan must see the known floor-turning calls; it found {floorTurned.Count}");

            var missing = floorTurned.Where(c => !Regex.IsMatch(c.Call, @"grounded:\s*true")).ToList();

            Assert.AreEqual(0, missing.Count, "a FloorOrientation spawn without grounded: true is refused at floor z:\n  "
                + string.Join("\n  ", missing.Select(m => $"{m.File}: {Regex.Replace(m.Call, @"\s+", " ")}")));
        }

        [TestMethod]
        public void MemoryWorld_SpawnBeam_passes_raise_as_grounded()
        {
            var text = File.ReadAllText(Path.Combine(ServerRoot(), "HazardMechanics", "MemoryWorld.cs"));
            var spawns = Calls(text, "ctx.SpawnTimedEffect");

            // SpawnBeam turns its host; SpawnPillar (the fire pillar, #1849) spawns unturned, so it needs no lift
            var beams = spawns.Where(s => !s.Contains("Quaternion.Identity")).ToList();

            Assert.AreEqual(1, beams.Count, "MemoryWorld has one turned SpawnTimedEffect call (HazardMemoryWorld.SpawnBeam): " + string.Join(" | ", spawns));
            Assert.IsTrue(Regex.IsMatch(beams[0], @"grounded:\s*raise\b"), "SpawnBeam must pass grounded: raise (raise is the floor-host flag): " + beams[0]);
        }

        [TestMethod]
        public void MemoryMechanic_floor_beams_spawn_with_raise_true()
        {
            // the raise flag IS the grounded flag for memory beams, so every beam on a GroundPoint must pass raise = true
            var text = File.ReadAllText(Path.Combine(ServerRoot(), "HazardMechanics", "MemoryMechanic.cs"));
            var groundBeams = Calls(text, "w.SpawnBeam").Where(c => c.Contains("w.GroundPoint(")).ToList();

            // the pad beams were retired for the fire pillar (#1849); the echo is the remaining ground beam
            Assert.IsTrue(groundBeams.Count >= 1, $"the echo spawns on the ground; found {groundBeams.Count}");

            foreach (var call in groundBeams)
                Assert.IsTrue(Regex.IsMatch(call, @",\s*true\)$"), "a floor beam must pass raise = true: " + Regex.Replace(call, @"\s+", " "));
        }
    }
}
