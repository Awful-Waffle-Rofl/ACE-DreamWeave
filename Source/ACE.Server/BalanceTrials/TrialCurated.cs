using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.ClassAbilities;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// Curated archetypes: masters built by hand and adopted from a live character (owner rulings R1, R2), with no archetype
    /// file. The master's own stamp is the whole registry entry:
    ///
    ///     v1|adopted:&lt;id&gt;:&lt;Class&gt;:&lt;level&gt;|adopted|promoted|yyyy-MM-ddTHH:mm:ssZ
    ///
    /// It is the promoted 5-field form TrialRebuild.TryParseStamp already accepts, so no tool's parser changes: trialseed and
    /// prodcharseedverify read it as promoted and leave the slot alone (a reseed DELETEs every slot that is not promoted), and
    /// rebuild accepts it as a promoted master. Field 1 carries the identity, so promote's lineage check (field 1 of the rep
    /// against field 1 of the master) means archetype identity; field 2 is the literal <see cref="CalibrationToken"/>.
    /// &lt;level&gt; is the adopted character's level at adopt time and is the archetype's TargetLevel. Nothing here caps or
    /// fixes a level (R3). Pure: no world or database access.
    /// </summary>
    public static class TrialCurated
    {
        /// <summary>The prefix of a curated stamp's field 1.</summary>
        public const string IdentityPrefix = "adopted";

        /// <summary>A curated stamp's field 2, where a seeded stamp carries its calibration sha.</summary>
        public const string CalibrationToken = "adopted";

        /// <summary>
        /// The longest curated id. 40 keeps the longest field 1 ("adopted:" + id + ":" + the longest class name + ":" + a 4-digit
        /// level) within 64 characters, the width of trial_run.archetype_checksum (CHAR(64)), where the trial run log writes it.
        /// </summary>
        public const int MaxIdLength = 40;

        /// <summary>The width of trial_run.archetype_checksum, which a curated archetype's field 1 must fit.</summary>
        public const int MaxIdentityLength = 64;

        /// <summary>The lowest slot a new curated archetype may take; slot 0 is never picked.</summary>
        public const int FirstSlot = 1;

        /// <summary>The buff profile of every curated archetype (a scenario's buffProfile still wins, TrialBuffs.EffectiveProfile).</summary>
        public const string BuffProfile = TrialBuffs.OwnSpellbook;

        /// <summary>The id rule: the archetype loader's (lower-case letters, digits, hyphens, so never a ':'), at most <see cref="MaxIdLength"/> characters.</summary>
        public static bool IsValidId(string id, out string reason)
        {
            if (!TrialJson.IsId(id))
            {
                reason = $"id '{id}' must be lower-case letters, digits and hyphens";
                return false;
            }

            if (id.Length > MaxIdLength)
            {
                reason = $"id '{id}' is {id.Length} characters; at most {MaxIdLength}, so the stamp's archetype field fits the {MaxIdentityLength}-character run log column";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>A class name exactly as ClassAbilityClass spells it (letters only, defined, not None), as the archetype loader parses one.</summary>
        public static bool TryParseClass(string name, out ClassAbilityClass cls)
        {
            cls = ClassAbilityClass.None;

            if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[A-Za-z]+\z")
                || !Enum.TryParse(name, false, out ClassAbilityClass parsed) || !Enum.IsDefined(typeof(ClassAbilityClass), parsed) || parsed == ClassAbilityClass.None)
                return false;

            cls = parsed;
            return true;
        }

        /// <summary>Field 1 of a curated stamp: <c>adopted:&lt;id&gt;:&lt;Class&gt;:&lt;level&gt;</c>. Throws on an invalid id, class or level.</summary>
        public static string Identity(string id, ClassAbilityClass cls, int level)
        {
            if (!IsValidId(id, out var reason))
                throw new ArgumentException(reason, nameof(id));

            if (cls == ClassAbilityClass.None || !Enum.IsDefined(typeof(ClassAbilityClass), cls))
                throw new ArgumentException($"class {cls} is not a class", nameof(cls));

            if (level < 1)
                throw new ArgumentException($"level {level} must be at least 1", nameof(level));

            return string.Join(":", IdentityPrefix, id, cls.ToString(), level.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Parses field 1 of a curated stamp (also the lineage a broken record keeps). False for a seeded sha or anything malformed.</summary>
        public static bool TryParseIdentity(string field1, out string id, out ClassAbilityClass cls, out int level)
        {
            id = null;
            cls = ClassAbilityClass.None;
            level = 0;

            if (string.IsNullOrEmpty(field1))
                return false;

            var parts = field1.Split(':');

            if (parts.Length != 4 || parts[0] != IdentityPrefix || !IsValidId(parts[1], out _) || !TryParseClass(parts[2], out var c))
                return false;

            // Digits only, in the one form Identity writes (no sign, no leading zero), so a stamp has exactly one spelling.
            if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var l) || l < 1
                || l.ToString(CultureInfo.InvariantCulture) != parts[3])
                return false;

            id = parts[1];
            cls = c;
            level = l;
            return true;
        }

        /// <summary>The curated master stamp, promoted at <paramref name="utc"/>. Throws on an invalid id, class or level.</summary>
        public static string Stamp(string id, ClassAbilityClass cls, int level, DateTime utc)
            => TrialRebuild.PromotedStamp(Identity(id, cls, level), CalibrationToken, utc);

        /// <summary>
        /// Parses a curated master stamp. It must be a promoted stamp by the shared parser (TrialRebuild.TryParseStamp, 5 fields),
        /// field 2 must be <see cref="CalibrationToken"/> and field 1 a curated identity. A seeded or seeded-then-promoted stamp is false.
        /// </summary>
        public static bool TryParse(string stamp, out string id, out ClassAbilityClass cls, out int level, out DateTime promotedAt)
        {
            id = null;
            cls = ClassAbilityClass.None;
            level = 0;
            promotedAt = default;

            if (!TrialRebuild.TryParseStamp(stamp, out var field1, out var field2, out var at) || at == null || field2 != CalibrationToken)
                return false;

            if (!TryParseIdentity(field1, out id, out cls, out level))
                return false;

            promotedAt = at.Value;
            return true;
        }

        /// <summary>What PickSlot needs to know about one master slot.</summary>
        /// <param name="Slot">0..TrialRoster.MasterCount-1.</param>
        /// <param name="Stamp">The master's PropertyString.TrialArchetypeStamp, or null.</param>
        /// <param name="HasCharacter">A character row exists for the master guid.</param>
        /// <param name="GateHeld">TrialCopyGate holds the master guid (any hold).</param>
        /// <param name="Broken">TrialCopyGate has a broken record for the master guid.</param>
        /// <param name="BrokenLineage">The archetype field the broken record keeps (TryGetBrokenLineage), or null.</param>
        /// <param name="Seeded">The slot of a seeded archetype file.</param>
        public readonly record struct SlotFacts(int Slot, string Stamp, bool HasCharacter, bool GateHeld, bool Broken, string BrokenLineage, bool Seeded);

        /// <summary>
        /// The slot a curated archetype <paramref name="id"/> is adopted into. Re-adopting an existing curated id reuses its slot
        /// (its stamp names the id, or, with no stamp, its broken record's lineage does). Otherwise the lowest slot in
        /// <see cref="FirstSlot"/>..MasterCount-1 that is free: no stamp, no character row, no gate hold, no broken record, and
        /// not the slot of a seeded file. False with the refusal when the id is invalid, the reused slot is a seeded file's, or
        /// no slot is free.
        /// </summary>
        public static bool TryPickSlot(string id, IReadOnlyList<SlotFacts> slots, out int slot, out bool reused, out string refusal)
        {
            slot = -1;
            reused = false;

            if (!IsValidId(id, out refusal))
                return false;

            var bySlot = (slots ?? Array.Empty<SlotFacts>()).Where(s => s.Slot >= 0 && s.Slot < TrialRoster.MasterCount).GroupBy(s => s.Slot).ToDictionary(g => g.Key, g => g.First());

            foreach (var s in bySlot.Values.OrderBy(s => s.Slot))
            {
                var names = TryParse(s.Stamp, out var stampedId, out _, out _, out _) && stampedId == id
                    || string.IsNullOrEmpty(s.Stamp) && TryParseIdentity(s.BrokenLineage, out var lineageId, out _, out _) && lineageId == id;

                if (!names)
                    continue;

                if (s.Seeded)
                {
                    refusal = $"curated archetype '{id}' sits in slot {s.Slot}, which a seeded archetype file also names; fix the data before adopting";
                    return false;
                }

                slot = s.Slot;
                reused = true;
                refusal = null;
                return true;
            }

            for (var candidate = FirstSlot; candidate < TrialRoster.MasterCount; candidate++)
            {
                // A slot with no facts at all is unknown, never free.
                if (!bySlot.TryGetValue(candidate, out var s))
                    continue;

                if (!string.IsNullOrEmpty(s.Stamp) || s.HasCharacter || s.GateHeld || s.Broken || s.Seeded)
                    continue;

                slot = candidate;
                refusal = null;
                return true;
            }

            refusal = $"no free master slot in {FirstSlot}..{TrialRoster.MasterCount - 1} (a slot is free only with no stamp, no character row, no copy in flight, no broken record and no seeded file)";
            return false;
        }
    }
}
