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

        public override string ToString()
        {
            switch (Type)
            {
                case HoldWaveType.Breach: return $"type=breach target=0x{BreachTargetGuid:X8} side={BreachSide}";
                case HoldWaveType.Runners: return $"type=runners keep={KeepCount} health=x{HealthMult:F2} speed=x{RunRateMult:F2}";
                case HoldWaveType.Ranged: return $"type=ranged replaced={(ReplacedTrash == null ? "none" : ReplacedTrash.Count.ToString())}";
                case HoldWaveType.Hunters: return $"type=hunters hunters={Hunters}";
                case HoldWaveType.Finale: return $"type=finale champion={(ChampionWcid == 0 ? "none" : $"{ChampionWcid} {ChampionName}")}";
                default: return "type=normal";
            }
        }
    }
}
