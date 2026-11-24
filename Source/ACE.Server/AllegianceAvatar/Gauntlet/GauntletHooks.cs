using System;
using System.Reflection;

using ACE.Server.Realms;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The combat hooks the Allegiance Gauntlet hangs on the existing World Event entry points (plan Task 11), plus the
/// "is this player standing in a Gauntlet copy" test the Avatar feed uses.
///
/// Contact follows the World Event rule (WorldEventCombatCreditHook): any landed hit by a player (or a CombatPet's owner)
/// on a Gauntlet creature, or by a Gauntlet creature on a player, including a fully absorbed hit and a stamina or mana
/// attack. Every call site is harmful by construction (DamageHistory.Add, the stamina/mana-lowering bolts, harmful Boost and
/// Transfer), so a beneficial cast on the Avatar fighter or on a member never reaches here. The same victim-state refusal
/// applies (Invincible, lifestone protection). The Avatar fighter is a CombatPet with no player owner, so its own hits never
/// resolve to a member.
///
/// HOT PATH. WorldEventHooks.OnDamageRecorded and OnCombatContact call <see cref="OnContact"/> first for every damaging hit
/// in the server. A hit with no Gauntlet creature on either side returns after one field read on the victim and, for a
/// player victim, a type test and one field read on a Creature attacker.
/// </summary>
public static class GauntletHooks
{
    private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// A landed hit between <paramref name="victim"/> and <paramref name="attacker"/>. When one side is a Gauntlet creature
    /// (Creature.P_Gauntlet) and the other resolves to a Player, and that creature's owner is an
    /// <see cref="IGauntletContactSink"/>, reports <see cref="IGauntletContactSink.OnMemberContact"/> with the creature's
    /// wave. Whether the player is actually a member is the sink's decision. Never throws: a damage hook must not abort the
    /// damage that triggered it.
    /// </summary>
    public static void OnContact(Creature victim, WorldObject attacker)
    {
        if (victim == null || attacker == null)
            return;

        // Each back-reference is read ONCE into a local: GauntletSpawner nulls it when the run goes inert.
        var owner = victim.P_Gauntlet;
        Player player;
        int wave;
        bool active;

        if (owner != null)
        {
            // A member (or their pet) hurt a Gauntlet creature.
            player = WorldEventHooks.ResolveAttackingPlayer(attacker);
            wave = victim.P_GauntletWave;
            active = true;
        }
        else if (victim is Player hurt && attacker is Creature source && (owner = source.P_Gauntlet) != null)
        {
            // A Gauntlet creature hurt a member, if the hit could land (see VictimStateRefusesCredit).
            if (WorldEventHooks.VictimStateRefusesCredit(hurt.Invincible, hurt.UnderLifestoneProtection))
                return;

            player = hurt;
            wave = source.P_GauntletWave;
            active = false;   // being hit counts as having fought, never toward a crate
        }
        else
            return;

        if (player == null || !(owner is IGauntletContactSink sink))
            return;

        try
        {
            sink.OnMemberContact(player, wave, active);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] contact hook threw for 0x{player.Guid.Full:X8} wave {wave}", ex);
        }
    }

    /// <summary>Test seam: the live run whose copy is an instance (GauntletManager.ForInstance).</summary>
    internal static Func<uint, GauntletRun> RunForInstance = instance => GauntletManager.ForInstance(instance);

    /// <summary>
    /// A member's beneficial act on another member or on the Avatar fighter: a beneficial spell that was not resisted
    /// (WorldObject.TryCastSpell_Inner) or a healing kit (Healer.DoHealing, DoHealingGauntletFighter). It counts toward the
    /// caster's crate in the current wave while the caster stands inside the arena circle. HOT PATH for every spell: a
    /// harmful spell, a non-player caster, a self-cast or a target that is neither a player nor the fighter returns after a
    /// type test or two. Never throws.
    /// </summary>
    public static void OnBeneficialAct(WorldObject caster, WorldObject target, bool beneficial)
    {
        if (!beneficial || caster is not Player player || target == null || ReferenceEquals(caster, target))
            return;

        if (target is not Player && target is not GauntletAvatarFighter)
            return;

        try
        {
            var at = player.Location;

            if (at == null || at.Instance == 0 || target.Location?.Instance != at.Instance)
                return;

            var run = RunForInstance(at.Instance);

            if (run == null || !run.IsLive || !run.InsideCircle(at))
                return;

            run.OnMemberSupport(player, run.CurrentWave);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] support hook threw for 0x{player.Guid.Full:X8}", ex);
        }
    }

    /// <summary>
    /// Player_Magic's spell target check: true when <paramref name="caster"/> is held by their run's death hold and the
    /// beneficial spell's target stands inside the fight circle. A held member may not heal or buff the fight from the edge.
    /// One bool test for every harmful spell; an instance-0 caster returns after a field read. Never throws (false on a fault).
    /// </summary>
    public static bool RefusesHeldSupport(Player caster, WorldObject target, bool beneficial)
    {
        if (!beneficial || caster == null || target == null)
            return false;

        try
        {
            var at = caster.Location;

            if (at == null || at.Instance == 0)
                return false;

            var run = RunForInstance(at.Instance);

            if (run == null || run.IsTerminal || run.DeathHoldSecondsLeft(caster) <= 0)
                return false;

            var to = target.Location;
            return to != null && to.Instance == at.Instance && run.InsideCircle(to);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] held-support check threw for 0x{caster.Guid.Full:X8}", ex);
            return false;
        }
    }

    /// <summary>
    /// The Boost Health branch's forwarder to <see cref="GauntletAvatarFighter.TryQueueSpellHeal"/> (kept here so the Boost
    /// method names the fighter type once, at its damage cap). True when the target is the fighter and the heal was queued.
    /// </summary>
    public static bool TryQueueFighterSpellHeal(Player caster, Creature target, double raw) => GauntletAvatarFighter.TryQueueSpellHeal(caster, target, raw);

    /// <summary>Pure: true when <paramref name="hold"/> is a Gauntlet arena's realm hold.</summary>
    public static bool IsGauntletHold(IEphemeralHold hold) => hold is GauntletArenaHold;

    /// <summary>
    /// True when <paramref name="player"/> is standing in a Gauntlet arena copy: its current landblock's realm hold is a
    /// <see cref="GauntletArenaHold"/>. Memory only; used by the Avatar feed, which must not credit Favor for anything earned
    /// inside a copy.
    /// </summary>
    public static bool InGauntletCopy(Player player) => player != null && IsGauntletHold(player.CurrentLandblock?.InnerRealmInfo?.Hold);
}
