using System;
using System.Collections.Generic;
using System.Globalization;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Avatars;

/// <summary>One Avatar level rise seen by a credit: a multi-level jump in one flush is a single event (old to new).</summary>
internal readonly record struct AvatarLevelUpEvent(uint AvatarId, uint? MonarchGuid, int OldLevel, int NewLevel);

/// <summary>
/// What happens when an Avatar's level rises: its live NPCs take the new level (with the level-up burst) and the
/// allegiance is told. AvatarManager collects the events under its lock and calls <see cref="Dispatch"/> only after
/// the lock is released, because everything here touches the world, NPCs or chat.
///
/// THREADS. The NPC mutation is queued onto the NPC's own landblock action queue (ActionChain.AddAction(npc, ...),
/// the same idiom AvatarNpcs uses to expire a summoned NPC), so the Level write, the property broadcast and the
/// script never run on a thread that does not own the NPC. An NPC that is detached from any landblock (WorldObject.
/// EnqueueAction) drops the action for a Creature: a re-spawned NPC takes its level from AvatarNpcs.Build, and
/// <see cref="SyncNpc"/> covers the window between Build and the NPC being registered, when a dispatch cannot see it.
/// </summary>
internal static class AvatarLevelUp
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The line sent to the allegiance, without the unlock suffix.</summary>
    internal static string AnnounceLine(int newLevel) =>
        string.Format(CultureInfo.InvariantCulture, "Your Allegiance Avatar has reached level {0}.", newLevel);

    /// <summary>Appended when the jump crosses the event-unlock level.</summary>
    internal const string EventUnlockSuffix = " It can now host " + AvatarStatusText.EventUnlockLine + ".";

    /// <summary>Appended when the jump crosses the Allegiance Gauntlet unlock level.</summary>
    internal const string GauntletUnlockSuffix = " It can now call an Allegiance Gauntlet.";

    /// <summary>Every this-many levels is announced on its own (25, 50, 75, ...).</summary>
    internal const int AnnounceMilestoneLevels = 25;

    /// <summary>Test seam: the Gauntlet's minimum Avatar level, which is an unlock the announcement marks.</summary>
    internal static Func<long> GauntletMinLevel = () => ACE.Server.Managers.PropertyManager.GetLong("allegiance_gauntlet_min_avatar_level").Item;

    /// <summary>
    /// True when a rise from <paramref name="oldLevel"/> to <paramref name="newLevel"/> is worth telling the allegiance:
    /// it reaches a multiple of 25, or it crosses an unlock level (the event gate or the Gauntlet gate). The NPC's level
    /// updates on every rise regardless.
    /// </summary>
    internal static bool ShouldAnnounce(int oldLevel, int newLevel, long eventMinLevel, long gauntletMinLevel)
    {
        if (newLevel <= oldLevel)
            return false;

        return newLevel / AnnounceMilestoneLevels > oldLevel / AnnounceMilestoneLevels
            || Crosses(oldLevel, newLevel, eventMinLevel)
            || Crosses(oldLevel, newLevel, gauntletMinLevel);
    }

    private static bool Crosses(int oldLevel, int newLevel, long gate) => oldLevel < gate && gate <= newLevel;

    /// <summary>Test seam: the whole dispatch. Production runs <see cref="Handle"/>.</summary>
    internal static Action<AvatarLevelUpEvent, AvatarTuning> Dispatch = Handle;

    /// <summary>Test seam: the live NPCs (summoned and seated) of an Avatar.</summary>
    internal static Func<uint, IReadOnlyList<AvatarVendor>> NpcsOf = DefaultNpcsOf;

    /// <summary>Test seam: applies the new level to one NPC on its own thread.</summary>
    internal static Action<AvatarVendor, int> ApplyToNpc = DefaultApplyToNpc;

    internal static void ResetSeams()
    {
        Dispatch = Handle;
        NpcsOf = DefaultNpcsOf;
        ApplyToNpc = DefaultApplyToNpc;
        GauntletMinLevel = () => ACE.Server.Managers.PropertyManager.GetLong("allegiance_gauntlet_min_avatar_level").Item;
    }

    internal static void Handle(AvatarLevelUpEvent e, AvatarTuning t)
    {
        try
        {
            foreach (var npc in NpcsOf(e.AvatarId))
            {
                try
                {
                    ApplyToNpc(npc, e.NewLevel);
                }
                catch (Exception ex)
                {
                    log.Error($"[AVATAR] Applying level {e.NewLevel} to the NPC of Avatar {e.AvatarId} failed.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Finding the NPCs of Avatar {e.AvatarId} failed; the announcement still goes out.", ex);
        }

        if (e.MonarchGuid.HasValue)
        {
            long gauntletMin;
            try
            {
                gauntletMin = GauntletMinLevel();
            }
            catch (Exception ex)
            {
                log.Error("[AVATAR] Reading the Gauntlet's minimum Avatar level failed; its unlock is not announced.", ex);
                gauntletMin = long.MaxValue;
            }

            if (!ShouldAnnounce(e.OldLevel, e.NewLevel, t.EventMinLevel, gauntletMin))
                return;

            var text = AnnounceLine(e.NewLevel);
            if (Crosses(e.OldLevel, e.NewLevel, t.EventMinLevel))
                text += EventUnlockSuffix;
            if (Crosses(e.OldLevel, e.NewLevel, gauntletMin))
                text += GauntletUnlockSuffix;
            AvatarAnnouncer.ToAllegiance(e.MonarchGuid.Value, text);
        }
    }

    private static IReadOnlyList<AvatarVendor> DefaultNpcsOf(uint avatarId)
    {
        var list = new List<AvatarVendor>(2);
        var summoned = AvatarNpcs.SummonedNpc(avatarId);
        var seated = AvatarNpcs.SeatedNpc(avatarId);
        if (summoned != null)
            list.Add(summoned);
        if (seated != null && !ReferenceEquals(seated, summoned))
            list.Add(seated);
        return list;
    }

    private static void DefaultApplyToNpc(AvatarVendor npc, int level)
    {
        var chain = new ActionChain();
        chain.AddAction(npc, () => ApplyLevel(npc, level));
        chain.EnqueueChain();
    }

    /// <summary>Sets the level, broadcasts it and plays the burst. Must run on the NPC's own thread; skips a destroyed NPC.</summary>
    internal static void ApplyLevel(AvatarVendor npc, int level)
    {
        if (npc.IsDestroyed)
            return;
        npc.Level = level;
        npc.EnqueueBroadcast(true, new GameMessagePublicUpdatePropertyInt(npc, PropertyInt.Level, level));
        npc.PlayParticleEffect(PlayScript.LevelUp, npc.Guid);
    }

    /// <summary>
    /// The Avatar's current level as an NPC shows it, clamped to at least 1 (a brand-new Avatar is level 0, which reads as
    /// broken on a creature). Null when the Avatar is missing or the read fails.
    /// </summary>
    internal static int? LiveLevel(uint avatarId)
    {
        try
        {
            var a = AvatarManager.Get(avatarId);
            return a == null ? null : Math.Max(1, a.Level(AvatarManager.CurrentTuning()));
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Reading the level of Avatar {avatarId} for its NPC failed; the weenie level stands.", ex);
            return null;
        }
    }

    /// <summary>
    /// Call right after an NPC is registered with AvatarNpcs, outside its lock. Build read the level before the NPC was
    /// visible to <see cref="NpcsOf"/>, so a level-up dispatched in between missed it; this applies the current level
    /// when it differs.
    /// </summary>
    internal static void SyncNpc(AvatarVendor npc, uint avatarId)
    {
        var level = LiveLevel(avatarId);
        if (!level.HasValue || npc.Level == level.Value)
            return;

        try
        {
            ApplyToNpc(npc, level.Value);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Re-syncing the level of the NPC of Avatar {avatarId} failed.", ex);
        }
    }
}
