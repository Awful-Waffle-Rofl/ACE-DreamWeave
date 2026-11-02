using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// A RECORDING IPinnacleFightPort for boss-script tests (M3). Every call is appended to <see cref="Calls"/> with
    /// its arguments, in order, and the interesting ones also land in typed lists. The test drives the world side:
    /// <see cref="Now"/>, <see cref="Rng"/>, <see cref="Phase"/>, <see cref="Head"/>, who is alive
    /// (<see cref="Kill"/>), and partway failure (<see cref="FailNextSpawns"/>).
    ///
    /// The port only records. It does NOT apply AdvancePhase or Win (it counts them) and it delivers no events: the
    /// test calls the script's OnEvent itself, which is exactly what keeps a script test about the script.
    /// </summary>
    public sealed class FakePinnacleFightPort : IPinnacleFightPort
    {
        public sealed record Call(string Name, object[] Args)
        {
            public override string ToString() => $"{Name}({string.Join(", ", Args.Select(a => a?.ToString() ?? "null"))})";
        }

        public sealed record SpawnCall(string Tag, uint Wcid, PinnaclePoint At, PinnacleHandle Result, PinnacleTier Tier);

        public sealed record HazardCall(string Tag, string Kind, string Pattern, HazardArgs Args, PinnacleHandle Origin);

        public sealed record PropCall(string Tag, PinnacleProp Prop, PinnacleHandle Result);

        public sealed record MotionCall(PinnacleHandle Handle, MotionStance Stance, MotionCommand Motion, bool Hold);

        public sealed record EffectCall(PinnacleHandle Handle, PlayScript Script);

        public sealed record MoveCall(PinnacleHandle Handle, PinnaclePoint To);

        public sealed record TurnCall(PinnacleHandle Handle, PinnaclePoint Facing);

        private int nextHandle;

        public FakePinnacleFightPort(int seed = 1, int firstHandle = 100)
        {
            Rng = new Random(seed);
            nextHandle = firstHandle;
            Head = new PinnacleHandle(firstHandle - 1);
            Alive.Add(Head);
        }

        // ---- driven by the test ----
        public double Now { get; set; }

        public Random Rng { get; set; }

        public int Phase { get; set; } = 1;

        public int Level { get; set; } = 250;

        public PinnacleHandle Head { get; set; }

        public HashSet<PinnacleHandle> Alive { get; } = new HashSet<PinnacleHandle>();

        /// <summary>The next N SpawnAdd calls return None (the core's retry is not modelled: None means it failed twice).</summary>
        public int FailNextSpawns { get; set; }

        /// <summary>What DispelOwnerBuffs returns; null = the requested count.</summary>
        public int? DispelResult { get; set; }

        /// <summary>Where the owner stands; null = TryGetOwnerPosition returns false.</summary>
        public PinnaclePoint? OwnerAt { get; set; }

        /// <summary>What TryGetPosition reads; a handle with no entry returns false.</summary>
        public Dictionary<PinnacleHandle, PinnaclePoint> PositionOf { get; } = new Dictionary<PinnacleHandle, PinnaclePoint>();

        /// <summary>The handles IsAtHealthFloor answers true for.</summary>
        public HashSet<PinnacleHandle> AtFloor { get; } = new HashSet<PinnacleHandle>();

        /// <summary>What ControlHazard and TryGetHazardLevel see, keyed by hazard tag. A tag with no entry reads false.</summary>
        public Dictionary<string, double> HazardLevel { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Tags ControlHazard accepts; null = every tag that has a started hazard or a HazardLevel entry.</summary>
        public HashSet<string> ControllableTags { get; set; }

        public void Kill(PinnacleHandle h) => Alive.Remove(h);

        // ---- recordings ----
        public List<Call> Calls { get; } = new List<Call>();

        public List<SpawnCall> Spawns { get; } = new List<SpawnCall>();

        public List<HazardCall> Hazards { get; } = new List<HazardCall>();

        public Dictionary<string, double> Timers { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

        public List<(string Tag, double Seconds)> TimerSets { get; } = new List<(string, double)>();

        public List<string> CancelledTimers { get; } = new List<string>();

        public Dictionary<PinnacleHandle, bool> Immune { get; } = new Dictionary<PinnacleHandle, bool>();

        public Dictionary<PinnacleHandle, bool> Attackable { get; } = new Dictionary<PinnacleHandle, bool>();

        public List<(PinnacleHandle Caster, uint SpellId)> Casts { get; } = new List<(PinnacleHandle, uint)>();

        public List<int> Dispels { get; } = new List<int>();

        public List<PropCall> Props { get; } = new List<PropCall>();

        public List<PinnacleHandle> DestroyedProps { get; } = new List<PinnacleHandle>();

        public Dictionary<PinnacleHandle, bool> DoorOpen { get; } = new Dictionary<PinnacleHandle, bool>();

        public List<MotionCall> Motions { get; } = new List<MotionCall>();

        public List<EffectCall> Effects { get; } = new List<EffectCall>();

        public List<MoveCall> MoveTos { get; } = new List<MoveCall>();

        public List<TurnCall> Turns { get; } = new List<TurnCall>();

        /// <summary>What TurnTo returns (the turn's seconds).</summary>
        public double TurnSeconds { get; set; }

        public Dictionary<PinnacleHandle, bool> Peaceful { get; } = new Dictionary<PinnacleHandle, bool>();

        public List<PinnacleHandle> Engaged { get; } = new List<PinnacleHandle>();

        /// <summary>The last SetHealthFloor per handle (null = cleared).</summary>
        public Dictionary<PinnacleHandle, double?> Floors { get; } = new Dictionary<PinnacleHandle, double?>();

        public List<PinnacleHandle> Slain { get; } = new List<PinnacleHandle>();

        /// <summary>Every accepted ControlHazard call.</summary>
        public List<(string Tag, string Key, double Value)> Controls { get; } = new List<(string, string, double)>();

        public List<string> Said { get; } = new List<string>();

        public int StopHazardsCount { get; private set; }

        public int AdvanceCount { get; private set; }

        public int WinCount { get; private set; }

        public IEnumerable<string> CallNames => Calls.Select(c => c.Name);

        public IEnumerable<Call> CallsNamed(string name) => Calls.Where(c => c.Name == name);

        /// <summary>Forgets every recording (not the driven state), so a test can assert only what one step did.</summary>
        public void ClearRecordings()
        {
            Calls.Clear();
            Spawns.Clear();
            Hazards.Clear();
            TimerSets.Clear();
            CancelledTimers.Clear();
            Casts.Clear();
            Dispels.Clear();
            Props.Clear();
            DestroyedProps.Clear();
            Motions.Clear();
            Effects.Clear();
            MoveTos.Clear();
            Turns.Clear();
            Engaged.Clear();
            Slain.Clear();
            Controls.Clear();
            Said.Clear();
            StoppedHazardTags.Clear();
            StopHazardsCount = 0;
            AdvanceCount = 0;
            WinCount = 0;
        }

        private void Record(string name, params object[] args) => Calls.Add(new Call(name, args));

        // ---- IPinnacleFightPort ----
        public PinnacleHandle SpawnAdd(string tag, uint wcid, PinnaclePoint at, PinnacleTier tier)
        {
            PinnacleHandle h;

            if (FailNextSpawns > 0)
            {
                FailNextSpawns--;
                h = PinnacleHandle.None;
            }
            else
            {
                h = new PinnacleHandle(nextHandle++);
                Alive.Add(h);
            }

            Record(nameof(SpawnAdd), tag, wcid, at, h, tier);
            Spawns.Add(new SpawnCall(tag, wcid, at, h, tier));
            return h;
        }

        public bool IsAlive(PinnacleHandle h) => !h.IsNone && Alive.Contains(h);

        public void SetImmune(PinnacleHandle h, bool immune)
        {
            Record(nameof(SetImmune), h, immune);
            Immune[h] = immune;
        }

        public void SetAttackable(PinnacleHandle h, bool attackable)
        {
            Record(nameof(SetAttackable), h, attackable);
            Attackable[h] = attackable;
        }

        public void CastAtOwner(PinnacleHandle caster, uint spellId)
        {
            Record(nameof(CastAtOwner), caster, spellId);
            Casts.Add((caster, spellId));
        }

        public void StartHazard(string tag, string kind, string pattern, HazardArgs args, PinnacleHandle origin = default)
        {
            Record(nameof(StartHazard), tag, kind, pattern, args, origin);
            Hazards.Add(new HazardCall(tag, kind, pattern, args, origin));
        }

        public void StopHazards()
        {
            Record(nameof(StopHazards));
            StopHazardsCount++;
            HazardLevel.Clear();
        }

        public List<string> StoppedHazardTags { get; } = new List<string>();

        public void StopHazard(string tag)
        {
            Record(nameof(StopHazard), tag);
            StoppedHazardTags.Add(tag);
            HazardLevel.Remove(tag);
        }

        public void SetTimer(string tag, double seconds)
        {
            Record(nameof(SetTimer), tag, seconds);
            TimerSets.Add((tag, seconds));
            Timers[tag] = Now + seconds;
        }

        public void CancelTimer(string tag)
        {
            Record(nameof(CancelTimer), tag);
            CancelledTimers.Add(tag);
            Timers.Remove(tag);
        }

        public int DispelOwnerBuffs(int count)
        {
            Record(nameof(DispelOwnerBuffs), count);
            Dispels.Add(count);
            return DispelResult ?? count;
        }

        public void Say(string line)
        {
            Record(nameof(Say), line);
            Said.Add(line);
        }

        public PinnacleHandle SpawnProp(string tag, PinnacleProp prop)
        {
            PinnacleHandle h;

            if (FailNextSpawns > 0)
            {
                FailNextSpawns--;
                h = PinnacleHandle.None;
            }
            else
            {
                h = new PinnacleHandle(nextHandle++);
                Alive.Add(h);
                PositionOf[h] = prop.At;

                if (prop.DoorOpen.HasValue)
                    DoorOpen[h] = prop.DoorOpen.Value;
            }

            Record(nameof(SpawnProp), tag, prop, h);
            Props.Add(new PropCall(tag, prop, h));
            return h;
        }

        public void DestroyProp(PinnacleHandle h)
        {
            Record(nameof(DestroyProp), h);
            DestroyedProps.Add(h);
            Alive.Remove(h);
        }

        public void SetDoorOpen(PinnacleHandle h, bool open)
        {
            Record(nameof(SetDoorOpen), h, open);
            DoorOpen[h] = open;
        }

        public void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold)
        {
            Record(nameof(PlayMotion), h, stance, motion, hold);
            Motions.Add(new MotionCall(h, stance, motion, hold));
        }

        public void PlayEffect(PinnacleHandle h, PlayScript script)
        {
            Record(nameof(PlayEffect), h, script);
            Effects.Add(new EffectCall(h, script));
        }

        public void MoveTo(PinnacleHandle h, PinnaclePoint to)
        {
            Record(nameof(MoveTo), h, to);
            MoveTos.Add(new MoveCall(h, to));
        }

        public double TurnTo(PinnacleHandle h, PinnaclePoint facing)
        {
            Record(nameof(TurnTo), h, facing);
            Turns.Add(new TurnCall(h, facing));
            return TurnSeconds;
        }

        public bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at)
        {
            if (PositionOf.TryGetValue(h, out at))
                return true;

            at = default;
            return false;
        }

        public bool TryGetOwnerPosition(out PinnaclePoint at)
        {
            at = OwnerAt ?? default;
            return OwnerAt.HasValue;
        }

        public void SetPeaceful(PinnacleHandle h, bool peaceful)
        {
            Record(nameof(SetPeaceful), h, peaceful);
            Peaceful[h] = peaceful;
        }

        public void Engage(PinnacleHandle h)
        {
            Record(nameof(Engage), h);
            Engaged.Add(h);
        }

        public void SetHealthFloor(PinnacleHandle h, double? fraction)
        {
            Record(nameof(SetHealthFloor), h, fraction);
            Floors[h] = fraction;
        }

        public bool IsAtHealthFloor(PinnacleHandle h) => AtFloor.Contains(h);

        public void Slay(PinnacleHandle h)
        {
            Record(nameof(Slay), h);
            Slain.Add(h);
        }

        public bool ControlHazard(string tag, string key, double value)
        {
            Record(nameof(ControlHazard), tag, key, value);

            var known = ControllableTags != null
                ? ControllableTags.Contains(tag)
                : Hazards.Any(x => x.Tag == tag) || HazardLevel.ContainsKey(tag);

            if (!known)
                return false;

            Controls.Add((tag, key, value));
            return true;
        }

        public bool TryGetHazardLevel(string tag, out double level) => HazardLevel.TryGetValue(tag, out level);

        public void AdvancePhase()
        {
            Record(nameof(AdvancePhase));
            AdvanceCount++;
        }

        public void Win()
        {
            Record(nameof(Win));
            WinCount++;
        }
    }
}
