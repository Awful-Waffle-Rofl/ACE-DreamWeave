using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T3 game-changer: a landed damaging spell projectile of ANY school has an 8/16/24% chance
    /// (by rank) to immediately re-cast itself at the same target - no mana, no wind-up. It was War Magic
    /// only until 2026-09-13, when the school test came off the shared spell-hit dispatch in
    /// SpellProjectile.OnCollideObject. The echo chance scales with Magic Item
    /// Tinkering (a finely tuned casting implement holds the echo). The recast reuses the normal
    /// projectile machinery (WorldObject.CreateSpellProjectiles), so it is a faithful copy of the
    /// original spell (all its projectiles, full damage, its own resist/crit rolls) and still benefits
    /// from Overchannel / Void Damage. Since 2026-09-21 (owner ruling: an echo is a full second cast with
    /// all modifiers, AOE included) it is flagged IsEchoCopy rather than IsClassAbilitySpawned, so Spell AOE
    /// radiates off it while it still never echoes again; fromProc keeps it from re-firing item procs.
    ///
    /// ISpellHitAbility: dispatched from SpellProjectile.OnCollideObject for every landed damaging-spell
    /// hit, any school; this handler self-restricts to a projectile that is neither an echo nor a Spell AOE
    /// child (no cascade) - see <see cref="CanEcho"/>.
    ///
    /// Magic Item Tinkering is MULTIPLICATIVE on this ability's OWN rank bonus (2026-09-12 overhaul), not
    /// an additive rider fed straight into EchoChance. EchoChance's fraction parameter stays additive in
    /// its own units, so OnSpellHit/GetReadout feed it the AMOUNT the multiplier adds
    /// (rankBonus * affinity - rankBonus) rather than the multiplier itself - mathematically identical to
    /// rankBonus * affinity + gear, just expressed through the pre-existing additive helper shape.
    /// </summary>
    public class EchoCastAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EchoCast,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "echocast",
            DisplayName = "Echo Cast",
            Description = "Your landed damaging spell projectiles, of any school, have an 8/16/24% chance " +
                          "(by rank) to immediately cast again at the same target for free - no mana, no " +
                          "wind-up. The echo is a full second cast with all your bonuses, and Spell AOE can " +
                          "spread it; an echo never echoes. Higher Magic Item Tinkering increases the chance.",
            MaxRank = 3,
            // premium reprice 2026-09-13: off the 1/2/3 escalator onto flat 2/rank. The buyout total is
            // unchanged at 6 - rank 3 got CHEAPER (3 -> 2) while ranks 1 and 2 got dearer.
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
            AffinitySkill = Skill.MagicItemTinkering,
        };

        /// <summary>
        /// The echo chance at a given rank: base + step*(rank-1) + the Magic Item Tinkering rider + the
        /// equipment-mod term. Pure for testability (mirrors the other reactive-chance handlers). Returns 0
        /// for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Echo Cast equipment mod, a MACHINERY mod: it amplifies
        /// the ability's own roll and stays behind the same rank check. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float EchoChance(int rank, double chanceBase, double chanceStep, double tinkerFraction, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + Math.Max(0.0, tinkerFraction) + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// TRUE when a landed projectile may roll for an echo: not an echo itself (<paramref name="isEchoCopy"/>
        /// - which also covers a Spell AOE child radiated off an echo, since the child inherits the flag) and
        /// not any other class-ability-spawned secondary copy (<paramref name="isClassAbilitySpawned"/> - a
        /// Spell AOE child). Pure so the recursion guard is unit-testable without a live projectile.
        /// </summary>
        public static bool CanEcho(bool isClassAbilitySpawned, bool isEchoCopy)
        {
            return !isClassAbilitySpawned && !isEchoCopy;
        }

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            // No cascade: an echo never echoes again, and neither does any class-ability-spawned copy.
            if (!CanEcho(projectile.IsClassAbilitySpawned, projectile.IsEchoCopy))
                return;

            // Don't echo at a corpse (the primary hit frequently kills the target) or a torn-down target.
            if (primaryTarget.IsDead || primaryTarget.PhysicsObj == null || caster.PhysicsObj == null)
                return;

            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            // Magic Item Tinkering (MULTIPLICATIVE, no-arg overload = the shared 0.12/0.17 rate pair).
            var affinity = caster.GetClassAbilityAffinityMultiplier(Skill.MagicItemTinkering);
            var tinker = rankBonus * affinity - rankBonus;

            // Echo Cast equipment mod (MACHINERY): +pp on this ability's own recast roll, OUTSIDE the
            // affinity multiply. Read inside the handler, which only runs for a caster who owns Echo Cast.
            var chance = EchoChance(rank, chanceBase, chanceStep, tinker,
                caster.GetEquippedModValue(EquipmentModId.EchoCast));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Re-cast the same spell at the same target: no mana, no wind-up. fromProc suppresses
            // item procs. The life-projectile basis goes in here too; this call does NOT go through
            // HandleCastSpell_Projectile, so it neither drains the caster's vital again nor re-resolves the
            // Blood Charge pool - both stay paid once, by the original cast. CreateSpellProjectiles snapshots
            // the player's CURRENT per-cast values onto the echo, which by now may belong to a later cast, so
            // StampEchoCopy then overwrites them with the parent's own launch-time snapshots.
            var echoes = caster.CreateSpellProjectiles(projectile.Spell, primaryTarget, projectile.ProjectileLauncher,
                projectile.IsWeaponSpell, fromProc: true, lifeProjectileDamage: projectile.LifeProjectileDamage, suppressPetAssist: true);

            foreach (var echo in echoes)
                StampEchoCopy(echo, projectile);
        }

        /// <summary>
        /// Stamps an echo with everything it inherits from the cast it copies. A FULL second cast (owner
        /// ruling 2026-09-21): the echo is deliberately NOT flagged IsClassAbilitySpawned, so every spell-hit
        /// handler treats it like a normal cast - Spell AOE radiates off it - and its damage runs the same
        /// CalculateDamage path (own crit roll, Overchannel / Void Damage via GetClassAbilitySpellDamageMod).
        ///
        ///  - IsEchoCopy: the one thing that differs. It stops the echo, and any AOE child radiated off it,
        ///    from echoing again, and it stops the echo granting a Blood Charge (see
        ///    SpellProjectile.GrantsLifeProjectileCharge).
        ///  - SpellweaveDamageMod: the PARENT's captured stamp. CreateSpellProjectiles stamped it from the live
        ///    player field, which by now may belong to a later cast.
        ///  - LifeProjectileDamage: the PARENT's damage basis (Martyr's Hecatomb, Curse of Raven Fury). That
        ///    basis is the health the ORIGINAL cast drained at cast time (WorldObject_Magic
        ///    .HandleCastSpell_Projectile); an echo never re-runs that path, so without this copy its basis was
        ///    0 and the echo landed for 0 (SpellProjectile.CalculateDamage: LifeProjectileDamage x
        ///    DamageRatio). The echo pays nothing for it - the drain happened once.
        ///  - LifeProjectileStamp: the PARENT's Blood Price multiplier, Blood Charge ramp or Exsanguinate burst,
        ///    and outcome, snapshotted when the parent launched. The live player fields belong to the caster's
        ///    most recent cast, so an echo reading them could take a burst multiplier and resistance-ignore
        ///    from a later Exsanguinate it never earned (PR #1271 review).
        ///
        /// Public and static so the inheritance rule is unit-testable on two plain projectiles.
        /// </summary>
        public static void StampEchoCopy(SpellProjectile echo, SpellProjectile parent)
        {
            if (echo == null || parent == null)
                return;

            echo.IsEchoCopy = true;
            SpellProjectile.CopyPerCastStamps(echo, parent);
        }

        /// <summary>
        /// Mirrors the terms fed into EchoChance() above (x100 for display) - the two must stay in step.
        /// EchoChance takes no affinity-cap parameter, so this readout never clamps the Magic Item
        /// Tinkering rider and CapNote is always null.
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective chance.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.MagicItemTinkering);

            var gearChance = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.EchoCast));

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            var gear = gearChance * 100.0;
            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "recast",
                Per = null,
                CapNote = null,
            };
        }
    }
}
