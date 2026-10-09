using System;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T1 splash: any weapon attack the Archer lands stamps a 10 second mark on the target, and a
    /// marked creature takes 1/2/3/4/5% more damage by rank from EVERY source and damage type - the Archer,
    /// their fellows, and their pets alike. The group-wide half is the point of the entry: it is the class's
    /// contribution to a fellowship's damage rather than another private rider on the Archer's own arrows.
    ///
    /// THE MARK IS ITS OWN DEBUFF AXIS (owner ruling 2026-09-14: "a stackable separate debuff"). It is written
    /// into EnchantmentManager.SpellCategory_ClassAbility_HuntersMark and read ONLY by
    /// Creature.GetHuntersMarkMod, which Creature.GetResistanceMod multiplies onto the resistance term AFTER
    /// the vulnerability-vs-weapon max. So it multiplies with Vulnerability, Sundermark, Elemental Rend and
    /// rending/cleaving weapons, and with Imperil through the separate armor term. It is element-independent,
    /// so a hit of any damage type applies it and a fellow's hit of any damage type benefits. Life damage takes
    /// it through the HealthDrain case; Drain is excluded (it reads GetHealthDrainResistanceOnly).
    ///
    /// This replaced a level-1 Vulnerability clone that was effectively inert: it sat in the element's real
    /// Vulnerability category at level 1's Power, so any stronger Vulnerability, Sundermark or Elemental Rend
    /// won the top-layer duel and zeroed it; a rending weapon replaced the whole vulnerability term with its
    /// own larger value; a different-element hit got nothing; Nether and Health hits placed no mark; and a
    /// same-caster refresh through EnchantmentManager.Add kept the first application's magnitude.
    ///
    /// STILL A REAL ENCHANTMENT, per the 2026-09-12 settled ruling: invisible attacker-side state cannot be
    /// read by a second player's damage path, so the mark lives in the target's enchantment registry. It
    /// carries <see cref="MarkStatModType"/> / <see cref="MarkStatModKey"/>, a shape no StatModType aggregator
    /// selects, so the entry changes nothing except through GetHuntersMarkMod.
    ///
    /// STACKING ACROSS ARCHERS: one entry per archer (see <see cref="ApplyMark"/>), combined by
    /// HuntersMarkMath.Combine - the strongest mark applies (owner ruling 2026-09-14). A low-rank archer's
    /// refresh overwrites only its own entry, so it can never clobber a high-rank archer's mark.
    ///
    /// AFFINITY: Assess Creature, multiplying the bonus (rank * percent_per_rank * affinity - unlike Break
    /// Armor/Sundermark, the affinity here scales the MAGNITUDE, not a proc chance, so it is baked into the
    /// entry's own StatModValue rather than reported as a separate rider).
    /// </summary>
    public class HuntersMarkAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.HuntersMark,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 1,
            Name = "hunters_mark",
            DisplayName = "Hunter's Mark",
            Description = "Any weapon attack you land marks the target for 10 seconds. A marked creature takes " +
                          "1/2/3/4/5% more damage (by rank) from every source and damage type, including your " +
                          "fellows and pets. The mark is its own debuff: it stacks with Vulnerability, Imperil " +
                          "and rending weapons. If several archers mark a target, the strongest mark applies. " +
                          "Higher Assess Creature multiplies the bonus.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.AssessCreature,
        };

        /// <summary>
        /// The ONE client-facing identity every mark write uses: Gauntlet Vulnerability Self (spell 6324). It
        /// is never cast, resisted or looked up for its magnitude - AddClassAbilityDebuff takes only the id
        /// (and <see cref="MarkPowerLevel"/>) - so the real spell's category, power and StatMod data play no
        /// part. Every archer's entry shares this id and is told apart by caster; AddEnchantmentAtFreeLayer
        /// gives each its own layer.
        ///
        /// THE ID MUST BE ONE NO PLAYER CAN PUT ON A CREATURE. EnchantmentManager.Remove, which the heartbeat
        /// expiry path also uses, deletes the FIRST registry entry matching (spell id, caster) and ignores
        /// category and layer (PropertiesEnchantmentRegistryExtensions.TryRemoveEnchantment). A player-castable
        /// identity such as Piercing Vulnerability Other I lets an archer who also cast that spell on the same
        /// creature hold two entries under one (spell id, caster), and either expiry can then delete the other.
        ///
        /// Evidence that 6324 has no player-reachable source on a creature:
        ///  - it is a Self-target spell, so any cast of it lands on the caster, never on a creature;
        ///  - it is not in Player.PlayerSpellTable (Player_AllowedSpellID.cs), the list LearnSpellsInBulk
        ///    teaches from;
        ///  - nothing else in Source/ or Content/ names GauntletVulnerabilitySelf, and the only other numeric
        ///    6324 hits are unrelated wcids in the two WeenieClassName enums;
        ///  - the local ace_world holds zero weenie_properties_spell_book rows, zero weenie_properties_d_i_d
        ///    rows of any type (so no scroll, gem, Spell or ProcSpell source) and zero emote casts for it;
        ///  - it is not in ElementalRendAbility's vulnerability ladders (which Sundermark and DebuffEffect also
        ///    draw from), and no other AddClassAbilityDebuff caller uses it.
        /// HuntersMarkDebuffTests pins the identity and the same-caster removal case.
        /// </summary>
        public const SpellId MarkIdentitySpell = SpellId.GauntletVulnerabilitySelf;

        /// <summary>Layer-ordering power for the entry. Nothing reads a top layer of this category, so it is inert.</summary>
        public const uint MarkPowerLevel = 1;

        /// <summary>
        /// The entry's StatModType. Every StatModType aggregator filters on "(entry type AND requested) ==
        /// requested", and each one requests Additive, Multiplicative, BodyArmorValue or Beneficial (resistance,
        /// vulnerability, protection, attribute, vital, skill, rating, armor, DoT and dispel readers alike).
        /// Float | SingleStat carries none of those four, so no aggregator selects the entry; only the
        /// Undef "everything" filter (a player-self cleanse; a mark is never on a player) can see it.
        /// </summary>
        public const EnchantmentTypeFlags MarkStatModType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat;

        /// <summary>
        /// The entry's StatModKey. 0 matches no keyed reader: resistance keys are ResistX ids, and the
        /// heartbeat DoT/HoT sort keys on DamageOverTime / NetherOverTime / HealOverTime. The MultipleStat
        /// readers that do match key 0 also require the MultipleStat flag, which <see cref="MarkStatModType"/>
        /// does not carry.
        /// </summary>
        public const uint MarkStatModKey = 0;

        /// <summary>
        /// The mark's total "more damage taken" fraction at a given rank: rank * percentPerRank, MULTIPLIED
        /// by the Assess Creature affinity factor (no cap is registered for this entry). Pure for testability.
        /// Returns 0 for rank &lt;= 0.
        /// </summary>
        public static double MarkPercent(int rank, double percentPerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            return rank * percentPerRank * affinityMultiplier;
        }

        /// <summary>
        /// The StatModValue a mark entry carries for <paramref name="markPercent"/> (a fraction, e.g. 0.05 for
        /// +5%): Creature.GetHuntersMarkMod reads each entry's value as a straight damage-taken multiplier, so
        /// 1.0 + markPercent applies exactly markPercent worth of bonus damage. Never below 1.0.
        /// </summary>
        public static float MarkStatModVal(double markPercent) => (float)(1.0 + Math.Max(0.0, markPercent));

        /// <summary>
        /// THE SINGLE WRITE SITE for a mark. Adds <paramref name="caster"/>'s mark on <paramref name="target"/>,
        /// or refreshes that caster's existing one - resetting its clock AND overwriting its magnitude, so a
        /// mark always reflects the caster's current rank and affinity rather than its first roll.
        ///
        /// refreshOnlyOwnCaster: true is what makes it one entry PER CASTER: the refresh lookup ignores other
        /// archers' entries, so a second archer lays down an independent entry and a low-rank archer can never
        /// overwrite a high-rank one. Returns null (writes nothing) for a non-positive percent or duration.
        /// </summary>
        public static PropertiesEnchantmentRegistry ApplyMark(Creature target, WorldObject caster, double markPercent, double durationSeconds)
        {
            if (target == null || markPercent <= 0.0 || durationSeconds <= 0.0)
                return null;

            return target.EnchantmentManager.AddClassAbilityDebuff(
                (uint)MarkIdentitySpell,
                MarkPowerLevel,
                caster,
                (SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_HuntersMark,
                MarkStatModType,
                MarkStatModKey,
                MarkStatModVal(markPercent),
                durationSeconds,
                refreshOnlyOwnCaster: true);
        }

        /// <summary>
        /// The target-side visual for a newly marked creature. Not ShieldDownGrey: that is Imperil's own
        /// portal.dat TargetEffect (Imperil Other I-VI), so the mark read as an Imperil landing.
        /// </summary>
        public const PlayScript MarkVisual = PlayScript.EnchantDownGreen;

        /// <summary>
        /// True while the target carries ANY archer's mark. An expired entry counts until the enchantment
        /// heartbeat removes it, the same window in which GetHuntersMarkMod still applies its bonus.
        /// </summary>
        public static bool IsMarked(Creature target)
        {
            return target != null &&
                   target.EnchantmentManager.GetEnchantments((SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_HuntersMark).Count > 0;
        }

        /// <summary>
        /// Applies the mark and returns the visual to play, or null for none. Only the transition from unmarked
        /// to marked plays <see cref="MarkVisual"/>; renewing a mark - the same archer's refresh, or a second
        /// archer marking an already-marked creature - plays nothing, so sustained fire does not replay the
        /// effect on every hit (owner ruling 2026-09-25).
        /// </summary>
        public static PlayScript? ApplyMarkAndGetVisual(Creature target, WorldObject caster, double markPercent, double durationSeconds)
        {
            var wasMarked = IsMarked(target);

            if (ApplyMark(target, caster, markPercent, durationSeconds) == null)
                return null;

            return wasMarked ? null : MarkVisual;
        }

        /// <summary>
        /// Applies (or refreshes) the attacker's mark on every landed hit, whatever its damage type - Nether
        /// and Health hits included, since the mark is element-independent. PvE only, and never on a corpse.
        ///
        /// The visual is cosmetic and universal, whatever the hit's damage type, and plays only when the target
        /// goes from unmarked to marked (see <see cref="ApplyMarkAndGetVisual"/>). AddClassAbilityDebuff plays
        /// nothing itself, the same gap Sundermark and Elemental Rend document.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // PvE only - never applies a hostile effect to a player.
            if (target is Player)
                return;

            if (target.IsDead)
                return;

            var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.AssessCreature);
            var perRank = PropertyManager.GetDouble("class_ability_huntersmark_percent_per_rank").Item;
            var markPercent = MarkPercent(rank, perRank, affinity);

            var duration = PropertyManager.GetDouble("class_ability_huntersmark_duration_seconds").Item;

            var visual = ApplyMarkAndGetVisual(target, attacker, markPercent, duration);
            if (visual == null)
                return;

            target.EnqueueBroadcast(new GameMessageScript(target.Guid, visual.Value));
        }

        /// <summary>
        /// Mirrors MarkPercent above. No gear term is registered for this entry, and no affinity cap either
        /// (see the class doc comment) - Skill + Affinity always equals Effective, with no CapNote.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_huntersmark_percent_per_rank").Item;

            var skillPercent = rank <= 0 ? 0.0 : rank * perRank;

            var affinityMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.AssessCreature);
            var affinityAdded = skillPercent * affinityMultiplier - skillPercent;

            var skill = skillPercent * 100.0;
            var affinity = affinityAdded * 100.0;
            var effective = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = effective,
                Unit = "%",
                Label = "dmg taken",
                Per = null,
                CapNote = null,
            };
        }
    }
}
