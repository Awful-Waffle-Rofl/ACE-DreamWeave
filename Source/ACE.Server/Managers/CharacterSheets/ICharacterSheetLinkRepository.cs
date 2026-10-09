using ACE.Database;

using CharacterSheetLink = ACE.Database.Models.Shard.CharacterSheetLink;

// Plural on purpose: a later CharacterSheet DTO type must not share its namespace's name.
namespace ACE.Server.Managers.CharacterSheets
{
    /// <summary>
    /// The persistence seam for character_sheet_link, so CharacterSheetService is unit-testable with no
    /// MySQL (mirrors IMarketRepository). A FALSE return from a read means the read FAILED; TRUE with a
    /// null row means genuinely absent. Conflating the two would turn a DB outage into "sheet not public".
    /// </summary>
    public interface ICharacterSheetLinkRepository
    {
        /// <summary>False = read failed; true with a null row = no such slug.</summary>
        bool TryGetBySlug(string slug, out CharacterSheetLink row);

        /// <summary>False = read failed; true with a null row = the character has no public sheet.</summary>
        bool TryGetByCharacter(uint characterId, out CharacterSheetLink row);

        /// <summary>Insert or rotate. False = nothing written; <paramref name="slugCollision"/> true = duplicate key, draw a new slug.</summary>
        bool Upsert(CharacterSheetLink row, out bool slugCollision);

        /// <summary>True when the row is gone, including when there was none.</summary>
        bool Delete(uint characterId);
    }

    /// <summary>
    /// Production repository: straight delegation to the link DAO on DatabaseManager.Shard's base
    /// ShardDatabase. Not a biota operation, so deliberately NOT on SerializedShardDatabase's worker thread.
    /// </summary>
    public class ShardCharacterSheetLinkRepository : ICharacterSheetLinkRepository
    {
        private static ShardDatabase Db => DatabaseManager.Shard.BaseDatabase;

        public bool TryGetBySlug(string slug, out CharacterSheetLink row) => Db.TryGetCharacterSheetLinkBySlug(slug, out row);

        public bool TryGetByCharacter(uint characterId, out CharacterSheetLink row) => Db.TryGetCharacterSheetLinkByCharacter(characterId, out row);

        public bool Upsert(CharacterSheetLink row, out bool slugCollision) => Db.UpsertCharacterSheetLink(row, out slugCollision);

        public bool Delete(uint characterId) => Db.DeleteCharacterSheetLink(characterId);
    }
}
