using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the exact set of ACE.Server files allowed to read a session's raw client endpoint
    /// (<c>EndPointC2S</c>). Anything that LIMITS by client IP must go through IpLimitManager.HouseholdKey /
    /// HouseholdKeyCore so IP household clusters apply; a new raw read in an unlisted file fails here, and the
    /// fix is either to key through the resolver or - for a genuinely raw use (audit, logging, networking) - to
    /// add the file below on purpose.
    /// </summary>
    [TestClass]
    public class RawClientIpReadGuardTests
    {
        // Each entry is a raw read that is deliberately NOT a limit key.
        private static readonly string[] AllowedFiles =
        {
            // The resolver itself, and its admin/diagnostic readouts.
            "Managers/IpLimitManager.cs",
            "Command/Handlers/IpLimitCommands.cs",
            // Session transport and routing: the NetworkManager session cap, handshake and routing stay on the raw IP.
            "Network/Session.cs",
            "Network/NetworkSession.cs",
            "Network/Managers/NetworkManager.cs",
            // Account bookkeeping (CreateIP / LastLoginIP).
            "Network/Handlers/AuthenticationHandler.cs",
            // Log lines beside the household key.
            "Network/Handlers/CharacterHandler.cs",
            // Audit, log and diagnostic fields.
            "Managers/Analytics/AnalyticsManager.cs",
            "Command/Handlers/DeveloperCommands.cs",
            "Command/Handlers/NetCaptureCommands.cs",
            "ThreadDungeons/ThreadPuzzleFailPolicy.cs",
            "WorldEvents/WorldEventParticipation.cs",
            // ip_Address on the reward_claim row (the key beside it is the household key).
            "WorldObjects/Managers/EmoteManager.cs",
            // The raw address world_events_ip_exempt is matched against, and the audit/log field.
            "WorldEvents/WorldEventCacheHandler.cs",
            // The /64 puzzle key's address input (the cluster is checked first).
            "ThreadDungeons/ThreadPuzzleIpKey.cs",
        };

        [TestMethod]
        public void OnlyTheListedFilesReadTheRawClientEndpoint()
        {
            var root = FindServerSource();

            var found = ReadersOf(root, "EndPointC2S");
            var allowed = AllowedFiles.OrderBy(f => f, StringComparer.Ordinal).ToList();

            var unexpected = found.Except(allowed).ToList();
            var vanished = allowed.Except(found).ToList();

            Assert.AreEqual(0, unexpected.Count,
                "A new raw client-IP read appeared in: " + string.Join(", ", unexpected)
                + ". Anything that limits by client IP must key through IpLimitManager.HouseholdKey; add the file to AllowedFiles only for a genuinely raw use.");

            Assert.AreEqual(0, vanished.Count,
                "These files no longer read EndPointC2S; drop them from AllowedFiles so the pin stays exact: " + string.Join(", ", vanished));
        }

        /// <summary>
        /// The file-level pin above cannot see a SECOND raw read inside an allowed file, which is exactly where a
        /// raw key would hide next to a legitimate log read. These three files carry a raw read beside a household
        /// key, so their occurrences are pinned exactly (non-comment lines), like
        /// NormalizeIpKey_and_IsExempt_have_no_new_call_sites.
        /// </summary>
        [TestMethod]
        public void TheKeyedSitesKeepExactlyTheirRawReads()
        {
            var root = FindServerSource();

            var expected = new Dictionary<string, int>
            {
                // ip_Address on the reward_claim request.
                ["WorldObjects/Managers/EmoteManager.cs"] = 1,
                // The refusal and eviction log lines.
                ["Network/Handlers/CharacterHandler.cs"] = 2,
                // The raw address world_events_ip_exempt is matched against.
                ["WorldEvents/WorldEventCacheHandler.cs"] = 1,
            };

            foreach (var pair in expected)
            {
                var count = File.ReadLines(Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar)))
                    .Select(l => l.Trim())
                    .Where(l => !l.StartsWith("//") && !l.StartsWith("*") && !l.StartsWith("/*"))
                    .Count(l => l.Contains("EndPointC2S"));

                Assert.AreEqual(pair.Value, count, $"{pair.Key}: a raw client-IP read was added or removed. If it is not a limit key, update the pin; if it is, key through IpLimitManager.HouseholdKey.");
            }
        }
        private static List<string> ReadersOf(string root, string token)
        {
            var result = new List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains(sep + "bin" + sep) || file.Contains(sep + "obj" + sep))
                    continue;

                var hit = File.ReadLines(file)
                    .Select(l => l.Trim())
                    .Where(l => !l.StartsWith("//") && !l.StartsWith("*") && !l.StartsWith("/*"))
                    .Any(l => l.Contains(token));

                if (hit)
                    result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string FindServerSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");
                if (Directory.Exists(candidate))
                    return candidate;

                candidate = Path.Combine(dir.FullName, "ACE.Server");
                if (Directory.Exists(Path.Combine(candidate, "ThreadDungeons")))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
