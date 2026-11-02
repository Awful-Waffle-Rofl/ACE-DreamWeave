using System.Collections.Generic;

using ACE.Entity.Enum;

using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    public enum BindVerdict
    {
        Allowed,
        RefusedItemTarget,
        RefusedPortal,
        RefusedSelfDrain,
        RefusedCannotCast,
        RefusedFull,
    }

    /// <summary>What a spell is, reduced to the three facts that can get a scroll refused.</summary>
    public readonly record struct SpellBindFacts(bool IsItemTargeted, bool IsPortal, bool IsSelfDrain);

    /// <summary>The outcome of one bind attempt on a blank caster summon. Pure data.</summary>
    public readonly record struct BindOutcome(uint? Spell1, uint? Spell2, bool ConsumeScroll, bool Completed);

    /// <summary>
    /// Scroll binding on a blank caster summon (Custom Summons Task 5). Everything here is pure: a Spell needs the
    /// DAT and the world DB and a Player needs a Session, so the verdict takes plain facts and the Spell/Player
    /// overload is a thin extractor over it.
    ///
    /// "Any spell the player could cast" is the owner-ruled skill test: the spell's school skill is Trained or
    /// Specialized on the binder. There is no skill-level check.
    /// </summary>
    public static class SummonSpellBindRules
    {
        public const int MaxBoundSpells = 2;

        /// <summary>
        /// Verdict for binding <paramref name="spell"/> on <paramref name="binder"/>'s behalf. Does not know how many
        /// slots the device has filled; use the <c>boundCount</c> overload for that.
        /// </summary>
        public static BindVerdict Classify(Spell spell, Player binder)
        {
            return Classify(FactsOf(spell), SchoolClassOf(spell, binder));
        }

        /// <summary>Same, with the device's filled-slot count so a third scroll is refused.</summary>
        public static BindVerdict Classify(int boundCount, Spell spell, Player binder)
        {
            return Classify(boundCount, FactsOf(spell), SchoolClassOf(spell, binder));
        }

        public static BindVerdict Classify(int boundCount, SpellBindFacts facts, SkillAdvancementClass schoolClass)
        {
            if (boundCount >= MaxBoundSpells)
                return BindVerdict.RefusedFull;

            return Classify(facts, schoolClass);
        }

        /// <summary>
        /// Refusal order: item-targeting, portal/recall, self-drain, then the skill test. The first reason that
        /// applies is the one named to the player.
        /// </summary>
        public static BindVerdict Classify(SpellBindFacts facts, SkillAdvancementClass schoolClass)
        {
            if (facts.IsItemTargeted)
                return BindVerdict.RefusedItemTarget;

            if (facts.IsPortal)
                return BindVerdict.RefusedPortal;

            if (facts.IsSelfDrain)
                return BindVerdict.RefusedSelfDrain;

            if (schoolClass < SkillAdvancementClass.Trained)
                return BindVerdict.RefusedCannotCast;

            return BindVerdict.Allowed;
        }

        private static SpellBindFacts FactsOf(Spell spell)
        {
            return FactsFrom(spell.School, spell.HasItemCategory, spell.MetaSpellType, spell.TransferFlags, spell.IsBeneficial);
        }

        private static SkillAdvancementClass SchoolClassOf(Spell spell, Player binder)
        {
            var skill = spell.GetMagicSkill();

            if (skill == Skill.None)
                return SkillAdvancementClass.Inactive;

            return binder.GetCreatureSkill(skill, false)?.AdvancementClass ?? SkillAdvancementClass.Inactive;
        }

        /// <summary>
        /// The pure half of the extractor.
        /// Item-targeting: an Item Enchantment spell with no creature category lands on a held item (weapon and armor
        /// auras and the other item enchantments), the same test Creature_Magic uses to pick its target.
        /// Portal: any portal-type meta spell (link, recall, sending, summon, fellowship sending).
        /// Self-drain: a Transfer whose destination is the caster, or a beneficial Transfer whose source and
        /// destination are both the target (see the comment at the rule).
        /// </summary>
        public static SpellBindFacts FactsFrom(MagicSchool school, bool hasItemCategory, SpellType metaType, TransferFlags transfer, bool isBeneficial)
        {
            var itemTargeted = school == MagicSchool.ItemEnchantment && !hasItemCategory;

            var portal = metaType == SpellType.PortalLink
                || metaType == SpellType.PortalRecall
                || metaType == SpellType.PortalSending
                || metaType == SpellType.PortalSummon
                || metaType == SpellType.FellowPortalSending;

            // Owner ruling: refuse a Transfer that lands on the caster itself (the "X to Y Self" family), and a
            // beneficial Transfer whose source AND destination are both the target (it would convert the owner's own
            // vitals). "X to Y Other" and Infuse-style spells, where the pet pays and the owner receives, are allowed.
            var selfDrain = metaType == SpellType.Transfer
                && (transfer.HasFlag(TransferFlags.CasterDestination)
                    || (isBeneficial && transfer.HasFlag(TransferFlags.TargetSource) && transfer.HasFlag(TransferFlags.TargetDestination)));

            return new SpellBindFacts(itemTargeted, portal, selfDrain);
        }

        /// <summary>The slot the next scroll fills: 1, 2, or 0 when both are taken.</summary>
        public static int NextSlot(uint? spell1, uint? spell2)
        {
            if (!spell1.HasValue)
                return 1;

            if (!spell2.HasValue)
                return 2;

            return 0;
        }

        public static int BoundCount(uint? spell1, uint? spell2)
        {
            return (spell1.HasValue ? 1 : 0) + (spell2.HasValue ? 1 : 0);
        }

        /// <summary>
        /// The state after one bind attempt. A refusal changes nothing and consumes nothing (the scroll and the
        /// blank are both left as they were); an allowed bind fills the next slot and consumes the scroll.
        /// </summary>
        public static BindOutcome Apply(uint? spell1, uint? spell2, uint spellId, BindVerdict verdict)
        {
            if (verdict != BindVerdict.Allowed)
                return new BindOutcome(spell1, spell2, false, false);

            switch (NextSlot(spell1, spell2))
            {
                case 1:
                    return new BindOutcome(spellId, spell2, true, false);
                case 2:
                    return new BindOutcome(spell1, spellId, true, true);
                default:
                    return new BindOutcome(spell1, spell2, false, false);
            }
        }

        public static string Message(BindVerdict verdict, string spellName)
        {
            switch (verdict)
            {
                case BindVerdict.RefusedItemTarget:
                    return $"{spellName} is cast on an item, so a summon cannot bind it.";
                case BindVerdict.RefusedPortal:
                    return $"{spellName} is a portal spell, so a summon cannot bind it.";
                case BindVerdict.RefusedSelfDrain:
                    return $"{spellName} costs the caster's own vitals, so a summon cannot bind it.";
                case BindVerdict.RefusedCannotCast:
                    return $"You are not trained in the school of {spellName}, so you cannot bind it.";
                case BindVerdict.RefusedFull:
                    return "This summon already has two bound spells.";
                default:
                    return "";
            }
        }

        // ---- element ----------------------------------------------------------------------------------------

        private const int ElementBits = (int)(UiEffects.Fire | UiEffects.Lightning | UiEffects.Frost | UiEffects.Acid);

        /// <summary>
        /// The UiEffects element a damaging spell gives a summon item: Fire, Frost (cold), Lightning (electric) or
        /// Acid. Anything else - nether, physical, friendly - is no element, since VTank only reads those four.
        /// </summary>
        public static UiEffects ElementOf(bool isHarmful, DamageType damageType)
        {
            if (!isHarmful)
                return UiEffects.Undef;

            switch (damageType)
            {
                case DamageType.Fire: return UiEffects.Fire;
                case DamageType.Cold: return UiEffects.Frost;
                case DamageType.Electric: return UiEffects.Lightning;
                case DamageType.Acid: return UiEffects.Acid;
                default: return UiEffects.Undef;
            }
        }

        /// <summary>
        /// The caster's element: that of the FIRST bound damaging spell (harmful with a damage type). If that spell is
        /// non-elemental the caster carries no element - the search does not move on to the second spell. A caster
        /// with only friendly or typeless spells carries none either.
        /// </summary>
        public static UiEffects CasterElement(IEnumerable<(bool isHarmful, DamageType damageType)> spellsInSlotOrder)
        {
            foreach (var (isHarmful, damageType) in spellsInSlotOrder)
            {
                if (isHarmful && damageType != DamageType.Undef)
                    return ElementOf(true, damageType);
            }

            return UiEffects.Undef;
        }

        /// <summary>
        /// A blank caster summon carries no SharedCooldown (an unbound blank must not look like a summon to VTank, which
        /// detects one by SharedCooldown 213); binding completes it into a real summon, so this writes the retail summon
        /// cooldown id and duration (the same constants the Duo Summon Gem writes). Memory only - the caller saves.
        /// </summary>
        public static void ApplySummonCooldown(WorldObject device)
        {
            device.CooldownId = DuoSummonGem.SummonSharedCooldown;
            device.CooldownDuration = DuoSummonGem.DefaultCooldownSeconds;
        }

        /// <summary>Replaces the four element bits of a UiEffects value, keeping every other bit.</summary>
        public static int WithElement(int? currentUiEffects, UiEffects element)
        {
            return ((currentUiEffects ?? 0) & ~ElementBits) | (int)element;
        }
    }
}
