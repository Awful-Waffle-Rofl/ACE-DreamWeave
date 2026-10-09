using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// /mule search all, /mule search mine and /mule search &lt;owner&gt; - the cross-mule half of
    /// /mule search. Plain /mule search &lt;text&gt; still lives in Player_Mule_Vendor.cs and behaves
    /// exactly as before; this file only adds the forms that name WHICH mule to search.
    ///
    /// ONE COOLDOWN STAMP PER COMMAND. Every form here shares the /mule vault-command window
    /// (TryStartMuleVaultCommandCore) and stamps it exactly once, before the first shard read. The
    /// trap is the one HandleActionMuleSearch's own remarks record: TrySummonMuleCore stamps that SAME
    /// window internally, so a path that stamped and then went through it would refuse itself. The
    /// summon here therefore goes straight to MuleSummonHandler.TrySummon, never TrySummonMuleCore.
    /// </summary>
    partial class Player
    {
        /// <summary>Shared mules /mule search all reads, beyond the caller's own. A constant, not a tunable (owner ruling).</summary>
        internal const int SearchAllMaxSharedMules = 10;

        /// <summary>Hit lines printed per mule before "+N more".</summary>
        internal const int SearchAllMaxHitsPerMule = 5;

        /// <summary>Hit lines printed across the whole report.</summary>
        internal const int SearchAllMaxHitsTotal = 25;

        /// <summary>
        /// Wall-clock regex allowance for one /mule search all. Each match already has its own 100 ms
        /// timeout (ItemTextSearch.TryCompile); this bounds the SUM across every entry of every mule,
        /// because the command runs on the world tick.
        /// </summary>
        internal static readonly TimeSpan SearchAllRegexBudget = TimeSpan.FromMilliseconds(250);

        internal const string SearchAllOwnLabel = "Your mule";
        internal const string SearchAllFallbackLabel = "A shared mule";

        /// <summary>
        /// /mule search all &lt;text&gt;: the caller's own mule plus every mule shared with THIS
        /// character, reported in chat. Nothing is summoned and no panel opens.
        ///
        /// A store that is not already warm is never loaded here - it is queued with
        /// AccountVaultManager.RequestWarm and reported as still loading, so one command cannot put
        /// eleven cold shard loads on the world tick. Asking again a moment later finds it warm.
        /// </summary>
        public void HandleActionMuleSearchAll(string pattern)
        {
            if (!ItemTextSearch.TryCompile(pattern, out var regex, out var compileError))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(compileError, ChatMessageType.System));
                return;
            }

            if (!TryStartMuleVaultCommandCore(DateTime.UtcNow, out var cooldownReason))
            {
                SendTransientError(cooldownReason);
                return;
            }

            var actor = VaultActor.From(this);
            var ownAccountId = Account?.AccountId ?? 0;
            var budget = VaultSearchBudget.StartNew(SearchAllRegexBudget);
            var mules = new List<MuleSearchReportEntry>();
            var budgetHit = false;

            // Own mule first. TryGetExistingStore, not GetStore: asking must not build a store.
            var own = new MuleSearchReportEntry { IsOwn = true };
            AccountVaultManager.TryGetExistingStore(ownAccountId, out var ownStore);
            ScanOrWarm(ownAccountId, ownStore, regex, budget, own, ref budgetHit);
            mules.Add(own);

            // NULL is a read FAILURE, reported as such - never as "nothing is shared with you".
            var grants = AccountVaultManager.GetGrantsForGrantee(Guid.Full);
            var grantsUnavailable = grants == null;

            var shared = (grants ?? new List<ACE.Database.Models.Shard.AccountVaultGrant>())
                .Where(g => g.OwnerAccountId != 0 && g.OwnerAccountId != ownAccountId)
                .GroupBy(g => g.OwnerAccountId)
                .Select(g => g.First())
                .ToList();

            var muleCapHit = shared.Count > SearchAllMaxSharedMules;

            foreach (var grant in shared.Take(SearchAllMaxSharedMules))
            {
                // Re-resolved through the ONE access authority, per store, exactly like a summon: a
                // revoke that landed after the grantee-keyed read above is honoured here.
                var store = AccountVaultManager.GetStore(grant.OwnerAccountId);

                if (store == null || !store.TryGetAccess(actor, out var access, out _) || access == VaultAccess.None)
                    continue;

                var entry = new MuleSearchReportEntry { GranterName = ResolveGranterName(grant) };

                ScanOrWarm(grant.OwnerAccountId, store.IsWarm ? store : null, regex, budget, entry, ref budgetHit);
                mules.Add(entry);
            }

            foreach (var line in FormatSearchAllReport(pattern.Trim(), mules, grantsUnavailable, muleCapHit, budgetHit))
                Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.System));
        }

        /// <summary>
        /// Scans a WARM store, or queues a cold one for warming and marks it loading. A store left
        /// unscanned because the budget ran out is marked <see cref="MuleSearchReportEntry.Skipped"/>
        /// and is neither counted nor listed.
        /// </summary>
        private static void ScanOrWarm(uint accountId, AccountVaultStore warmCandidate, Regex regex, VaultSearchBudget budget, MuleSearchReportEntry entry, ref bool budgetHit)
        {
            if (warmCandidate == null || !warmCandidate.IsWarm)
            {
                AccountVaultManager.RequestWarm(accountId);
                entry.Loading = true;
                return;
            }

            if (budgetHit)
            {
                entry.Skipped = true;
                return;
            }

            entry.Scan = VaultSearch.Scan(warmCandidate.GetEntries(0, -1), regex, SearchAllMaxHitsPerMule, budget, DatabaseManager.World.GetCachedWeenie);

            if (entry.Scan.BudgetHit)
                budgetHit = true;
        }

        /// <summary>
        /// The granting character's CURRENT name when the guid still resolves, else the name stored at
        /// grant time, else null (the report then uses the neutral fallback label). Only ever the
        /// character the owner granted FROM - never some other character on the owner's account.
        /// </summary>
        private static string ResolveGranterName(ACE.Database.Models.Shard.AccountVaultGrant grant)
        {
            string current = null;

            if (grant.GrantedByCharacterGuid is uint granterGuid && granterGuid != 0)
                current = PlayerManager.FindByGuid(granterGuid)?.Name;

            return ChooseGranterName(current, grant.GrantedByCharacterName);
        }

        /// <summary>Pure half of <see cref="ResolveGranterName"/>, so the fallback order is testable.</summary>
        internal static string ChooseGranterName(string currentName, string storedName)
        {
            if (!string.IsNullOrWhiteSpace(currentName))
                return currentName;

            // WriteLog snapshots an unnamed actor as "<unknown>", and the backfill copies that value.
            if (!string.IsNullOrWhiteSpace(storedName) && storedName != "<unknown>")
                return storedName;

            return null;
        }

        /// <summary>
        /// /mule search mine &lt;text&gt;: the caller's OWN mule, filtered - even when a friend's
        /// vendor is the one currently summoned, which plain /mule search would filter instead.
        /// </summary>
        public void HandleActionMuleSearchMine(string pattern)
        {
            if (!ItemTextSearch.TryCompile(pattern, out var regex, out var compileError))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(compileError, ChatMessageType.System));
                return;
            }

            if (!TryStartMuleVaultCommandCore(DateTime.UtcNow, out var cooldownReason))
            {
                SendTransientError(cooldownReason);
                return;
            }

            SearchMuleFiltered(Account?.AccountId ?? 0, Name, pattern, regex);
        }

        /// <summary>
        /// The /mule search &lt;owner&gt; &lt;text&gt; routing HandleActionMuleSearch runs before its
        /// own body. True when it fully handled the command (a shared or own mule was searched, or the
        /// command was refused); false when the tokens are plain search text and the caller should fall
        /// through to the existing single-vendor search. <paramref name="cooldownStamped"/> tells that
        /// fall-through whether this already spent the command's one cooldown stamp.
        /// </summary>
        private bool TryRouteMuleSearchToOwner(IReadOnlyList<string> tokens, out bool cooldownStamped)
        {
            var target = TryResolveSearchTarget(tokens, Account?.AccountId ?? 0,
                name => PlayerManager.FindByName(name.Trim()) != null,
                () => TryStartMuleVaultCommandCore(DateTime.UtcNow, out var reason) ? null : reason,
                ResolveVaultCharacter,
                CheckMuleSearchAccess);

            cooldownStamped = target.CooldownStamped;

            switch (target.Kind)
            {
                case MuleSearchTargetKind.Refused:
                    SendTransientError(target.FailReason ?? AccountVaultStore.UnavailableMessage);
                    return true;

                case MuleSearchTargetKind.Own:
                case MuleSearchTargetKind.Shared:
                    if (!ItemTextSearch.TryCompile(target.Pattern, out var regex, out var compileError))
                    {
                        Session.Network.EnqueueSend(new GameMessageSystemChat(compileError, ChatMessageType.System));
                        return true;
                    }

                    var ownerName = target.Kind == MuleSearchTargetKind.Own ? Name : target.OwnerName;

                    SearchMuleFiltered(target.AccountId, ownerName, target.Pattern, regex);
                    return true;

                default:
                    return false;
            }
        }

        private bool CheckMuleSearchAccess(uint ownerAccountId, out VaultAccess access, out string failReason)
        {
            access = VaultAccess.None;

            var store = AccountVaultManager.GetStore(ownerAccountId);

            if (store == null)
            {
                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            return store.TryGetAccess(VaultActor.From(this), out access, out failReason);
        }

        /// <summary>
        /// Filters ONE named mule and reports "N of M". The cooldown is already stamped by the caller.
        /// If that exact mule is already the caller's live vendor it is re-filtered in place (and an open
        /// panel refreshes); otherwise it is summoned - exactly /mule &lt;owner&gt; - and the filter is
        /// attached before the view is built, the same order plain /mule search's summon branch uses.
        /// </summary>
        private void SearchMuleFiltered(uint storeAccountId, string ownerName, string pattern, Regex regex)
        {
            var vendor = CurrentSummonedVendor;

            if (vendor != null && !vendor.IsDestroyed && vendor.StoreAccountId == storeAccountId)
            {
                vendor.SetSearchFilter(regex);

                if (!RefreshMuleVendorView(vendor, out var refreshFailReason))
                {
                    SendTransientError(refreshFailReason);
                    return;
                }
            }
            else
            {
                // TrySummon directly, NEVER TrySummonMuleCore: that core stamps the cooldown this
                // command has already stamped, and would refuse the summon on its own timestamp.
                if (!MuleSummonHandler.TrySummon(this, storeAccountId, out var summonFailReason, ownerName))
                {
                    SendTransientError(summonFailReason ?? AccountVaultStore.UnavailableMessage);
                    return;
                }

                vendor = CurrentSummonedVendor;

                if (vendor == null)
                    return;

                vendor.SetSearchFilter(regex);

                if (!vendor.TryPrepareApproach(VaultActor.From(this), out var approachFailReason))
                {
                    SendTransientError(approachFailReason);
                    return;
                }
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Vault filter \"{pattern.Trim()}\": {vendor.LastRebuildShown} of {vendor.LastRebuildTotal} entries shown.",
                ChatMessageType.System));
        }

        #region Pure resolution and formatting (MuleGrantTests)

        internal delegate bool MuleSearchAccessCheck(uint ownerAccountId, out VaultAccess access, out string failReason);

        internal enum MuleSearchTargetKind
        {
            /// <summary>The tokens are search text; fall through to plain /mule search.</summary>
            Text,

            /// <summary>The first token names a character on the caller's own account.</summary>
            Own,

            /// <summary>The first token names a mule the caller may open.</summary>
            Shared,

            /// <summary>Refused - on cooldown, or the access read failed.</summary>
            Refused,
        }

        internal sealed class MuleSearchTarget
        {
            public MuleSearchTargetKind Kind { get; set; }
            public uint AccountId { get; set; }
            public string OwnerName { get; set; }

            /// <summary>For Own/Shared, the text AFTER the owner token. For Text, every token joined.</summary>
            public string Pattern { get; set; }

            public string FailReason { get; set; }

            /// <summary>Whether resolution spent the command's one cooldown stamp.</summary>
            public bool CooldownStamped { get; set; }
        }

        /// <summary>
        /// Decides whether "/mule search &lt;first&gt; &lt;rest...&gt;" names a mule. Pure: every lookup
        /// and the cooldown stamp arrive as delegates.
        ///
        /// In order: fewer than two tokens is text (a lone token is ALWAYS text). A first token that is
        /// not a known character name in memory (<paramref name="nameIsKnown"/>, PlayerManager's name
        /// index) is text, with no shard access and no stamp. Otherwise the cooldown is stamped ONCE
        /// (<paramref name="stampCooldown"/> returns the refusal, or null) BEFORE the shard reads that
        /// follow: <paramref name="resolver"/> maps the name to its account, and an account that is the
        /// caller's own is <see cref="MuleSearchTargetKind.Own"/>. Any other account is opened only when
        /// <paramref name="accessCheck"/> - the per-CHARACTER grant check - answers something other than
        /// None; a grant to the caller's alt does not count. Everything else is text.
        /// </summary>
        internal static MuleSearchTarget TryResolveSearchTarget(IReadOnlyList<string> tokens, uint callerAccountId,
            Func<string, bool> nameIsKnown, Func<string> stampCooldown, VaultCharacterResolver resolver, MuleSearchAccessCheck accessCheck)
        {
            var joined = tokens == null ? string.Empty : string.Join(" ", tokens);
            var text = new MuleSearchTarget { Kind = MuleSearchTargetKind.Text, Pattern = joined };

            if (tokens == null || tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[0]))
                return text;

            if (!nameIsKnown(tokens[0]))
                return text;

            var cooldownFail = stampCooldown();

            if (cooldownFail != null)
                return new MuleSearchTarget { Kind = MuleSearchTargetKind.Refused, FailReason = cooldownFail };

            text.CooldownStamped = true;

            if (!resolver(tokens[0], out var ownerName, out var accountId) || accountId == 0)
                return text;

            var rest = string.Join(" ", tokens.Skip(1));

            if (callerAccountId != 0 && accountId == callerAccountId)
                return new MuleSearchTarget { Kind = MuleSearchTargetKind.Own, AccountId = accountId, OwnerName = ownerName, Pattern = rest, CooldownStamped = true };

            if (!accessCheck(accountId, out var access, out var failReason))
            {
                // The grant read FAILED. Not "no access": say the vault is unavailable rather than
                // quietly searching the owner's name as text in whatever vendor happens to be live.
                return new MuleSearchTarget { Kind = MuleSearchTargetKind.Refused, FailReason = failReason ?? AccountVaultStore.UnavailableMessage, CooldownStamped = true };
            }

            if (access == VaultAccess.None)
                return text;

            return new MuleSearchTarget { Kind = MuleSearchTargetKind.Shared, AccountId = accountId, OwnerName = ownerName, Pattern = rest, CooldownStamped = true };
        }

        internal sealed class MuleSearchReportEntry
        {
            /// <summary>The caller's own mule. Always labelled "Your mule" and listed first.</summary>
            public bool IsOwn { get; set; }

            /// <summary>The granting character's name for a shared mule; null means "not known".</summary>
            public string GranterName { get; set; }

            /// <summary>Cold store queued for warming; reported as still loading.</summary>
            public bool Loading { get; set; }

            /// <summary>Not scanned because the budget ran out before it; omitted from the report.</summary>
            public bool Skipped { get; set; }

            public VaultScanResult Scan { get; set; }
        }

        /// <summary>
        /// Pure formatting of the /mule search all report, one string per chat line. Mirrors
        /// <see cref="FormatLogDetail"/>: extracted so every cap, fallback and ordering rule is testable
        /// without a Player.
        /// </summary>
        internal static List<string> FormatSearchAllReport(string pattern, IReadOnlyList<MuleSearchReportEntry> mules,
            bool grantsUnavailable, bool muleCapHit, bool budgetHit)
        {
            var lines = new List<string>();

            if (grantsUnavailable)
                lines.Add("Shared mules are unavailable right now - showing your mule only.");

            // Labels. Fallbacks are numbered in input order (owner account order), then every shared
            // mule is sorted by its final label; the caller's own mule always comes first.
            var labelled = new List<(MuleSearchReportEntry Mule, string Label, string OwnerToken)>();
            var fallbackCount = 0;

            foreach (var mule in mules)
            {
                if (mule == null || mule.Skipped)
                    continue;

                if (mule.IsOwn)
                {
                    labelled.Add((mule, SearchAllOwnLabel, "mine"));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(mule.GranterName))
                {
                    labelled.Add((mule, $"{mule.GranterName}'s mule", MuleCommandParser.QuoteNameIfNeeded(mule.GranterName)));
                    continue;
                }

                fallbackCount++;
                labelled.Add((mule, fallbackCount == 1 ? SearchAllFallbackLabel : $"{SearchAllFallbackLabel} ({fallbackCount})", null));
            }

            var ordered = labelled.Where(l => l.Mule.IsOwn)
                .Concat(labelled.Where(l => !l.Mule.IsOwn).OrderBy(l => l.Label, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var total = 0;
            var searched = 0;
            var printedHits = 0;
            var totalCapHit = false;

            foreach (var (mule, label, ownerToken) in ordered)
            {
                if (mule.Loading)
                {
                    lines.Add($"{label}: still loading - try again in a moment.");
                    continue;
                }

                searched++;

                var scan = mule.Scan;

                if (scan == null || scan.Total <= 0)
                    continue;

                total += scan.Total;

                lines.Add($"{label}: {scan.Total} match(es)");

                var perMule = Math.Min(scan.Hits.Count, SearchAllMaxHitsPerMule);
                var room = Math.Max(0, SearchAllMaxHitsTotal - printedHits);
                var shown = Math.Min(perMule, room);

                if (shown < perMule)
                    totalCapHit = true;

                foreach (var hit in scan.Hits.Take(shown))
                    lines.Add(hit.Count > 1 ? $"  {hit.Name} x{hit.Count}" : $"  {hit.Name}");

                printedHits += shown;

                var more = scan.Total - shown;

                if (more > 0)
                {
                    lines.Add(ownerToken != null
                        ? $"  +{more} more - /mule search {ownerToken} {pattern}"
                        : $"  +{more} more");
                }
            }

            if (searched > 0)
            {
                lines.Add(total > 0
                    ? $"{total} match(es) in {searched} mule(s)."
                    : $"No matches for \"{pattern}\" in {searched} mule(s).");
            }

            if (budgetHit)
                lines.Add("Search stopped early - try a narrower pattern.");

            if (totalCapHit)
                lines.Add($"Showing the first {SearchAllMaxHitsTotal} matches - narrow your pattern or search one mule.");

            if (muleCapHit)
                lines.Add($"Searched the first {SearchAllMaxSharedMules} shared mules only.");

            return lines;
        }

        #endregion
    }
}
