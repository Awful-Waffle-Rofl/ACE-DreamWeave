using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    /// <summary>What <see cref="Player.ApplyPvpTemplate"/> reports to its completion callback.</summary>
    public sealed class PvpTemplateApplyResult
    {
        /// <summary>True when the template is on and the kit is issued: the coordinator may now teleport.</summary>
        public bool Success { get; }

        /// <summary>The player-facing reason on failure (already sent to the player), else null.</summary>
        public string Refusal { get; }

        /// <summary>
        /// True when the failure happened AFTER the record was written and the follow-up restore did not clear it,
        /// so the player is still templated (inert until /pvptemplate restore). The coordinator must not admit them.
        /// </summary>
        public bool StillTemplated { get; }

        /// <summary>
        /// True when the player caused the failure (busy with an animation, or a pack too full to strip into): the
        /// coordinator locks them out as it would a decliner, so a deliberate failure cannot cancel 1v1/2v2 for free.
        /// </summary>
        public bool PlayerCaused { get; }

        /// <summary>True when the only problem was being busy: the coordinator retries the entry for a short window first.</summary>
        public bool Busy { get; }

        private PvpTemplateApplyResult(bool success, string refusal, bool stillTemplated, bool playerCaused = false, bool busy = false)
        {
            Success = success;
            Refusal = refusal;
            StillTemplated = stillTemplated;
            PlayerCaused = playerCaused;
            Busy = busy;
        }

        public static PvpTemplateApplyResult Ok() => new PvpTemplateApplyResult(true, null, false);

        public static PvpTemplateApplyResult Fail(string refusal, bool stillTemplated = false, bool playerCaused = false, bool busy = false) =>
            new PvpTemplateApplyResult(false, refusal, stillTemplated, playerCaused, busy);
    }

    /// <summary>What <see cref="Player.PvpTemplateRunEquippedBackstop"/> found and did.</summary>
    public readonly struct PvpTemplateBackstopResult
    {
        public PvpTemplateBackstopResult(int moved, int survivors)
        {
            Moved = moved;
            Survivors = survivors;
        }

        /// <summary>Personal items that were worn and are now off (verified by recounting EquippedObjects).</summary>
        public int Moved { get; }

        /// <summary>Personal items STILL worn after the backstop: the caller must treat the player as not templated-safe.</summary>
        public int Survivors { get; }
    }

    /// <summary>
    /// The player side of PvP Template Facets (Docs/Pvp/TEMPLATES.md): the in-place overlay's apply and restore,
    /// the issued kit, the gate predicate every Phase B site calls, the equipped-item backstop, the login restore
    /// and sweep, and the heartbeat backstop.
    ///
    /// THE RECORD IS THE STATE. PropertyString 9022 PvpTemplateRestore present means templated, and nothing else
    /// does: no in-memory flag shadows it, so a crash, a relog or a failed restore can never leave a player
    /// "open" while the record still says otherwise.
    ///
    /// THREADING. Apply and restore run on the player's own action queue (the world thread for this landblock),
    /// like EnterPvpMatch. The public queued entry points wrap their body in a try/catch of their own, because an
    /// exception inside EnqueueAction escapes the caller's try/catch.
    ///
    /// NO STATIC FIELDS IN THIS FILE (see PvpArenaHookSettings for why): the logger and every tunable seam live on
    /// PvpTemplateSettings.
    ///
    /// NEVER SENDS GameEventPlayerDescription: resending it mid-session wedges the client
    /// (PlayerCommands.cs:529-536). Every client-visible change is pushed with the per-object private update
    /// messages instead - skills, attributes, vitals, each replaced PropertyInt (verified live 2026-10-03 to
    /// refresh the aug and rating panels instantly), spells and enchantments.
    /// </summary>
    partial class Player
    {
        // ---------------- state ----------------

        /// <summary>Depth of the system apply/restore scope. Above zero, every template gate allows (see BeginPvpTemplateSystemOperation).</summary>
        private int pvpTemplateSystemDepth;

        /// <summary>The heartbeat backstop's "templated with no live match since" stamp. In memory only.</summary>
        private DateTime? pvpTemplateUnboundSinceUtc;

        /// <summary>The last parsed record and the exact string it was parsed from, so the cast gate does not parse JSON per cast.</summary>
        private string pvpTemplateRecordCacheJson;
        private PvpTemplateRestoreRecord pvpTemplateRecordCache;

        /// <summary>The heartbeat backstop does not retry a failed restore before this (in memory only; see PvpTemplateHeartbeatCore).</summary>
        private DateTime? pvpTemplateHeartbeatRetryAfterUtc;

        /// <summary>
        /// True iff the restore record is present. THE definition of templated (TEMPLATES.md "Restore record lives
        /// on the biota"). Phase B and C read this; no site reads the record inline.
        /// </summary>
        public bool IsPvpTemplated => !string.IsNullOrEmpty(GetProperty(PropertyString.PvpTemplateRestore));

        /// <summary>True inside a system apply or restore, where every template gate allows.</summary>
        public bool PvpTemplateSystemBypass => pvpTemplateSystemDepth > 0;

        /// <summary>
        /// THE explicit bypass for the system paths (TEMPLATES.md "Gates": "The system apply and restore paths
        /// bypass gates through one explicit flag, never by inlining a check"). Scoped per player - opening it on
        /// one player never opens a gate for anyone else - and nestable. Dispose to close; always in a using.
        /// </summary>
        internal PvpTemplateSystemScope BeginPvpTemplateSystemOperation() => new PvpTemplateSystemScope(this);

        internal readonly struct PvpTemplateSystemScope : IDisposable
        {
            private readonly Player player;

            public PvpTemplateSystemScope(Player player)
            {
                this.player = player;
                player.pvpTemplateSystemDepth++;
            }

            public void Dispose()
            {
                if (player != null && player.pvpTemplateSystemDepth > 0)
                    player.pvpTemplateSystemDepth--;
            }
        }

        /// <summary>
        /// The parsed restore record. False with a reason when there is none or it cannot be read; a caller that
        /// gates on the result must treat an unreadable record as templated (inert), never as open.
        /// </summary>
        public bool TryGetPvpTemplateRecord(out PvpTemplateRestoreRecord record, out string error)
        {
            var json = GetProperty(PropertyString.PvpTemplateRestore);

            if (string.IsNullOrEmpty(json))
            {
                record = null;
                error = "no restore record";
                return false;
            }

            if (ReferenceEquals(json, pvpTemplateRecordCacheJson) || json == pvpTemplateRecordCacheJson)
            {
                record = pvpTemplateRecordCache;
                error = null;
                return record != null;
            }

            if (!PvpTemplateJson.TryDeserializeRecord(json, out record, out error))
                return false;

            pvpTemplateRecordCacheJson = json;
            pvpTemplateRecordCache = record;
            return true;
        }

        // ======================================================================================
        // the gate predicate (Phase B inserts calls; the decision is PvpTemplateGate.Decide)
        // ======================================================================================

        /// <summary>How the template gates see <paramref name="wo"/> for this player: issued, personal (a possession), or neither.</summary>
        public PvpTemplateItemKind ClassifyForPvpTemplate(WorldObject wo)
        {
            if (wo == null || wo == this)
                return PvpTemplateItemKind.None;

            if (PvpTemplate.IsIssued(wo))
                return PvpTemplateItemKind.Issued;

            if (FindObject(wo.Guid.Full, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out _, out _, out _) != null)
                return PvpTemplateItemKind.Personal;

            return PvpTemplateItemKind.None;
        }

        /// <summary>The cheap classification for a non-templated player: Issued or None (Personal is only meaningful while templated).</summary>
        private PvpTemplateItemKind ClassifyIssuedOnly(WorldObject wo)
            => wo != null && wo != this && PvpTemplate.IsIssued(wo) ? PvpTemplateItemKind.Issued : PvpTemplateItemKind.None;

        /// <summary>
        /// True when an equip of <paramref name="item"/> skips CheckWieldRequirements: the player is templated and the
        /// item itself carries the issued mark. The kit goes on without wield checks (TEMPLATES.md power-source table,
        /// "Level"), so an issued item the player took off goes back on the same way.
        /// </summary>
        public bool PvpTemplateWaivesWieldRequirements(WorldObject item)
            => IsPvpTemplated && PvpTemplate.IsMarkedIssued(item);

        /// <summary>
        /// THE predicate every template gate calls (TEMPLATES.md "Gates"). Returns the refusal text, or null to
        /// allow. Covers the personal-item lockdown and progression lock (while templated) and the issued-item
        /// economy lock (always). Call it before any side effect and before any move-to chain.
        /// </summary>
        public string PvpTemplateBlocked(PvpTemplateAction action, WorldObject item = null)
            => PvpTemplateBlocked(action, item, null);

        /// <summary>The two-object form: use-with-target (source, target) and merge/split (source, destination stack).</summary>
        public string PvpTemplateBlocked(PvpTemplateAction action, WorldObject item, WorldObject target)
        {
            if (PvpTemplateSystemBypass)
                return null;

            var templated = IsPvpTemplated;

            // Not templated: only the always-on issued-item economy lock can refuse, and it needs Issued vs not.
            // Skip the FindObject walk that tells Personal from None (hot path: every pickup, move and use).
            if (!templated)
                return PvpTemplateGate.Decide(action, false, false, ClassifyIssuedOnly(item), ClassifyIssuedOnly(target));

            return PvpTemplateGate.Decide(action, true, false, ClassifyForPvpTemplate(item), ClassifyForPvpTemplate(target));
        }

        /// <summary>
        /// The cast gate: while templated only a spell in the template's spellbook may be cast. An unreadable record
        /// refuses every cast (inert, never open).
        /// </summary>
        public string PvpTemplateCastBlocked(uint spellId) => PvpTemplateCastBlocked(spellId, null);

        /// <summary>
        /// The cast gate for a cast that may come from an item: a built-in spell of an issued item is allowed without
        /// being in the spellbook, one of a personal item is refused, and with no item the spellbook decides.
        /// </summary>
        public string PvpTemplateCastBlocked(uint spellId, WorldObject casterItem)
        {
            if (PvpTemplateSystemBypass || !IsPvpTemplated)
                return null;

            ICollection<int> spells = TryGetPvpTemplateRecord(out var record, out _) ? record.TemplateSpells : null;

            return PvpTemplateGate.DecideCast(spellId, true, false, spells, ClassifyForPvpTemplate(casterItem));
        }

        // ======================================================================================
        // apply
        // ======================================================================================

        /// <summary>
        /// Puts <paramref name="definition"/> on this player for <paramref name="matchId"/>, on the player's action
        /// queue, then calls <paramref name="onComplete"/> (also on that queue) with the outcome. The coordinator
        /// teleports ONLY from a successful callback: the strip and the kit issue happen inside this action, so a
        /// teleport queued blindly behind it could land before the build is on (TEMPLATES.md "Lifecycle").
        /// A throwing callback is logged and swallowed.
        /// </summary>
        public void ApplyPvpTemplate(PvpTemplateDefinition definition, Guid matchId, Action<PvpTemplateApplyResult> onComplete)
        {
            EnqueueAction(new ActionEventDelegate(() =>
            {
                PvpTemplateApplyResult result;

                try
                {
                    result = ApplyPvpTemplateNow(definition, matchId);
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): ApplyPvpTemplate threw for match {matchId}.", ex);
                    result = PvpTemplateApplyResult.Fail(PvpTemplateText.ApplyFailed, IsPvpTemplated);
                }

                try
                {
                    onComplete?.Invoke(result);
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): the apply completion callback threw for match {matchId}.", ex);
                }
            }));
        }

        /// <summary>
        /// The body of <see cref="ApplyPvpTemplate"/>, in the lifecycle's order:
        ///
        ///   precheck (enabled; no existing record; the kit's weenies exist; not busy with gear to strip; main-pack
        ///   room for the own worn set plus the kit's pack items; the record fits in 60 KB) - nothing written yet
        ///   -> capture the record, write it, overwrite the build, clear own buffs, push everything, SAVE at once
        ///   -> strip own gear to the pack -> issue and equip the kit -> apply the template buffs -> full vitals
        ///   -> the equipped-item backstop -> save.
        ///
        /// Any failure after the record is written runs the full restore (which is absolute and idempotent), so a
        /// failed entry leaves the player as they were - or, if that restore itself fails, inert behind the record.
        /// </summary>
        internal PvpTemplateApplyResult ApplyPvpTemplateNow(PvpTemplateDefinition definition, Guid matchId)
        {
            // ---- precheck: nothing is written until every check passes ----

            if (!PvpTemplateSettings.EnabledSource())
                return RefuseApply(PvpTemplateText.ApplyDisabled, matchId, "templates disabled");

            if (IsPvpTemplated)
                return RefuseApply(PvpTemplateText.ApplyAlreadyTemplated, matchId, "a restore record already exists (never overwritten)");

            if (definition == null || string.IsNullOrEmpty(definition.Key))
                return RefuseApply(PvpTemplateText.ApplyFailed, matchId, "no definition");

            if (!TryResolvePvpTemplateKitWeenies(definition, out var weenies, out var missingWcid))
                return RefuseApply(PvpTemplateText.ApplyFailed, matchId, $"kit weenie {missingWcid} of template {definition.Key} does not exist");

            var equipped = EquippedObjects.Values.ToList();

            if (equipped.Count > 0 && IsBusy)
                // Player-caused and retryable: silent here, because the coordinator retries for a short window and only
                // tells the player (with PvpTemplateText.ApplyBusy) if they are still busy when it gives up.
                return RefuseApply(PvpTemplateText.ApplyBusy, matchId, "busy with gear to strip", playerCaused: true, busy: true, announce: false);

            var roomRefusal = PvpTemplateRoomRefusal(definition, weenies);

            if (roomRefusal != null)
                return RefuseApply(roomRefusal, matchId, "no pack room", playerCaused: true);

            // No dialog opened before the template may survive into it (HIGH-1: a stale Yes would bypass the Use gate).
            ConfirmationManager?.AbortAll();

            var record = PvpTemplateOverlay.Capture(Biota, BiotaDatabaseLock, definition, matchId, DateTime.UtcNow,
                PvpTemplateSettings.KeepOwnBuffsSource(), CaptureFacetEquip());

            if (!PvpTemplateJson.TrySerializeRecord(record, out var recordJson, out var recordBytes))
                return RefuseApply(PvpTemplateText.ApplyRecordTooLarge, matchId, $"restore record would be {recordBytes} bytes, cap {PvpTemplateJson.MaxRecordBytes}");

            // ---- the record, then the build. From here on, failure restores. ----

            using (BeginPvpTemplateSystemOperation())
            {
                try
                {
                    // Crash invariant 1: written once, never over an existing record. Re-checked at the write itself.
                    if (IsPvpTemplated)
                        return RefuseApply(PvpTemplateText.ApplyAlreadyTemplated, matchId, "a restore record appeared before the write");

                    SetProperty(PropertyString.PvpTemplateRestore, recordJson);

                    PvpTemplateOverlay.ApplyBuild(Biota, BiotaDatabaseLock, definition);

                    RemoveEnchantmentsForPvpTemplate(PvpTemplateOverlay.NonItemEnchantments(Biota, BiotaDatabaseLock), silent: false);

                    PushPvpTemplateBuild(definition.Skills, record.AddedSpells, removedSpells: null);

                    ChangesDetected = true;
                    PvpTemplateSettings.SaveBiota(this);
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): writing template {definition.Key} for match {matchId} threw; restoring.", ex);
                    return FailAfterStart(matchId, "build write threw");
                }

                // ---- strip own gear (synchronous once IsBusy is false - see TryStripForFacetSwitch) ----

                if (!TryStripForFacetSwitch(out var stripRefusal))
                {
                    PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): strip failed entering template {definition.Key} for match {matchId}: {stripRefusal}");
                    return FailAfterStart(matchId, "strip failed");
                }

                // ---- issue and equip the kit ----

                try
                {
                    foreach (var kitItem in definition.Kit)
                    {
                        if (!TryIssuePvpTemplateKitItem(kitItem, weenies[kitItem.WeenieClassId], out var issueError))
                        {
                            PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): could not issue kit item wcid {kitItem.WeenieClassId} of template {definition.Key}: {issueError}");
                            return FailAfterStart(matchId, "kit issue failed");
                        }
                    }

                    // ---- template buffs, then full vitals (the maximum depends on the buffs and the kit) ----

                    var buffs = PvpTemplateOverlay.BuildTemplateBuffs(definition, Guid.Full);
                    AddEnchantmentsForPvpTemplate(buffs, silent: false);

                    foreach (var vital in new[] { Health, Stamina, Mana })
                        UpdateVital(vital, vital.MaxValue);

                    var backstop = PvpTemplateRunEquippedBackstop();

                    if (backstop.Survivors > 0)
                    {
                        PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): {backstop.Survivors} personal item(s) are still worn after the strip and the backstop entering template {definition.Key}; restoring.");
                        return FailAfterStart(matchId, "personal gear still worn");
                    }

                    ChangesDetected = true;
                    PvpTemplateSettings.SaveBiota(this);
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): finishing template {definition.Key} for match {matchId} threw; restoring.", ex);
                    return FailAfterStart(matchId, "kit or buffs threw");
                }
            }

            pvpTemplateUnboundSinceUtc = null;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(PvpTemplateText.Applied, ("template", definition.DisplayName ?? definition.Key)), ChatMessageType.Broadcast));

            PvpTemplateSettings.Log.Info($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): applied template {definition.Key} v{definition.Version} for match {matchId}; record {recordBytes} bytes, {record.OwnEquip.Count} own items stripped, {definition.Kit.Count} kit items issued, {record.RemovedEnchantments.Count} own enchantments held.");

            return PvpTemplateApplyResult.Ok();
        }

        /// <summary>Every distinct kit weenie of <paramref name="definition"/>, from the world cache. False names the first missing wcid.</summary>
        private static bool TryResolvePvpTemplateKitWeenies(PvpTemplateDefinition definition, out Dictionary<uint, Weenie> weenies, out uint missingWcid)
        {
            weenies = new Dictionary<uint, Weenie>();
            missingWcid = 0;

            foreach (var kitItem in definition.Kit)
            {
                if (weenies.ContainsKey(kitItem.WeenieClassId))
                    continue;

                var weenie = DatabaseManager.World.GetCachedWeenie(kitItem.WeenieClassId);

                if (weenie == null)
                {
                    missingWcid = kitItem.WeenieClassId;
                    return false;
                }

                weenies[kitItem.WeenieClassId] = weenie;
            }

            return true;
        }

        /// <summary>
        /// The pack-room part of the apply precheck for <paramref name="definition"/>, against this player's current
        /// worn set and free slots: the worn set is stripped into the MAIN pack and the kit's pack items are issued
        /// alongside it. The same arithmetic the apply itself runs, shared so the coordinator's join and accept
        /// prechecks (Phase C) can never disagree with it.
        /// </summary>
        private string PvpTemplateRoomRefusal(PvpTemplateDefinition definition, IReadOnlyDictionary<uint, Weenie> weenies)
        {
            var equipped = EquippedObjects.Values.ToList();

            // Worn kit items that will predictably fail to equip fall back to the main pack (TryIssuePvpTemplateKitItem),
            // so they need room there too.
            var wornFallbacks = CountPvpTemplateKitWornFallbacks(definition.Kit
                .Where(k => k.WieldLocation.HasValue)
                .Select(k => (k.WieldLocation.Value, KitValidLocations(k, weenies[k.WeenieClassId]))));

            return ComposePvpTemplateRoomRefusal(
                equipped.Count(i => !i.UseBackpackSlot) + wornFallbacks + definition.Kit.Count(k => k.WieldLocation == null && !weenies[k.WeenieClassId].RequiresBackpackSlotOrIsContainer()),
                equipped.Count(i => i.UseBackpackSlot) + definition.Kit.Count(k => k.WieldLocation == null && weenies[k.WeenieClassId].RequiresBackpackSlotOrIsContainer()),
                GetFreeInventorySlots(includeSidePacks: false),
                GetFreeContainerSlots());
        }

        /// <summary>
        /// Phase C's join and accept precheck (TEMPLATES.md "Lifecycle": "pack room for own equipped count + issued
        /// kit"): null when this player has room to enter a match on <paramref name="definition"/>, else the
        /// player-facing refusal. A kit weenie missing from the world database refuses too, since the apply would.
        /// Called on the world thread; it only reads counts, and the apply re-checks for real on the player's queue.
        /// </summary>
        public string CheckPvpTemplateRoom(PvpTemplateDefinition definition)
        {
            if (definition == null)
                return PvpTemplateText.TemplateCannotBeIssued;

            if (!TryResolvePvpTemplateKitWeenies(definition, out var weenies, out var missingWcid))
            {
                PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] template {definition.Key} v{definition.Version} names kit weenie {missingWcid}, which does not exist; it cannot be issued.");
                return PvpTemplateText.TemplateCannotBeIssued;
            }

            return PvpTemplateRoomRefusal(definition, weenies);
        }

        /// <summary>
        /// The remembered /arena template choice (PropertyString 9023), persisted on the character's biota so it
        /// survives a relog. Null or empty removes it. Only a key the caller has validated is ever written here; a key
        /// that later stops being offered simply refuses the next join.
        /// </summary>
        public string PvpTemplatePreference
        {
            get => GetProperty(PropertyString.PvpTemplatePreference);
            set
            {
                if (string.IsNullOrEmpty(value))
                    RemoveProperty(PropertyString.PvpTemplatePreference);
                else
                    SetProperty(PropertyString.PvpTemplatePreference, value);
            }
        }

        /// <summary>
        /// Puts the template's buffs back after a battleground respawn (TEMPLATES.md "Lifecycle", respawn row). Only a
        /// template buff whose SpellId is ABSENT from the registry is added (PvpTemplateOverlay.AbsentBySpellId, read
        /// under BiotaDatabaseLock): with pvp_arena_death_keeps_enchantments on (the default) the buffs survive the death
        /// and nothing is added - never a second row alongside, never a refreshed duration; with it off the death purge
        /// removed them and they all come back; a buff that expired mid-match comes back on its own. The restore removes
        /// every non-item enchantment, so nothing added here survives it. No-op unless templated with a frozen definition
        /// on the binding. Player queue only.
        /// </summary>
        internal void ReapplyPvpTemplateBuffsAfterRespawn()
        {
            var template = PvpBinding?.Template;

            if (!IsPvpTemplated || template == null)
                return;

            try
            {
                var absent = PvpTemplateOverlay.AbsentBySpellId(Biota, BiotaDatabaseLock, PvpTemplateOverlay.BuildTemplateBuffs(template, Guid.Full));

                if (absent.Count == 0)
                    return;

                using (BeginPvpTemplateSystemOperation())
                    AddEnchantmentsForPvpTemplate(absent, silent: false);
            }
            catch (Exception ex)
            {
                PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): re-applying template {template.Key} buffs after a respawn threw.", ex);
            }
        }

        /// <summary>
        /// The death exit (TEMPLATES.md "Lifecycle": "start of the arrival action in ThreadSafeTeleportOnDeath, before
        /// vitals reset"). Called first thing in that arrival action, so the 75% vitals the death path then sets are
        /// computed from the player's OWN restored maximums. Idempotent (no record: nothing happens), so the
        /// coordinator's later ExitPvpMatch for the same death is a no-op restore. Has its own try/catch: a restore
        /// failure must never stop the rest of the death sequence (the record is kept and the player is inert).
        /// </summary>
        internal void PvpTemplateDeathArrival(bool respawnsInMatch = false)
        {
            // A battleground pen death keeps the template: the player is still in the match and respawns on it. Only a
            // death that ends participation (an arena death, a battleground death outside Live) restores here.
            if (respawnsInMatch)
                return;

            try
            {
                PvpTemplateSettings.Restore(this, "match death");
            }
            catch (Exception ex)
            {
                PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): the death-arrival restore threw; any record is kept.", ex);
            }
        }

        private PvpTemplateApplyResult RefuseApply(string text, Guid matchId, string why, bool playerCaused = false, bool busy = false, bool announce = true)
        {
            if (announce)
                Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            PvpTemplateSettings.Log.Info($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): template apply refused for match {matchId}: {why}{(playerCaused ? " (player caused)" : "")}.");

            return PvpTemplateApplyResult.Fail(text, playerCaused: playerCaused, busy: busy);
        }

        private PvpTemplateApplyResult FailAfterStart(Guid matchId, string why)
        {
            var restored = RestorePvpTemplateNow($"apply failed for match {matchId}: {why}", silent: false, announce: false);
            var still = IsPvpTemplated;

            var text = still ? PvpTemplateText.RestoreFailed : PvpTemplateText.ApplyFailedAfterStart;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): template apply for match {matchId} failed after the record was written ({why}); restore {(restored ? "succeeded" : "did NOT clear the record - player is inert")}.");

            return PvpTemplateApplyResult.Fail(text, still);
        }

        /// <summary>
        /// The apply's room precheck, as a pure function (ACE.Server.Tests cannot read a capacity off a live
        /// Player). The worn set is stripped into the MAIN pack only (see ComposeStripRoomRefusal for why a side
        /// pack cannot take it), and the kit's pack items are issued alongside it.
        /// </summary>
        internal static string ComposePvpTemplateRoomRefusal(int mainPackItemsNeeded, int packSlotItemsNeeded, int freeMainPackSlots, int freeContainerSlots)
        {
            if (freeMainPackSlots < mainPackItemsNeeded)
            {
                var needed = mainPackItemsNeeded - freeMainPackSlots;
                return PvpArenaText.Fill(PvpTemplateText.ApplyNoRoom, ("needed", needed), ("plural", needed == 1 ? "" : "s"));
            }

            if (freeContainerSlots < packSlotItemsNeeded)
            {
                var needed = packSlotItemsNeeded - freeContainerSlots;
                return PvpArenaText.Fill(PvpTemplateText.ApplyNoPackSlotRoom, ("needed", needed), ("plural", needed == 1 ? "" : "s"));
            }

            return null;
        }

        /// <summary>
        /// How many worn kit items will predictably NOT equip and so fall back to the main pack: a location of 0, a
        /// location outside the item's ValidLocations, or a location that overlaps one an earlier kit item already
        /// claimed. Pure, for the room precheck and the tests. <paramref name="worn"/> is in kit order.
        /// </summary>
        internal static int CountPvpTemplateKitWornFallbacks(IEnumerable<(int Location, int? ValidLocations)> worn)
        {
            var claimed = 0;
            var fallbacks = 0;

            foreach (var (location, valid) in worn)
            {
                if (location == 0 || (valid.HasValue && (location & ~valid.Value) != 0) || (location & claimed) != 0)
                {
                    fallbacks++;
                    continue;
                }

                claimed |= location;
            }

            return fallbacks;
        }

        /// <summary>The kit item's ValidLocations: its own captured value, else its weenie's, else unknown.</summary>
        private static int? KitValidLocations(PvpTemplateKitItem kitItem, Weenie weenie)
        {
            if (kitItem.Ints != null && kitItem.Ints.TryGetValue((int)PropertyInt.ValidLocations, out var own))
                return own;

            return weenie?.GetProperty(PropertyInt.ValidLocations);
        }

        /// <summary>
        /// Clones one kit item with a fresh guid and the issued stamps, then equips it directly (no wield
        /// requirement check, TEMPLATES.md "Issued kit") or issues it into the pack. A worn item that will not
        /// equip falls back to the pack; one that fits nowhere is destroyed and reported as a failure.
        /// </summary>
        private bool TryIssuePvpTemplateKitItem(PvpTemplateKitItem kitItem, Weenie weenie, out string error)
        {
            error = null;

            var guid = GuidManager.NewDynamicGuid();

            var weenieBase = ACE.Entity.Adapter.WeenieConverter.ConvertToBiota(weenie, guid.Full, false, false);
            var biota = PvpTemplateKit.BuildIssuedBiota(kitItem, guid.Full, Guid.Full, weenieBase);

            var item = WorldObjectFactory.CreateWorldObject(biota);

            if (item == null)
            {
                GuidManager.RecycleDynamicGuid(guid);
                error = "the factory built nothing";
                return false;
            }

            // Equip first: TryEquipWithdrawnForFacet equips an unparented object and only then introduces it to the
            // client with one GameMessageCreateObject describing its worn state, and saves it - exactly the shape a
            // freshly built object needs (see its remarks for why the pack round trip is wrong). It does NOT run
            // CheckWieldRequirements, which is what the kit wants.
            if (kitItem.WieldLocation.HasValue && TryEquipWithdrawnForFacet(item, (EquipMask)kitItem.WieldLocation.Value))
                return true;

            if (TryCreateInInventoryWithNetworking(item))
                return true;

            item.Destroy();
            error = "no room to equip or carry it";
            return false;
        }

        // ======================================================================================
        // restore
        // ======================================================================================

        /// <summary>
        /// Every exit's one call (TEMPLATES.md "Lifecycle"): match end, /arena leave, leaving the instance, admin
        /// cancel, death, logout and the heartbeat backstop. Idempotent - does nothing and returns false when there
        /// is no record. Must run on the player's action queue; <see cref="RestorePvpTemplateQueued"/> is the
        /// form for a caller that is not already there.
        /// </summary>
        public bool RestorePvpTemplate(string reason) => RestorePvpTemplateNow(reason, silent: false, announce: true);

        /// <summary>Queues <see cref="RestorePvpTemplate"/> on the player's action queue, with its own try/catch.</summary>
        public void RestorePvpTemplateQueued(string reason)
        {
            EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    RestorePvpTemplate(reason);
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): queued restore ({reason}) threw.", ex);
                }
            }));
        }

        /// <summary>
        /// The restore body, in the lifecycle's order: destroy every issued possession -> restore the build,
        /// enchantments and spells -> remove the record -> save -> re-equip own gear. <paramref name="silent"/> is
        /// the login form, run before PlayerEnterWorld: nothing is sent (the login PlayerDescription carries the
        /// restored state) and the gear re-equip is deferred until the player is in the world.
        ///
        /// A throw keeps the record (crash invariant 4): every gate keys on it, so the player is inert, never open,
        /// until /pvptemplate restore replays this - which is safe, because every write is absolute (invariant 3).
        /// </summary>
        internal bool RestorePvpTemplateNow(string reason, bool silent, bool announce)
        {
            if (!IsPvpTemplated)
                return false;

            if (!TryGetPvpTemplateRecord(out var record, out var error))
            {
                PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): restore ({reason}) cannot read the restore record ({error}); the record is KEPT and the player stays inert until staff run /pvptemplate restore.");

                if (!silent && announce)
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpTemplateText.RestoreFailed, ChatMessageType.Broadcast));

                return false;
            }

            using (BeginPvpTemplateSystemOperation())
            {
                try
                {
                    // Out of combat stance before any issued weapon comes off, as TryStripForFacetSwitch does before
                    // its own strip: dequipping a wielded weapon mid-swing or mid-cast leaves the stance and the
                    // attack/cast state pointing at an object that is about to be destroyed.
                    if (!silent && Session != null)
                        LeaveCombatStanceForPvpTemplate();

                    var destroyed = DestroyPvpTemplateIssuedPossessions(silent, out var remaining);

                    // Crash invariant 4: an issued item that is still possessed means the restore did NOT finish.
                    // Throwing here keeps the record, so the player is inert (never open with template gear) and the
                    // next restore - the login sweep, the heartbeat, or /pvptemplate restore - retries the destroy.
                    if (remaining > 0)
                        throw new InvalidOperationException($"{remaining} issued item(s) could not be destroyed");

                    PvpTemplateOverlay.RestoreBuild(Biota, BiotaDatabaseLock, record);

                    RemoveEnchantmentsForPvpTemplate(PvpTemplateOverlay.NonItemEnchantments(Biota, BiotaDatabaseLock), silent);
                    AddEnchantmentsForPvpTemplate(PvpTemplateOverlay.PlanRestoredEnchantments(record), silent);

                    if (!silent)
                        PushPvpTemplateBuild(record.Skills, addedSpells: null, removedSpells: record.AddedSpells);

                    // The saved current vitals come back as they were (RestoreBuild wrote them). In the live form they
                    // are clamped to the restored maximum and pushed here. The silent (login) form clamps after the
                    // own gear is back on in the world (SendPvpTemplateLoginLine), because GearMaxHealth is part of
                    // the maximum and the gear is still in the pack at this point.
                    if (!silent)
                        ClampAndPushVitalsForPvpTemplate();

                    RemoveProperty(PropertyString.PvpTemplateRestore);
                    pvpTemplateRecordCacheJson = null;
                    pvpTemplateRecordCache = null;
                    pvpTemplateUnboundSinceUtc = null;

                    ChangesDetected = true;
                    PvpTemplateSettings.SaveBiota(this);

                    PvpTemplateSettings.Log.Info($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): restored from template {record.TemplateKey} v{record.TemplateVersion} (match {record.MatchId}, {reason}); {destroyed} issued item(s) destroyed, {record.RemovedEnchantments.Count} own enchantment(s) held{(record.ReturnOwnBuffs ? "" : ", own buffs not returned (setting)")}.");
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): restore ({reason}) threw; the record is KEPT and the player stays inert until staff run /pvptemplate restore.", ex);

                    if (!silent && announce)
                        Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpTemplateText.RestoreFailed, ChatMessageType.Broadcast));

                    return false;
                }

                // Own gear goes back on last, outside the try: a gear report is never a reason to call the restore
                // failed, and the record is already gone. The silent form defers it until the player is in the world.
                if (!silent)
                {
                    var report = RestorePvpTemplateOwnEquip(record.OwnEquip);
                    ReportPvpTemplateGear(report);
                }
            }

            if (!silent && announce)
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpTemplateText.Restored, ChatMessageType.Broadcast));

            return true;
        }

        private void ReportPvpTemplateGear(List<string> report)
        {
            if (report == null || report.Count == 0)
                return;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(PvpTemplateText.GearNotRestored, ("details", string.Join(" ", report))), ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Destroys every possession that itself carries the issued mark, keyed on the mark ALONE (crash invariant 2).
        /// A worn item is dequipped first, which takes its item spells off the player; the silent form removes those
        /// registry rows by caster guid instead, because the networked dequip would talk to a client that is not in
        /// the world yet. Returns how many were destroyed.
        /// </summary>
        internal int DestroyPvpTemplateIssuedPossessions(bool silent) => DestroyPvpTemplateIssuedPossessions(silent, out _);

        /// <summary>
        /// The destroy, also reporting <paramref name="remaining"/>: how many issued items were NOT destroyed (a
        /// per-item failure, or one still found among the possessions afterwards). A restore treats any remainder as
        /// failure and keeps the record. With no session there is no client to network to, so the silent form runs
        /// whatever <paramref name="silent"/> says.
        /// </summary>
        internal int DestroyPvpTemplateIssuedPossessions(bool silent, out int remaining)
        {
            silent = silent || Session == null;

            var issued = GetAllPossessions().Where(PvpTemplate.IsMarkedIssued).ToList();

            var destroyed = 0;

            foreach (var item in issued)
            {
                try
                {
                    var worn = EquippedObjects.ContainsKey(item.Guid);

                    if (silent)
                    {
                        WorldObject removed;

                        if (worn)
                        {
                            removed = PvpTemplateSettings.SilentDequip(this, item.Guid);

                            if (removed == null)
                                continue;

                            var granted = Biota.PropertiesEnchantmentRegistry?.Clone(BiotaDatabaseLock)
                                .Where(e => PvpTemplateOverlay.IsItemGranted(e) && e.CasterObjectId == item.Guid.Full).ToList();

                            PvpTemplateOverlay.RemoveEnchantments(Biota, BiotaDatabaseLock, granted);
                            EnchantmentManager.InvalidateCaches();
                        }
                        else if (!TryRemoveFromInventory(item.Guid, out removed))
                            continue;

                        PvpTemplateSettings.DestroyItem(removed);
                        destroyed++;
                    }
                    else
                    {
                        var ok = worn
                            ? TryDequipObjectWithNetworking(item.Guid, out _, DequipObjectAction.ConsumeItem)
                            : TryConsumeFromInventoryWithNetworking(item);

                        if (ok)
                            destroyed++;
                    }
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): destroying issued item 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) threw.", ex);
                }
            }

            // Two views of the same failure, and the larger one wins: an item whose destroy threw after it was
            // detached is no longer among the possessions (so only the count sees it), and an item a failed dequip
            // left in place is still possessed (so the rescan sees it).
            var stillPossessed = GetAllPossessions().Count(PvpTemplate.IsMarkedIssued);

            remaining = Math.Max(issued.Count - destroyed, stillPossessed);

            if (remaining > 0)
                PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): destroyed {destroyed} of {issued.Count} issued item(s), {stillPossessed} still possessed; a restore keeps the record and the next restore or login sweep retries.");

            if (destroyed > 0)
                ChangesDetected = true;

            return destroyed;
        }

        /// <summary>
        /// Out of combat stance before the restore dequips issued gear. Mirrors TryStripForFacetSwitch's own
        /// cleanup (fail an in-progress cast, cancel the attack, set NonCombat directly rather than through
        /// HandleActionChangeCombatMode, which can enqueue a delayed chain).
        /// </summary>
        private void LeaveCombatStanceForPvpTemplate()
        {
            if (CombatMode == CombatMode.NonCombat)
                return;

            if (CombatMode == CombatMode.Magic && MagicState.IsCasting)
                FailCast();

            HandleActionCancelAttack();

            SetCombatMode(CombatMode.NonCombat);
        }

        /// <summary>
        /// Puts the player's own worn set back on after a template restore. Unlike RestoreFacetEquip this NEVER
        /// touches the account vault: the own set was stripped into the pack, so a remembered item that is not in
        /// the pack is reported, never substituted by a same-wcid vault withdrawal. An entry that is already worn
        /// (a crash between the record write and the strip leaves the own set on) is satisfied as is.
        /// </summary>
        internal List<string> RestorePvpTemplateOwnEquip(List<ACE.Server.Entity.Facets.FacetEquipEntry> remembered)
        {
            var report = new List<string>();

            if (remembered == null)
                return report;

            foreach (var entry in remembered)
            {
                if (entry == null)
                    continue;

                var guid = new ObjectGuid(entry.Guid);

                if (EquippedObjects.ContainsKey(guid))
                    continue;

                var item = FindObject(guid, SearchLocations.MyInventory, out _, out var rootOwner, out _);

                if (item == null)
                {
                    report.Add($"Could not find {FacetItemLabel(entry.Wcid, null)}.");
                    continue;
                }

                var wieldError = CheckWieldRequirements(item, false);

                if (wieldError != WeenieError.None)
                {
                    report.Add($"{item.NameWithMaterial} no longer meets its wield requirements ({wieldError}).");
                    continue;
                }

                TryDetachAndEquipForFacet(rootOwner, item, (EquipMask)entry.Slot, report);
            }

            return report;
        }

        // ======================================================================================
        // client pushes and enchantment edits shared by apply and restore
        // ======================================================================================

        /// <summary>
        /// Pushes a freshly written build to the client: every skill (through ApplyFacetSkills, which also re-asserts
        /// the authoritative sweep), the six attributes and three vitals (through ApplyFacetAttributes, with the
        /// innate values already written so it changes nothing and only pushes), every replaced PropertyInt as a
        /// private update (0 for an absent property, as the 2026-10-03 probe sent), and the spellbook delta.
        /// </summary>
        private void PushPvpTemplateBuild(List<ACE.Server.Entity.Facets.FacetSkillEntry> skills, List<int> addedSpells, List<int> removedSpells)
        {
            ApplyFacetSkills(skills);

            var innate = new Dictionary<PropertyAttribute, uint>();

            foreach (var attribute in ACE.Server.Entity.Facets.FacetAttributes.PrimaryAttributes)
                innate[attribute] = Attributes[attribute].StartingValue;

            ApplyFacetAttributes(innate);

            if (Session == null)
                return;

            var updates = new List<GameMessage>();

            foreach (var property in PvpTemplatePowerProperties.ReplacedInts)
                updates.Add(new GameMessagePrivateUpdatePropertyInt(this, property, GetProperty(property) ?? 0));

            if (addedSpells != null)
            {
                foreach (var spell in addedSpells)
                    updates.Add(new GameEventMagicUpdateSpell(Session, (ushort)spell));
            }

            if (removedSpells != null)
            {
                foreach (var spell in removedSpells)
                    updates.Add(new GameEventMagicRemoveSpell(Session, (ushort)spell));
            }

            if (updates.Count > 0)
                Session.Network.EnqueueSend(updates.ToArray());

            if (PvpTemplateSettings.RunRateHooksSource())
                HandleRunRateUpdate();
        }

        /// <summary>
        /// Removes registry rows directly (so a cooldown row needs no Spell lookup), drops the enchantment caches,
        /// and - unless silent - tells the client with one silent-dispel message and runs the max-vital and run-rate
        /// hooks for each real spell.
        /// </summary>
        private void RemoveEnchantmentsForPvpTemplate(List<PropertiesEnchantmentRegistry> entries, bool silent)
        {
            if (entries == null || entries.Count == 0)
                return;

            PvpTemplateOverlay.RemoveEnchantments(Biota, BiotaDatabaseLock, entries);
            EnchantmentManager.InvalidateCaches();
            ChangesDetected = true;

            if (silent || Session == null)
                return;

            Session.Network.EnqueueSend(new GameEventMagicDispelMultipleEnchantments(Session, PvpTemplateOverlay.ToDispelWire(entries)));

            foreach (var entry in entries.Where(e => !PvpTemplateOverlay.IsCooldown(e)))
                HandleSpellHooks(new Spell(entry.SpellId));
        }

        /// <summary>
        /// Adds registry rows at their own layer where free (PvpTemplateOverlay.AddEnchantmentsPreservingLayer), drops
        /// the caches, and - unless silent - sends them to the client and runs the spell hooks.
        /// </summary>
        private void AddEnchantmentsForPvpTemplate(List<PropertiesEnchantmentRegistry> entries, bool silent)
        {
            if (entries == null || entries.Count == 0)
                return;

            var added = PvpTemplateOverlay.AddEnchantmentsPreservingLayer(Biota, BiotaDatabaseLock, entries);
            EnchantmentManager.InvalidateCaches();
            ChangesDetected = true;

            if (silent || Session == null || added.Count == 0)
                return;

            Session.Network.EnqueueSend(new GameEventMagicUpdateMultipleEnchantments(Session, added.Select(e => new ACE.Server.Network.Structure.Enchantment(this, e)).ToList()));

            foreach (var entry in added.Where(e => !PvpTemplateOverlay.IsCooldown(e)))
                HandleSpellHooks(new Spell(entry.SpellId));
        }

        /// <summary>Clamps each current vital to its maximum and pushes all three (Player.UpdateVital sends the update).</summary>
        private void ClampAndPushVitalsForPvpTemplate()
        {
            foreach (var vital in new[] { Health, Stamina, Mana })
                UpdateVital(vital, Math.Min(vital.Current, vital.MaxValue));
        }
        // ======================================================================================
        // backstops
        // ======================================================================================

        /// <summary>
        /// The equipped-item backstop (TEMPLATES.md "Backstop"): while templated, every worn item that is not issued
        /// is a gate miss. Each is moved to the pack (through the system bypass) and logged at warn level naming the
        /// item and the player. Returns how many were ACTUALLY moved - recounted afterwards, never assumed - so the
        /// coordinator can note on the match record that the backstop fired. Runs at apply; Phase C calls it (or
        /// <see cref="PvpTemplateRunEquippedBackstop"/>, which also reports survivors) on every match tick (battleground seats in the pen cycle are skipped).
        /// </summary>
        public int PvpTemplateCheckEquippedBackstop() => PvpTemplateRunEquippedBackstop().Moved;

        /// <summary>
        /// The backstop with its full outcome. <see cref="PvpTemplateBackstopResult.Survivors"/> counts personal
        /// items STILL worn after the move attempts (a full pack, or a move that did not resolve): the player is
        /// then fighting in own gear, and the caller must act - the apply fails and restores; the per-tick caller
        /// should remove the player from the match and restore.
        /// </summary>
        public PvpTemplateBackstopResult PvpTemplateRunEquippedBackstop()
        {
            if (!IsPvpTemplated)
                return new PvpTemplateBackstopResult(0, 0);

            var personal = EquippedObjects.Values.Where(i => !PvpTemplate.IsMarkedIssued(i)).ToList();

            if (personal.Count == 0)
                return new PvpTemplateBackstopResult(0, 0);

            using (BeginPvpTemplateSystemOperation())
            {
                foreach (var item in personal)
                {
                    PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] GATE MISS: {Name} (0x{Guid.Full:X8}) is templated and wearing personal item 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}); moving it to the pack.");

                    try
                    {
                        PvpTemplateSettings.MoveWornItemToPack(this, item);
                    }
                    catch (Exception ex)
                    {
                        PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): moving personal item 0x{item.Guid.Full:X8} to the pack threw.", ex);
                    }
                }
            }

            // HandleActionPutItemInContainer returns nothing, and a full pack leaves the item worn, so what moved is
            // read back off EquippedObjects rather than assumed.
            var moved = 0;
            var survivors = 0;

            foreach (var item in personal)
            {
                if (EquippedObjects.ContainsKey(item.Guid))
                {
                    survivors++;
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): personal item 0x{item.Guid.Full:X8} ({item.Name}) is STILL worn after the backstop.");
                    continue;
                }

                moved++;
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(PvpTemplateText.BackstopMovedItem, ("item", item.NameWithMaterial ?? item.Name)), ChatMessageType.Broadcast));
            }

            return new PvpTemplateBackstopResult(moved, survivors);
        }

        /// <summary>
        /// Heartbeat backstop (TEMPLATES.md lifecycle, last row): templated with no live match for
        /// pvp_template_backstop_seconds -> restore. Skipped while teleporting, so a death or exit teleport in flight
        /// is never raced; the next beat catches it.
        /// </summary>
        internal void PvpTemplateHeartbeat()
        {
            if (!IsPvpTemplated && pvpTemplateUnboundSinceUtc == null)
                return;

            PvpTemplateHeartbeatCore(DateTime.UtcNow, IsInPvpMatch, Teleporting);
        }

        /// <summary>
        /// The heartbeat's decision and action, with the clock and the match/teleport state passed in so the tests
        /// can drive it. Returns true when a restore was attempted. A restore that FAILS (the record is kept) is not
        /// retried until <see cref="PvpTemplateSettings.HeartbeatRetryBackoff"/> has passed, and the heartbeat never
        /// announces, so a stuck player is not hit with a stack trace and a chat line every beat. An explicit
        /// RestorePvpTemplate (an exit, /pvptemplate restore) is not subject to the backoff.
        /// </summary>
        internal bool PvpTemplateHeartbeatCore(DateTime nowUtc, bool inMatch, bool teleporting)
        {
            var templated = IsPvpTemplated;

            if (!templated)
            {
                pvpTemplateUnboundSinceUtc = null;
                pvpTemplateHeartbeatRetryAfterUtc = null;
                return false;
            }

            if (teleporting)
                return false;

            if (!PvpTemplateBackstop.ShouldRestore(templated, inMatch, ref pvpTemplateUnboundSinceUtc, nowUtc, PvpTemplateSettings.BackstopDelay()))
                return false;

            if (pvpTemplateHeartbeatRetryAfterUtc.HasValue && nowUtc < pvpTemplateHeartbeatRetryAfterUtc.Value)
                return false;

            PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): templated with no live match for {PvpTemplateSettings.BackstopDelay().TotalSeconds:0}s - heartbeat backstop restoring.");

            if (RestorePvpTemplateNow("heartbeat backstop: no live match", silent: false, announce: false) || !IsPvpTemplated)
            {
                pvpTemplateHeartbeatRetryAfterUtc = null;
                return true;
            }

            pvpTemplateHeartbeatRetryAfterUtc = nowUtc + PvpTemplateSettings.HeartbeatRetryBackoff;

            PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): heartbeat restore failed and the record is kept; next heartbeat attempt no sooner than {PvpTemplateSettings.HeartbeatRetryBackoff.TotalSeconds:0}s from now.");

            return true;
        }

        // ======================================================================================
        // login
        // ======================================================================================

        /// <summary>
        /// Login (TEMPLATES.md lifecycle, crash row). Called from WorldManager beside RestorePvpMatchStatusAtLogin,
        /// BEFORE PlayerEnterWorld, so the PlayerDescription that login sends already carries the restored build and
        /// nothing has to be pushed (or resent) mid-session.
        ///
        ///   1. The sweep: every possession marked PvpTemplateIssued is destroyed, keyed on the mark ALONE - whether
        ///      or not a record exists (crash invariant 2).
        ///   2. With a record: the silent restore. Own gear goes back on after the player is in the world.
        ///   3. A record that cannot be restored is KEPT: the player logs in, inert, and is told to contact staff.
        /// </summary>
        public void RestorePvpTemplateAtLogin()
        {
            var outcome = RestorePvpTemplateAtLoginCore();

            switch (outcome.Kind)
            {
                case PvpTemplateLoginOutcomeKind.Swept:
                    SendPvpTemplateLoginLine(PvpArenaText.Fill(PvpTemplateText.IssuedItemsSwept, ("count", outcome.Swept), ("plural", outcome.Swept == 1 ? "" : "s")), null, clampVitals: false);
                    break;
                case PvpTemplateLoginOutcomeKind.Restored:
                    SendPvpTemplateLoginLine(PvpTemplateText.RestoredAtLogin, outcome.OwnEquip, clampVitals: true);
                    break;
                case PvpTemplateLoginOutcomeKind.Inert:
                    SendPvpTemplateLoginLine(PvpTemplateText.InertAtLogin, null, clampVitals: false);
                    break;
            }
        }

        internal enum PvpTemplateLoginOutcomeKind
        {
            /// <summary>No record and nothing issued: nothing happened.</summary>
            Clean,

            /// <summary>No record, but issued items were found and destroyed.</summary>
            Swept,

            /// <summary>A record was found and the silent restore removed it.</summary>
            Restored,

            /// <summary>A record was found and could not be restored: it is kept, the player is inert.</summary>
            Inert,
        }

        internal readonly struct PvpTemplateLoginOutcome
        {
            public PvpTemplateLoginOutcome(PvpTemplateLoginOutcomeKind kind, int swept, List<ACE.Server.Entity.Facets.FacetEquipEntry> ownEquip)
            {
                Kind = kind;
                Swept = swept;
                OwnEquip = ownEquip;
            }

            public PvpTemplateLoginOutcomeKind Kind { get; }

            public int Swept { get; }

            /// <summary>The own worn set to re-equip once in the world (Restored only).</summary>
            public List<ACE.Server.Entity.Facets.FacetEquipEntry> OwnEquip { get; }
        }

        /// <summary>
        /// The pre-PlayerEnterWorld half of <see cref="RestorePvpTemplateAtLogin"/>, with nothing scheduled and
        /// nothing sent, so ACE.Server.Tests can drive it on a seeded Player: the sweep (keyed on the mark alone),
        /// then the silent restore when a record exists.
        /// </summary>
        internal PvpTemplateLoginOutcome RestorePvpTemplateAtLoginCore()
        {
            int swept, sweepRemaining;

            using (BeginPvpTemplateSystemOperation())
                swept = DestroyPvpTemplateIssuedPossessions(silent: true, out sweepRemaining);

            // An item whose destroy threw after it was detached is no longer possessed, so the restore's own rescan
            // could not see it: the sweep's own failure count decides. Keep the record (inert) rather than restore
            // around an issued item that may still exist.
            if (sweepRemaining > 0 && IsPvpTemplated)
            {
                PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): login sweep could not destroy {sweepRemaining} issued item(s); the restore record is KEPT and the player is inert.");
                return new PvpTemplateLoginOutcome(PvpTemplateLoginOutcomeKind.Inert, swept, null);
            }

            if (!IsPvpTemplated)
            {
                if (swept == 0)
                    return new PvpTemplateLoginOutcome(PvpTemplateLoginOutcomeKind.Clean, 0, null);

                PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): login sweep destroyed {swept} issued item(s) with no restore record present.");
                ChangesDetected = true;
                PvpTemplateSettings.SaveBiota(this);

                return new PvpTemplateLoginOutcome(PvpTemplateLoginOutcomeKind.Swept, swept, null);
            }

            TryGetPvpTemplateRecord(out var record, out _);
            var ownEquip = record?.OwnEquip;

            if (!RestorePvpTemplateNow("login", silent: true, announce: false))
                return new PvpTemplateLoginOutcome(PvpTemplateLoginOutcomeKind.Inert, swept, null);

            PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): login found a template restore record and restored it ({swept} issued item(s) swept).");

            return new PvpTemplateLoginOutcome(PvpTemplateLoginOutcomeKind.Restored, swept, ownEquip);
        }
        /// <summary>
        /// After the player is in the world: re-equips the own worn set (when given) through the system bypass, and
        /// sends the line. The same 3-second delay RestorePvpMatchStatusAtLogin uses, so the client has the inventory.
        /// </summary>
        private void SendPvpTemplateLoginLine(string line, List<ACE.Server.Entity.Facets.FacetEquipEntry> ownEquip, bool clampVitals)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(3.0f);
            chain.AddAction(this, () =>
            {
                try
                {
                    if (ownEquip != null && ownEquip.Count > 0)
                    {
                        using (BeginPvpTemplateSystemOperation())
                            ReportPvpTemplateGear(RestorePvpTemplateOwnEquip(ownEquip));
                    }

                    // After the gear, because GearMaxHealth is part of the maximum (see RestorePvpTemplateNow).
                    if (clampVitals)
                        ClampAndPushVitalsForPvpTemplate();

                    Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] {Name} (0x{Guid.Full:X8}): post-login template follow-up threw.", ex);
                }
            });
            chain.EnqueueChain();
        }
    }
}
