using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Server.Managers.CharacterSheets;

using CharacterSheetLink = ACE.Database.Models.Shard.CharacterSheetLink;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-memory stand-in for character_sheet_link. Models the repository contract the service rests on:
    /// a FALSE read is a failed read while TRUE with a null row is genuinely absent, and a slug already
    /// held by another character is refused as a collision. Every row handed out is a copy, so the
    /// service can never mutate the fake's state by accident.
    /// </summary>
    internal sealed class FakeCharacterSheetLinkRepository : ICharacterSheetLinkRepository
    {
        private readonly object gate = new object();
        private readonly Dictionary<uint, CharacterSheetLink> byCharacter = new Dictionary<uint, CharacterSheetLink>();

        /// <summary>Every read answers FALSE (failed).</summary>
        public volatile bool FailReads;

        /// <summary>Every Upsert and Delete answers FALSE with no collision.</summary>
        public volatile bool FailWrites;

        /// <summary>How many of the next Upsert calls answer FALSE with slugCollision TRUE. int.MaxValue = always.</summary>
        public int ForceCollisions;

        public int SlugReads;
        public int CharacterReads;
        public int UpsertCalls;
        public int DeleteCalls;

        public FakeCharacterSheetLinkRepository With(uint characterId, string slug)
        {
            lock (gate)
                byCharacter[characterId] = new CharacterSheetLink { CharacterId = characterId, Slug = slug, CreatedAt = DateTime.UtcNow };

            return this;
        }

        /// <summary>The stored slug for a character, or null.</summary>
        public string SlugOf(uint characterId)
        {
            lock (gate)
                return byCharacter.TryGetValue(characterId, out var row) ? row.Slug : null;
        }

        public bool TryGetBySlug(string slug, out CharacterSheetLink row)
        {
            Interlocked.Increment(ref SlugReads);
            row = null;

            if (FailReads)
                return false;

            lock (gate)
                row = Copy(byCharacter.Values.FirstOrDefault(r => string.Equals(r.Slug, slug, StringComparison.Ordinal)));

            return true;
        }

        public bool TryGetByCharacter(uint characterId, out CharacterSheetLink row)
        {
            Interlocked.Increment(ref CharacterReads);
            row = null;

            if (FailReads)
                return false;

            lock (gate)
                row = byCharacter.TryGetValue(characterId, out var found) ? Copy(found) : null;

            return true;
        }

        /// <summary>Runs at the start of every Upsert, outside the lock (e.g. a racing writer inserting a row).</summary>
        public Action BeforeUpsert;

        public bool Upsert(CharacterSheetLink row, out bool slugCollision)
        {
            Interlocked.Increment(ref UpsertCalls);
            slugCollision = false;

            BeforeUpsert?.Invoke();

            if (FailWrites)
                return false;

            lock (gate)
            {
                if (ForceCollisions > 0)
                {
                    if (ForceCollisions != int.MaxValue)
                        ForceCollisions--;

                    slugCollision = true;
                    return false;
                }

                if (byCharacter.Values.Any(r => r.CharacterId != row.CharacterId && string.Equals(r.Slug, row.Slug, StringComparison.Ordinal)))
                {
                    slugCollision = true;
                    return false;
                }

                var now = DateTime.UtcNow;
                byCharacter[row.CharacterId] = new CharacterSheetLink { CharacterId = row.CharacterId, Slug = row.Slug, CreatedAt = now };
                row.CreatedAt = now;
                return true;
            }
        }

        public bool Delete(uint characterId)
        {
            Interlocked.Increment(ref DeleteCalls);

            if (FailWrites)
                return false;

            lock (gate)
                byCharacter.Remove(characterId);

            return true;
        }

        private static CharacterSheetLink Copy(CharacterSheetLink row) =>
            row == null ? null : new CharacterSheetLink { CharacterId = row.CharacterId, Slug = row.Slug, CreatedAt = row.CreatedAt };
    }
}
