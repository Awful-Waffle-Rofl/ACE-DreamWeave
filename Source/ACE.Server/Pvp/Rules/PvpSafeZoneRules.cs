using ACE.Server.Realms;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// The Marketplace combat-free-zone rule (owner report: "I was able to shoot another PK in the
    /// Marketplace. Marketplace should be a combat free zone."). Landblock-based, so it applies to every
    /// player regardless of PlayerKillerStatus - it is checked ahead of the retail PK rules in
    /// Player.CheckPKStatusVsTarget, not a variant of them.
    ///
    /// Pure - no live server types, no PropertyManager reads - so it is unit tested directly.
    /// </summary>
    public static class PvpSafeZoneRules
    {
        /// <summary>
        /// TRUE when a party standing at <paramref name="landblock"/> in realm <paramref name="realm"/> counts
        /// as being in a configured safe zone, per <see cref="LandblockRealmList.Contains(ushort, ushort, bool)"/>.
        /// A BARE entry (e.g. 016C) matches that landblock in every PERSISTENT realm, as this rule always did; a
        /// REALM-SCOPED entry (e.g. 01F5@1, the Marketplace in its realm-1 copy of Aerfalle Keep) matches only
        /// that realm, so the retail Aerfalle Keep in realm 0 is not a safe zone.
        ///
        /// EXCLUDES ephemeral instances (<paramref name="isEphemeralRealm"/>, from
        /// <c>Position.IsEphemeralRealm</c>) for both entry forms: an arena match, a Proving Grounds run, or a
        /// Thread dungeon instanced onto a listed landblock id is not the Marketplace and must not inherit its
        /// protection. The tunable is parsed with bareMatchesEphemeral false for exactly this reason.
        /// </summary>
        internal static bool IsInSafeZone(ushort landblock, ushort realm, bool isEphemeralRealm, LandblockRealmList safe)
        {
            if (isEphemeralRealm)
                return false;

            return safe != null && safe.Contains(landblock, realm, false);
        }

        /// <summary>
        /// TRUE when harm between these two parties must be refused: EITHER party currently being
        /// <see cref="IsInSafeZone"/> is enough, so a safe-zone resident cannot be shot from outside and cannot
        /// shoot out of it either. An empty <paramref name="safe"/> list (the tunable parsed to nothing) always
        /// returns false - not this rule's call, so the retail rules run unchanged.
        /// </summary>
        internal static bool IsPvpSafe(
            ushort attackerLandblock, ushort attackerRealm, bool attackerIsEphemeralRealm,
            ushort targetLandblock, ushort targetRealm, bool targetIsEphemeralRealm,
            LandblockRealmList safe)
        {
            if (safe == null || safe.Count == 0)
                return false;

            return IsInSafeZone(attackerLandblock, attackerRealm, attackerIsEphemeralRealm, safe)
                || IsInSafeZone(targetLandblock, targetRealm, targetIsEphemeralRealm, safe);
        }
    }
}
