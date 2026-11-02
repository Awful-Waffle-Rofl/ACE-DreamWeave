using System;

using ACE.Database.Models.Shard;
using ACE.Server.Managers;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// How a mule shared with a character is NAMED to that character: after the owner-account character
    /// that issued the grant, never after the owner account itself or any other character on it. The one
    /// definition, shared by /mule search all (Player_Mule_Search) and the suit builder's shared-mule
    /// inventory and transfer, so the three can never label the same mule differently.
    /// </summary>
    internal static class VaultGranterName
    {
        /// <summary>Shared mules one read covers, beyond the caller's own. A constant, not a tunable (owner ruling).</summary>
        internal const int MaxSharedMules = 10;

        /// <summary>The neutral label for a shared mule whose granter is not known.</summary>
        internal const string FallbackLabel = "A shared mule";

        /// <summary>
        /// The granting character's CURRENT name when the guid still resolves, else the name stored at
        /// grant time, else null (the caller then uses <see cref="FallbackFor"/>). Only ever the character
        /// the owner granted FROM - never some other character on the owner's account.
        ///
        /// THREADING: PlayerManager.FindByGuid takes PlayerManager's player-table read lock, and the
        /// name read goes through the biota's own lock, so this is safe off the world thread.
        /// </summary>
        internal static string Resolve(AccountVaultGrant grant)
        {
            return Resolve(grant, guid => PlayerManager.FindByGuid(guid)?.Name);
        }

        /// <summary><see cref="Resolve(AccountVaultGrant)"/> with the current-name lookup as a seam.</summary>
        internal static string Resolve(AccountVaultGrant grant, Func<uint, string> currentNameOf)
        {
            if (grant == null)
                return null;

            string current = null;

            if (grant.GrantedByCharacterGuid is uint granterGuid && granterGuid != 0)
                current = currentNameOf?.Invoke(granterGuid);

            return Choose(current, grant.GrantedByCharacterName);
        }

        /// <summary>Pure half of <see cref="Resolve(AccountVaultGrant)"/>, so the fallback order is testable.</summary>
        internal static string Choose(string currentName, string storedName)
        {
            if (!string.IsNullOrWhiteSpace(currentName))
                return currentName;

            // WriteLog snapshots an unnamed actor as "<unknown>", and the backfill copies that value.
            if (!string.IsNullOrWhiteSpace(storedName) && storedName != "<unknown>")
                return storedName;

            return null;
        }

        /// <summary>
        /// The fallback label for the <paramref name="ordinal"/>-th unnamed shared mule (1-based, in owner
        /// account order): "A shared mule", then "A shared mule (2)", "A shared mule (3)" and so on.
        /// </summary>
        internal static string FallbackFor(int ordinal)
        {
            return ordinal <= 1 ? FallbackLabel : $"{FallbackLabel} ({ordinal})";
        }
    }
}
