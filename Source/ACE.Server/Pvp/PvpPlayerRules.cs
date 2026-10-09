using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The pure rules behind Player_PvpArena.cs's predicates and the in-match death waivers, split out so
    /// they can be unit tested without a live Player. Every Player-side predicate is a one-line call into
    /// this class; nothing here reads a live server type.
    /// </summary>
    public static class PvpPlayerRules
    {
        /// <summary>
        /// A binding counts only when it names a match, exactly as <see cref="PvpArenaGate"/> decides it. A
        /// null binding (the state of every player until C3 binds one) is never "in a match".
        /// </summary>
        public static bool InMatch(PvpPlayerBinding binding) => binding != null && binding.Match != null;

        public static bool ClassAbilityMask(PvpPlayerBinding binding) => InMatch(binding) && binding.ClassAbilitiesSuppressed;

        public static bool EquipmentModMask(PvpPlayerBinding binding) => InMatch(binding) && binding.EquipmentModsSuppressed;

        public static bool WeaponModMask(PvpPlayerBinding binding) => InMatch(binding) && binding.WeaponModsSuppressed;

        public static bool PickupBoonMask(PvpPlayerBinding binding) => InMatch(binding) && binding.PickupBoonsSuppressed;

        public static bool TurnSpeedMask(PvpPlayerBinding binding) => InMatch(binding) && binding.TurnSpeedSuppressed;

        /// <summary>
        /// The one shape every combined suppression predicate has (DESIGN "Must stay identical"): the PK facet
        /// rule OR the arena mask. ClassAbilitySuppressed, EquipmentModSuppressed, PickupBoonSuppressed and
        /// TurnSpeedSuppressed all resolve through here; WeaponModSuppressed resolves through
        /// WeaponModSuppression.Suppressed, which has the same body.
        /// </summary>
        public static bool Suppressed(bool pkFacetRuleActive, bool arenaMaskActive) => pkFacetRuleActive || arenaMaskActive;

        /// <summary>Both bound to the same match on the same team. Decides whether a gate refusal gets the friendly-fire line.</summary>
        public static bool IsTeammate(PvpPlayerBinding a, PvpPlayerBinding b)
            => InMatch(a) && InMatch(b) && a.Match.MatchId == b.Match.MatchId && a.TeamIndex == b.TeamIndex;

        /// <summary>
        /// Player.CheckFacetGates' arena gate: ANY facet switch is refused while PvpMatchManager.Status(player).Kind is
        /// Queued, AwaitingAccept or InMatch. The PK facet is no longer an arena requirement (PvP Template Facets),
        /// so this no longer keeps anyone on a facet. It protects the template apply instead: a facet switch is a
        /// multi-step strip and re-equip with an asynchronous vault-poll re-entry, and the apply captures the worn
        /// set and the build into the restore record. A switch still in flight when the apply runs could capture a
        /// half-switched build, which the restore would then faithfully put back. InMatch matters most: between
        /// dispatch and the bind the status is InMatch but the player is not yet IsInPvpMatch, so CheckFacetGates'
        /// own in-match gate does not cover that window. Queued and AwaitingAccept are covered too, because a switch
        /// started there can still be in flight when the match forms and dispatches. Pure, so each status is tested.
        /// </summary>
        internal static bool RefusesFacetSwitchForArena(PvpStatusKind status)
            => status == PvpStatusKind.Queued || status == PvpStatusKind.AwaitingAccept || status == PvpStatusKind.InMatch;

        /// <summary>
        /// Bound to a match whose published state is Live. Reads the binding's State, the same snapshot
        /// <see cref="PvpArenaGate"/> reads, never PvpMatch.State (the coordinator publishes state changes
        /// through ReplacePvpBinding).
        /// </summary>
        public static bool InLiveMatch(PvpPlayerBinding binding) => InMatch(binding) && binding.State == PvpMatchState.Live;

        /// <summary>Both bound to the same match and it is Live for both - the arena scope of Rules.PvpClassifier.</summary>
        public static bool InSameLiveMatch(PvpPlayerBinding a, PvpPlayerBinding b)
            => InLiveMatch(a) && InLiveMatch(b) && a.Match.MatchId == b.Match.MatchId;

        /// <summary>
        /// Bound to a Live match that has gone to overtime (Docs/Pvp/DESIGN.md "Overtime"): the binding's own
        /// OvertimeSinceUtc snapshot, never a setting. False for no binding, a non-Live state, or regulation.
        /// </summary>
        public static bool InOvertime(PvpPlayerBinding binding) => InLiveMatch(binding) && binding.OvertimeSinceUtc.HasValue;

        /// <summary>
        /// Both bound to the SAME Live 1v1 match - the scope of the arena 1v1 tunables (pvp_arena_dmg_mod_1v1,
        /// pvp_arena_healkit_skill_cap_1v1, pvp_arena_healkit_restoration_cap_1v1). A 2v2 or FFA match, even
        /// Live, is never this - the mode key must be ArenaMapCatalog.OneVOneKey for BOTH sides' match record
        /// (they are the same match, so checking one side's ModeKey after InSameLiveMatch is sufficient, but
        /// both are read here so the predicate stays correct even if that invariant is ever relaxed).
        /// </summary>
        public static bool InSameLive1v1Match(PvpPlayerBinding a, PvpPlayerBinding b)
            => InSameLiveMatch(a, b) && a.Match.ModeKey == ArenaMapCatalog.OneVOneKey && b.Match.ModeKey == ArenaMapCatalog.OneVOneKey;

        /// <summary>Bound to a match whose published state is Countdown (the pre-match window).</summary>
        public static bool InCountdown(PvpPlayerBinding binding) => InMatch(binding) && binding.State == PvpMatchState.Countdown;

        /// <summary>
        /// TRUE when the rare-gem-buff strip (choke point RB1) must wait: either side of the hit is in a match
        /// Countdown. A Countdown vuln (PvpArenaSpellRules.IsCountdownVuln) still sets the PK timestamp (and so the dispel-vuln lock), but
        /// does not strip rares before Live; the first Live PK action strips them anyway.
        /// </summary>
        public static bool RareStripDeferred(PvpPlayerBinding self, PvpPlayerBinding opponent) => InCountdown(self) || InCountdown(opponent);

        /// <summary>Both bound to the SAME Live FFA match - the scope of pvp_arena_dmg_mod_ffa. The FFA twin of <see cref="InSameLive1v1Match"/>.</summary>
        public static bool InSameLiveFfaMatch(PvpPlayerBinding a, PvpPlayerBinding b)
            => InSameLiveMatch(a, b) && a.Match.ModeKey == ArenaMapCatalog.FfaKey && b.Match.ModeKey == ArenaMapCatalog.FfaKey;

        /// <summary>
        /// Both bound to the SAME Live battleground match, whatever its mode (King of the Hill, Attack/Defend, any later one) - the scope
        /// of pvp_bg_dmg_mod. Scoped by "a battleground mode key" (<see cref="Battlegrounds.BattlegroundModes.IsBattlegroundModeKey"/>),
        /// never by a named mode, so a new mode is covered without a change here. Disjoint from the 1v1 and FFA predicates: a match has one key.
        /// </summary>
        public static bool InSameLiveBattlegroundMatch(PvpPlayerBinding a, PvpPlayerBinding b)
            => InSameLiveMatch(a, b) && Battlegrounds.BattlegroundModes.IsBattlegroundModeKey(a.Match.ModeKey) && Battlegrounds.BattlegroundModes.IsBattlegroundModeKey(b.Match.ModeKey);

        /// <summary>
        /// EnterPvpMatch removes these: a harmful enchantment (a DoT or debuff) that ANOTHER PLAYER put on this
        /// player before entry, so a fight outside cannot keep hurting them inside the sealed match. Identified
        /// by the registry entry's CasterObjectId, which EnchantmentManager stamps on every entry (the caster, or
        /// the player themself when there is none) and which is a player guid only when a player cast it.
        ///
        /// Left alone: beneficial entries, self-cast ones, monster-cast ones, item auras (Duration -1), cooldowns
        /// (spell id above short.MaxValue), and vitae (stamped with the player's own guid). ALSO LEFT ALONE, and
        /// not identifiable: a harmful spell another player's WEAPON cast (a cast-on-strike proc) is stamped with
        /// the item's guid, not the player's, and nothing on the entry names the item's wielder.
        /// </summary>
        public static bool IsHarmFromAnotherPlayer(PropertiesEnchantmentRegistry entry, uint selfGuid)
        {
            if (entry == null)
                return false;

            if (entry.StatModType.HasFlag(EnchantmentTypeFlags.Beneficial) || entry.Duration == -1 || entry.SpellId > short.MaxValue)
                return false;

            return entry.CasterObjectId != selfGuid && ObjectGuid.IsPlayer(entry.CasterObjectId);
        }

        /// <summary>
        /// Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): every player-driven fellowship change (create,
        /// recruit, dismiss, new leader, openness, lock, quit, the mutating /fship subcommands, a direct join) is refused
        /// while the player holds a battleground binding whose match builds team fellowships, from Staging through
        /// Resolving, and in the Closed death hold. Never for an arena binding, a binding without the flag, or
        /// AwaitingAccept/Canceled. The
        /// system paths (team formation, release, dispose, logout) do not consult this.
        /// </summary>
        public static bool RefusesFellowshipChange(PvpPlayerBinding binding)
        {
            if (!InMatch(binding) || !binding.TeamFellowship)
                return false;

            if (!Battlegrounds.BattlegroundModes.IsBattlegroundModeKey(binding.Match.ModeKey))
                return false;

            switch (binding.State)
            {
                case PvpMatchState.Staging:
                case PvpMatchState.Countdown:
                case PvpMatchState.Live:
                case PvpMatchState.Resolving:
                // The death hold: MarkOut(Died) publishes Closed to a player dying out of the match
                // (PvpMatchCoordinator.MarkOut, the only Closed binding ever published) and they stay in the team
                // fellowship until their death return reaches ExitSeat; a dead leader must not reshape it meanwhile.
                case PvpMatchState.Closed:
                    return true;

                default:
                    return false;
            }
        }

        // ---------------- battleground respawn (Docs/Pvp/BATTLEGROUNDS.md "Respawn") ----------------

        /// <summary>Bound to a match and dead or waiting in the pen. Always false for an arena binding.</summary>
        public static bool IsRespawning(PvpPlayerBinding binding) => InMatch(binding) && binding.Respawning;

        /// <summary>
        /// Casting is refused while respawning, so a player in the pen cannot heal or buff teammates through the seal
        /// (Docs/Pvp/BATTLEGROUNDS.md "Risks": "A respawning player heals teammates from the pen").
        /// </summary>
        public static bool CastRefusedWhileRespawning(PvpPlayerBinding binding) => IsRespawning(binding);

        /// <summary>
        /// A healing kit used on ANOTHER player is refused while respawning, the kit counterpart of
        /// <see cref="CastRefusedWhileRespawning"/>: no healing teammates from the pen. A kit on oneself is left alone.
        /// </summary>
        public static bool HealKitRefusedWhileRespawning(PvpPlayerBinding binding, bool onSelf) => IsRespawning(binding) && !onSelf;

        /// <summary>The vitals a battleground respawn fills: all three.</summary>
        public static readonly ACE.Entity.Enum.Properties.PropertyAttribute2nd[] BattlegroundRespawnVitals =
        {
            ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth,
            ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxStamina,
            ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxMana
        };

        /// <summary>
        /// Sets every <see cref="BattlegroundRespawnVitals"/> vital to its maximum. Reads and writes are delegates so the
        /// rule is pinned without a constructed Player (Player.RespawnInBattleground passes Vitals and UpdateVital).
        /// </summary>
        public static void RestoreRespawnVitals(Func<ACE.Entity.Enum.Properties.PropertyAttribute2nd, uint> maxOf, Action<ACE.Entity.Enum.Properties.PropertyAttribute2nd, uint> set)
        {
            foreach (var vital in BattlegroundRespawnVitals)
                set(vital, maxOf(vital));
        }

        /// <summary>
        /// Where a battleground death teleport goes: the latched pen, but ONLY when the binding the player holds right
        /// now still names the same match and is Live. Anything else - no pen latched (an arena or open-world death),
        /// the match ended or was canceled, the player was exited - returns null, and the caller takes the existing
        /// exit path (the EphemeralRealmExitTo stamp).
        /// </summary>
        public static Position DeathDestination(Position latchedPen, Guid latchedMatchId, PvpPlayerBinding current)
        {
            if (latchedPen == null || latchedMatchId == Guid.Empty)
                return null;

            if (!InLiveMatch(current) || current.Match.MatchId != latchedMatchId)
                return null;

            return latchedPen;
        }

        /// <summary>
        /// The completion backstop: a player who went to the pen must be sent to their stamped exit when the binding
        /// they hold at the end of the death sequence is no longer Live for the match they died in.
        /// </summary>
        public static bool PenReturnNeeded(Guid latchedMatchId, PvpPlayerBinding current)
            => !InLiveMatch(current) || current.Match.MatchId != latchedMatchId;

        /// <summary>
        /// An exit the coordinator issued for a seat it believed was still dying (ExitMatchFromPen) teleports the player
        /// home only when the death sequence has already finished and left them standing in the pen's instance; while
        /// the sequence is still running, the death teleport or the backstop owns the trip. Both run on the player's
        /// own queue, so exactly one of them moves the player.
        /// </summary>
        public static bool ShouldReturnFromPen(Position pen, bool inDeathProcess, uint? playerInstance)
            => pen != null && !inDeathProcess && playerInstance.HasValue && playerInstance.Value == pen.Instance;

        /// <summary>
        /// PvP Template Facets: an exit the coordinator issued for a seat still in the death sequence (ExitMatchFromPen
        /// while dying) does NOT restore the template on the spot - that would rewrite the build of a Health-0 player
        /// mid-death. The restore is left to the end of the death sequence: the pen backstop (PvpPenBackstopNeeded ->
        /// ReturnFromBattlegroundPenNow), or the death-arrival restore when the death did not go to the pen. Every other
        /// exit, including an exit from the pen once the death has finished, restores at once.
        /// </summary>
        public static bool DefersTemplateRestoreToPenBackstop(bool returnFromPen, bool inDeathProcess)
            => returnFromPen && inDeathProcess;

        /// <summary>
        /// The Respawn action's guard, read on the player's own queue: still bound to that match, standing in the match
        /// instance, alive and out of the death sequence.
        /// </summary>
        public static bool CanRespawn(PvpPlayerBinding binding, Guid matchId, uint? playerInstance, uint matchInstance, bool isDead, bool inDeathProcess)
            => InMatch(binding) && binding.Match.MatchId == matchId && playerInstance.HasValue && playerInstance.Value == matchInstance && !isDead && !inDeathProcess;

        // ---------------- in-match death waivers (DESIGN H5) ----------------
        //
        // Each takes the LATCHED pvpMatchDeathInProgress (never a live binding read) and the waiver latched
        // beside it. A null waiver with the latch set is treated as the ruling's fixed floor - no vitae, no
        // item loss, no respite - and keeps enchantments, the shipped default of
        // pvp_arena_death_keeps_enchantments. It cannot occur (Die() latches both together); it is handled so
        // a future caller cannot turn a missing waiver into a penalty.

        /// <summary>Player.Die's vitae site skips InflictVitaePenalty when this is true.</summary>
        public static bool WaivesVitae(bool pvpMatchDeath, PvpDeathWaiver waiver) => pvpMatchDeath && (waiver?.NoVitae ?? true);

        /// <summary>Player.Die's enchantment purge is skipped entirely when this is true (pvp_arena_death_keeps_enchantments).</summary>
        public static bool WaivesEnchantmentPurge(bool pvpMatchDeath, PvpDeathWaiver waiver) => pvpMatchDeath && (waiver?.KeepsEnchantments ?? true);

        /// <summary>Player.Die's delayed chain skips SetMinimumTimeSincePK (the respite and its NPK flip) when this is true.</summary>
        public static bool WaivesRespite(bool pvpMatchDeath, PvpDeathWaiver waiver) => pvpMatchDeath && (waiver?.NoRespite ?? true);

        /// <summary>CalculateDeathItems and CalculateDeathItems_Olthoi drop nothing when this is true.</summary>
        public static bool WaivesItemLoss(bool pvpMatchDeath, PvpDeathWaiver waiver) => pvpMatchDeath && (waiver?.NoItemLoss ?? true);
    }
}
