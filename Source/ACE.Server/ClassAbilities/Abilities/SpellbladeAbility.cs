using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T1 game-changer: a landed LIGHT WEAPON hit has a flat 25% base chance to cast a Streak
    /// war spell at the target, matching the damage type the swing actually dealt. Rank does NOT raise the
    /// chance - it raises the spell LEVEL cap (III / V / top rung), which is the class's whole purchase
    /// shape (SPELLSWORD-DESIGN.md sections 1, 3). The Item Enchantment affinity raises the CHANCE
    /// (design Q2), so the proc is rank-invariant but not skill-invariant.
    ///
    /// Like AcidProcAbility this uses IOutgoingDamageAbility purely as a "landed weapon hit vs a monster"
    /// trigger: it does not touch the strike's damage at all. The spell it spawns is a separate,
    /// independently-resisted, independently-critting event.
    ///
    /// THE CAST IS PLAYER-SOURCED AND SWORD-LAUNCHED, and the two are different arguments on purpose:
    /// - CASTER is the player, so the target's magic-defense check and every skill-derived term run off the
    ///   player's War Magic rather than off an item's spellcraft. That is the class premise - "proc damage
    ///   scales with the player's magic skills, never their melee skills" (design section 1c / Q3).
    /// - LAUNCHER is the wielded weapon, which is what lets the weapon's rend, slayer and crit frequency
    ///   reach the proc (design Q11 / section 5a). The rend term is the happiest part: GetRendingMod reads
    ///   Spell.School, which for a Streak is War Magic, so a rending light weapon rends the proc and the
    ///   rend magnitude scales off the player's magic skill.
    /// </summary>
    public class SpellbladeAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Spellblade,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 1,
            Name = "spellblade",
            DisplayName = "Spellblade",
            Description = "Your landed light weapon hits have a 25% chance to cast a Streak war spell at the " +
                          "target, matching the damage type of the swing. Rank raises the spell level cap " +
                          "(III / V / the Incantation), not the chance; the level you actually throw is also " +
                          "limited by your War Magic. Higher Item Enchantment increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ItemEnchantment,
        };

        /// <summary>
        /// This handler casts a spell from inside the outgoing-damage dispatch, so it runs in the LAST band,
        /// after Spellweave's weapon half has resolved for the same hit. See
        /// OutgoingDamageDispatchOrder.SpellCastingProc.
        /// </summary>
        public int DispatchOrder => OutgoingDamageDispatchOrder.SpellCastingProc;

        /// <summary>
        /// The proc chance: a flat, rank-invariant base MULTIPLIED by the Item Enchantment affinity
        /// (2026-09-12 overhaul), plus the current Spellsurge stack bonus, plus the Spellblade equipment mod.
        /// Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// There is deliberately no rank term on the base. Rank buys spell level (see
        /// SpellswordSpellTables.MaxLevelForRank), which is the design's stated shape for all three war
        /// procs; a rank term here would double-dip the purchase.
        ///
        /// <paramref name="affinityMultiplier"/> is what <c>Player.GetClassAbilityAffinityMultiplier</c>
        /// returns: a FACTOR &gt;= 1.0 that multiplies <paramref name="chanceBase"/>, not a raw additive
        /// quotient. What this method reports as the affinity's contribution is the AMOUNT that multiply
        /// adds (<c>chanceBase * affinityMultiplier - chanceBase</c>), because the chance is still a sum of
        /// terms at the call site (base + affinity + Spellsurge + gear) even though affinity itself is
        /// computed by multiplying.
        ///
        /// <paramref name="affinityCap"/> bounds THAT ADDED AMOUNT, not the raw multiplier -
        /// GetClassAbilityAffinityMultiplier has no ceiling of its own (ClassAbilityAffinity.Multiplier is
        /// linear in the source skill), so an uncapped multiply still saturates this proc exactly as the old
        /// additive rider did. Live test 2026-08-04: a character with Item Enchantment 5226 produced a rider
        /// of +209 percentage points, which saturated this proc to a literal 100% on every swing. Spellstorm,
        /// the one war proc with no affinity, was simultaneously firing at its correct ~20%, which is what
        /// isolated the term. A cap of 0 means uncapped, which is only for tests that want the raw sum.
        ///
        /// <paramref name="gearModChance"/> is the SPELLBLADE equipment mod (EquipmentModId.Spellblade), a
        /// MACHINERY mod: it amplifies this ability's own roll and is unreachable without it, because the
        /// rank test above returns first. It is added OUTSIDE the affinity clamp AND outside the multiply
        /// itself, in the same position <paramref name="spellsurgeBonus"/> occupies - see the note on the
        /// clamp above. Last and defaulted to 0, so an unmodded build reproduces the previous chance
        /// bit-for-bit.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double affinityMultiplier, double spellsurgeBonus = 0.0, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var added = chanceBase * affinityMultiplier - chanceBase;

            // THE GEAR TERM IS OUTSIDE THIS CLAMP, and the placement is load-bearing rather than tidy.
            // affinityCap exists to bound the affinity ADDED AMOUNT, which is unbounded because the
            // multiplier itself has no ceiling. The gear term is already bounded twice over - by the row's
            // registry MaxMagnitude and by its StackCap - so folding it inside the clamp would let a
            // saturating multiply silently eat the mod, which is the Resonance failure recorded in
            // DESIGN.md section 2.2.
            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            added = Math.Max(0.0, added);

            return (float)Math.Max(0.0, chanceBase + added + Math.Max(0.0, spellsurgeBonus) + Math.Max(0.0, gearModChance));
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // Light weapons only (design section 1, "the three war procs are light weapons only"), tested
            // against the weapon that actually landed THIS hit rather than the current attack skill - see
            // SpellswordSpellTables.IsLightWeaponHit for why GetCurrentWeaponSkill() silently breaks
            // offhand dual-wield swings.
            if (!SpellswordSpellTables.IsLightWeaponHit(attacker, damageEvent.Weapon))
                return;

            // THE DEALT damage type, never the weapon's declared W_DamageType. DamageEvent.DamageType is set
            // from Player.GetDamageType, which is what resolves a combined Pierce|Slash weapon down to the
            // single type the swing actually landed - so a thrust procs Force Streak and a slash from the
            // same sword procs Whirling Blade Streak. This is the user's hardest constraint and it costs
            // zero lines: read the field, inspect nothing else.
            var damageType = damageEvent.DamageType;

            var itemEnchantAffinity = attacker.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment);

            // Spellblade equipment mod (MACHINERY): +pp on this ability's own proc roll. Ownership is
            // already proven - ApplyOutgoingDamageClassAbilities dispatches only handlers in the player's
            // LEARNED hook cache - so the plain read is correct here and no GetMachineryEquipmentModValue
            // gate is needed.
            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_spellblade_chance").Item,
                itemEnchantAffinity,
                attacker.GetSpellsurgeProcChanceBonus(),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                attacker.GetEquippedModValue(EquipmentModId.Spellblade));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Level is driven by War Magic against the engine's own MinPower thresholds, capped by rank.
            // Returns null for an exotic damage type (Health / Stamina / Mana / Nether / Base), which must
            // proc nothing, silently.
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType,
                SpellswordSpellTables.StreakLevels,
                damageType,
                attacker.GetCreatureSkill(Skill.WarMagic).Current,
                rank,
                PropertyManager.GetDouble("class_ability_spellsword_level_skill_scale").Item);

            if (spellId == null)
                return;

            var spell = new Spell(spellId.Value);

            // fromProc suppresses the weapon's own item procs (this is a cast-on-strike, not a cast).
            //
            // The spawned projectiles are deliberately NOT flagged IsClassAbilitySpawned. Design Q10: "I
            // want this class to be a firework" - leaving them unflagged is what lets Archmage's Echo Cast
            // and Elemental Rend fire off a Spellsword proc. It cannot recurse: a proc is triggered by a
            // landed MELEE hit (IOutgoingDamageAbility), never by a spell hit, so no proc can ever spawn
            // another proc. Spell AOE still cannot radiate off these - it is Arc-only and a Streak is not
            // an Arc - which is a property of that ability's own gate, not of this flag.
            //
            // WRAPPED IN CastClassAbilityProc SO CASCADE CAN SEE THIS. The latch stamps
            // SpellProjectile.IsClassAbilityProc on every projectile this cast launches
            // (WorldObject_Magic.LaunchSpellProjectiles), which is the ONLY signal that tells a Spellsword
            // proc apart from an ordinary war cast - fromProc is also true for item cast-on-strike, cloak
            // procs and Echo Cast recasts. Cascade FAILS CLOSED without this: an unwrapped cast simply
            // never cascades, with no error and no log line.
            attacker.CastClassAbilityProc(() =>
                attacker.CreateSpellProjectiles(spell, target, damageEvent.Weapon, false, fromProc: true, suppressPetAssist: true));

            // A landed war proc feeds Spellsurge (design section 5e). Sundermark deliberately does not.
            attacker.AddSpellsurgeStack();
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into Chance() above. Skill is the flat base chance and is
        /// RANK-INVARIANT (rank buys spell level, not chance - see the class doc comment), so it does not
        /// multiply by rank; it is 0 only when the ability is unowned. Affinity is the CAPPED added amount
        /// (chanceBase * multiplier - chanceBase, then clamped), so the three displayed terms still sum to
        /// Effective - unlike the pre-migration additive readout, which showed the raw uncapped rider here.
        /// Gear is the SPELLBLADE mod, added to Effective OUTSIDE the clamp exactly as Chance() adds it, so
        /// the panel and combat agree at any Item Enchantment.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_spellblade_chance").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase;

            var affinityMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment);
            var rawAdded = skillChance * affinityMultiplier - skillChance;

            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;
            clampedAdded = Math.Max(0.0, clampedAdded);
            var capBit = affinityCap > 0.0 && rawAdded > affinityCap;

            // Rank-gated like skillChance above: this is a MACHINERY mod, so it reports nothing without the
            // ability. /abilities only renders a readout at rank > 0, so the gate never bites in practice -
            // it is here so the panel cannot contradict the machinery contract if that ever changes.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Spellblade) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = clampedAdded * 100.0;
            var gear = gearChance * 100.0;
            var effective = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = effective,
                Unit = "%",
                Label = "proc",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
