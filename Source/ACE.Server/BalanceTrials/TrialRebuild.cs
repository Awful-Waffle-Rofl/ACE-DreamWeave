using System;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The hard rebuild of a rep from its master (Docs/BalanceTrials/DESIGN.md 6, rebuild step 2): purge the rep slot, then
    /// DoCopyChar the master onto it. The tester roster's rules apply unchanged: an online slot is refused, the online
    /// check is made again immediately before the purge, and nothing is copied onto a slot whose purge failed
    /// (TesterRosterCommands.HandleRefreshTestRoster).
    ///
    /// MUST RUN ON THE WORLD THREAD, like the roster refresh: PurgeSlot calls AllegianceManager/HouseManager.DoHandlePlayerDelete
    /// directly (see its own remarks). The @trial rebuild command (Task 8) is RequiresWorld.
    ///
    /// The copy itself is asynchronous (DoCopyChar is callback-driven): a true return means the purge committed and the copy
    /// was queued, not that it has landed. The rep's block (TrialRepSetup) is reset only when DoCopyChar's onDone reports
    /// success; a failed copy, or one that never reports, leaves the block and any owed rebuild exactly as they were.
    /// </summary>
    public static class TrialRebuild
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string StampVersion = "v1";

        /// <summary>The literal fourth stamp field of a promoted master (lower case, exact).</summary>
        public const string PromotedToken = "promoted";

        /// <summary>The exact UTC time format of a promoted stamp's fifth field.</summary>
        public const string PromotedTimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

        /// <summary>The world lookups a rebuild makes, as delegates so tests can fake each one.</summary>
        internal sealed class Lookups
        {
            public Func<uint, bool> IsOnline;

            /// <summary>The master's PropertyString.TrialArchetypeStamp, or null when the master is missing or unstamped. Live, this reads the shard DB
            /// directly: masters are parked characters (IsDeleted), which PlayerManager never loads, so FindByGuid would always miss them.
            /// This is a synchronous shard read on the world thread (the command handler), unlike DoCopyChar, whose read runs inside a GetCharacter
            /// callback on the shard worker. Acceptable because the parked master is never saved and the command is admin-only and one-shot.
            /// doNotAddToCache is true so the caching override disposes its context (it leaks one on a null biota otherwise).</summary>
            public Func<uint, string> MasterStamp;

            public Func<string, uint?> AccountId;

            public Func<uint, bool> Purge;

            /// <summary>(admin, master guid, rep slot, account id, onDone): queues the copy; onDone(true) when it landed, onDone(false) on a failure. onDone may run on any thread, or never (a swallowed shard read).</summary>
            public Action<Session, uint, TrialRoster.RepSlot, uint, Action<bool>> Copy;

            /// <summary>The copy gate (TrialCopyGate.Shared live). Required: rebuild takes the rep exclusive and the master shared.</summary>
            public TrialCopyGate Gate;
        }

        internal static Lookups Live() => new Lookups
        {
            IsOnline = guid => PlayerManager.GetOnlinePlayer(guid) != null,
            MasterStamp = guid => DatabaseManager.Shard.BaseDatabase.GetBiota(guid, true)?.BiotaPropertiesString?.FirstOrDefault(p => p.Type == (ushort)PropertyString.TrialArchetypeStamp)?.Value,
            AccountId = name => DatabaseManager.Authentication.GetAccountByName(name)?.AccountId,
            Purge = TesterRosterCommands.PurgeSlot,
            Copy = (admin, master, rep, accountId, onDone) => AdminCommands.DoCopyChar(admin, $"0x{master:X8}", master, false, rep.Name, accountId, rep.Guid, onDone),
            Gate = TrialCopyGate.Shared,
        };

        /// <summary>Rebuilds rep <paramref name="repIndex"/> (1..8, TrialRoster.RepSlots[repIndex - 1]) from the master of <paramref name="a"/>.</summary>
        public static bool TryRebuild(Session admin, TrialArchetype a, string archetypeSha, int repIndex, out string refusal)
            => TryRebuild(admin, a, archetypeSha, repIndex, Live(), out refusal, out _);

        /// <summary>As above, plus <paramref name="note"/>: non-null when the master is promoted (the rebuild goes ahead and copies the tuned build, even when its archetype sha differs from the file's; the reply should say so).</summary>
        public static bool TryRebuild(Session admin, TrialArchetype a, string archetypeSha, int repIndex, out string refusal, out string note)
            => TryRebuild(admin, a, archetypeSha, repIndex, Live(), out refusal, out note);

        internal static bool TryRebuild(Session admin, TrialArchetype a, string archetypeSha, int repIndex, Lookups lookups, out string refusal)
            => TryRebuild(admin, a, archetypeSha, repIndex, lookups, out refusal, out _);

        internal static bool TryRebuild(Session admin, TrialArchetype a, string archetypeSha, int repIndex, Lookups lookups, out string refusal, out string note)
        {
            refusal = null;
            note = null;

            if (a == null || string.IsNullOrEmpty(archetypeSha))
                return Refuse("no archetype", out refusal);

            if (repIndex < 1 || repIndex > TrialRoster.RepSlots.Count)
                return Refuse($"rep index {repIndex} must be 1..{TrialRoster.RepSlots.Count}", out refusal);

            var rep = TrialRoster.RepSlots[repIndex - 1];
            var master = TrialRoster.MasterSlot(a.Slot);

            // 1. The rep slot must be offline: the hard re-copy needs the rep logged out.
            if (lookups.IsOnline(rep.Guid))
                return Refuse($"{rep.Name} (0x{rep.Guid:X8}) is online; log it out first, then rebuild", out refusal);

            // 1b. A master a promote or unpromote left mid-purge or mid-copy is in an unknown state: nothing is copied from it.
            // Ahead of the stamp check, because that purge deleted the stamp: "no stamp" would point at the wrong recovery.
            var gate = lookups.Gate ?? throw new ArgumentException("the copy gate is required", nameof(lookups));

            if (gate.IsBroken(master, out var brokenWhy))
                return Refuse($"master 0x{master:X8} is broken ({brokenWhy}); {TrialPromote.RecoveryHint}", out refusal);

            // 2. The master must carry a stamp, and the stamp must name this archetype file.
            var stamp = lookups.MasterStamp(master);
            if (string.IsNullOrEmpty(stamp))
                return Refuse($"master 0x{master:X8} ({TrialRoster.MasterName(a.Slot)}) has no stamp; run seed-trial-archetypes", out refusal);

            if (!TryParseStamp(stamp, out var stampedArchetype, out _, out var promotedAt))
                return Refuse($"master 0x{master:X8} has a stamp of an unknown format ('{stamp}'); re-run seed-trial-archetypes", out refusal);

            var shaDiffers = !string.Equals(stampedArchetype, archetypeSha, StringComparison.OrdinalIgnoreCase);

            // A promoted master was tuned by hand and is deliberately no longer the archetype file's build: a sha mismatch is expected, not stale.
            if (shaDiffers && promotedAt == null)
                return Refuse($"master is stale; re-run seed-trial-archetypes (master 0x{master:X8} was built from archetype {stampedArchetype}, the file '{a.Id}' is {archetypeSha})", out refusal);

            if (promotedAt != null)
                note = $"note: master 0x{master:X8} is promoted (hand-tuned, {promotedAt.Value.ToString(PromotedTimeFormat, CultureInfo.InvariantCulture)}), so the rep copies the tuned build" + (shaDiffers ? $", which differs from the archetype file '{a.Id}' ({archetypeSha})" : "");

            // 3. The rep's own account.
            var accountId = lookups.AccountId(rep.AccountName);
            if (accountId == null)
                return Refuse($"account \"{rep.AccountName}\" does not exist; create it with @accountcreate", out refusal);

            // 3c. The copy gate (TrialCopyGate): the rep exclusive, the master shared. Released on every path below, including a
            // throw before lookups.Copy hands the hold to its onDone.
            if (!gate.TryTake(rep.Guid, master, out var token, out var busy))
                return Refuse($"a copy or purge is already in flight on 0x{busy:X8}; wait for its 'Successfully copied' line (at most {TrialCopyGate.HoldSeconds / 60:0} minutes) and retry", out refusal);

            var purgeStarted = false;
            var handedOff = false;

            try
            {
                // 4. Re-check immediately before the purge (the roster rule): purging a now-live session deletes a connected character.
                if (lookups.IsOnline(rep.Guid))
                {
                    gate.Release(token);
                    return Refuse($"{rep.Name} (0x{rep.Guid:X8}) came online; log it out first, then rebuild", out refusal);
                }

                purgeStarted = true;

                if (!lookups.Purge(rep.Guid))
                {
                    gate.Release(token);

                    // The slot may be partly populated: no rep may run on it, in any session, until a rebuild completes.
                    TrialRepSetup.MarkRebuildRequired(rep.Guid, "the last rebuild's purge failed", purgeFailed: true);
                    return Refuse($"the purge of {rep.Name} (0x{rep.Guid:X8}) failed and nothing was copied; the slot may be partly populated, check the server log", out refusal);
                }

                // 5. The copy. Only a copy that landed starts a new block (and clears NeedsRebuild); a failed copy leaves both.
                lookups.Copy(admin, master, rep, accountId.Value, ok =>
                {
                    gate.Release(token);

                    if (ok)
                    {
                        TrialRepSetup.ResetBlock(rep.Guid);
                        log.Info($"[TRIAL] rebuild done: master 0x{master:X8} ({a.Id}) -> {rep.Name} 0x{rep.Guid:X8}");
                    }
                    else
                        log.Warn($"[TRIAL] rebuild copy FAILED: master 0x{master:X8} ({a.Id}) -> {rep.Name} 0x{rep.Guid:X8}; the rep block is unchanged, rebuild again");
                });

                handedOff = true;
            }
            catch (Exception ex) when (!handedOff)
            {
                log.Error($"[TRIAL] rebuild of {rep.Name} 0x{rep.Guid:X8} from master 0x{master:X8} threw{(purgeStarted ? " after its purge started" : "")}; the gate is released", ex);

                // Once the purge has started the slot may be partly populated, exactly as after a purge that returned false.
                if (purgeStarted)
                    TrialRepSetup.MarkRebuildRequired(rep.Guid, "the last rebuild threw after its purge started", purgeFailed: true);

                gate.Release(token);
                return Refuse($"the rebuild threw ({ex.Message}){(purgeStarted ? "; the slot may be partly populated" : "")}; check the server log, then rebuild again", out refusal);
            }

            log.Info($"[TRIAL] rebuild queued: master 0x{master:X8} ({a.Id}) -> {rep.Name} 0x{rep.Guid:X8} on account {rep.AccountName}");

            return true;
        }

        /// <summary>Parses <c>v1|&lt;archetypeSha256&gt;|&lt;calibrationSha256&gt;</c> (property-registry.tsv, PropertyString 9034). Anything else is refused.
        /// A promoted stamp (5 fields) parses too; use the overload with promotedAt to tell them apart.</summary>
        public static bool TryParseStamp(string stamp, out string archetypeSha, out string calibrationSha)
            => TryParseStamp(stamp, out archetypeSha, out calibrationSha, out _);

        /// <summary>
        /// Parses a master stamp: 3 fields <c>v1|arch|cal</c> (promotedAt null), or 5 fields <c>v1|arch|cal|promoted|yyyy-MM-ddTHH:mm:ssZ</c>
        /// (promotedAt the UTC time). The token is the exact lower-case literal and the time must round-trip the exact format; 4 or 6 fields,
        /// another token or a malformed time are refused. This is the one parser: trialseed, prodcharseedverify and rebuild all use it.
        /// </summary>
        public static bool TryParseStamp(string stamp, out string archetypeSha, out string calibrationSha, out DateTime? promotedAt)
        {
            archetypeSha = null;
            calibrationSha = null;
            promotedAt = null;

            if (string.IsNullOrEmpty(stamp))
                return false;

            var parts = stamp.Split('|');

            if ((parts.Length != 3 && parts.Length != 5) || parts[0] != StampVersion || parts[1].Length == 0 || parts[2].Length == 0)
                return false;

            DateTime? at = null;

            if (parts.Length == 5)
            {
                if (parts[3] != PromotedToken
                    || !DateTime.TryParseExact(parts[4], PromotedTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    return false;

                at = parsed;
            }

            archetypeSha = parts[1];
            calibrationSha = parts[2];
            promotedAt = at;
            return true;
        }

        /// <summary>Builds a promoted stamp: <c>v1|arch|cal|promoted|yyyy-MM-ddTHH:mm:ssZ</c> with the time in UTC.</summary>
        public static string PromotedStamp(string archetypeSha, string calibrationSha, DateTime utc)
            => string.Join("|", StampVersion, archetypeSha, calibrationSha, PromotedToken, utc.ToUniversalTime().ToString(PromotedTimeFormat, CultureInfo.InvariantCulture));

        private static bool Refuse(string reason, out string refusal)
        {
            refusal = reason;
            return false;
        }
    }
}
