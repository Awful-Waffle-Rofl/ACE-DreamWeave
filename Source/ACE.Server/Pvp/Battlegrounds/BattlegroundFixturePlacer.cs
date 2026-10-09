using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The decision half of the fixture placement (Docs/Pvp/BATTLEGROUNDS.md "Pen seals", "Zone markers"), with the world
    /// calls behind delegates so it is driven from unit tests; <see cref="LivePvpMatchSpaces"/> supplies the live ones. It
    /// runs on the instance landblock's own action queue and always finishes the job.
    ///
    /// <para/>
    /// Strict (the pen seals, the default): Complete(count) when every piece entered the world, Fail on the first refusal
    /// or throw. Pieces already placed are left in place on a failure; the coordinator cancels and releases the space,
    /// and they go with the instance.
    ///
    /// <para/>
    /// Best effort (the zone markers): a refusal or a throw for one piece is counted and skipped, and the job Completes
    /// with the number placed and the FIRST refusal as its detail (null when none). Only an instance that is no longer
    /// live still Fails, since nothing can be placed then.
    /// </summary>
    internal static class BattlegroundFixturePlacer
    {
        /// <param name="stillLive">True while the instance the job was queued for is still the live one.</param>
        /// <param name="place">Creates and enters one piece; null on success, else the reason it was refused.</param>
        /// <param name="bestEffort">Skip a refused or throwing piece instead of stopping (see the class remarks).</param>
        public static void Run(IReadOnlyList<BattlegroundSealPiece> pieces, Func<bool> stillLive, Func<BattlegroundSealPiece, string> place, BattlegroundFixtureJob job, bool bestEffort = false)
        {
            var placed = 0;
            string firstRefusal = null;

            try
            {
                if (!stillLive())
                {
                    job.Fail("the match instance is no longer live", placed);
                    return;
                }

                foreach (var piece in pieces ?? Array.Empty<BattlegroundSealPiece>())
                {
                    string refusal;

                    if (piece == null)
                        refusal = "a seal piece is null";
                    else if (!bestEffort)
                        refusal = place(piece);
                    else
                    {
                        try
                        {
                            refusal = place(piece);
                        }
                        catch (Exception ex)
                        {
                            refusal = $"placement threw: {ex.GetType().Name}: {ex.Message}";
                        }
                    }

                    if (refusal != null)
                    {
                        if (!bestEffort)
                        {
                            job.Fail(refusal, placed);
                            return;
                        }

                        firstRefusal ??= refusal;
                        continue;
                    }

                    placed++;
                }

                job.Complete(placed, firstRefusal);
            }
            catch (Exception ex)
            {
                job.Fail($"placement threw: {ex.GetType().Name}: {ex.Message}", placed);
            }
        }
    }
}
