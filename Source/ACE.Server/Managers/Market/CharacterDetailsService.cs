using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity.Actions;
using ACE.Server.Physics.Common;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>A details read's answer. Error None means 200 with <see cref="Details"/>.</summary>
    public sealed class CharacterDetailsReadResult
    {
        public CharacterDetailsReadResult(MarketError error, CharacterDetails details)
        {
            Error = error;
            Details = details;
        }

        public MarketError Error { get; }
        public CharacterDetails Details { get; }
    }

    /// <summary>
    /// What the details route needs from the running server, behind a seam so the route tests need no live Player.
    /// The route has already checked the key, the session, the switch and the roster.
    /// </summary>
    public interface ICharacterDetailsService
    {
        /// <summary>The snapshot of an ONLINE character; character_offline without loading anything otherwise.</summary>
        CharacterDetailsReadResult Read(uint accountId, uint characterGuid);
    }

    /// <summary>
    /// The live service, modelled on the ONLINE branch of LiveClassAbilityPanelService.Read: the work runs on the
    /// WorldManager action queue through <see cref="AdminWorldCall"/>, waited on for at most the panel's world timeout.
    /// Online only (owner ruling): no offline path, no shard read.
    /// </summary>
    internal sealed class LiveCharacterDetailsService : ICharacterDetailsService
    {
        internal const string DetailsReadLogPrefix = "[MARKET][DETAILS] a queued character details read";

        private readonly Action<IAction> enqueueWorld;
        private readonly TimeSpan worldTimeout;

        public LiveCharacterDetailsService(Action<IAction> enqueueWorld, int worldTimeoutMs)
        {
            this.enqueueWorld = enqueueWorld ?? throw new ArgumentNullException(nameof(enqueueWorld));
            worldTimeout = TimeSpan.FromMilliseconds(Math.Max(1, worldTimeoutMs));
        }

        public CharacterDetailsReadResult Read(uint accountId, uint characterGuid)
        {
            // the cheap pre-check: an offline character is answered without a world visit and without any load
            if (PlayerManager.GetOnlinePlayer(characterGuid) == null)
                return new CharacterDetailsReadResult(MarketError.CharacterOffline, null);

            var status = AdminWorldCall.Run(enqueueWorld, () =>
            {
                // the panel's own eligibility rule: online in this process, on this account, not logging out
                var player = LiveClassAbilityPanelService.FindEligibleForDetails(accountId, characterGuid);

                if (player == null)
                    return null;

                var inputs = CharacterDetailsReader.Read(player);

                return CharacterDetailsProjector.Build(inputs, ThreadBaseline.Live(inputs.Level));
            }, worldTimeout, out CharacterDetails details, DetailsReadLogPrefix);

            if (status == AdminWorldCall.Status.TimedOut)
                return new CharacterDetailsReadResult(MarketError.Timeout, null);

            if (status == AdminWorldCall.Status.Faulted)
                return new CharacterDetailsReadResult(MarketError.ServerError, null);

            return details == null
                ? new CharacterDetailsReadResult(MarketError.CharacterOffline, null)
                : new CharacterDetailsReadResult(MarketError.None, details);
        }
    }

    /// <summary>
    /// World thread. Reads <see cref="CharacterDetailsInputs"/> off a live, in-world Player. NON-MUTATING: every
    /// combat-stance value goes through the pure stance parameter (the weapon modifiers, GetEffectiveDefenseSkill,
    /// GetEffectiveMagicDefense, GetEffectiveAttackSkill) and the accuracy bar through GetAccuracyMod(weapon, bar); the
    /// Player's CombatMode, AccuracyLevel and hands are never touched (SimulatorStance / PlayerLoadout.With are for
    /// objects outside the world only). Roll-time conditionals (Intimidate, Overpowering Intent) are not modelled.
    /// </summary>
    internal static class CharacterDetailsReader
    {
        private static readonly Skill[] MagicSchools = { Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic, Skill.CreatureEnchantment };

        private static readonly DamageType[] SpellDamageTypes =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire, DamageType.Cold, DamageType.Acid,
            DamageType.Electric, DamageType.Nether, DamageType.Health,
        };

        public static CharacterDetailsInputs Read(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            // ALWAYS a combat stance, even in peace mode (owner ruling): the stance the character's wield implies.
            var stance = ProfileBuilder.DefenderCombatStance(player);
            var level = player.Level ?? 1;

            var magicSkills = new Dictionary<Skill, uint>();
            foreach (var school in MagicSchools)
            {
                var skill = player.GetCreatureSkill(school);
                if (skill.AdvancementClass >= SkillAdvancementClass.Trained)
                    magicSkills[school] = skill.Current;
            }

            uint weaponAttack = 0;
            uint[] byBar = null;
            var weaponSkillId = Skill.None;

            if (stance != CombatMode.Magic)
            {
                weaponSkillId = player.GetCurrentWeaponSkill(mainHandOnly: true);
                weaponAttack = player.GetEffectiveAttackSkill(stance, CharacterDetailsProjector.MissileAccuracyBar, mainHandOnly: true);

                if (stance == CombatMode.Missile)
                {
                    byBar = new uint[101];
                    for (var percent = 0; percent <= 100; percent++)
                        byBar[percent] = player.GetEffectiveAttackSkill(stance, percent / 100.0f, mainHandOnly: true);
                }
            }

            // The held weapon's crit chance and crit damage multiplier. Physical: the weapon DamageEvent passes for this stance
            // (PlayerAttackerSpecs' choice) and the main-hand skill. Magic: the wielded caster, and the higher of War and Void (the
            // skill only feeds the Critical Strike imbue).
            float heldCritPre;
            float heldCritDamageMod;
            float? offCritPre = null;
            float? offCritDamageMod = null;
            uint? offAttack = null;
            var offAttackSkill = Skill.None;

            if (stance == CombatMode.Magic)
            {
                var wand = player.GetEquippedWand();
                var war = player.GetCreatureSkill(Skill.WarMagic);
                var voidSkill = player.GetCreatureSkill(Skill.VoidMagic);
                var magicSkill = voidSkill.Current > war.Current ? voidSkill : war;

                heldCritPre = WorldObject.GetWeaponMagicCritFrequencyPreResist(wand, player, magicSkill, null);
                heldCritDamageMod = AttackerTerms.MagicCritDamageMod(wand, player, magicSkill, null);
            }
            else
            {
                var weapon = stance == CombatMode.Missile
                    ? player.GetEquippedMissileLauncher() ?? player.GetMissileAmmo()
                    : player.GetEquippedMeleeWeapon(forceMainHand: true);
                var weaponSkill = player.GetCreatureSkill(weaponSkillId);

                heldCritPre = WorldObject.GetWeaponCriticalChancePreResist(weapon, player, weaponSkill, null);
                heldCritDamageMod = AttackerTerms.PhysicalCritDamageMod(weapon, player, weaponSkill, null);

                // Dual wield: the off-hand weapon swings on the alternate attack, at ITS OWN skill capped at Dual Wield (the skill
                // GetEffectiveOffHandAttackSkill reports, which a live off-hand swing reads through GetCurrentWeaponSkill).
                var offWeapon = stance == CombatMode.Melee ? player.GetDualWieldWeapon() : null;

                if (offWeapon != null)
                {
                    offAttack = player.GetEffectiveOffHandAttackSkill(stance, out offAttackSkill);

                    var offSkill = player.GetCreatureSkill(offAttackSkill);

                    offCritPre = WorldObject.GetWeaponCriticalChancePreResist(offWeapon, player, offSkill, null);
                    offCritDamageMod = AttackerTerms.PhysicalCritDamageMod(offWeapon, player, offSkill, null);
                }
            }

            // Spell damage taken: the engine's own resistance reads for a monster's spell (no attacker, so no hollow and no
            // rend) and the magic absorb in this stance for each facing - SpellProjectile.AbsorbModFor, never the projectile's angle.
            var spellResistance = new Dictionary<DamageType, float>();
            foreach (var type in SpellDamageTypes)
                spellResistance[type] = (float)Math.Max(0.0, player.GetResistanceMod(Creature.GetResistanceType(type), null, null, 1.0f));

            var spellAbsorbFront = SpellProjectile.AbsorbModFor(player, stance, withinShieldArc: true);
            var spellAbsorbBack = SpellProjectile.AbsorbModFor(player, stance, withinShieldArc: false);

            return new CharacterDetailsInputs
            {
                CharacterGuid = player.Guid.Full,
                Level = level,
                Stance = stance,
                WeaponSkill = weaponSkillId,
                WeaponAttackSkill = weaponAttack,
                OffWeaponSkill = offAttackSkill,
                OffWeaponAttackSkill = offAttack,
                MissileAttackSkillByBarPercent = byBar,
                MagicSkills = magicSkills,
                MeleeDefense = player.GetEffectiveDefenseSkill(CombatType.Melee, stance),
                MissileDefense = player.GetEffectiveDefenseSkill(CombatType.Missile, stance),
                MagicDefense = player.GetEffectiveMagicDefense(stance),
                HeldCritPreResist = heldCritPre,
                OffCritPreResist = offCritPre,
                HeldCritDamageMod = heldCritDamageMod,
                OffCritDamageMod = offCritDamageMod,
                KillerInstinctCritBonus = (float)player.GetKillerInstinctCritChanceBonus(),
                Profile = ProfileBuilder.ReadDefenderProfile(player, stance),
                SpellResistanceMod = spellResistance,
                SpellAbsorbFront = spellAbsorbFront,
                SpellAbsorbBack = spellAbsorbBack,
                HarmResistanceMod = (float)player.GetResistanceMod(ResistanceType.HealthDrain, null, null, 1.0f),
                DrainResistanceMod = (float)player.GetHealthDrainResistanceOnly(),
                DotNetherResistanceMod = player.GetResistanceMod(DamageType.Nether, null, null),
                DotUntypedResistanceMod = player.GetResistanceMod(DamageType.Undef, null, null),
                DotResistRating = player.GetDotResistanceRating(),
                Fortitude = player.GetFortitude(null),
                DamageRating = player.GetDamageRating(),
                CritDamageRating = player.GetCritDamageRating(),
                CritRating = player.GetCritRating(),
                BurdenMod = player.GetBurdenMod(),
            };
        }
    }
}
