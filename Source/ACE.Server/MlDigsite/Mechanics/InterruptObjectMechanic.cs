using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlDigsite.Mechanics
{
    /// <summary>
    /// INTERRUPT OBJECT. "The boss announces a huge hit with a message and a sound. One player has to use a
    /// drum or lever, placed far from the boss, before the timer ends. If it was used, the boss skips the
    /// hit." (RoZ round 13.)
    ///
    /// THE MISS IS SURVIVABLE ALONE, by owner ruling (2026-09-20): 35% of max health, non-lethal. Boss Rush
    /// spawns are random, so a player can meet this set solo and must be able to attempt it - and learn it by
    /// failing - rather than be deleted by a mechanic they had no chance to reach. The damage is a FRACTION
    /// of each player's own max health rather than a flat number, so a level 205 boss and a level 275 boss
    /// punish the same way. It is read through MlDigsiteBossMechanicRules.ResolveDamage, which is the ONE
    /// damage convention this feature has - at or below 1.0 a fraction of max health, above 1.0 a flat
    /// pre-resistance number - and the shifting safe zones read their own damage through the same call.
    ///
    /// DISTANCE IS CONSTRAINED BY THE TETHER, and this is the one number in the whole feature that is not
    /// free. The object has to stand strictly inside ml_digsite_boss_tether_radius, or a player running to it
    /// drags the boss past its leash and the boss disengages - which would turn the interrupt into an
    /// accidental reset button. MlDigsiteBossMechanicRules.ClampInterruptDistance is the guard, applied at
    /// read time so a live retune cannot break the fight silently.
    ///
    /// NO OBJECT MEANS NO MECHANIC. If the interrupt wcid is 0, or its weenie is not applied to this world
    /// database, the module does nothing at all rather than landing an unavoidable hit: the hit exists only
    /// to be cancelled, and a hit nobody can cancel is not a mechanic, it is a tax.
    /// </summary>
    public sealed class InterruptObjectMechanic : IMlDigsiteMechanic
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MlDigsiteMechanic Kind => MlDigsiteMechanic.Interrupt;

        // Documented defaults, applied for any arg the tunable string leaves out. Never 0, never blank.
        private const double DefaultEvery = 45.0;
        // Round 17 owner ruling: window 20 s (was 12), distance 15 m (was 30) - on stage the drum expired
        // unanswered 30 m out in 12 s. Must match MlDigsiteBossMechanicRules.DefaultInterrupt.
        private const double DefaultWindow = 20.0;
        private const double DefaultDistance = 15.0;
        private const double DefaultDamage = 0.35;

        /// <summary>Seconds between repeats of the drum's visibility flare while the window is open.</summary>
        private const double DefaultPulse = 2.0;

        /// <summary>
        /// Round 17: the drum's visibility effect - stamped as its PhysicsScript at placement AND re-played every
        /// pulse= seconds for as long as the window is open. EnchantUpYellow (0x3D), matching the drum's yellow
        /// radar blip. CHOSEN FROM THE DRUM'S OWN PhysicsEffectTable 0x3400002B, which a DatLoader read of
        /// client_portal.dat shows maps 17 scripts - the twelve Enchant Up/Down colours, EnchantUp/DownWhite,
        /// Create, Destroy and DisappearDestroy - and NOT RestrictionEffectBlue (0x98), the script this object
        /// was stamped with before. A PlayScript is resolved through the object's own effect table, so the
        /// old stamp most likely rendered nothing at all (hypothesis - confirm in the live test).
        /// </summary>
        internal const PlayScript DrumFlare = PlayScript.EnchantUpYellow;

        /// <summary>
        /// Seconds-left marks the window is counted down at. One line each, latched. The marks live on
        /// MlDigsiteBossMechanicRules with the rule that decides when one is due, so a test can drive both
        /// halves without a live window.
        /// </summary>
        private static readonly IReadOnlyList<int> CountdownMarks = MlDigsiteBossMechanicRules.InterruptCountdownMarks;

        public void Tick(MlDigsiteMechanicContext ctx)
        {
            if (ctx.State.InterruptOpen)
            {
                DriveOpenWindow(ctx);
                return;
            }

            if (!ctx.CadenceDue(ctx.Args.GetDouble("every", DefaultEvery)))
                return;

            OpenWindow(ctx);
        }

        /// <summary>
        /// Counts an open window down, and lands the hit when it runs out. The close is a single latch on the
        /// driver state, which is what makes "used" and "expired" mutually exclusive: a player using the
        /// object on the same tick the window runs out produces exactly one outcome.
        /// </summary>
        private static void DriveOpenWindow(MlDigsiteMechanicContext ctx)
        {
            if (!ctx.State.InterruptExpired(ctx.Now))
            {
                // Round 17: re-play the drum's flare on its own cadence for the whole window, so it keeps
                // drawing the eye rather than flashing once at placement. Due at once for a freshly built drum.
                if (ctx.State.TryClaimInterruptPulse(ctx.Now, Math.Clamp(ctx.Args.GetDouble("pulse", DefaultPulse), 1.0, 30.0)))
                    MlDigsiteProps.PlayScriptOn(ctx.State.InterruptObject, DrumFlare);

                var left = ctx.State.InterruptSecondsLeft(ctx.Now);

                foreach (var mark in CountdownMarks)
                {
                    // AT OR BELOW, never equal: a starved tick can jump 7 s left straight to 5 s and an
                    // equality test would drop that window's 6 s reminder entirely. The latch keeps it to
                    // one line per mark per window either way.
                    if (MlDigsiteBossMechanicRules.CountdownMarkDue(left, mark) && ctx.State.TryLatchInterruptWarning(mark))
                        ctx.Say($"The drum - {mark} seconds.", ChatMessageType.WorldBroadcast);
                }

                return;
            }

            if (!ctx.State.TryCloseInterrupt(out var placed))
                return;

            MlDigsiteProps.Remove(placed);

            var authored = ctx.Args.GetDouble("damage", DefaultDamage);

            ctx.Say($"Nobody answers. {ctx.Boss.Name} brings it down on the whole dig.", ChatMessageType.WorldBroadcast);

            var hit = 0;

            foreach (var player in ctx.Participants())
            {
                var max = player?.Health?.MaxValue ?? 0;

                if (max == 0)
                    continue;

                // NON-LETHAL BY CONSTRUCTION (owner ruling): the cap is applied inside the queued write
                // against the player's health at the moment of the write, not against what it was when the
                // window opened.
                ctx.Hit(player, MlDigsiteBossMechanicRules.ResolveDamage(authored, max), DamageType.Bludgeon, nonLethal: true);
                hit++;
            }

            log.Info($"[ML_DIGSITE] {ctx.Encounter} interrupt window expired unanswered; {hit} player(s) hit for damage={authored:0.##}");
        }

        /// <summary>
        /// Opens a window: announce the windup, place the object, tell players which way to run. The object's
        /// build is asynchronous (MlDigsiteProps queues it onto the anchor landblock), so the state records it
        /// through a callback - and the chat line names the bearing computed from the position that was ASKED
        /// for, which is the same point the terrain snap lands on in X and Y.
        /// </summary>
        private static void OpenWindow(MlDigsiteMechanicContext ctx)
        {
            var wcid = MlDigsiteTunables.BossRushInterruptWcid;

            if (wcid == 0)
                return;

            var anchor = ctx.Anchor;

            if (anchor == null)
                return;

            var distance = MlDigsiteBossMechanicRules.ClampInterruptDistance(
                ctx.Args.GetDouble("distance", DefaultDistance), MlDigsiteTunables.TetherRadius);

            var window = Math.Clamp(ctx.Args.GetDouble("window", DefaultWindow), 3.0, 120.0);

            var points = WorldEventGeometry.Ring(anchor, (float)distance, 1, 2.0f, ctx.Rng);

            if (points.Count == 0)
                return;

            var where = points[0];

            if (!ctx.State.TryOpenInterrupt(ctx.Now + TimeSpan.FromSeconds(window)))
                return;

            var state = ctx.State;

            var encounter = ctx.Encounter;

            if (!MlDigsiteProps.TryPlace(ctx.Encounter, wcid, where, (uint)DrumFlare,
                wo => OnObjectBuilt(encounter, state, wcid, wo)))
            {
                // Nothing was placed and nothing will be, so close the window at once rather than counting
                // down to a hit nobody could have stopped.
                ctx.State.TryCloseInterrupt(out _);

                log.Warn($"[ML_DIGSITE] {ctx.Encounter} interrupt object wcid {wcid} could not be queued; the windup is cancelled rather than landing unavoidably");
                return;
            }

            var offset = where.ToGlobal() - anchor.ToGlobal();
            var bearing = MlDigsiteMechanicGeometry.Compass(offset.X, offset.Y);

            ctx.Say($"{ctx.Boss.Name} raises both arms, and the ground starts to go. Something is coming.", ChatMessageType.WorldBroadcast);
            ctx.Sound(Sound.UI_Drums);
            ctx.Say($"A drum stands lit to the {bearing}, about {(int)Math.Round(distance)} paces out. Someone use it, fast - {(int)Math.Round(window)} seconds.", ChatMessageType.WorldBroadcast);

            log.Info($"[ML_DIGSITE] {ctx.Encounter} interrupt window opened: {window}s, object wcid {wcid} {bearing} at {distance}m (slot {ctx.Slot})");
        }

        /// <summary>
        /// The prop build has run, on the anchor LANDBLOCK's thread. Placement is asynchronous, so this is
        /// the first moment anything knows whether the drum actually exists.
        ///
        /// A NULL here is the one failure mode that would otherwise be unfair rather than merely broken: the
        /// window is already open and the "someone use it, fast" line has already gone out, so counting down
        /// to the hit would punish a dig for not using an object that was never in the world. The commonest
        /// way to reach it is the weenie not being applied to this world database, which is exactly the state
        /// a code deploy that outran its content deploy leaves behind.
        /// </summary>
        private static void OnObjectBuilt(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, uint wcid, WorldObject wo)
        {
            if (wo != null)
            {
                state.SetInterruptObject(wo);
                return;
            }

            // Only the caller that wins the single latch says anything: a window already closed by a player's
            // use, or by the expiry, is not this failure.
            if (!state.TryCloseInterrupt(out _))
                return;

            log.Error($"[ML_DIGSITE] {encounter} interrupt object wcid {wcid} failed to build; the window is cancelled. Is its weenie applied to this world database?");

            MlDigsiteManager.Announce(encounter, "The drum never rises. Whatever was coming passes over the dig.",
                ChatMessageType.WorldBroadcast);
        }

        /// <summary>
        /// A player used the object. Runs on the LANDBLOCK thread that handled the use, from
        /// MlDigsiteInterruptObject.TryHandleUse through MlDigsiteBossMechanics.TryInterrupt.
        ///
        /// Returns true only when this use is the one that closed the window - the same single latch the
        /// expiry takes - so two players double-clicking the drum in the same instant cancel one hit and the
        /// second is told it is already done, rather than both being told they saved the group.
        /// </summary>
        public static bool TryUse(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, Player player)
        {
            if (!state.TryCloseInterrupt(out var placed))
                return false;

            MlDigsiteProps.Remove(placed);

            var boss = encounter.ObjectiveCreature;
            var name = boss == null ? "It" : boss.Name;

            MlDigsiteManager.Announce(encounter,
                $"{player.Name} sounds the drum. {name} staggers, and the blow never lands.", ChatMessageType.WorldBroadcast);

            log.Info($"[ML_DIGSITE] {encounter} interrupt answered by {player.Name}");

            return true;
        }
    }
}
