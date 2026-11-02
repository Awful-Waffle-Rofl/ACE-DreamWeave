using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// A scriptable IPinnacleWorld. Every call is appended to <see cref="Calls"/> by method name (plus typed lists
    /// for the interesting ones), and the fake can FAIL PARTWAY: refuse the next N spawns or hazard starts, or fail
    /// the cache delivery a given number of times before succeeding, so retry tests discriminate.
    /// </summary>
    public sealed class FakePinnacleWorld : IPinnacleWorld
    {
        public sealed record SpawnCall(uint Wcid, PinnaclePoint At, bool WithImmuneRecord, PinnacleHandle Result, PinnacleTier Tier, bool LeavesCorpse = false);

        public sealed record HazardCall(string Kind, string Pattern, HazardArgs Args, int Token);

        public sealed record PropCall(PinnacleProp Prop, PinnacleHandle Result);

        public sealed record MotionCall(PinnacleHandle Handle, MotionStance Stance, MotionCommand Motion, bool Hold);

        public sealed record EffectCall(PinnacleHandle Handle, PlayScript Script);

        public sealed record MoveCall(PinnacleHandle Handle, PinnaclePoint To);

        public sealed record TurnCall(PinnacleHandle Handle, PinnaclePoint Facing);

        private int nextHandle = 1;
        private int nextToken = 1;

        // ---- knobs ----
        public bool IsLive { get; set; } = true;

        public bool OwnerPresent { get; set; } = true;

        /// <summary>Out of portal space. Default true; false models a return still materializing.</summary>
        public bool OwnerInPortalSpace { get; set; }

        public bool OwnerSettled => OwnerPresent && !OwnerInPortalSpace;

        /// <summary>The owner has a session. Default true; false models a logout.</summary>
        public bool OwnerOnline { get; set; } = true;

        /// <summary>Unset: online and not present (alive elsewhere whenever online and absent). Set to override.</summary>
        public bool? OwnerAliveElsewhereOverride { get; set; }

        public bool OwnerAliveElsewhere => OwnerAliveElsewhereOverride ?? (OwnerOnline && !OwnerPresent);

        /// <summary>True: Bob holds its continuation in <see cref="PendingNods"/> until the test calls <see cref="FinishNods"/> (the live nod takes 0.4 s).</summary>
        public bool DeferNods { get; set; }

        public List<Action> PendingNods { get; } = new List<Action>();

        public void FinishNods()
        {
            var due = PendingNods.ToList();
            PendingNods.Clear();

            foreach (var a in due)
                a();
        }

        /// <summary>The next N Spawn calls (of any wcid) return None.</summary>
        public int FailNextSpawns { get; set; }

        /// <summary>The next N Spawn calls OF THIS WCID return None (checked before FailNextSpawns).</summary>
        public Dictionary<uint, int> FailSpawnsOfWcid { get; } = new Dictionary<uint, int>();

        /// <summary>The next N StartHazard calls return 0.</summary>
        public int RefuseNextHazards { get; set; }

        /// <summary>TryDeliverCache results in order; once empty, <see cref="DeliverDefault"/>.</summary>
        public Queue<bool> DeliverResults { get; } = new Queue<bool>();

        public bool DeliverDefault { get; set; } = true;

        /// <summary>Destroy records the call, then throws (the object is NOT removed).</summary>
        public bool ThrowOnDestroy { get; set; }

        /// <summary>ReportBossKilled records the call, then throws.</summary>
        public bool ThrowOnReportBossKilled { get; set; }

        /// <summary>The next N SpawnProp calls return None (checked after FailSpawnsOfWcid).</summary>
        public int FailNextProps { get; set; }

        /// <summary>Where the owner stands; null = TryGetOwnerPosition returns false.</summary>
        public PinnaclePoint? OwnerAt { get; set; }

        /// <summary>What TryGetPosition reads (a prop is stored here at its spawn point); no entry reads false.</summary>
        public Dictionary<PinnacleHandle, PinnaclePoint> PositionOf { get; } = new Dictionary<PinnacleHandle, PinnaclePoint>();

        /// <summary>The handles IsAtHealthFloor answers true for.</summary>
        public HashSet<PinnacleHandle> AtFloor { get; } = new HashSet<PinnacleHandle>();

        /// <summary>What TryGetHazardLevel reads, keyed by hazard token. No entry reads false.</summary>
        public Dictionary<int, double> HazardLevel { get; } = new Dictionary<int, double>();

        /// <summary>Tokens ControlHazard refuses (returns false for).</summary>
        public HashSet<int> RefuseControlTokens { get; } = new HashSet<int>();

        public int DefaultHealth { get; set; } = 1000;

        public int BuffsAvailable { get; set; } = 10;

        // ---- state ----
        public HashSet<PinnacleHandle> Alive { get; } = new HashSet<PinnacleHandle>();

        public Dictionary<PinnacleHandle, int> Health { get; } = new Dictionary<PinnacleHandle, int>();

        public Dictionary<PinnacleHandle, (float X, float Y)> Positions { get; } = new Dictionary<PinnacleHandle, (float, float)>();

        public Dictionary<PinnacleHandle, uint> WcidOf { get; } = new Dictionary<PinnacleHandle, uint>();

        public Dictionary<PinnacleHandle, bool> Immune { get; } = new Dictionary<PinnacleHandle, bool>();

        public Dictionary<PinnacleHandle, bool> Attackable { get; } = new Dictionary<PinnacleHandle, bool>();

        public Dictionary<PinnacleHandle, bool> Peaceful { get; } = new Dictionary<PinnacleHandle, bool>();

        // ---- recordings ----
        public List<string> Calls { get; } = new List<string>();

        public List<SpawnCall> Spawns { get; } = new List<SpawnCall>();

        public List<PinnacleHandle> Destroyed { get; } = new List<PinnacleHandle>();

        public List<HazardCall> Hazards { get; } = new List<HazardCall>();

        public List<int> StoppedTokens { get; } = new List<int>();

        public List<PropCall> Props { get; } = new List<PropCall>();

        public Dictionary<PinnacleHandle, bool> DoorOpen { get; } = new Dictionary<PinnacleHandle, bool>();

        public List<MotionCall> Motions { get; } = new List<MotionCall>();

        public List<EffectCall> Effects { get; } = new List<EffectCall>();

        public List<MoveCall> MoveTos { get; } = new List<MoveCall>();

        public List<TurnCall> Turns { get; } = new List<TurnCall>();

        /// <summary>What TurnTo returns (the turn's seconds).</summary>
        public double TurnSeconds { get; set; }

        public List<PinnacleHandle> Engaged { get; } = new List<PinnacleHandle>();

        /// <summary>The last SetHealthFloor per handle (null = cleared).</summary>
        public Dictionary<PinnacleHandle, double?> Floors { get; } = new Dictionary<PinnacleHandle, double?>();

        public List<PinnacleHandle> Slain { get; } = new List<PinnacleHandle>();

        /// <summary>Every accepted ControlHazard call, by hazard token.</summary>
        public List<(int Token, string Key, double Value)> Controls { get; } = new List<(int, string, double)>();

        public List<(PinnacleHandle Handle, int Max)> Clamps { get; } = new List<(PinnacleHandle, int)>();

        public List<(PinnacleHandle Caster, uint SpellId)> Casts { get; } = new List<(PinnacleHandle, uint)>();

        public List<int> DispelRequests { get; } = new List<int>();

        public List<string> Told { get; } = new List<string>();

        public List<string> Logged { get; } = new List<string>();

        public List<bool> Releases { get; } = new List<bool>();

        public int Charges { get; private set; }

        public int DeliverAttempts { get; private set; }

        public int BossKilledReports { get; private set; }

        public int AttemptLostReports { get; private set; }

        public int Refunds { get; private set; }

        // ---- test helpers ----
        public void Kill(PinnacleHandle h) => Alive.Remove(h);

        public PinnacleHandle HandleOf(uint wcid) => Spawns.Last(s => s.Wcid == wcid && !s.Result.IsNone).Result;

        public int ActiveHazardCount => Hazards.Count(h => h.Token > 0 && !StoppedTokens.Contains(h.Token));

        /// <summary>The calls recorded since index <paramref name="from"/>, by method name.</summary>
        public IEnumerable<string> CallsSince(int from) => Calls.Skip(from);

        // ---- IPinnacleWorld ----
        public PinnacleHandle Spawn(uint wcid, PinnaclePoint at, bool withImmuneRecord, PinnacleTier tier, bool leavesCorpse = false)
        {
            Calls.Add(nameof(Spawn));

            var fail = false;

            if (FailSpawnsOfWcid.TryGetValue(wcid, out var n) && n > 0)
            {
                FailSpawnsOfWcid[wcid] = n - 1;
                fail = true;
            }
            else if (FailNextSpawns > 0)
            {
                FailNextSpawns--;
                fail = true;
            }

            var h = fail ? PinnacleHandle.None : new PinnacleHandle(nextHandle++);

            if (!h.IsNone)
            {
                Alive.Add(h);
                Health[h] = DefaultHealth;
                Positions[h] = (at.X, at.Y);
                WcidOf[h] = wcid;
            }

            Spawns.Add(new SpawnCall(wcid, at, withImmuneRecord, h, tier, leavesCorpse));
            return h;
        }

        public PinnacleHandle SpawnProp(PinnacleProp prop)
        {
            Calls.Add(nameof(SpawnProp));

            var fail = false;

            if (FailSpawnsOfWcid.TryGetValue(prop.Wcid, out var n) && n > 0)
            {
                FailSpawnsOfWcid[prop.Wcid] = n - 1;
                fail = true;
            }
            else if (FailNextProps > 0)
            {
                FailNextProps--;
                fail = true;
            }

            var h = fail ? PinnacleHandle.None : new PinnacleHandle(nextHandle++);

            if (!h.IsNone)
            {
                Alive.Add(h);
                Health[h] = DefaultHealth;
                Positions[h] = (prop.At.X, prop.At.Y);
                PositionOf[h] = prop.At;
                WcidOf[h] = prop.Wcid;

                if (prop.DoorOpen.HasValue)
                    DoorOpen[h] = prop.DoorOpen.Value;
            }

            Props.Add(new PropCall(prop, h));
            return h;
        }

        public void SetDoorOpen(PinnacleHandle h, bool open)
        {
            Calls.Add(nameof(SetDoorOpen));
            DoorOpen[h] = open;
        }

        public void PlayMotion(PinnacleHandle h, MotionStance stance, MotionCommand motion, bool hold)
        {
            Calls.Add(nameof(PlayMotion));
            Motions.Add(new MotionCall(h, stance, motion, hold));
        }

        public void PlayEffect(PinnacleHandle h, PlayScript script)
        {
            Calls.Add(nameof(PlayEffect));
            Effects.Add(new EffectCall(h, script));
        }

        public void MoveTo(PinnacleHandle h, PinnaclePoint to)
        {
            Calls.Add(nameof(MoveTo));
            MoveTos.Add(new MoveCall(h, to));
        }

        public double TurnTo(PinnacleHandle h, PinnaclePoint facing)
        {
            Calls.Add(nameof(TurnTo));
            Turns.Add(new TurnCall(h, facing));
            return TurnSeconds;
        }

        public bool TryGetPosition(PinnacleHandle h, out PinnaclePoint at)
        {
            if (Alive.Contains(h) && PositionOf.TryGetValue(h, out at))
                return true;

            at = default;
            return false;
        }

        public bool TryGetOwnerPosition(out PinnaclePoint at)
        {
            at = OwnerAt ?? default;
            return OwnerAt.HasValue;
        }

        public void Engage(PinnacleHandle h)
        {
            Calls.Add(nameof(Engage));
            Engaged.Add(h);
            Peaceful[h] = false;
        }

        public void SetHealthFloor(PinnacleHandle h, double? fraction)
        {
            Calls.Add(nameof(SetHealthFloor));
            Floors[h] = fraction;
        }

        public bool IsAtHealthFloor(PinnacleHandle h) => AtFloor.Contains(h);

        public void Slay(PinnacleHandle h)
        {
            Calls.Add(nameof(Slay));
            Slain.Add(h);
            Alive.Remove(h);
        }

        public bool ControlHazard(int token, string key, double value)
        {
            Calls.Add(nameof(ControlHazard));

            if (RefuseControlTokens.Contains(token))
                return false;

            Controls.Add((token, key, value));
            return true;
        }

        public bool TryGetHazardLevel(int token, out double level) => HazardLevel.TryGetValue(token, out level);

        public bool IsAlive(PinnacleHandle h) => Alive.Contains(h);

        public void Destroy(PinnacleHandle h)
        {
            Calls.Add(nameof(Destroy));
            Destroyed.Add(h);

            if (ThrowOnDestroy)
                throw new InvalidOperationException("fake Destroy failure");

            Alive.Remove(h);
        }

        public bool TryGetXY(PinnacleHandle h, out float x, out float y)
        {
            if (Alive.Contains(h) && Positions.TryGetValue(h, out var p))
            {
                x = p.X;
                y = p.Y;
                return true;
            }

            x = y = 0;
            return false;
        }

        public void SetImmune(PinnacleHandle h, bool immune)
        {
            Calls.Add(nameof(SetImmune));
            Immune[h] = immune;
        }

        public void SetAttackable(PinnacleHandle h, bool attackable)
        {
            Calls.Add(nameof(SetAttackable));
            Attackable[h] = attackable;
        }

        public void SetPeaceful(PinnacleHandle h, bool peaceful)
        {
            Calls.Add(nameof(SetPeaceful));
            Peaceful[h] = peaceful;
        }

        public int GetHealth(PinnacleHandle h) => Health.TryGetValue(h, out var v) ? v : 0;

        public void ClampHealth(PinnacleHandle h, int max)
        {
            Calls.Add(nameof(ClampHealth));
            Clamps.Add((h, max));

            if (Health.TryGetValue(h, out var v) && v > max)
                Health[h] = max;
        }

        public void FaceOwner(PinnacleHandle h) => Calls.Add(nameof(FaceOwner));

        public void Bob(PinnacleHandle h, Action then = null)
        {
            Calls.Add(nameof(Bob));

            if (then == null)
                return;

            if (DeferNods)
                PendingNods.Add(then);
            else
                then();
        }

        public void Cast(PinnacleHandle caster, uint spellId)
        {
            Calls.Add(nameof(Cast));
            Casts.Add((caster, spellId));
        }

        public int StartHazard(string kind, string pattern, HazardArgs args, out string error)
        {
            Calls.Add(nameof(StartHazard));

            if (RefuseNextHazards > 0)
            {
                RefuseNextHazards--;
                error = "fake refusal";
                Hazards.Add(new HazardCall(kind, pattern, args, 0));
                return 0;
            }

            error = null;
            var token = nextToken++;
            Hazards.Add(new HazardCall(kind, pattern, args, token));
            return token;
        }

        public void StopHazard(int token)
        {
            Calls.Add(nameof(StopHazard));
            StoppedTokens.Add(token);
        }

        public int DispelOwnerBuffs(int count, Random rng)
        {
            Calls.Add(nameof(DispelOwnerBuffs));
            DispelRequests.Add(count);
            var taken = Math.Min(count, BuffsAvailable);
            BuffsAvailable -= taken;
            return taken;
        }

        public void Tell(string line)
        {
            Calls.Add(nameof(Tell));
            Told.Add(line);
        }

        public void ChargeEntry()
        {
            Calls.Add(nameof(ChargeEntry));
            Charges++;
        }

        public void RefundEntry()
        {
            Calls.Add(nameof(RefundEntry));
            Refunds++;
        }

        public bool TryDeliverCache()
        {
            Calls.Add(nameof(TryDeliverCache));
            DeliverAttempts++;
            return DeliverResults.Count > 0 ? DeliverResults.Dequeue() : DeliverDefault;
        }

        /// <summary>SummonReturnPortal's result (after recording the call). Default true.</summary>
        public bool SummonPortalResult { get; set; } = true;

        /// <summary>SummonReturnPortal records the call, then throws.</summary>
        public bool ThrowOnSummonPortal { get; set; }

        public int PortalSummons { get; private set; }

        public bool SummonReturnPortal()
        {
            Calls.Add(nameof(SummonReturnPortal));
            PortalSummons++;

            if (ThrowOnSummonPortal)
                throw new InvalidOperationException("fake SummonReturnPortal failure");

            return SummonPortalResult;
        }

        public void ReportBossKilled()
        {
            Calls.Add(nameof(ReportBossKilled));
            BossKilledReports++;

            if (ThrowOnReportBossKilled)
                throw new InvalidOperationException("fake ReportBossKilled failure");
        }

        /// <summary>Every ReportEnded, in order. Deliberately NOT in <see cref="Calls"/>, so existing call-sequence asserts are unchanged.</summary>
        public List<PinnacleAttemptEnd> Ends { get; } = new List<PinnacleAttemptEnd>();

        /// <summary>ReportEnded records the call, then throws.</summary>
        public bool ThrowOnReportEnded { get; set; }

        public void ReportEnded(PinnacleAttemptEnd e)
        {
            Ends.Add(e);

            if (ThrowOnReportEnded)
                throw new InvalidOperationException("fake ReportEnded failure");
        }

        /// <summary>Every ReportCleared, in order. Deliberately NOT in <see cref="Calls"/>, so the delivery-then-portal ordering asserts are unchanged.</summary>
        public List<PinnacleClear> Clears { get; } = new List<PinnacleClear>();

        /// <summary>ReportCleared records the clear, then throws.</summary>
        public bool ThrowOnReportCleared { get; set; }

        public void ReportCleared(PinnacleClear clear)
        {
            Clears.Add(clear);

            if (ThrowOnReportCleared)
                throw new InvalidOperationException("fake ReportCleared failure");
        }

        /// <summary>Every LiftPlayersUnderPlateFloor, by its now. Deliberately NOT in <see cref="Calls"/>: it runs every tick.</summary>
        public List<double> PlateLiftChecks { get; } = new List<double>();

        /// <summary>LiftPlayersUnderPlateFloor records the call, then throws.</summary>
        public bool ThrowOnPlateLift { get; set; }

        public void LiftPlayersUnderPlateFloor(double now)
        {
            PlateLiftChecks.Add(now);

            if (ThrowOnPlateLift)
                throw new InvalidOperationException("fake plate lift failure");
        }

        public void ReportAttemptLost()
        {
            Calls.Add(nameof(ReportAttemptLost));
            AttemptLostReports++;
        }

        public void ReleaseInstance(bool ownerInside)
        {
            Calls.Add(nameof(ReleaseInstance));
            Releases.Add(ownerInside);
        }

        public void Log(string line) => Logged.Add(line);

        /// <summary>The ERROR lines alone (each is also in <see cref="Logged"/>, so a text search finds it either way).</summary>
        public List<string> Errors { get; } = new List<string>();

        public void LogError(string line)
        {
            Errors.Add(line);
            Logged.Add(line);
        }
    }
}
