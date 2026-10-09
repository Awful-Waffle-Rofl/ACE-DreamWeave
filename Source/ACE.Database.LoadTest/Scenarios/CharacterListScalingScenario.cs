using System;
using System.Collections.Generic;
using System.Diagnostics;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// ShardDatabase.GetCharacterList (ACE.Database/ShardDatabase.cs) scans the entire CharacterContexts
    /// ConditionalWeakTable with a linear FirstOrDefault for every character it loads. But that table is
    /// weakly-keyed - an entry only survives as long as something else holds a strong reference to that Character
    /// object, and in the real server the only long-lived holder is Player.Character (i.e. an online character).
    /// Character-select-screen reads and the startup "load all players" pass never keep a reference, so they
    /// don't inflate it, and logout drops the reference (PlayerManager.SwitchPlayerFromOnlineToOffline replaces
    /// the Player with a fresh Biota-only OfflinePlayer). So this table's size tracks concurrently-online
    /// characters, not the total ever created.
    ///
    /// An earlier version of this scenario called GetCharacters and discarded the result each iteration, which
    /// let the table shrink again between calls and showed no trend - it wasn't actually simulating anything
    /// online. This version explicitly keeps every returned Character referenced for the rest of the run, the
    /// same way Player.Character would for N simultaneously online characters, so the table is forced to grow
    /// monotonically to `count` and stay there.
    /// </summary>
    public class CharacterListScalingScenario : IScenario
    {
        public string Name => "character-list-scaling";

        public string Description => "Simulates `count` concurrently-online characters (holds a live reference to each) and times GetCharacter as the count grows. Args: --startCharacterId=0x5F000000 --count=500";

        public void Run(ScenarioArgs args)
        {
            var startCharacterId = args.GetUInt("startCharacterId", 0x5F000000);
            var count = args.GetInt("count", 500);

            Console.WriteLine($"Loading {count} distinct characters one at a time, keeping every one referenced " +
                               $"(simulating {count} characters staying online simultaneously)...");

            // Held for the rest of the run - this is what makes CharacterContexts actually grow to `count`,
            // rather than shrinking back down as soon as each loop iteration's local variable goes out of scope.
            var keepOnline = new List<Character>(count);
            var callMs = new double[count];
            var notFound = 0;

            for (var i = 0; i < count; i++)
            {
                var characterId = startCharacterId + (uint)i;

                var stopwatch = Stopwatch.StartNew();
                var character = DatabaseManager.Shard.BaseDatabase.GetCharacter(characterId);
                stopwatch.Stop();

                callMs[i] = stopwatch.Elapsed.TotalMilliseconds;

                if (character != null)
                    keepOnline.Add(character);
                else
                    notFound++;
            }

            if (notFound > 0)
                Console.WriteLine($"Warning: {notFound} character ids were not found - seed more with seed-characters first, or adjust --startCharacterId.");

            Console.WriteLine($"Characters kept alive (simulated online): {keepOnline.Count}");
            LatencyStats.Report("GetCharacter (all calls)", callMs);

            Console.WriteLine();
            Console.WriteLine("Latency by call index (each call scans a CharacterContexts table this many entries larger than the last):");

            var checkpoints = Math.Min(10, count);
            for (var c = 1; c <= checkpoints; c++)
            {
                var index = (count * c / checkpoints) - 1;
                Console.WriteLine($"  call #{index + 1,-6} {callMs[index],8:N2}ms");
            }

            var firstDecile = callMs[Math.Max(0, count / 10 - 1)];
            var lastDecile = callMs[count - 1];
            var ratio = firstDecile > 0 ? lastDecile / firstDecile : double.NaN;

            Console.WriteLine();
            Console.WriteLine($"10th-percentile call: {firstDecile:N2}ms  |  Final call: {lastDecile:N2}ms  |  ratio: {ratio:N1}x");
        }
    }
}
