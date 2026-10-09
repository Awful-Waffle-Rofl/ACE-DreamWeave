using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using log4net;

using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The override payload <see cref="VaultCollapse.TryDescribeClass"/> hands back: the values of
    /// exactly the whitelisted keys, read off the item.
    ///
    /// EVERY WHITELISTED KEY IS ALWAYS PRESENT HERE, carrying a null when the item does not have that
    /// property. That is not padding. A class row is replayed onto a fresh template, and a template can
    /// carry a property the item does not (salvage weenies 20988 and 21050 ship a bugged Structure that
    /// Player_Crafting.GetSalvageBag:442-444 clears on every bag it makes). "Absent" therefore has to be
    /// expressible, or the materializer would silently leave the template's value in place and hand back
    /// an item that is not the one that was stored.
    ///
    /// THE KEY SET IS PART OF THE IDENTITY CONTRACT, AND THE CONSTRUCTOR ENFORCES IT. A payload whose
    /// key set is not EXACTLY VaultCollapse's whitelist is refused rather than canonicalized, in the
    /// spirit of VaultCollapse.AssertBiotaCoverage: refuse rather than guess.
    ///
    /// Two different failures, both silent without the guard.
    /// <see cref="VaultItemClass.CanonicalForm"/> walks whatever the payload holds, so a SHORT payload
    /// canonicalizes to different text than a full one carrying the same values plus a null - two
    /// payloads meaning the same thing would then key differently and one stored class would split in
    /// two. And a payload carrying a key OUTSIDE the whitelist names something the predicate never
    /// proved the item could be rebuilt from, which <see cref="VaultItemClass.Materialize"/> would
    /// replay onto a fresh template regardless.
    ///
    /// Today the only producer is VaultCollapse.CaptureOverridesLocked, which always emits the full
    /// set, so nothing is wrong right now. The guard is what keeps that a guarantee rather than a
    /// coincidence once the storage tier adds its second caller, or once the whitelist gains a fifth
    /// key and a stale caller keeps sending four.
    /// </summary>
    public sealed class VaultItemClassOverrides
    {
        /// <summary>Whitelisted int keys, ascending by numeric id. A null value means ABSENT on the item.</summary>
        public IReadOnlyList<KeyValuePair<PropertyInt, int?>> Ints { get; }

        /// <summary>Whitelisted string keys, ascending by numeric id. A null value means ABSENT on the item.</summary>
        public IReadOnlyList<KeyValuePair<PropertyString, string>> Strings { get; }

        public VaultItemClassOverrides(IEnumerable<KeyValuePair<PropertyInt, int?>> ints,
                                       IEnumerable<KeyValuePair<PropertyString, string>> strings)
        {
            // Sorted HERE rather than trusted from the caller, so the canonical form below cannot
            // depend on the order a dictionary happened to enumerate in.
            Ints = (ints ?? Array.Empty<KeyValuePair<PropertyInt, int?>>())
                .OrderBy(kvp => (int)kvp.Key)
                .ToList();

            Strings = (strings ?? Array.Empty<KeyValuePair<PropertyString, string>>())
                .OrderBy(kvp => (int)kvp.Key)
                .ToList();

            // AFTER the sort, so the refusal message lists the keys in the same order the canonical
            // form would have emitted them.
            AssertKeySetIsExactlyTheWhitelist(nameof(ints), Ints.Select(kvp => kvp.Key), VaultCollapse.CollapsibleIntKeyContract);
            AssertKeySetIsExactlyTheWhitelist(nameof(strings), Strings.Select(kvp => kvp.Key), VaultCollapse.CollapsibleStringKeyContract);
        }

        /// <summary>
        /// Refuses unless the supplied keys are EXACTLY the whitelist: no key missing, no key that is
        /// not on it, and no key twice.
        ///
        /// The duplicate check is not redundant with the set check. Set membership alone passes a
        /// payload carrying one key twice, and the canonical form would then emit that key twice, so
        /// two payloads differing only in which duplicate came first could canonicalize differently.
        /// The count test is what closes it.
        ///
        /// Throws <see cref="ArgumentException"/> rather than the InvalidOperationException
        /// AssertBiotaCoverage uses, because this really is a bad argument and naming the parameter
        /// points at the caller that built it. The message shape is deliberately the same: say which
        /// keys are wrong, and say why refusing beats proceeding.
        /// </summary>
        private static void AssertKeySetIsExactlyTheWhitelist<TKey>(string parameterName, IEnumerable<TKey> supplied, IReadOnlyList<TKey> whitelist)
            where TKey : Enum
        {
            var keys = supplied.ToList();
            var distinct = new HashSet<TKey>(keys);

            var missing = whitelist.Where(key => !distinct.Contains(key)).ToList();
            var extra = distinct.Where(key => !whitelist.Contains(key)).ToList();
            var duplicated = keys.GroupBy(key => key).Where(group => group.Count() > 1).Select(group => group.Key).ToList();

            if (missing.Count == 0 && extra.Count == 0 && duplicated.Count == 0)
                return;

            var detail = new StringBuilder();

            if (missing.Count > 0)
                detail.Append(" Missing: ").Append(Render(missing)).Append('.');

            if (extra.Count > 0)
                detail.Append(" Not on the whitelist: ").Append(Render(extra)).Append('.');

            if (duplicated.Count > 0)
                detail.Append(" Supplied more than once: ").Append(Render(duplicated)).Append('.');

            throw new ArgumentException(
                "A vault item class payload must carry EXACTLY the collapsible keys, each once, with a null for any the " +
                "item does not have." + detail +
                " Expected: " + Render(whitelist) + "." +
                " Refusing rather than canonicalizing it: a short payload keys differently from a full one carrying the " +
                "same values plus a null, so one stored class would split in two, and a key outside the whitelist is one " +
                "the predicate never proved the item could be rebuilt from - the materializer would replay it anyway.",
                parameterName);
        }

        private static string Render<TKey>(IEnumerable<TKey> keys) where TKey : Enum
        {
            return string.Join(", ", keys.Select(key => $"{key} ({Convert.ToInt32(key, CultureInfo.InvariantCulture)})"));
        }

        public int? GetInt(PropertyInt key)
        {
            foreach (var kvp in Ints)
            {
                if (kvp.Key == key)
                    return kvp.Value;
            }

            return null;
        }

        public string GetString(PropertyString key)
        {
            foreach (var kvp in Strings)
            {
                if (kvp.Key == key)
                    return kvp.Value;
            }

            return null;
        }
    }

    /// <summary>
    /// A vault item CLASS: the canonical form of an override payload, the fixed-width key derived from
    /// it, and the one materializer that turns a (wcid, overrides, pooled value) triple back into a
    /// WorldObject.
    ///
    /// SAFETY NOTE, and it is the reason this file reads the way it does. VaultCollapse.cs:245-247 says
    /// a wrong read-time GROUPING answer costs a mislabelled panel row and never a destroyed item.
    /// THAT LICENCE DOES NOT REACH ANY OF THIS. The consumers of <see cref="VaultCollapse.TryDescribeClass"/>
    /// and of <see cref="Materialize"/> destroy the stored biota and rebuild it from the payload, so a
    /// key that merges two items which are not interchangeable, or a materializer that drops one of the
    /// payload's fields, silently changes a player's property and is unrecoverable. Everything here is
    /// written to refuse rather than to guess.
    ///
    /// ONE MATERIALIZER. <see cref="Materialize"/> will later serve the withdraw path, the vendor
    /// panel's display branch and a collapse-time self-check. A second implementation of "rebuild this
    /// item" is how those three drift apart, and the drift would only show as items that came back
    /// slightly wrong.
    ///
    /// KNOWN LIMITATION: TEMPLATE DRIFT. A payload is a set of OVERRIDES relative to a weenie
    /// template, and the predicate proves the item differs from that template on nothing but the
    /// whitelist - as the template stood AT DEPOSIT TIME. Every property NOT on the whitelist is
    /// therefore not stored at all; <see cref="Materialize"/> takes it from whatever the weenie says
    /// when the item is rebuilt. So if a content update retunes the salvage bag weenie (its burden,
    /// its icon, its max structure, anything outside the whitelist) between the deposit and the
    /// withdraw, the item that comes back carries the NEW value, not the one it went in with.
    ///
    /// This is documented rather than fixed, and deliberately:
    ///
    /// - it is PRE-EXISTING, not introduced here. The pristine stack ledger has exactly the same
    ///   exposure and has shipped with it: a collapsed stack is rebuilt from the current weenie too.
    ///   A class row is no worse, only newer.
    /// - "fixing" it means storing the full template alongside every row and replaying it, which turns
    ///   a counted row back into a biota and gives up the entire point of the tier.
    /// - for the retunes that actually happen it is usually the DESIRED behaviour: an operator who
    ///   reprices or re-icons a weenie expects stored copies to follow.
    ///
    /// What it is NOT is a silent identity change: the whitelisted properties - the ones that decide
    /// whether two items are interchangeable - are all stored in the payload and replayed exactly. The
    /// drift is confined to properties the class was never keyed on. Widening the whitelist widens
    /// what is preserved; it does not close the gap, because something is always left to the template.
    /// </summary>
    public static class VaultItemClass
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Bumped whenever the canonical encoding changes shape. It is part of the canonical text, so an
        /// old key and a new key can never collide and a stored key can always be told apart from one
        /// this build would produce.
        /// </summary>
        public const string CanonicalFormatVersion = "v1";

        /// <summary>Marks an ABSENT value. Not a legal rendering of any present one: a present int renders as digits, a present string as its length, a colon and its text.</summary>
        private const string AbsentToken = "~";

        #region Value banding

        /// <summary>
        /// The owner's v1 ruling: Value is EXCLUDED from class identity and pooled instead. Pass this.
        /// </summary>
        public const int ValueExcluded = 0;

        /// <summary>
        /// Which geometric band <paramref name="value"/> falls in, for a band width of
        /// <paramref name="valueBandPercent"/> percent. Band 0 means Value is not part of identity at
        /// all and this is never called.
        ///
        /// INTEGER ARITHMETIC ON PURPOSE. The obvious form is floor(log(v) / log(1 + p/100)), and
        /// IEEE Math.Log is not guaranteed to produce the same last bit on every runtime and
        /// architecture, so a value sitting exactly on a band boundary could bucket differently on two
        /// machines and split one stored class in half. The loop below is exact: band k covers
        /// [bound(k), bound(k+1)), where bound(0) = 1 and bound(k+1) = max(bound(k) * (100+p) / 100,
        /// bound(k) + 1). The +1 floor keeps it strictly increasing for small values, where integer
        /// division would otherwise stall forever at 1.
        /// </summary>
        public static int ValueBand(int value, int valueBandPercent)
        {
            if (valueBandPercent <= 0)
                throw new ArgumentOutOfRangeException(nameof(valueBandPercent), "ValueBand is only meaningful for a positive band width; band 0 means Value is excluded from identity entirely.");

            // Everything at or below zero shares band 0 with 1. A negative Value is not a thing a
            // salvage bag can have, and bucketing it alongside the smallest legal value is the
            // conservative direction: it merges nothing that is not already adjacent.
            if (value <= 1)
                return 0;

            var band = 0;
            long bound = 1;
            var factor = 100L + valueBandPercent;

            while (bound < value)
            {
                var next = bound * factor / 100L;

                if (next <= bound)
                    next = bound + 1;

                bound = next;
                band++;
            }

            // The loop stops at the first bound that REACHES the value, so a value exactly on a
            // boundary belongs to the band that opens there, not the one that closes.
            return bound == value ? band : band - 1;
        }

        #endregion

        #region Canonical form and key

        /// <summary>
        /// The one canonical serialization of a class. Deterministic, culture-free and unambiguous:
        ///
        /// - fields are emitted in a FIXED order (format version, band, wcid, then int keys ascending by
        ///   numeric id, then string keys ascending by numeric id), so no dictionary iteration order can
        ///   reach it;
        /// - every number is rendered with <see cref="CultureInfo.InvariantCulture"/>, so a Turkish or
        ///   German locale cannot change a digit or a sign;
        /// - every string is LENGTH-PREFIXED rather than quoted, for exactly the reason
        ///   VaultCollapse.Signature is: with no escaping, a quoted string could contain the separators
        ///   and two different payloads could canonicalize to one text. That is the false-POSITIVE
        ///   shape, and here it would merge two items that are not interchangeable and then hand one
        ///   player the other's item.
        ///
        /// <paramref name="valueBandPercent"/> of 0 is the owner's v1 ruling: PropertyInt.Value is
        /// omitted from the text entirely, because it is pooled rather than keyed. A positive band emits
        /// the BAND INDEX in its place. The band itself is part of the text, so a key computed under one
        /// policy can never be mistaken for one computed under another.
        /// </summary>
        public static string CanonicalForm(uint wcid, VaultItemClassOverrides overrides, int valueBandPercent)
        {
            if (overrides == null)
                throw new ArgumentNullException(nameof(overrides));

            if (valueBandPercent < 0)
                throw new ArgumentOutOfRangeException(nameof(valueBandPercent));

            var text = new StringBuilder(CanonicalFormatVersion);

            text.Append("|b").Append(valueBandPercent.ToString(CultureInfo.InvariantCulture));
            text.Append("|w").Append(wcid.ToString(CultureInfo.InvariantCulture));

            foreach (var kvp in overrides.Ints)
            {
                if (kvp.Key == PropertyInt.Value)
                {
                    if (valueBandPercent == ValueExcluded)
                        continue;

                    text.Append("|B").Append(((int)kvp.Key).ToString(CultureInfo.InvariantCulture)).Append('=');
                    text.Append(kvp.Value.HasValue
                        ? ValueBand(kvp.Value.Value, valueBandPercent).ToString(CultureInfo.InvariantCulture)
                        : AbsentToken);

                    continue;
                }

                text.Append("|I").Append(((int)kvp.Key).ToString(CultureInfo.InvariantCulture)).Append('=');
                text.Append(kvp.Value.HasValue ? kvp.Value.Value.ToString(CultureInfo.InvariantCulture) : AbsentToken);
            }

            foreach (var kvp in overrides.Strings)
            {
                text.Append("|S").Append(((int)kvp.Key).ToString(CultureInfo.InvariantCulture)).Append('=');

                if (kvp.Value == null)
                    text.Append(AbsentToken);
                else
                    text.Append(kvp.Value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(kvp.Value);
            }

            return text.ToString();
        }

        /// <summary>How many hex characters <see cref="ClassKey"/> returns. Fixed width, so a storage column can be pinned to it.</summary>
        public const int ClassKeyLength = 32;

        /// <summary>
        /// A fixed-width hash of <see cref="CanonicalForm"/>: the first 16 bytes of its SHA-256, as 32
        /// lowercase hex characters.
        ///
        /// SHA-256 rather than string.GetHashCode: GetHashCode is randomized per process by default on
        /// .NET, so a key derived from it would differ between two runs of the same server and every
        /// stored class row would stop matching on restart. Truncated to 16 bytes because a class key
        /// only has to separate the classes one account holds, and 128 bits is far past any collision
        /// risk at that scale while halving what a future row has to store.
        /// </summary>
        public static string ClassKey(uint wcid, VaultItemClassOverrides overrides, int valueBandPercent)
        {
            var canonical = CanonicalForm(wcid, overrides, valueBandPercent);

            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));

                var hex = new StringBuilder(ClassKeyLength);

                for (var i = 0; i < ClassKeyLength / 2; i++)
                    hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));

                return hex.ToString();
            }
        }

        /// <summary>
        /// The inverse of <see cref="CanonicalForm"/>, and it lives beside it deliberately so the two
        /// are read and changed together.
        ///
        /// WHY THIS EXISTS AT ALL. account_vault_class stores the canonical form and nothing else: the
        /// class key is a hash of exactly that text, and a second encoding of the same facts stored
        /// beside it (a JSON column, say) is how the two drift apart and a rebuilt item comes back
        /// wrong. The withdraw path therefore has to read the payload back OUT of the canonical text,
        /// which is what this does.
        ///
        /// IT REFUSES RATHER THAN GUESSES, for the same reason everything else in this file does: the
        /// caller materializes a real item from the result and hands it to a player. Anything this
        /// cannot parse exactly - a truncated row, a text written by a future format version, a key set
        /// that is not the whitelist - returns false and logs, and the caller must leave the row alone.
        ///
        /// THE PROOF IS A RE-CANONICALIZATION, not a field-by-field audit. Whatever is parsed is
        /// re-emitted through <see cref="CanonicalForm"/> and compared ordinally against the input, so
        /// a text that differs anywhere - a key out of order, a key twice, a stray character, a length
        /// prefix that disagrees with its string - is refused even if every individual field read
        /// cleanly. That check is what makes "parsed" mean "this text is exactly what this build would
        /// have written".
        ///
        /// PropertyInt.Value comes back ABSENT at band <see cref="ValueExcluded"/>, because the
        /// canonical text genuinely does not carry it - it is pooled in the row's total instead. The
        /// caller must therefore pass a pooled per-item value to <see cref="Materialize"/>, which wins
        /// over the payload; passing null there would REMOVE Value from the rebuilt item. A band above
        /// 0 is refused outright rather than parsed: the text carries a band INDEX where the value
        /// would be, and turning an index back into a value is exactly the guess this file does not
        /// make.
        /// </summary>
        public static bool TryParseCanonicalForm(string canonical, out uint wcid, out VaultItemClassOverrides overrides, out int valueBandPercent)
        {
            wcid = 0;
            overrides = null;
            valueBandPercent = ValueExcluded;

            if (string.IsNullOrEmpty(canonical))
            {
                log.Error("[VAULT] VaultItemClass.TryParseCanonicalForm was handed an empty canonical form; refusing.");
                return false;
            }

            try
            {
                var pos = 0;

                if (!Expect(canonical, ref pos, CanonicalFormatVersion))
                    return RefuseParse(canonical, $"it does not open with the format version {CanonicalFormatVersion}");

                if (!Expect(canonical, ref pos, "|b") || !TryReadDigits(canonical, ref pos, out var band) || band > int.MaxValue)
                    return RefuseParse(canonical, "the value band field is missing or is not a non-negative integer");

                if (band != ValueExcluded)
                    return RefuseParse(canonical, $"it was written at value band {band}, and a band index cannot be turned back into a Value; only band {ValueExcluded} rows can be rebuilt");

                if (!Expect(canonical, ref pos, "|w") || !TryReadDigits(canonical, ref pos, out var parsedWcid) || parsedWcid > uint.MaxValue)
                    return RefuseParse(canonical, "the wcid field is missing or is not an unsigned integer");

                var ints = new List<KeyValuePair<PropertyInt, int?>>();
                var strings = new List<KeyValuePair<PropertyString, string>>();

                while (pos < canonical.Length)
                {
                    if (canonical[pos] != '|')
                        return RefuseParse(canonical, $"expected a field separator at offset {pos}");

                    pos++;

                    if (pos >= canonical.Length)
                        return RefuseParse(canonical, "the text ends on a field separator");

                    var kind = canonical[pos++];

                    if (!TryReadDigits(canonical, ref pos, out var keyId) || keyId > int.MaxValue)
                        return RefuseParse(canonical, $"a field at offset {pos} carries no numeric property id");

                    if (!Expect(canonical, ref pos, "="))
                        return RefuseParse(canonical, $"a field at offset {pos} is not followed by '='");

                    if (kind == 'I')
                    {
                        var key = (PropertyInt)(int)keyId;

                        if (!VaultCollapse.CollapsibleIntKeyContract.Contains(key))
                            return RefuseParse(canonical, $"int property {keyId} is not on the collapsible whitelist");

                        if (!TryReadOptionalInt(canonical, ref pos, out var value))
                            return RefuseParse(canonical, $"int property {keyId} carries neither a number nor the absent token");

                        ints.Add(new KeyValuePair<PropertyInt, int?>(key, value));
                        continue;
                    }

                    if (kind == 'S')
                    {
                        var key = (PropertyString)(int)keyId;

                        if (!VaultCollapse.CollapsibleStringKeyContract.Contains(key))
                            return RefuseParse(canonical, $"string property {keyId} is not on the collapsible whitelist");

                        if (!TryReadOptionalString(canonical, ref pos, out var value))
                            return RefuseParse(canonical, $"string property {keyId} carries neither a length-prefixed string nor the absent token");

                        strings.Add(new KeyValuePair<PropertyString, string>(key, value));
                        continue;
                    }

                    // 'B' lands here as well as any junk character. It is the BANDED value field, which
                    // only a band above 0 emits, and that band was already refused above - so reaching
                    // it means the text and its own band field disagree.
                    return RefuseParse(canonical, $"unknown field kind '{kind}' at offset {pos - 1}");
                }

                // Band 0 omits PropertyInt.Value from the text entirely, and the payload contract
                // demands EXACTLY the whitelist, so it is restored as ABSENT. See this method's remarks
                // for why that is the honest encoding and what the caller owes because of it.
                if (!ints.Exists(kvp => kvp.Key == PropertyInt.Value))
                    ints.Add(new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, null));

                VaultItemClassOverrides parsed;

                try
                {
                    parsed = new VaultItemClassOverrides(ints, strings);
                }
                catch (ArgumentException ex)
                {
                    return RefuseParse(canonical, $"the key set it carries is not the whitelist: {ex.Message}");
                }

                // THE PROOF. Anything that read cleanly field by field but is not what this build would
                // have written fails here.
                var reEmitted = CanonicalForm((uint)parsedWcid, parsed, (int)band);

                if (!string.Equals(reEmitted, canonical, StringComparison.Ordinal))
                    return RefuseParse(canonical, $"it does not re-canonicalize to itself (got '{reEmitted}')");

                wcid = (uint)parsedWcid;
                overrides = parsed;
                valueBandPercent = (int)band;

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] VaultItemClass.TryParseCanonicalForm threw on '{canonical}'; refusing the row rather than rebuilding from a payload it could not read.", ex);

                wcid = 0;
                overrides = null;
                valueBandPercent = ValueExcluded;

                return false;
            }
        }

        private static bool RefuseParse(string canonical, string why)
        {
            log.Error($"[VAULT] VaultItemClass.TryParseCanonicalForm refused '{canonical}': {why}.");
            return false;
        }

        private static bool Expect(string text, ref int pos, string literal)
        {
            if (pos + literal.Length > text.Length)
                return false;

            if (string.CompareOrdinal(text, pos, literal, 0, literal.Length) != 0)
                return false;

            pos += literal.Length;
            return true;
        }

        /// <summary>Reads one or more ASCII digits as a long. False when there is not at least one.</summary>
        private static bool TryReadDigits(string text, ref int pos, out long value)
        {
            value = 0;

            var start = pos;

            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
            {
                value = (value * 10) + (text[pos] - '0');

                // Cheap overflow stop. Nothing this format emits is anywhere near it, and letting a
                // hostile row wrap around would turn a refusal into a wrong number.
                if (value > uint.MaxValue)
                    return false;

                pos++;
            }

            return pos > start;
        }

        /// <summary>The absent token, or a possibly-negative int. Stops at the next field separator.</summary>
        private static bool TryReadOptionalInt(string text, ref int pos, out int? value)
        {
            value = null;

            if (pos < text.Length && text[pos] == AbsentToken[0])
            {
                pos++;
                return true;
            }

            var start = pos;

            if (pos < text.Length && text[pos] == '-')
                pos++;

            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
                pos++;

            if (pos == start)
                return false;

            if (!int.TryParse(text.Substring(start, pos - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                return false;

            value = parsed;
            return true;
        }

        /// <summary>
        /// The absent token, or a LENGTH-PREFIXED string. Positional by construction: the payload may
        /// contain '|', '=' and ':', so nothing here may split on a separator.
        /// </summary>
        private static bool TryReadOptionalString(string text, ref int pos, out string value)
        {
            value = null;

            if (pos < text.Length && text[pos] == AbsentToken[0])
            {
                pos++;
                return true;
            }

            if (!TryReadDigits(text, ref pos, out var length) || length > int.MaxValue)
                return false;

            if (!Expect(text, ref pos, ":"))
                return false;

            if (pos + length > text.Length)
                return false;

            value = text.Substring(pos, (int)length);
            pos += (int)length;

            return true;
        }

        #endregion

        #region Materializer

        /// <summary>
        /// THE materializer. Builds a fresh instance of <paramref name="wcid"/>, replays the override
        /// payload onto it, sets its Value, and renders it once so it lands in the same dat-derived
        /// state every stored item is already in.
        ///
        /// <paramref name="pooledValue"/> is the per-item share of a class row's pooled total, and it
        /// WINS over the payload's own Value. That is the owner's ruling made concrete: Value is not
        /// part of class identity, so a class row carries a count and a total and hands out
        /// round(remaining total / remaining count) per item. Pass NULL to replay the payload's own
        /// Value instead, which is what a round-trip self-check wants and what a band above 0 would
        /// want.
        ///
        /// Returns null if the wcid cannot be instantiated. Callers must treat that as "do not destroy
        /// anything", never as "hand the player nothing".
        /// </summary>
        public static WorldObject Materialize(uint wcid, VaultItemClassOverrides overrides, int? pooledValue)
        {
            if (overrides == null)
                throw new ArgumentNullException(nameof(overrides));

            var item = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (item == null)
            {
                log.Error($"[VAULT] VaultItemClass.Materialize could not instantiate wcid {wcid}; no item was produced.");
                return null;
            }

            foreach (var kvp in overrides.Ints)
            {
                if (kvp.Key == PropertyInt.Value && pooledValue.HasValue)
                    continue;

                if (kvp.Value.HasValue)
                    item.SetProperty(kvp.Key, kvp.Value.Value);
                else
                    item.RemoveProperty(kvp.Key);
            }

            foreach (var kvp in overrides.Strings)
            {
                if (kvp.Value != null)
                    item.SetProperty(kvp.Key, kvp.Value);
                else
                    item.RemoveProperty(kvp.Key);
            }

            if (pooledValue.HasValue)
                item.SetProperty(PropertyInt.Value, pooledValue.Value);

            // The same normalization VaultReferenceCache applies to a template and that every stored
            // item has already been through on its first render. Without it a materialized item carries
            // the authored icon while the one it replaced carried the dat's, which is a visible
            // difference to the player and would make a collapse-time self-check fail for no real
            // reason. Its own try/catch: a dat that cannot be read must not lose the item.
            try
            {
                item.CalculateObjDesc();
            }
            catch (Exception ex)
            {
                log.Warn($"[VAULT] VaultItemClass.Materialize could not render wcid {wcid}; the item keeps its authored appearance.", ex);
            }

            return item;
        }

        #endregion
    }
}
