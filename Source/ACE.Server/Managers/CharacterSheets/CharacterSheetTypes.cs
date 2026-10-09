using System;
using System.Collections.Generic;

using ACE.Server.Managers.Market;

namespace ACE.Server.Managers.CharacterSheets
{
    // Wire DTOs, serialized snake_case by MarketApiHost.Json. The names are the published contract the
    // market web app reads; CharacterSheetProjectorTests pins every one of them.

    public sealed class CharacterSheet
    {
        public string Name { get; set; }
        public int Level { get; set; }
        public DateTime GeneratedAt { get; set; }           // UTC
        public List<SheetStat> Attributes { get; set; } = new();
        public List<SheetStat> Vitals { get; set; } = new();
        public List<SheetItem> Equipped { get; set; } = new();
        public List<SheetSkill> Skills { get; set; } = new();
        public List<SheetAbility> ClassAbilities { get; set; } = new();
        public List<SheetRank> Ranks { get; set; } = new();

        /// <summary>
        /// The owner-only extras, filled ONLY when the projection was asked for the owner view
        /// (CharacterSheetProjector.Project's ownerView flag). Internal, so it can never reach the public
        /// sheet's JSON: System.Text.Json serializes public properties only.
        /// </summary>
        internal OwnerProfile Owner { get; set; }
    }

    /// <summary>
    /// What GET /v1/accounts/me/characters/{guid}/suit-profile returns: the character's own numbers and its
    /// suit-relevant equipment, for the character's owner. Built from the same Player reads as the public
    /// sheet (attributes, vitals and skills carry both base and current), and never cached or opt-in gated.
    /// </summary>
    public sealed class OwnerProfile
    {
        public uint CharacterGuid { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public string Heritage { get; set; }

        /// <summary>The character's HeritageGroup as its numeric id (Olthoi 12, OlthoiAcid 13), which the display name cannot always tell apart.</summary>
        public int HeritageId { get; set; }

        public bool Online { get; set; }
        public List<SheetStat> Attributes { get; set; } = new();
        public List<SheetStat> Vitals { get; set; } = new();
        public List<SheetSkill> Skills { get; set; } = new();
        public List<ACE.Server.Managers.Market.Suit.SuitItem> Equipped { get; set; } = new();

        /// <summary>
        /// The appraisal snapshot (the same SheetItem the public sheet carries) of each item in <see cref="Equipped"/>,
        /// joined on ItemGuid. Weapons, shields and aetheria have no suit entry, so they have none here either.
        /// </summary>
        public List<SheetItem> EquippedAppraisal { get; set; } = new();
    }

    public sealed class SheetStat    { public string Name { get; set; } public uint Base { get; set; } public uint Current { get; set; } }
    public sealed class SheetItem    { public uint ItemGuid { get; set; } public string Name { get; set; } public string Slot { get; set; } public ListingSnapshot Snapshot { get; set; } }
    public sealed class SheetSkill   { public string Name { get; set; } public string Advancement { get; set; } public uint Base { get; set; } public uint Current { get; set; } }   // "specialized" | "trained"
    public sealed class SheetAbility { public string Name { get; set; } public string DisplayName { get; set; } public string Class { get; set; } public int Tier { get; set; } public int Rank { get; set; } public int MaxRank { get; set; } }
    public sealed class SheetRank    { public string Board { get; set; } public string Title { get; set; } public int Rank { get; set; } public string ScoreText { get; set; } }
    public sealed class SheetLink    { public bool Enabled { get; set; } public string Slug { get; set; } public string Url { get; set; } }

    public enum SheetOutcome { Ok, NotFound, Disabled, Busy, Failed }
    public readonly record struct SheetResult(SheetOutcome Outcome, CharacterSheet Sheet);
    public readonly record struct LinkResult(SheetOutcome Outcome, SheetLink Link);
    public readonly record struct OwnerProfileResult(SheetOutcome Outcome, OwnerProfile Profile);

    public interface ICharacterSheetService
    {
        SheetResult GetSheet(string slug);

        /// <summary>
        /// The owner's own view of one character (suit builder). The CALLER must already have proved the
        /// session's account owns <paramref name="characterGuid"/>. Takes no slug, never reads or writes the
        /// public sheet cache, and ignores the opt-in link and charsheet_enabled. Same request budget and
        /// offline-build cap as GetSheet. Outcomes: Ok, NotFound (missing or deleted), Busy, Failed.
        /// </summary>
        OwnerProfileResult GetOwnerProfile(uint characterGuid);
        LinkResult GetLink(uint characterGuid);
        LinkResult EnableOrRotate(uint characterGuid, bool rotate);
        LinkResult Disable(uint characterGuid);
    }
}
