using System;
using System.Globalization;

using ACE.Entity;
using ACE.Server.Command.Handlers;

namespace ACE.Server.Realms
{
    /// <summary>
    /// A config loc string with an optional realm: the /teleloc token order (cell x y z qw qx qy qz) plus an
    /// optional trailing <c>@R</c> token naming the realm the position belongs to, e.g.
    /// <c>0x01F50262 39.971748 -118.261452 6.005000 0.663589 0.000000 0.000000 0.748097 @1</c>.
    ///
    /// A plain /teleloc string cannot name a realm, and a position with no realm silently means "whatever
    /// instance the caller supplies" - for mule_bind_position that was the converting player's CURRENT instance,
    /// which is only right while the bind target and the player are in the same realm. With <c>@R</c> the
    /// position is bound to realm R's default instance instead, and an unregistered R is refused rather than
    /// quietly binding into some other copy of the landblock.
    ///
    /// Pure apart from the injected realm lookup, so it is unit tested directly.
    /// </summary>
    public static class RealmLocString
    {
        /// <summary>
        /// Parses <paramref name="raw"/>. Without a trailing <c>@R</c> the position takes
        /// <paramref name="instanceWithoutRealm"/>, exactly as before this token existed. With one, the realm
        /// must parse (decimal, 0..32767) and <paramref name="getRealm"/> must return it, and the position takes
        /// that realm's <see cref="WorldRealm.DefaultInstanceID"/>. False with <paramref name="error"/> set on any
        /// failure; <paramref name="realmNotRegistered"/> distinguishes "the realm is not loaded on this world"
        /// from a malformed string, since the former is a deployment-order problem rather than a typo.
        /// </summary>
        public static bool TryParse(string raw, uint instanceWithoutRealm, Func<ushort, WorldRealm> getRealm,
            out Position position, out string error, out bool realmNotRegistered)
        {
            position = null;
            error = null;
            realmNotRegistered = false;

            var tokens = raw?.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens == null || tokens.Length == 0)
            {
                error = "no tokens";
                return false;
            }

            var instance = instanceWithoutRealm;

            var last = tokens[tokens.Length - 1];

            if (last.StartsWith("@", StringComparison.Ordinal))
            {
                if (!ushort.TryParse(last.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var realmId) || realmId > LandblockRealmList.MaxRealmId)
                {
                    error = $"'{last}' is not a valid realm token (expected @R with R a realm id, e.g. @1).";
                    return false;
                }

                var realm = getRealm?.Invoke(realmId);

                if (realm == null)
                {
                    realmNotRegistered = true;
                    error = $"realm {realmId} is not in the realm registry.";
                    return false;
                }

                instance = realm.DefaultInstanceID;

                Array.Resize(ref tokens, tokens.Length - 1);

                if (tokens.Length == 0)
                {
                    error = "no position tokens before the realm token";
                    return false;
                }
            }

            return AdminCommands.TryParseLocPosition(tokens, instance, out position, out error);
        }
    }
}
