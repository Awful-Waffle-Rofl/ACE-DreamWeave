using System;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;

namespace ACE.Server.Realms
{
    /// <summary>
    /// Where the client-native Marketplace recall (Player.HandleActionTeleToMarketPlace) lands.
    ///
    /// The Marketplace lives in realm 1's copy of Aerfalle Keep (landblock 0x01F5), and realm 0's copy of that
    /// same landblock is the ordinary retail dungeon. A weenie Destination cannot say which: Weenie.GetPosition
    /// hardcodes instance 0 (ACE.Entity/Models/WeenieExtensions.cs), so the realm comes from the Marketplace
    /// portal weenie's PortalRealm (9022) - the same property that routes the portal itself
    /// (Portal.ApplyPortalRealm) - and the drop is bound to that realm's default instance.
    ///
    /// Resolved on every recall rather than once in a static initializer: the old type-init static read the
    /// world database the first time anything touched Player (which is also why several test classes had to
    /// seed "portalmarketplace"), and it froze whatever the weenie said at that moment, so a content reload
    /// never reached it. The weenie read is a cache hit after the first call.
    ///
    /// FAILS CLOSED. If no realm can be determined, or the realm is not registered on this world, the recall is
    /// refused with <see cref="UnavailableMessage"/>. It never falls back to realm 0: realm 0's 0x01F5 is a
    /// dungeon full of monsters, which is the worst possible place to drop a player who asked for the
    /// Marketplace.
    /// </summary>
    public static class MarketplaceRecall
    {
        /// <summary>The Marketplace portal weenie (wcid 23032).</summary>
        public const string PortalWeenieClassName = "portalmarketplace";

        /// <summary>The realm the fallback drop belongs to (realm 1, the Weave).</summary>
        public const ushort FallbackRealmId = 1;

        /// <summary>
        /// Used only when the portal weenie itself is missing from the world database, or carries no Destination:
        /// the Marketplace landing, 0x01F50224 [50 -132.618988 0.005] facing 1 0 0 0 (qw qx qy qz), the spot the
        /// owner chose in realm 1 on 2026-09-28 (read with /loc). It replaced the Aerfalle Keep entry point
        /// 0x01F50229 [50 -180 0.005], which was the realm-0 dungeon entry portal's own Destination (wcid 7417).
        /// Instance 0 here, rebound to <see cref="FallbackRealmId"/>'s default instance at resolve time.
        /// </summary>
        public static Position FallbackDrop => new Position(0x01F50224, 50f, -132.618988f, 0.005f, 0f, 0f, 0f, 1f, 0);

        public const string UnavailableMessage = "The Marketplace cannot be reached right now. Please report this to an administrator.";

        /// <summary>
        /// Pure resolution rule, with the weenie facts and the realm lookup injected:
        /// <list type="bullet">
        ///   <item>No portal weenie at all (<paramref name="weenieFound"/> false): <see cref="FallbackDrop"/> in
        ///   <see cref="FallbackRealmId"/>.</item>
        ///   <item>Portal weenie found: its Destination (or <see cref="FallbackDrop"/> if it has none) in the realm
        ///   its PortalRealm names. A weenie with NO PortalRealm is refused - that is the pre-move content, whose
        ///   Destination still points at the old realm-0 Marketplace, and guessing a realm for it is exactly the
        ///   silent wrong-copy landing this type exists to prevent.</item>
        ///   <item>The chosen realm must be a valid id, at least 1 (realm 0 is refused even when named explicitly),
        ///   and registered (<paramref name="getRealm"/>), else refused.</item>
        /// </list>
        /// On success <paramref name="drop"/> is a fresh Position bound to the realm's default instance.
        /// </summary>
        internal static bool TryResolve(bool weenieFound, Position weenieDestination, int? weeniePortalRealm,
            Func<ushort, WorldRealm> getRealm, out Position drop, out string failure)
        {
            drop = null;
            failure = null;

            int realmValue;
            Position source;

            if (!weenieFound)
            {
                realmValue = FallbackRealmId;
                source = FallbackDrop;
            }
            else
            {
                if (weeniePortalRealm == null)
                {
                    failure = $"{PortalWeenieClassName} has no PortalRealm (9022), so the Marketplace's realm is unknown";
                    return false;
                }

                realmValue = weeniePortalRealm.Value;
                source = weenieDestination ?? FallbackDrop;
            }

            if (realmValue < 0 || realmValue > LandblockRealmList.MaxRealmId)
            {
                failure = $"{PortalWeenieClassName} names PortalRealm {realmValue}, which is not a valid realm id";
                return false;
            }

            // Realm 0 is refused even when named explicitly. It is always registered (RealmManager.BaseRealm), so
            // the registry check below would accept it, and realm 0's 0x01F5 is the hostile retail Aerfalle Keep -
            // never the Marketplace. The fail-closed contract is "never realm 0", not "never an unregistered realm".
            if (realmValue == 0)
            {
                failure = $"{PortalWeenieClassName} names PortalRealm {realmValue}; the Marketplace never resolves into realm 0";
                return false;
            }

            var realm = getRealm?.Invoke((ushort)realmValue);

            if (realm == null)
            {
                failure = $"realm {realmValue} is not in the realm registry";
                return false;
            }

            drop = new Position(source, realm.DefaultInstanceID);
            return true;
        }

        /// <summary>Live resolution: reads the portal weenie from the world cache and the realm registry.</summary>
        public static bool TryResolve(out Position drop, out string failure)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(PortalWeenieClassName);

            return TryResolve(
                weenie != null,
                weenie?.GetPosition(PositionType.Destination),
                weenie?.GetProperty(PropertyInt.PortalRealm),
                RealmManager.GetRealm,
                out drop,
                out failure);
        }
    }
}
