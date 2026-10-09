using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Entity.Facets;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The PK facet's player-killer status wiring. The decisions themselves are pure and live in
    /// <see cref="FacetPk"/>; this file only reads the live inputs and applies the answers. See
    /// Docs/Facets/DESIGN.md, "The PK facet".
    ///
    /// The status is DERIVED, never stored as a lasting choice: nothing here writes PkLevelModifier
    /// (PropertyInt 99). While the facet rules are active the rule overrides the lasting level, and the
    /// moment they are switched off the lasting level governs again, exactly as before facets existed.
    /// </summary>
    partial class Player
    {
        /// <summary>facet_enabled AND facet_pk_enabled, read through the same seam as every other facet dial.</summary>
        internal static bool FacetPkRulesActiveNow() => FacetTunables.PkRulesSource();

        /// <summary>
        /// THE predicate every PK-facet SUPPRESSION site reads (class abilities and the point freeze, weapon mods via WeaponModSuppressed,
        /// equipment mods, pick-up and turn speed): standing on the PK facet AND the facet PK rule active.
        /// A player left on the PK facet after facet_enabled or facet_pk_enabled is switched off is an
        /// ordinary player - nothing is suppressed - which matters because with facet_enabled off they
        /// cannot switch away at all. No site may inline either half.
        ///
        /// Suppression sites do not read this directly: each reads a combined predicate (ClassAbilitySuppressed,
        /// EquipmentModSuppressed, WeaponModSuppressed, PickupBoonSuppressed, TurnSpeedSuppressed) that ORs this
        /// with the PvP arena mask (Player_PvpArena.cs, Docs/Pvp/DESIGN.md "Must stay identical").
        ///
        /// Cost: IsOnPkFacet (one biota property read) is evaluated first and is false for everyone not on
        /// the PK facet, so only PK-facet players ever pay the two cached GetBool reads behind
        /// FacetPkRulesActiveNow. See FacetPk.SuppressionActive.
        /// </summary>
        public bool IsPkFacetRuleActive => FacetPk.SuppressionActive(IsOnPkFacet, FacetPkRulesActiveNow);

        /// <summary>What the facet rules require of this player right now (PK, NPK, or nothing).</summary>
        public FacetPkRequirement FacetPkRequirementNow()
        {
            // Short-circuit before touching any character property: with the rules off (the stock
            // facet_enabled=false case) this costs the dial read and nothing else.
            if (!FacetPkRulesActiveNow())
                return FacetPkRequirement.None;

            return FacetPk.RequiredStatus(true, IsOnPkFacet, IsOlthoiPlayer);
        }

        /// <summary>
        /// Forces PlayerKillerStatus to what the facet rules require, when <see cref="FacetPk.ShouldReassert"/>
        /// says it is out of line. Returns true when it changed anything. Free and every non-ordinary status
        /// are never touched, the respite keeps its NPK window on the PK facet, and an active PK timer defers
        /// an NPK reassert on any other facet.
        ///
        /// <paramref name="broadcast"/> is false only at login, before the character is in the world: the
        /// status then simply goes out with the character's own description.
        /// </summary>
        internal bool EnforceFacetPkStatus(string context, bool broadcast, bool warn)
        {
            // PvP arena (Docs/Pvp/DESIGN.md H7): a player in a match is PK Lite for the match, and ExitPvpMatch
            // restores their own status afterwards. Neither this heartbeat backstop nor a login or switch may
            // overwrite PK Lite in between.
            if (IsInPvpMatch)
                return false;

            var required = FacetPkRequirementNow();

            if (required == FacetPkRequirement.None)
                return false;

            var previous = PlayerKillerStatus;

            if (!FacetPk.ShouldReassert(required, previous, MinimumTimeSincePk != null, PKTimerActive, out var target))
                return false;

            PlayerKillerStatus = target;

            if (broadcast)
            {
                EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus));
                Session?.Network.EnqueueSend(new GameMessageSystemChat(ComposeFacetPkStatusLine(target), ChatMessageType.Broadcast));
            }

            var line = $"[FACET] {Name} (0x{Guid.Full:X8}): {context} set PlayerKillerStatus {previous} -> {target} (facet rule {required}, slot {ActiveFacetSlot}).";

            if (warn)
                log.Warn(line);
            else
                log.Info(line);

            return true;
        }

        /// <summary>The chat line for a status the facet rule just set. Pure, for unit tests.</summary>
        internal static string ComposeFacetPkStatusLine(PlayerKillerStatus status)
        {
            return status == PlayerKillerStatus.PK
                ? "Your PK facet keeps you a player killer."
                : "Only your PK facet can be a player killer, so you are a non-player killer on this facet.";
        }

        /// <summary>
        /// The heartbeat backstop (Player.Heartbeat, right after PK_DeathTick). Every writer of
        /// PlayerKillerStatus that the facet rules do not gate directly - @pk, an emote writing the stat, the
        /// pk_server / pkl_server world toggle, a login-time retail adjustment - is corrected here within one
        /// heartbeat, so no path can leave a player off the facet rule for longer than that. Logged as a
        /// warning because on a running server something other than the rule moved the status.
        /// </summary>
        public void FacetPkHeartbeat()
        {
            // Caught here: an exception escaping into Player.Heartbeat would skip everything after this call
            // for this beat (the mule leash, the offline-bonus reconcile, the periodic save).
            try
            {
                RefreshPkFacetSuppressionCaches();

                EnforceFacetPkStatus("the heartbeat backstop", broadcast: true, warn: true);
            }
            catch (System.Exception ex)
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): the PK facet heartbeat backstop threw; the rest of the heartbeat is unaffected.", ex);
            }
        }

        /// <summary>
        /// The <see cref="IsPkFacetRuleActive"/> answer the class ability hook caches were last built against.
        /// Transient: a fresh Player starts at false with its caches unbuilt, and the first heartbeat settles it.
        /// </summary>
        private bool pkFacetSuppressionSeen;

        /// <summary>
        /// The hook caches (BuildClassAbilityHookCache) bake in <see cref="IsPkFacetRuleActive"/> and are only
        /// rebuilt when invalidated. A switch invalidates them itself; this catches the other way the answer
        /// changes - facet_enabled or facet_pk_enabled being flipped while a player stands on the PK facet -
        /// within one heartbeat. Turning suppression ON also drops the in-memory class ability state, exactly
        /// as entering the PK facet does.
        /// </summary>
        private void RefreshPkFacetSuppressionCaches()
        {
            // The caches bake in ClassAbilitySuppressed (the facet rule OR the PvP arena mask), so this tracks that
            // combined answer; EnterPvpMatch and ExitPvpMatch also rebuild the caches themselves.
            var suppressed = ClassAbilitySuppressed;

            if (suppressed == pkFacetSuppressionSeen)
                return;

            pkFacetSuppressionSeen = suppressed;

            InvalidateClassAbilityHookCaches();

            if (suppressed)
                ResetClassAbilityTransientState();
        }

        /// <summary>
        /// Login: applies the rule before the character enters the world, so a player whose stored status
        /// disagrees with their facet never appears in the world with the wrong one. Not broadcast (the
        /// character is not in the world yet); the explanation line is sent a moment later, the same way the
        /// retail PKLite-to-NPK login adjustment right before this call sends its own.
        /// </summary>
        public void FacetPkLoginCheck()
        {
            if (!EnforceFacetPkStatus("login", broadcast: false, warn: false))
                return;

            var status = PlayerKillerStatus;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(3.0f);
            actionChain.AddAction(this, () =>
            {
                Session?.Network.EnqueueSend(new GameMessageSystemChat(ComposeFacetPkStatusLine(status), ChatMessageType.Broadcast));
            });
            actionChain.EnqueueChain();
        }

        /// <summary>
        /// A facet switch's PK side effects, run by TrySwitchFacet after ActiveFacetSlot is set and before
        /// the saves. Every refusal has already happened by then; nothing here can fail the switch.
        ///
        /// Entering the PK facet: every in-memory class ability stack and ward is dropped
        /// (<see cref="ResetClassAbilityTransientState"/>), live combat pets are retired (a pet summoned on
        /// another facet carries that facet's class ability boosts), and the status becomes PK.
        ///
        /// Leaving it: the status becomes what <see cref="FacetPk.StatusOnLeavingPkFacet"/> says - NPK while
        /// the rule is active. A switch is refused while the PK timer is active, so this never flips anyone
        /// mid-fight. A player in respite is already NPK, and the respite keeps running its full length on
        /// the new facet (FacetPk.RespiteTick), so /facet pk stays refused until it ends.
        ///
        /// The class ability hook caches are rebuilt on EVERY switch, not only one touching the PK facet,
        /// because the rank-0 handlers are gated on IsPkFacetRuleActive and the slot just changed.
        /// </summary>
        private void ApplyFacetPkTransition(bool leftPkFacet, bool enteredPkFacet)
        {
            InvalidateClassAbilityHookCaches();
            pkFacetSuppressionSeen = ClassAbilitySuppressed;

            if (enteredPkFacet)
            {
                ResetClassAbilityTransientState();

                var retired = RetireCombatPetsForPkFacet();

                if (EnforceFacetPkStatus("a switch to the PK facet", broadcast: true, warn: false) == false && PlayerKillerStatus != PlayerKillerStatus.PK)
                    log.Warn($"[FACET] {Name} (0x{Guid.Full:X8}): switched to the PK facet but PlayerKillerStatus is {PlayerKillerStatus}, not PK (facet rule {FacetPkRequirementNow()}). The heartbeat backstop will retry.");

                if (retired > 0)
                    Session?.Network.EnqueueSend(new GameMessageSystemChat("Your combat pets are dismissed as you turn to your PK facet.", ChatMessageType.Broadcast));

                return;
            }

            if (leftPkFacet)
            {
                var current = PlayerKillerStatus;

                if (current != PlayerKillerStatus.PK && current != PlayerKillerStatus.NPK && current != PlayerKillerStatus.PKLite)
                    return;   // Free, or anything else an admin set - never touched

                // PvP arena (H7): PK Lite belongs to the match until ExitPvpMatch restores the player's own status
                if (IsInPvpMatch)
                    return;

                var target = FacetPk.StatusOnLeavingPkFacet(FacetPkRequirementNow(), PkLevel,
                    PropertyManager.GetBool("pk_server").Item, PropertyManager.GetBool("pkl_server").Item);

                if (target == current)
                    return;

                PlayerKillerStatus = target;

                EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus));

                if (target == PlayerKillerStatus.NPK)
                    Session?.Network.EnqueueSend(new GameMessageSystemChat("You leave your PK facet and are no longer a player killer.", ChatMessageType.Broadcast));

                log.Info($"[FACET] {Name} (0x{Guid.Full:X8}): left the PK facet; PlayerKillerStatus {current} -> {target}.");
                return;
            }

            // Between two non-PK facets the rule's answer does not change, so this only ever corrects a status
            // that was already out of line.
            EnforceFacetPkStatus("a facet switch", broadcast: true, warn: true);
        }

        /// <summary>
        /// Dismisses the owner's live combat pets (both slots) through the same Destroy the essence swap uses
        /// (Pet.Init's queued dismissal). Passive pets are cosmetic and stay. Returns how many were dismissed.
        /// </summary>
        private int RetireCombatPetsForPkFacet()
        {
            var retired = 0;

            foreach (var pet in new[] { CurrentActivePet, SecondaryActivePet })
            {
                if (pet is CombatPet combatPet && !combatPet.IsDestroyed)
                {
                    combatPet.Destroy();
                    retired++;
                }
            }

            return retired;
        }
    }
}
