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
    /// Spellsword T2 game-changer: a landed LIGHT WEAPON hit has a flat 15% base chance to cast a Blast war
    /// spell at the target, matching the damage type the swing actually dealt. Same shape as Spellblade -
    /// rank buys spell LEVEL, the Magic Item Tinkering affinity buys CHANCE - but the payload is a 90 degree
    /// cone rather than a single projectile.
    ///
    /// WHAT RUNEBLADE IS FOR, and why its chance is lower than the T1 entry's. A Blast lands exactly ONE
    /// projectile on the centre target; the other two (four at the Incantation) run impact checks against
    /// other monsters. So Blast's SINGLE-TARGET damage equals Streak's at the same level, and everything
    /// this adds over Spellblade is cleave (SPELLSWORD-DESIGN.md section 2b / Q1). 15% keeps T2 below T1 on
    /// the single-target column while paying for itself against a pack. It is not "a bigger Streak", and a
    /// future tuning pass that treats it as one will overprice it.
    ///
    /// The Blast ladder STARTS AT LEVEL III - there is no Blast I or II - which happens to make the rank cap
    /// land perfectly: rank 1's cap of level III is the ladder's own floor, so no clamping special case is
    /// needed. See SpellswordSpellTables.BlastLevels.
    ///
    /// Caster/launcher split, damage-type resolution and the deliberately-unflagged projectiles are all
    /// identical to SpellbladeAbility; see that file's doc comment for the reasoning behind each.
    /// </summary>
    public class RunebladeAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Runeblade,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 2,
            Name = "runeblade",
            DisplayName = "Runeblade",
            Description = "Your landed light weapon hits have a 15% chance to cast a Blast war spell at the " +
                          "target, matching the damage type of the swing - one projectile on your target and " +
                          "the rest cleaving through a 90 degree cone. Rank raises the spell level cap " +
                          "(III / V / the Incantation), not the chance; the level you actually throw is also " +
                          "limited by your War Magic. Higher Magic Item Tinkering increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.MagicItemTinkering,
        };

        /// <summary>
        /// The proc chance: a flat base, plus the Magic Item Tinkering rider, plus the current Spellsurge
        /// stack bonus, plus the Runeblade equipment mod. Pure for testability. Returns 0 for rank &lt;= 0.
        /// No rank term - rank buys spell level, not chance.
        ///
        /// <paramref name="gearModChance"/> is the RUNEBLADE equipment mod (EquipmentModId.Runeblade), a
        /// MACHINERY mod added OUTSIDE the affinity clamp - see SpellbladeAbility.Chance for why that
        /// placement is load-bearing. Last and defaulted to 0, so an unmodded build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double magicItemTinkerFraction, double spellsurgeBonus = 0.0, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var rider = Math.Max(0.0, magicItemTinkerFraction);

            // Affinity rider capped - see SpellbladeAbility.Chance for why an uncapped rider saturates
            // the proc to 100% on any character with an inflated source skill. A cap of 0 means uncapped.
            // The gear term below sits OUTSIDE this clamp, for the reason recorded in that same comment.
            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return (float)Math.Max(0.0, chanceBase + rider + Math.Max(0.0, spellsurgeBonus) + Math.Max(0.0, gearModChance));
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // Light weapons only - one of the three war procs. Gated on the weapon that landed this hit,
            // not the current attack skill (SpellswordSpellTables.IsLightWeaponHit - offhand dual-wield).
            if (!SpellswordSpellTables.IsLightWeaponHit(attacker, damageEvent.Weapon))
                return;

            // The DEALT damage type only. A combined Pierce|Slash weapon procs whichever type the swing
            // actually landed; DamageEvent.DamageType already carries that resolution.
            var damageType = damageEvent.DamageType;

            var magicItemTinker = attacker.GetClassAbilityScaling(Skill.MagicItemTinkering,
                PropertyManager.GetDouble("class_ability_runeblade_magicitemtink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_runeblade_magicitemtink_per_spec").Item) * 0.01;

            // Runeblade equipment mod (MACHINERY): +pp on this ability's own proc roll. Ownership is proven
            // by the dispatch itself - only LEARNED IOutgoingDamageAbility handlers are in the hook cache.
            var chance = Chance(rank,
                PropertyManager.GetDouble("class_ability_runeblade_chance").Item,
                magicItemTinker,
                attacker.GetSpellsurgeProcChanceBonus(),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                attacker.GetEquippedModValue(EquipmentModId.Runeblade));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BlastByType,
                SpellswordSpellTables.BlastLevels,
                damageType,
                attacker.GetCreatureSkill(Skill.WarMagic).Current,
                rank,
                PropertyManager.GetDouble("class_ability_spellsword_level_skill_scale").Item);

            if (spellId == null)
                return;

            var spell = new Spell(spellId.Value);

            // Player as caster, wielded weapon as launcher, fromProc to suppress item procs. Projectiles are
            // deliberately left unflagged (design Q10) so Echo Cast and Elemental Rend can fire off them;
            // no recursion is possible because the trigger is a landed melee hit, never a spell hit.
            // Wrapped in CastClassAbilityProc so the launched projectiles carry
            // SpellProjectile.IsClassAbilityProc - the signal Cascade gates on. See SpellbladeAbility for
            // why fromProc alone is not that signal, and note Cascade fails CLOSED without this wrapper.
            attacker.CastClassAbilityProc(() =>
                attacker.CreateSpellProjectiles(spell, target, damageEvent.Weapon, false, fromProc: true));

            // A landed war proc feeds Spellsurge (design section 5e).
            attacker.AddSpellsurgeStack();
        }

        /// <summary>
        /// Mirrors the proc-chance terms fed into Chance() above. Skill is the flat base chance and is
        /// RANK-INVARIANT (rank buys spell level, not chance - see the class doc comment), so it does not
        /// multiply by rank; it is 0 only when the ability is unowned. Affinity carries the RAW
        /// (pre-clamp) Magic Item Tinkering rider, Effective uses the clamped one. Gear is the RUNEBLADE
        /// mod, added to Effective OUTSIDE the clamp exactly as Chance() adds it.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_runeblade_chance").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase;

            var rawRider = Math.Max(0.0, player.GetClassAbilityScaling(Skill.MagicItemTinkering,
                PropertyManager.GetDouble("class_ability_runeblade_magicitemtink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_runeblade_magicitemtink_per_spec").Item) * 0.01);

            var clampedRider = affinityCap > 0.0 ? Math.Min(rawRider, affinityCap) : rawRider;
            var capBit = affinityCap > 0.0 && rawRider > affinityCap;

            // Rank-gated like skillChance above - a machinery mod reports nothing without its ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Runeblade) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = rawRider * 100.0;
            var gear = gearChance * 100.0;
            var effective = skill + clampedRider * 100.0 + gear;

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
