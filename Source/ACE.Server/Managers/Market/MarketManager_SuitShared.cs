using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>One shared mule as the suit builder's shared-inventory route lists it.</summary>
    internal sealed class SharedSuitVault
    {
        public const string StateReady = "ready";
        public const string StateLoading = "loading";

        public SharedSuitVault(string vaultKey, string label, string state)
        {
            VaultKey = vaultKey;
            Label = label;
            State = state;
        }

        /// <summary>"s:&lt;AccountVaultGrant.Id&gt;", the value a transfer line echoes back as vault_key.</summary>
        public string VaultKey { get; }

        /// <summary>The granter character's name, or the numbered "A shared mule" fallback.</summary>
        public string Label { get; }

        /// <summary><see cref="StateReady"/> (its items are in the response) or <see cref="StateLoading"/> (ask again).</summary>
        public string State { get; }
    }

    /// <summary>The roster check the shared-inventory read runs first: MarketApiHost.TryResolveCharacter in production.</summary>
    internal delegate bool SuitCharacterResolver(uint accountId, uint characterGuid, out string characterName);

    public static partial class MarketManager
    {
        internal const string SharedVaultKeyPrefix = "s:";

        /// <summary>
        /// Owner ruling: a BONDED item (Bonded 1 or Sticky 2) never leaves a shared mule, because a shared mule moves
        /// items across accounts. Attuned is checked as well, as belt and braces: an attuned item cannot reach a vault
        /// at all. The caller's own vault is unaffected.
        /// </summary>
        internal static bool IsBoundForSharing(WorldObject item)
            => item != null && (((int?)item.Bonded ?? 0) >= 1 || ((int?)item.Attuned ?? 0) >= 1);

        /// <summary>
        /// The same ruling for a ledger row or class line, which has no object of its own: its units are made from
        /// the template weenie, so a template that is bonded or attuned (>= 1) keeps the row in the mule.
        /// </summary>
        internal static bool IsBoundForSharing(ACE.Entity.Models.Weenie weenie)
            => weenie?.PropertiesInt != null
               && ((weenie.PropertiesInt.TryGetValue(ACE.Entity.Enum.Properties.PropertyInt.Bonded, out var bonded) && bonded >= 1)
                   || (weenie.PropertiesInt.TryGetValue(ACE.Entity.Enum.Properties.PropertyInt.Attuned, out var attuned) && attuned >= 1));

        /// <summary>The vault key for a shared mule: "s:" and the grant row's id, which a re-grant keeps and a revoke deletes.</summary>
        internal static string SharedVaultKey(uint grantId) => SharedVaultKeyPrefix + grantId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses a vault key. Null (absent) is the caller's OWN vault (true, grantId null); "s:&lt;id&gt;" with a
        /// positive decimal id is a shared mule; anything else, the empty string included, is false (malformed).
        /// </summary>
        internal static bool TryParseVaultKey(string vaultKey, out uint? grantId)
        {
            grantId = null;

            if (vaultKey == null)
                return true;

            if (!vaultKey.StartsWith(SharedVaultKeyPrefix, StringComparison.Ordinal))
                return false;

            var digits = vaultKey.Substring(SharedVaultKeyPrefix.Length);

            if (digits.Length == 0 || digits.Length > 10 || !digits.All(c => c >= '0' && c <= '9'))
                return false;

            if (!uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                return false;

            grantId = id;
            return true;
        }

        /// <summary>
        /// The suit builder's view of the mules SHARED with one of the caller's characters
        /// (GET /v1/accounts/me/characters/{guid}/suit/shared-inventory). Owner rulings: only DepositWithdraw
        /// grants count (a deposit-only share offers nothing to withdraw), listed items are left out (a partly
        /// listed ledger or class row contributes its free remainder only), and each mule is labelled after its
        /// granter character exactly as /mule search all labels it - never the owner account or any other
        /// character on it.
        ///
        /// In order: the character must be the caller's (<paramref name="resolveCharacter"/>, failing closed to
        /// <see cref="MarketError.NotOwner"/>); the grantee-keyed grant read must succeed (NULL is
        /// <see cref="MarketError.VaultUnavailable"/>, never "nothing is shared"); grants owned by the caller's own
        /// account are dropped, one per owner is kept, and at most <see cref="VaultGranterName.MaxSharedMules"/>
        /// are read; each is then re-resolved through the ONE access authority (AccountVaultStore.TryGetAccess,
        /// by way of <see cref="ISharedVaultSource"/>) for exactly this character, so a revoke or a downgrade that
        /// landed after the grant read is honoured and a grant to the caller's alt never counts.
        ///
        /// NEVER A STORE BUILT, NEVER A COLD LOAD FROM HERE. Every shared lookup goes to an ALREADY LIVE store only (no
        /// AccountVaultManager.GetStore, which builds one and refreshes its capacity bonuses). The warm check runs FIRST,
        /// before the access read. A store that is not already WARM (<see cref="ISharedVaultSource.IsWarm"/>,
        /// AccountVaultStore.IsWarm, which answers from state alone) is queued with
        /// <see cref="ISharedVaultSource.RequestWarm"/> (AccountVaultManager.RequestWarm, rate-limited on the world
        /// heartbeat) and listed as <see cref="SharedSuitVault.StateLoading"/> with no items - the /mule search all
        /// pattern - so one request can never start ten synchronous shard loads. A store whose access could not be
        /// read is listed as loading too, rather than failing the whole response. A warm one is projected with the OWNER's
        /// account id through the very loop the own-vault inventory uses, then its listed parts are dropped and
        /// every item is stamped with the mule's vault_key and label.
        ///
        /// THREADING (traced): this runs on the market host's request worker, like
        /// <see cref="TryGetSuitInventory"/>. The grant reads open their own short-lived ShardDbContext
        /// (ShardDatabase.GetAccountVaultGrants*), TryGetAccess, IsWarm and IsReady take the store's own locks (IsReady
        /// only after IsWarm said yes, so it never loads), RequestWarm takes the warm queue's lock, and the
        /// granter-name lookup is PlayerManager.FindByGuid (player-table read lock) plus a biota-locked name read.
        /// </summary>
        internal static bool TryGetSharedSuitInventory(uint callerAccountId, uint characterGuid, SuitCharacterResolver resolveCharacter,
                                                       out IReadOnlyList<SharedSuitVault> vaults, out IReadOnlyList<SuitItem> items, out MarketError error)
        {
            var vaultList = new List<SharedSuitVault>();
            var itemList = new List<SuitItem>();
            vaults = vaultList;
            items = itemList;
            error = MarketError.None;

            string characterName = null;

            if (callerAccountId == 0 || characterGuid == 0 || resolveCharacter == null || !resolveCharacter(callerAccountId, characterGuid, out characterName))
            {
                error = MarketError.NotOwner;
                return false;
            }

            var store = itemStore;

            if (store == null || !(store is ISharedVaultSource source))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            List<AccountVaultGrant> grants;

            try
            {
                grants = source.GetGrantsForGrantee(characterGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] the shared-mule grant read threw for character 0x{characterGuid:X8}: {ex.GetFullMessage()}");
                grants = null;
            }

            // NULL is a read FAILURE, reported as such - never as "nothing is shared with you".
            if (grants == null)
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            // CanWithdraw here only keeps deposit-only shares from taking one of the ten slots; it grants nothing.
            // Access is still decided per store by TryGetAccess below.
            var shared = grants
                .Where(g => g != null && g.CanWithdraw && g.OwnerAccountId != 0 && g.OwnerAccountId != callerAccountId && g.GranteeCharacterGuid == characterGuid)
                .OrderBy(g => g.OwnerAccountId)
                .GroupBy(g => g.OwnerAccountId)
                .Select(g => g.First())
                .Take(VaultGranterName.MaxSharedMules)
                .ToList();

            var actor = new VaultActor(callerAccountId, characterGuid, characterName);
            var fallbackCount = 0;

            var warmRefusalLogged = false;

            foreach (var grant in shared)
            {
                // WARM FIRST, from an already-live store only: a cold mule is never built or loaded on this thread.
                // It is queued for the rate-limited warm-up (which builds the store) and listed as loading. Its access
                // is then decided on a later ask, once warm - until then the grant row naming this character is what
                // lists it, so a grant revoked a moment ago can show as loading once and then drop out.
                bool warm;

                try
                {
                    warm = source.IsWarm(grant.OwnerAccountId);
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] the shared-mule warm check threw for owner {grant.OwnerAccountId}: {ex.GetFullMessage()}");
                    warm = false;
                }

                if (!warm)
                {
                    bool queued;

                    try
                    {
                        queued = source.RequestWarm(grant.OwnerAccountId);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[MARKET] queueing a warm-up threw for owner {grant.OwnerAccountId}: {ex.GetFullMessage()}");
                        queued = false;
                    }

                    if (!queued && !warmRefusalLogged)
                    {
                        warmRefusalLogged = true;
                        log.Warn($"[MARKET] the vault warm queue refused owner {grant.OwnerAccountId} for character 0x{characterGuid:X8}'s shared mules (queue full?); they stay 'loading' until a later ask is queued.");
                    }

                    vaultList.Add(new SharedSuitVault(SharedVaultKey(grant.Id), LabelFor(source, grant, ref fallbackCount), SharedSuitVault.StateLoading));
                    continue;
                }

                bool accessRead;
                VaultAccess access;

                try
                {
                    accessRead = source.TryGetAccess(grant.OwnerAccountId, actor, out access);
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] the shared-mule access read threw for owner {grant.OwnerAccountId}, character 0x{characterGuid:X8}: {ex.GetFullMessage()}");
                    accessRead = false;
                    access = VaultAccess.None;
                }

                // Revoked since the grant read, deposit-only, or never this character's: not a mule to withdraw from.
                if (accessRead && access != VaultAccess.DepositWithdraw)
                    continue;

                var label = LabelFor(source, grant, ref fallbackCount);
                var key = SharedVaultKey(grant.Id);

                // Warm is strictly stronger than ready, so IsReady never starts a load here; it catches a store retired
                // or invalidated between the two reads. An access read that failed is loading too, never dropped.
                if (!accessRead || !store.IsReady(grant.OwnerAccountId, out _))
                {
                    vaultList.Add(new SharedSuitVault(key, label, SharedSuitVault.StateLoading));
                    continue;
                }

                var projected = new List<SuitItem>();

                ProjectSuitVault(store, grant.OwnerAccountId, projected, excludeBound: true);

                foreach (var item in projected)
                {
                    // Owner ruling: a listed part of a shared mule is never offered.
                    if (item.Listed)
                        continue;

                    itemList.Add(item with { VaultKey = key, VaultLabel = label });
                }

                vaultList.Add(new SharedSuitVault(key, label, SharedSuitVault.StateReady));
            }

            return true;
        }

        /// <summary>The mule's label: the granter's name, else the next numbered fallback (owner order).</summary>
        private static string LabelFor(ISharedVaultSource source, AccountVaultGrant grant, ref int fallbackCount)
        {
            string granter;

            try
            {
                granter = source.GranterName(grant);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] the granter-name lookup threw for grant {grant.Id}; using the fallback label: {ex.GetFullMessage()}");
                granter = null;
            }

            return granter ?? VaultGranterName.FallbackFor(++fallbackCount);
        }
    }
}
