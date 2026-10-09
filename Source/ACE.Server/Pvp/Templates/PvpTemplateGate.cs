namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// Every action named in the TEMPLATES.md gate tables. Phase B passes one of these to
    /// Player.PvpTemplateBlocked at each site; the decision itself is <see cref="PvpTemplateGate.Decide"/>.
    /// Append-only: a site may log the value.
    /// </summary>
    public enum PvpTemplateAction
    {
        // ---- personal-item lockdown (TEMPLATES.md "Personal-item lockdown") ----

        /// <summary>Equip or wield by drag, double-click or shortcut. Item = the item being equipped.</summary>
        Equip,

        /// <summary>Use an item (potion, food, mana stone, aug gem, scroll, gem, device, essence). Item = the used object.</summary>
        Use,

        /// <summary>Use-with-target (kits, tinkering, dyes, keys, lockpicks). Item = source, target = the target.</summary>
        UseWithTarget,

        /// <summary>A spell component being counted or burned. Item = the component.</summary>
        SpellComponent,

        /// <summary>A cast. Decided by the spell id (Player.PvpTemplateCastBlocked); the item-keyed predicate refuses it while templated (fail closed).</summary>
        Cast,

        /// <summary>Unequip. Item = the item being unequipped.</summary>
        Unequip,

        /// <summary>Merge or split between two stacks. Item = source stack, target = destination stack.</summary>
        MergeOrSplit,

        /// <summary>A move within the player's own pack. The one action a personal item may always take.</summary>
        MoveWithinPack,

        VendorBuy,
        VendorSell,
        GiveToPlayer,
        GiveToNpc,

        /// <summary>Receiving an item from another player or an NPC.</summary>
        ReceiveGive,

        GroundPickup,
        Trade,

        // ---- issued-item economy lock (always) - sites not already named above ----

        Drop,

        /// <summary>Into a container the player does not own (a chest, a corpse, another player's pack), including a split or merge into one.</summary>
        MoveToForeignContainer,

        Salvage,

        /// <summary>Account vault deposit.</summary>
        Vault,

        /// <summary>Mule storage or a mule sale.</summary>
        Mule,

        /// <summary>A market listing.</summary>
        MarketList,

        // ---- progression lock (while templated) ----

        Experience,
        Luminance,
        QuestStamp,
        ClassAbilityPoints,
        Title,
        Contract,
        Challenge,
        House,
        RaiseAttribute,
        RaiseVital,
        RaiseSkill,
        TrainSkill,
        FacetSwitch,

        /// <summary>A Yes on a pending confirmation dialog (Gem of Forgetfulness, augmentation, attribute alter). Default-deny while templated.</summary>
        Confirmation,
    }

    /// <summary>How a gate sees one object, classified by Player.ClassifyForPvpTemplate.</summary>
    public enum PvpTemplateItemKind
    {
        /// <summary>No object, or one that is not a possession of the player (a door, an NPC, another player).</summary>
        None,

        /// <summary>A possession of the player that is not issued (and holds nothing issued).</summary>
        Personal,

        /// <summary>An issued template item, or a container holding one (PvpTemplate.IsIssued).</summary>
        Issued,
    }

    /// <summary>
    /// The decision table of TEMPLATES.md "Gates", as one pure function. Every gate site reaches it through
    /// Player.PvpTemplateBlocked; nothing else reads the record, IsPvpTemplated or PvpTemplateIssued inline.
    /// Returns the refusal text, or null to allow.
    /// </summary>
    public static class PvpTemplateGate
    {
        public static string Decide(PvpTemplateAction action, bool templated, bool systemBypass, PvpTemplateItemKind item, PvpTemplateItemKind target)
        {
            // The system apply and restore paths move issued and personal items on the player's behalf.
            if (systemBypass)
                return null;

            // ---- issued-item economy lock: always, templated or not ----
            if (item == PvpTemplateItemKind.Issued || target == PvpTemplateItemKind.Issued)
            {
                switch (action)
                {
                    case PvpTemplateAction.GiveToPlayer:
                    case PvpTemplateAction.GiveToNpc:
                    case PvpTemplateAction.Trade:
                    case PvpTemplateAction.Drop:
                    case PvpTemplateAction.MoveToForeignContainer:
                    case PvpTemplateAction.VendorSell:
                    case PvpTemplateAction.Vault:
                    case PvpTemplateAction.Mule:
                    case PvpTemplateAction.MarketList:
                    case PvpTemplateAction.Salvage:
                        return PvpTemplateText.IssuedItemLocked;
                }

                if (!templated)
                {
                    switch (action)
                    {
                        case PvpTemplateAction.Equip:
                        case PvpTemplateAction.Use:
                        case PvpTemplateAction.UseWithTarget:
                        case PvpTemplateAction.SpellComponent:
                        case PvpTemplateAction.MergeOrSplit:
                            return PvpTemplateText.IssuedItemOutsideMatch;
                    }
                }
            }

            if (!templated)
                return null;

            // ---- templated: personal-item lockdown and progression lock. The default is to refuse. ----
            switch (action)
            {
                case PvpTemplateAction.MoveWithinPack:
                    return null;

                case PvpTemplateAction.Equip:
                case PvpTemplateAction.SpellComponent:
                    return item == PvpTemplateItemKind.Issued ? null : PvpTemplateText.PersonalItemLocked;

                case PvpTemplateAction.Use:
                    // A world object (a portal, an NPC, a door) is not a personal item.
                    return item == PvpTemplateItemKind.Personal ? PvpTemplateText.PersonalItemLocked : null;

                case PvpTemplateAction.UseWithTarget:
                    return item == PvpTemplateItemKind.Personal || target == PvpTemplateItemKind.Personal ? PvpTemplateText.PersonalItemLocked : null;

                case PvpTemplateAction.Cast:
                    // The spell decides, through Player.PvpTemplateCastBlocked. A site that reaches the item-keyed
                    // predicate with Cast instead has no spell to check, so it refuses rather than fail open.
                    return PvpTemplateText.NonTemplateSpell;

                case PvpTemplateAction.Unequip:
                    // Owner ruling 2026-10-04: issued gear comes off and goes back on freely (the issued shield off, a
                    // switch to the issued wand). Unequip is only ever a move into the player's own inventory (main
                    // pack or a side pack, Player.PvpTemplateMoveAction); a move out of the player is
                    // MoveToForeignContainer or Drop and stays under the issued-item economy lock above.
                    return null;

                case PvpTemplateAction.MergeOrSplit:
                    // Issued into issued, or personal into personal (a split onto empty space passes None as the
                    // target and keeps its source's kind): allowed. Anything that mixes the two is refused.
                    if (target == PvpTemplateItemKind.None)
                        return null;
                    return item == target ? null : PvpTemplateText.MixedStack;

                case PvpTemplateAction.VendorBuy:
                case PvpTemplateAction.VendorSell:
                case PvpTemplateAction.GiveToPlayer:
                case PvpTemplateAction.GiveToNpc:
                case PvpTemplateAction.ReceiveGive:
                case PvpTemplateAction.GroundPickup:
                case PvpTemplateAction.Trade:
                case PvpTemplateAction.Drop:
                case PvpTemplateAction.MoveToForeignContainer:
                case PvpTemplateAction.Salvage:
                case PvpTemplateAction.Vault:
                case PvpTemplateAction.Mule:
                case PvpTemplateAction.MarketList:
                    return PvpTemplateText.EconomyLocked;

                case PvpTemplateAction.FacetSwitch:
                    return PvpTemplateText.FacetSwitchLocked;

                case PvpTemplateAction.Experience:
                case PvpTemplateAction.Luminance:
                case PvpTemplateAction.QuestStamp:
                case PvpTemplateAction.ClassAbilityPoints:
                case PvpTemplateAction.Title:
                case PvpTemplateAction.Contract:
                case PvpTemplateAction.Challenge:
                case PvpTemplateAction.House:
                case PvpTemplateAction.RaiseAttribute:
                case PvpTemplateAction.RaiseVital:
                case PvpTemplateAction.RaiseSkill:
                case PvpTemplateAction.TrainSkill:
                    return PvpTemplateText.ProgressionLocked;

                default:
                    return PvpTemplateText.PersonalItemLocked;
            }
        }

        /// <summary>
        /// The cast gate: while templated, only a template spell may be cast. <paramref name="templateSpells"/>
        /// null while templated (an unreadable record) refuses every cast - inert, never open.
        /// </summary>
        public static string DecideCast(uint spellId, bool templated, bool systemBypass, System.Collections.Generic.ICollection<int> templateSpells)
            => DecideCast(spellId, templated, systemBypass, templateSpells, PvpTemplateItemKind.None);

        /// <summary>
        /// The cast gate with the spell's source item. A spell that comes from an ISSUED item (a wand or orb's built-in
        /// spell) is allowed without being in the template's spellbook: the kit is the authored loadout. A built-in
        /// spell from a PERSONAL item is refused (personal items are unusable while templated). With no source item
        /// (a spellbook cast, or an item that is not a possession) the spellbook decides, exactly as before.
        /// </summary>
        public static string DecideCast(uint spellId, bool templated, bool systemBypass, System.Collections.Generic.ICollection<int> templateSpells, PvpTemplateItemKind casterItem)
        {
            if (systemBypass || !templated)
                return null;

            if (casterItem == PvpTemplateItemKind.Issued)
                return null;

            if (casterItem == PvpTemplateItemKind.Personal)
                return PvpTemplateText.PersonalItemLocked;

            if (templateSpells == null || !templateSpells.Contains((int)spellId))
                return PvpTemplateText.NonTemplateSpell;

            return null;
        }
    }
}
