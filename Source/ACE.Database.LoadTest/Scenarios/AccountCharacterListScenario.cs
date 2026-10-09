using System;
using System.Collections.Generic;
using System.Diagnostics;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Times ShardDatabase.GetCharacters(accountId) - the character-select-screen read - and reports how many SQL
    /// statements it issued.
    ///
    /// This is the multi-character sibling of character-list-scaling, which only ever calls GetCharacter for a
    /// single id and so cannot see per-account scaling at all. GetCharacterList pulls the eight
    /// CharacterPropertiesX collections with Include(...).Load() calls bound to the account-wide query, so the
    /// statement count is the number that matters here: whether it grows with the number of characters on the
    /// account, or stays flat.
    ///
    /// Every character must be uncached for the measurement to mean anything, so run this in a fresh process
    /// (CharacterContexts is a static ConditionalWeakTable) and only sample once per process.
    /// </summary>
    public class AccountCharacterListScenario : IScenario
    {
        public string Name => "account-character-list";

        public string Description => "Times GetCharacters(accountId) for one account and counts the SQL statements it issues. Args: --accountId=500000000 --samples=1";

        public void Run(ScenarioArgs args)
        {
            var accountId = args.GetUInt("accountId", 500_000_000);
            var samples = args.GetInt("samples", 1);

            // Held for the whole run so CharacterContexts entries are not collected between samples - otherwise
            // sample 2+ would randomly look like sample 1.
            var keepAlive = new List<Character>();

            var callMs = new double[samples];

            for (var s = 0; s < samples; s++)
            {
                var before = SqlStatementCounter.Read();

                var stopwatch = Stopwatch.StartNew();
                var characters = DatabaseManager.Shard.BaseDatabase.GetCharacters(accountId, true);
                stopwatch.Stop();

                var after = SqlStatementCounter.Read();

                callMs[s] = stopwatch.Elapsed.TotalMilliseconds;
                keepAlive.AddRange(characters);

                var questRows = 0;
                var friendRows = 0;
                foreach (var character in characters)
                {
                    questRows += character.CharacterPropertiesQuestRegistry?.Count ?? 0;
                    friendRows += character.CharacterPropertiesFriendList?.Count ?? 0;
                }

                Console.WriteLine(
                    $"  sample {s}: {callMs[s]:N2}ms, {characters.Count} characters, " +
                    $"{questRows} quest rows, {friendRows} friend rows, {SqlStatementCounter.Delta(before, after)} SQL statements");

                if (s == 0 && characters.Count == 0)
                    Console.WriteLine($"WARNING: account {accountId} has no characters - seed some first with seed-characters --perAccount=N.");
            }

            LatencyStats.Report("GetCharacters(account) ms", callMs);
        }
    }
}
