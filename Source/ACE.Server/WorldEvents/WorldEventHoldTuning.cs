using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>World Events Hold: the live admin settings that act on a run (HoldSettings) and need the run's own state.</summary>
    public sealed partial class WorldEvent
    {
        /// <summary>
        /// world_events_hold_&lt;role&gt;_level_offset on a mixed wave: each slot of a role with an offset is redrawn from the
        /// roster's members in the wave's band moved by that offset (a ranged slot from the ranged members of its kind,
        /// caster or archer). A slot with no member there keeps its monster. Nothing happens while every offset is 0.
        /// </summary>
        private void ApplyRoleLevelOffsets(HoldWavePlan plan, WavePick pick, ISet<int> keep, int waveNumber)
        {
            if (plan?.Roles == null || pick == null)
                return;

            var offsets = new Dictionary<HoldDamageRole, int>();

            foreach (HoldDamageRole role in Enum.GetValues(typeof(HoldDamageRole)))
            {
                var offset = HoldSettings.RoleLevelOffset(role);

                if (offset != 0)
                    offsets[role] = offset;
            }

            if (offsets.Count == 0)
                return;

            var trash = plan.ReplacedTrash ?? pick.Trash;
            var members = Composition?.Roster?.Members ?? new List<ACE.Server.WorldEvents.Defs.FamilyMember>();

            HoldDamageRole RoleOf(int i)
            {
                if (i >= plan.Roles.Length)
                    return HoldDamageRole.None;

                var role = plan.Roles[i];

                return role switch
                {
                    HoldRole.Runner => HoldDamageRole.Runner,
                    HoldRole.Corrupted => HoldDamageRole.Corrupted,
                    HoldRole.Ranged => i < trash.Count && WorldEventRangedMembers.IsCasterWcid(trash[i]) ? HoldDamageRole.Caster : HoldDamageRole.Ranged,
                    _ => HoldDamageRole.Melee
                };
            }

            var pools = new Dictionary<HoldDamageRole, IReadOnlyList<uint>>();

            IReadOnlyList<uint> PoolFor(int i)
            {
                var role = RoleOf(i);

                if (!offsets.TryGetValue(role, out var offset))
                    return null;

                if (pools.TryGetValue(role, out var cached))
                    return cached;

                var band = WorldEventHoldRules.ShiftBand(pick.Band, offset);
                var inBand = members.Where(m => m != null && m.Wcid != 0 && m.Role == 0 && band.Contains(m.Level));

                if (role == HoldDamageRole.Ranged || role == HoldDamageRole.Caster)
                    inBand = inBand.Where(m => WorldEventRangedMembers.IsRanged(m) && WorldEventRangedMembers.IsCasterWcid(m.Wcid) == (role == HoldDamageRole.Caster));

                var pool = inBand.Select(m => m.Wcid).Distinct().ToList();
                pools[role] = pool;

                return pool;
            }

            var redrawn = WorldEventHoldRules.RedrawForLevelOffsets(trash, keep, i => offsets.TryGetValue(RoleOf(i), out var o) ? o : 0, PoolFor, holdWaveRng, out var moved);

            if (moved.Count == 0)
            {
                log.Info($"[WORLDEVENT] run={RunId} hold wave {waveNumber}: role level offsets [{string.Join(", ", offsets.Select(o => $"{o.Key}{o.Value:+0;-0}"))}] " +
                         $"found no member in the moved band (band {pick.Band}); the wave is unchanged");
                return;
            }

            plan.ReplacedTrash = redrawn;
            plan.LevelOffsetSlots = moved;

            log.Info($"[WORLDEVENT] run={RunId} hold wave {waveNumber}: {moved.Count} slot(s) redrawn for role level offsets " +
                     $"[{string.Join(", ", offsets.Select(o => $"{o.Key}{o.Value:+0;-0}"))}] from band {pick.Band}");
        }
    }
}
