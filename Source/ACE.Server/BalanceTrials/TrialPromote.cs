using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;

using EntityBiota = ACE.Entity.Models.Biota;
using ShardCharacter = ACE.Database.Models.Shard.Character;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// @trial promote: copies an offline, hand-tuned rep over its archetype's master slot, parked the way the seed parks a
    /// master (IsDeleted, AccountId 0, "Trialmaster &lt;slot&gt;") and stamped promoted, so the next @trial rebuild copies the
    /// tuned build and the live reseed leaves the slot alone. @trial unpromote purges a promoted master so the next reseed
    /// fills the slot again.
    ///
    /// MUST RUN ON THE WORLD THREAD, like rebuild: PurgeSlot calls AllegianceManager/HouseManager.DoHandlePlayerDelete
    /// directly. The @trial command is RequiresWorld.
    ///
    /// Item guids are the dynamic guids DoCopyChar assigns (owner ruling A1: never re-banded into the trial item band while
    /// the server runs). Every strip happens in the BeforeSave delegate (<see cref="Normalize"/>), never in DoCopyChar.
    ///
    /// ORDERING AGAINST THE REP'S LOGOUT SAVE. The refusals read the rep synchronously (BaseDatabase, on this thread) and
    /// may see its state from before its final logout save lands; they only test things that save does not change (the
    /// character row and biota exist, the lineage stamp, a non-zero possession count) and the advisory consumable count. The
    /// COPY reads the rep through the serialized shard queue (DoCopyChar's GetCharacter, then reads inside its callback),
    /// and the logout save is already on that queue before the rep stops counting as online: FinalizeLogout calls
    /// SavePlayerToDatabase before PlayerManager.SwitchPlayerFromOnlineToOffline (Player.cs), the forced-logoff replay in
    /// PlayerManager.Tick saves before it switches too, and the queue runs each task to completion in FIFO order
    /// (SerializedShardDatabase.DoWork/RunStandalone). So the copy always reads the saved rep.
    /// </summary>
    public static class TrialPromote
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>What the refusals need to know about the rep. <see cref="Stacks"/> is every possession's stack size summed by wcid.</summary>
        internal readonly record struct RepFacts(bool HasCharacter, bool HasBiota, int Possessions, IReadOnlyDictionary<uint, long> Stacks);

        /// <summary>What <see cref="Normalize"/> changed, for the log.</summary>
        internal readonly record struct NormalizeResult(int TimedRemoved, int VitaeRemoved, int CastersCleared);

        /// <summary>The world lookups promote and unpromote make, as delegates so tests can fake each one (the shape of TrialRebuild.Lookups). Not sealed: TrialAdopt.Lookups adds the adopt-only ones.</summary>
        internal class Lookups
        {
            public Func<uint, bool> IsOnline;

            /// <summary>True when the guid is the player of a registered TrialSession, in any phase.</summary>
            public Func<uint, bool> HasTrialSession;

            public Func<int, bool> AnySessionForSlot;

            /// <summary>True when the rep's last rebuild failed at its purge (TrialRepSetup.PurgeFailed).</summary>
            public Func<uint, bool> RepPurgeFailed;

            /// <summary>The guid's PropertyString.TrialArchetypeStamp, or null. Live: a direct shard read, GetBiota(guid, true), as rebuild reads the master.</summary>
            public Func<uint, string> Stamp;

            public Func<uint, RepFacts> Rep;

            public Func<uint, bool> Purge;

            /// <summary>(admin, rep, master guid, master name, options, onDone): queues the copy; onDone may run on any thread, or never.</summary>
            public Action<Session, TrialRoster.RepSlot, uint, string, CopyCharOptions, Action<bool>> Copy;

            public Func<DateTime> UtcNow;

            /// <summary>Required. Promote takes the master exclusive and the rep shared; unpromote the master exclusive.</summary>
            public TrialCopyGate Gate;

            /// <summary>Unpromote slot:&lt;n&gt; only: true when a character row exists for the guid (live: the disposed-context stub read).</summary>
            public Func<uint, bool> HasCharacterRow;

            /// <summary>Unpromote slot:&lt;n&gt; only: the slots the seeded archetype files name, or null when unknown.</summary>
            public Func<IReadOnlyCollection<int>> SeededSlots;
        }

        internal static Lookups Live() => new Lookups
        {
            IsOnline = guid => PlayerManager.GetOnlinePlayer(guid) != null,
            HasTrialSession = guid => TrialManager.TryGetByPlayer(guid, out _),
            AnySessionForSlot = TrialManager.AnySessionForSlot,
            RepPurgeFailed = TrialRepSetup.PurgeFailed,
            Stamp = guid => DatabaseManager.Shard.BaseDatabase.GetBiota(guid, true)?.BiotaPropertiesString?.FirstOrDefault(p => p.Type == (ushort)PropertyString.TrialArchetypeStamp)?.Value,
            Rep = LiveRepFacts,
            Purge = TesterRosterCommands.PurgeSlot,
            Copy = (admin, rep, master, masterName, options, onDone) => AdminCommands.DoCopyChar(admin, rep.Name, rep.Guid, false, masterName, 0, master, onDone, options),
            UtcNow = () => DateTime.UtcNow,
            Gate = TrialCopyGate.Shared,
            HasCharacterRow = guid => DatabaseManager.Shard.BaseDatabase.GetCharacterStubByGuid(guid) != null,
        };

        /// <summary>
        /// The rep's character row (a disposed-context stub read, so it never enters ShardDatabase's CharacterContexts), its
        /// biota, and its possessions, all read directly on this thread.
        /// </summary>
        private static RepFacts LiveRepFacts(uint guid)
        {
            var db = DatabaseManager.Shard.BaseDatabase;

            var hasCharacter = db.GetCharacterStubByGuid(guid) != null;
            var hasBiota = db.GetBiota(guid, true) != null;

            var possessions = db.GetPossessedBiotasInParallel(guid);
            var items = possessions.Inventory.Concat(possessions.WieldedItems).ToList();

            var stacks = StacksByWcid(items.Select(b => (b.WeenieClassId,
                (long?)b.BiotaPropertiesInt.FirstOrDefault(p => p.Type == (ushort)PropertyInt.StackSize)?.Value)));

            return new RepFacts(hasCharacter, hasBiota, items.Count, stacks);
        }

        // ================================================================ promote

        /// <summary>Promotes rep <paramref name="repIndex"/> (1..12) over the master of <paramref name="a"/>, live.</summary>
        public static bool TryPromote(Session admin, TrialArchetype a, int repIndex, out string refusal, out IReadOnlyList<string> reply)
            => TryPromote(admin, admin?.Player?.Guid.Full, a, repIndex, Live(), out refusal, out reply);

        /// <summary>
        /// The refusals, in order: (the kill switch, the data directory and an unknown archetype are the command's, see
        /// TrialCommands.PromoteReply) the rep index; the caller is the rep (or not a character); the rep is online; the rep has
        /// a registered TrialSession; any session runs on this archetype's slot; the gate is busy for the master or the rep;
        /// the rep's last purge failed; the master stamp is missing or unparseable; the rep's lineage differs from the master's;
        /// the rep has no character row, no biota or no possessions; the rep came online. Then PurgeSlot(master), then the copy.
        /// Nothing is purged or copied on any refusal.
        /// </summary>
        internal static bool TryPromote(Session admin, uint? callerGuid, TrialArchetype a, int repIndex, Lookups lookups, out string refusal, out IReadOnlyList<string> reply)
        {
            refusal = null;
            reply = null;

            var gate = lookups?.Gate ?? throw new ArgumentException("the copy gate is required", nameof(lookups));

            if (a == null)
                return Refuse("no archetype", out refusal);

            // 4. The rep index.
            if (repIndex < 1 || repIndex > TrialRoster.RepSlots.Count)
                return Refuse($"rep index {repIndex} must be 1..{TrialRoster.RepSlots.Count}", out refusal);

            var rep = TrialRoster.RepSlots[repIndex - 1];
            var master = TrialRoster.MasterSlot(a.Slot);
            var masterName = TrialRoster.MasterName(a.Slot);

            // 5. The caller: an in-world character (the copy is made under its session), and never the rep itself.
            if (callerGuid == null)
                return Refuse("run @trial promote from an in-world character that is not the rep", out refusal);

            if (callerGuid.Value == rep.Guid)
                return Refuse($"you are {rep.Name}; run @trial promote from another character, with the rep logged out", out refusal);

            // 6. The rep must be logged out.
            if (lookups.IsOnline(rep.Guid))
                return Refuse($"{rep.Name} (0x{rep.Guid:X8}) is online; log it out first, then promote", out refusal);

            // 7. The rep must not belong to a trial session, in any phase.
            if (lookups.HasTrialSession(rep.Guid))
                return Refuse($"{rep.Name} (0x{rep.Guid:X8}) still has a trial session; end it (@trial abort, or log the rep out and wait for the sweep) first", out refusal);

            // 8. No session may be reading this slot's master.
            if (lookups.AnySessionForSlot(a.Slot))
                return Refuse($"a trial session is running archetype slot {a.Slot}; end it before promoting over master 0x{master:X8}", out refusal);

            // 9. The gate: the master exclusive (it is purged and written), the rep shared (it is only read). Released on every path below.
            if (!gate.TryTake(master, rep.Guid, out var token, out var busy))
                return Refuse($"a copy or purge is already in flight on 0x{busy:X8}; wait for its 'Successfully copied' line (at most {TrialCopyGate.HoldSeconds / 60:0} minutes) and retry", out refusal);

            // From the take until lookups.Copy hands the hold to onDone, a throw must not leave the gate held (it would refuse every
            // copy on this master and rep for HoldSeconds) and, once the purge has started, must leave the master marked broken
            // (the shared tail, TryPurgeThenCopy, owns that half).
            TrialPromote.RepFacts facts;
            bool wasBroken;
            string brokenBefore;
            string lineageNote = null;
            var handedOff = false;
            string masterArchetype = null, masterCalibration = null;

            try
            {
                // 10. A rep whose last purge failed may be partly populated: drift alone does not refuse, a failed purge does.
                if (lookups.RepPurgeFailed(rep.Guid))
                    return Release(gate, token, $"{rep.Name}'s last rebuild failed at its purge or copy, so it may be partly populated; rebuild it before promoting it", out refusal);

                // 11. The master's lineage, which supplies both shas of the promoted stamp: from its stamp, or, when an earlier
                // promote or unpromote purged it (the purge deletes the biota and with it the stamp), from that run's broken
                // record. The record covers only a MISSING stamp (the post-purge state): a stamp that is present but unreadable
                // still refuses as an unknown format. With neither, only a reseed can rebuild the master.
                var masterStamp = lookups.Stamp(master);
                if (!TrialRebuild.TryParseStamp(masterStamp, out masterArchetype, out masterCalibration, out _))
                {
                    if (string.IsNullOrEmpty(masterStamp) && gate.TryGetBrokenLineage(master, out masterArchetype, out masterCalibration))
                        lineageNote = $"note: master 0x{master:X8} has no readable stamp (an earlier promote or unpromote purged it); its lineage {masterArchetype} comes from that run's record";
                    else
                        return Release(gate, token, string.IsNullOrEmpty(masterStamp)
                            ? $"master 0x{master:X8} ({masterName}) has no stamp and no recorded lineage; seed it first (seed-trial-archetypes). After a server restart that is also how a master left empty by a failed promote is recovered"
                            : $"master 0x{master:X8} has a stamp of an unknown format ('{masterStamp}'); re-run seed-trial-archetypes", out refusal);
                }

                // 12. Lineage: the rep must descend from this master's archetype build (stamp field 1).
                var repStamp = lookups.Stamp(rep.Guid);
                if (!TrialRebuild.TryParseStamp(repStamp, out var repArchetype, out _, out _))
                    return Release(gate, token, $"{rep.Name} carries no archetype stamp, so its lineage is unknown; rebuild it from {a.Id} first", out refusal);

                if (!string.Equals(repArchetype, masterArchetype, StringComparison.OrdinalIgnoreCase))
                    return Release(gate, token, $"{rep.Name} was built from archetype {repArchetype}, but master 0x{master:X8} is {masterArchetype}; rebuild the rep from {a.Id} first", out refusal);

                // 13. Something to copy.
                facts = lookups.Rep(rep.Guid);
                if (!facts.HasCharacter)
                    return Release(gate, token, $"{rep.Name} (0x{rep.Guid:X8}) has no character row", out refusal);

                if (!facts.HasBiota)
                    return Release(gate, token, $"{rep.Name} (0x{rep.Guid:X8}) has no biota", out refusal);

                if (facts.Possessions == 0)
                    return Release(gate, token, $"{rep.Name} (0x{rep.Guid:X8}) has no possessions; a promoted master needs the rep's gear", out refusal);

                // 14. Re-check immediately before the purge.
                if (lookups.IsOnline(rep.Guid))
                    return Release(gate, token, $"{rep.Name} (0x{rep.Guid:X8}) came online; log it out first, then promote", out refusal);

                wasBroken = gate.IsBroken(master, out brokenBefore);
                var now = lookups.UtcNow().ToUniversalTime();

                // The shared tail: mark broken with the lineage, purge the master, copy the rep onto it (stamped from that lineage).
                if (!TryPurgeThenCopy(lookups, token, master, "promote", $"promote of rep {repIndex}", masterArchetype, masterCalibration, now,
                        (options, onDone) => lookups.Copy(admin, rep, master, masterName, options, onDone),
                        $"{rep.Name} 0x{rep.Guid:X8} -> master 0x{master:X8} ({a.Id})", RecoveryHint, out _, out refusal))
                    return false;

                handedOff = true;
            }
            catch (Exception ex) when (!handedOff)
            {
                // Only the steps before the tail can throw to here (the tail catches its own), so nothing has been purged.
                log.Error($"[TRIAL] promote of {rep.Name} 0x{rep.Guid:X8} over master 0x{master:X8} threw; the gate is released", ex);

                gate.Release(token);

                return Refuse($"the promote threw before anything was purged ({ex.Message}); check the server log", out refusal);
            }

            log.Info($"[TRIAL] promote queued: {rep.Name} 0x{rep.Guid:X8} -> master 0x{master:X8} ({a.Id})");

            var lines = new List<string>
            {
                $"promote of rep {repIndex} to {a.Id} (master 0x{master:X8}) queued; wait for 'Successfully copied', check for a facet WARNING, then rebuild the reps (reps built before this promote will report drift)",
            };

            if (wasBroken)
                lines.Add($"note: master 0x{master:X8} was marked broken ({brokenBefore}); this promote replaces it");

            if (lineageNote != null)
                lines.Add(lineageNote);

            var shortfalls = ConsumableShortfalls(a.LiftUp?.Consumables, facts.Stacks);
            if (shortfalls.Count > 0)
                lines.Add($"WARNING: {rep.Name} carries fewer consumables than {a.Id} lists ({string.Join(", ", shortfalls)}); the promoted master keeps the rep's counts, and so will every rep built from it");

            reply = lines;
            return true;
        }

        /// <summary>How long an unreported copy's "has not landed" mark holds off the next purge of its master: 3 x HoldSeconds (15 minutes).</summary>
        internal const double CopyInFlightWaitSeconds = 3 * TrialCopyGate.HoldSeconds;

        /// <summary>
        /// The purge-then-copy tail promote and adopt share, run with the master already held exclusively under
        /// <paramref name="token"/> and every refusal behind it. In order: mark the master broken with its lineage
        /// (<paramref name="masterArchetype"/>, <paramref name="masterCalibration"/>), PurgeSlot it, then hand <paramref name="copy"/>
        /// the options: unpublished, with <see cref="Normalize"/> in BeforeSave writing the promoted stamp built from that lineage.
        /// The copy's onDone clears the broken flag on success, or re-marks it on failure, and releases the gate either way.
        ///
        /// False, with the gate released, when the purge fails or anything throws before the copy is handed off; the master is
        /// left broken once its purge has started. Never throws. The out stamp is the stamp the copy will carry, or
        /// null when the purge never succeeded (<paramref name="copyStamp"/>). <paramref name="verb"/> ("promote") and <paramref name="operation"/>
        /// ("promote of rep 4") word the broken reasons and log lines; <paramref name="description"/> names the copy in the log.
        /// </summary>
        internal static bool TryPurgeThenCopy(Lookups lookups, long token, uint master, string verb, string operation, string masterArchetype, string masterCalibration,
            DateTime now, Action<CopyCharOptions, Action<bool>> copy, string description, string recoveryHint, out string copyStamp, out string refusal)
        {
            copyStamp = null;
            refusal = null;

            var gate = lookups.Gate;
            var at = now.ToString(TrialRebuild.PromotedTimeFormat, CultureInfo.InvariantCulture);
            var purgeStarted = false;

            // A copy onto this master that has not reported may still land: its hold lapses after HoldSeconds, but the copy itself
            // runs on, so purging again under it would race the save. Wait for it until the mark is CopyInFlightWaitSeconds old.
            if (gate.TryGetCopyInFlight(master, out var inFlightAge) && inFlightAge < CopyInFlightWaitSeconds)
            {
                gate.Release(token);
                return Refuse($"a copy onto this master started {(int)inFlightAge} s ago and has not reported; wait for it or restart", out refusal);
            }

            try
            {
                // Broken from the purge until the copy lands: a master in between is in an unknown state, and rebuild refuses it.
                // The lineage is recorded with it, read before the purge deletes the stamp.
                var markGeneration = gate.MarkBroken(master, $"a {operation} started at {at} and its copy has not landed", masterArchetype, masterCalibration, copyInFlight: true);
                purgeStarted = true;

                if (!lookups.Purge(master))
                {
                    gate.MarkBroken(master, $"the {operation} at {at} failed at its purge", masterArchetype, masterCalibration);
                    gate.Release(token);
                    return Refuse($"the purge of master 0x{master:X8} failed and nothing was copied; the master is marked broken (rebuild refuses it); {recoveryHint}", out refusal);
                }

                var stamp = TrialRebuild.PromotedStamp(masterArchetype, masterCalibration, now);
                var deleteTime = (ulong)Time.GetUnixTime(now);
                copyStamp = stamp;

                var options = new CopyCharOptions
                {
                    Publish = false,
                    BeforeSave = player =>
                    {
                        var result = Normalize(player.Biota, player.GetAllPossessions().Select(p => p.Biota), player.Character, stamp, deleteTime);
                        log.Info($"[TRIAL] {verb} normalize 0x{master:X8}: {result.TimedRemoved} timed enchantment(s) and {result.VitaeRemoved} Vitae removed, {result.CastersCleared} foreign caster(s) cleared");
                    },
                };

                copy(options, ok =>
                {
                    if (ok)
                    {
                        if (gate.ReleaseAndClearBroken(token, master))
                            log.Info($"[TRIAL] {verb} done: {description}, stamp {stamp}");
                        else if (gate.ClearBrokenIfGeneration(master, markGeneration))
                            log.Info($"[TRIAL] {verb} done AFTER its gate hold lapsed: {description}, stamp {stamp}; no newer mark was made, so the broken flag is cleared");
                        else
                            log.Warn($"[TRIAL] {verb} copy landed AFTER its gate hold lapsed: {description}; the broken flag is left as it is, check the master and re-run @trial {verb} if in doubt");
                    }
                    else
                    {
                        gate.MarkBroken(master, $"the {operation} at {at} failed at its copy", masterArchetype, masterCalibration);
                        gate.Release(token);
                        log.Warn($"[TRIAL] {verb} copy FAILED: {description}; the master was purged and is marked broken; {recoveryHint}");
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] {verb}: {description} threw{(purgeStarted ? " after its purge started" : "")}; the gate is released", ex);

                if (purgeStarted)
                    gate.MarkBroken(master, $"a {operation} threw after its purge started", masterArchetype, masterCalibration);

                gate.Release(token);
                copyStamp = null;

                return Refuse(purgeStarted
                    ? $"the {verb} threw after the purge of master 0x{master:X8} started ({ex.Message}); the master is marked broken (rebuild refuses it); {recoveryHint}"
                    : $"the {verb} threw before anything was purged ({ex.Message}); check the server log", out refusal);
            }
        }

        // ================================================================ unpromote

        /// <summary>Purges the promoted master of <paramref name="a"/>, live.</summary>
        public static bool TryUnpromote(TrialArchetype a, out string refusal, out string reply)
            => TryUnpromote(a, Live(), out refusal, out reply);

        /// <summary>
        /// The refusals, in order: (the kill switch, the data directory and an unknown archetype are the command's) any session
        /// runs on this archetype's slot; the gate is busy for the master; the master's stamp does not parse as promoted (so a
        /// seeded master is never purged by mistake). Then PurgeSlot(master).
        /// </summary>
        internal static bool TryUnpromote(TrialArchetype a, Lookups lookups, out string refusal, out string reply)
        {
            refusal = null;
            reply = null;

            var gate = lookups?.Gate ?? throw new ArgumentException("the copy gate is required", nameof(lookups));

            if (a == null)
                return Refuse("no archetype", out refusal);

            var master = TrialRoster.MasterSlot(a.Slot);

            if (lookups.AnySessionForSlot(a.Slot))
                return Refuse($"a trial session is running archetype slot {a.Slot}; end it before unpromoting master 0x{master:X8}", out refusal);

            if (!gate.TryTake(master, 0, out var token, out var busy))
                return Refuse($"a copy or purge is already in flight on 0x{busy:X8}; wait for its 'Successfully copied' line (at most {TrialCopyGate.HoldSeconds / 60:0} minutes) and retry", out refusal);

            // As in TryPurgeThenCopy: an unreported copy may still land, so it is waited for, not purged under.
            if (gate.TryGetCopyInFlight(master, out var inFlightAge) && inFlightAge < CopyInFlightWaitSeconds)
            {
                gate.Release(token);
                return Refuse($"a copy onto this master started {(int)inFlightAge} s ago and has not reported; wait for it or restart", out refusal);
            }

            DateTime? promotedAt;
            var purgeStarted = false;

            // As in promote: a throw after the take releases the gate, and leaves the master broken once the purge has started.
            try
            {
                var stamp = lookups.Stamp(master);
                if (!TrialRebuild.TryParseStamp(stamp, out var archetypeSha, out var calibrationSha, out promotedAt) || promotedAt == null)
                    return Release(gate, token, $"master 0x{master:X8} is not promoted (stamp '{stamp ?? "none"}'); unpromote only purges a promoted master, never a seeded one", out refusal);

                gate.MarkBroken(master, "an unpromote's purge has not completed", archetypeSha, calibrationSha);
                purgeStarted = true;

                if (!lookups.Purge(master))
                {
                    gate.MarkBroken(master, "an unpromote failed at its purge");
                    gate.Release(token);
                    return Refuse($"the purge of master 0x{master:X8} failed; the master is marked broken (rebuild refuses it); re-run @trial promote to restore a promoted build, or reseed (seed-trial-archetypes) to restore the seeded one; after a server restart the reseed is the only recovery", out refusal);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] unpromote of master 0x{master:X8} threw{(purgeStarted ? " after its purge started" : "")}; the gate is released", ex);

                if (purgeStarted)
                    gate.MarkBroken(master, "an unpromote threw after its purge started");

                gate.Release(token);

                return Refuse(purgeStarted
                    ? $"the unpromote threw after the purge of master 0x{master:X8} started ({ex.Message}); the master is marked broken; reseed it (seed-trial-archetypes)"
                    : $"the unpromote threw before anything was purged ({ex.Message}); check the server log", out refusal);
            }

            gate.ReleaseAndClearBroken(token, master);

            log.Info($"[TRIAL] unpromote: purged master 0x{master:X8} ({a.Id}), promoted {promotedAt.Value.ToString(TrialRebuild.PromotedTimeFormat, CultureInfo.InvariantCulture)}");

            reply = $"master 0x{master:X8} ({a.Id}) purged; run seed-trial-archetypes (mode=seed) to rebuild it from the snapshot";
            return true;
        }

        /// <summary>
        /// @trial unpromote slot:&lt;n&gt;: the same unpromote, gate and refusals, addressed by master slot rather than by archetype id.
        /// It is the recovery for a resolver collision (two archetypes on one slot, one curated id in two slots), where resolving the
        /// id refuses, and for what a broken adopt or promote leaves. With a promoted stamp it is unpromote. With no stamp it clears
        /// (<see cref="TryClearSlot"/>) a slot whose broken record keeps a curated lineage, or a slot with a leftover character row
        /// that no seeded file names (a copy that saved its row but never its stamp, then a restart); a seeded file's slot is never
        /// cleared this way, since the reseed rebuilds it. Only trial master guids (TrialRoster.MasterSlot 0..MasterCount-1) are touched.
        /// </summary>
        internal static bool TryUnpromoteSlot(int slot, Lookups lookups, out string refusal, out string reply)
        {
            reply = null;

            if (slot < 0 || slot >= TrialRoster.MasterCount)
                return Refuse($"slot {slot} is outside 0..{TrialRoster.MasterCount - 1}", out refusal);

            var gate = lookups?.Gate ?? throw new ArgumentException("the copy gate is required", nameof(lookups));
            var master = TrialRoster.MasterSlot(slot);

            // Belt and braces: whatever MasterSlot ever becomes, nothing outside the trial master band is purged from here.
            if (master < TrialRoster.SlotBase || master >= TrialRoster.SlotBase + (uint)TrialRoster.MasterCount)
                return Refuse($"0x{master:X8} is not a trial master guid", out refusal);

            if (string.IsNullOrEmpty(lookups.Stamp(master)))
            {
                // A curated slot with no stamp but a broken record keeping a curated lineage (an adopt or promote that purged and
                // never landed): what the resolver lists as a broken curated archetype.
                if (gate.TryGetBrokenLineage(master, out var lineage, out _) && TrialCurated.TryParseIdentity(lineage, out var curatedId, out _, out _))
                    return TryClearSlot(slot, $"the broken curated archetype {curatedId}", lookups, out refusal, out reply);

                // A leftover character row with no stamp: cleared only when no seeded file names the slot.
                if (lookups.HasCharacterRow != null && lookups.HasCharacterRow(master))
                {
                    var seeded = lookups.SeededSlots?.Invoke();
                    if (seeded == null)
                        return Refuse($"slot {slot} has a character row and no stamp, but the seeded archetype files could not be read; nothing was purged", out refusal);

                    if (seeded.Contains(slot))
                        return Refuse($"slot {slot} has a character row and no stamp, and a seeded archetype file names it; reseed (seed-trial-archetypes) to rebuild that master, nothing was purged", out refusal);

                    return TryClearSlot(slot, "a leftover character row with no stamp", lookups, out refusal, out reply);
                }
            }

            if (!TryUnpromote(SlotArchetype(slot), lookups, out refusal, out _))
                return false;

            reply = $"master slot {slot} (0x{master:X8}) purged; the slot is free, unless a seeded archetype file names it (then reseed, seed-trial-archetypes, to rebuild that file's master)";
            return true;
        }

        /// <summary>
        /// Clears a stamp-less master slot: the same guards as unpromote (no session on the slot, the master taken exclusively, so no
        /// copy whose hold is still live) plus the in-flight copy wait, then PurgeSlot and any broken record cleared. On a failed or
        /// throwing purge the slot is left broken (a curated lineage, if one was recorded, is kept).
        /// </summary>
        private static bool TryClearSlot(int slot, string what, Lookups lookups, out string refusal, out string reply)
        {
            reply = null;

            var gate = lookups.Gate;
            var master = TrialRoster.MasterSlot(slot);

            if (lookups.AnySessionForSlot(slot))
                return Refuse($"a trial session is running archetype slot {slot}; end it before clearing master 0x{master:X8}", out refusal);

            if (!gate.TryTake(master, 0, out var token, out var busy))
                return Refuse($"a copy or purge is already in flight on 0x{busy:X8}; wait for its 'Successfully copied' line (at most {TrialCopyGate.HoldSeconds / 60:0} minutes) and retry", out refusal);

            // As in TryPurgeThenCopy: an unreported copy may still land, so it is waited for, not purged under.
            if (gate.TryGetCopyInFlight(master, out var inFlightAge) && inFlightAge < CopyInFlightWaitSeconds)
            {
                gate.Release(token);
                return Refuse($"a copy onto this master started {(int)inFlightAge} s ago and has not reported; wait for it or restart", out refusal);
            }

            try
            {
                gate.MarkBroken(master, $"a slot unpromote's purge of {what} has not completed");

                if (!lookups.Purge(master))
                {
                    gate.MarkBroken(master, $"a slot unpromote of {what} failed at its purge");
                    gate.Release(token);
                    return Refuse($"the purge of master 0x{master:X8} failed; it stays broken, check the server log and run @trial unpromote {TrialArchetypeResolver.SlotPrefix}{slot} again", out refusal);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] slot unpromote of master 0x{master:X8} ({what}) threw; the gate is released", ex);
                gate.MarkBroken(master, $"a slot unpromote of {what} threw after its purge started");
                gate.Release(token);
                return Refuse($"the purge of master 0x{master:X8} threw ({ex.Message}); it stays broken, check the server log", out refusal);
            }

            gate.ReleaseAndClearBroken(token, master);

            log.Info($"[TRIAL] slot unpromote: cleared master 0x{master:X8} ({what})");

            refusal = null;
            reply = $"master slot {slot} (0x{master:X8}): {what} was cleared and what was left purged; the slot is free";
            return true;
        }

        /// <summary>The stand-in archetype unpromote is given for a bare slot: only its slot and its id (for the log) are read.</summary>
        internal static TrialArchetype SlotArchetype(int slot)
            => new TrialArchetype($"{TrialArchetypeResolver.SlotPrefix}{slot}", slot, ACE.Server.ClassAbilities.ClassAbilityClass.None, 0, null, null, TrialBuffs.OwnSpellbook);

        // ================================================================ pure helpers

        /// <summary>
        /// Prepares the built copy as a parked master, mirroring the seed (TrialSeedCommand.DropVitae and DropTimedEnchantments)
        /// on the player and every item: the Vitae row and every enchantment whose Duration is not -1 are removed, and a
        /// permanent row whose caster is not part of the copy gets caster 0 (the seed's DropTimedEnchantments zeroes a
        /// foreign caster the same way, so a promoted master never names a character outside itself as a caster). Then the promoted stamp is set and the character is
        /// parked: AccountId 0, IsDeleted, DeleteTime <paramref name="deleteTime"/>. Pure: no world or database access.
        /// </summary>
        internal static NormalizeResult Normalize(EntityBiota player, IEnumerable<EntityBiota> items, ShardCharacter character, string stamp, ulong deleteTime)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(character);

            var all = new List<EntityBiota> { player };
            if (items != null)
                all.AddRange(items.Where(i => i != null));

            var inCopy = all.Select(b => b.Id).ToHashSet();

            int timed = 0, vitae = 0, casters = 0;

            foreach (var biota in all)
            {
                if (biota.PropertiesEnchantmentRegistry == null)
                    continue;

                foreach (var row in biota.PropertiesEnchantmentRegistry.ToList())
                {
                    if (row.SpellId == (int)SpellId.Vitae)
                    {
                        biota.PropertiesEnchantmentRegistry.Remove(row);
                        vitae++;
                    }
                    else if (row.Duration != -1)
                    {
                        biota.PropertiesEnchantmentRegistry.Remove(row);
                        timed++;
                    }
                    else if (row.CasterObjectId != 0 && !inCopy.Contains(row.CasterObjectId))
                    {
                        row.CasterObjectId = 0;
                        casters++;
                    }
                }
            }

            player.PropertiesString ??= new Dictionary<PropertyString, string>();
            player.PropertiesString[PropertyString.TrialArchetypeStamp] = stamp;

            character.AccountId = 0;
            character.IsDeleted = true;
            character.DeleteTime = deleteTime;

            return new NormalizeResult(timed, vitae, casters);
        }

        /// <summary>Sums stack sizes by wcid; a possession with no StackSize counts 1.</summary>
        internal static IReadOnlyDictionary<uint, long> StacksByWcid(IEnumerable<(uint Wcid, long? Stack)> possessions)
        {
            var stacks = new Dictionary<uint, long>();

            foreach (var (wcid, stack) in possessions ?? Enumerable.Empty<(uint, long?)>())
            {
                stacks.TryGetValue(wcid, out var have);
                stacks[wcid] = have + (stack ?? 1);
            }

            return stacks;
        }

        /// <summary>
        /// The archetype's LiftUp.Consumables the rep holds fewer of than listed, as "wcid N: have/want" (wants summed by wcid).
        /// An empty or missing list yields nothing, so an archetype with no consumables never warns.
        /// </summary>
        internal static IReadOnlyList<string> ConsumableShortfalls(IReadOnlyList<TrialConsumable> wanted, IReadOnlyDictionary<uint, long> have)
        {
            if (wanted == null || wanted.Count == 0)
                return Array.Empty<string>();

            var shortfalls = new List<string>();

            foreach (var want in wanted.GroupBy(c => c.Wcid).Select(g => (Wcid: g.Key, Stack: g.Sum(c => (long)c.Stack))).OrderBy(w => w.Wcid))
            {
                long count = 0;
                have?.TryGetValue(want.Wcid, out count);

                if (count < want.Stack)
                    shortfalls.Add($"wcid {want.Wcid}: {count}/{want.Stack}");
            }

            return shortfalls;
        }

        /// <summary>What to do about a broken master. The broken record (and the lineage promote recovers from) is in memory only.</summary>
        internal const string RecoveryHint = "re-run @trial promote; after a server restart that record is gone and the recovery is a reseed (seed-trial-archetypes)";

        private static bool Release(TrialCopyGate gate, long token, string reason, out string refusal)
        {
            gate.Release(token);
            return Refuse(reason, out refusal);
        }

        private static bool Refuse(string reason, out string refusal)
        {
            refusal = reason;
            return false;
        }
    }
}
