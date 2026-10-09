using System.Threading;

using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-memory stand-in for ICharacterSheetService, for MarketApiTests: the route layer only needs to
    /// see the outcome mapping and the call counters, never a real build.
    /// </summary>
    internal sealed class FakeCharacterSheetService : ICharacterSheetService
    {
        /// <summary>What GetSheet answers next. Defaults to Ok with an empty sheet.</summary>
        public SheetResult NextSheet = new SheetResult(SheetOutcome.Ok, new CharacterSheet());

        /// <summary>What GetLink answers next. Defaults to Ok with a disabled link.</summary>
        public LinkResult NextLink = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = false });

        /// <summary>What EnableOrRotate answers next. Defaults to Ok with an enabled link.</summary>
        public LinkResult NextEnableOrRotate = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = true, Slug = "Ab3dE5gH9k", Url = "https://char.example.test/Ab3dE5gH9k" });

        /// <summary>What Disable answers next. Defaults to Ok with a disabled link.</summary>
        public LinkResult NextDisable = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = false });

        /// <summary>What GetOwnerProfile answers next. Defaults to Ok with an empty profile.</summary>
        public OwnerProfileResult NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile { Name = "Fakechar", Level = 10, Heritage = "Aluvian", Online = true });

        public int GetOwnerProfileCalls;
        public uint LastOwnerProfileGuid;

        public int GetSheetCalls;
        public int GetLinkCalls;
        public int EnableCalls;
        public int DisableCalls;

        public SheetResult GetSheet(string slug)
        {
            Interlocked.Increment(ref GetSheetCalls);
            return NextSheet;
        }

        public OwnerProfileResult GetOwnerProfile(uint characterGuid)
        {
            Interlocked.Increment(ref GetOwnerProfileCalls);
            LastOwnerProfileGuid = characterGuid;
            return NextOwnerProfile;
        }

        public LinkResult GetLink(uint characterGuid)
        {
            Interlocked.Increment(ref GetLinkCalls);
            return NextLink;
        }

        public LinkResult EnableOrRotate(uint characterGuid, bool rotate)
        {
            Interlocked.Increment(ref EnableCalls);
            return NextEnableOrRotate;
        }

        public LinkResult Disable(uint characterGuid)
        {
            Interlocked.Increment(ref DisableCalls);
            return NextDisable;
        }
    }
}
