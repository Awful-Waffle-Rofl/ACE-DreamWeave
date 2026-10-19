using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The role a Hold wave monster is tuned by (world_events_hold_&lt;role&gt;_*). Melee covers the breach squad, the
    /// seekers and the hunters; Caster is a ranged monster whose ranged attack is spells (no launcher or thrown weapon).
    /// </summary>
    public enum HoldDamageRole { None, Melee, Runner, Ranged, Caster, Corrupted }

    public static partial class HoldSettings
    {
        /// <summary>The role names as they appear in setting names, in order.</summary>
        public static readonly string[] RoleNames = { "melee", "runner", "ranged", "caster", "corrupted" };

        public static string RoleName(HoldDamageRole role) => role switch
        {
            HoldDamageRole.Melee => "melee",
            HoldDamageRole.Runner => "runner",
            HoldDamageRole.Ranged => "ranged",
            HoldDamageRole.Caster => "caster",
            HoldDamageRole.Corrupted => "corrupted",
            _ => null
        };

        public const double RoleMultMin = 0, RoleMultMax = 5;
        public const double RoleHealthMin = 0.1, RoleHealthMax = 10;
        public const double RoleAttackSpeedMin = 1, RoleAttackSpeedMax = 3;
        public const int RoleLevelOffsetMin = -50, RoleLevelOffsetMax = 50;

        static partial void RegisterMonsters(List<HoldSetting> r)
        {
            // ---- the wave mix (goals.json protect.waveMix) ----
            r.Add(Num(GroupMonsters, HoldKeys.ShareBreach, 0.15, 0, 1, HoldApplies.NextWave, "protect.waveMix.breach", false,
                "Share of each mixed wave that is the breach squad (melee, all on one knight).", g => F(g.Protect?.WaveMix?.Breach ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.ShareRunners, 0.15, 0, 1, HoldApplies.NextWave, "protect.waveMix.runners", false,
                "Share of each mixed wave that is runners.", g => F(g.Protect?.WaveMix?.Runners ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.ShareRanged, 0.3, 0, 1, HoldApplies.NextWave, "protect.waveMix.ranged", false,
                "Share of each mixed wave that is ranged (archers and casters).", g => F(g.Protect?.WaveMix?.Ranged ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.ShareHunters, 0.1, 0, 1, HoldApplies.NextWave, "protect.waveMix.hunters", false,
                "Share of each mixed wave that hunts players (melee).", g => F(g.Protect?.WaveMix?.Hunters ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.ShareSeekers, 0.2, 0, 1, HoldApplies.NextWave, "protect.waveMix.seekers", false,
                "Share of each mixed wave that goes for any knight (melee).", g => F(g.Protect?.WaveMix?.Seekers ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.ShareCorrupted, 0.1, 0, 1, HoldApplies.NextWave, "protect.waveMix.corrupted", false,
                "Share of each mixed wave that is corrupted (when assaults are off; see corrupted_assault_every_waves).", g => F(g.Protect?.WaveMix?.Corrupted ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.MinBreach, 0, 0, 10, HoldApplies.NextWave, "protect.waveMix.minBreach", false,
                "At least this many in the breach squad.", g => F(g.Protect?.WaveMix?.MinBreach ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.MinRanged, 3, 0, 20, HoldApplies.NextWave, "protect.waveMix.minRanged", false,
                "At least this many ranged.", g => F(g.Protect?.WaveMix?.MinRanged ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.MinCorrupted, 1, 0, 5, HoldApplies.NextWave, "protect.waveMix.minCorrupted", false,
                "At least this many corrupted (share mode).", g => F(g.Protect?.WaveMix?.MinCorrupted ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.MaxCorrupted, 2, 0, 10, HoldApplies.NextWave, "protect.waveMix.maxCorrupted", false,
                "At most this many corrupted (share mode).", g => F(g.Protect?.WaveMix?.MaxCorrupted ?? 0)));

            // ---- runner, ranged and corrupted values that already live in goals.json ----
            r.Add(Num(GroupMonsters, HoldKeys.RunnerHealthMult, 0.6, 0.1, 1, HoldApplies.NextWave, "protect.specialWaves[type=runners].healthMult", false,
                "Runner health as a share of normal.", g => F(g.Protect?.SpecialWave(HoldWaveType.Runners)?.HealthMult ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.RunnerSpeedMult, 1.5, 1, 3, HoldApplies.NextWave, "protect.specialWaves[type=runners].speedMult", false,
                "Runner movement speed.", g => F(g.Protect?.SpecialWave(HoldWaveType.Runners)?.SpeedMult ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.RunnerAttackSpeedMult, 1.5, 1, 3, HoldApplies.NextWave, "protect.specialWaves[type=runners].attackSpeedMult", false,
                "Runner swing speed (and how fast its hits wear a knight down).", g => F(g.Protect?.SpecialWave(HoldWaveType.Runners)?.AttackSpeedMult ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.RangedKeepDistance, 18, 5, 40, HoldApplies.NextWave, "protect.specialWaves[type=ranged].holdDistance", false,
                "Metres ranged and caster monsters stop at from their target.", g => F(g.Protect?.SpecialWave(HoldWaveType.Ranged)?.HoldDistance ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.CorruptedHealthMult, 1.5, 1, 5, HoldApplies.NextSpawn, "protect.corrupted.healthMult", false,
                "Corrupted health multiplier.", g => F(g.Protect?.EffectiveCorrupted.HealthMult ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.CorruptedSelfMult, 2, 1, 10, HoldApplies.Live, "protect.corrupted.selfMult", false,
                "How much harder a corrupted monster hits a knight (still under min_death_seconds).", g => F(g.Protect?.EffectiveCorrupted.SelfMult ?? 0)));
            r.Add(Num(GroupMonsters, HoldKeys.CorruptedEmpowerMult, 1.5, 1, 5, HoldApplies.Live, "protect.corrupted.empowerMult", false,
                "How much harder monsters near a corrupted one hit a knight.", g => F(g.Protect?.EffectiveCorrupted.EmpowerMult ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.CorruptedAssaultEveryWaves, 0, 0, 10, HoldApplies.NextWave, "protect.corrupted.assaultEveryWaves", false,
                "Corrupted assault every this many waves (0 = corrupted come by share_corrupted instead).", g => F(g.Protect?.EffectiveCorrupted.AssaultEveryWaves ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.CorruptedAssaultFromWave, 2, 1, 10, HoldApplies.NextWave, "protect.corrupted.assaultFromWave", false,
                "The first assault wave.", g => F(g.Protect?.EffectiveCorrupted.AssaultFromWave ?? 0)));
            r.Add(Int(GroupMonsters, HoldKeys.CorruptedAssaultCount, 2, 1, 5, HoldApplies.NextWave, "protect.corrupted.assaultCount", false,
                "Corrupted monsters per assault (each sent at one knight).", g => F(g.Protect?.EffectiveCorrupted.AssaultCount ?? 0)));
            r.Add(TextSetting(GroupMonsters, HoldKeys.CorruptedTint, "none", HoldApplies.NextSpawn, "protect.corrupted.tintPalette", false,
                "The corrupted color: a client palette (0x04001381 cyan, 0x04001378 crimson, 0x0400137D violet, 0x0400137A green) or none.",
                g => g.Protect?.EffectiveCorrupted.TintPalette));

            // ---- new: per role ----
            foreach (var role in RoleNames)
            {
                // runner_health_mult and corrupted_health_mult are the goals.json values above; the rest are new.
                if (role != "runner" && role != "corrupted")
                    r.Add(Num(GroupMonsters, $"{role}_health_mult", 1, RoleHealthMin, RoleHealthMax, HoldApplies.NextSpawn, null, true,
                        $"{Capital(role)} monsters' health multiplier (1 = as today)."));

                r.Add(Num(GroupMonsters, $"{role}_player_damage_mult", 1, RoleMultMin, RoleMultMax, HoldApplies.Live, null, true,
                    $"Multiplies {role} monsters' damage to players (melee, missile and spells)."));

                r.Add(Num(GroupMonsters, $"{role}_npc_damage_mult", 1, RoleMultMin, RoleMultMax, HoldApplies.Live, null, true,
                    $"Multiplies {role} monsters' damage to the knights, before the min_death_seconds cap."));

                // runner_attack_speed_mult is the goals.json value above.
                if (role != "runner")
                    r.Add(Num(GroupMonsters, $"{role}_attack_speed_mult", 1, RoleAttackSpeedMin, RoleAttackSpeedMax, HoldApplies.NextSpawn, null, true,
                        $"{Capital(role)} monsters' melee swing speed (and how fast their hits wear a knight down)."));

                r.Add(Int(GroupMonsters, $"{role}_level_offset", 0, RoleLevelOffsetMin, RoleLevelOffsetMax, HoldApplies.NextWave, null, true,
                    $"{Capital(role)} slots of a mixed wave are drawn from the event's level band moved by this many levels (0 = as today; " +
                    "a slot with no member in the moved band keeps its monster)."));
            }

            r.Add(Num(GroupMonsters, HoldKeys.CasterShareOfRanged, 0.5, 0, 1, HoldApplies.NextWave, null, true,
                "Share of the ranged slots filled by casters when the family has both casters and archers. Not set: no preference (as today)."));
        }

        private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>The player damage multiplier for <paramref name="c"/> (1 when its role has none set). Live.</summary>
        public static double PlayerDamageMultFor(Creature c)
        {
            var name = RoleName(c?.WorldEventHoldRole ?? HoldDamageRole.None);

            return name == null ? BossPlayerDamageMult(c) : Number($"{name}_player_damage_mult", 1.0);
        }

        /// <summary>The knight damage multiplier for <paramref name="c"/> (1 when its role has none set). Live.</summary>
        public static double NpcDamageMultFor(Creature c)
        {
            var name = RoleName(c?.WorldEventHoldRole ?? HoldDamageRole.None);

            return name == null ? 1.0 : Number($"{name}_npc_damage_mult", 1.0);
        }

        /// <summary>The bosses' own player damage multipliers (finale, champion); 1 for anything else.</summary>
        private static double BossPlayerDamageMult(Creature c) => 1.0;

        /// <summary>The health multiplier for a new-role monster (melee, ranged, caster); null when not set (as today).</summary>
        public static double? RoleHealthMult(HoldDamageRole role)
        {
            if (role != HoldDamageRole.Melee && role != HoldDamageRole.Ranged && role != HoldDamageRole.Caster)
                return null;

            var key = $"{RoleName(role)}_health_mult";

            return IsSet(key) ? Number(key, 1.0) : (double?)null;
        }

        /// <summary>The attack speed for a role whose attack speed is a new setting (not runners); null when not set (as today).</summary>
        public static double? RoleAttackSpeedMult(HoldDamageRole role)
        {
            var name = RoleName(role);

            if (name == null || role == HoldDamageRole.Runner)
                return null;

            var key = $"{name}_attack_speed_mult";

            return IsSet(key) ? Number(key, 1.0) : (double?)null;
        }

        /// <summary>The level offset for a role (0 = none).</summary>
        public static int RoleLevelOffset(HoldDamageRole role)
        {
            var name = RoleName(role);

            return name == null ? 0 : Integer($"{name}_level_offset", 0);
        }

        /// <summary>The caster share of the ranged slots; null when not set (no preference, as today).</summary>
        public static double? CasterShareOfRanged() =>
            IsSet(HoldKeys.CasterShareOfRanged) ? Number(HoldKeys.CasterShareOfRanged, 0.5) : (double?)null;
    }
}
