using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Database.Models.Shard;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// The blocking-read-with-timeout mechanism Player.TryGetCharacterFacetRows depends on, extracted
    /// as a pure, Player-independent helper so it is unit-testable. No test in ACE.Server.Tests
    /// constructs a live Player - Player's constructors call DatabaseManager.Authentication.GetAccountById
    /// unconditionally, a real MySQL round-trip unavailable in this test project (independently
    /// documented in MuleCommerceTests.cs, PersonalVendorTests.cs and StageTestCommandsTests.cs) - so
    /// anything here that needs test coverage has to be extractable down to this level, matching the
    /// same reasoning Player.LookupSkillCreditCost's own doc comment gives for why FacetPools stays
    /// DatManager-free.
    ///
    /// TryFetch signals "the read did not complete" (a timeout, or the fetcher never answering its
    /// callback) as a FALSE return with rows == null, DISTINCT from a genuine empty answer (true,
    /// rows.Count == 0). Collapsing the two was Task 11 fix round 1's Finding 1: TrySwitchFacet used
    /// to degrade a timeout to an empty list, which was then read as "slot never visited" and fed
    /// BuildFreshFacetSkills() onto a live character - silently destroying a real stored build
    /// whenever the shard read queue happened to be briefly backed up (an acknowledged real occurrence -
    /// see AccountVaultStore.SaveAndWait's own doc comment). Every LOAD-BEARING caller (a switch, a
    /// rename that looks up an existing row) must treat a false return as its own refusal reason, never
    /// as "no rows". A caller whose answer is merely ADVISORY (a confirmation-prompt peek, a read-only
    /// listing display) may still collapse false to an empty list, since the worst outcome there is an
    /// extra prompt or a stale display, never a mutation.
    /// </summary>
    public static class FacetRowFetch
    {
        /// <summary>
        /// Calls <paramref name="fetcher"/>(<paramref name="characterId"/>, callback) and blocks the
        /// calling thread until the callback fires or <paramref name="timeoutMs"/> elapses. Returns false
        /// (with rows == null) when the callback never fired in time, or when <paramref name="fetcher"/>
        /// itself is null. On success, rows is never null (an empty list stands in for a null callback
        /// argument), matching character_facet's own "absence is meaningful, never backfilled" contract.
        /// </summary>
        public static bool TryFetch(
            uint characterId,
            int timeoutMs,
            Action<uint, Action<List<CharacterFacet>>> fetcher,
            out List<CharacterFacet> rows)
        {
            if (fetcher == null)
            {
                rows = null;
                return false;
            }

            var gate = new object();
            List<CharacterFacet> result = null;
            var fired = false;

            fetcher(characterId, r =>
            {
                lock (gate)
                {
                    result = r;
                    fired = true;
                    Monitor.Pulse(gate);
                }
            });

            lock (gate)
            {
                if (!fired)
                    Monitor.Wait(gate, timeoutMs);

                if (!fired)
                {
                    rows = null;
                    return false;
                }

                rows = result ?? new List<CharacterFacet>();
                return true;
            }
        }
    }
}
