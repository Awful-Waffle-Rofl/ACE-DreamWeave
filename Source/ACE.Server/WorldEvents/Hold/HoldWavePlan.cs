using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// What one Hold wave does (its type, roles and targets), built on the world thread by
    /// <see cref="WorldEventHoldWavePlanner"/> and handed to <see cref="WorldEventSpawner.SpawnHoldWave"/>, which turns it into
    /// one <see cref="WorldEventHoldTag"/> per spawn entry. Unlike the contributor's plan, an ordinary wave of a Hold run has a
    /// plan too (Type Normal), so every Hold wave monster is tagged. Not shared across threads after it is handed over.
    /// Ported from DreamWeave #17's HoldWavePlan.
    /// </summary>
    public sealed class HoldWavePlan
    {
        public HoldWaveType Type { get; set; } = HoldWaveType.Normal;

        /// <summary>The 1-based wave number.</summary>
        public int WaveNumber { get; set; }

        /// <summary>Breach (and a mixed wave's breach squad): the one knight they all go for (0 = none).</summary>
        public uint BreachTargetGuid { get; set; }

        public string BreachTargetName { get; set; }

        /// <summary>Which side of the anchor the breach target stands on ("east").</summary>
        public string BreachSide { get; set; }

        /// <summary>Runners: how many of the wave's monsters are kept (null = all).</summary>
        public int? KeepCount { get; set; }

        /// <summary>Runners: health as a share of normal (1 = unchanged).</summary>
        public double HealthMult { get; set; } = 1.0;

        public float RunRateMult { get; set; } = 1.0f;

        public float AttackSpeedMult { get; set; } = 1.0f;

        /// <summary>Ranged: the distance (m) ranged and caster monsters stop at (0 = stock AI).</summary>
        public float HoldDistance { get; set; }

        /// <summary>The wave's trash with ranged slots redrawn and level offsets applied (null = the pick's own trash).</summary>
        public IReadOnlyList<uint> ReplacedTrash { get; set; }

        /// <summary>Trash slots redrawn by a role level offset: slot index to offset. Null = none.</summary>
        public IReadOnlyDictionary<int, int> LevelOffsetSlots { get; set; }

        /// <summary>Hunters (one-type wave): how many non-seekers hunt players.</summary>
        public int Hunters { get; set; }

        /// <summary>One-type waves: the entry indices that go for the knights (null = none).</summary>
        public HashSet<int> SeekerIndices { get; set; }

        /// <summary>Finale: the champion leading the wave (0 = none could be picked).</summary>
        public uint ChampionWcid { get; set; }

        public bool ChampionSynthetic { get; set; }
        public int ChampionUplift { get; set; }
        public int ChampionSlotLevel { get; set; }

        /// <summary>Finale: the champion's display name ("Warlord ...").</summary>
        public string ChampionName { get; set; }

        /// <summary>Finale: the champion's index in the spawn batch, set when the entries are built (-1 = none).</summary>
        public int ChampionEntryIndex { get; set; } = -1;

        /// <summary>Finale: this batch is the boss alone; the rest of the wave follows (<see cref="EscortFor"/>).</summary>
        public bool FinaleBossOnly { get; set; }

        /// <summary>Finale: this batch is the rest of the final wave, sent after the boss.</summary>
        public bool FinaleEscort { get; set; }

        /// <summary>Mixed: the ranged carry the run's ward (protect.ward).</summary>
        public bool Warded { get; set; }

        /// <summary>Mixed: one role per entry of the batch, in spawn order (null for any other wave).</summary>
        public HoldRole[] Roles { get; set; }

        /// <summary>Mixed, an assault wave: the knight each corrupted monster marches on, by its index in the batch.</summary>
        public Dictionary<int, uint> CorruptedTargets { get; set; }

        /// <summary>Mixed, an assault wave: one call-out per corrupted monster (its name, its target's name).</summary>
        public List<(string Monster, string Target)> AssaultCalls { get; set; }

        /// <summary>Mixed: this wave is a corrupted assault.</summary>
        public bool Assault { get; set; }

        /// <summary>The finale's rest-of-wave plan: same wave number, no copies of the boss's own monster.</summary>
        public static HoldWavePlan EscortFor(HoldWavePlan bossPlan, WavePick pick, System.Random rng)
        {
            return new HoldWavePlan
            {
                Type = HoldWaveType.Finale,
                WaveNumber = bossPlan?.WaveNumber ?? 0,
                FinaleEscort = true,
                ReplacedTrash = WorldEventHoldRules.WithoutBossLookalikes(pick?.Trash, bossPlan?.ChampionWcid ?? 0, rng)
            };
        }

        /// <summary>Mixed: how many of each role, for the log ("breach=2 runners=2 ...").</summary>
        public string RoleSummary()
        {
            if (Roles == null)
                return "";

            var counts = new int[WorldEventHoldRules.RoleCount];

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
                case HoldWaveType.Runners: return $"type=runners keep={KeepCount} health=x{HealthMult:F2} speed=x{RunRateMult:F2} attackSpeed=x{AttackSpeedMult:F2}";
                case HoldWaveType.Ranged: return $"type=ranged replaced={(ReplacedTrash == null ? "none" : ReplacedTrash.Count.ToString())} holdDistance={HoldDistance:0.#}";
                case HoldWaveType.Hunters: return $"type=hunters hunters={Hunters}";
                case HoldWaveType.Finale: return FinaleEscort ? "type=finale escort" : $"type=finale champion={(ChampionWcid == 0 ? "none" : $"{ChampionWcid} {ChampionName}")}";
                case HoldWaveType.Mixed:
                    return $"type=mixed breach=0x{BreachTargetGuid:X8} ({BreachTargetName}) side={BreachSide} {RoleSummary()} " +
                           $"runnerSpeed=x{RunRateMult:F2} runnerAttack=x{AttackSpeedMult:F2} runnerHealth=x{HealthMult:F2} holdDistance={HoldDistance:0.#}" +
                           (Assault ? $" assault=[{string.Join(", ", AssaultCalls?.Select(c => $"{c.Monster} -> {c.Target}") ?? new string[0])}]" : "");
                default: return "type=normal";
            }
        }
    }
}
