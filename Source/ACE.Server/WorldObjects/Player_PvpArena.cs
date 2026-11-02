using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rules;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The player side of the PvP arena (Docs/Pvp/DESIGN.md, "Core-file hooks" and "Enter and exit"): the
    /// per-player binding, the mask predicates every suppression site reads, and the enter/exit pair.
    ///
    /// INERT UNTIL C3. Nothing in this PR sets a binding - only the coordinator (PR C3) calls EnterPvpMatch.
    /// With no binding every predicate here is false, every combined predicate reduces to the PK facet term it
    /// replaced, the damage gate returns NotApplicable, and no death is latched as a match death, so behaviour
    /// is exactly master's.
    ///
    /// THREADING. The binding is an immutable object behind a volatile field, read lock-free from landblock
    /// threads (DESIGN "Reads from landblock threads"). Every change to the player - the status flips, the
    /// masks, the marker - runs on the player's own action queue, and each queued action carries its own
    /// try/catch, because an exception inside an EnqueueAction escapes the caller's try/catch.
    /// </summary>
    partial class Player
    {
        private volatile PvpPlayerBinding pvpBinding;

        /// <summary>The live binding, or null when this player is not in a match (every player, until C3 binds one).</summary>
        public PvpPlayerBinding PvpBinding => pvpBinding;

        public bool IsInPvpMatch => PvpPlayerRules.InMatch(pvpBinding);

        /// <summary>
        /// pvp_match_free_components: true while the player is in a match and the setting is on. Computed, never stored:
        /// SpellComponentsRequired is a saved PropertyBool, so writing it would let a crash mid-match leave the player
        /// casting free forever. The cast path (HasComponentsForSpell, TryBurnComponents) and the client sync read this.
        /// </summary>
        public bool PvpMatchComponentsWaived => IsInPvpMatch && PvpArenaHookSettings.MatchFreeComponentsSource();

        /// <summary>
        /// pvp_match_block_dual_wield (Docs/Pvp/TEMPLATES.md "Dual wield"): true when equipping <paramref name="itemIsShield"/>'s item into
        /// <paramref name="slot"/> must be refused for this player. "In a match" is a match binding OR a worn issued template kit (the
        /// logout path's own test, Player.cs: PvpBinding != null || IsPvpTemplated), so the kit-apply window before the binding is covered.
        /// The decision itself is the pure <see cref="PvpDualWieldRule.Refuses"/>. Computed, never stored; the setting is read per call.
        /// </summary>
        internal bool PvpDualWieldRefuses(EquipMask slot, bool itemIsShield)
            => PvpDualWieldRule.Refuses(IsInPvpMatch || IsPvpTemplated, PvpArenaHookSettings.MatchBlockDualWieldSource(), slot, itemIsShield);

        /// <summary>
        /// Clears a weapon the player holds in the off-hand when <see cref="PvpDualWieldRefuses"/> says it may not stay: a kit that
        /// contains one (called right after the kit is issued) or the player's own gear in a mode with no kit (called at match entry).
        /// Warns in the log naming <paramref name="templateKey"/>, and tells the player. A shield is never touched.
        ///   - An ISSUED item (<see cref="PvpTemplate.IsMarkedIssued"/>: at kit apply the player's own gear is already stripped, so any
        ///     off-hand weapon is the kit's) is consumed with DequipObjectAction.ConsumeItem. The restore destroys issued items anyway,
        ///     so this needs no pack room and cannot leave the rule defeated by a full pack.
        ///   - An unmarked (own) weapon goes to the pack. If the pack has no room, or anything throws after the dequip, it is re-equipped
        ///     the way Player_Facets.TryDetachAndEquipForFacet recovers: a throw is judged by HasEquippedItem, never assumed either way.
        /// Never throws. Player queue only.
        /// </summary>
        internal bool ClearPvpOffHandWeapon(string templateKey)
        {
            try
            {
                if (!PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false))
                    return false;

                var weapon = GetDualWieldWeapon();

                if (weapon == null)
                    return false;

                var name = weapon.Name;
                var issued = PvpTemplate.IsMarkedIssued(weapon);

                if (!TryDequipObjectWithNetworking(weapon.Guid, out var dequipped, issued ? DequipObjectAction.ConsumeItem : DequipObjectAction.DequipToPack))
                {
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): could not take the off-hand weapon {name} off (template {templateKey ?? "(none)"}); dual wielding stays on this player.");
                    return false;
                }

                if (issued)
                {
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.DualWieldDisabled, ChatMessageType.Broadcast));

                    PvpArenaHookSettings.Log.Warn($"[PVP] {Name} (0x{Guid.Full:X8}): the issued off-hand weapon {name} was removed because pvp_match_block_dual_wield is on (template {templateKey ?? "(none)"}); the restore would destroy it anyway.");

                    return true;
                }

                // An own weapon: to the pack. Everything after the dequip is guarded, because the item is in neither place until it lands.
                var inPack = false;

                try
                {
                    inPack = TryCreateInInventoryWithNetworking(dequipped, out _, burdenCheck: false);
                }
                catch (Exception ex)
                {
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): putting the off-hand weapon {name} in the pack threw (template {templateKey ?? "(none)"}).", ex);
                }

                if (!inPack)
                {
                    bool reEquipped;

                    try
                    {
                        reEquipped = TryEquipObjectWithNetworking(dequipped, EquipMask.Shield);
                    }
                    catch (Exception ex)
                    {
                        // As in TryDetachAndEquipForFacet: TryEquipObject runs first, so a throw usually means the item IS worn again.
                        PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): re-equipping the off-hand weapon {name} threw (template {templateKey ?? "(none)"}).", ex);
                        reEquipped = HasEquippedItem(dequipped.Guid);
                    }

                    if (reEquipped)
                        PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): no pack room for the off-hand weapon {name} (template {templateKey ?? "(none)"}); put back in the off-hand.");
                    else
                        PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the off-hand weapon {name} (0x{dequipped.Guid.Full:X8}) could not go to the pack AND could not be re-equipped (template {templateKey ?? "(none)"}). It is in NEITHER place - manual recovery required.");

                    return false;
                }

                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.DualWieldDisabled, ChatMessageType.Broadcast));

                PvpArenaHookSettings.Log.Warn($"[PVP] {Name} (0x{Guid.Full:X8}): off-hand weapon {name} moved to the pack because pvp_match_block_dual_wield is on (template {templateKey ?? "(none)"}).");

                return true;
            }
            catch (Exception ex)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): clearing the off-hand weapon (template {templateKey ?? "(none)"}) threw.", ex);
                return false;
            }
        }

        /// <summary>
        /// The SpellComponentsRequired value the client should hold right now: false while waived, else the player's real
        /// effective value. The one computation behind the login send, the match-bind send and the match-exit send.
        /// </summary>
        internal bool ClientSpellComponentsRequired => !PvpMatchComponentsWaived && SpellComponentsRequired && PvpArenaHookSettings.RequireSpellCompsSource();

        /// <summary>
        /// Teaches the player, for free, every level 7 War/Life spell the Omni-School Professor teaches plus Curse of Raven Fury
        /// that they do not already know (PvpMatchSpellGrant). Idempotent. Called at queue join (PvpMatchManager.Join) and again as a
        /// safety net just before the template restore record is captured (ApplyPvpTemplateNow), so a granted spell is already in the
        /// spellbook when Capture decides which template spells the exit must remove. NEVER call it while templated or inside a match
        /// bind: a spell granted after Capture that the template also names would be recorded as template-added and removed at exit.
        /// Never throws: a failure is logged and the caller carries on.
        /// </summary>
        internal void GrantPvpMatchSpells()
        {
            try
            {
                var taught = PvpMatchSpellGrant.Grant(
                    PvpArenaHookSettings.MatchGrantSpellsSource(),
                    PvpArenaHookSettings.MatchGrantSpellListSource,
                    AddKnownSpell,
                    id => Session?.Network.EnqueueSend(new GameEventMagicUpdateSpell(Session, (ushort)id)));

                if (taught > 0)
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpMatchSpellGrant.TaughtMessage(taught), ChatMessageType.Magic));
            }
            catch (Exception ex)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the free spell grant threw.", ex);
            }
        }

        /// <summary>Pushes ClientSpellComponentsRequired to the client (the match bind and exit sites).</summary>
        private void SendClientSpellComponentsRequired()
            => Session?.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyBool(this, PropertyBool.SpellComponentsRequired, ClientSpellComponentsRequired));

        /// <summary>
        /// WaffleACE: IP active-player limit. True while the player is in a live PvP arena match AND still
        /// standing inside one of the actual ephemeral PvP map instances the match occupies
        /// (the arena maps and the battleground maps, PvpMatchLandblocks, Docs/Pvp/DESIGN.md "Match spaces") - never just the persisted binding, since a
        /// player can carry a not-yet-torn-down binding for a few ticks after leaving. This is a landblock
        /// check, not an instance-id check: every match on a given map shares that map's landblock id but gets
        /// its own ephemeral instance, and IpLimitManager only needs "is this player physically inside an
        /// arena room right now", not which specific match.
        /// </summary>
        public bool IsInPvpArenaMatchSpace =>
            IsInPvpMatch
            && Location != null
            && Location.IsEphemeralRealm
            && PvpMatchLandblocks.IsPvpMapLandblock(Location.LandblockShort);

        public bool IsPvpClassAbilityMaskActive => PvpPlayerRules.ClassAbilityMask(pvpBinding);

        public bool IsPvpEquipmentModMaskActive => PvpPlayerRules.EquipmentModMask(pvpBinding);

        public bool IsPvpWeaponModMaskActive => PvpPlayerRules.WeaponModMask(pvpBinding);

        public bool IsPvpPickupBoonMaskActive => PvpPlayerRules.PickupBoonMask(pvpBinding);

        public bool IsPvpTurnSpeedMaskActive => PvpPlayerRules.TurnSpeedMask(pvpBinding);

        /// <summary>
        /// THE predicate every class ability read site reads (A1-A5: the hook caches, TryGetClassAbility, the
        /// Enhanced skill/attribute/vital bonuses, GetClassAbilityRating, and the two raw rank reads). No site
        /// reads IsPkFacetRuleActive or the binding directly (DESIGN "Must stay identical").
        ///
        /// NEVER applied to the rank cache itself (ClassAbilityPointsSpentOnOwnedRanks and its readers: the CAP
        /// ledger, facet pricing, the learn and token write paths). Masking that would corrupt their accounting.
        /// </summary>
        public bool ClassAbilitySuppressed => PvpPlayerRules.Suppressed(IsPkFacetRuleActive, IsPvpClassAbilityMaskActive);

        /// <summary>The equipment-mod read site (Creature.GetEquippedModValue) reads this, same shape as ClassAbilitySuppressed.</summary>
        public bool EquipmentModSuppressed => PvpPlayerRules.Suppressed(IsPkFacetRuleActive, IsPvpEquipmentModMaskActive);

        /// <summary>Pick-up speed bonuses (GetPickupAnimationSpeed): off on the PK facet OR under the arena pick-up mask.</summary>
        public bool PickupBoonSuppressed => PvpPlayerRules.Suppressed(IsPkFacetRuleActive, IsPvpPickupBoonMaskActive);

        /// <summary>The Quickness turn-speed bonus (TurnToSpeed): off on the PK facet OR under the arena turn-speed mask.</summary>
        public bool TurnSpeedSuppressed => PvpPlayerRules.Suppressed(IsPkFacetRuleActive, IsPvpTurnSpeedMaskActive);

        /// <summary>PropertyInt 9075: the pre-match PlayerKillerStatus, present only between EnterPvpMatch and a clean exit.</summary>
        public int? PvpMatchReturnPkStatus
        {
            get => GetProperty(PropertyInt.PvpMatchReturnPkStatus);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.PvpMatchReturnPkStatus); else SetProperty(PropertyInt.PvpMatchReturnPkStatus, value.Value); }
        }

        /// <summary>LastPkAttackTimestamp as it stood when the match was entered; ExitPvpMatch puts it back. In memory only.</summary>
        private double pvpPreMatchLastPkAttackTimestamp;

        /// <summary>The match a logout forfeit was already reported for, so a deferred PK logoff cannot report it twice.</summary>
        private Guid pvpForfeitReportedFor;

        /// <summary>Throttles the friendly-fire line: an AoE or cleave checks every target in range in one swing.</summary>
        private DateTime pvpFriendlyFireNoticeAtUtc;

        /// <summary>Throttles the Tugak Brawl "only Curse of Raven Fury can hurt" line (pvp_arena_ffa_spell_only): a cleave or volley asks once per target.</summary>
        private DateTime pvpFfaSpellOnlyNoticeAtUtc;

        /// <summary>When the Tugak Brawl spell-only line last went out (default when never). Read by tests; a plain read, no Player static.</summary>
        internal DateTime PvpFfaSpellOnlyNoticeAtUtc => pvpFfaSpellOnlyNoticeAtUtc;

        /// <summary>
        /// Per-target throttle for the "is protected for a moment longer" line: target guid to the last time it was sent. Bounded
        /// (<see cref="PvpArenaHookSettings.SpawnProtectionNoticeMaxTracked"/>) and created lazily, guarded by its own lock.
        /// </summary>
        private Dictionary<uint, DateTime> pvpProtectionNotices;

        /// <summary>
        /// What this player has already done with the grant of spawn protection they hold: ended it early, or left the spawn room (so it
        /// runs to a set time). Null before either. Swapped as a whole reference; read by ReplacePvpBinding so a republish rebuilt from the seat
        /// cannot undo it.
        /// </summary>
        private volatile PvpProtectionProgress pvpProtectionProgress;

        /// <summary>Battlegrounds: the binding's pen, latched in Die() beside pvpMatchDeathInProgress; null outside a battleground death.</summary>
        private Position pvpDeathPenLatch;

        /// <summary>The match <see cref="pvpDeathPenLatch"/> belongs to, latched with it.</summary>
        private Guid pvpDeathMatchLatch;

        /// <summary>True while this player is dead or in a battleground pen (the published binding's flag).</summary>
        public bool IsPvpRespawning => PvpPlayerRules.IsRespawning(pvpBinding);

        /// <summary>Read at the head of both cast handlers (Player_Magic.cs): a respawning player cannot cast.</summary>
        internal bool PvpCastRefusedWhileRespawning => PvpPlayerRules.CastRefusedWhileRespawning(pvpBinding);

        /// <summary>Read in Healer.HandleActionUseOnTarget: a respawning player cannot use a healing kit on another player.</summary>
        internal bool PvpHealKitRefusedWhileRespawning(WorldObject target) => PvpPlayerRules.HealKitRefusedWhileRespawning(pvpBinding, ReferenceEquals(target, this));

        // NO STATIC FIELDS IN THIS FILE: the logger and the seams live on PvpArenaHookSettings, so nothing here
        // runs Player's World-database-reading type initializer (see that class for why that matters to tests).

        // ---------------- enter ----------------

        /// <summary>
        /// Puts this player into a match. Queued on the player's own action queue (DESIGN "Enter and exit"), to
        /// be called by the coordinator when the teleport into the match space is dispatched.
        /// </summary>
        public void EnterPvpMatch(PvpPlayerBinding binding, Position exitTo)
        {
            if (!PvpPlayerRules.InMatch(binding))
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): EnterPvpMatch called without a bound match; ignored.");
                return;
            }

            var exit = exitTo != null ? new Position(exitTo) : null;

            EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    EnterPvpMatchNow(binding, exit);
                }
                catch (Exception ex)
                {
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): EnterPvpMatch threw for match {binding.Match.MatchId}.", ex);
                }
            }));
        }

        /// <summary>
        /// The body of <see cref="EnterPvpMatch"/>, on the player's action queue. The step order is the design's.
        ///
        /// Step 6 of the design ("remove any self-buff a class ability cast before entry") has nothing to do:
        /// no class ability writes an enchantment onto its own player. Every class-ability enchantment writer
        /// targets the OTHER creature - Pocket Sand on the attacker (Player_ClassAbilityCombat.TryPocketSand),
        /// Weakened Blood's Fester, Sundermark, Break Armor, Elemental Rend, Hunter's Mark and Nether Bloom on the
        /// struck target. The in-memory stacks and wards are the class abilities' only self-state, and step 3
        /// clears them.
        /// </summary>
        internal void EnterPvpMatchNow(PvpPlayerBinding binding, Position exitTo)
        {
            var reentry = pvpBinding != null;

            // 1. the return point and the crash-recovery marker, then a save soon. The marker is written ONCE: a
            // repeated enter must not overwrite the true pre-match status with PK Lite.
            if (exitTo != null)
                SetPosition(PositionType.EphemeralRealmExitTo, exitTo);

            if (PvpMatchReturnPkStatus == null)
                PvpMatchReturnPkStatus = (int)PlayerKillerStatus;

            if (!reentry)
                pvpPreMatchLastPkAttackTimestamp = LastPkAttackTimestamp;

            RushNextPlayerSave(5);

            // 2. PK Lite, and the binding - which switches every mask on
            if (PlayerKillerStatus != PlayerKillerStatus.PKLite)
            {
                PlayerKillerStatus = PlayerKillerStatus.PKLite;
                EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus));
            }

            pvpBinding = binding;

            // 2b. free components for the match: the client must be told (it refuses a cast without components otherwise)
            SendClientSpellComponentsRequired();

            // 2c. pvp_match_block_dual_wield: an off-hand weapon the player brought in (a mode with no kit; a kit is cleared when it is
            // issued) goes to the pack now that the binding makes the rule apply.
            ClearPvpOffHandWeapon(binding.Template?.Key);

            // 2d. PropertyBool.PveOnly: a PvE-only item the player brought in goes to the pack (an issued kit one is consumed).
            ClearPvpPveOnlyItems(binding.Template?.Key);

            // 3. the hook caches bake in ClassAbilitySuppressed, so rebuild them now; drop every in-memory stack and ward
            InvalidateClassAbilityHookCaches();
            pkFacetSuppressionSeen = ClassAbilitySuppressed;
            ResetClassAbilityTransientState();

            // 4. combat pets (pvp_arena_retire_combat_pets); summoning is refused for the rest of the match (PetDevice)
            if (PvpTunables.DialSource().RetireCombatPets)
                RetireCombatPetsForPkFacet();

            // 5. the Enhanced / bundle stat bonuses now read 0 under the mask: push the lowered values to the client
            ResendLearnedEnhancedStats();

            // 7. (DESIGN "Enter and exit") a DoT or debuff ANOTHER PLAYER left on this player before entry would keep hurting them inside
            // the sealed match; remove those. Self-cast, monster-cast and beneficial entries stay (see
            // PvpPlayerRules.IsHarmFromAnotherPlayer, including the weapon-proc case it cannot identify).
            RemoveHarmFromOtherPlayers();

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Entering, ChatMessageType.Broadcast));

            PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): entered match {binding.Match.MatchId} ({binding.Match.ModeKey}) on team {binding.TeamIndex}; return status {(PlayerKillerStatus)PvpMatchReturnPkStatus.Value}{(reentry ? ", re-entry" : "")}.");
        }

        /// <summary>
        /// Swaps in a new binding for a player already in a match - for the coordinator to publish a state change
        /// (Countdown to Live, Live to Resolving), since a binding is immutable. Refuses to bind a player who is
        /// not already in a match, and to move them to a different match: entering goes through EnterPvpMatch only.
        /// </summary>
        public bool ReplacePvpBinding(PvpPlayerBinding next)
        {
            // Compare-and-swap, so a swap racing the early end of a spawn-protection window (EndPvpSpawnProtection, on this player's
            // thread) retries against the newer binding instead of overwriting it.
            while (true)
            {
                var current = pvpBinding;

                if (!PvpPlayerRules.InMatch(current) || !PvpPlayerRules.InMatch(next) || current.Match.MatchId != next.Match.MatchId)
                    return false;

                // A coordinator republish is rebuilt from the seat, and the seat only learns of an early end when the intent drains.
                // If this player already ended THAT window, the republish must not bring it back: the player is the single owner of
                // "this window was ended", so the window is stripped here instead of needing a gateway read in the coordinator.
                var toPublish = next;
                var progress = pvpProtectionProgress;

                if (progress != null && progress.Id != 0 && next.HasSpawnProtection && next.ProtectionId == progress.Id)
                {
                    // Same grant: carry over what the player did with it. Ended: no protection. Left the room: room mode becomes the window
                    // that started at the exit (a binding already in window mode is left alone), and re-entering never restores room mode.
                    if (progress.Ended)
                        toPublish = next.WithoutSpawnProtection();
                    else if (next.ProtectedInRoom)
                        toPublish = next.WithRoomExit(progress.UntilUtc);
                }

#pragma warning disable 420
                if (System.Threading.Interlocked.CompareExchange(ref pvpBinding, toPublish, current) == current)
                    return true;
#pragma warning restore 420
            }
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo, ACE.Server.csproj:15): sets the binding alone, without the rest of
        /// EnterPvpMatchNow, so ACE.Server.Tests can drive the gate and the predicates on a seeded Player. Setting
        /// the field by reflection is not an option there: FieldInfo.SetValue on a field Player declares runs
        /// Player's World-database-reading type initializer (see PvpArenaHookSettings). Production never calls it.
        /// </summary>
        internal void SetPvpBindingForTests(PvpPlayerBinding binding) => pvpBinding = binding;

        // ---------------- exit ----------------

        /// <summary>
        /// Takes this player out of a match: the inverse of EnterPvpMatch. Idempotent - a player already out
        /// (no binding and no marker) is left untouched. Queued on the player's own action queue.
        ///
        /// This does NOT teleport. The return trip is the caller's: a match death returns through
        /// ThreadSafeTeleportOnDeath to the stamped EphemeralRealmExitTo, the coordinator (C3) sends the living
        /// back, and a logout inside a match is relocated by the login path's dead-instance check, which reads
        /// the same stamp. That is also why the stamp is left in place here.
        /// </summary>
        public void ExitPvpMatch(string context) => ExitPvpMatch(context, returnFromPen: false);

        /// <summary>
        /// <see cref="ExitPvpMatch(string)"/>, and, with <paramref name="returnFromPen"/>, the battleground pen rule
        /// (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 8): the coordinator exits a seat it believes is still dying without
        /// teleporting it. If, when this runs on the player's queue, the death sequence has already finished and left
        /// the player standing in the pen, this sends them to their stamped exit; while the sequence is still running
        /// the death teleport or its completion backstop does it instead. Same queue, so exactly one of them moves them.
        /// </summary>
        public void ExitPvpMatch(string context, bool returnFromPen)
        {
            EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var pen = pvpBinding?.RespawnPen;
                    var penReturn = returnFromPen && PvpPlayerRules.ShouldReturnFromPen(pen, IsInDeathProcess, Location?.Instance);
                    var deferRestore = PvpPlayerRules.DefersTemplateRestoreToPenBackstop(returnFromPen, IsInDeathProcess);

                    ExitPvpMatchNow(context, deferRestore);

                    if (penReturn)
                        ReturnFromBattlegroundPenNow($"exited from the pen ({context})");
                }
                catch (Exception ex)
                {
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): ExitPvpMatch ({context}) threw.", ex);
                }
            }));
        }

        /// <summary>
        /// The body of <see cref="ExitPvpMatch"/>. Returns false when the player was already out of the match.
        ///
        /// PvP Template Facets (TEMPLATES.md "Lifecycle"): FIRST, the idempotent template restore. Every exit the
        /// coordinator drives - match end, /arena leave, leaving the instance, admin cancel, the post-death exit - and
        /// the logout path all reach it here, and it runs on the player's queue BEFORE the return teleport the
        /// coordinator queues behind this action. It runs even when the player is no longer bound (a bind that failed
        /// after the apply), and it is a no-op without a record (a death already restored at its arrival).
        ///
        /// With <paramref name="deferRestoreToPenBackstop"/> (a battleground exit while the player is still dying, see
        /// PvpPlayerRules.DefersTemplateRestoreToPenBackstop) the restore is skipped HERE and runs at the end of the death
        /// sequence instead, so a Health-0 player is never rebuilt mid-death; the rest of the exit runs as usual.
        /// </summary>
        internal bool ExitPvpMatchNow(string context, bool deferRestoreToPenBackstop = false)
        {
            if (deferRestoreToPenBackstop)
                PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): exit ({context}) while dying; the template restore waits for the end of the death sequence.");
            else
            {
                try
                {
                    PvpTemplateSettings.Restore(this, $"match exit: {context}");
                }
                catch (Exception ex)
                {
                    // The restore keeps its record on any failure (the player stays inert); the match exit itself must go on.
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the template restore at match exit ({context}) threw.", ex);
                }
            }

            var binding = pvpBinding;
            var marker = PvpMatchReturnPkStatus;

            if (binding == null && marker == null)
                return false;

            pvpBinding = null;

            // the player's real components setting goes back to the client (the binding was the only thing waiving it)
            SendClientSpellComponentsRequired();

            if (marker != null)
            {
                var restored = (PlayerKillerStatus)marker.Value;

                PvpMatchReturnPkStatus = null;

                if (PlayerKillerStatus != restored)
                {
                    PlayerKillerStatus = restored;
                    EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus));
                }
            }

            // LastPkAttackTimestamp goes back to what it was at entry, so the PK timer the match itself started
            // does not follow the player out: the arena is sealed, so nothing in it can be fled from outside, and
            // an active timer would otherwise block recall, dispels and a prompt logoff right after a match.
            LastPkAttackTimestamp = pvpPreMatchLastPkAttackTimestamp;
            pvpPreMatchLastPkAttackTimestamp = 0;

            InvalidateClassAbilityHookCaches();
            pkFacetSuppressionSeen = ClassAbilitySuppressed;

            ResendLearnedEnhancedStats();

            RushNextPlayerSave(5);

            PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): left match {binding?.Match?.MatchId.ToString() ?? "(none bound)"} ({context}); PlayerKillerStatus is {PlayerKillerStatus}.");

            return true;
        }

        /// <summary>
        /// Login (DESIGN H2): a marker present at login means the last match ended without a clean exit (a crash,
        /// or an exit action that never ran). Restores the PK status from it and removes it. Called from
        /// WorldManager.DoPlayerEnterWorld_Inner BEFORE PlayerEnterWorld, whose PK Lite to NPK login check would
        /// otherwise turn even a real PK into an NPK. Not broadcast: the character is not in the world yet.
        /// </summary>
        public void RestorePvpMatchStatusAtLogin()
        {
            var marker = PvpMatchReturnPkStatus;

            if (marker == null)
                return;

            var previous = PlayerKillerStatus;
            var restored = (PlayerKillerStatus)marker.Value;

            PlayerKillerStatus = restored;
            PvpMatchReturnPkStatus = null;

            PvpArenaHookSettings.Log.Warn($"[PVP] {Name} (0x{Guid.Full:X8}): login found the arena return marker; PlayerKillerStatus {previous} -> {restored}.");

            var chain = new ActionChain();
            chain.AddDelaySeconds(3.0f);
            chain.AddAction(this, () =>
            {
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.RestoredAtLogin, ChatMessageType.Broadcast));
            });
            chain.EnqueueChain();
        }

        // ---------------- intents ----------------

        /// <summary>
        /// Head of Player.LogOut (DESIGN H3), before the PK logoff deferral: a logout inside a match is a forfeit.
        /// Reported once per match, because a deferred PK logoff calls LogOut again when the timer runs out.
        /// </summary>
        internal void ReportPvpLogoutForfeit()
        {
            // Deserter lockout (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"): a logout the server or staff forced is never a strike. The
            // one-shot eviction flag is consumed on every call, in a match or not, so it can never leak into a later logout.
            var evicted = PvpServerLogoffPending;
            PvpServerLogoffPending = false;

            var binding = pvpBinding;

            if (!PvpPlayerRules.InMatch(binding) || pvpForfeitReportedFor == binding.Match.MatchId)
                return;

            pvpForfeitReportedFor = binding.Match.MatchId;

            var serverCaused = evicted;

            try
            {
                serverCaused |= PvpDeserterRules.IsServerCausedTermination(Session?.PendingTermination?.Reason);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): classifying the logout's termination reason threw; treating it as the player's own", ex);
            }

            PvpMatchManager.Report(PvpMatchManager.LogoutForfeit(Guid.Full, binding.Match.MatchId, DateTime.UtcNow, serverCaused));
        }

        /// <summary>
        /// One-shot: set immediately before a server eviction that logs the player off with no session termination reason (the IP-limit
        /// sweep and the higher-level-character evict), so the match forfeit that logout reports is not a deserter strike. Consumed by
        /// <see cref="ReportPvpLogoutForfeit"/>.
        /// </summary>
        public bool PvpServerLogoffPending { get; set; }

        /// <summary>
        /// Player.Die (DESIGN H4): latches whether this death is a match death, and the waiver it gets, from the
        /// binding at the moment of death; reports the death intent. Returns the latch.
        /// </summary>
        private bool LatchPvpMatchDeath(DamageHistoryInfo lastDamager, out PvpDeathWaiver waiver)
        {
            var binding = pvpBinding;

            // Battlegrounds: the pen and the match it belongs to are latched with the death, so the delayed death
            // teleport decides against the death's own match rather than whatever is bound later.
            pvpDeathPenLatch = null;
            pvpDeathMatchLatch = System.Guid.Empty;

            if (!PvpPlayerRules.InMatch(binding))
            {
                waiver = null;
                return false;
            }

            if (binding.RespawnPen != null)
            {
                pvpDeathPenLatch = new Position(binding.RespawnPen);
                pvpDeathMatchLatch = binding.Match.MatchId;
            }

            waiver = PvpDeathWaiver.ForInMatchDeath(PvpTunables.DialSource().DeathKeepsEnchantments);

            // The killer, for the elimination line and the kill count: only a player's guid means anything to the match.
            var killerId = lastDamager != null && lastDamager.IsPlayer ? lastDamager.Guid.Full : 0u;

            // Where the victim died rides along (copied, on this landblock thread), for Attack/Defend's kill chip and heal range check.
            PvpMatchManager.Report(PvpMatchManager.Death(Guid.Full, binding.Match.MatchId, DateTime.UtcNow, killerId, Location));

            return true;
        }

        // ---------------- the damage gate ----------------


        /// <summary>
        /// The arena half of <see cref="CheckPKStatusVsTarget"/> (DESIGN H6). Returns false when the arena has no
        /// say - the target is not a player, or neither player is in a match - and the retail rules run unchanged.
        /// Returns true when it decided: <paramref name="result"/> is null to allow, or an error pair to refuse,
        /// which is the vocabulary every caller already reports.
        /// </summary>
        internal bool TryPvpArenaGate(Player targetPlayer, Spell spell, out List<WeenieErrorWithString> result)
        {
            result = null;

            if (targetPlayer == null)
                return false;

            var mine = pvpBinding;
            var theirs = targetPlayer.pvpBinding;

            if (!PvpPlayerRules.InMatch(mine) && !PvpPlayerRules.InMatch(theirs))
                return false;

            // A Countdown vuln (defense-lowering, Life elemental vulnerability or Imperil) is the one kind of harmful spell the
            // gate lets through before Live (pre-match Countdown).
            var isCountdownVuln = spell != null && spell.IsHarmful && PvpArenaSpellRules.IsCountdownVuln(spell.School, spell.Category);

            var decision = PvpArenaGate.Evaluate(mine, theirs, PvpArenaHookSettings.FriendlyFireSource(), isCountdownVuln, PvpArenaHookSettings.UtcNow(), targetPlayer.Location);

            if (decision == PvpGateDecision.NotApplicable)
                return false;

            // Battleground spawn protection: the target's window refuses melee, missile and every harmful spell at the one choke
            // point, which also runs at projectile impact, so a bolt that lands inside the window is refused too.
            if (decision == PvpGateDecision.RefuseProtected)
            {
                SendPvpSpawnProtectedNotice(targetPlayer);

                result = PvpArenaHookSettings.SpawnProtectedRefusal;
                return true;
            }

            if (decision == PvpGateDecision.Allow)
            {
                // Tugak Brawl is spell-only (owner ruling 2026-10-15, pvp_arena_ffa_spell_only): an attack that carries no spell - a
                // swing, a shot, a cleave or multi-shot extra, a riposte - never lands between two players of the same Live FFA match.
                // The refusal is silent at the call sites (the throttled line below is the only message) because a cleave or volley asks
                // once per target. A missile is refused here at IMPACT, not at launch, so the shot's ammo is already spent.
                var inFfa = mine.Match.ModeKey == ArenaMapCatalog.FfaKey;

                if (spell == null && inFfa && PvpArenaSpellRules.IsPhysicalRefused(inFfa, PvpArenaHookSettings.FfaSpellOnlySource()))
                {
                    SendPvpFfaSpellOnlyNotice();

                    result = PvpArenaHookSettings.SpawnProtectedRefusal;
                    return true;
                }

                // Doctide-ported arena spell rules (Docs/Pvp/DESIGN.md "Arena spell rules"): a harmful spell the
                // gate above already allowed can still be refused by school/category, or by FFA. Resolved
                // against targetPlayer, the SAME creature the gate itself just checked - unlike Doctide, which
                // resolves its equivalent check against the raw target and so skips an item-targeted spell; here
                // the caller has already walked a wielded item back to its wielder before calling this gate, so
                // an item-targeted harmful spell is covered too (deliberately stricter than Doctide).
                if (spell != null && spell.IsHarmful && mine.RestrictSpells)
                {
                    var isFfa = mine.Match.ModeKey == ArenaMapCatalog.FfaKey;

                    if (PvpArenaSpellRules.IsRefused(spell.School, spell.Category, spell.Id, isFfa))
                    {
                        result = new List<WeenieErrorWithString>() { WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone, WeenieErrorWithString._FailsToAffectYou_TheyCannotAffectAnyone };
                        return true;
                    }
                }

                // Conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): a clean allow of a hostile action on an ENEMY of the same Live
                // match is activity for the attacker, whether or not it then lands (misses and resists count). A teammate (friendly fire on)
                // and a Countdown vuln never stamp.
                if (PvpConductRules.IsHostileAttempt(mine, theirs))
                    StampPvpActivity(mine.Match.MatchId, PvpArenaHookSettings.UtcNow());

                return true;
            }

            if (PvpPlayerRules.IsTeammate(mine, theirs))
                SendPvpFriendlyFireNotice();

            result = new List<WeenieErrorWithString>() { WeenieErrorWithString.YouFailToAffect_NotSamePKType, WeenieErrorWithString._FailsToAffectYou_NotSamePKType };
            return true;
        }

        private void SendPvpSpawnProtectedNotice(Player target)
        {
            if (Session == null || !ShouldSendPvpSpawnProtectedNotice(target.Guid.Full, PvpArenaHookSettings.UtcNow()))
                return;

            Session.Network.EnqueueSend(new GameMessageSystemChat(BattlegroundText.SpawnProtectedTarget(target.Name), ChatMessageType.Broadcast));
        }

        /// <summary>
        /// The throttle behind the "is protected" line: true at most once per <see cref="PvpArenaHookSettings.SpawnProtectionNoticeIntervalSeconds"/>
        /// for each target guid, so an AoE or a run of swings at one player speaks once while a second protected target still gets its line.
        /// Records the send when it returns true. Bounded: past the cap, expired entries go first, then the oldest.
        /// </summary>
        internal bool ShouldSendPvpSpawnProtectedNotice(uint targetGuid, DateTime now)
        {
            var map = System.Threading.Volatile.Read(ref pvpProtectionNotices)
                ?? System.Threading.Interlocked.CompareExchange(ref pvpProtectionNotices, new Dictionary<uint, DateTime>(), null)
                ?? pvpProtectionNotices;

            lock (map)
            {
                if (map.TryGetValue(targetGuid, out var last) && (now - last).TotalSeconds < PvpArenaHookSettings.SpawnProtectionNoticeIntervalSeconds)
                    return false;

                if (map.Count >= PvpArenaHookSettings.SpawnProtectionNoticeMaxTracked && !map.ContainsKey(targetGuid))
                {
                    var expired = map.Where(kvp => (now - kvp.Value).TotalSeconds >= PvpArenaHookSettings.SpawnProtectionNoticeIntervalSeconds).Select(kvp => kvp.Key).ToList();

                    foreach (var key in expired)
                        map.Remove(key);

                    if (map.Count >= PvpArenaHookSettings.SpawnProtectionNoticeMaxTracked)
                        map.Remove(map.OrderBy(kvp => kvp.Value).First().Key);
                }

                map[targetGuid] = now;
                return true;
            }
        }

        /// <summary>
        /// Ends this player's battleground spawn protection because they attacked or cast a harmful spell (Docs/Pvp/BATTLEGROUNDS.md
        /// "Spawn protection"). Swaps in the binding without the window at once, so the very next hit on them lands, and reports it so
        /// the coordinator drops its copy. Says so only when a window was still running: a lapsed one ends silently. A no-op for
        /// everyone without a window, which is nearly every call.
        /// </summary>
        internal void EndPvpSpawnProtection()
        {
            while (true)
            {
                var binding = pvpBinding;

                if (binding == null || !binding.HasSpawnProtection)
                    return;

                var now = PvpArenaHookSettings.UtcNow();
                var wasRunning = binding.IsSpawnProtected(now, Location);

                // Remember WHICH grant was ended before the swap, so a republish rebuilt from the seat (which still carries it until
                // the intent drains) is stripped by ReplacePvpBinding instead of undoing this.
                pvpProtectionProgress = new PvpProtectionProgress(binding.ProtectionId, true, DateTime.MinValue);

#pragma warning disable 420
                if (System.Threading.Interlocked.CompareExchange(ref pvpBinding, binding.WithoutSpawnProtection(), binding) != binding)
                    continue;
#pragma warning restore 420

                if (!wasRunning)
                    return;

                Session?.Network.EnqueueSend(new GameMessageSystemChat(BattlegroundText.SpawnProtectionEnded, ChatMessageType.Broadcast));

                PvpMatchManager.Report(PvpMatchManager.SpawnProtectionEnded(Guid.Full, binding.Match.MatchId, now));
                return;
            }
        }

        /// <summary>
        /// Room-based spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): called right after the player's position is committed
        /// (UpdatePlayerPosition), so the moment they leave their team's spawn room is seen without the coordinator polling anyone. While
        /// the binding is in room mode and the current cell is outside the room, the binding becomes the window that started now and runs
        /// <see cref="PvpPlayerBinding.ProtectionSeconds"/> from it. Re-entering the room changes nothing (room mode is gone). A no-op, one
        /// volatile read, for everyone whose binding is not in room mode, which is nearly every call.
        /// </summary>
        internal void CheckPvpSpawnRoomExit()
        {
            while (true)
            {
                var binding = pvpBinding;

                if (binding == null || !binding.ProtectedInRoom || binding.IsInSpawnRoom(Location))
                    return;

                // Recorded before the swap, like the early end, so a republish rebuilt from the seat (still in room mode) cannot restore it.
                // Written by compare-and-swap so it can never overwrite an early end of the same grant (Ended always wins), and a retry
                // after a lost swap keeps the exit instant already recorded instead of restarting the window.
                DateTime until;

                while (true)
                {
                    var prior = pvpProtectionProgress;

                    if (prior != null && prior.Id == binding.ProtectionId && prior.Ended)
                        return;

                    until = prior != null && prior.Id == binding.ProtectionId
                        ? prior.UntilUtc
                        : PvpArenaHookSettings.UtcNow().AddSeconds(Math.Max(0, binding.ProtectionSeconds));

#pragma warning disable 420
                    if (System.Threading.Interlocked.CompareExchange(ref pvpProtectionProgress, new PvpProtectionProgress(binding.ProtectionId, false, until), prior) == prior)
                        break;
#pragma warning restore 420
                }

#pragma warning disable 420
                if (System.Threading.Interlocked.CompareExchange(ref pvpBinding, binding.WithRoomExit(until), binding) == binding)
                    return;
#pragma warning restore 420
            }
        }

        /// <summary>True while the last position commit had this player inside their own team's spawn room (the edge detector for the refill).</summary>
        private bool pvpWasInOwnSpawnRoom;

        /// <summary>When this player's last spawn-room refill fired (UtcNow seam clock), for pvp_bg_spawn_refill_cooldown_seconds. Default = never.</summary>
        private DateTime pvpLastSpawnRefillUtc;

        /// <summary>
        /// Respawn-zone refill (Docs/Pvp/TEMPLATES.md "Respawn zone"): called right after the position commit, beside
        /// <see cref="CheckPvpSpawnRoomExit"/>. On the step that carries the player INTO their own team's spawn room during a Live match,
        /// health, stamina and mana go to full, at most once per pvp_bg_spawn_refill_cooldown_seconds. A no-op, one volatile read, for
        /// everyone without a binding. The whole decision lives in <see cref="RunPvpSpawnRoomEntry"/>, which a test drives with a counting refill.
        /// </summary>
        internal void CheckPvpSpawnRoomEntry() => RunPvpSpawnRoomEntry(RefillPvpSpawnRoomVitals);

        /// <summary>
        /// The refill decision as a seam: runs <paramref name="refill"/> exactly when this call is an entry (<see cref="TrackPvpSpawnRoomEntry"/>)
        /// and the cooldown has lapsed. <paramref name="refill"/> returns whether it actually refilled; only then is the cooldown started.
        /// Returns whether it did. The edge flag follows the position through a cooldown, so a skipped entry is not retried while inside.
        /// </summary>
        internal bool RunPvpSpawnRoomEntry(Func<bool> refill)
        {
            if (!TrackPvpSpawnRoomEntry())
                return false;

            var now = PvpArenaHookSettings.UtcNow();
            var cooldown = PvpArenaHookSettings.SpawnRefillCooldownSecondsSource();

            if (cooldown > 0 && pvpLastSpawnRefillUtc != default && (now - pvpLastSpawnRefillUtc).TotalSeconds < cooldown)
                return false;

            try
            {
                if (!refill())
                    return false;
            }
            catch (Exception ex)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the spawn-room refill threw.", ex);
                return false;
            }

            pvpLastSpawnRefillUtc = now;
            return true;
        }

        /// <summary>Sets Health, Stamina and Mana to max through the respawn's vital path (Player.UpdateVital sends each change). False for a dead player.</summary>
        private bool RefillPvpSpawnRoomVitals()
        {
            if (IsDead || IsInDeathProcess)
                return false;

            PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));
            return true;
        }
        /// <summary>
        /// The edge detector behind <see cref="CheckPvpSpawnRoomEntry"/>: true exactly when this call sees the player inside their OWN
        /// team's spawn room (the binding's <see cref="PvpPlayerBinding.SpawnRoom"/>, so an enemy room never counts) in a Live match, not
        /// respawning, having been outside on the previous call. The flag follows the position whatever the match state, so the first
        /// Live step inside the room after leaving it is the next entry.
        /// </summary>
        internal bool TrackPvpSpawnRoomEntry()
        {
            var binding = pvpBinding;

            var inRoom = binding != null && binding.SpawnRoom != null && binding.IsInSpawnRoom(Location);
            var was = pvpWasInOwnSpawnRoom;

            pvpWasInOwnSpawnRoom = inRoom;

            return inRoom && !was && binding.State == PvpMatchState.Live && !binding.Respawning;
        }

        private void SendPvpFfaSpellOnlyNotice()
        {
            var now = PvpArenaHookSettings.UtcNow();

            if ((now - pvpFfaSpellOnlyNoticeAtUtc).TotalSeconds < PvpArenaHookSettings.FfaSpellOnlyNoticeIntervalSeconds)
                return;

            pvpFfaSpellOnlyNoticeAtUtc = now;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.FfaSpellOnlyAttacker, ChatMessageType.Broadcast));
        }

        private void SendPvpFriendlyFireNotice()
        {
            var now = DateTime.UtcNow;

            if ((now - pvpFriendlyFireNoticeAtUtc).TotalSeconds < PvpArenaHookSettings.FriendlyFireNoticeIntervalSeconds)
                return;

            pvpFriendlyFireNoticeAtUtc = now;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.FriendlyFireAttacker, ChatMessageType.Broadcast));
        }

        // ---------------- battleground death and respawn (Docs/Pvp/BATTLEGROUNDS.md "Respawn") ----------------

        /// <summary>
        /// Where the current death's teleport goes when it is a battleground pen death, or null for every other death
        /// (the existing exit path). Reads the latch from Die() and the binding held right now. Player queue only.
        /// </summary>
        internal Position PvpDeathPenDestination() => PvpPlayerRules.DeathDestination(pvpDeathPenLatch, pvpDeathMatchLatch, pvpBinding);

        /// <summary>
        /// The completion backstop's test (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 4): the binding held at the END of the
        /// death sequence is no longer Live for <paramref name="penMatch"/>. Deliberately a live binding read - the
        /// question is whether the match is still on - kept here so Player_Death.cs reads only latches itself.
        /// </summary>
        internal bool PvpPenBackstopNeeded(Guid penMatch) => PvpPlayerRules.PenReturnNeeded(penMatch, pvpBinding);

        /// <summary>True when the current death was latched as a battleground death: it never leaves a corpse.</summary>
        internal bool PvpBattlegroundDeathLatched => pvpDeathPenLatch != null;

        /// <summary>The match the current battleground death was latched against (Guid.Empty for any other death).</summary>
        internal Guid PvpDeathMatchLatch => pvpDeathMatchLatch;

        /// <summary>Clears the battleground death latch, at the end of the death sequence beside the other latches.</summary>
        internal void ClearPvpDeathPenLatch()
        {
            pvpDeathPenLatch = null;
            pvpDeathMatchLatch = System.Guid.Empty;
        }

        /// <summary>
        /// Sends a player standing in a battleground pen to the stamped exit and clears the stamp, the same trip a
        /// match death takes (ThreadSafeTeleportOnDeath). Player queue only.
        /// </summary>
        internal void ReturnFromBattlegroundPenNow(string why)
        {
            // PvP Template Facets: leaving the pen for home ends participation, so the template comes off before the
            // trip. Usually a no-op, because the exit that sends this (ExitPvpMatch with returnFromPen) restored first;
            // the death-sequence completion backstop can reach here before that exit runs.
            try
            {
                PvpTemplateSettings.Restore(this, $"left the battleground pen: {why}");
            }
            catch (Exception ex)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the template restore on leaving the pen threw ({why}).", ex);
            }

            // The one PvP return resolver: back inside a Thread they were pulled from when it still takes them, else the stamp, else home.
            var destination = ResolvePvpReturn($"left the battleground pen: {why}") ?? Instantiation;

            if (destination == null)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): no exit stamp, sanctuary or instantiation to return to from the pen ({why}).");
                return;
            }

            PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): returned from the battleground pen to {destination} ({why}).");

            WorldManager.ThreadSafeTeleport(this, destination);
        }

        // ---------------- the PvP return (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread") ----------------

        /// <summary>
        /// THE ONE PvP RETURN RESOLVER, called by every path that sends a player home from a match: the coordinator's return
        /// (LivePvpPlayerGateway.ReturnToExit), a match death (ThreadSafeTeleportOnDeath), the battleground pen return
        /// (<see cref="ReturnFromBattlegroundPenNow"/>) and the login relocation (WorldManager.DoPlayerEnterWorld_Inner). None of them reads
        /// EphemeralRealmExitTo itself. Returns the destination (a copy) and leaves the stamp as the decision says
        /// (PvpReturnRules.Decide): a player pulled from a Thread goes back to the exact spot they left when the run registered for that
        /// instance is still the very run they left (by reference: run ids are re-issued), it has not ended, its copy is loaded and its
        /// realm accepts them; the Thread's outer stamp then stays their EphemeralRealmExitTo. Otherwise the stamp (cleared; never a dead
        /// or refusing instance), else <paramref name="seatExit"/>, else home, and an invalid Thread entry is released.
        /// <paramref name="keepAwayEntry"/> (a login whose battleground seat is held for a rejoin) leaves any Thread entry for after the
        /// match. <paramref name="homeFallback"/> false (the login path) returns null when there is neither a Thread return nor a stamp,
        /// so the caller keeps its own fallback. Player queue (or the world thread at login).
        /// </summary>
        internal Position ResolvePvpReturn(string why, Position seatExit = null, bool keepAwayEntry = false, bool homeFallback = true)
        {
            var guid = Guid.Full;
            var stamp = GetPosition(PositionType.EphemeralRealmExitTo);

            ACE.Server.ThreadDungeons.ThreadPvpAway.Entry entry = null;
            var hasEntry = !keepAwayEntry && ACE.Server.ThreadDungeons.ThreadPvpAway.TryGet(guid, out entry);
            var threadValid = false;

            if (hasEntry)
            {
                var instance = entry.Inside.Instance;
                var registered = ACE.Server.ThreadDungeons.ThreadDungeonManager.GetRun(instance);
                var landblock = LandblockManager.GetEphemeralLandblock(instance);
                var accepts = landblock?.InnerRealmInfo?.Accepts(this) ?? false;

                threadValid = ACE.Server.ThreadDungeons.ThreadPvpAway.IsReturnValid(ReferenceEquals(registered, entry.Run),
                    entry.Run.State == ACE.Server.ThreadDungeons.ThreadDungeonRunState.Ended, landblock != null, accepts);
            }

            var stampUsable = false;

            if (stamp != null)
            {
                ACE.Server.Realms.InstanceRouting.ValidateInstanceDestination(stamp, this, out var rejection);
                stampUsable = rejection == ACE.Server.Realms.InstanceRejection.None;
            }

            var decision = PvpReturnRules.Decide(hasEntry, threadValid, entry?.Inside, stamp, stampUsable, seatExit, Sanctuary, Instantiation, homeFallback);

            if (decision.ReleaseThreadEntry)
                ACE.Server.ThreadDungeons.ThreadPvpAway.Release(guid, entry.Run);

            if (!decision.KeepStamp)
                SetPosition(PositionType.EphemeralRealmExitTo, null);

            PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{guid:X8}): PvP return ({why}): {decision.Kind}{(hasEntry ? $" (Thread entry for {entry.Run}: {(threadValid ? "valid" : "not valid, released")})" : "")} -> {decision.Destination?.ToString() ?? "none"}");

            return decision.Destination != null ? new Position(decision.Destination) : null;
        }

        /// <summary>
        /// The coordinator's Respawn action (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 6). Queued on the player's own queue;
        /// does nothing unless the player is still bound to <paramref name="matchId"/>, standing in the match instance,
        /// alive and out of the death sequence. Then: lifestone protection off, other players' harm removed, full
        /// vitals, and the teleport to <paramref name="spawn"/>.
        /// </summary>
        public void RespawnInBattleground(Guid matchId, Position spawn)
        {
            if (spawn == null)
                return;

            var destination = new Position(spawn);

            EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!PvpPlayerRules.CanRespawn(pvpBinding, matchId, Location?.Instance, destination.Instance, IsDead, IsInDeathProcess))
                    {
                        PvpArenaHookSettings.Log.Warn($"[PVP] {Name} (0x{Guid.Full:X8}): respawn for match {matchId} skipped (bound {pvpBinding?.Match?.MatchId.ToString() ?? "none"}, instance 0x{Location?.Instance ?? 0:X8}, dead {IsDead}, dying {IsInDeathProcess}).");
                        return;
                    }

                    ClearBattlegroundRespawnShields();

                    // PvP Template Facets: still templated through the respawn; the template's buffs go back on before
                    // the full vitals below, since the maximums depend on them.
                    ReapplyPvpTemplateBuffsAfterRespawn();

                    // Full vitals. Player.UpdateVital sends each change to the client itself.
                    PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));

                    DamageHistory.Reset();

                    WorldManager.ThreadSafeTeleport(this, destination);

                    PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): respawned in match {matchId} at {destination}.");
                }
                catch (Exception ex)
                {
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the battleground respawn threw for match {matchId}.", ex);
                }
            }));
        }

        /// <summary>
        /// The two things the death sequence leaves on a player that a respawn must remove: lifestone protection, which
        /// would otherwise make a respawned player unkillable on the hill for a minute (Player_Death.cs
        /// SetLifestoneProtection, LifestoneProtectionTime), and any harmful enchantment another player put on them.
        /// Split out so a test can pin it on a seeded Player. Player queue only.
        /// </summary>
        internal void ClearBattlegroundRespawnShields()
        {
            UnderLifestoneProtection = false;
            LifestoneProtectionTimestamp = null;

            RemoveHarmFromOtherPlayers();
        }

        /// <summary>
        /// The King of the Hill drain (Docs/Pvp/BATTLEGROUNDS.md "King of the Hill"), queued on the player's own queue.
        /// Skipped unless still bound Live to <paramref name="matchId"/>, not respawning, in <paramref name="matchInstance"/>,
        /// alive and not teleporting. With <paramref name="lethal"/> the health drain can kill (shape: Hotspot.Activate);
        /// the player is their own last damager, so the death intent credits no killer. Without it, health stops at 1.
        /// </summary>
        public void DrainForBattlegroundZone(Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal)
        {
            EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var binding = pvpBinding;

                    if (!PvpPlayerRules.InLiveMatch(binding) || binding.Match.MatchId != matchId || binding.Respawning
                        || Location?.Instance != matchInstance || IsDead || IsInDeathProcess || Teleporting || Invincible)
                        return;

                    if (stamina > 0)
                        UpdateVitalDelta(Stamina, -stamina);

                    if (mana > 0)
                        UpdateVitalDelta(Mana, -mana);

                    if (health > 0)
                    {
                        var amount = lethal ? health : Math.Min(health, Math.Max(0, (int)Health.Current - 1));

                        if (amount > 0)
                            UpdateVitalDelta(Health, -amount);
                    }

                    // Player.UpdateVital (behind UpdateVitalDelta) sends each change to the client itself.
                    if (IsDead)
                    {
                        var self = new DamageHistoryInfo(this);

                        OnDeath(self, DamageType.Health, false);
                        Die(self, DamageHistory.TopDamager);
                    }
                }
                catch (Exception ex)
                {
                    PvpArenaHookSettings.Log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the battleground zone drain threw for match {matchId}.", ex);
                }
            }));
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// Dispels every harmful enchantment another player cast on this player (EnterPvpMatch step 6b). Returns
        /// how many were removed. Goes through EnchantmentManager.Dispel, which also tells the client.
        /// </summary>
        internal int RemoveHarmFromOtherPlayers()
        {
            var registry = ACE.Entity.Models.PropertiesEnchantmentRegistryExtensions.Clone(Biota.PropertiesEnchantmentRegistry, BiotaDatabaseLock);

            if (registry == null || registry.Count == 0)
                return 0;

            var foreign = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>();

            foreach (var entry in registry)
            {
                if (PvpPlayerRules.IsHarmFromAnotherPlayer(entry, Guid.Full))
                    foreign.Add(entry);
            }

            if (foreign.Count > 0)
            {
                EnchantmentManager.Dispel(foreign);
                PvpArenaHookSettings.Log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): removed {foreign.Count} harmful enchantment(s) cast by other players on entry.");
            }

            return foreign.Count;
        }

        /// <summary>
        /// Pushes every learned Enhanced / bundle stat to the client, so the panel follows the mask on entry and
        /// on exit without a relog. SendEnhancedStatUpdate ignores an ability that is neither.
        /// </summary>
        private void ResendLearnedEnhancedStats()
        {
            if (Session == null)
                return;

            foreach (var (id, rank) in GetClassAbilityCache())
            {
                if (rank <= 0 || !ClassAbilityRegistry.Abilities.TryGetValue(id, out var definition))
                    continue;

                SendEnhancedStatUpdate(definition);
            }
        }
    }
}
