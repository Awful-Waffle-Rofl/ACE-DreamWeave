using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Knots;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A peddler row's parsed config (encounters.json "config", Docs/Threads/ENCOUNTERS.md "Wandering Peddler"). Every key
    /// but <see cref="Prices"/> and <see cref="Cheap"/> has a default; those two are required.
    /// </summary>
    public sealed class PeddlerConfig
    {
        public const uint DefaultNpcWcid = 1007981;
        public const uint DefaultCurrencyWcid = 1007982;
        public const uint DefaultBundleWcid = 1007983;
        public const double DefaultIdleSeconds = 120;
        public const double DefaultWalkAwaySeconds = 5;

        public const double MinIdleSeconds = 10;
        public const double MaxIdleSeconds = 600;
        public const double MinWalkAwaySeconds = 1;
        public const double MaxWalkAwaySeconds = 60;
        public const int MaxOffers = 9;
        public const double MaxPriceMultiplier = 10;
        public const double MaxItemJitter = 0.5;
        public const int MaxPrice = 100000;

        public uint NpcWcid { get; init; } = DefaultNpcWcid;
        public uint CurrencyWcid { get; init; } = DefaultCurrencyWcid;
        public uint BundleWcid { get; init; } = DefaultBundleWcid;
        /// <summary>A member who never Uses the peddler, or stands by it without buying, is done after this many seconds.</summary>
        public double IdleSeconds { get; init; } = DefaultIdleSeconds;

        /// <summary>A member who has Used the peddler and stays out of its use radius this long is done (the stand-in for closing the panel).</summary>
        public double WalkAwaySeconds { get; init; } = DefaultWalkAwaySeconds;
        public int OffersMin { get; init; } = 3;
        public int OffersMax { get; init; } = 5;
        public double PriceMultiplierMin { get; init; } = 0.85;
        public double PriceMultiplierMax { get; init; } = 1.6;
        public double ItemJitter { get; init; } = 0.10;

        /// <summary>Base price per prize-entry id, in peddler units (Thread Cache items).</summary>
        public IReadOnlyDictionary<string, int> Prices { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The entry ids that count as a cheap offer (at least one is always on the stall).</summary>
        public IReadOnlyCollection<string> Cheap { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The entry ids a member may buy at most ONE of per run (owner ruling 2026-11-20), counted by entry id, so two
        /// Patterns for different pinnacles are two of "pattern". Optional; empty when the key is absent. Enforced by
        /// ThreadDungeonRun.TryChargePeddlerPurchase, and a capped listing is hidden from a member who already bought it.
        /// </summary>
        public IReadOnlySet<string> OnePerPlayer { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Percent-of-pile pricing (owner ruling 2026-11-20): per priced entry id, the share of the buyer's pile basis an
        /// offer costs, (0, 100]. Optional; empty when the key is absent, which is flat pricing exactly as before. The flat
        /// price stays the FLOOR: see <see cref="PeddlerEncounterType.MemberPrice"/>.
        /// </summary>
        public IReadOnlyDictionary<string, double> PricePercent { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>The smallest and largest price any offer can be given: round(base x mult x (1 -/+ jitter)), floor 1. Pure.</summary>
        public (int Min, int Max) ReachablePriceRange()
        {
            if (Prices.Count == 0)
                return (1, 1);

            var min = Prices.Values.Min(p => PeddlerEncounterType.FinalPrice(p, PriceMultiplierMin, -ItemJitter));
            var max = Prices.Values.Max(p => PeddlerEncounterType.FinalPrice(p, PriceMultiplierMax, ItemJitter));
            return (min, max);
        }
    }

    /// <summary>One offer on the stall: what the prize roll drew (the pick, which fixes a Pattern's pinnacle), and its final price.</summary>
    public sealed class PeddlerOffer
    {
        public PeddlerOffer(ThreadEncounterPrizePick pick, int price, bool cheap, double jitterFactor = 1.0)
        {
            Pick = pick;
            Price = price;
            Cheap = cheap;
            JitterFactor = jitterFactor;
        }

        public ThreadEncounterPrizePick Pick { get; }
        public string EntryId => Pick.Entry.Id;
        /// <summary>The flat price: round(base x spawnMult x JitterFactor), at least 1. The floor of every member's price.</summary>
        public int Price { get; }
        public bool Cheap { get; }

        /// <summary>1 + this offer's rolled per-item jitter: the same factor the flat price used, applied to the percent term.</summary>
        public double JitterFactor { get; }

        /// <summary>The purchase record a buy of this offer commits (amount N commits N of these).</summary>
        public PeddlerPurchase Template => new PeddlerPurchase(Pick.Entry.Id, Price, Pick.Pattern);
    }

    /// <summary>
    /// The Wandering Peddler (Docs/Threads/ENCOUNTERS.md "Wandering Peddler"). One NPC, a <see cref="ThreadPeddlerVendor"/>,
    /// whose native vendor window sells 3 to 5 offers drawn from the shared encounter prize table, priced in Thread Cache
    /// items. Clear only (owner rulings 2026-11-19): Place only records that the run HAS a peddler and rolls its stall; the NPC
    /// is created at the clear beside the first seated member who can afford something, and each member shops with their
    /// real pile (ThreadPeddlerShop). Never placed in a run without pooled loot, because the currency is the buyer's own pile.
    ///
    /// The owner accepted a Vendor subclass for this type (2026-11-16): the exception to the framework's "never subclass
    /// WorldObject" rule (IThreadEncounterType). The type does NOT implement IThreadEncounterUsable: the vendor's own
    /// ActOnUse handles a Use, once.
    ///
    /// Threading. Place runs on the point's landblock queue (the framework's rule) and creates nothing. <see cref="CreateNpcAt"/>
    /// runs on the delivery chain's landblock queue. OnRunEnded releases memory only (the peddler stops trading); EndRun
    /// destroys the copy, and with it the NPC and every display.
    /// </summary>
    public sealed class PeddlerEncounterType : IThreadEncounterType
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindName = "peddler";

        /// <summary>The bundle shell's description, for every bundled listing.</summary>
        public const string BundleDescription = "Sold by the Wandering Peddler. Goes straight into your pack when you buy it.";

        public string Kind => KindName;

        /// <summary>Test seam: the object factory. Production is WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>Test seam: a weenie by wcid (a world-cache read; the display copies a real item's name and icon from it).</summary>
        internal static Func<uint, Weenie> WeenieLookup = wcid => DatabaseManager.World.GetCachedWeenie(wcid);

        /// <summary>Test seam: the Knots inputs to the stock roll (the encounter prize's own gate, ThreadEncounterPrize.KnotsInputs).</summary>
        internal static Func<ThreadDungeonRun, (int? FiberLevel, IReadOnlyList<PinnacleDef> PatternPool)> KnotsInputs = ThreadEncounterPrize.KnotsInputs;

        /// <summary>Test seam: the prize table. Production: the live Thread store's.</summary>
        internal static Func<ThreadEncounterPrizeTable> PrizeTable = () => ThreadDungeonManager.Store?.Encounters?.Prize ?? ThreadEncounterPrizeTable.Empty;

        /// <summary>Test seam: does a weenie exist (the prize roll's own check).</summary>
        internal static Func<uint, bool> WeenieExists = wcid => ThreadEncounterPrize.WeenieExists(wcid);

        /// <summary>Test seam: WorldObject.EnterWorld.</summary>
        internal static Func<WorldObject, bool> EnterWorld = wo => wo.EnterWorld();

        // ---- config ------------------------------------------------------------------------------------------------

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "npcWcid", "currencyWcid", "bundleWcid", "idleSeconds", "walkAwaySeconds", "offers", "priceMultiplier", "itemJitter", "prices", "cheap", "onePerPlayer", "pricePercent",
        };

        /// <summary>
        /// Parses a peddler row's config and checks it against <paramref name="prize"/>. Null with <paramref name="error"/>
        /// for: a config that is not an object, an unknown key, a bad value, a price for an id the prize table does not
        /// have, a drawable prize entry (weight above 0) with no price, an empty or unpriced cheap list, an onePerPlayer list that is not an array of priced entry ids, or a pricePercent that is not an object of
        /// { pricedEntryId: percent } with each percent in (0, 100]. A config error means
        /// the peddler is not placed. Pure.
        /// </summary>
        public static PeddlerConfig ParseConfig(JsonElement config, ThreadEncounterPrizeTable prize, out string error)
        {
            error = null;

            if (config.ValueKind != JsonValueKind.Object)
            {
                error = config.ValueKind == JsonValueKind.Undefined || config.ValueKind == JsonValueKind.Null
                    ? "config is required (prices and cheap have no default)"
                    : $"config must be an object, not {config.ValueKind}";
                return null;
            }

            foreach (var property in config.EnumerateObject())
            {
                if (!KnownKeys.Contains(property.Name))
                {
                    error = $"config.{property.Name} is not a peddler key";
                    return null;
                }
            }

            if (!TryReadWcid(config, "npcWcid", PeddlerConfig.DefaultNpcWcid, out var npc, ref error)
                || !TryReadWcid(config, "currencyWcid", PeddlerConfig.DefaultCurrencyWcid, out var currency, ref error)
                || !TryReadWcid(config, "bundleWcid", PeddlerConfig.DefaultBundleWcid, out var bundle, ref error))
                return null;

            if (!TryReadSeconds(config, "idleSeconds", PeddlerConfig.DefaultIdleSeconds, PeddlerConfig.MinIdleSeconds, PeddlerConfig.MaxIdleSeconds, out var idle, ref error)
                || !TryReadSeconds(config, "walkAwaySeconds", PeddlerConfig.DefaultWalkAwaySeconds, PeddlerConfig.MinWalkAwaySeconds, PeddlerConfig.MaxWalkAwaySeconds, out var walkAway, ref error))
                return null;

            int offersMin = 3, offersMax = 5;

            if (config.TryGetProperty("offers", out var offers))
            {
                if (!TryReadRange(offers, out var lo, out var hi) || lo != Math.Floor(lo) || hi != Math.Floor(hi)
                    || lo < 1 || hi < lo || hi > PeddlerConfig.MaxOffers)
                {
                    error = $"config.offers must be {{ min, max }} whole numbers with 1 <= min <= max <= {PeddlerConfig.MaxOffers}";
                    return null;
                }

                offersMin = (int)lo;
                offersMax = (int)hi;
            }

            double multMin = 0.85, multMax = 1.6;

            if (config.TryGetProperty("priceMultiplier", out var mult))
            {
                if (!TryReadRange(mult, out multMin, out multMax) || !(multMin > 0) || multMax < multMin || multMax > PeddlerConfig.MaxPriceMultiplier)
                {
                    error = $"config.priceMultiplier must be {{ min, max }} with 0 < min <= max <= {PeddlerConfig.MaxPriceMultiplier}";
                    return null;
                }
            }

            var jitter = 0.10;

            if (config.TryGetProperty("itemJitter", out var j)
                && (j.ValueKind != JsonValueKind.Number || !j.TryGetDouble(out jitter) || !double.IsFinite(jitter) || jitter < 0 || jitter > PeddlerConfig.MaxItemJitter))
            {
                error = $"config.itemJitter must be a number from 0 to {PeddlerConfig.MaxItemJitter}";
                return null;
            }

            if (!config.TryGetProperty("prices", out var pricesElement) || pricesElement.ValueKind != JsonValueKind.Object)
            {
                error = "config.prices must be an object of { entryId: price }";
                return null;
            }

            var prices = new Dictionary<string, int>(StringComparer.Ordinal);
            var entries = (prize ?? ThreadEncounterPrizeTable.Empty).Entries;
            var entryIds = new HashSet<string>(entries.Select(e => e.Id), StringComparer.Ordinal);

            foreach (var p in pricesElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var price) || price < 1 || price > PeddlerConfig.MaxPrice)
                {
                    error = $"config.prices.{p.Name} must be a whole number from 1 to {PeddlerConfig.MaxPrice}";
                    return null;
                }

                if (!entryIds.Contains(p.Name))
                {
                    error = $"config.prices.{p.Name} names no prize entry";
                    return null;
                }

                // A multi-wcid item entry picks its wcid per roll, so the listing shown at Place and the item built at the
                // clear could differ. Only a salvage entry (bundled, materials drawn at delivery) may list several.
                var priced = entries.First(e => string.Equals(e.Id, p.Name, StringComparison.Ordinal));

                if (!priced.IsSalvage && priced.Wcids.Count > 1)
                {
                    error = $"config.prices.{p.Name}: prize entry {p.Name} lists {priced.Wcids.Count} wcids; the peddler sells only single-wcid item entries (or salvage)";
                    return null;
                }

                prices[p.Name] = price;
            }

            var unpriced = entries.Where(e => e.Weight > 0 && !prices.ContainsKey(e.Id)).Select(e => e.Id).ToList();

            if (unpriced.Count > 0)
            {
                error = $"config.prices has no price for prize entr{(unpriced.Count == 1 ? "y" : "ies")} {string.Join(", ", unpriced)}";
                return null;
            }

            if (!config.TryGetProperty("cheap", out var cheapElement) || cheapElement.ValueKind != JsonValueKind.Array)
            {
                error = "config.cheap must be an array of entry ids";
                return null;
            }

            var cheap = new List<string>();

            foreach (var c in cheapElement.EnumerateArray())
            {
                var id = c.ValueKind == JsonValueKind.String ? c.GetString() : null;

                if (id == null || !prices.ContainsKey(id))
                {
                    error = $"config.cheap entry {(id ?? c.ValueKind.ToString())} must be a priced entry id";
                    return null;
                }

                if (!cheap.Contains(id))
                    cheap.Add(id);
            }

            if (cheap.Count == 0)
            {
                error = "config.cheap must name at least one entry";
                return null;
            }

            var onePerPlayer = new HashSet<string>(StringComparer.Ordinal);

            if (config.TryGetProperty("onePerPlayer", out var capElement))
            {
                if (capElement.ValueKind != JsonValueKind.Array)
                {
                    error = "config.onePerPlayer must be an array of entry ids";
                    return null;
                }

                foreach (var c in capElement.EnumerateArray())
                {
                    var id = c.ValueKind == JsonValueKind.String ? c.GetString() : null;

                    if (id == null || !prices.ContainsKey(id))
                    {
                        error = $"config.onePerPlayer entry {(id ?? c.ValueKind.ToString())} must be a priced entry id";
                        return null;
                    }

                    onePerPlayer.Add(id);
                }
            }

            var pricePercent = new Dictionary<string, double>(StringComparer.Ordinal);

            if (config.TryGetProperty("pricePercent", out var pctElement))
            {
                if (pctElement.ValueKind != JsonValueKind.Object)
                {
                    error = "config.pricePercent must be an object of { entryId: percent }";
                    return null;
                }

                foreach (var p in pctElement.EnumerateObject())
                {
                    if (!entryIds.Contains(p.Name))
                    {
                        error = $"config.pricePercent.{p.Name} names no prize entry";
                        return null;
                    }

                    if (!prices.ContainsKey(p.Name))
                    {
                        error = $"config.pricePercent.{p.Name} has no flat price in config.prices (the floor)";
                        return null;
                    }

                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var pct) || !double.IsFinite(pct) || !(pct > 0) || pct > 100)
                    {
                        error = $"config.pricePercent.{p.Name} must be a number above 0 and at most 100";
                        return null;
                    }

                    pricePercent[p.Name] = pct;
                }
            }

            return new PeddlerConfig
            {
                NpcWcid = npc,
                CurrencyWcid = currency,
                BundleWcid = bundle,
                IdleSeconds = idle,
                WalkAwaySeconds = walkAway,
                OffersMin = offersMin,
                OffersMax = offersMax,
                PriceMultiplierMin = multMin,
                PriceMultiplierMax = multMax,
                ItemJitter = jitter,
                Prices = prices,
                Cheap = cheap,
                OnePerPlayer = onePerPlayer,
                PricePercent = pricePercent,
            };
        }

        private static bool TryReadWcid(JsonElement config, string name, uint fallback, out uint value, ref string error)
        {
            value = fallback;

            if (!config.TryGetProperty(name, out var e))
                return true;

            if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out value) && value != 0)
                return true;

            error = $"config.{name} must be a positive whole number";
            return false;
        }

        private static bool TryReadSeconds(JsonElement config, string name, double fallback, double min, double max, out double value, ref string error)
        {
            value = fallback;

            if (!config.TryGetProperty(name, out var e))
                return true;

            if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value) && double.IsFinite(value) && value >= min && value <= max)
                return true;

            error = $"config.{name} must be a number from {min} to {max}";
            return false;
        }

        private static bool TryReadRange(JsonElement e, out double min, out double max)
        {
            min = max = 0;

            if (e.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var p in e.EnumerateObject())
            {
                if (p.Name != "min" && p.Name != "max")
                    return false;
            }

            return e.TryGetProperty("min", out var lo) && lo.ValueKind == JsonValueKind.Number && lo.TryGetDouble(out min) && double.IsFinite(min)
                && e.TryGetProperty("max", out var hi) && hi.ValueKind == JsonValueKind.Number && hi.TryGetDouble(out max) && double.IsFinite(max);
        }

        // ---- stock (pure) ------------------------------------------------------------------------------------------

        /// <summary>
        /// One member's price for one offer (owner ruling 2026-11-20, revised): max(flat, min(MaxPrice, ceil(pct / 100 x
        /// jitterFactor x basis))), where flat is the offer's <see cref="FinalPrice"/> (which carries the per-spawn
        /// multiplier), jitterFactor the offer's own 1 + jitter, and basis the member's pile snapshot at shop open. The
        /// percent term deliberately carries the per-item jitter only, NOT the spawn multiplier, so a high percent never
        /// costs more than the pile. Without a percent (null, or not above 0) or with a basis of 0 or less this is exactly
        /// the flat price. Only the percent term is clamped, so the flat price is never changed. Pure.
        /// </summary>
        public static int MemberPrice(int flat, double? percent, double jitterFactor, long basis)
        {
            if (percent == null || !(percent.Value > 0) || basis <= 0)
                return flat;

            // The tiny tolerance keeps ceil from rounding up an exact product that floating point lands a hair above:
            // 6.25% of 800 is 50, never 51.
            var raw = percent.Value / 100.0 * jitterFactor * basis;
            var term = raw >= PeddlerConfig.MaxPrice ? PeddlerConfig.MaxPrice : (int)Math.Ceiling(raw - 1e-9);
            return Math.Max(flat, Math.Min(PeddlerConfig.MaxPrice, term));
        }

        /// <summary>round(basePrice x mult x (1 + jitter)), half away from zero, floor 1. Pure.</summary>
        public static int FinalPrice(int basePrice, double mult, double jitter)
            => Math.Max(1, (int)Math.Round(basePrice * mult * (1 + jitter), MidpointRounding.AwayFromZero));

        /// <summary>
        /// The stall, pure over <paramref name="rng"/>. One rng.Next draws the offer count in [OffersMin, OffersMax]. Then
        /// each slot is one ThreadEncounterPrize.Roll over the PRICED entries not yet on the stall (so a weight-0 entry, an
        /// entry with no weenie, and a Fiber or Pattern behind a closed Knots gate never draw, exactly as for the prize);
        /// the stall stops early when nothing is left. When no drawn offer is cheap, the LAST slot is redrawn from the
        /// cheap entries not on the stall (the Roll again, so their weights hold). Then one rng.NextDouble draws the spawn
        /// multiplier, uniform in [PriceMultiplierMin, PriceMultiplierMax], and one more per offer, in stall order, draws
        /// its jitter, uniform in [-ItemJitter, ItemJitter]; the price is <see cref="FinalPrice"/>. Empty when nothing can
        /// be drawn.
        /// </summary>
        public static List<PeddlerOffer> BuildStock(PeddlerConfig config, IReadOnlyList<ThreadEncounterPrizeEntry> entries, Func<uint, bool> exists, Random rng,
            int? fiberLevel, IReadOnlyList<PinnacleDef> patternPool, out double spawnMultiplier)
        {
            spawnMultiplier = 1.0;
            var stall = new List<ThreadEncounterPrizePick>();

            if (config == null || entries == null || exists == null || rng == null)
                return new List<PeddlerOffer>();

            var priced = entries.Where(e => e != null && config.Prices.ContainsKey(e.Id)).ToList();
            var count = rng.Next(config.OffersMin, config.OffersMax + 1);

            for (var i = 0; i < count; i++)
            {
                var remaining = priced.Where(e => !stall.Any(s => s.Entry.Id == e.Id)).ToList();
                var pick = ThreadEncounterPrize.Roll(remaining, exists, rng, null, fiberLevel, patternPool);

                if (pick == null)
                    break;

                stall.Add(pick);
            }

            if (stall.Count > 0 && !stall.Any(s => config.Cheap.Contains(s.Entry.Id)))
            {
                var cheapLeft = priced.Where(e => config.Cheap.Contains(e.Id) && !stall.Any(s => s.Entry.Id == e.Id)).ToList();
                var redraw = ThreadEncounterPrize.Roll(cheapLeft, exists, rng, null, fiberLevel, patternPool);

                if (redraw != null)
                    stall[stall.Count - 1] = redraw;
            }

            if (stall.Count == 0)
                return new List<PeddlerOffer>();

            spawnMultiplier = config.PriceMultiplierMin + rng.NextDouble() * (config.PriceMultiplierMax - config.PriceMultiplierMin);

            var result = new List<PeddlerOffer>();

            foreach (var pick in stall)
            {
                var jitter = (rng.NextDouble() * 2 - 1) * config.ItemJitter;
                result.Add(new PeddlerOffer(pick, FinalPrice(config.Prices[pick.Entry.Id], spawnMultiplier, jitter), config.Cheap.Contains(pick.Entry.Id), 1 + jitter));
            }

            return result;
        }

        // ---- displays --------------------------------------------------------------------------------------------

        /// <summary>
        /// Must this offer be shown as a bundle shell rather than the real item? Yes for a salvage entry, any quantity
        /// above 1 (the client prices unit Value x quantity, so a stack cannot be one listing), and any item whose
        /// weenie's ItemType carries PromissoryNote or Money: Vendor.CalcSellCost forces a PromissoryNote-typed object to
        /// a 1.15 rate (so the shown and charged prices would part), and a currency object in a vendor list reads as change.
        /// A Fiber or Pattern is always the real Knots object. Pure over <paramref name="weenie"/>.
        /// </summary>
        internal static bool NeedsShell(ThreadEncounterPrizePick pick, Weenie weenie)
        {
            var entry = pick.Entry;

            if (entry.IsFiber || entry.IsPattern)
                return false;

            if (entry.IsSalvage || entry.Qty > 1)
                return true;

            var type = weenie?.GetItemType() ?? ItemType.None;
            return (type & (ItemType.PromissoryNote | ItemType.Money)) != 0;
        }

        /// <summary>
        /// A bundle's name: "Salvage Bundle (2-4 bags)" for a salvage entry ("(3 bags)" when min equals max, "(1 bag)");
        /// otherwise the real item's name, with " xN" when the quantity is above 1: "Trade Note (250,000) x5". Pure.
        /// </summary>
        internal static string BundleName(ThreadEncounterPrizeEntry entry, string itemName)
        {
            if (entry.IsSalvage)
            {
                var s = entry.Salvage;
                var range = s.BagsMin == s.BagsMax ? s.BagsMin.ToString(CultureInfo.InvariantCulture) : $"{s.BagsMin.ToString(CultureInfo.InvariantCulture)}-{s.BagsMax.ToString(CultureInfo.InvariantCulture)}";
                return $"Salvage Bundle ({range} {(s.BagsMax == 1 ? "bag" : "bags")})";
            }

            var name = string.IsNullOrEmpty(itemName) ? entry.Id : itemName;
            return entry.Qty > 1 ? $"{name} x{entry.Qty.ToString(CultureInfo.InvariantCulture)}" : name;
        }

        /// <summary>
        /// The display object one offer is listed as, unplaced, or null when it cannot be built (logged). A Pattern is the
        /// real Pattern (KnotDrops.PatternFactory, for the pinnacle the roll fixed), a Fiber the real Fiber at the run's
        /// level (KnotDrops.FiberFactory), a single plain item the real item; anything <see cref="NeedsShell"/> is the
        /// bundle shell (<paramref name="config"/>.BundleWcid) carrying the real item's name, icon and the bundle
        /// description. Every display's Value (and StackUnitValue, when it has one) is <paramref name="price"/> when given (one
        /// member's own price, <see cref="BuildMemberListings"/>), else the offer's flat price, so the panel (SellPrice 1.0 x
        /// Value) shows exactly what a buy commits.
        /// </summary>
        internal static WorldObject BuildDisplay(PeddlerOffer offer, PeddlerConfig config, int? price = null)
        {
            var pick = offer.Pick;
            var entry = pick.Entry;
            var weenie = entry.IsFiber || entry.IsPattern ? null : WeenieLookup(pick.Wcid);
            WorldObject display;

            if (entry.IsPattern)
                display = KnotDrops.PatternFactory(pick.Pattern);
            else if (entry.IsFiber)
                display = KnotDrops.FiberFactory(pick.FiberLevel);
            else if (NeedsShell(pick, weenie))
            {
                display = CreateObject(config.BundleWcid);

                if (display != null)
                {
                    display.Name = BundleName(entry, weenie?.GetProperty(PropertyString.Name));
                    display.LongDesc = BundleDescription;

                    var icon = weenie?.GetProperty(PropertyDataId.Icon);

                    if (icon != null)
                        display.IconId = icon.Value;

                    display.IconOverlayId = weenie?.GetProperty(PropertyDataId.IconOverlay);
                    display.IconUnderlayId = weenie?.GetProperty(PropertyDataId.IconUnderlay);
                }
            }
            else
                display = CreateObject(pick.Wcid);

            if (display == null)
            {
                log.Warn($"[DYNDUNGEON] peddler: offer {entry.Id} did not build a display; not listed");
                return null;
            }

            // A real object that still turned out PromissoryNote-typed would be charged at 1.15 by any retail pricing: never list it.
            if (!NeedsShell(pick, weenie) && (display.ItemType & ItemType.PromissoryNote) != 0)
            {
                log.Error($"[DYNDUNGEON] peddler: offer {entry.Id} built a PromissoryNote-typed display (wcid {display.WeenieClassId}); not listed");
                try { display.Destroy(); } catch { }
                return null;
            }

            var shown = price ?? offer.Price;
            display.Value = shown;

            if (display.StackUnitValue != null)
                display.StackUnitValue = shown;

            return display;
        }

        // ---- IThreadEncounterType -------------------------------------------------------------------------------

        /// <summary>Pooled loot off: the type is never drawn, because the currency is the buyer's own Thread Cache pile.</summary>
        public string RejectRun(ThreadEncounterTypeDef def, ThreadDungeonRun run)
            => run != null && !run.PooledLoot ? "pooled loot is off (the peddler trades a share of the Thread Cache)" : null;

        /// <summary>
        /// Records that this run HAS a peddler, and rolls its stall (owner ruling 2026-11-19, clear only). Nothing is created:
        /// the NPC appears at the clear, beside the first seated member who can afford something (ThreadPeddlerShop.Present
        /// -> <see cref="CreateNpcAt"/>). The run's one-peddler claim is taken before the plan is recorded and kept for the
        /// rest of the run once it is; it is released only when no plan was recorded.
        /// </summary>
        public bool Place(ThreadEncounterPlaceRequest request, out string failure)
        {
            failure = null;
            var run = request.Run;

            // /dd encounter place skips RejectRun; the rule holds here too.
            var rejected = RejectRun(request.Def, run);

            if (rejected != null)
            {
                failure = rejected;
                return false;
            }

            // The plan is recorded only before the clear; a run already Cleared or Ended gets no peddler.
            if (run.State != ThreadDungeonRunState.Active)
            {
                failure = $"the run is not Active ({run.State})";
                return false;
            }

            var prize = PrizeTable();
            var config = ParseConfig(request.Def?.Config ?? default, prize, out var error);

            if (config == null)
            {
                failure = error;
                return false;
            }

            var (fiberLevel, patternPool) = KnotsInputs(run);
            var offers = BuildStock(config, prize.Entries, WeenieExists, new Random(unchecked(request.Seed * 31 + 0x7EDD)), fiberLevel, patternPool, out var spawnMult);

            if (offers.Count == 0)
            {
                failure = "the prize table has nothing the peddler can sell";
                return false;
            }

            // Owner rule: one peddler per run. Claimed here, before anything is recorded, and released only when no plan was.
            if (!run.TryClaimPeddler())
            {
                failure = "the run already had its peddler";
                return false;
            }

            var recorded = false;

            try
            {
                var plan = new PeddlerPlan(request.Instance, config, offers, spawnMult);

                if (!run.TrySetPeddlerPlan(plan))
                {
                    failure = $"the run did not take the plan (it is {run.State})";
                    return false;
                }

                recorded = true;
                request.Instance.State = plan;

                run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: peddler for the clear with {1} offer(s) at x{2:0.00}: {3}",
                    request.Instance, offers.Count, spawnMult, string.Join(", ", offers.Select(o => $"{o.EntryId} {o.Price}"))));
                return true;
            }
            finally
            {
                // A plan, once recorded, holds the claim for the rest of the run (it is what "this run has its peddler" means).
                if (!recorded)
                    run.ReleasePeddlerClaim();
            }
        }

        /// <summary>
        /// Builds, binds, tracks and enters the run's one NPC at <paramref name="position"/>. Runs on the delivery chain's
        /// landblock queue (ThreadPeddlerShop.Present), as the Thread Caches it stands beside do. Null, leaving nothing in the
        /// world, when the weenie is wrong, no offer builds a display, or EnterWorld fails. Never throws.
        /// </summary>
        internal static ThreadPeddlerVendor CreateNpcAt(ThreadDungeonRun run, PeddlerPlan plan, Position position, out string failure)
        {
            failure = null;
            ThreadPeddlerVendor peddler = null;

            try
            {
                var config = plan.Config;
                var created = CreateObject(config.NpcWcid);
                peddler = created as ThreadPeddlerVendor;

                if (peddler == null)
                {
                    try { created?.Destroy(); } catch { }
                    failure = $"wcid {config.NpcWcid} did not create a ThreadPeddlerVendor - is its weenie in the world database, with bool 9096?";
                    return null;
                }

                if (!peddler.OpenForBusiness || peddler.AlternateCurrency != config.CurrencyWcid)
                {
                    failure = $"wcid {config.NpcWcid} is not open for business or its AlternateCurrency ({peddler.AlternateCurrency}) is not config.currencyWcid {config.CurrencyWcid}";
                    DestroyQuietly(peddler);
                    return null;
                }

                peddler.Location = new Position(position);
                peddler.TimeToRot = -1;
                peddler.IsTransientSpawn = true;

                var listed = new List<(WorldObject Display, PeddlerPurchase Purchase)>();

                foreach (var offer in plan.Offers)
                {
                    var display = BuildDisplay(offer, config);

                    if (display != null)
                        listed.Add((display, offer.Template));
                }

                var bound = peddler.Bind(run, config.IdleSeconds, config.WalkAwaySeconds, listed);

                // Percent pricing: each member is shown and charged their own price, so each gets their own displays at Arm.
                if (plan.HasPercentPricing)
                    peddler.MemberListings = member => BuildMemberListings(run, plan, member);

                // A display Bind refused is one this method just built and nobody else holds: destroyed, so no guid leaks.
                foreach (var (display, _) in listed)
                {
                    if (!display.IsDestroyed && !peddler.DefaultItemsForSale.ContainsKey(display.Guid) && display.ContainerId == null && display.WielderId == null
                        && !display.BiotaOriginatedFromOrHasBeenSavedToDatabase())
                    {
                        try { display.Destroy(); } catch { }
                    }
                }

                if (bound == 0)
                {
                    failure = "no offer built a display";
                    DestroyQuietly(peddler);
                    return null;
                }

                // Before EnterWorld: a use can arrive the moment it is visible.
                plan.Instance?.Track(peddler);

                bool entered;

                try
                {
                    entered = EnterWorld(peddler);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} peddler EnterWorld threw at {peddler.Location?.ToLOCString()}", ex);
                    entered = false;
                }

                if (!entered)
                {
                    failure = $"wcid {config.NpcWcid} failed to enter the world at {peddler.Location?.ToLOCString()}";
                    peddler.P_ThreadEncounter = null;
                    DestroyQuietly(peddler);
                    return null;
                }

                return peddler;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} building the peddler threw", ex);
                failure = "threw " + ex.GetType().Name;

                if (peddler != null)
                {
                    peddler.P_ThreadEncounter = null;
                    DestroyQuietly(peddler);
                }

                return null;
            }
        }

        /// <summary>
        /// Percent pricing: one member's own displays, one per offer in stall order, each valued at that member's price
        /// (ThreadDungeonRun.PeddlerPriceFor, from their basis snapshot) and paired with a purchase record at the same
        /// price. Called by ThreadPeddlerVendor.Arm, after the member's shop opened. An offer that builds no display is
        /// skipped (logged by BuildDisplay). A throw part way destroys everything already built and returns none.
        /// </summary>
        internal static List<(WorldObject Display, PeddlerPurchase Purchase)> BuildMemberListings(ThreadDungeonRun run, PeddlerPlan plan, uint memberGuid)
        {
            var listed = new List<(WorldObject Display, PeddlerPurchase Purchase)>();

            try
            {
                foreach (var offer in plan.Offers)
                {
                    var price = run.PeddlerPriceFor(memberGuid, offer.EntryId) ?? offer.Price;
                    var display = BuildDisplay(offer, plan.Config, price);

                    if (display != null)
                        listed.Add((display, new PeddlerPurchase(offer.EntryId, price, offer.Pick.Pattern)));
                }
            }
            catch (Exception ex)
            {
                // Review of 05daac285 (F2): a factory that throws part way must not leak what was already built. Nothing is
                // listed, so Arm fails closed (no own listings) and the member's caches form.
                log.Error($"[DYNDUNGEON] {run} peddler: building the own listings for 0x{memberGuid:X8} threw; none listed", ex);

                foreach (var (display, _) in listed)
                    DestroyQuietly(display);

                listed.Clear();
            }

            return listed;
        }

        private static void DestroyQuietly(WorldObject wo)
        {
            try
            {
                if (wo != null && !wo.IsDestroyed)
                    wo.Destroy();
            }
            catch (Exception ex)
            {
                log.Error("[DYNDUNGEON] peddler: destroying an unplaced peddler threw", ex);
            }
        }

        /// <summary>Memory only: the live NPC, if any, stops trading. EndRun destroys the copy and the NPC with it.</summary>
        public void OnRunEnded(ThreadEncounterInstance instance, string why)
        {
            if (instance?.State is PeddlerPlan)
                instance.Run.PeddlerNpc?.MarkRunEnded();
        }
    }
}
