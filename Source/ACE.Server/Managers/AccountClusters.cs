using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ACE.Server.Managers
{
    /// <summary>
    /// An immutable set of admin-defined account clusters (the ip_household_clusters tunable). Each cluster is a
    /// named group of ACCOUNT IDS that the IP-based limits treat as one synthetic household, regardless of which
    /// addresses its members connect from. An account is in at most one cluster.
    ///
    /// Raw format: <c>name=accountId,accountId;name2=accountId</c>. Names match <c>[a-z0-9-]{1,32}</c> (upper case
    /// is folded to lower). Whitespace around any token and an empty value are tolerated. Pure: no
    /// PropertyManager access, so it is unit-testable on its own.
    /// </summary>
    public sealed class AccountClusters
    {
        public static readonly AccountClusters Empty = new AccountClusters(new SortedDictionary<string, SortedSet<uint>>(StringComparer.Ordinal));

        public const int MaxNameLength = 32;

        private static readonly Regex NamePattern = new Regex(@"^[a-z0-9-]{1,32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly SortedDictionary<string, SortedSet<uint>> clusters;
        private readonly Dictionary<uint, string> byAccount;

        private AccountClusters(SortedDictionary<string, SortedSet<uint>> clusters)
        {
            this.clusters = clusters;
            byAccount = new Dictionary<uint, string>();

            foreach (var kv in clusters)
                foreach (var id in kv.Value)
                    byAccount[id] = kv.Key;
        }

        /// <summary>True if <paramref name="name"/> is a legal cluster name.</summary>
        public static bool IsValidName(string name) => name != null && NamePattern.IsMatch(name);

        /// <summary>Cluster names, sorted.</summary>
        public IReadOnlyList<string> Names => clusters.Keys.ToList();

        public int Count => clusters.Count;

        /// <summary>The cluster this account belongs to, or null.</summary>
        public string ClusterOf(uint accountId)
            => byAccount.TryGetValue(accountId, out var name) ? name : null;

        /// <summary>The member account ids of a cluster, sorted ascending; empty if there is no such cluster.</summary>
        public IReadOnlyList<uint> MembersOf(string name)
            => name != null && clusters.TryGetValue(name, out var members) ? members.ToList() : new List<uint>();

        /// <summary>
        /// Parses the raw value. Never throws. A bad name, a bad id or an empty/malformed entry is skipped and
        /// described in <paramref name="warnings"/>. An account listed in two clusters stays in the first cluster
        /// in sorted-name order (and the later one is warned about). A cluster left with no valid member is dropped.
        /// </summary>
        public static AccountClusters Parse(string raw, out IReadOnlyList<string> warnings)
        {
            var warn = new List<string>();
            warnings = warn;

            if (string.IsNullOrWhiteSpace(raw))
                return Empty;

            // First pass: collect every named group, merging a repeated name.
            var parsed = new SortedDictionary<string, SortedSet<uint>>(StringComparer.Ordinal);

            foreach (var entryRaw in raw.Split(';'))
            {
                var entry = entryRaw.Trim();
                if (entry.Length == 0)
                    continue;

                var eq = entry.IndexOf('=');
                if (eq < 0)
                {
                    warn.Add($"entry '{entry}' has no '=' and was skipped");
                    continue;
                }

                var name = entry.Substring(0, eq).Trim().ToLowerInvariant();
                if (!IsValidName(name))
                {
                    warn.Add($"cluster name '{name}' is not [a-z0-9-]{{1,{MaxNameLength}}} and the cluster was skipped");
                    continue;
                }

                if (!parsed.TryGetValue(name, out var members))
                {
                    members = new SortedSet<uint>();
                    parsed[name] = members;
                }

                foreach (var idRaw in entry.Substring(eq + 1).Split(','))
                {
                    var idText = idRaw.Trim();
                    if (idText.Length == 0)
                        continue;

                    if (!uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                    {
                        warn.Add($"cluster '{name}': '{idText}' is not a valid account id and was skipped");
                        continue;
                    }

                    members.Add(id);
                }
            }

            // Second pass: one cluster per account. SortedDictionary enumerates in sorted-name order, so the first
            // cluster keeps the account.
            var result = new SortedDictionary<string, SortedSet<uint>>(StringComparer.Ordinal);
            var owner = new Dictionary<uint, string>();

            foreach (var kv in parsed)
            {
                var kept = new SortedSet<uint>();

                foreach (var id in kv.Value)
                {
                    if (owner.TryGetValue(id, out var first))
                    {
                        warn.Add($"account {id} is in clusters '{first}' and '{kv.Key}'; it stays in '{first}'");
                        continue;
                    }

                    owner[id] = kv.Key;
                    kept.Add(id);
                }

                if (kept.Count > 0)
                    result[kv.Key] = kept;
            }

            return result.Count == 0 ? Empty : new AccountClusters(result);
        }

        /// <summary>The canonical raw form: clusters sorted by name, ids ascending, no whitespace. Round-trips through <see cref="Parse"/>.</summary>
        public string Format()
        {
            var sb = new StringBuilder();

            foreach (var kv in clusters)
            {
                if (sb.Length > 0)
                    sb.Append(';');

                sb.Append(kv.Key).Append('=').Append(string.Join(",", kv.Value.Select(id => id.ToString(CultureInfo.InvariantCulture))));
            }

            return sb.ToString();
        }

        /// <summary>A copy with <paramref name="name"/> set to exactly <paramref name="members"/> (an empty set deletes the cluster). Does not validate; callers check first.</summary>
        public AccountClusters With(string name, IEnumerable<uint> members)
        {
            var copy = new SortedDictionary<string, SortedSet<uint>>(StringComparer.Ordinal);

            foreach (var kv in clusters)
                if (kv.Key != name)
                    copy[kv.Key] = new SortedSet<uint>(kv.Value);

            var set = new SortedSet<uint>(members ?? Array.Empty<uint>());
            if (set.Count > 0)
                copy[name] = set;

            return copy.Count == 0 ? Empty : new AccountClusters(copy);
        }
    }
}
