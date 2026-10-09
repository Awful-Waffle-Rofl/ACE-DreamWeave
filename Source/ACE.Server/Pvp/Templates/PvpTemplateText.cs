namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// Every player-facing PvP template string (TEMPLATES.md "Gates": "Refusal text comes from one PvpTemplateText
    /// class"). Every line carries the "[Arena] " prefix PvpArenaText uses. Placeholders are {name}, filled with
    /// PvpArenaText.Fill. Wording is functional placeholder text for the orchestrator to finalize.
    /// </summary>
    public static class PvpTemplateText
    {
        // ---------------- gate refusals ----------------

        public const string PersonalItemLocked = "[Arena] You cannot use your own items while wearing an arena template. Only issued template items work in a match.";

        public const string IssuedItemLocked = "[Arena] Issued arena template items cannot be given, traded, dropped, sold, stored or salvaged.";

        public const string IssuedItemOutsideMatch = "[Arena] Issued arena template items only work while you are wearing an arena template.";

        public const string MixedStack = "[Arena] You cannot combine issued arena template items with your own items.";

        public const string NonTemplateSpell = "[Arena] That spell is not part of your arena template.";

        public const string EconomyLocked = "[Arena] You cannot trade, give, buy, sell, store or pick up items while wearing an arena template.";

        public const string ProgressionLocked = "[Arena] You cannot gain or spend progression while wearing an arena template.";

        public const string FacetSwitchLocked = "[Arena] You cannot change facets while wearing an arena template.";

        // ---------------- apply ----------------

        public const string Applied = "[Arena] You are now wearing the {template} arena template. Your own build and gear come back when the match ends.";

        public const string ApplyDisabled = "[Arena] Arena templates are switched off.";

        public const string ApplyAlreadyTemplated = "[Arena] You are already wearing an arena template.";

        public const string ApplyBusy = "[Arena] You are too busy to put on an arena template right now.";

        public const string ApplyNoRoom = "[Arena] You need {needed} more free slot{plural} in your MAIN pack to enter a templated match: your worn gear is stored there and the template's pack items are issued there.";

        public const string ApplyNoPackSlotRoom = "[Arena] You need {needed} more free pack slot{plural} to enter a templated match.";

        public const string ApplyRecordTooLarge = "[Arena] Your character holds too many effects to enter a templated match. Please contact staff.";

        public const string ApplyFailed = "[Arena] Something went wrong putting on the arena template. Your own build was not changed.";

        public const string TemplateCannotBeIssued = "[Arena] That arena template cannot be issued right now. Please tell staff.";

        public const string ApplyFailedAfterStart ="[Arena] Something went wrong putting on the arena template, so it has been taken off again.";

        // ---------------- restore ----------------

        public const string Restored = "[Arena] Your arena template has been removed and your own build restored.";

        public const string RestoredAtLogin = "[Arena] Your arena template was removed while you were away. Your own build has been restored.";

        public const string RestoreFailed = "[Arena] Your arena template could not be removed. Your character is locked until staff restore it. Please contact staff.";

        public const string InertAtLogin = "[Arena] Your arena template could not be removed. Your character is locked until staff restore it with /pvptemplate restore. Please contact staff.";

        public const string IssuedItemsSwept = "[Arena] {count} issued arena template item{plural} crumbled away.";

        public const string GearNotRestored = "[Arena] Some of your gear could not be put back on: {details}";

        // ---------------- backstop ----------------

        public const string BackstopMovedItem = "[Arena] {item} is not part of your arena template and has been moved to your pack.";
    }
}
