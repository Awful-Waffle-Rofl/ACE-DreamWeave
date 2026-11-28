using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// @trial adopt &lt;id&gt; &lt;Class&gt; &lt;character name...&gt;: creates a curated archetype (owner rulings R1, R2) by copying
    /// an offline, hand-built character onto a free master slot, parked and stamped with the curated stamp (TrialCurated), with
    /// the purge-then-copy tail promote uses (TrialPromote.TryPurgeThenCopy). No archetype file is written: the stamp is the
    /// archetype, and TrialArchetypeResolver finds it from there.
    ///
    /// R3: nothing here enforces a level or a CAP total. The adopted character's level becomes the archetype's level, and the
    /// reply reports it with TotalClassAbilityPointsEarned (9018) for the owner to check by hand. The one CAP check is that the
    /// login catch-ups have run: MilestoneClassAbilityPointsGranted (9026) has reached the milestones of the source's level, and
    /// EnlightenmentClassAbilityPointsGranted (9032) those of its enlightenment count, so the copy is not short points the
    /// source would have been paid at its next login.
    ///
    /// THE SLOT AND THE GATE. The slot is picked from the stamps, the character rows and the gate, then the gate is taken on it
    /// (the master exclusive, the source shared), and then, UNDER that hold, the slot facts are read again and the pick is made
    /// again; a stamp or character row that has appeared on the slot, or a pick that no longer lands on it, releases and
    /// refuses. The broken mark, with the curated lineage, goes on in TryPurgeThenCopy BEFORE the purge (as promote's always
    /// did) and comes off only when the copy lands, so even after the 300 s hold lapses mid-copy a second adopt of another id
    /// reads the slot broken and passes over it. Every other writer of a master slot (promote,
    /// unpromote, rebuild, another adopt) takes the gate on that master first, and an adopt in flight leaves its slot held, then
    /// broken, then stamped, so a second adopt can never pick the same slot: before its own take the slot reads busy or broken
    /// or stamped, and a take on a slot someone else holds fails. The re-pick under the hold closes the remaining gap, a slot
    /// whose facts changed between the first read and the take. The seeder (trialseed) does not take the gate, but it only
    /// writes slots whose stamp is not promoted, and a curated slot is promoted from the moment its copy lands.
    ///
    /// MUST RUN ON THE WORLD THREAD, like promote: PurgeSlot calls AllegianceManager/HouseManager.DoHandlePlayerDelete directly.
    /// The @trial command is RequiresWorld.
    /// </summary>
    public static class TrialAdopt
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>What the refusals need to know about the source character, read from its saved rows.</summary>
        internal readonly record struct SourceFacts(bool HasCharacter, bool HasBiota, int Possessions, int? Level, int Enlightenment,
            int MilestonePointsGranted, int EnlightenmentPointsGranted, int TotalPointsEarned);

        /// <summary>The adopt lookups: promote's (online, sessions, purge, clock, gate) plus the adopt-only ones.</summary>
        internal sealed class Lookups : TrialPromote.Lookups
        {
            /// <summary>The seeded archetype files (TrialArchetypeLoader.LoadDirectory). Throws TrialArchetypeException on bad data.</summary>
            public Func<IReadOnlyList<(TrialArchetype Archetype, string Sha256)>> Seeded;

            /// <summary>The master stamps by slot (TrialArchetypeResolver.StampScan live).</summary>
            public Func<IReadOnlyDictionary<int, string>> Stamps;

            /// <summary>The master slots that have a character row, deleted or not.</summary>
            public Func<IReadOnlyCollection<int>> CharacterSlots;

            /// <summary>The character by name: its guid and its exact name, or null.</summary>
            public Func<string, (uint Guid, string Name)?> FindByName;

            public Func<uint, SourceFacts> Source;

            /// <summary>PropertyManager class_abilities_enabled. Off, the login catch-ups never pay a milestone, so the counter refusal says so.</summary>
            public Func<bool> ClassAbilitiesEnabled = () => true;

            /// <summary>(admin, source guid, source name, master guid, master name, options, onDone): queues the copy; onDone may run on any thread, or never.</summary>
            public Action<Session, uint, string, uint, string, CopyCharOptions, Action<bool>> CopySource;
        }

        internal static Lookups Live(string archetypesDir)
        {
            var promote = TrialPromote.Live();

            return new Lookups
            {
                IsOnline = promote.IsOnline,
                HasTrialSession = promote.HasTrialSession,
                AnySessionForSlot = promote.AnySessionForSlot,
                RepPurgeFailed = promote.RepPurgeFailed,
                Stamp = promote.Stamp,
                Purge = promote.Purge,
                UtcNow = promote.UtcNow,
                Gate = promote.Gate,
                Seeded = () => TrialArchetypeLoader.LoadDirectory(archetypesDir),
                Stamps = () => TrialArchetypeResolver.StampScan(),
                CharacterSlots = () => DatabaseManager.Shard.BaseDatabase.GetCharacterIdsInRange(TrialRoster.SlotBase, TrialRoster.SlotBase + (uint)TrialRoster.MasterCount - 1)
                    .Select(g => (int)(g - TrialRoster.SlotBase)).ToList(),
                FindByName = name =>
                {
                    var p = PlayerManager.FindByName(name);
                    return p == null ? null : (p.Guid.Full, p.Name);
                },
                Source = LiveSourceFacts,
                ClassAbilitiesEnabled = () => PropertyManager.GetBool("class_abilities_enabled").Item,
                CopySource = (admin, source, sourceName, master, masterName, options, onDone) => AdminCommands.DoCopyChar(admin, sourceName, source, false, masterName, 0, master, onDone, options),
            };
        }

        /// <summary>The source's character row (a disposed-context stub read), its biota and its possessions, read directly on this thread, as promote reads a rep.</summary>
        private static SourceFacts LiveSourceFacts(uint guid)
        {
            var db = DatabaseManager.Shard.BaseDatabase;

            var hasCharacter = db.GetCharacterStubByGuid(guid) != null;
            var biota = db.GetBiota(guid, true);

            var possessions = db.GetPossessedBiotasInParallel(guid);
            var count = possessions.Inventory.Count + possessions.WieldedItems.Count;

            int? Int(PropertyInt p) => biota?.BiotaPropertiesInt?.FirstOrDefault(r => r.Type == (ushort)p)?.Value;

            return new SourceFacts(hasCharacter, biota != null, count, Int(PropertyInt.Level), Int(PropertyInt.Enlightenment) ?? 0,
                Int(PropertyInt.MilestoneClassAbilityPointsGranted) ?? 0, Int(PropertyInt.EnlightenmentClassAbilityPointsGranted) ?? 0,
                Int(PropertyInt.TotalClassAbilityPointsEarned) ?? 0);
        }

        /// <summary>What to do about a master an adopt left broken. The broken record (and the slot reuse it allows) is in memory only.</summary>
        internal const string RecoveryHint = "re-run @trial adopt with the same id (it goes back to this slot); after a server restart the broken record is gone: if @trial list still shows the id, the master may be half-written, so purge it with @trial unpromote slot:<n> and adopt again";

        /// <summary>Adopts <paramref name="name"/> as curated archetype <paramref name="id"/>, live.</summary>
        public static bool TryAdopt(Session admin, string archetypesDir, string id, string className, string name, out string refusal, out IReadOnlyList<string> reply)
            => TryAdopt(admin, admin?.Player?.Guid.Full, id, className, name, Live(archetypesDir), out refusal, out reply, out _);

        /// <summary>
        /// The refusals, in order (the kill switch and the data directory are the command's, see TrialCommands.AdoptReply):
        /// 1. the id's format, and an id a seeded file already has (or trial data the resolver refuses);
        /// 2. the class;
        /// 3. the slot pick (TrialCurated.TryPickSlot), or no free slot;
        /// 4. the name resolves;
        /// 5. the source is a trial master or a rep (use promote);
        /// 6. the caller is not an in-world character, or is the source;
        /// 7. the source is online, or has a trial session;
        /// 8. a session is running this slot;
        /// 9. the gate (the master exclusive, the source shared), and the slot re-picked under it;
        /// 10. the source has no character row, no biota or no possessions, or no level;
        /// 11. the milestone counters 9026 and 9032 have not caught up;
        /// 12. the source came online.
        /// Then TryPurgeThenCopy: mark broken with the curated lineage, purge, copy with Normalize(curated stamp).
        /// Nothing is purged or copied on any refusal. <paramref name="stamp"/> is the stamp the copy carries.
        /// </summary>
        internal static bool TryAdopt(Session admin, uint? callerGuid, string id, string className, string name, Lookups lookups,
            out string refusal, out IReadOnlyList<string> reply, out string stamp)
        {
            refusal = null;
            reply = null;
            stamp = null;

            var gate = lookups?.Gate ?? throw new ArgumentException("the copy gate is required", nameof(lookups));

            // 1. The id, and the data it must not collide with. The resolver's consistency checks run on the same reads.
            if (!TrialCurated.IsValidId(id, out var idReason))
                return Refuse(idReason, out refusal);

            IReadOnlyList<(TrialArchetype Archetype, string Sha256)> seeded;
            try
            {
                seeded = lookups.Seeded();
            }
            catch (TrialArchetypeException ex)
            {
                return Refuse($"trial data: {ex.Message}", out refusal);
            }

            if (seeded.Any(s => string.Equals(s.Archetype.Id, id, StringComparison.Ordinal)))
                return Refuse($"'{id}' is a seeded archetype file; pick another id for a curated archetype", out refusal);

            // An id the resolver already finds ambiguous (it sits in two slots, or on a seeded file's slot) is cleared with
            // @trial unpromote slot:<n> first; adopting over it would pick one copy at random. Other collisions do not block.
            var collision = TrialArchetypeResolver.Collisions(seeded, ReadStamps(lookups), gate).FirstOrDefault(c => c.Ids.Contains(id, StringComparer.Ordinal));
            if (collision != null)
                return Refuse($"trial data: {collision.Message}", out refusal);

            // 2. The class.
            if (!TrialCurated.TryParseClass(className, out var cls))
                return Refuse($"'{className}' is not a class; one of {string.Join(", ", Enum.GetValues<ClassAbilityClass>().Where(c => c != ClassAbilityClass.None))}", out refusal);

            // 3. The slot.
            var seededSlots = seeded.Select(s => s.Archetype.Slot).ToHashSet();

            if (!TryPick(id, lookups, seededSlots, heldSlot: null, out var slot, out var reused, out var pickRefusal, out var pickedStamp, out var pickedHadCharacter))
                return Refuse(pickRefusal, out refusal);

            var master = TrialRoster.MasterSlot(slot);
            var masterName = TrialRoster.MasterName(slot);

            // 4. The source by name.
            if (string.IsNullOrWhiteSpace(name))
                return Refuse("name the character to adopt", out refusal);

            var found = lookups.FindByName(name);
            if (found == null)
                return Refuse($"no character is named \"{name}\"", out refusal);

            var (source, sourceName) = found.Value;

            // 5. Not a trial character: a master is already an archetype, and a rep goes back with promote.
            if (source >= TrialRoster.SlotBase && source < TrialRoster.SlotBase + (uint)TrialRoster.MasterCount)
                return Refuse($"{sourceName} (0x{source:X8}) is a trial master; adopt copies a live character, never a master", out refusal);

            if (TrialStarter.IsRepGuid(source))
                return Refuse($"{sourceName} (0x{source:X8}) is a trial rep; use @trial promote to copy a tuned rep over its master", out refusal);

            // 6. The caller: an in-world character (the copy is made under its session), and never the source.
            if (callerGuid == null)
                return Refuse("run @trial adopt from an in-world character that is not the one being adopted", out refusal);

            if (callerGuid.Value == source)
                return Refuse($"you are {sourceName}; run @trial adopt from another character, with {sourceName} logged out", out refusal);

            // 7. The source must be logged out, and must not belong to a trial session.
            if (lookups.IsOnline(source))
                return Refuse($"{sourceName} (0x{source:X8}) is online; log it out first, then adopt", out refusal);

            if (lookups.HasTrialSession(source))
                return Refuse($"{sourceName} (0x{source:X8}) still has a trial session; end it first", out refusal);

            // 8. No session may be reading this slot's master.
            if (lookups.AnySessionForSlot(slot))
                return Refuse($"a trial session is running archetype slot {slot}; end it before adopting over master 0x{master:X8}", out refusal);

            // 9. The gate: the master exclusive (purged and written), the source shared (only read). Released on every path below.
            if (!gate.TryTake(master, source, out var token, out var busy))
                return Refuse($"a copy or purge is already in flight on 0x{busy:X8}; wait for its 'Successfully copied' line (at most {TrialCopyGate.HoldSeconds / 60:0} minutes) and retry", out refusal);

            bool wasBroken;
            string brokenBefore;
            SourceFacts facts;
            int level;
            var handedOff = false;

            try
            {
                // 9b. Under the hold, the picked slot read again: a stamp or a character row that has appeared (or a stamp that has
                // changed) since the pick means another writer got there first. Then the whole pick again from those reads: it must
                // still land on the slot just taken.
                var stampNow = ReadStamps(lookups).TryGetValue(slot, out var s9) ? s9 : null;
                var characterNow = (lookups.CharacterSlots() ?? Array.Empty<int>()).Contains(slot);

                if (!string.Equals(stampNow ?? "", pickedStamp ?? "", StringComparison.Ordinal))
                    return Release(gate, token, $"slot {slot} {(string.IsNullOrEmpty(pickedStamp) ? "gained a stamp" : "changed its stamp")} while adopting; nothing was purged, run @trial adopt again", out refusal);

                if (characterNow && !pickedHadCharacter)
                    return Release(gate, token, $"slot {slot} gained a character row while adopting; nothing was purged, run @trial adopt again", out refusal);

                if (!TryPick(id, lookups, seededSlots, heldSlot: slot, out var again, out _, out pickRefusal, out _, out _) || again != slot)
                    return Release(gate, token, $"slot {slot} changed while adopting ({pickRefusal ?? $"the pick now lands on slot {again}"}); nothing was purged, run @trial adopt again", out refusal);

                // 10. Something to copy.
                facts = lookups.Source(source);
                if (!facts.HasCharacter)
                    return Release(gate, token, $"{sourceName} (0x{source:X8}) has no character row", out refusal);

                if (!facts.HasBiota)
                    return Release(gate, token, $"{sourceName} (0x{source:X8}) has no biota", out refusal);

                if (facts.Possessions == 0)
                    return Release(gate, token, $"{sourceName} (0x{source:X8}) has no possessions; a curated master needs the character's gear", out refusal);

                if (facts.Level == null || facts.Level.Value < 1)
                    return Release(gate, token, $"{sourceName} (0x{source:X8}) has no level", out refusal);

                level = facts.Level.Value;

                // 11. The login catch-ups have paid every milestone the source is owed (no level or CAP total is enforced, R3).
                var milestones = ClassAbilityMilestones.EntitledCount(level);
                var enlightenment = EnlightenmentCapMilestones.EntitledCount(facts.Enlightenment);
                if (facts.MilestonePointsGranted < milestones || facts.EnlightenmentPointsGranted < enlightenment)
                    return Release(gate, token, $"{sourceName}'s class ability milestone points have not caught up (level milestones {facts.MilestonePointsGranted}/{milestones}, enlightenment milestones {facts.EnlightenmentPointsGranted}/{enlightenment}); " +
                        (lookups.ClassAbilitiesEnabled()
                            ? $"log {sourceName} in once so the login catch-up pays them, log out, then adopt"
                            : "class abilities are disabled, so CAP milestones never catch up; turn class_abilities_enabled on, log the character in once, then adopt"), out refusal);

                // 12. Re-check immediately before the purge.
                if (lookups.IsOnline(source))
                    return Release(gate, token, $"{sourceName} (0x{source:X8}) came online; log it out first, then adopt", out refusal);

                wasBroken = gate.IsBroken(master, out brokenBefore);
                var now = lookups.UtcNow().ToUniversalTime();

                // The shared tail, with the curated identity as the lineage: its stamp is TrialCurated.Stamp(id, cls, level, now).
                if (!TrialPromote.TryPurgeThenCopy(lookups, token, master, "adopt", $"adopt of {id}", TrialCurated.Identity(id, cls, level), TrialCurated.CalibrationToken, now,
                        (options, onDone) => lookups.CopySource(admin, source, sourceName, master, masterName, options, onDone),
                        $"{sourceName} 0x{source:X8} -> master 0x{master:X8} (curated {id})", RecoveryHint, out stamp, out refusal))
                    return false;

                handedOff = true;
            }
            catch (Exception ex) when (!handedOff)
            {
                // Only the steps before the tail can throw to here (the tail catches its own), so nothing has been purged.
                log.Error($"[TRIAL] adopt of {sourceName} 0x{source:X8} as {id} threw; the gate is released", ex);
                gate.Release(token);
                return Refuse($"the adopt threw before anything was purged ({ex.Message}); check the server log", out refusal);
            }

            log.Info($"[TRIAL] adopt queued: {sourceName} 0x{source:X8} -> master 0x{master:X8} (curated {id} {cls} L{level}, slot {slot})");

            var lines = new List<string>
            {
                $"adopt of {sourceName} as curated archetype {id} ({cls}) into slot {slot} (master 0x{master:X8}) queued: level {level}, TotalClassAbilityPointsEarned {facts.TotalPointsEarned}; wait for 'Successfully copied', then rebuild the reps from {id}",
            };

            if (reused)
                lines.Add($"note: slot {slot} already held curated archetype {id}; this adopt replaces it, and reps built from the old one will report drift");

            if (wasBroken)
                lines.Add($"note: master 0x{master:X8} was marked broken ({brokenBefore}); this adopt replaces it");

            reply = lines;
            return true;
        }

        /// <summary>
        /// The slot facts from fresh reads and TrialCurated.TryPickSlot over them. <paramref name="heldSlot"/> is the slot this adopt
        /// already holds exclusively: its hold is this adopt's own, so it does not count as another copy in flight.
        /// </summary>
        private static bool TryPick(string id, Lookups lookups, HashSet<int> seededSlots, int? heldSlot, out int slot, out bool reused, out string refusal,
            out string pickedStamp, out bool pickedHadCharacter)
        {
            pickedStamp = null;
            pickedHadCharacter = false;

            var stamps = ReadStamps(lookups);
            var characters = (lookups.CharacterSlots() ?? Array.Empty<int>()).ToHashSet();
            var gate = lookups.Gate;

            var facts = Enumerable.Range(0, TrialRoster.MasterCount).Select(s =>
            {
                var guid = TrialRoster.MasterSlot(s);
                stamps.TryGetValue(s, out var stamp);
                gate.TryGetBrokenLineage(guid, out var lineage, out _);

                return new TrialCurated.SlotFacts(s, stamp, characters.Contains(s), s != heldSlot && gate.IsBusy(guid), gate.IsBroken(guid, out _), lineage, seededSlots.Contains(s));
            }).ToList();

            if (!TrialCurated.TryPickSlot(id, facts, out slot, out reused, out refusal))
                return false;

            pickedStamp = facts[slot].Stamp;
            pickedHadCharacter = facts[slot].HasCharacter;
            return true;
        }

        private static IReadOnlyDictionary<int, string> ReadStamps(Lookups lookups) => lookups.Stamps() ?? new Dictionary<int, string>();

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
