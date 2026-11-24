using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// What a placed fighter needs from its run: whether the run's copy is live (the beneficial-spell opt-in), and where to
/// report a death. Implemented by the fighter hooks' per-run slot; any thread.
/// </summary>
public interface IGauntletFighterOwner
{
    /// <summary>True while the run is in Staging, Active or Intermission: members may heal and buff the fighter.</summary>
    bool IsLiveCopy { get; }

    /// <summary>The fighter died (its landblock thread). Reported once per fighter.</summary>
    void OnFighterDied(GauntletAvatarFighter fighter);
}

/// <summary>
/// The Allegiance Avatar's in-fight body (plan Task 8, per the Task 1A spike): a CombatPet with NO player owner and NO pet
/// device, built by WorldObjectFactory from PropertyBool.AllegianceGauntletFighter (9102) on wcid 1008504.
///
/// Being a CombatPet is the point. The physics layer reads it as a combat pet (WeenieObject.IsCombatPet true, IsMonster
/// false), so monsters admit it as a target, it admits only monsters, a player's own combat pet never admits it,
/// Player.CanDamage refuses it for every player attack (NPK and PK), and no target is ever taken across an instance
/// boundary, so the copy is the run's scope. WeenieType CombatPet also keeps it out of the shard
/// (WorldObject_Database IsDynamicThatShouldPersistToShard).
///
/// What replaces the owner-dependent paths:
/// <list type="bullet">
/// <item>Lifespan, the real killer (WorldObject_Tick deletes a spent one, and every retail CombatPet weenie carries one), is
/// stripped twice: from the weenie (<see cref="BuildFighterWeenie"/>) and in the constructor.</item>
/// <item>Pet.SlowTick (the owner-lost destroy) is a no-op. Monster_Tick only reaches it for a passive pet, so this is the
/// guard against a weenie typo.</item>
/// <item>No owner to follow: it follows its <see cref="Leader"/>, the highest-level living participant (<see cref="ChooseLeader"/>),
/// through CombatPet's own idle follow and summon assist (FollowLeader). With no leader it fights only what is in range, else
/// stands still. An optional leash (<see cref="Anchor"/>, <see cref="LeashRadius"/>) bounds a private event's fighter.</item>
/// <item>Placement does CombatPet.Init's world half by hand (combat mode, Awake, IsAwake); see LiveGauntletFighterHooks.</item>
/// </list>
///
/// Damage intake is capped through WorldEventHoldRules.CapProtectedHit (via WorldEventHooks) (<see cref="CapIncomingHit"/>), called on all three
/// damage paths beside the Hold knight's: Creature.TakeDamage, SpellProjectile.DamageTarget and the life-magic Harm.
/// Members may heal and buff it only while its run's copy is live (<see cref="IsInvalidFighterSpellTarget"/>); a beneficial
/// cast or kit on it counts toward the caster's crate through GauntletHooks.OnBeneficialAct. Its kills credit no player: it has
/// no P_PetOwner, so every kill hook that resolves the killer through the pet owner gets the fighter itself.
/// </summary>
public class GauntletAvatarFighter : CombatPet, IAvatarFigure
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The fighter weenie (Content, Task 14). Reserved in Content/wcid-registry.d/wcid-1008500.tsv.</summary>
    public const uint FighterWcid = 1008504;

    public const string FighterName = "Allegiance Avatar";

    /// <summary>Metres from the anchor inside which the fighter may hold a target when no radius is set.</summary>
    public const float DefaultLeashRadius = 60.0f;

    /// <summary>
    /// The point a private event's fighter is leashed to (the event anchor). Null in the Gauntlet: there the fighter follows
    /// its leader anywhere in the copy (owner ruling 2026-11-23).
    /// </summary>
    public Position Anchor { get; set; }

    public float LeashRadius { get; set; } = DefaultLeashRadius;

    /// <summary>The run's side. Null outside a run: the fighter then refuses every beneficial spell and reports nothing.</summary>
    public IGauntletFighterOwner Owner { get; set; }

    /// <summary>The last stamp applied (null before the first).</summary>
    public GauntletFighterStamp Stamp { get; private set; }

    /// <summary>The Avatar this body fights for (set by AvatarFighterFactory.Dress). 0 when unbound.</summary>
    public uint AvatarId { get; internal set; }

    /// <summary>The dressed hair texture pair (old, new), or null. Read by WorldObject.AddBaseModelData (see IAvatarFigure).</summary>
    internal (uint, uint)? HairTextureOverride { get; set; }

    (uint, uint)? IAvatarFigure.HairTextureOverride { get => HairTextureOverride; set => HairTextureOverride = value; }

    private int deathReported;

    public GauntletAvatarFighter(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
    {
        SetEphemeralValues();
    }

    public GauntletAvatarFighter(Biota biota) : base(biota)
    {
        SetEphemeralValues();
    }

    private void SetEphemeralValues()
    {
        // Pet.Init sets this for every summon; this fighter never runs Pet.Init
        NoCorpse = true;

        // the second Lifespan strip (the first is BuildFighterWeenie): WorldObject.Heartbeat deletes a spent one
        Lifespan = null;

        authoredBodyParts = Biota.PropertiesBodyPart;
    }

    // ---- NPC presentation ---------------------------------------------------------------------------------------------

    /// <summary>
    /// WorldObject_Networking: the Attackable bit sent to the CLIENT. A fighter is sent as not attackable, so a player cannot
    /// select it as an attack target and the client draws it as an NPC; the server keeps Attackable true, which monster
    /// targeting and the damage paths read (the Hold knights' split, the other way round). <paramref name="attackable"/>
    /// unchanged for every other object after one type test.
    /// </summary>
    public static bool ClientAttackable(WorldObject wo, bool attackable) => attackable && wo is not GauntletAvatarFighter;

    // ---- the leader (pure) -----------------------------------------------------------------------------------------------

    /// <summary>One participant as the leader choice sees it.</summary>
    public readonly record struct LeaderCandidate(uint Guid, int Level, bool Alive, bool Present);

    /// <summary>
    /// The leader: the highest-level participant who is alive and present (in the world, on the fighter's side of the
    /// instance and landblock, and inside the leash when there is one). A tie keeps <paramref name="current"/> when it is
    /// among the tied, so two equal members never trade the lead back and forth; otherwise the lowest guid, so the choice
    /// is the same on every tick. Null when nobody qualifies.
    /// </summary>
    public static uint? ChooseLeader(IEnumerable<LeaderCandidate> candidates, uint? current)
    {
        if (candidates == null)
            return null;

        var eligible = candidates.Where(c => c.Alive && c.Present).ToList();

        if (eligible.Count == 0)
            return null;

        var top = eligible.Max(c => c.Level);
        var tied = eligible.Where(c => c.Level == top).Select(c => c.Guid).ToList();

        if (current.HasValue && tied.Contains(current.Value))
            return current.Value;

        return tied.Min();
    }

    /// <summary>True when a held target must be dropped: the fighter or the target has left the leash circle.</summary>
    public static bool ShouldDropTarget(double selfFromAnchor, double targetFromAnchor, float leashRadius)
    {
        return selfFromAnchor > leashRadius || targetFromAnchor > leashRadius;
    }

    // ---- the leader (live) ------------------------------------------------------------------------------------------------

    /// <summary>Seconds between leader re-checks while the current leader stays eligible. A dead or gone leader is replaced at once.</summary>
    public const double LeaderRecheckSeconds = 1.0;

    /// <summary>
    /// The players this fighter fights beside (the Gauntlet copy's members, or the event's participants), read on the
    /// fighter's landblock thread. Must return a snapshot. Null: no participants, so no leader.
    /// </summary>
    public Func<IEnumerable<Player>> Participants { get; set; }

    /// <summary>
    /// Who hears the fighter's voice (AvatarFighterVoice): the Gauntlet copy's members inside, or the private event's
    /// participants, dead ones included. Must return a snapshot. Null: it speaks to nobody.
    /// </summary>
    public Func<IEnumerable<Player>> Audience { get; set; }

    /// <summary>Called on the fighter's landblock thread when the leader changes (old, new; either may be null). Never throws into the tick.</summary>
    public Action<GauntletAvatarFighter, Player, Player> LeaderChanged { get; set; }

    private Player leader;
    private double nextLeaderCheck;

    /// <summary>The participant this fighter follows and assists, or null (then it fights only what is in range, else stands).</summary>
    public Player Leader => leader;

    /// <summary>CombatPet's idle follow and summon assist read the leader; the fighter never has a PetOwner.</summary>
    protected override Player FollowLeader => leader;

    /// <summary>
    /// A private event's fighter (one with an <see cref="Anchor"/>) never takes a live objective source (a Destroy the
    /// Source rift or pillar), so it can never complete or break that objective by itself. Nothing in the Gauntlet.
    /// </summary>
    protected override bool RefusesPetTarget(Creature creature) => Anchor != null && (IsObjectiveSource ?? WorldEventHooks.IsLiveEventSource)(creature);

    /// <summary>Test seam: the objective-source test (WorldEventHooks.IsLiveEventSource when null).</summary>
    internal Func<Creature, bool> IsObjectiveSource { get; set; }

    /// <summary>Always assist the leader's target: the leader's own /summonattackontarget setting is for their pets.</summary>
    protected override bool AssistsLeaderTarget(Player leader) => true;

    /// <summary>
    /// Re-chooses the leader: at once when the current one is no longer eligible, else every <see cref="LeaderRecheckSeconds"/>
    /// (so a higher-level member who arrives takes the lead). Returns true when the leader changed.
    /// </summary>
    internal bool RefreshLeader(double now)
    {
        var current = leader;

        // between checks: nothing to do while the leader stays eligible, or while there is none (no participant read per tick)
        if (now < nextLeaderCheck && (current == null || IsEligible(current)))
            return false;

        nextLeaderCheck = now + LeaderRecheckSeconds;

        var players = new Dictionary<uint, Player>();
        var candidates = new List<LeaderCandidate>();

        IEnumerable<Player> snapshot = null;

        try
        {
            snapshot = Participants?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] fighter 0x{Guid}: the participant read threw; no leader this check", ex);
        }

        if (snapshot != null)
        {
            foreach (var p in snapshot)
            {
                if (p == null || !players.TryAdd(p.Guid.Full, p))
                    continue;

                candidates.Add((Describe ?? DescribeLive)(p));
            }
        }

        var currentListed = current != null && players.ContainsKey(current.Guid.Full);
        var chosen = ChooseLeader(candidates, currentListed ? current.Guid.Full : null);
        var next = chosen.HasValue ? players[chosen.Value] : null;

        if (ReferenceEquals(next, current))
            return false;

        leader = next;

        // the voice. The first pick is the spawn line's moment; a higher-level arrival or a member back from the pen taking
        // the lead over a leader who still stands says nothing. Only a LOST leader (down, or gone) speaks.
        var currentLost = current != null && !(currentListed && candidates.Any(c => c.Guid == current.Guid.Full && c.Alive && c.Present));

        try
        {
            if (currentLost && next != null)
            {
                // a hidden admin is never named
                if (!AvatarFighterVoice.IsHidden(next))
                    AvatarFighterVoice.Say(this, FighterVoiceTrigger.LeaderSwitch, next.Name);
            }
            else if (currentLost && candidates.Count > 0 && candidates.All(c => !c.Alive))
                AvatarFighterVoice.Say(this, FighterVoiceTrigger.AllDown);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] fighter 0x{Guid}: the leader voice line threw", ex);
        }

        try
        {
            LeaderChanged?.Invoke(this, current, next);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] fighter 0x{Guid}: the leader-change callback threw", ex);
        }

        return true;
    }

    /// <summary>Test seam: how a participant is described to <see cref="ChooseLeader"/>. Null: the live reads (<see cref="DescribeLive"/>).</summary>
    internal Func<Player, LeaderCandidate> Describe { get; set; }

    // a hidden or cloaked admin is never present: following one would walk the 2x Avatar to them and give their position away
    private LeaderCandidate DescribeLive(Player p) => new LeaderCandidate(p.Guid.Full, p.Level ?? 0, !(IsDown ?? DefaultIsDown)(p), (IsPresentSeam ?? IsPresent)(p) && !AvatarFighterVoice.IsHidden(p));

    /// <summary>
    /// Whether a participant counts as down (never a leader; all of them down is the all-down line). Null: dead. The Gauntlet
    /// sets dead OR held in the death pen, so a held member stays a candidate who is down, not one who left.
    /// </summary>
    public Func<Player, bool> IsDown { get; set; }

    private static bool DefaultIsDown(Player p) => p.IsDead;

    /// <summary>Test seam: the presence read (<see cref="IsPresent"/> when null).</summary>
    internal Func<Player, bool> IsPresentSeam { get; set; }

    private bool IsEligible(Player p)
    {
        var c = (Describe ?? DescribeLive)(p);
        return c.Alive && c.Present;
    }

    /// <summary>
    /// In the world, not mid-teleport, in this fighter's instance and on its tick thread (CombatPet's own follow rule, so a
    /// member who left the landblock is never chosen), and inside the leash circle when there is one.
    /// </summary>
    private bool IsPresent(Player p)
    {
        if (p.PhysicsObj == null || p.Location == null || p.Teleporting || Location == null)
            return false;

        if (p.Location.Instance != Location.Instance || !SharesTickThreadWith(p))
            return false;

        return Anchor == null || p.Location.DistanceTo(Anchor) <= LeashRadius;
    }

    // ---- overrides ----------------------------------------------------------------------------------------------------

    /// <summary>No owner, so no owner-lost destroy and no owner leash (see the class summary).</summary>
    public override void SlowTick(double currentUnixTime)
    {
    }

    /// <summary>
    /// The leader first, then CombatPet.HandleFindTarget: assist the leader's last attacked monster, else the nearest
    /// admitted monster in the standard pet range (with or without a leader), else none. A fighter with an
    /// <see cref="Anchor"/> (a private event) also drops a target, or a chase, that leaves the leash circle.
    /// </summary>
    public override void HandleFindTarget()
    {
        RefreshLeader(Timers.RunningTime);
        TickVoice(Timers.RunningTime);

        base.HandleFindTarget();

        if (Anchor == null || Location == null)
            return;

        if (AttackTarget is not Creature target || target.Location == null)
            return;

        if (ShouldDropTarget(Location.DistanceTo(Anchor), target.Location.DistanceTo(Anchor), LeashRadius))
        {
            StopMovingForTeleport();
            PhysicsObj?.unstick_from_object();
            ResetAttack();
            AttackTarget = null;
        }
    }

    // ---- the periodic voice -----------------------------------------------------------------------------------------------

    private double nextVoiceAt;

    /// <summary>Re-reads the period after this many seconds while the periodic line is off, so turning it on takes effect.</summary>
    public const double VoiceOffRecheckSeconds = 30.0;

    /// <summary>The periodic line, every allegiance_avatar_fighter_voice_seconds from placement. Returns true when it spoke.</summary>
    internal bool TickVoice(double now)
    {
        if (IsDead)
            return false;

        if (nextVoiceAt > 0 && now < nextVoiceAt)
            return false;

        long period;

        try
        {
            period = AvatarFighterVoice.PeriodSeconds();
        }
        catch (Exception ex)
        {
            log.Debug($"[AVATAR] fighter 0x{Guid}: the voice period read threw", ex);
            period = 0;
        }

        var due = AvatarFighterVoice.PeriodicDue(now, nextVoiceAt, period);

        nextVoiceAt = now + (period > 0 ? period : VoiceOffRecheckSeconds);

        if (due)
            AvatarFighterVoice.Say(this, FighterVoiceTrigger.Periodic);

        return due;
    }

    /// <summary>Reports the death to the run once (the run counts it and schedules the respawn), then dies as any combat pet.</summary>
    public override DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)
    {
        if (deathReported == 0)
            AvatarFighterVoice.Say(this, FighterVoiceTrigger.Death);

        ReportDeathOnce();

        return base.OnDeath(lastDamager, damageType, criticalHit);
    }

    /// <summary>The once-only half of <see cref="OnDeath"/>. True when this call reported.</summary>
    internal bool ReportDeathOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref deathReported, 1) != 0)
            return false;

        var owner = Owner;

        if (owner == null)
            return false;

        try
        {
            owner.OnFighterDied(this);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] fighter 0x{Guid}: the death report threw", ex);
        }

        return true;
    }

    // ---- damage intake cap ----------------------------------------------------------------------------------------------

    private readonly object capLock = new object();
    private double capTokens;
    private double capLastAt;

    /// <summary>
    /// The three damage paths' hook (Creature.TakeDamage, SpellProjectile.DamageTarget, the life-magic Harm in
    /// WorldObject_Magic): a hit on the fighter comes back capped, any other target unchanged after one type test.
    /// </summary>
    public static float CapGauntletFighterHit(Creature target, float amount)
    {
        if (target is not GauntletAvatarFighter fighter)
            return amount;

        return fighter.CapIncomingHit(amount, ACE.Common.Time.GetUnixTime());
    }

    /// <summary>
    /// A hit re-sized by WorldEventHoldRules.CapProtectedHit: at most max health over the stamp's min_death_seconds, with
    /// burst_seconds of that held as burst. A heal (0 or less) and a fighter with no stamp pass through.
    /// </summary>
    internal float CapIncomingHit(float amount, double now)
    {
        var stamp = Stamp;

        if (!(amount > 0) || stamp == null)
            return amount;

        var max = Health.MaxValue;

        lock (capLock)
            return WorldEventHooks.CapProtectedHit(amount, max, stamp.MinDeathSeconds, stamp.BurstSeconds, ref capTokens, ref capLastAt, now);
    }

    /// <summary>A full bucket: the next hit gets the whole burst (a restamp or a respawn).</summary>
    private void ResetCap()
    {
        lock (capLock)
        {
            capTokens = 0;
            capLastAt = 0;
        }
    }

    // ---- members' heals and buffs ----------------------------------------------------------------------------------------

    /// <summary>
    /// Player.IsInvalidTarget, beside the Hold knight's IsInvalidHoldSpellTarget and ahead of the beneficial-on-creature
    /// refusal: true when <paramref name="target"/> is a fighter, with <paramref name="invalid"/> the decision (only a
    /// beneficial spell, and only while its run's copy is live). False - decide as before - for every other target, after
    /// one type test.
    /// </summary>
    public static bool IsInvalidFighterSpellTarget(Creature target, ACE.Server.Entity.Spell spell, out bool invalid)
    {
        if (target is not GauntletAvatarFighter fighter)
        {
            invalid = false;
            return false;
        }

        invalid = IsInvalidFighterSpell(fighter.AcceptsBeneficialSpells, spell != null && spell.IsBeneficial);
        return true;
    }

    /// <summary>Pure core of <see cref="IsInvalidFighterSpellTarget"/>: valid only when beneficial and the copy is live.</summary>
    public static bool IsInvalidFighterSpell(bool acceptsBeneficial, bool isBeneficial) => !(acceptsBeneficial && isBeneficial);

    /// <summary>True while this fighter belongs to a live run (Staging, Active or Intermission) and is alive.</summary>
    public bool AcceptsBeneficialSpells => !IsDead && Owner?.IsLiveCopy == true;

    // ---- members' healing kits -------------------------------------------------------------------------------------------

    /// <summary>
    /// The most health, as a share of the fighter's maximum, that kits may restore in any ten seconds (a token bucket, the
    /// Hold knight's player-heal shape). A flat starting value for the Task 16 tuning pass; not a tunable.
    /// </summary>
    public const double KitHealMaxSharePerTenSeconds = 0.25;

    private double kitTokens;
    private double kitLastAt;

    /// <summary>Healer.HandleActionUseOnTarget: true for a fighter (the kit path is the fighter's, accepted or refused). One type test.</summary>
    public static bool IsFighterHealTarget(WorldObject target) => target is GauntletAvatarFighter;

    /// <summary>Pure core of the kit decision: a health kit only, only while the run's copy is live and the fighter alive.</summary>
    public static bool AcceptsKit(PropertyAttribute2nd kitVital, bool acceptsBeneficial) => kitVital == PropertyAttribute2nd.Health && acceptsBeneficial;

    /// <summary>True when a health kit may be used on this fighter now (a live copy, alive).</summary>
    public bool AcceptsHealthKit(PropertyAttribute2nd kitVital) => AcceptsKit(kitVital, AcceptsBeneficialSpells);

    /// <summary>
    /// Applies a kit's raw heal (healing skill x kit bonus, rolled by Healer): converted to a share of the fighter's max
    /// health the way the Hold knight converts a player heal (WorldEventHoldRules.ConvertPlayerHeal, so a typical kit restores
    /// a few percent however large the scaled health), through a ten-second bucket so a crowd of kits cannot make it
    /// unkillable, and never above its missing health. Its landblock thread. Returns the health restored, with
    /// <paramref name="outcome"/> Healed, Full (already full) or Refused (not a live copy, dead, or the bucket is dry).
    /// </summary>
    internal int ApplyKitHeal(double raw, double now, out ACE.Server.WorldEvents.Hold.HoldHealOutcome outcome)
    {
        if (!AcceptsBeneficialSpells)
        {
            outcome = ACE.Server.WorldEvents.Hold.HoldHealOutcome.Refused;
            return 0;
        }

        var max = Health.MaxValue;
        var missing = (int)(max - Math.Min(max, Health.Current));

        if (missing <= 0)
        {
            outcome = ACE.Server.WorldEvents.Hold.HoldHealOutcome.Full;
            return 0;
        }

        int converted;

        lock (capLock)
            converted = WorldEventHooks.ConvertPlayerHeal(raw, max, KitHealMaxSharePerTenSeconds, ref kitTokens, ref kitLastAt, now);

        var healed = Math.Min(converted, missing);

        if (healed <= 0)
        {
            outcome = ACE.Server.WorldEvents.Hold.HoldHealOutcome.BudgetSpent;
            return 0;
        }

        UpdateVitalDelta(Health, healed);
        outcome = ACE.Server.WorldEvents.Hold.HoldHealOutcome.Healed;
        return healed;
    }

    /// <summary>
    /// Queues <see cref="ApplyKitHeal"/> on the fighter's own landblock (the healer may stand on another thread) and calls
    /// <paramref name="reply"/> there with the result. False when the fighter has no landblock (nothing queued).
    /// </summary>
    public bool QueueKitHeal(double raw, Action<int, ACE.Server.WorldEvents.Hold.HoldHealOutcome> reply)
    {
        var landblock = CurrentLandblock;

        if (landblock == null)
            return false;

        landblock.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
        {
            var healed = ApplyKitHeal(raw, ACE.Common.Time.GetUnixTime(), out var outcome);
            reply?.Invoke(healed, outcome);
        }));

        return true;
    }

    /// <summary>Test seam: queues a heal on the fighter's landblock (<see cref="QueueKitHeal"/> by default).</summary>
    internal static Func<GauntletAvatarFighter, double, Action<int, ACE.Server.WorldEvents.Hold.HoldHealOutcome>, bool> QueueHeal = (f, raw, reply) => f.QueueKitHeal(raw, reply);

    /// <summary>
    /// The Health branch of a beneficial spell (WorldObject_Magic, the Boost write): a player's heal spell on the fighter goes
    /// through the kit heal (the same conversion and the same ten-second bucket), queued on the fighter's landblock, which
    /// sends the caster the result line. True when <paramref name="target"/> is a fighter: the caller then does not write
    /// the heal itself. False after one type test for every other target.
    /// </summary>
    public static bool TryQueueSpellHeal(Player caster, Creature target, double raw)
    {
        if (target is not GauntletAvatarFighter fighter)
            return false;

        var name = fighter.Name;

        void Reply(int healed, ACE.Server.WorldEvents.Hold.HoldHealOutcome outcome)
        {
            var text = WorldEventHooks.PlayerHealLine(name, healed, outcome, false, null);

            if (!string.IsNullOrEmpty(text))
                caster?.Session?.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(text, ChatMessageType.Magic));
        }

        if (!QueueHeal(fighter, raw, Reply))
            Reply(0, ACE.Server.WorldEvents.Hold.HoldHealOutcome.Refused);

        return true;
    }

    // ---- restamp ------------------------------------------------------------------------------------------------------------

    private IDictionary<CombatBodyPart, PropertiesBodyPart> authoredBodyParts;
    private readonly Dictionary<uint, int> authoredWeaponDamage = new Dictionary<uint, int>();

    /// <summary>
    /// Applies a wave's stamp: clears the members' buffs (so no stacks carry across waves), sets the shown level, skills,
    /// attributes, body-part and weapon damage, then the new maximum health at the old health fraction (measured before the
    /// buffs came off), and refills the damage cap. A null stamp (a refused wave) only clears the buffs and refills the cap.
    /// Its landblock thread.
    /// </summary>
    public void ApplyStamp(GauntletFighterStamp stamp, bool fullHealth = false, bool clearBuffs = true)
    {
        var oldMax = Health.MaxValue;
        var oldCurrent = Health.Current;

        if (clearBuffs)
            ClearMemberBuffs();

        if (stamp != null)
        {
            SetProperty(PropertyInt.Level, stamp.Level);

            foreach (var kv in stamp.Skills)
            {
                // add: false - never hand the fighter a skill its weenie does not author
                var skill = GetCreatureSkill(kv.Key, false);

                if (skill != null && kv.Value > 0)
                    skill.InitLevel = kv.Value;
            }

            foreach (var kv in stamp.Attributes)
            {
                if (kv.Value > 0 && Attributes.TryGetValue(kv.Key, out var attribute) && attribute != null)
                {
                    attribute.StartingValue = kv.Value;
                    attribute.Ranks = 0;
                }
            }

            ApplyDamageTarget(stamp.DamageTarget);

            Stamp = stamp;
        }

        var newMax = stamp?.MaxHealth ?? 0;

        if (newMax > 0)
            ACE.Server.WorldEvents.WorldEventSpawner.ApplyHealth(this, (int)Math.Min(newMax, int.MaxValue));

        var max = Health.MaxValue;
        Health.Current = fullHealth ? max : Math.Min(max, GauntletFighterStats.KeptHealth(oldCurrent, oldMax, max));

        if (Health.Current == 0 && !IsDead)
            Health.Current = 1;

        Stamina.Current = Stamina.MaxValue;
        Mana.Current = Mana.MaxValue;

        ResetCap();
    }

    /// <summary>Body parts from the AUTHORED set each time (no drift over many restamps); weapons from their authored damage.</summary>
    private void ApplyDamageTarget(uint target)
    {
        if (target == 0)
            return;

        Biota.PropertiesBodyPart = authoredBodyParts;
        DungeonCombatNormalizer.SetBossBodyParts(Biota, target, 0, 1.0, 0);

        var items = EquippedObjects.Values.Concat(Inventory.Values).Where(i => i != null).Distinct().ToList();
        var launcher = items.FirstOrDefault(i => i is MissileLauncher);

        foreach (var item in items)
        {
            if (item is MissileLauncher)
                continue;

            if (!authoredWeaponDamage.TryGetValue(item.Guid.Full, out var authored))
            {
                authored = item.GetProperty(PropertyInt.Damage) ?? 0;
                authoredWeaponDamage[item.Guid.Full] = authored;
            }

            if (authored <= 0)
                continue;

            var modSource = item.WeenieType == WeenieType.Ammunition ? launcher : item;
            var damageMod = modSource?.GetProperty(PropertyFloat.DamageMod) ?? 1.0;

            item.SetProperty(PropertyInt.Damage, DungeonCombatNormalizer.WeaponDamageTarget(authored, damageMod, target, 1.0));
        }
    }

    /// <summary>
    /// Removes every beneficial timed enchantment (the members' heals over time and buffs). Cooldowns, item spells
    /// (duration -1) and harmful enchantments stay.
    /// </summary>
    internal int ClearMemberBuffs()
    {
        var registry = Biota.PropertiesEnchantmentRegistry;

        if (registry == null)
            return 0;

        var all = registry.Clone(BiotaDatabaseLock);
        var keep = all.Where(e => !IsMemberBuff(e)).Select(e => e.SpellId).ToHashSet();
        var removed = all.Count(IsMemberBuff);

        if (removed == 0)
            return 0;

        EnchantmentManager.RemoveAllEnchantmentsExcept(keep);

        return removed;
    }

    /// <summary>A beneficial, timed, castable enchantment (the members' buffs), never a cooldown or an item spell.</summary>
    internal static bool IsMemberBuff(PropertiesEnchantmentRegistry e) =>
        e != null && e.StatModType.HasFlag(EnchantmentTypeFlags.Beneficial) && e.Duration != -1 && e.SpellId <= short.MaxValue;

    // ---- weenie shaping (pure) ----------------------------------------------------------------------------------------

    /// <summary>
    /// A copy of <paramref name="donor"/> shaped as the fighter. The scalar dictionaries and the create list are copied (they
    /// are written); the rest is shared by reference, as MuleFormBody.CloneWithDonorBody does, and never written. Idempotent:
    /// the fighter weenie (wcid 1008504) is already shaped, and running it through again changes nothing.
    /// </summary>
    public static Weenie BuildFighterWeenie(Weenie donor, string name)
    {
        if (donor == null)
            throw new ArgumentNullException(nameof(donor));

        var clone = new Weenie
        {
            WeenieClassId = donor.WeenieClassId,
            ClassName = donor.ClassName,
            WeenieType = WeenieType.CombatPet,

            PropertiesBool = Copy(donor.PropertiesBool),
            PropertiesDID = Copy(donor.PropertiesDID),
            PropertiesFloat = Copy(donor.PropertiesFloat),
            PropertiesIID = Copy(donor.PropertiesIID),
            PropertiesInt = Copy(donor.PropertiesInt),
            PropertiesInt64 = Copy(donor.PropertiesInt64),
            PropertiesString = Copy(donor.PropertiesString),

            PropertiesPosition = donor.PropertiesPosition,
            PropertiesSpellBook = donor.PropertiesSpellBook,
            PropertiesAnimPart = donor.PropertiesAnimPart,
            PropertiesPalette = donor.PropertiesPalette,
            PropertiesTextureMap = donor.PropertiesTextureMap,
            PropertiesAttribute = donor.PropertiesAttribute,
            PropertiesAttribute2nd = donor.PropertiesAttribute2nd,
            PropertiesBodyPart = donor.PropertiesBodyPart,
            PropertiesSkill = donor.PropertiesSkill,

            // a death emote can hand items or XP to the last damager, and generators have no place on a fighter
            PropertiesEmote = null,
            PropertiesGenerator = null,
            PropertiesEventFilter = donor.PropertiesEventFilter,
            PropertiesBook = null,
            PropertiesBookPageData = null,

            // wield-only rows stay (the fighter's weapon); anything that would drop on a NoCorpse death goes
            PropertiesCreateList = donor.PropertiesCreateList?
                .Where(i => (i.DestinationType & (DestinationType.Contain | DestinationType.Treasure)) == 0)
                .Select(i => i.Clone())
                .ToList(),
        };

        clone.PropertiesBool[PropertyBool.AllegianceGauntletFighter] = true;
        clone.PropertiesBool[PropertyBool.Attackable] = true;
        clone.PropertiesBool[PropertyBool.NoCorpse] = true;
        clone.PropertiesBool.Remove(PropertyBool.SummonNeverInitiates);
        clone.PropertiesBool.Remove(PropertyBool.SummonCasterBrain);

        // the first Lifespan strip (the second is the constructor)
        clone.PropertiesInt.Remove(PropertyInt.Lifespan);

        clone.PropertiesDID.Remove(PropertyDataId.DeathTreasureType);

        // SummonTauntInterval is weenie data a Tank pet reads; the taunt pulse needs an owner (ApplyTauntPulse returns 0)
        clone.PropertiesFloat.Remove(PropertyFloat.SummonTauntInterval);

        // NPC presentation (owner ruling 2026-11-23): a yellow NPC blip and the shopkeepers' PK status, as the Hold knights
        // wear. Neither is read by monster targeting (ObjectMaint excludes only PK-only creatures), so monsters still take it
        // as a target. The client is also told it is not attackable (ClientAttackable), while the server keeps Attackable.
        clone.PropertiesInt[PropertyInt.RadarBlipColor] = (int)ACE.Entity.Enum.RadarColor.Yellow;
        clone.PropertiesInt[PropertyInt.PlayerKillerStatus] = (int)PlayerKillerStatus.RubberGlue;

        if (!string.IsNullOrEmpty(name))
            clone.PropertiesString[PropertyString.Name] = name;

        return clone;
    }

    private static Dictionary<TKey, TValue> Copy<TKey, TValue>(IDictionary<TKey, TValue> source) =>
        source == null ? new Dictionary<TKey, TValue>() : new Dictionary<TKey, TValue>(source);
}
