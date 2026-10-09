using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The materials a Wanted order may name (WANTED-DESIGN 4.1) and THE rule for what fills one.
    ///
    /// THE TWO KINDS HAVE DIFFERENT MATERIAL LISTS. Bags are built from Player.MaterialSalvage - one bag
    /// wcid per material - minus the five category headers that map to wcid 0, leaving 72. Hammers exist
    /// in only the five materials SalvageForge.MaterialTable names, and that dictionary is read directly
    /// rather than copied, so adding a sixth Hammer there needs no change in this file.
    ///
    /// Never reads PropertyManager (unit tests have no shard).
    /// </summary>
    public static class MarketSalvageMaterials
    {
        public sealed class MaterialEntry
        {
            public int MaterialType { get; set; }
            public string MaterialName { get; set; }

            /// <summary>Bag or Hammer. Fixes which wcid, name and icon the other fields describe.</summary>
            public MarketBuyOrderKind Kind { get; set; }

            /// <summary>The bag wcid for a bag entry, the HAMMER wcid for a hammer entry.</summary>
            public uint Wcid { get; set; }

            /// <summary>Display name of <see cref="Wcid"/>. Named for the bag because the wire field is bag_name.</summary>
            public string BagName { get; set; }
            public uint IconId { get; set; }
            public uint? IconOverlayId { get; set; }
            public uint? IconUnderlayId { get; set; }

            /// <summary>TRUE when a Hammer exists in this material, so a client can offer the hammer kind without a second round trip.</summary>
            public bool HasHammer { get; set; }
        }

        private static readonly Dictionary<int, uint> wcidByMaterial = Player.MaterialSalvage
            .Where(kvp => kvp.Value != 0)
            .ToDictionary(kvp => kvp.Key, kvp => (uint)kvp.Value);

        /// <summary>
        /// The Hammer wcid per material, projected straight off <see cref="SalvageForge.MaterialTable"/>.
        /// A literal copy of the five would be a second place to update and a second place to be wrong;
        /// this way a new Hammer in the forge is orderable the moment it exists there.
        /// </summary>
        private static readonly Dictionary<int, uint> hammerWcidByMaterial = SalvageForge.MaterialTable
            .ToDictionary(kvp => (int)kvp.Key, kvp => kvp.Value.HammerWcid);

        /// <summary>Weenie-derived fields, cached on the weenie INSTANCE like MarketManager.LedgerSnapshot so a content reload invalidates them.</summary>
        private static readonly ConcurrentDictionary<(int Material, MarketBuyOrderKind Kind), (Weenie Source, MaterialEntry Entry)> cache
            = new ConcurrentDictionary<(int, MarketBuyOrderKind), (Weenie, MaterialEntry)>();

        /// <summary>TRUE when a multi-charge Hammer exists in this material. Read by the /v1/materials projection and by placement.</summary>
        public static bool HasHammer(int materialType) => hammerWcidByMaterial.ContainsKey(materialType);

        /// <summary>
        /// The bag overload, kept because most callers are bag-only and reading
        /// <c>TryGet(m, MarketBuyOrderKind.SalvageBag, out e)</c> everywhere buys nothing.
        /// </summary>
        public static bool TryGet(int materialType, out MaterialEntry entry)
            => TryGet(materialType, MarketBuyOrderKind.SalvageBag, out entry);

        /// <summary>
        /// The kind-aware lookup. FALSE for a material the market knows no salvage of at ALL, and - for
        /// <see cref="MarketBuyOrderKind.SalvageHammer"/> - also false for a material that has salvage
        /// but no Hammer. Placement tells those two apart before refusing, so the caller is given
        /// no_hammer_for_material rather than invalid_material; see MarketManager.PlaceOrder.
        /// </summary>
        public static bool TryGet(int materialType, MarketBuyOrderKind kind, out MaterialEntry entry)
        {
            entry = null;

            if (!wcidByMaterial.ContainsKey(materialType))
                return false;

            if (kind == MarketBuyOrderKind.SalvageHammer)
            {
                if (!hammerWcidByMaterial.TryGetValue(materialType, out var hammerWcid))
                    return false;

                entry = Describe(materialType, kind, hammerWcid);
                return true;
            }

            entry = Describe(materialType, MarketBuyOrderKind.SalvageBag, wcidByMaterial[materialType]);
            return true;
        }

        /// <summary>Every orderable BAG material, sorted by name. Each entry carries HasHammer.</summary>
        public static IReadOnlyList<MaterialEntry> All()
            => wcidByMaterial.Select(kvp => Describe(kvp.Key, MarketBuyOrderKind.SalvageBag, kvp.Value))
                             .OrderBy(e => e.MaterialName, StringComparer.Ordinal)
                             .ToList();

        /// <summary>
        /// Every material a Hammer exists in, sorted by name - the five of
        /// <see cref="SalvageForge.MaterialTable"/>, each carrying its HAMMER wcid, name and icons.
        ///
        /// NO PRODUCTION CALLER, and that is intentional rather than an oversight waiting to be
        /// tidied away. The wire carries the hammer set as a per-material <c>has_hammer</c> flag on
        /// GET /v1/materials, never as a second list (see <see cref="All"/>), so this exists as the
        /// test-side and diagnostic view of that set: it is what lets a test assert the hammer
        /// entries resolve the right WCID, which <c>All().Where(m =&gt; m.HasHammer)</c> cannot show
        /// because those entries describe the bag. Delete it only together with that assertion.
        /// </summary>
        public static IReadOnlyList<MaterialEntry> AllHammers()
            => hammerWcidByMaterial.Where(kvp => wcidByMaterial.ContainsKey(kvp.Key))
                                   .Select(kvp => Describe(kvp.Key, MarketBuyOrderKind.SalvageHammer, kvp.Value))
                                   .OrderBy(e => e.MaterialName, StringComparer.Ordinal)
                                   .ToList();

        /// <summary>
        /// The full-bag test, delegated to <see cref="SalvageForge.IsEligibleBag"/> rather than
        /// re-implemented here: that is the one rule the salvage forge's own CountFullBags and
        /// VerifyUseRequirements are both built on ("so the two can never drift apart" -
        /// SalvageForge.cs), and the market must not diverge from it either.
        ///
        /// It is a PREDICATE and nothing more. Its only caller in the server is the order fill path
        /// in MarketManager_Orders, which hands it to IMarketItemStore's CountMatching and
        /// TryTakeMatching; the item store applies whatever predicate it is given and knows nothing
        /// about salvage, and no web route calls this at all.
        /// </summary>
        public static bool IsFullBagOf(WorldObject wo, int materialType)
            => wo != null && SalvageForge.IsEligibleBag(wo, (MaterialType)materialType);

        /// <summary>
        /// The full-CHARGE Hammer test, delegated to <see cref="SalvageForge.IsEligibleHammer"/> for
        /// the same reason <see cref="IsFullBagOf"/> delegates to IsEligibleBag. A Hammer at 7 of 10
        /// charges is refused - see IsEligibleHammer for why the Wanted market requires a full one.
        /// </summary>
        public static bool IsFullHammerOf(WorldObject wo, int materialType)
            => wo != null && SalvageForge.IsEligibleHammer(wo, (MaterialType)materialType);

        /// <summary>
        /// THE ONE PLACE a kind becomes a predicate. Every matching decision in the fill path routes
        /// through here rather than switching on the kind itself, because the two predicates overlap on
        /// everything except the salvage-tool clause: a Hammer and its bag share an ItemType and a
        /// MaterialType, so a site that forgot the kind would match both and hand a buyer paying bag
        /// prices a Hammer worth ten bags.
        ///
        /// An unrecognised kind matches NOTHING. A future kind that reached a fill before its predicate
        /// did must refuse every item rather than fall through to the bag rule.
        /// </summary>
        public static bool MatchesOrder(WorldObject wo, int materialType, MarketBuyOrderKind kind)
        {
            switch (kind)
            {
                case MarketBuyOrderKind.SalvageBag:    return IsFullBagOf(wo, materialType);
                case MarketBuyOrderKind.SalvageHammer: return IsFullHammerOf(wo, materialType);
                default:                               return false;
            }
        }

        private static MaterialEntry Describe(int materialType, MarketBuyOrderKind kind, uint wcid)
        {
            var name = ((MaterialType)materialType).ToString();
            var hasHammer = hammerWcidByMaterial.ContainsKey(materialType);
            var weenie = MarketManager.WeenieLookup?.Invoke(wcid);

            // A failed lookup is never cached: a transient world read must not pin "Item wcid" forever.
            if (weenie == null)
                return new MaterialEntry { MaterialType = materialType, MaterialName = name, Kind = kind, Wcid = wcid, BagName = $"Item {wcid}", HasHammer = hasHammer };

            if (cache.TryGetValue((materialType, kind), out var cached) && ReferenceEquals(cached.Source, weenie))
                return cached.Entry;

            var snapshot = MarketSnapshot.FromWeenie(weenie);
            var entry = new MaterialEntry
            {
                MaterialType = materialType, MaterialName = name, Kind = kind, Wcid = wcid,
                BagName = string.IsNullOrEmpty(snapshot.Name) ? $"Item {wcid}" : snapshot.Name,
                IconId = snapshot.IconId, IconOverlayId = snapshot.IconOverlayId, IconUnderlayId = snapshot.IconUnderlayId,
                HasHammer = hasHammer,
            };
            cache[(materialType, kind)] = (weenie, entry);
            return entry;
        }
    }
}
