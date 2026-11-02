using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T3 (Berserker pass 2026-10-24, owner ruling): when ENEMY damage - melee, missile, spell, or
    /// an enemy's DoT - would kill the player, they are left at 1 HP instead, become immune to incoming damage
    /// for 5 seconds, and deal x1.5 damage while the rage lasts. Cooldown 90 / 60 / 30 seconds by rank. No
    /// affinity. Hazards (HazardContext.HitPlayers, and the Marae Lassel digsite boss mechanics) never trigger
    /// it and are not blocked by the immunity, matching "hazards bypass all wards" (owner rulings R3 and
    /// 2026-10-19).
    ///
    /// TWO PRE-WRITE HANDLERS, ONE ABILITY. The immunity has to run BEFORE every absorb pool (so a blocked hit
    /// never drains a ward or charges a barrier), and the trigger has to run AFTER every mitigation and after
    /// Soul Jump (it decides on the final figure). One handler cannot sit in two bands, so the immunity is the
    /// companion <see cref="BerserkRageImmunity"/>, which shares this Definition and is appended to
    /// ClassAbilityRegistry.PreWriteDamageAbilities only (it is not in Handlers, whose id map would reject a
    /// second handler with the same id). The per-player hook cache filters both on the BerserkRage rank.
    ///
    /// SOUL JUMP ORDERING: the trigger is at DamageMitigationOrder.LastResortDeathSave (950), after Soul
    /// Jump's DeathSave (900). A Soul Jump save returns damage that leaves the summoner on its restore line,
    /// so by the time this runs the blow is no longer lethal and the rage does not fire or start its cooldown.
    ///
    /// COOLDOWN SURVIVES RELOG: it lives in Player's static berserkRageReadyTimes map keyed by character guid
    /// (owner ruling: no new persisted property). A process restart clears it.
    ///
    /// PvE ONLY: the trigger needs the dispatch's filtered Attacker (a live non-player creature) or, for a DoT
    /// tick, a non-zero enemy-creature share. A killing blow from another player, from the player themselves,
    /// from an unattributed source, or from a hazard spends nothing. The outgoing x1.5 rides the PvE-only
    /// outgoing-damage dispatch and the magic sites' not-a-Player-target gates.
    /// </summary>
    public class BerserkRageAbility : IClassAbility, IPreWriteDamageAbility, IOutgoingDamageAbility, IAbilityReadout
    {
        public const string DurationKey = "class_ability_berserkrage_duration_seconds";
        public const string DamageMultiplierKey = "class_ability_berserkrage_damage_multiplier";
        public const string CooldownKeyPrefix = "class_ability_berserkrage_cooldown_seconds_r";

        /// <summary>The companion immunity handler, appended to the pre-write bucket by ClassAbilityRegistry.</summary>
        public static readonly BerserkRageImmunity Immunity = new BerserkRageImmunity();

        public static readonly ClassAbilityDefinition SharedDefinition = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.BerserkRage,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 3,
            Name = "berserk_rage",
            DisplayName = "Berserk Rage",
            Description = "When a monster's attack, spell or damage over time would kill you, you are left at 1 health " +
                          "instead, become immune to all incoming damage for 5 seconds and deal 50% more damage while " +
                          "the rage lasts. Once every 90 / 60 / 30 seconds by rank. Environmental hazards never " +
                          "trigger it and are not blocked by it.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
        };

        public ClassAbilityDefinition Definition => SharedDefinition;

        /// <summary>The per-rank cooldown tunable key (rank clamped to 1-3).</summary>
        public static string CooldownKey(int rank) => CooldownKeyPrefix + Math.Clamp(rank, 1, 3);

        /// <summary>
        /// TRUE when the hit qualifies as ENEMY damage for the trigger: never a hazard; either a filtered
        /// enemy-creature attacker, or a DoT tick with an enemy-creature share. Pure, for tests.
        /// </summary>
        public static bool IsEnemyDamage(bool isHazard, bool hasEnemyAttacker, bool isDamageOverTime, uint enemyCreatureSpellDamage)
        {
            if (isHazard)
                return false;

            return hasEnemyAttacker || (isDamageOverTime && enemyCreatureSpellDamage > 0);
        }

        /// <summary>
        /// The damage to write when the rage triggers: whatever leaves exactly 1 health. Returns the incoming
        /// damage untouched when it is not lethal. Pure, for tests.
        /// </summary>
        public static uint SaveDamage(uint incomingDamage, uint currentHealth)
        {
            if (currentHealth == 0 || incomingDamage < currentHealth)
                return incomingDamage;

            return currentHealth - 1;
        }

        /// <summary>
        /// LastResortDeathSave band - after Soul Jump, after every mitigation. See the type doc.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.LastResortDeathSave;

        /// <summary>FALSE: the trigger is derived from the live rank, and the window lives on Player.</summary>
        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// RANK-GATED trigger. Damage here is the post-mitigation figure, never clamped to Health, so
        /// `Damage >= Health.Current` is a genuine lethality test.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (defender == null || rank <= 0 || context.Damage == 0)
                return;

            if (!IsEnemyDamage(context.IsHazard, context.Attacker != null, context.IsDamageOverTime, context.SpellDamageFromEnemyCreature))
                return;

            var health = defender.Health.Current;

            if (context.Damage < health)
                return;

            var now = Time.GetUnixTime();

            if (!defender.IsBerserkRageReady(now))
                return;

            context.Damage = SaveDamage(context.Damage, health);

            defender.StartBerserkRage(now,
                PropertyManager.GetDouble(DurationKey).Item,
                PropertyManager.GetDouble(CooldownKey(rank)).Item);
        }

        /// <summary>
        /// Weapon-hit leg of the x1.5 (the outgoing dispatch is monsters-only by construction). The spell legs
        /// read Player.GetBerserkRageDamageMod beside Hemomancy at the magic damage sites.
        /// Registered AHEAD of Bloodlust (owner ruling 2026-10-24), so the enraged x1.5 feeds Bloodlust's lifesteal.
        /// No spell lifesteal reads it: the Drain/Transfer path (Drain heals, Crimson Harvest, Transfusion) carries no
        /// Rage leg.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || rank <= 0)
                return;

            var mod = attacker.GetBerserkRageDamageMod();

            if (mod > 1.0f)
                damageEvent.Damage *= mod;
        }

        /// <summary>
        /// Primary: the COOLDOWN for this rank (the one rank-scaled magnitude). Secondaries: the window
        /// duration and the enraged damage bonus. No affinity, no gear term.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var cooldown = rank <= 0 ? 0.0 : PropertyManager.GetDouble(CooldownKey(rank)).Item;
            var duration = PropertyManager.GetDouble(DurationKey).Item;
            var bonus = (PropertyManager.GetDouble(DamageMultiplierKey).Item - 1.0) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = cooldown,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = cooldown,
                Unit = "s",
                Label = "cooldown",
                Per = null,
                CapNote = null,
                Secondary = new List<ClassAbilityReadout>
                {
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = duration,
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = duration,
                        Unit = "s",
                        Label = "immune",
                        Per = null,
                        CapNote = null,
                    },
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = bonus,
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = bonus,
                        Unit = "%",
                        Prefix = "+",
                        Label = "dmg while raging",
                        Per = null,
                        CapNote = null,
                    },
                },
            };
        }
    }

    /// <summary>
    /// Berserk Rage's immunity window, the companion pre-write handler (see BerserkRageAbility's type doc for
    /// why it is a second handler). Shares BerserkRageAbility's Definition, so the hook cache filters it on the
    /// same rank. Immunity band: after the observers (Adrenaline and Kinetic Charge still see the blocked hit
    /// as having reached the player), ahead of every absorb pool (a blocked hit drains nothing).
    ///
    /// WHAT IT BLOCKS: every hit while the window is open EXCEPT a hazard hit (hazards bypass all wards) and a
    /// hit whose source is a Player (PvP or self - class abilities are PvE only). A DoT tick carries no source
    /// and may mix casters, so for a tick only the enemy-creature share is removed and any remainder lands.
    /// </summary>
    public class BerserkRageImmunity : IPreWriteDamageAbility
    {
        public ClassAbilityDefinition Definition => BerserkRageAbility.SharedDefinition;

        public int MitigationOrder => DamageMitigationOrder.Immunity;

        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// The damage left after the immunity for one hit while raging. Pure, for tests.
        /// </summary>
        public static uint DamageAfterImmunity(uint damage, bool isHazard, bool sourceIsPlayer, bool isDamageOverTime, uint enemyCreatureSpellDamage)
        {
            if (isHazard || sourceIsPlayer)
                return damage;

            if (isDamageOverTime)
                return enemyCreatureSpellDamage >= damage ? 0u : damage - enemyCreatureSpellDamage;

            return 0u;
        }

        /// <summary>RANK-GATED (the cache filter); reads the live window on the defender.</summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (defender == null || rank <= 0 || context.Damage == 0 || !defender.IsBerserkRaging)
                return;

            context.Damage = DamageAfterImmunity(context.Damage, context.IsHazard, context.Source is Player,
                context.IsDamageOverTime, context.SpellDamageFromEnemyCreature);
        }
    }
}
