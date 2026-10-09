using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.WorldObjects
{
    partial class Portal
    {
        public PortalBitmask PortalRestrictions
        {
            get => (PortalBitmask)(GetProperty(PropertyInt.PortalBitmask) ?? (int)PortalBitmask.Unrestricted);
            set { if (value == PortalBitmask.Undef) RemoveProperty(PropertyInt.PortalBitmask); else SetProperty(PropertyInt.PortalBitmask, (int)value); }
        }

        public bool NoRecall => (PortalRestrictions & PortalBitmask.NoRecall) != 0 || IsRecallLockedByRealmRouting;

        public bool NoTie => NoRecall;

        public bool NoSummon => (PortalRestrictions & PortalBitmask.NoSummon) != 0 || IsRecallLockedByRealmRouting;

        /// <summary>
        /// WaffleACE: a portal that routes into a realm or an ephemeral instance - PortalRealm (9022),
        /// PortalInstancing (9021) or PortalExitInstance (9025). What it LOCKS is
        /// <see cref="IsRecallLockedByRealmRouting"/>, which also applies the recall allowlist.
        /// </summary>
        public bool IsRealmRouted =>
            GetProperty(PropertyInt.PortalRealm) != null
            || IsInstanceRouted;

        /// <summary>
        /// PortalInstancing (9021) or PortalExitInstance (9025): the destination is an ephemeral instance, or
        /// wherever the player entered one from.
        /// </summary>
        private bool IsInstanceRouted =>
            (GetProperty(PropertyInt.PortalInstancing) ?? 0) == 1
            || (GetProperty(PropertyInt.PortalExitInstance) ?? 0) == 1;

        /// <summary>
        /// Standing rule: a REALM-routed portal (PortalRealm 9022, PortalInstancing 9021 or PortalExitInstance
        /// 9025) is never recallable, tie-able or summonable from a tie, whatever its PortalBitmask says, so a new
        /// realm portal cannot ship recallable by forgetting the NoRecall bit. Covers ties stored before this rule
        /// too: recall and summon rebuild the portal from its weenie and check here.
        ///
        /// The one exception is OPT-IN (owner decision 2026-09-26, the Marketplace move into realm 1): a portal
        /// whose wcid is listed in realm_portal_recall_allowlist (<see cref="Realms.RealmPortalRecallTunables"/>,
        /// default the two Marketplace portals) AND that is routed ONLY by a persistent PortalRealm (9022 set,
        /// neither 9021 nor 9025) is not locked. Recall, tie and summon of it resolve through the same
        /// Portal.ResolvePortalDestination -> ApplyPortalRealm route walking through it does, so they land in the
        /// portal's realm (WorldObject_Magic.HandleCastSpell_PortalRecall rebuilds the portal from its weenie and
        /// resolves through it; a summoned gateway carries PortalRealm over via CopyForkGatewayProperties). An
        /// allowlisted wcid that is instance-routed stays locked. A summoned gateway is judged by its
        /// OriginalPortal's wcid.
        /// </summary>
        public bool IsRecallLockedByRealmRouting => IsRealmRouted && !IsAllowlistedPersistentRealmPortal;

        private bool IsAllowlistedPersistentRealmPortal =>
            !IsInstanceRouted
            && GetProperty(PropertyInt.PortalRealm) != null
            && IsRecallAllowlisted(OriginalPortal ?? WeenieClassId, Realms.RealmPortalRecallTunables.AllowlistSource());

        /// <summary>Pure membership check, split out so the allowlist rule is testable without a live tunable.</summary>
        internal static bool IsRecallAllowlisted(uint wcid, System.Collections.Generic.IReadOnlySet<uint> allowlist)
        {
            return allowlist != null && allowlist.Contains(wcid);
        }

        /// <summary>
        /// Whether two portal objects carry the same PortalRealm (both absent counts as the same). Tie, recall
        /// and summon all store only a weenie id and re-resolve from a portal REBUILT from that weenie, so a
        /// PortalRealm set on a live object alone (the /portal-realm admin command) would be silently dropped
        /// on recall and land the player in the wrong realm copy. Callers compare the live portal against its
        /// weenie-built twin and refuse to remember it when they differ, so an allowlisted wcid given a live-only
        /// realm override is still not remembered.
        /// </summary>
        internal static bool SamePortalRealm(WorldObject live, WorldObject fromWeenie)
        {
            return SamePortalRealm(live?.GetProperty(PropertyInt.PortalRealm), fromWeenie?.GetProperty(PropertyInt.PortalRealm));
        }

        /// <summary>The one comparison behind both SamePortalRealm overloads: equal values, or both absent.</summary>
        internal static bool SamePortalRealm(int? livePortalRealm, int? weeniePortalRealm)
        {
            return livePortalRealm == weeniePortalRealm;
        }

        /// <summary>
        /// For summoned portals, the DID of the original portal
        /// </summary>
        public uint? OriginalPortal
        {
            get => GetProperty(PropertyDataId.OriginalPortal);
            set { if (!value.HasValue) RemoveProperty(PropertyDataId.OriginalPortal); else SetProperty(PropertyDataId.OriginalPortal, value.Value); }
        }

        public bool? PortalShowDestination
        {
            get => GetProperty(PropertyBool.PortalShowDestination);
            set { if (!value.HasValue) RemoveProperty(PropertyBool.PortalShowDestination); else SetProperty(PropertyBool.PortalShowDestination, value.Value); }
        }

        public string AppraisalPortalDestination
        {
            get => GetProperty(PropertyString.AppraisalPortalDestination);
            set { if (value == null) RemoveProperty(PropertyString.AppraisalPortalDestination); else SetProperty(PropertyString.AppraisalPortalDestination, value); }
        }

        public int SocietyId => 0;

        public bool PortalIgnoresPkAttackTimer
        {
            get => GetProperty(PropertyBool.PortalIgnoresPkAttackTimer) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.PortalIgnoresPkAttackTimer); else SetProperty(PropertyBool.PortalIgnoresPkAttackTimer, value); }
        }

        public SubscriptionStatus? AccountRequirements
        {
            get => (SubscriptionStatus?)GetProperty(PropertyInt.AccountRequirements);
            set { if (value == null) RemoveProperty(PropertyInt.AccountRequirements); else SetProperty(PropertyInt.AccountRequirements, (int)value); }
        }

        public bool? AdvocateQuest
        {
            get => GetProperty(PropertyBool.AdvocateQuest);
            set { if (value == null) RemoveProperty(PropertyBool.AdvocateQuest); else SetProperty(PropertyBool.AdvocateQuest, value.Value); }
        }
    }
}
