using System;
using System.Linq;
using System.Threading;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Seeds synthetic Character rows across many distinct account ids, so `character-list-scaling` has enough
    /// data to show whether ShardDatabase's CharacterContexts linear scan actually grows with cached-character
    /// count. These are shard-only rows - no ace_auth account needs to exist for GetCharacters(accountId) to work,
    /// since ShardDatabase never joins across to the auth database.
    ///
    /// Safe to re-run - it looks up the highest existing account id / character id already in the reserved seed
    /// ranges and continues after them, so repeated runs just add more rather than colliding.
    /// </summary>
    public class SeedCharactersScenario : IScenario
    {
        public string Name => "seed-characters";

        public string Description => "Seeds synthetic characters across many accounts. Args: --count=500 --perAccount=1";

        private const uint SeedAccountRangeMin = 500_000_000;
        private const uint SeedCharacterIdRangeMin = 0x5F000000; // top of the player guid range - unlikely to collide with any real character

        public void Run(ScenarioArgs args)
        {
            var count = args.GetInt("count", 500);
            var perAccount = Math.Max(1, args.GetInt("perAccount", 1));

            uint startAccountId;
            uint startCharacterId;

            using (var context = new ShardDbContext())
            {
                startAccountId = (context.Character.Where(c => c.AccountId >= SeedAccountRangeMin).Select(c => (uint?)c.AccountId).Max() ?? SeedAccountRangeMin - 1) + 1;
                startCharacterId = (context.Character.Where(c => c.Id >= SeedCharacterIdRangeMin).Select(c => (uint?)c.Id).Max() ?? SeedCharacterIdRangeMin - 1) + 1;
            }

            var accountCount = (count + perAccount - 1) / perAccount;

            Console.WriteLine($"Seeding {count} characters across {accountCount} accounts (account ids {startAccountId}+, character ids 0x{startCharacterId:X8}+)...");

            var remaining = new CountdownEvent(count);
            var failures = 0;

            for (var i = 0; i < count; i++)
            {
                var characterId = startCharacterId + (uint)i;

                var character = new Character
                {
                    Id = characterId,
                    AccountId = startAccountId + (uint)(i / perAccount),
                    Name = $"LoadTestChar_{characterId:X8}",
                    IsDeleted = false
                };

                DatabaseManager.Shard.SaveCharacter(character, new ReaderWriterLockSlim(), success =>
                {
                    if (!success)
                        Interlocked.Increment(ref failures);
                    remaining.Signal();
                });
            }

            remaining.Wait();

            Console.WriteLine($"Done. {count - failures}/{count} characters saved.");
            Console.WriteLine($"Try: character-list-scaling --startAccountId={startAccountId} --count={accountCount}");
        }
    }
}
