using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The shared fixture of the Gauntlet purchase and panel tests: Avatar 7 bound to monarch 1000 (a monarch, a Seneschal, a
    /// plain member and an outsider), a factory-built Avatar vendor, and a fake run starter. Every seam it swaps is restored in
    /// <see cref="Dispose"/>. Nothing reads PropertyManager: the tunables go through the AvatarVendor and GauntletPurchase seams.
    /// </summary>
    internal sealed class GauntletVendorHarness : IDisposable
    {
        public const double Start = 1_800_000_000.0;
        public const uint AvatarId = 7;
        public const uint Monarch = 1000;
        public const uint MonarchAcct = 100;
        public const uint VassalGuid = 1001;
        public const uint VassalAcct = 101;
        public const uint PlainGuid = 1002;
        public const uint OutsiderGuid = 2000;
        public static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        public static readonly AvatarTuning AT = AvatarTuning.Defaults with { EventMinLevel = 1 };

        private const string ShopGeneratorKey = "vendor_shop_uses_generator";
        private static uint nextGuid = 0x7ECA0000;

        public GauntletTuning GT = GauntletTuning.Defaults with { MinAvatarLevel = 1 };
        public readonly List<string> Told = new List<string>();
        public readonly FakeStarter Starter = new FakeStarter();
        public Player Monarchp, Seneschal, Plain, Outsider;

        private readonly Store store = new Store();
        private readonly AccountActivityIndex activity = new AccountActivityIndex();
        private readonly List<AvatarRoster> rosters = new List<AvatarRoster>();

        public GauntletVendorHarness(bool installStarter = true)
        {
            SalvageRollerVendorTests.SeedVendorConstructible();
            AvatarTestChart.Install();

            var facts = new Dictionary<uint, (uint, bool)>
            {
                { Monarch, (Monarch, false) },
                { VassalGuid, (Monarch, true) },
                { PlainGuid, (Monarch, false) },
                { OutsiderGuid, (OutsiderGuid, false) },
            };

            AvatarRights.AllegianceFacts = p => facts[p.Guid.Full];
            AvatarVendor.Tuning = () => AT;
            AvatarVendor.ListingBuilder = () => MakeDisplay(AvatarVendor.EventListingWcid);
            AvatarVendor.GauntletListingBuilder = () => MakeDisplay(AvatarVendor.GauntletListingWcid);
            AvatarVendor.BlessingListingBuilder = () => MakeDisplay(AvatarVendor.BlessingListingWcid);
            AvatarBlessing.AllOnline = () => Array.Empty<Player>();
            AvatarAnnouncer.Sink = (m, text) => { };
            AvatarStatusText.KeeperName = g => g == Monarch ? "Varric" : null;
            GauntletPurchase.Tuning = () => GT;
            GauntletPurchase.UtcNow = () => Today;
            GauntletPurchase.ResendPanel = (n, p) => { };
            GauntletPurchase.NpcInInstance = pos => pos == null || pos.Instance != 0;

            if (installStarter)
                GauntletRunStarter.Current = Starter;

            Monarchp = SalvageRollerVendorTests.MakePlayer(Monarch);
            Seneschal = SalvageRollerVendorTests.MakePlayer(VassalGuid);
            Plain = SalvageRollerVendorTests.MakePlayer(PlainGuid);
            Outsider = SalvageRollerVendorTests.MakePlayer(OutsiderGuid);
        }

        public void Dispose()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
            AvatarTestChart.Reset();
            AvatarVendor.ResetSeams();
            AvatarBlessing.ResetSeams();
            AvatarRights.ResetSeams();
            GauntletPurchase.ResetSeams();
            GauntletRunStarter.Current = null;
            AvatarAnnouncer.Sink = null;
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => AT, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        /// <summary>Avatar 7 bound to monarch 1000, Active after one tick.</summary>
        public AvatarState InitActive(ulong favor = 5000, ulong lifetime = 10_000, int r = 2, int s = 2)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound,
                Favor = favor, LifetimeXp = lifetime, LevelXp = lifetime, LastDecayDay = Today,
            });
            rosters.Add(AvatarScaleFixture.Roster(activity, r, s, Monarch, MonarchAcct, VassalGuid, VassalAcct, Start));

            AvatarManager.Initialize(store, () => AT, () => Start, () => activity, () => rosters, m => rosters.FirstOrDefault(r => r.MonarchGuid == m));
            AvatarManager.Tick();

            var state = AvatarManager.Get(AvatarId);
            if (state == null || state.State != AvatarBindState.Bound || !state.Active)
                throw new InvalidOperationException("the fixture must start Bound and Active");

            return state;
        }

        public static WorldObject MakeDisplay(uint wcid)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Listing" } },
            };

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        public AvatarVendor MakeVendor(uint avatarId = AvatarId, uint instance = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.AvatarWcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceAvatarVendor, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 0 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, 0 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.BuyPrice, 0 }, { PropertyFloat.SellPrice, 1 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Avatar" } },
            };

            var vendor = (AvatarVendor)WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(++nextGuid));
            vendor.AvatarId = avatarId;
            vendor.Location = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, instance);
            vendor.Told = (p, text) => Told.Add(text);
            return vendor;
        }

        public static List<WorldObject> Panel(AvatarVendor vendor, Player viewer)
        {
            var rows = new List<WorldObject>();
            vendor.ForEachItemFor(viewer, rows.Add);
            return rows;
        }

        /// <summary>A starter that records what it is given and can be told to refuse.</summary>
        internal sealed class FakeStarter : IGauntletRunStarter
        {
            private readonly object gate = new object();
            public readonly List<GauntletStartRequest> Started = new List<GauntletStartRequest>();
            public int CheckCalls;
            public int StartCalls;
            public string CheckRefusal;
            public string StartRefusal;
            public bool ThrowOnStart;
            /// <summary>When true a second TryStart for an Avatar that already has a run is refused, as the real manager does.</summary>
            public bool OneRunPerAvatar = true;

            public string CheckCanStart(uint avatarId, DateTime nowUtc)
            {
                lock (gate)
                {
                    CheckCalls++;
                    return CheckRefusal;
                }
            }

            public bool TryStart(GauntletStartRequest request, out string refusal)
            {
                lock (gate)
                {
                    StartCalls++;

                    if (ThrowOnStart)
                        throw new InvalidOperationException("boom");

                    if (StartRefusal != null)
                    {
                        refusal = StartRefusal;
                        return false;
                    }

                    if (OneRunPerAvatar && Started.Any(r => r.AvatarId == request.AvatarId))
                    {
                        refusal = "A run is already live for this Avatar.";
                        return false;
                    }

                    Started.Add(request);
                    refusal = null;
                    return true;
                }
            }
        }

        internal sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
