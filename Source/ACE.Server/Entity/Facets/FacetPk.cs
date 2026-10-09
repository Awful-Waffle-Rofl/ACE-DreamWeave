using ACE.Entity.Enum;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// What the facet PK rules demand of one player's PlayerKillerStatus right now.
    /// </summary>
    public enum FacetPkRequirement
    {
        /// <summary>
        /// The facet rules say nothing: they are switched off (facet_enabled or facet_pk_enabled is false),
        /// or the player is exempt (an Olthoi player). Retail and fork PK behaviour runs exactly as it
        /// would without facets.
        /// </summary>
        None,

        /// <summary>The player stands on the PK facet, so they are a player killer.</summary>
        PK,

        /// <summary>The player stands on any other facet, so they are a non-player killer.</summary>
        NPK,
    }

    /// <summary>
    /// The PK facet's status rules as pure decisions, so they are unit-testable without a live Player
    /// (ACE.Server.Tests cannot construct one). See Docs/Facets/DESIGN.md, "The PK facet".
    ///
    /// THE RULE, while facet_enabled AND facet_pk_enabled are both true: a player on the PK facet is always
    /// PK, and a player on any other facet - including one who never switched and stands on slot 1 - is
    /// always NPK. Two carve-outs keep retail behaviour intact inside the rule: the post-death respite
    /// (pk_respite_timer) still grants its NPK window on the PK facet, and a player whose PK timer is active
    /// on another facet is not flipped mid-fight. The rule wins over pk_server / pkl_server while it is
    /// active, and it NEVER writes the lasting PkLevelModifier (PropertyInt 99): the status is derived and
    /// overrides whatever the lasting level says.
    /// </summary>
    public static class FacetPk
    {
        /// <summary>The rule is live only when both switches are on.</summary>
        public static bool RulesActive(bool facetsEnabled, bool pkFacetEnabled) => facetsEnabled && pkFacetEnabled;

        /// <summary>
        /// What the rule requires of this player. Olthoi players are exempt: their PK standing is fixed by
        /// their heritage (Player_Death.SetMinimumTimeSincePK skips them the same way), and they cannot enter
        /// the PK facet at all.
        /// </summary>
        public static FacetPkRequirement RequiredStatus(bool rulesActive, bool isOnPkFacet, bool isOlthoi)
        {
            if (!rulesActive || isOlthoi)
                return FacetPkRequirement.None;

            return isOnPkFacet ? FacetPkRequirement.PK : FacetPkRequirement.NPK;
        }

        /// <summary>
        /// The PK level a player resolves to: with the rule active it is the rule's answer and nothing else -
        /// the lasting level and the pk_server / pkl_server flags are overridden. With no rule it is exactly the
        /// retail derivation PK_DeathTick always used: pk_server forces PK, else pkl_server forces PKLite,
        /// else the lasting level. <see cref="RespiteTick"/> uses it for the level a respite ends on (so a
        /// PK-facet player with a lasting level of NPK still returns to PK) and, with the rule off only, for
        /// the retail early clear.
        /// </summary>
        public static PKLevel EffectivePkLevel(FacetPkRequirement required, PKLevel lastingLevel, bool pkServer, bool pklServer)
        {
            switch (required)
            {
                case FacetPkRequirement.PK:
                    return PKLevel.PK;

                case FacetPkRequirement.NPK:
                    return PKLevel.NPK;
            }

            if (pkServer)
                return PKLevel.PK;

            if (pklServer)
                return PKLevel.PKLite;

            return lastingLevel;
        }

        /// <summary>
        /// The heartbeat backstop and the login check: should the player's live status be forced, and to
        /// what. Only the three ordinary statuses are ever touched - Free, and anything else an admin set,
        /// is left alone.
        ///
        /// PK required: NPK or PKLite becomes PK, EXCEPT during the post-death respite
        /// (<paramref name="inRespite"/>, i.e. MinimumTimeSincePk is set), whose NPK window the rule keeps;
        /// the respite tick itself restores PK when the window ends.
        ///
        /// NPK required: PK or PKLite becomes NPK, EXCEPT while the PK timer is active
        /// (<paramref name="pkTimerActive"/>), so a player is never flipped out of a fight they are in; the
        /// next heartbeat after the timer clears does it.
        /// </summary>
        public static bool ShouldReassert(FacetPkRequirement required, PlayerKillerStatus current, bool inRespite, bool pkTimerActive, out PlayerKillerStatus target)
        {
            target = current;

            switch (required)
            {
                case FacetPkRequirement.PK:
                    if (inRespite)
                        return false;

                    if (current == PlayerKillerStatus.NPK || current == PlayerKillerStatus.PKLite)
                    {
                        target = PlayerKillerStatus.PK;
                        return true;
                    }

                    return false;

                case FacetPkRequirement.NPK:
                    if (pkTimerActive)
                        return false;

                    if (current == PlayerKillerStatus.PK || current == PlayerKillerStatus.PKLite)
                    {
                        target = PlayerKillerStatus.NPK;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The status to set on the switch that LEAVES the PK facet, when the player currently holds one of
        /// the three ordinary statuses (the caller leaves Free and anything else alone).
        ///
        /// With the rule active this is always NPK: the target is a non-PK facet. The respite needs no
        /// special case - a player in respite is already NPK, and the respite keeps running its full length
        /// on the non-PK facet (<see cref="RespiteTick"/>), ending on NPK there.
        ///
        /// With no rule (facet_pk_enabled switched off while the player stood on the PK facet), the status
        /// falls back to the retail derivation of <see cref="EffectivePkLevel"/>, so leaving hands back what
        /// the lasting level and the server flags would have given anyway.
        /// </summary>
        public static PlayerKillerStatus StatusOnLeavingPkFacet(FacetPkRequirement requiredAfter, PKLevel lastingLevel, bool pkServer, bool pklServer)
        {
            switch (EffectivePkLevel(requiredAfter, lastingLevel, pkServer, pklServer))
            {
                case PKLevel.PK:
                    return PlayerKillerStatus.PK;

                case PKLevel.PKLite:
                    return PlayerKillerStatus.PKLite;

                default:
                    return PlayerKillerStatus.NPK;
            }
        }

        /// <summary>
        /// The PK altar (PKModifier) decision under the rule. Returns the refusal line, or null when the
        /// altar may proceed to its ordinary retail checks.
        ///   - PK facet: both altars refuse - the facet owns the status.
        ///   - any other facet: the PK altar refuses (that facet is always NPK); the NPK altar is harmless
        ///     there and proceeds.
        ///   - no rule: nothing is refused here.
        /// </summary>
        public static string AltarRefusal(FacetPkRequirement required, bool altarMakesPk)
        {
            switch (required)
            {
                case FacetPkRequirement.PK:
                    return PkFacetAltarRefusal;

                case FacetPkRequirement.NPK:
                    return altarMakesPk ? NonPkFacetPkRefusal : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether the PK facet's suppressions apply: the player stands on the PK facet AND the facet PK rule
        /// is active. <paramref name="rulesActive"/> is only invoked when the player is on the PK facet, so
        /// every other player pays nothing for the rule read. With the rule off, a player left on the PK facet
        /// is an ordinary player (they may be unable to switch away while facet_enabled is off).
        /// </summary>
        public static bool SuppressionActive(bool isOnPkFacet, System.Func<bool> rulesActive)
            => isOnPkFacet && rulesActive != null && rulesActive();

        /// <summary>
        /// Whether a class ability point may be spent, in ANY form - learning a rank, buying or applying a
        /// voucher, or paying a vendor in points. The PK facet freezes every such spend while its rule is
        /// active (<paramref name="suppressed"/> = <see cref="SuppressionActive"/>); earning points is
        /// untouched and accrues to the shared pool.
        /// </summary>
        public static bool CanSpendClassAbilityPoints(bool suppressed) => !suppressed;

        /// <summary>A pick-up speed bonus source (boon count or augmentation count) as the PK facet sees it: zero while suppressed.</summary>
        public static int PickupBonusCount(bool suppressed, int count) => suppressed ? 0 : count;

        /// <summary>The server-initiated turn speed as the PK facet sees it: the unboosted 1.0 while suppressed.</summary>
        public static double TurnSpeed(bool suppressed, double computed) => suppressed ? 1.0 : computed;

        /// <summary>An equipment mod's applied value as the PK facet sees it: 0 while suppressed.</summary>
        public static double EquipmentModValue(bool suppressed, double value) => suppressed ? 0.0 : value;

        /// <summary>
        /// One heartbeat of the post-death respite (Player.PK_DeathTick), as a pure step. Takes the current
        /// MinimumTimeSincePk (null = no respite) and returns the next one; <paramref name="ended"/> is true
        /// on the beat the respite completes, with <paramref name="endLevel"/> the level the status resolves
        /// to (the caller's switch: PK -> PK, PKLite -> PKLite, NPK -> no change).
        ///
        /// THE EARLY CLEAR (drop the respite at once, change nothing) happens ONLY with the facet PK rule
        /// inactive, and then exactly as retail always did: when pk_server / pkl_server / the lasting level
        /// say NPK. With the rule active the respite, once started, runs its full
        /// <paramref name="respiteSeconds"/> whatever facet the player stands on - otherwise dying on the PK
        /// facet, switching to another facet (effective level NPK) and letting the next beat clear the
        /// respite would skip it, and /facet pk (refused while the respite is set) would let them straight
        /// back in. At the end the level is the rule's answer for the facet they stand on THEN: PK on the PK
        /// facet, NPK anywhere else.
        /// </summary>
        public static double? RespiteTick(double? minimumTimeSincePk, double heartbeatSeconds, double respiteSeconds,
            FacetPkRequirement required, PKLevel lastingLevel, bool pkServer, bool pklServer, out bool ended, out PKLevel endLevel)
        {
            ended = false;
            endLevel = PKLevel.NPK;

            if (minimumTimeSincePk == null)
                return null;

            if (required == FacetPkRequirement.None && EffectivePkLevel(FacetPkRequirement.None, lastingLevel, pkServer, pklServer) == PKLevel.NPK)
                return null;

            var next = minimumTimeSincePk.Value + heartbeatSeconds;

            if (next < respiteSeconds)
                return next;

            ended = true;
            endLevel = EffectivePkLevel(required, lastingLevel, pkServer, pklServer);
            return null;
        }

        /// <summary>
        /// The popup (GameEventPopupString) sent on every committed switch INTO the PK facet - never on a
        /// refused switch, on leaving, or at login. Owner-supplied text, ASCII only: the popup is written as
        /// a 16-bit length plus Windows-1252 bytes (Network.Extensions.WriteString16L), and newlines are
        /// plain "\n" like every other popup the server sends. Pure, for unit tests.
        /// </summary>
        public static string ComposeEntryPopup()
        {
            return string.Join("\n", new[]
            {
                "You are now on your PK facet.",
                "",
                "While on this facet:",
                "- You are always a player killer, and cannot become non-PK until you switch away. A PK death still grants the usual respite.",
                "- Class abilities do not work, and class ability points cannot be spent.",
                "- Equipment mods and weapon mods do not work.",
                "- Pickup speed bonuses do not apply.",
                "- Turn speed bonuses do not apply.",
                "- Your other facets are always non-PK.",
                "",
                "Use /facet with a facet number to switch away.",
            });
        }

        public const string PkFacetAltarRefusal = "Your PK facet keeps you a player killer, so this altar cannot change your status. Switch facets to stop being a player killer.";

        public const string NonPkFacetPkRefusal = "Only your PK facet can be a player killer. Switch to it with /facet pk.";

        public const string NonPkFacetPkLiteRefusal = "Your facets other than the PK facet are always non-player killer, so you cannot become a player killer lite. Switch to your PK facet with /facet pk.";

        public const string PkFacetPkLiteRefusal = "Your PK facet keeps you a full player killer, so you cannot become a player killer lite.";

        public const string PkFacetClassAbilityRefusal = "Class abilities and class ability points cannot be used on your PK facet. Switch to another facet to learn or buy them.";
    }
}
