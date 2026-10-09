using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Entity;

namespace ACE.Server.Realms
{
    /// <summary>
    /// A parsed, realm-aware landblock list: the ONE parser behind every comma-separated landblock tunable
    /// (mule_landblocks, pvp_safe_landblocks, account_vault_allowlist / account_vault_denylist,
    /// facet_allowlist). Before this type each of those had its own copy of the same 4-hex-digit parser and
    /// matched on <see cref="Position.LandblockShort"/> alone, which is realm-blind - so once the Marketplace
    /// moved into a realm copy of a landblock that also exists in the base world (Aerfalle Keep, 0x01F5, realm
    /// 1), a bare landblock id could no longer say "the Marketplace" without also saying "the retail Aerfalle
    /// Keep".
    ///
    /// Two entry forms, comma-separated, whitespace-tolerant, case-insensitive, optional 0x prefix:
    /// <list type="bullet">
    ///   <item><c>LLLL</c> - a BARE entry. Matches landblock LLLL in every persistent realm, exactly as every
    ///   list matched before this type existed. Whether it also matches an EPHEMERAL instance of LLLL is the
    ///   caller's existing behaviour, preserved per list via <see cref="BareMatchesEphemeral"/>: the PvP safe
    ///   zone excluded ephemeral instances; mule_landblocks, the vault lists and facet_allowlist did not.</item>
    ///   <item><c>LLLL@R</c> - a REALM-SCOPED entry (R decimal, 0..32767). Matches landblock LLLL only in a
    ///   non-ephemeral instance of realm R. Never matches an ephemeral instance, whatever the list's
    ///   <see cref="BareMatchesEphemeral"/> says - an instanced copy of the Marketplace is not the Marketplace.</item>
    /// </list>
    ///
    /// Parsing is lenient on purpose, the same leniency every one of the replaced parsers had: a malformed
    /// entry is SKIPPED with a logged warning, never thrown, because these lists feed login, PvP and summon
    /// gates and a config typo must never break any of them.
    ///
    /// Immutable once built, so a parsed instance can be cached and shared across threads.
    /// </summary>
    public sealed class LandblockRealmList
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(LandblockRealmList));

        /// <summary>The highest realm id an instance id can carry (15 bits - see Position.InstanceIDFromVars).</summary>
        public const ushort MaxRealmId = 0x7FFF;

        private readonly HashSet<ushort> bareLandblocks;
        private readonly HashSet<uint> scopedEntries;   // (landblock << 16) | realm

        /// <summary>
        /// Whether a BARE entry also matches an ephemeral instance of its landblock. Realm-scoped entries never
        /// do. Set per list by the caller so each list keeps the bare-entry behaviour it had before.
        /// </summary>
        public bool BareMatchesEphemeral { get; }

        private LandblockRealmList(HashSet<ushort> bare, HashSet<uint> scoped, bool bareMatchesEphemeral)
        {
            bareLandblocks = bare;
            scopedEntries = scoped;
            BareMatchesEphemeral = bareMatchesEphemeral;
        }

        /// <summary>An empty list. Callers that treat an empty list as "no restriction" must check <see cref="Count"/>.</summary>
        public static LandblockRealmList Empty(bool bareMatchesEphemeral = false)
            => new LandblockRealmList(new HashSet<ushort>(), new HashSet<uint>(), bareMatchesEphemeral);

        /// <summary>Number of distinct entries (bare plus realm-scoped).</summary>
        public int Count => bareLandblocks.Count + scopedEntries.Count;

        /// <summary>
        /// Parses a comma-separated list. Never throws: null, empty and whitespace-only input give an empty
        /// list, empty entries are ignored, and each malformed entry is skipped with a warning naming
        /// <paramref name="configKeyForLogging"/>. <paramref name="rejected"/>, when supplied, receives each
        /// skipped raw token (used by tests to prove a skip happened without scraping the log).
        /// </summary>
        public static LandblockRealmList Parse(string raw, string configKeyForLogging, bool bareMatchesEphemeral = false, ICollection<string> rejected = null)
        {
            var bare = new HashSet<ushort>();
            var scoped = new HashSet<uint>();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var token in raw.Split(','))
                {
                    if (token.Trim().Length == 0)
                        continue;

                    if (!TryParseEntry(token, out var landblock, out var realm))
                    {
                        rejected?.Add(token);
                        log.Warn($"LandblockRealmList: skipping malformed entry '{token}' in '{configKeyForLogging}' (expected LLLL or LLLL@R, e.g. 016C or 01F5@1).");
                        continue;
                    }

                    if (realm == null)
                        bare.Add(landblock);
                    else
                        scoped.Add(((uint)landblock << 16) | realm.Value);
                }
            }

            return new LandblockRealmList(bare, scoped, bareMatchesEphemeral);
        }

        /// <summary>
        /// Parses ONE entry: <c>LLLL</c> or <c>LLLL@R</c>, surrounding whitespace ignored, optional 0x prefix on
        /// the landblock, landblock hex (any value that fits 16 bits), realm decimal in 0..<see cref="MaxRealmId"/>.
        /// <paramref name="realm"/> is null for a bare entry.
        /// </summary>
        public static bool TryParseEntry(string token, out ushort landblock, out ushort? realm)
        {
            landblock = 0;
            realm = null;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            var trimmed = token.Trim();
            string realmPart = null;

            var at = trimmed.IndexOf('@');
            if (at >= 0)
            {
                realmPart = trimmed.Substring(at + 1).Trim();
                trimmed = trimmed.Substring(0, at).Trim();

                if (realmPart.Length == 0)
                    return false;
            }

            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(2);

            // No digit-count check: the replaced parsers accepted anything ushort.TryParse(hex) did (so
            // "16C" and "0016C" both meant 016C), and bare-entry back-compat keeps that exactly.
            if (trimmed.Length == 0)
                return false;

            if (!ushort.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out landblock))
                return false;

            if (realmPart != null)
            {
                if (!ushort.TryParse(realmPart, NumberStyles.None, CultureInfo.InvariantCulture, out var realmId) || realmId > MaxRealmId)
                {
                    landblock = 0;
                    return false;
                }

                realm = realmId;
            }

            return true;
        }

        /// <summary>
        /// The canonical spelling of one entry (<c>016C</c>, <c>01F5@1</c>), or null if it does not parse.
        /// Used by the admin commands so "0x01f5 @ 1" and "01F5@1" are recognised as the same entry.
        /// </summary>
        public static string NormalizeEntry(string token)
        {
            if (!TryParseEntry(token, out var landblock, out var realm))
                return null;

            return FormatEntry(landblock, realm);
        }

        private static string FormatEntry(ushort landblock, ushort? realm)
            => realm == null ? landblock.ToString("X4") : $"{landblock:X4}@{realm.Value}";

        /// <summary>
        /// The whole membership rule. A realm-scoped entry matches only a non-ephemeral instance of its own
        /// realm; a bare entry matches any persistent realm, and an ephemeral instance only when
        /// <see cref="BareMatchesEphemeral"/> is set.
        /// </summary>
        public bool Contains(ushort landblock, ushort realm, bool ephemeral)
        {
            if (bareLandblocks.Contains(landblock) && (!ephemeral || BareMatchesEphemeral))
                return true;

            if (ephemeral)
                return false;

            return scopedEntries.Contains(((uint)landblock << 16) | realm);
        }

        /// <summary>As <see cref="Contains(ushort, ushort, bool)"/>, decoding the realm and ephemeral bit out of a full instance id.</summary>
        public bool ContainsInstance(ushort landblock, uint instance)
        {
            Position.ParseInstanceID(instance, out var ephemeral, out var realm, out _);
            return Contains(landblock, realm, ephemeral);
        }

        /// <summary>As <see cref="Contains(ushort, ushort, bool)"/> for a position. A null position is never contained.</summary>
        public bool Contains(Position position)
        {
            if (position == null)
                return false;

            return ContainsInstance((ushort)position.LandblockShort, position.Instance);
        }

        /// <summary>Every entry in canonical spelling, sorted by landblock, bare entries before realm-scoped ones.</summary>
        public IReadOnlyList<string> Entries
        {
            get
            {
                var all = bareLandblocks.Select(lb => (lb, realm: (ushort?)null))
                    .Concat(scopedEntries.Select(e => ((ushort)(e >> 16), realm: (ushort?)(e & 0xFFFF))))
                    .OrderBy(e => e.Item1)
                    .ThenBy(e => e.realm.HasValue ? 1 : 0)
                    .ThenBy(e => e.realm ?? 0)
                    .Select(e => FormatEntry(e.Item1, e.realm))
                    .ToList();

                return all;
            }
        }

        /// <summary>The canonical comma-separated form (no spaces), e.g. <c>016C,01F5@1</c>. Round-trips through <see cref="Parse"/>.</summary>
        public override string ToString() => string.Join(",", Entries);
    }
}
