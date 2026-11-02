using System.Collections.Generic;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The reads the suit builder needs to show a character the mules SHARED with it: which grants name the
    /// character, what the ONE access authority (AccountVaultStore.TryGetAccess) says about each owner's
    /// store, and how each mule is named. Kept apart from <see cref="IMarketItemStore"/> on purpose - none of
    /// this is custody, and the custody contract stays as narrow as it was.
    ///
    /// Implemented by <see cref="VaultMarketItemStore"/> beside its store resolver seam (production and the
    /// real-store tests), and by the test fake. An item store that does not implement it serves no shared
    /// mules: the route then answers unavailable, never "nothing is shared".
    /// </summary>
    internal interface ISharedVaultSource
    {
        /// <summary>Every grant naming <paramref name="granteeCharacterGuid"/> as grantee. NULL means the read FAILED, never "nothing is shared".</summary>
        List<AccountVaultGrant> GetGrantsForGrantee(uint granteeCharacterGuid);

        /// <summary>
        /// AccountVaultStore.TryGetAccess on <paramref name="ownerAccountId"/>'s ALREADY LIVE store for <paramref name="actor"/>.
        /// False when the access could not be read: no live store (never one built here), or the grant read failed. A
        /// genuine "no grant" is true with <see cref="VaultAccess.None"/>.
        /// </summary>
        bool TryGetAccess(uint ownerAccountId, VaultActor actor, out VaultAccess access);

        /// <summary>
        /// AccountVaultStore.IsWarm for the owner's ALREADY LIVE store (AccountVaultManager.TryGetExistingStore): true only
        /// when it could be served right now with no shard read. False when no store is live. NEVER builds a store and
        /// NEVER starts a load (unlike IsReady / TryCheckReady, which loads a cold store).
        /// </summary>
        bool IsWarm(uint ownerAccountId);

        /// <summary>
        /// AccountVaultManager.RequestWarm: queue the owner's store for a rate-limited load on the world heartbeat, which
        /// also builds the store when none is live. False when the warm queue refused it (full).
        /// </summary>
        bool RequestWarm(uint ownerAccountId);

        /// <summary><see cref="VaultGranterName.Resolve(AccountVaultGrant)"/>: the granter's name, or null for the fallback label.</summary>
        string GranterName(AccountVaultGrant grant);
    }
}
