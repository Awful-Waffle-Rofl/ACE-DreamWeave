using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The Mule Vendor collapse test (DESIGN section 8, risk R6).
    ///
    /// Collapse is the ONLY place in the vault design where an item is destroyed and later re-created,
    /// so it is the only place item degradation can occur. A false positive here silently destroys a
    /// player's tinkered gear and replaces it with a vendor-fresh copy, which is unrecoverable and
    /// would not look like a bug to anyone.
    ///
    /// The rule is a diff, never a blacklist. Instantiate a fresh weenie of the item's own wcid in
    /// memory, diff the two biotas, and collapse ONLY if the diff is empty. Anything unrecognized
    /// shows up as a difference and the item keeps its biota, so the design is conservative by
    /// construction rather than by enumeration. Do NOT test on Stackable, on MaxStackSize, or on a
    /// list of "interesting" properties: arrows stack and can be tinkered and imbued, healing kits
    /// stack and carry Structure, and any blacklist rots the moment someone adds a property to the
    /// codebase.
    ///
    /// Any change that makes <see cref="IgnoredIntKeys"/> and friends longer is moving in the wrong
    /// direction. Every exemption below is a property of where the object currently sits or how it
    /// got there, never of what it is.
    ///
    /// This type deliberately knows nothing about vendors, containers or the network, so it stays
    /// unit-testable with no world database.
    ///
    /// KNOWN LIMITATION, deliberately left alone. Four model types carry a DatabaseRecordId that ties
    /// a row back to the table it came from: PropertiesEmote, PropertiesEmoteAction,
    /// PropertiesGenerator and PropertiesCreateList. A shard-sourced candidate and a world-sourced
    /// reference will not agree on it, and the IList collections here are compared in order while
    /// IWeenie.cs:39 notes that no order is guaranteed for db records. Either one makes those
    /// collections permanently unequal, so an item carrying them never collapses. That is a false
    /// NEGATIVE: it costs one database row and destroys nothing, which is the safe direction. The
    /// obvious fix is to exclude a property by name, and that is exactly the enumeration-based
    /// reasoning DESIGN section 8 forbids, so changing it is a spec question rather than a code
    /// change. Related shape, also unreachable today: a type that stored its data in public FIELDS
    /// rather than properties would render as TypeName{} on both sides and compare equal. No model
    /// type in ACE.Entity.Models does that.
    /// </summary>
    public static class VaultCollapse
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// How deep the value-signature walker will recurse into a nested object graph before giving
        /// up. Hitting the limit (or a reference cycle) is reported as a difference, so a graph this
        /// code cannot fully compare simply never collapses.
        /// </summary>
        private const int MaxSignatureDepth = 8;

        private const int MaxSignatureTextInMessage = 200;

        private const string Absent = "<absent>";

        #region Ignore lists

        /// <summary>
        /// The guid. Every instance differs; that is not a modification.
        /// Plus PropertiesPosition (all of it): where the object is standing. A vault item has none,
        /// a fresh weenie may.
        /// </summary>
        private static readonly HashSet<string> IgnoredBiotaProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Biota.Id),
            nameof(Biota.PropertiesPosition),
        };

        /// <summary>Where it currently sits. Set in Container.cs when an item is added, cleared when removed.</summary>
        private static readonly HashSet<PropertyInstanceId> IgnoredIidKeys = new HashSet<PropertyInstanceId>
        {
            PropertyInstanceId.Container,
            PropertyInstanceId.Wielder,
            PropertyInstanceId.Owner,
        };

        /// <summary>
        /// PlacementPosition is slot ordering within a container. CreationTimestamp is when it was made.
        /// Neither says anything about what the item is.
        /// </summary>
        private static readonly HashSet<PropertyInt> IgnoredIntKeys = new HashSet<PropertyInt>
        {
            PropertyInt.PlacementPosition,
            PropertyInt.CreationTimestamp,
        };

        /// <summary>
        /// Persistence bookkeeping. DESIGN section 8 states the rule as "diff the two biotas ignoring
        /// guid, container, placement position and timestamps", and all three of these are timestamps
        /// of how the object got here, never of what it is.
        ///
        /// SoldTimestamp is vendor bookkeeping.
        ///
        /// CheckpointTimestamp (96) is stamped by WorldObject.SaveBiotaToDatabase on every enqueued
        /// save (WorldObject_Database.cs:63), and every ordinary inventory path calls that:
        /// Player_Inventory.cs:186 inside TryCreateInInventoryWithNetworking covers every loot drop,
        /// vendor purchase, quest reward and /create, plus Player_Inventory.cs:1271, :1527, :1843,
        /// :2079, :3444 and Container.cs:666. It carries no [Ephemeral] attribute (PropertyFloat.cs:112), so it
        /// persists. A reference built by WorldObjectFactory.CreateNewWorldObject is never saved, so
        /// it never has one. Without this exemption the diff is non-empty for anything a player has
        /// ever held and the ledger never fires at all.
        ///
        /// ReleasedTimestamp (56) is stamped by WorldObject.Destroy (WorldObject.cs:879) and is
        /// likewise not [Ephemeral] (PropertyFloat.cs:70).
        ///
        /// Neither is authored content: "select count(*) from weenie_properties_float where type in
        /// (56,96)" against ace_world returns 0, so exempting them cannot mask a difference that a
        /// weenie template could ever have expressed. Verified 2026-08-27.
        /// </summary>
        private static readonly HashSet<PropertyFloat> IgnoredFloatKeys = new HashSet<PropertyFloat>
        {
            PropertyFloat.SoldTimestamp,
            PropertyFloat.CheckpointTimestamp,
            PropertyFloat.ReleasedTimestamp,
        };

        /// <summary>
        /// The GUARDED stack-derived exemption. WorldObject.SetStackSize derives Value and
        /// EncumbranceVal from StackSize, so a partial stack differs from a fresh weenie on all three
        /// by arithmetic alone; without this exemption the collapse test would reject every partial
        /// stack and the ledger would never fire. It is guarded, not blanket: see
        /// <see cref="StackDerivationHolds"/>. A value that does not match its own derivation is a
        /// modification and keeps its biota.
        /// </summary>
        private static readonly HashSet<PropertyInt> StackDerivedIntKeys = new HashSet<PropertyInt>
        {
            PropertyInt.StackSize,
            PropertyInt.Value,
            PropertyInt.EncumbranceVal,
        };

        #endregion

        #region Coverage guard

        /// <summary>
        /// Every collection on ACE.Entity.Models.Biota that <see cref="Diff(Biota, Biota, bool)"/>
        /// actually compares. The point of naming them here is the inversion below: adding a
        /// collection to Biota and forgetting to diff it must be caught, not silently ignored, or the
        /// next property someone adds becomes a hole through which a modified item collapses.
        /// </summary>
        private static readonly HashSet<string> DiffedBiotaProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Biota.WeenieClassId),
            nameof(Biota.WeenieType),

            nameof(Biota.PropertiesBool),
            nameof(Biota.PropertiesDID),
            nameof(Biota.PropertiesFloat),
            nameof(Biota.PropertiesIID),
            nameof(Biota.PropertiesInt),
            nameof(Biota.PropertiesInt64),
            nameof(Biota.PropertiesString),

            nameof(Biota.PropertiesSpellBook),

            nameof(Biota.PropertiesAnimPart),
            nameof(Biota.PropertiesPalette),
            nameof(Biota.PropertiesTextureMap),

            nameof(Biota.PropertiesCreateList),
            nameof(Biota.PropertiesEmote),
            nameof(Biota.PropertiesEventFilter),
            nameof(Biota.PropertiesGenerator),

            nameof(Biota.PropertiesAttribute),
            nameof(Biota.PropertiesAttribute2nd),
            nameof(Biota.PropertiesBodyPart),
            nameof(Biota.PropertiesSkill),

            nameof(Biota.PropertiesBook),
            nameof(Biota.PropertiesBookPageData),

            nameof(Biota.PropertiesAllegiance),
            nameof(Biota.PropertiesEnchantmentRegistry),
            nameof(Biota.HousePermissions),
        };

        /// <summary>
        /// Biota properties that are neither diffed nor deliberately ignored. Non-empty means someone
        /// extended Biota without deciding what collapse should do about it, and every call to
        /// <see cref="Diff(Biota, Biota, bool)"/> throws until that decision is made.
        /// </summary>
        private static readonly List<string> UnhandledBiotaProperties;

        static VaultCollapse()
        {
            UnhandledBiotaProperties = typeof(Biota)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => p.Name)
                .Where(name => !DiffedBiotaProperties.Contains(name) && !IgnoredBiotaProperties.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Throws if any Biota property is neither diffed nor deliberately exempted.
        ///
        /// Public and parameterless on purpose, for two callers. ACE.Server.Program calls it once
        /// during startup so that extending Biota without deciding about it stops the server from
        /// booting, rather than throwing out of the first player's deposit on a live shard. And
        /// ACE.Server.Tests calls it directly, because every other test reaches this guard only in
        /// its non-throwing state: deleting the call from Diff would leave all of them green and the
        /// inversion this design leans on would be gone silently.
        /// </summary>
        public static void AssertBiotaCoverage()
        {
            if (UnhandledBiotaProperties.Count == 0)
                return;

            throw new InvalidOperationException(
                "VaultCollapse does not know what to do with these ACE.Entity.Models.Biota properties: " +
                string.Join(", ", UnhandledBiotaProperties) +
                ". Every Biota property must either be diffed (add it to DiffedBiotaProperties and give it a real " +
                "comparison in Diff) or be deliberately exempted (add it to IgnoredBiotaProperties with a reason). " +
                "Refusing to run rather than silently skipping it: an unchecked property is a hole through which a " +
                "modified item collapses into a ledger row and loses its state.");
        }

        #endregion

        #region Grouping (Docs/MuleVendor/2026-08-31-vault-grouping-design.md)

        /// <summary>
        /// The ONLY property differences read-time vault grouping tolerates. Two stored biotas that
        /// differ on nothing but these present as one panel row.
        ///
        /// THIS IS NOT AN IGNORE LIST AND MUST NEVER BE WIRED INTO ONE. It does not weaken
        /// <see cref="AssertBiotaCoverage"/>, it does not weaken <see cref="IsPristine(WorldObject)"/>,
        /// and it does not weaken <see cref="Diff(Biota, Biota, bool)"/> - grouping is a DISPLAY
        /// projection over items every one of which keeps its own biota, so a wrong answer here costs a
        /// mislabelled row, never a destroyed item. That asymmetry is the whole reason this set is
        /// allowed to exist beside a diff that tolerates nothing.
        ///
        /// Reusing Diff rather than writing a fresh comparison is the point: the coverage guard above
        /// still fires when someone extends Biota, so a property added later cannot silently become
        /// groupable.
        ///
        /// Why these three and nothing else:
        /// - ItemWorkmanship and NumItemsInMaterial are the raw pair behind WorldObject.Workmanship,
        ///   and the bucket key already pins their QUOTIENT to two decimals. (77, 12) and (154, 24)
        ///   both read 6.42 to the player and must group.
        /// - Value is cosmetic on a salvage bag (a weighted sum of what was salvaged) and is why the
        ///   caller restricts grouping to ItemType.TinkeringMaterial: applying this set generally would
        ///   merge two differently-priced weapons into one row with no way to tell them apart.
        ///
        /// Structure is deliberately ABSENT, because the caller's bucket key makes it exact-equal.
        /// Name is deliberately absent too: a bag's name is $"Salvage ({Structure})"
        /// (Player_Crafting.cs:286), so equal Structure already implies equal Name, and a hand-renamed
        /// bag correctly splits into its own row.
        /// </summary>
        private static readonly HashSet<PropertyInt> GroupableIntKeys = new HashSet<PropertyInt>
        {
            PropertyInt.ItemWorkmanship,
            PropertyInt.NumItemsInMaterial,
            PropertyInt.Value,
        };

        /// <summary>
        /// <see cref="GroupableIntKeys"/> rendered as the exact line prefixes
        /// <see cref="DiffDictionary{TKey,TValue}"/> emits, so the tolerance is derived from the one
        /// constant above rather than restated as literals that could drift from it.
        /// </summary>
        private static readonly HashSet<string> GroupableDiffPrefixes = new HashSet<string>(
            GroupableIntKeys.Select(key => $"{nameof(Biota.PropertiesInt)}.{key}:"), StringComparer.Ordinal);

        /// <summary>
        /// True when <paramref name="candidate"/> may join the group <paramref name="representative"/>
        /// stands for: the two biotas differ on nothing outside <see cref="GroupableIntKeys"/>.
        ///
        /// Takes the RUNTIME ACE.Entity.Models.Biota (property dictionaries), never the EF entity
        /// ACE.Database.Models.Shard.Biota - same as every other member of this class.
        ///
        /// It takes NO LOCK, exactly like <see cref="Diff(Biota, Biota, bool)"/> which it delegates to,
        /// so unlike <see cref="IsPristine(WorldObject)"/> it is safe to call from a caller that
        /// already holds a lock. It reaches no database either, so it costs no world read per item.
        ///
        /// candidateIsStackable is passed as FALSE on purpose. The stack-derived exemption would let
        /// two members with different StackSize group, and a group row's Count means "how many
        /// members", so it has no way to express that. No weenie in ace_world carries
        /// ItemType.TinkeringMaterial with a MaxStackSize above 1 (verified 2026-08-31), so this
        /// forecloses a case that is unreachable today rather than changing a real outcome.
        ///
        /// A diff line this method does not recognize is a REFUSAL to group, so the depth-limit and
        /// cycle report - which carries no key at all - correctly keeps an unwalkable graph out of a
        /// group.
        /// </summary>
        public static bool AreGroupable(Biota candidate, Biota representative)
        {
            foreach (var difference in Diff(candidate, representative, candidateIsStackable: false))
            {
                var separator = difference.IndexOf(':');

                if (separator < 0 || !GroupableDiffPrefixes.Contains(difference.Substring(0, separator + 1)))
                    return false;
            }

            return true;
        }

        /// <summary>The bucket value for an item carrying no ItemWorkmanship at all.</summary>
        public const int WorkmanshipBucketNone = int.MinValue;

        /// <summary>
        /// The bucket value for an item whose workmanship arithmetic produced something no key can be
        /// made from (NaN, or a magnitude the guarded cast refuses). Deliberately NOT
        /// <see cref="WorkmanshipBucketNone"/>: a corrupted item must not bucket with every item that
        /// simply has no workmanship.
        /// </summary>
        public const int WorkmanshipBucketUnkeyable = int.MinValue + 1;

        /// <summary>
        /// The workmanship component of a display bucket: the quotient the CLIENT renders, as an
        /// integer hundredth.
        ///
        /// THE ONE COPY OF THIS ARITHMETIC. It used to live privately inside
        /// AccountVaultStore.BucketKey, which takes a WorldObject and so could not answer for a counted
        /// class row, which has no object behind it. Two copies of "which items are interchangeable to
        /// the player" is exactly what produced the 2026-09-25 entry-count defect, where the panel
        /// grouped on the quotient while the class key kept the raw pair and the cap counted the finer
        /// partition. It is over raw nullable ints for that reason: both callers can reach it.
        ///
        /// Rounding to what the CLIENT renders is deliberate rather than sloppy: two bags both reading
        /// "6.42" must group even though their raw (ItemWorkmanship, NumItemsInMaterial) pairs differ -
        /// (77, 12) and (154, 24) are the worked example.
        ///
        /// THE QUOTIENT IS COMPUTED HERE AND NOT READ FROM WorldObject.Workmanship, and that is a
        /// correctness requirement rather than a style choice. That getter is not a getter: when the
        /// quotient falls outside [1, 10] it REWRITES ItemWorkmanship in place through SetProperty
        /// (WorldObject_Properties.cs:1587-1598). Calling it from here would make a nominally read-only
        /// grouping computation mutate a persisted property of a stored biota on every deposit,
        /// withdraw, EntryCount and GetEntries, under stateLock only, with no BiotaDatabaseLock and no
        /// audit row. Reads of this store do not write to items. Callers holding a WorldObject must
        /// therefore feed this from GetProperty(PropertyInt.ItemWorkmanship).
        ///
        /// The arithmetic below is deliberately identical to the getter's FIRST line - the same float
        /// division widened to double and rounded at two decimals - AND THE RECOVERY AND CLAMP BRANCH
        /// TOO, minus its write. The key is therefore what that getter would have RETURNED on its first
        /// read, for every input rather than only for in-range ones, and it is now stable on every
        /// later read as well, which it could not be while the first read rewrote the value the second
        /// read keyed on.
        ///
        /// Replicating the recovery branch is not optional tidiness. An earlier revision dropped it and
        /// claimed that was conservative because it could only SPLIT rows. That was false, because the
        /// bucket key - not the diff - is the real gate on whether a workmanship difference matters:
        /// <see cref="AreGroupable"/> forgives ItemWorkmanship and NumItemsInMaterial outright once two
        /// items share a bucket. Legacy botched-formula data makes that reachable. Two bags at
        /// Structure 5, one (ItemWorkmanship 50000, NumItemsInMaterial 1) and one (100000, 2), have the
        /// same RAW quotient of 50000 and would have landed in one bucket and been merged - while the
        /// recovery formula, which reads ItemWorkmanship and Structure and ignores NumItemsInMaterial,
        /// separates them as 1.0 and 2.0. They also appraise at different workmanship tiers once
        /// withdrawn, because the client-visible getter still clamps. So the row would have merged two
        /// items the player can tell apart.
        ///
        /// The cast is guarded rather than trusted. A double-to-int conversion in .NET SATURATES, it
        /// does not throw and is not unspecified: (int)3e9 is int.MaxValue, (int)double.NegativeInfinity
        /// is int.MinValue, (int)double.NaN is 0. int.MinValue is exactly the "no workmanship at all"
        /// sentinel, so an unguarded cast lets a corrupted item bucket with every item that has none.
        /// ItemWorkmanship has no range validation where it is written - AdminCommands.cs:4676-4688
        /// parses an admin-typed float straight into it - so a large negative value needs no zero
        /// denominator to get there. The clamp above happens to bound every path that reaches the cast
        /// today; the guard stays because it protects the CAST, not the arithmetic that fed it, and it
        /// must keep working if the clamp is ever changed.
        /// </summary>
        public static int WorkmanshipBucket(int? itemWorkmanship, int? numItemsInMaterial, int? structure)
        {
            if (itemWorkmanship == null)
                return WorkmanshipBucketNone;

            var denominator = numItemsInMaterial ?? 1;

            var workmanship = (float)itemWorkmanship.Value / denominator;

            if (workmanship < 1.0f || workmanship > 10.0f)
            {
                // Structure defaults to 1 HERE, not to the -1 a bucket key uses for "absent". Two
                // different questions, and conflating them would change the recovered value.
                var recoveryStructure = structure ?? 1;

                workmanship = (float)itemWorkmanship.Value / 10000 / recoveryStructure;
                workmanship = Math.Clamp(workmanship, 1.0f, 10.0f);
            }

            var scaled = Math.Round(workmanship * 100.0);

            // NaN reaches here from 0/0 (a zero ItemWorkmanship with a zero Structure), and Math.Clamp
            // passes NaN through rather than clamping it.
            if (double.IsNaN(scaled) || scaled <= int.MinValue + 2 || scaled >= int.MaxValue)
                return WorkmanshipBucketUnkeyable;

            return (int)scaled;
        }

        /// <summary>The format stamp on <see cref="ClassDisplayGroupKey"/>, so two spellings can never collide.</summary>
        private const string ClassDisplayGroupKeyVersion = "d1";

        /// <summary>
        /// The DISPLAY group a counted class row belongs to: the same four components a stored item's
        /// bucket uses - wcid, Structure, <see cref="WorkmanshipBucket"/> and Name.
        ///
        /// WHY THOSE FOUR AND NOTHING ELSE. <see cref="AreGroupable"/> forgives exactly
        /// {ItemWorkmanship, NumItemsInMaterial, Value} inside a bucket and forgives nothing else, so a
        /// set of items sharing those four draws as ONE panel row. A class key, by contrast, keys on
        /// the RAW (ItemWorkmanship, NumItemsInMaterial) pair, which makes the class partition strictly
        /// FINER than the display partition. Counting class rows therefore over-counted what the panel
        /// draws, which is the defect this exists to close.
        ///
        /// THIS IS A DISPLAY PROJECTION AND NOTHING ELSE. It never merges, rewrites or deletes a class
        /// row, and it is not an identity key: <see cref="CollapsibleIntKeys"/>' remarks forbid
        /// admitting a key the payload does not carry and the materializer does not replay, and merging
        /// two rows that share a display bucket would mean picking one raw pair and discarding the
        /// other - handing a player back a bag carrying numbers they never deposited. Fine-grained rows
        /// are what keeps withdrawal exact. This groups how they are DRAWN and COUNTED, never what they
        /// are.
        ///
        /// The name is LENGTH-PREFIXED for the same reason <see cref="VaultItemClass.CanonicalForm"/>
        /// does it: with no escaping, a name containing a separator could collapse two different groups
        /// into one key, which would draw two unlike rows as one.
        /// </summary>
        public static string ClassDisplayGroupKey(uint wcid, VaultItemClassOverrides overrides)
        {
            if (overrides == null)
                throw new ArgumentNullException(nameof(overrides));

            var structure = overrides.GetInt(PropertyInt.Structure);

            var bucket = WorkmanshipBucket(overrides.GetInt(PropertyInt.ItemWorkmanship),
                                           overrides.GetInt(PropertyInt.NumItemsInMaterial),
                                           structure);

            var name = overrides.GetString(PropertyString.Name);

            var text = new StringBuilder(ClassDisplayGroupKeyVersion);

            text.Append("|w").Append(wcid.ToString(CultureInfo.InvariantCulture));

            // -1 for absent, exactly as AccountVaultStore.BucketKey's Structure component does it, so a
            // class row and a stored item of the same shape land on the same bucket.
            text.Append("|s").Append((structure ?? -1).ToString(CultureInfo.InvariantCulture));
            text.Append("|k").Append(bucket.ToString(CultureInfo.InvariantCulture));
            text.Append("|n");

            if (name == null)
                text.Append('~');
            else
                text.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name);

            return text.ToString();
        }

        /// <summary>
        /// A fixed-width id for one DISPLAY group: the first 16 bytes of the SHA-256 of
        /// <paramref name="displayGroupKey"/> (normally <see cref="ClassDisplayGroupKey"/>'s output), as
        /// 32 lowercase hex characters - the same scheme and width as <see cref="VaultItemClass.ClassKey"/>,
        /// so one char(32) column shape serves both.
        ///
        /// WHY A LISTING ANCHORS ON THIS AND NOT ON A CLASS KEY. A drawn class line can stand for
        /// several account_vault_class rows, and its REPRESENTATIVE member (the first in class key
        /// order) changes as members are drained or deposited. A listing pinned to a member key would
        /// silently start naming a different line, or none, after an ordinary withdraw. The display key
        /// is a function of what the line IS (wcid, Structure, workmanship bucket, Name), so it is the
        /// same for as long as the line exists and changes only when the line itself does.
        ///
        /// SHA-256 rather than GetHashCode for the reason ClassKey gives: GetHashCode is randomized per
        /// process, and this id is persisted in market_listing.
        /// </summary>
        public static string ClassDisplayId(string displayGroupKey)
        {
            if (displayGroupKey == null)
                throw new ArgumentNullException(nameof(displayGroupKey));

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(displayGroupKey));

                var hex = new StringBuilder(VaultItemClass.ClassKeyLength);

                for (var i = 0; i < VaultItemClass.ClassKeyLength / 2; i++)
                    hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));

                return hex.ToString();
            }
        }

        /// <summary>
        /// Whether <paramref name="id"/> has the shape <see cref="ClassDisplayId"/> produces: exactly 32
        /// lowercase hex characters. For validating an id a client sent, before it is compared against
        /// anything - uppercase is refused rather than normalized, because the wire contract fixes the
        /// spelling and two spellings of one id would be two keys in the listing index.
        /// </summary>
        public static bool IsWellFormedClassDisplayId(string id)
        {
            if (id == null || id.Length != VaultItemClass.ClassKeyLength)
                return false;

            foreach (var c in id)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }

            return true;
        }

        #endregion

        #region Item classes (the counted storage tier's predicate)

        /// <summary>
        /// The ONLY property differences an item CLASS tolerates. Two items of the same wcid that
        /// differ on nothing but these are interchangeable: replaying these keys onto a fresh template
        /// reproduces either one exactly.
        ///
        /// READ THE ASYMMETRY BEFORE CHANGING ANYTHING HERE. <see cref="GroupableIntKeys"/> above is a
        /// DISPLAY tolerance, and its own remarks say a wrong answer there costs a mislabelled panel row
        /// and never a destroyed item, because every grouped item keeps its own biota. THAT LICENCE DOES
        /// NOT TRANSFER TO THIS SET. The consumers of <see cref="TryDescribeClass"/> destroy the stored
        /// biota and rebuild the item from the payload, so a key wrongly admitted here silently rewrites
        /// a property of a player's item and the loss is unrecoverable. This set may only ever contain a
        /// key whose value the payload actually carries and the materializer actually replays.
        ///
        /// Why each one, all five written by the salvage path and nothing else:
        /// - Structure (92), ItemWorkmanship (105) and NumItemsInMaterial (170) are written by
        ///   Player_Crafting.TryAddSalvage (Player_Crafting.cs:332, :366, :367);
        /// - Value (19) by Player_Crafting.AddSalvage (Player_Crafting.cs:243);
        /// - Name by TryAddSalvage (Player_Crafting.cs:369).
        ///
        /// NAME STAYS IN, even though an untouched bag's name is $"Salvage ({Structure})" and so adds
        /// zero cardinality for a normal bag. Keeping it means a hand-renamed bag automatically forms
        /// its own class of one, with no special case anywhere; dropping it would merge a renamed bag
        /// into the anonymous class and hand the player back a differently-named item.
        ///
        /// MaterialType (131) is deliberately OUT. It comes off the weenie unchanged, so it never
        /// appears in the diff at all - and if some future path ever did write it per-instance, its
        /// absence here means that item refuses to be classed rather than being merged across materials.
        /// </summary>
        private static readonly HashSet<PropertyInt> CollapsibleIntKeys = new HashSet<PropertyInt>
        {
            PropertyInt.Value,
            PropertyInt.Structure,
            PropertyInt.ItemWorkmanship,
            PropertyInt.NumItemsInMaterial,
        };

        /// <summary><see cref="CollapsibleIntKeys"/>'s string half. See that set's remarks for Name.</summary>
        private static readonly HashSet<PropertyString> CollapsibleStringKeys = new HashSet<PropertyString>
        {
            PropertyString.Name,
        };

        /// <summary>
        /// The two sets above rendered as the exact line prefixes <see cref="DiffDictionary{TKey,TValue}"/>
        /// emits, derived rather than restated so they cannot drift from the constants.
        /// </summary>
        private static readonly HashSet<string> CollapsibleDiffPrefixes = new HashSet<string>(
            CollapsibleIntKeys.Select(key => $"{nameof(Biota.PropertiesInt)}.{key}:")
                .Concat(CollapsibleStringKeys.Select(key => $"{nameof(Biota.PropertiesString)}.{key}:")),
            StringComparer.Ordinal);

        /// <summary>
        /// The whitelist again, read-only, so <see cref="VaultItemClassOverrides"/> can REFUSE a payload
        /// whose key set is not exactly it.
        ///
        /// ONE SOURCE OF TRUTH, deliberately: these are projections of the two sets above rather than a
        /// restatement, so a key added to the whitelist flows into the payload contract without anybody
        /// having to remember to edit VaultItemClass.cs as well. A duplicated list would rot on the
        /// first change, and it would rot silently - the two halves would simply disagree about what a
        /// payload is allowed to carry.
        ///
        /// Sorted by numeric id so the refusal message reads in the same order as the canonical form.
        /// </summary>
        internal static readonly IReadOnlyList<PropertyInt> CollapsibleIntKeyContract =
            CollapsibleIntKeys.OrderBy(key => (int)key).ToList();

        /// <summary><see cref="CollapsibleIntKeyContract"/>'s string half.</summary>
        internal static readonly IReadOnlyList<PropertyString> CollapsibleStringKeyContract =
            CollapsibleStringKeys.OrderBy(key => (int)key).ToList();

        /// <summary>Refusal reasons that are not a diff prefix. Short, stable tokens: the dry-run command histograms them.</summary>
        public static class ClassRefusal
        {
            public const string NoBiota = "no-biota";
            public const string NotTinkeringMaterial = "not-tinkering-material";
            public const string Stacked = "stacked";
            public const string Equippable = "equippable";
            public const string Timed = "timed";
            public const string NoReference = "no-reference";
            public const string GraphTruncated = "graph-truncated";
            public const string Threw = "threw";
        }

        /// <summary>
        /// <see cref="IsPristine(WorldObject)"/> generalized from "differs on nothing" to "differs only
        /// on a whitelist": true when <paramref name="item"/> is provably reproducible from a fresh
        /// instance of its own weenie plus the values in <paramref name="overrides"/>.
        ///
        /// The lossless-and-reversible property is a THEOREM rather than a hope. Step 4 below proves the
        /// item differs from its template on nothing outside <see cref="CollapsibleIntKeys"/> and
        /// <see cref="CollapsibleStringKeys"/>; step 5 puts exactly those keys' values into the payload;
        /// so replaying the payload onto a fresh template reproduces the item. Every key the payload
        /// carries is replayed by <see cref="VaultItemClass.Materialize"/>, absent values included.
        ///
        /// REFUSAL IS NEVER AN ERROR. A false here means the item stays an ordinary stored biota, which
        /// is always safe; <paramref name="refusalReason"/> exists so a caller can count why, never so
        /// it can tell a player something failed. Total by construction, like IsPristine: anything this
        /// cannot answer - a hostile object graph, a world database that dropped, the coverage guard
        /// firing - resolves to false.
        ///
        /// CALL THIS FROM OUTSIDE ANY BiotaDatabaseLock REGION, for the same reason IsPristine says so:
        /// it takes the item's read lock itself and BiotaDatabaseLock is NoRecursion, so a caller that
        /// already holds it would get a silent false on every call.
        /// </summary>
        public static bool TryDescribeClass(WorldObject item, out VaultItemClassOverrides overrides, out string refusalReason)
        {
            overrides = null;

            try
            {
                if (item?.Biota == null)
                {
                    refusalReason = ClassRefusal.NoBiota;
                    return false;
                }

                // 1. ItemType.TinkeringMaterial EXACTLY, mirroring AccountVaultStore.IsGroupCandidate
                //    (AccountVaultStore.cs:783-786) and its reason: ItemType is a [Flags] enum, so an
                //    item carrying TinkeringMaterial alongside another bit declines rather than matching
                //    partially. The restriction is what makes pooling Value defensible at all - Value is
                //    an accumulated salvage total on a bag and a meaningful price on a weapon.
                if (item.ItemType != ItemType.TinkeringMaterial)
                {
                    refusalReason = ClassRefusal.NotTinkeringMaterial;
                    return false;
                }

                // 2. A class row counts ITEMS, so it has no way to express a per-item stack size.
                //
                //    TESTED ON THE DATA, NOT ON THE C# TYPE, and that is load-bearing rather than
                //    stylistic: every real salvage bag is a WeenieType.CraftTool, CraftTool derives
                //    from Stackable (CraftTool.cs:9), and so an `item is Stackable` test would refuse
                //    every single bag and this predicate would never fire on anything. MaxStackSize is
                //    checked alongside StackSize so an item that COULD be stacked is refused even while
                //    it happens to hold one unit.
                if ((item.StackSize ?? 1) != 1 || (item.MaxStackSize ?? 1) != 1)
                {
                    refusalReason = ClassRefusal.Stacked;
                    return false;
                }

                // 3. Never anything equippable. Nothing salvage-shaped is, and a class row is the wrong
                //    home for gear even if some future item type made it past the diff.
                if ((item.ValidLocations ?? 0) != 0)
                {
                    refusalReason = ClassRefusal.Equippable;
                    return false;
                }

                // 4. A timed item is already refused entry by AccountVaultStore.TryDeposit
                //    (AccountVaultStore.cs:2053) for a reason that applies here twice over: the diff
                //    ignores CreationTimestamp, so destroying and rebuilding a timed item restarts its
                //    clock. Repeated here rather than assumed, because this predicate must be safe for
                //    any caller, not only one standing behind that deposit check.
                if (item.Lifespan != null)
                {
                    refusalReason = ClassRefusal.Timed;
                    return false;
                }

                var reference = VaultReferenceCache.Get(item.WeenieClassId);

                if (reference == null)
                {
                    refusalReason = ClassRefusal.NoReference;
                    return false;
                }

                List<string> diffs;

                // Same lock rule and the same reasoning as IsPristine: the diff iterates every biota
                // collection, and those must not be added to or removed from while it does.
                item.BiotaDatabaseLock.EnterReadLock();

                try
                {
                    // candidateIsStackable FALSE, always. Step 2 has already established the item is not
                    // stacked, so the stack-derived exemption has nothing to do here - and leaving it
                    // switched on would exempt Value and EncumbranceVal by arithmetic, which is the one
                    // way a real difference could reach the payload unnoticed.
                    diffs = Diff(item.Biota, reference.Biota, candidateIsStackable: false);

                    if (!AllDifferencesAreCollapsible(diffs, out refusalReason))
                        return false;

                    // 5. The payload, read off the SAME locked view the diff just proved things about.
                    //    Reading it after releasing the lock would let a concurrent write land between
                    //    the proof and the capture.
                    overrides = CaptureOverridesLocked(item.Biota);
                }
                finally
                {
                    item.BiotaDatabaseLock.ExitReadLock();
                }

                refusalReason = null;
                return true;
            }
            catch (Exception ex)
            {
                var guid = item != null ? $"0x{item.Guid.Full:X8}" : "<null item>";
                var wcid = item != null ? item.WeenieClassId.ToString() : "<null item>";

                log.Error($"[VAULT] VaultCollapse.TryDescribeClass failed for {guid} (wcid {wcid}); treating the item as unclassifiable so it keeps its own biota.", ex);

                overrides = null;
                refusalReason = ClassRefusal.Threw;
                return false;
            }
        }

        /// <summary>Convenience overload for callers that do not histogram the reason.</summary>
        public static bool TryDescribeClass(WorldObject item, out VaultItemClassOverrides overrides)
        {
            return TryDescribeClass(item, out overrides, out _);
        }

        /// <summary>
        /// True when every difference the diff reported is one the payload carries.
        ///
        /// The keyless line comes FIRST in the reason, ahead of any prefix. A diff line with no ':' is
        /// the depth-limit and reference-cycle report (see the tail of <see cref="Diff(Biota, Biota, bool)"/>),
        /// which means the comparison could not be finished - so the item was not proven to differ only
        /// on the whitelist, whatever else the list happens to say. Reporting it as whichever ordinary
        /// prefix came first would hide an unwalkable graph inside an innocuous histogram bucket.
        /// </summary>
        private static bool AllDifferencesAreCollapsible(List<string> diffs, out string refusalReason)
        {
            string firstPrefix = null;

            foreach (var difference in diffs)
            {
                var separator = difference.IndexOf(':');

                if (separator < 0)
                {
                    refusalReason = ClassRefusal.GraphTruncated;
                    return false;
                }

                if (CollapsibleDiffPrefixes.Contains(difference.Substring(0, separator + 1)))
                    continue;

                if (firstPrefix == null)
                    firstPrefix = difference.Substring(0, separator);
            }

            refusalReason = firstPrefix;
            return firstPrefix == null;
        }

        /// <summary>
        /// The whitelisted keys' values, read off the item's biota. Caller holds the item's
        /// BiotaDatabaseLock read lock.
        ///
        /// EVERY whitelisted key is emitted, carrying a null when the item does not have it. See
        /// <see cref="VaultItemClassOverrides"/> for why absence has to be expressible rather than
        /// simply omitted.
        /// </summary>
        private static VaultItemClassOverrides CaptureOverridesLocked(Biota biota)
        {
            var ints = new List<KeyValuePair<PropertyInt, int?>>();

            foreach (var key in CollapsibleIntKeys)
            {
                int? value = null;

                if (biota.PropertiesInt != null && biota.PropertiesInt.TryGetValue(key, out var found))
                    value = found;

                ints.Add(new KeyValuePair<PropertyInt, int?>(key, value));
            }

            var strings = new List<KeyValuePair<PropertyString, string>>();

            foreach (var key in CollapsibleStringKeys)
            {
                string value = null;

                if (biota.PropertiesString != null && biota.PropertiesString.TryGetValue(key, out var found))
                    value = found;

                strings.Add(new KeyValuePair<PropertyString, string>(key, value));
            }

            // VaultItemClassOverrides sorts both lists itself, so the HashSet iteration order above
            // cannot reach the canonical form.
            return new VaultItemClassOverrides(ints, strings);
        }

        #endregion

        #region Public API

        /// <summary>
        /// True when the item is provably identical to a fresh instance of its own weenie.
        ///
        /// Total by construction: anything this cannot answer resolves to false, because the caller
        /// destroys the item on a true and a wrong true is unrecoverable. Every statement in the body
        /// is inside the outermost try for that reason, including the construction of the reference
        /// object (which reaches the world database and can throw if it drops mid-session) and its
        /// destruction.
        ///
        /// CALL THIS FROM OUTSIDE ANY BiotaDatabaseLock REGION. It takes the item's read lock itself,
        /// and BiotaDatabaseLock is a default-policy ReaderWriterLockSlim (NoRecursion), so a caller
        /// that already holds the lock would make EnterReadLock throw, be caught below, and get a
        /// false back on every single call. That failure mode is silent and looks exactly like
        /// "nothing ever collapses".
        /// </summary>
        public static bool IsPristine(WorldObject item)
        {
            try
            {
                if (item?.Biota == null)
                    return false;

                // Inside the try on purpose: this reaches WorldDatabaseWithEntityCache.GetWeenie for
                // anything not already cached, which runs an unguarded EF query. If ace_world drops
                // mid-session the retry strategy eventually surfaces a MySqlException, and that must
                // not escape into the deposit handler.
                var reference = WorldObjectFactory.CreateNewWorldObject(item.WeenieClassId);

                if (reference == null)
                    return false;   // cannot prove it pristine, so it keeps its biota

                NormalizeDatDerivedState(reference);

                try
                {
                    // Best practice in this codebase is to hold BiotaDatabaseLock any time the biota is
                    // read, because its collections must not be added to or removed from while something
                    // iterates them (WorldObject_Database.cs:26-37). The diff iterates every one of them.
                    item.BiotaDatabaseLock.EnterReadLock();

                    try
                    {
                        return Diff(item.Biota, reference.Biota, item is Stackable).Count == 0;
                    }
                    finally
                    {
                        item.BiotaDatabaseLock.ExitReadLock();
                    }
                }
                finally
                {
                    // The reference exists only to be compared against. Destroy it so its guid returns
                    // to the pool; leaking one guid per deposit would exhaust the dynamic range on a
                    // busy shard. Its own try/catch because a throw here would otherwise escape the
                    // outer catch's return and mask the answer we already computed.
                    try
                    {
                        reference.Destroy();
                    }
                    catch (Exception destroyEx)
                    {
                        log.Error($"VaultCollapse.IsPristine could not destroy its scratch reference for wcid {item.WeenieClassId}; a guid has been leaked.", destroyEx);
                    }
                }
            }
            catch (Exception ex)
            {
                // The coverage guard throws when someone extends Biota, a hostile object graph can
                // throw out of an enumerator, and the world database can drop. None of them should
                // take down a deposit: log it and keep the item's biota, which is always the safe
                // answer.
                var guid = item != null ? $"0x{item.Guid.Full:X8}" : "<null item>";
                var wcid = item != null ? item.WeenieClassId.ToString() : "<null item>";

                log.Error($"VaultCollapse.IsPristine failed for {guid} (wcid {wcid}); treating the item as NOT pristine so it keeps its biota.", ex);

                return false;
            }
        }

        /// <summary>
        /// Every difference found, as human-readable strings. Empty means pristine.
        ///
        /// Diagnostic counterpart to <see cref="IsPristine(WorldObject)"/>. This one does NOT swallow
        /// exceptions, because its callers are commands and logging rather than the deposit path.
        /// </summary>
        public static List<string> Diff(WorldObject item)
        {
            if (item?.Biota == null)
                return new List<string> { "item has no biota" };

            var reference = WorldObjectFactory.CreateNewWorldObject(item.WeenieClassId);

            if (reference == null)
                return new List<string> { $"could not instantiate a reference weenie for wcid {item.WeenieClassId}" };

            // Same normalization as IsPristine, and not optional: a diagnostic that reported an icon
            // difference IsPristine does not see would send an investigation after a phantom.
            NormalizeDatDerivedState(reference);

            try
            {
                // Same lock rule as IsPristine.
                item.BiotaDatabaseLock.EnterReadLock();

                try
                {
                    return Diff(item.Biota, reference.Biota, item is Stackable);
                }
                finally
                {
                    item.BiotaDatabaseLock.ExitReadLock();
                }
            }
            finally
            {
                // Same reason as IsPristine: the reference is scratch, and its guid must go back.
                reference.Destroy();
            }
        }

        /// <summary>
        /// Brings a freshly instantiated reference object into the state a real item is ALREADY in
        /// after its first render, so the diff compares like with like.
        ///
        /// The one thing this fixes today is the icon. WorldObject.CalculateObjDesc writes the dat's
        /// sub-palette icon over the authored one for any item carrying a ClothingBase and a
        /// PaletteTemplate or Shade (WorldObject_Networking.cs:994-995), through IconId's ordinary
        /// setter (WorldObject_Properties.cs), which persists. A stored item has been rendered at least
        /// once and therefore carries the dat value; a reference built by
        /// WorldObjectFactory.CreateNewWorldObject and diffed immediately still carries the authored
        /// one, so the diff was never empty and NOTHING ClothingBase-bearing could ever collapse.
        /// Confirmed against the live shard 2026-08-31: all 32 mod-hammer biotas carry Icon 100669066
        /// while all five hammer weenies in ace_world carry 100669065.
        ///
        /// Adding PropertyDataId.Icon to an ignore list would have been the narrower change and is
        /// WRONG: Aetheria (Aetheria.cs) and tailoring (Tailoring.cs) both legitimately rewrite an
        /// item's icon, and ignoring it would mask those real modifications - the false-POSITIVE
        /// direction, which destroys the item.
        ///
        /// Its own try/catch, and deliberately not folded into the caller's. CalculateObjDesc reads
        /// DatManager, which a diff has no business depending on; if it throws, the item simply keeps
        /// the un-normalized icon and fails the diff as it did before this change - never worse.
        /// </summary>
        private static void NormalizeDatDerivedState(WorldObject reference)
        {
            try
            {
                reference.CalculateObjDesc();
            }
            catch (Exception ex)
            {
                log.Warn($"VaultCollapse could not normalize the dat-derived state of its scratch reference for wcid {reference.WeenieClassId}; the item will simply not be judged pristine.", ex);
            }
        }

        /// <summary>Diff overload taking an explicit reference, so tests need no world database.</summary>
        public static List<string> Diff(Biota candidate, Biota reference, bool candidateIsStackable)
        {
            AssertBiotaCoverage();

            var diffs = new List<string>();

            if (candidate == null)
            {
                diffs.Add("candidate biota is null");
                return diffs;
            }

            if (reference == null)
            {
                diffs.Add("reference biota is null");
                return diffs;
            }

            var ctx = new SignatureContext();

            if (candidate.WeenieClassId != reference.WeenieClassId)
                diffs.Add($"WeenieClassId: candidate={candidate.WeenieClassId}, reference={reference.WeenieClassId}");

            if (candidate.WeenieType != reference.WeenieType)
                diffs.Add($"WeenieType: candidate={candidate.WeenieType}, reference={reference.WeenieType}");

            // The stack-derived exemption is decided once, from the candidate alone, before any int is
            // compared. If the candidate's own arithmetic does not hold, no exemption applies and all
            // three properties are diffed like anything else.
            var stackExempt = candidateIsStackable && StackDerivationHolds(candidate);

            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesBool), candidate.PropertiesBool, reference.PropertiesBool, null);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesDID), candidate.PropertiesDID, reference.PropertiesDID, null);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesFloat), candidate.PropertiesFloat, reference.PropertiesFloat, IgnoredFloatKeys.Contains);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesIID), candidate.PropertiesIID, reference.PropertiesIID, IgnoredIidKeys.Contains);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesInt), candidate.PropertiesInt, reference.PropertiesInt,
                key => IgnoredIntKeys.Contains(key) || (stackExempt && StackDerivedIntKeys.Contains(key)));
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesInt64), candidate.PropertiesInt64, reference.PropertiesInt64, null);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesString), candidate.PropertiesString, reference.PropertiesString, null);

            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesSpellBook), candidate.PropertiesSpellBook, reference.PropertiesSpellBook, null);

            DiffCollection(diffs, ctx, nameof(Biota.PropertiesAnimPart), candidate.PropertiesAnimPart, reference.PropertiesAnimPart);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesPalette), candidate.PropertiesPalette, reference.PropertiesPalette);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesTextureMap), candidate.PropertiesTextureMap, reference.PropertiesTextureMap);

            DiffCollection(diffs, ctx, nameof(Biota.PropertiesCreateList), candidate.PropertiesCreateList, reference.PropertiesCreateList);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesEmote), candidate.PropertiesEmote, reference.PropertiesEmote);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesEventFilter), candidate.PropertiesEventFilter, reference.PropertiesEventFilter);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesGenerator), candidate.PropertiesGenerator, reference.PropertiesGenerator);

            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesAttribute), candidate.PropertiesAttribute, reference.PropertiesAttribute, null);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesAttribute2nd), candidate.PropertiesAttribute2nd, reference.PropertiesAttribute2nd, null);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesBodyPart), candidate.PropertiesBodyPart, reference.PropertiesBodyPart, null);
            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesSkill), candidate.PropertiesSkill, reference.PropertiesSkill, null);

            DiffValue(diffs, ctx, nameof(Biota.PropertiesBook), candidate.PropertiesBook, reference.PropertiesBook);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesBookPageData), candidate.PropertiesBookPageData, reference.PropertiesBookPageData);

            DiffDictionary(diffs, ctx, nameof(Biota.PropertiesAllegiance), candidate.PropertiesAllegiance, reference.PropertiesAllegiance, null);
            DiffCollection(diffs, ctx, nameof(Biota.PropertiesEnchantmentRegistry), candidate.PropertiesEnchantmentRegistry, reference.PropertiesEnchantmentRegistry);
            DiffDictionary(diffs, ctx, nameof(Biota.HousePermissions), candidate.HousePermissions, reference.HousePermissions, null);

            // A graph this code could not walk to the bottom has not been proven identical, so it is
            // reported as a difference rather than assumed clean.
            if (ctx.Truncated)
                diffs.Add("biota comparison hit a depth limit or a reference cycle, so the item was not proven identical");

            return diffs;
        }

        #endregion

        #region Stack derivation

        /// <summary>
        /// True when the candidate's Value and EncumbranceVal are exactly what SetStackSize would have
        /// derived from its own StackSize. This is the guard on the stack-derived exemption: an item
        /// whose Value has moved off its own derivation has been modified, and must keep its biota.
        /// </summary>
        private static bool StackDerivationHolds(Biota candidate)
        {
            var props = candidate.PropertiesInt;

            if (props == null)
                return false;

            if (!props.TryGetValue(PropertyInt.StackSize, out var stackSize))
                return false;

            // A zero or negative stack satisfies both derivations trivially (0 == unit * 0), which
            // would exempt StackSize itself and let a zero-size stack be judged identical to a
            // size-1 reference and collapse into a ledger row of quantity 0.
            if (stackSize <= 0)
                return false;

            if (!props.TryGetValue(PropertyInt.Value, out var value))
                return false;

            if (!props.TryGetValue(PropertyInt.EncumbranceVal, out var encumbrance))
                return false;

            // Absent unit properties count as 0, matching SetStackSize's own (StackUnitValue ?? 0).
            props.TryGetValue(PropertyInt.StackUnitValue, out var unitValue);
            props.TryGetValue(PropertyInt.StackUnitEncumbrance, out var unitEncumbrance);

            return value == (long)unitValue * stackSize
                && encumbrance == (long)unitEncumbrance * stackSize;
        }

        #endregion

        #region Comparison helpers

        private static void DiffDictionary<TKey, TValue>(List<string> diffs, SignatureContext ctx, string label,
            IDictionary<TKey, TValue> candidate, IDictionary<TKey, TValue> reference, Func<TKey, bool> ignoreKey)
        {
            if (candidate == null && reference == null)
                return;

            var keys = new HashSet<TKey>();

            if (candidate != null)
            {
                foreach (var key in candidate.Keys)
                    keys.Add(key);
            }

            if (reference != null)
            {
                foreach (var key in reference.Keys)
                    keys.Add(key);
            }

            foreach (var key in keys.OrderBy(KeyName, StringComparer.Ordinal))
            {
                if (ignoreKey != null && ignoreKey(key))
                    continue;

                TValue candidateValue = default;
                TValue referenceValue = default;

                var inCandidate = candidate != null && candidate.TryGetValue(key, out candidateValue);
                var inReference = reference != null && reference.TryGetValue(key, out referenceValue);

                if (!inCandidate && !inReference)
                    continue;

                var candidateText = inCandidate ? Signature(candidateValue, ctx, 0) : Absent;
                var referenceText = inReference ? Signature(referenceValue, ctx, 0) : Absent;

                if (!string.Equals(candidateText, referenceText, StringComparison.Ordinal))
                    diffs.Add($"{label}.{KeyName(key)}: candidate={Clip(candidateText)}, reference={Clip(referenceText)}");
            }
        }

        private static void DiffCollection<T>(List<string> diffs, SignatureContext ctx, string label,
            ICollection<T> candidate, ICollection<T> reference)
        {
            // Only populated collections are initialized on a Biota, so null and empty are equivalent.
            var candidateCount = candidate?.Count ?? 0;
            var referenceCount = reference?.Count ?? 0;

            if (candidateCount == 0 && referenceCount == 0)
                return;

            if (candidateCount != referenceCount)
            {
                diffs.Add($"{label}: candidate has {candidateCount} entries, reference has {referenceCount}");
                return;
            }

            var candidateText = Signature(candidate, ctx, 0);
            var referenceText = Signature(reference, ctx, 0);

            if (!string.Equals(candidateText, referenceText, StringComparison.Ordinal))
                diffs.Add($"{label}: candidate={Clip(candidateText)}, reference={Clip(referenceText)}");
        }

        private static void DiffValue(List<string> diffs, SignatureContext ctx, string label, object candidate, object reference)
        {
            var candidateText = Signature(candidate, ctx, 0);
            var referenceText = Signature(reference, ctx, 0);

            if (!string.Equals(candidateText, referenceText, StringComparison.Ordinal))
                diffs.Add($"{label}: candidate={Clip(candidateText)}, reference={Clip(referenceText)}");
        }

        private static string KeyName<TKey>(TKey key)
        {
            return Convert.ToString(key, CultureInfo.InvariantCulture) ?? "null";
        }

        private static string Clip(string text)
        {
            if (text == null)
                return "null";

            if (text.Length <= MaxSignatureTextInMessage)
                return text;

            return text.Substring(0, MaxSignatureTextInMessage) + "...";
        }

        #endregion

        #region Value signatures

        /// <summary>
        /// Carries the per-Diff state of the signature walker: which objects are on the current path
        /// (so a reference cycle cannot loop forever) and whether anything had to be truncated.
        /// </summary>
        private sealed class SignatureContext
        {
            public bool Truncated;

            public readonly HashSet<object> Path = new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        /// <summary>
        /// Test seam. Renders one value exactly as the diff does, so the canonical encoding can be
        /// asserted on directly instead of through a diff outcome.
        ///
        /// This exists because the encoding cannot be pinned any other way against today's model
        /// types: a collision needs two ordinal-adjacent string properties, and no type in
        /// ACE.Entity.Models has a pair (PropertiesBookPageData sorts AuthorAccount, AuthorId,
        /// AuthorName, ... so the uint always splits the two strings), while the cross-element attack
        /// is blocked because DiffCollection's count check fires before any signature is computed. A
        /// test written through Diff therefore passes with or without the length prefix and pins
        /// nothing. Visible to ACE.Server.Tests via InternalsVisibleTo in ACE.Server.csproj:15.
        /// </summary>
        internal static string SignatureForTests(object value)
        {
            return Signature(value, new SignatureContext(), 0);
        }

        /// <summary>
        /// Renders a value as a canonical string, walking into nested objects and collections by
        /// reflection. Reflecting rather than listing fields is the same conservatism as diffing
        /// rather than blacklisting: a field added to one of these model types is compared without
        /// anyone remembering to come back here.
        /// </summary>
        private static string Signature(object value, SignatureContext ctx, int depth)
        {
            if (value == null)
                return "null";

            if (depth > MaxSignatureDepth)
            {
                ctx.Truncated = true;
                return "<depth-limit>";
            }

            var type = value.GetType();

            if (type.IsEnum)
                return type.Name + "." + value;

            switch (value)
            {
                case string text:
                    // Length-prefixed, not quoted. Object properties render as Name=<sig>; with no
                    // escaping, so a quoted string could contain the separators and two different
                    // objects could canonicalize to one signature, which is a FALSE POSITIVE shape:
                    // it would judge a modified item pristine and destroy it. A length prefix is
                    // unambiguous with no escape table to maintain and nothing to rot.
                    return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text;
                case bool flag:
                    return flag ? "true" : "false";
                case float single:
                    return single.ToString("R", CultureInfo.InvariantCulture);
                case double real:
                    return real.ToString("R", CultureInfo.InvariantCulture);
                case decimal fixedPoint:
                    return fixedPoint.ToString(CultureInfo.InvariantCulture);
                case Guid guid:
                    return guid.ToString();
                case DateTime dateTime:
                    return dateTime.ToString("O", CultureInfo.InvariantCulture);
                case TimeSpan timeSpan:
                    return timeSpan.ToString("c", CultureInfo.InvariantCulture);
            }

            if (type.IsPrimitive)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (!ctx.Path.Add(value))
            {
                ctx.Truncated = true;
                return "<cycle>";
            }

            try
            {
                if (value is IEnumerable enumerable)
                {
                    var parts = new List<string>();

                    foreach (var element in enumerable)
                        parts.Add(Signature(element, ctx, depth + 1));

                    // Lists and arrays carry meaning in their order, so keep it. Sets and dictionaries
                    // do not guarantee an enumeration order, so sort them to avoid a spurious diff.
                    if (!(value is IList) && !type.IsArray)
                        parts.Sort(StringComparer.Ordinal);

                    return "[" + string.Join(",", parts) + "]";
                }

                var builder = new StringBuilder(type.Name).Append('{');

                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    object propertyValue;

                    try
                    {
                        propertyValue = property.GetValue(value);
                    }
                    catch (Exception)
                    {
                        // A property this code cannot even read has not been proven identical.
                        ctx.Truncated = true;
                        continue;
                    }

                    builder.Append(property.Name).Append('=').Append(Signature(propertyValue, ctx, depth + 1)).Append(';');
                }

                return builder.Append('}').ToString();
            }
            finally
            {
                ctx.Path.Remove(value);
            }
        }

        #endregion
    }
}
