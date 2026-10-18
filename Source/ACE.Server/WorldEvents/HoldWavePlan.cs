using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// What one Hold wave does differently (protect.specialWaves), built by WorldEvent.SpawnWave on the world
    /// thread and handed to the spawner, which applies it to each creature before it enters the world. An
    /// ordinary wave carries no plan (null) and spawns exactly as before.
    /// </summary>
    public sealed class HoldWavePlan
    {
        public HoldWaveType Type { get; set; } = HoldWaveType.Normal;

        /// <summary>The 1-based wave number, for logs.</summary>
        public int WaveNumber { get; set; }

        /// <summary>Breach: the one target every seeker in the wave goes for (0 = none).</summary>
        public uint BreachTargetGuid { get; set; }

        /// <summary>Breach: which side of the anchor that target stands on ("east").</summary>
        public string BreachSide { get; set; }

        /// <summary>Runners: how many of the wave's monsters are kept (null = all).</summary>
        public int? KeepCount { get; set; }

        /// <summary>Runners: health as a share of normal (1 = unchanged).</summary>
        public double HealthMult { get; set; } = 1.0;

        /// <summary>Runners: run speed multiplier (1 = unchanged).</summary>
        public float RunRateMult { get; set; } = 1.0f;

        /// <summary>Runners: attack speed multiplier (1 = unchanged). Also speeds their time-based damage to the targets.</summary>
        public float AttackSpeedMult { get; set; } = 1.0f;

        /// <summary>Ranged: the distance (m) ranged and caster monsters stop at from their target (0 = stock AI).</summary>
        public float HoldDistance { get; set; }

        /// <summary>Ranged: the wave's trash wcids replaced by ranged and caster family members (null = unchanged).</summary>
        public IReadOnlyList<uint> ReplacedTrash { get; set; }

        /// <summary>Hunters: how many non-seekers hunt players.</summary>
        public int Hunters { get; set; }

        /// <summary>Finale: the family champion leading the wave (0 = none could be picked).</summary>
        public uint ChampionWcid { get; set; }

        public bool ChampionSynthetic { get; set; }
        public int ChampionUplift { get; set; }
        public int ChampionSlotLevel { get; set; }

        /// <summary>Finale: the champion's display name, for the announcement.</summary>
        public string ChampionName { get; set; }

        /// <summary>Finale: the champion's index in the spawn batch, set by the spawner (-1 = none).</summary>
        public int ChampionEntryIndex { get; set; } = -1;

        /// <summary>Mixed: the ranged carry the run's ward (protect.ward).</summary>
        public bool Warded { get; set; }

        /// <summary>Mixed: one role per entry of the batch, in spawn order (null for any other wave).</summary>
        public HoldRole[] Roles { get; set; }

        /// <summary>Mixed / breach: the breach target's name, for the announcement.</summary>
        public string BreachTargetName { get; set; }

        /// <summary>Finale: this batch is the boss alone; the rest of the wave follows after the lead time.</summary>
        public bool FinaleBossOnly { get; set; }

        /// <summary>Finale: this batch is the rest of the final wave, sent after the boss.</summary>
        public bool FinaleEscort { get; set; }

        /// <summary>Mixed: how many of each role, for the log ("breach=2 runners=2 ...").</summary>
        public string RoleSummary()
        {
            if (Roles == null)
                return "";

            var counts = new int[System.Enum.GetValues(typeof(HoldRole)).Length];

            foreach (var r in Roles)
                counts[(int)r]++;

            return $"breach={counts[(int)HoldRole.Breach]} runners={counts[(int)HoldRole.Runner]} ranged={counts[(int)HoldRole.Ranged]} " +
                   $"hunters={counts[(int)HoldRole.Hunter]} seekers={counts[(int)HoldRole.Seeker]} corrupted={counts[(int)HoldRole.Corrupted]}";
        }

        public override string ToString()
        {
            switch (Type)
            {
                case HoldWaveType.Breach: return $"type=breach target=0x{BreachTargetGuid:X8} side={BreachSide}";
                case HoldWaveType.Runners: return $"type=runners keep={KeepCount} health=x{HealthMult:F2} speed=x{RunRateMult:F2} attackSpeed=x{AttackSpeedMult:F2} seekers=all";
                case HoldWaveType.Ranged: return $"type=ranged replaced={(ReplacedTrash == null ? "none" : ReplacedTrash.Count.ToString())} holdDistance={HoldDistance:0.#}";
                case HoldWaveType.Hunters: return $"type=hunters hunters={Hunters}";
                case HoldWaveType.Finale: return $"type=finale champion={(ChampionWcid == 0 ? "none" : $"{ChampionWcid} {ChampionName}")}";
                case HoldWaveType.Mixed: return $"type=mixed breach=0x{BreachTargetGuid:X8} ({BreachTargetName}) side={BreachSide} {RoleSummary()} " +
                                                $"runnerSpeed=x{RunRateMult:F2} runnerAttack=x{AttackSpeedMult:F2} runnerHealth=x{HealthMult:F2} holdDistance={HoldDistance:0.#}";
                default: return "type=normal";
            }
        }
    }
}
