using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Pvp
{
    /// <summary>Where the in-memory ladders stand. Only <see cref="Available"/> ever rates a match.</summary>
    public enum PvpRatingStoreState
    {
        /// <summary>The boot read is queued and has not come back yet. Matchmaking waits.</summary>
        Loading,

        /// <summary>The boot read succeeded (possibly empty: the table may not exist yet).</summary>
        Available,

        /// <summary>The boot read FAILED. Matches still run, unrated, and no rating row is ever written.</summary>
        Unavailable
    }

    /// <summary>One character's standing on one ladder, as read now: <see cref="Rating"/> already has decay applied.</summary>
    public sealed record PvpRatingView(
        uint CharacterId,
        string Ladder,
        string CharacterName,
        int Rating,
        int StoredRating,
        int Games,
        int Wins,
        int Losses,
        int Draws,
        int Peak,
        DateTime? LastMatchAtUtc,
        bool HasRecord);

    /// <summary>
    /// The in-memory ladders (Docs/Pvp/DESIGN.md "Rating" and "Storage"), keyed by (character, ladder). WORLD
    /// THREAD ONLY: the boot read is queued on the shard database worker and its result is published here by the
    /// coordinator's tick, never by the database callback.
    ///
    /// Decay is applied on every read and never stored; the decayed value reaches the table only through that
    /// player's next match upsert, which carries the absolute post-match rating.
    ///
    /// RATINGS UNAVAILABLE. When the boot read fails (GetAllPvpRatings returned null), the store is
    /// <see cref="PvpRatingStoreState.Unavailable"/> for the life of the process: every match runs UNRATED and no
    /// rating row is written. Rating from an empty store would start everyone at the initial rating, and the next
    /// match's absolute upsert would overwrite that player's real stored rating with it (the DAO's warning). Running
    /// unrated keeps the arena playable and writes nothing that could clobber a real rating; refusing rated joins
    /// instead would close every mode, because every v1 mode is rated.
    /// </summary>
    public sealed class PvpRatingStore
    {
        private readonly Dictionary<(uint CharacterId, string Ladder), PvpRatingRecord> rows = new();

        public PvpRatingStoreState State { get; private set; } = PvpRatingStoreState.Loading;

        public bool IsAvailable => State == PvpRatingStoreState.Available;

        public int Count => rows.Count;

        /// <summary>Publishes the boot read. A null list marks the store unavailable (see the class comment).</summary>
        public void Publish(List<PvpRatingRecord> loaded)
        {
            rows.Clear();

            if (loaded == null)
            {
                State = PvpRatingStoreState.Unavailable;
                return;
            }

            foreach (var row in loaded)
            {
                if (row == null || row.Ladder == null)
                    continue;

                rows[(row.CharacterId, row.Ladder)] = Copy(row);
            }

            State = PvpRatingStoreState.Available;
        }

        /// <summary>The decayed standing of one character on one ladder. A character with no row reads as the initial rating.</summary>
        public PvpRatingView Read(uint characterId, string ladder, DateTime utcNow, PvpArenaDials dials)
        {
            if (rows.TryGetValue((characterId, ladder), out var row))
            {
                var decayed = RatingDecay.Apply(row.Rating, row.LastMatchAt, utcNow, dials.RatingDecayGraceDays, dials.RatingDecayPointsPerWeek, dials.RatingDecayFloor);

                return new PvpRatingView(characterId, ladder, row.CharacterName, decayed, row.Rating, row.Games, row.Wins, row.Losses, row.Draws, row.Peak, row.LastMatchAt, HasRecord: true);
            }

            return new PvpRatingView(characterId, ladder, null, dials.RatingInitial, dials.RatingInitial, 0, 0, 0, 0, dials.RatingInitial, null, HasRecord: false);
        }

        /// <summary>
        /// Applies one post-match row (absolute values) immediately, so the ladder reflects the result while the save
        /// is still queued. Ignored unless the store is available: an unavailable store never rates anything.
        /// </summary>
        public void Apply(PvpRatingRecord row)
        {
            if (!IsAvailable || row == null || row.Ladder == null)
                return;

            rows[(row.CharacterId, row.Ladder)] = Copy(row);
        }

        /// <summary>The top <paramref name="count"/> of a ladder by decayed rating (then wins, then name), computed from memory.</summary>
        public IReadOnlyList<PvpRatingView> Top(string ladder, int count, DateTime utcNow, PvpArenaDials dials)
        {
            if (count <= 0 || ladder == null)
                return Array.Empty<PvpRatingView>();

            return rows
                .Where(kvp => string.Equals(kvp.Key.Ladder, ladder, StringComparison.OrdinalIgnoreCase) && kvp.Value.Games > 0)
                .Select(kvp => Read(kvp.Key.CharacterId, kvp.Key.Ladder, utcNow, dials))
                .OrderByDescending(v => v.Rating)
                .ThenByDescending(v => v.Wins)
                .ThenBy(v => v.CharacterName, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
        }

        private static PvpRatingRecord Copy(PvpRatingRecord r) => new PvpRatingRecord
        {
            CharacterId = r.CharacterId,
            Ladder = r.Ladder,
            CharacterName = r.CharacterName,
            Rating = r.Rating,
            Games = r.Games,
            Wins = r.Wins,
            Losses = r.Losses,
            Draws = r.Draws,
            Peak = r.Peak,
            LastMatchAt = r.LastMatchAt,
        };
    }
}
