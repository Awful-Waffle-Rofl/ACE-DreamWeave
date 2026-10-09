using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Objective Locks (WaffleACE): the wiring that connects the pure counter in
    /// ACE.Server.Entity.ObjectiveLock to live world objects.
    /// <para/>
    /// The shape is two roles bound by one authored string:
    /// <list type="bullet">
    /// <item>a GATE is any world object carrying PropertyInt.ObjectiveLockRequired greater than zero.
    /// Carrying that property is what MAKES it the gate - there is no separate weenie type, so a door, a
    /// chest or a piece of scenery can all be one.</item>
    /// <item>a CONTRIBUTOR is any world object carrying the same PropertyString.ObjectiveLockKey. It fires
    /// either by being activated (WorldObject_Use.OnActivate) or by dying (Creature_Death.Die).</item>
    /// </list>
    /// The lock instance itself hangs off the GATE, in the P_ObjectiveLock field below, and is allocated
    /// lazily on the first contribution. Nothing about it is persisted: it is deliberately in-memory only,
    /// so a per-run ephemeral instance starts every run with an unsolved puzzle and no cleanup step.
    /// <para/>
    /// THREADING. ObjectiveLock has no internal synchronization and needs none here. A contributor only
    /// ever resolves a gate inside its OWN landblock (see ContributeToObjectiveLock), so a lock and every
    /// contribution to it belong to one landblock; LandblockManager.TickMultiThreadedWork parallelizes over
    /// landblock GROUPS and then walks each group's landblocks serially (LandblockManager.cs:425-433), so a
    /// single landblock is only ever ticked by one thread at a time. Both call sites - activation and
    /// creature death - run inside that tick.
    /// </summary>
    partial class WorldObject
    {
        /// <summary>
        /// The shared string binding a gate to its contributors, e.g. "lca_pentagon". Carried by the gate
        /// AND by every contributor; a contribution is only ever routed to a gate whose key matches.
        /// </summary>
        public string ObjectiveLockKey
        {
            get => GetProperty(PropertyString.ObjectiveLockKey);
            set { if (value == null) RemoveProperty(PropertyString.ObjectiveLockKey); else SetProperty(PropertyString.ObjectiveLockKey, value); }
        }

        /// <summary>
        /// This contributor's token identity. Unset is the intended common case - see
        /// ResolveObjectiveTokenKey for why the fallback is the contributor's own guid.
        /// </summary>
        public string ObjectiveLockToken
        {
            get => GetProperty(PropertyString.ObjectiveLockToken);
            set { if (value == null) RemoveProperty(PropertyString.ObjectiveLockToken); else SetProperty(PropertyString.ObjectiveLockToken, value); }
        }

        /// <summary>
        /// The summed live-token weight this gate must reach to open. Carried by the GATE only; a value
        /// greater than zero is the sole marker that an object is a gate rather than a contributor.
        /// </summary>
        public int? ObjectiveLockRequired
        {
            get => GetProperty(PropertyInt.ObjectiveLockRequired);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.ObjectiveLockRequired); else SetProperty(PropertyInt.ObjectiveLockRequired, value.Value); }
        }

        /// <summary>
        /// How much this contributor's token is worth toward the gate's ObjectiveLockRequired. Unset
        /// means 1.
        /// </summary>
        public int? ObjectiveLockWeight
        {
            get => GetProperty(PropertyInt.ObjectiveLockWeight);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.ObjectiveLockWeight); else SetProperty(PropertyInt.ObjectiveLockWeight, value.Value); }
        }

        /// <summary>
        /// Seconds this contributor's token survives before it stops counting. Unset or non-positive means
        /// it never expires. This is how "three levers held down at the same time" is authored.
        /// </summary>
        public double? ObjectiveLockExpiry
        {
            get => GetProperty(PropertyFloat.ObjectiveLockExpiry);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.ObjectiveLockExpiry); else SetProperty(PropertyFloat.ObjectiveLockExpiry, value.Value); }
        }

        /// <summary>
        /// This contributor CLEARS the gate's progress instead of adding to it - the wrong-answer
        /// punishment. It cannot re-close a gate that has already opened; ObjectiveLock.Reset is a no-op
        /// once the lock is latched.
        /// </summary>
        public bool ObjectiveLockResets
        {
            get => GetProperty(PropertyBool.ObjectiveLockResets) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ObjectiveLockResets); else SetProperty(PropertyBool.ObjectiveLockResets, value); }
        }

        /// <summary>
        /// Objective Locks (WaffleACE): the live counter for the puzzle this object GATES, allocated lazily
        /// on the first contribution routed here and null on everything else in the world. Purely
        /// in-memory and NEVER persisted - the same back-reference convention as Creature.P_WaveOwner and
        /// Creature.P_WorldEvent (Creature.cs:33,42).
        /// <para/>
        /// Not persisting it is the design, not an omission: the intended home for these gates is a
        /// per-run ephemeral instance, which is rebuilt from scratch each run, so the puzzle resets for
        /// free and there is no stale "already solved" state to carry forward.
        /// </summary>
        public ObjectiveLock P_ObjectiveLock;

        /// <summary>
        /// True when this object is a gate. Carrying a positive ObjectiveLockRequired is the whole
        /// definition; see the class remarks.
        /// </summary>
        public bool IsObjectiveGate => (ObjectiveLockRequired ?? 0) > 0;

        // =========================================================================================
        // Pure decision logic. Split out as statics so the rules that decide what a contribution IS
        // can be unit-tested with no landblock, no database and no world - see
        // ACE.Server.Tests/ObjectiveLockWiringTests.cs. Everything below this line that touches a
        // WorldObject is a thin shell over these.
        // =========================================================================================

        /// <summary>
        /// The token identity a contribution is filed under. An authored ObjectiveLockToken wins; when it
        /// is absent the contributor's own guid is used.
        /// <para/>
        /// That default is load-bearing rather than a convenience. ObjectiveLock replaces a repeat token
        /// instead of accumulating it, so the token key is exactly "which distinct contributor is this".
        /// Defaulting to the guid makes six creatures in a room six distinct tokens with zero per-creature
        /// authoring, while three levers that should collectively count once can still be given one shared
        /// explicit token.
        /// </summary>
        public static string ResolveObjectiveTokenKey(string authoredToken, uint contributorGuid)
        {
            if (!string.IsNullOrWhiteSpace(authoredToken))
                return authoredToken;

            // Prefixed rather than a bare hex string so an authored token can never collide with a
            // guid-derived one by accident.
            return $"guid:0x{contributorGuid:X8}";
        }

        /// <summary>
        /// A contribution's weight. Unset means 1, which is what makes "kill all six" author as nothing
        /// more than a key on each creature and Required = 6 on the door.
        /// <para/>
        /// An explicitly authored 0 is passed through unchanged: it is indistinguishable from a deliberate
        /// "this one is scenery in the count" and is not this layer's to second-guess.
        /// </summary>
        public static double ResolveObjectiveWeight(int? authoredWeight)
        {
            return authoredWeight ?? 1;
        }

        /// <summary>
        /// Converts an authored expiry in SECONDS into the absolute expiry instant ObjectiveLock wants, or
        /// null for a token that never expires.
        /// <para/>
        /// Non-positive collapses to null (never expires) rather than to "already expired". An expiry of 0
        /// is how content spells "no expiry" in a schema where every numeric property defaults to 0, and
        /// the alternative reading would make every such token dead on arrival.
        /// </summary>
        public static DateTime? ResolveObjectiveExpiry(double? expirySeconds, DateTime now)
        {
            if (!expirySeconds.HasValue || expirySeconds.Value <= 0)
                return null;

            return now.AddSeconds(expirySeconds.Value);
        }

        /// <summary>
        /// Picks the gate for <paramref name="key"/> out of a landblock's candidates: the entry whose key
        /// matches exactly and whose Required is positive, lowest guid first.
        /// <para/>
        /// Lowest guid is not arbitrary. Two gates sharing one key is a content error, and the damaging
        /// version of that error is one that resolves DIFFERENTLY run to run - half the contributions
        /// landing on one door and half on the other, producing a puzzle that is unsolvable only
        /// sometimes. A total order over guids makes the broken content behave the same way every time, so
        /// it can be reported and reproduced; <paramref name="ambiguous"/> is what carries the report.
        /// </summary>
        /// <param name="ambiguous">Set TRUE when more than one candidate matched, i.e. the content is
        /// mis-authored and the caller should log it.</param>
        /// <returns>The winning gate's guid, or null when nothing matched.</returns>
        public static uint? SelectObjectiveGateGuid(IReadOnlyList<(uint Guid, string Key, int Required)> candidates, string key, out bool ambiguous)
        {
            ambiguous = false;

            if (candidates == null || string.IsNullOrWhiteSpace(key))
                return null;

            uint? winner = null;
            var matches = 0;

            foreach (var candidate in candidates)
            {
                if (candidate.Required <= 0)
                    continue;

                // Ordinal: these are authored content identifiers, not display text. A culture-sensitive
                // comparison could bind a gate to a contributor whose key merely looks the same.
                if (!string.Equals(candidate.Key, key, StringComparison.Ordinal))
                    continue;

                matches++;

                if (!winner.HasValue || candidate.Guid < winner.Value)
                    winner = candidate.Guid;
            }

            ambiguous = matches > 1;

            return winner;
        }

        // =========================================================================================
        // Live wiring.
        // =========================================================================================

        /// <summary>
        /// Files this object's contribution against its gate, and opens the gate if that contribution is
        /// the one that satisfies it. Called from the two contributor events: WorldObject_Use.OnActivate
        /// and Creature_Death.Die.
        /// <para/>
        /// Safe to call on anything - every way this is not a real contribution exits without side
        /// effects. Both call sites still guard on a cheap ObjectiveLockKey read first, so the overwhelming
        /// majority of activations and deaths never reach this method at all.
        /// </summary>
        /// <param name="notify">The player to show progress to, or null when there is nobody to tell (a
        /// pressure plate tripped by a monster, a creature killed by the environment). The contribution
        /// itself does not need a player.</param>
        public void ContributeToObjectiveLock(Player notify)
        {
            var key = ObjectiveLockKey;

            if (string.IsNullOrWhiteSpace(key))
                return;

            // A gate carries the key too - that is how it is found - so without this guard a gate would be
            // its own contributor: activating the door would count toward opening the door. It also closes
            // the recursion, because the non-Door open path below calls gate.OnActivate, which re-enters
            // this method on the gate itself.
            if (IsObjectiveGate)
                return;

            var landblock = CurrentLandblock;

            if (landblock == null)
            {
                // Reached when the contributor has already been pulled out of the world (Landblock sets
                // CurrentLandblock to null on removal, Landblock.cs:1319,1348,1443). There is no landblock
                // to scan, so there is no gate to find - a contribution here is simply lost, which is worth
                // saying out loud rather than swallowing.
                log.Warn($"[OBJECTIVE] {Name} (0x{Guid}, wcid {WeenieClassId}) tried to contribute to objective lock '{key}' with no CurrentLandblock - the contribution is dropped.");
                return;
            }

            // ToList() snapshot, documented as taken specifically to avoid cross-thread issues
            // (Landblock.GetAllWorldObjectsForDiagnostics, Landblock.cs:1516-1521). Deliberately NOT
            // memoized on the contributor: contributions per run number in the low tens, a puzzle room
            // holds a few dozen objects, and a cached gate reference would be a dangling pointer the
            // moment the gate is destroyed or the instance rebuilt.
            var candidates = new List<(uint Guid, string Key, int Required)>();
            var candidateObjects = new Dictionary<uint, WorldObject>();

            foreach (var wo in landblock.GetAllWorldObjectsForDiagnostics())
            {
                var woKey = wo.ObjectiveLockKey;

                if (woKey == null)
                    continue;

                candidates.Add((wo.Guid.Full, woKey, wo.ObjectiveLockRequired ?? 0));
                candidateObjects[wo.Guid.Full] = wo;
            }

            var gateGuid = SelectObjectiveGateGuid(candidates, key, out var ambiguous);

            if (!gateGuid.HasValue)
            {
                log.Warn($"[OBJECTIVE] {Name} (0x{Guid}, wcid {WeenieClassId}) contributes to objective lock '{key}' but landblock 0x{landblock.Id.Raw:X8} (instance 0x{landblock.Instance:X8}) holds no object with that ObjectiveLockKey and a positive ObjectiveLockRequired - no gate, contribution dropped.");
                return;
            }

            if (ambiguous)
                log.Warn($"[OBJECTIVE] objective lock key '{key}' is claimed by more than one gate in landblock 0x{landblock.Id.Raw:X8} (instance 0x{landblock.Instance:X8}). This is a content error - one key names one gate. Routing every contribution to the lowest guid, 0x{gateGuid.Value:X8}, so the behaviour at least stays the same on every run.");

            var gate = candidateObjects[gateGuid.Value];

            var now = DateTime.UtcNow;

            if (ObjectiveLockResets)
            {
                // Null-conditional on purpose: no lock allocated means no progress has been made, and
                // punishing a player for a wrong answer they gave first is nothing to do. Reset is also a
                // no-op once latched, so this can never re-close an opened gate.
                gate.P_ObjectiveLock?.Reset();

                if (notify != null && gate.P_ObjectiveLock != null && !gate.P_ObjectiveLock.Latched)
                    notify.Session.Network.EnqueueSend(new GameMessageSystemChat("Something grinds back into place. The workings have forgotten your progress.", ChatMessageType.Broadcast));

                return;
            }

            var objectiveLock = gate.P_ObjectiveLock ??= new ObjectiveLock(gate.ObjectiveLockRequired ?? 0);

            var satisfied = objectiveLock.Contribute(
                ResolveObjectiveTokenKey(ObjectiveLockToken, Guid.Full),
                ResolveObjectiveWeight(ObjectiveLockWeight),
                now,
                ResolveObjectiveExpiry(ObjectiveLockExpiry, now));

            if (satisfied)
            {
                if (!landblock.IsEphemeral)
                {
                    // These gates are MEANT to be reusable in ordinary dungeons, so this warns rather than
                    // refuses. The hazard it names is real though: opening the gate writes to the gate's
                    // biota (a Door has its Locked property cleared), and outside an ephemeral instance
                    // that write is a candidate to persist to the shard (Landblock.SaveDB skips ephemeral
                    // landblocks outright, Landblock.cs:1669-1673). Note how SOON that lands: SaveDB is not
                    // only an unload-time call, it also runs from the landblock heartbeat on a timer
                    // (Landblock.cs:1002-1007), so the exposure is the next save tick, not "if this landblock
                    // ever unloads". A persisted open gate is a puzzle nobody ever solves again.
                    log.Warn($"[OBJECTIVE] objective lock '{key}' was satisfied in NON-ephemeral landblock 0x{landblock.Id.Raw:X8} (instance 0x{landblock.Instance:X8}); gate {gate.Name} (0x{gate.Guid}). The gate's opened state may persist to the shard and leave this puzzle permanently solved. Ephemeral instances are the intended home for objective locks.");
                }

                // The progress line below never fires for the contribution that finishes the job (it is
                // guarded on !Latched, and Contribute latches on the satisfying call), so without this the
                // last kill or lever of a puzzle is the ONLY one that says nothing at all. Reported from
                // play: every step counted up and then the gate opened in silence.
                if (notify != null)
                    notify.Session.Network.EnqueueSend(new GameMessageSystemChat($"{gate.Name} has opened!", ChatMessageType.Broadcast));

                OpenObjectiveGate(gate, this);
                return;
            }

            // Progress, but only while the gate is genuinely still shut. Once latched, Contribute always
            // returns false, and a "3 of 3" line trailing a door that already opened reads as a bug.
            if (notify != null && !objectiveLock.Latched)
            {
                var current = objectiveLock.CurrentWeight(now);
                notify.Session.Network.EnqueueSend(new GameMessageSystemChat($"The workings shift. {FormatObjectiveProgress(current)} of {FormatObjectiveProgress(objectiveLock.Required)}.", ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// Weights are doubles because ObjectiveLock sums doubles, but essentially all authored content
        /// counts whole things - bells, levers, corpses. Trim the decimals when there are none so the
        /// common case reads "2 of 3" rather than "2.00 of 3.00".
        /// </summary>
        private static string FormatObjectiveProgress(double value)
        {
            return value.ToString("0.##");
        }

        /// <summary>
        /// Opens a satisfied gate. Runs exactly once per lock, because ObjectiveLock.Contribute returns
        /// TRUE only on the transition into the satisfied state and latches afterwards.
        /// Internal because PuzzleGateManager opens its gate through the same path, once, on its own latch.
        /// The Door branch never consults PropertyInt.Active, so an Active = 0 gate (unclickable by players)
        /// still opens here.
        /// </summary>
        internal static void OpenObjectiveGate(WorldObject gate, WorldObject contributor)
        {
            if (gate is Door door)
            {
                // A Door needs its own path because a bare activation will NOT open a locked one:
                // Door.ActOnUse refuses on IsLocked and shows "The door is locked!" (Door.cs:93,108-116),
                // and nothing on the activation path unlocks it. So clear the lock first, tell clients
                // about it (they render and message the locked state themselves), then open directly.
                if (door.IsLocked)
                {
                    door.IsLocked = false;
                    door.EnqueueBroadcast(new GameMessagePublicUpdatePropertyBool(door, PropertyBool.Locked, false));
                }

                // Retire any auto-close chain already armed against this door. A player standing on the far
                // side can open even a LOCKED door (Door.ActOnUse's `behind` case, Door.cs:93), which arms
                // a Reset that closes it and re-locks it if DefaultLocked (Door.cs:210-222). Reset is
                // keyed on the UseTimestamp captured when it was armed, so bumping the stamp here makes
                // that pending chain a no-op; without it a gate solved a few seconds later could re-lock
                // itself. Door.Open only bumps the stamp when it actually moves the door, so it cannot be
                // relied on to do this for an already-open door.
                door.UseTimestamp = Time.GetUnixTime();

                door.Open(contributor.Guid);

                // Note what is NOT reachable here: Door.ActOnUse toggles an open door CLOSED on a second
                // activation by a non-Switch (Door.cs:97-98). This method never goes through ActOnUse, and
                // the latch means it is never called twice for one lock, so the puzzle can never close its
                // own gate. A player double-clicking the door afterwards still toggles it like any other
                // unlocked door - that is ordinary door behaviour and it stays unlocked, so the puzzle
                // remains solved.
                return;
            }

            // Everything else goes through the generic activation path, so a gate can be whatever the
            // dungeon needs - a chest, a lever, a piece of scenery with an ActivationResponse of Emote or
            // Generate, or an object with a linked ActivationTarget. Passing the contributor as the
            // activator (rather than the player) keeps the causal chain honest: the last bell rung is what
            // opened the gate.
            //
            // Content note: a couple of types filter their activator and would ignore a non-creature one -
            // Switch.OnActivate returns unless the activator is a Creature (Switch.cs:43) and
            // PressurePlate.OnActivate unless it is a Player (PressurePlate.cs:63). Neither makes sense
            // as a gate anyway; author gates as a Door or a plain WorldObject.
            gate.OnActivate(contributor);
        }
    }
}
