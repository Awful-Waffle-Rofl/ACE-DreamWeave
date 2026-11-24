using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure rules for Hunter's Echo (Archer T3). Everything here is a function of its arguments, so the duration,
    /// the cooldown, the cloak-absorb decision and the mitigation snapshot are unit testable without a Player
    /// (Player's static initializer cannot run under the test host).
    /// </summary>
    public static class HuntersEchoMath
    {
        /// <summary>The most translucent the Echo may be made by its tunable. 1.0 would make it invisible.</summary>
        public const double MaxTranslucency = 0.95;

        /// <summary>The lowest damage multiplier a mitigation snapshot can hand the Echo (it always takes some damage).</summary>
        public const float MinMitigationFactor = 0.01f;

        /// <summary>The highest damage multiplier a mitigation snapshot can hand the Echo (a heavily debuffed owner).</summary>
        public const float MaxMitigationFactor = 2.0f;

        /// <summary>
        /// The Echo's lifetime in seconds: the base duration multiplied by the Assess Creature affinity multiplier,
        /// with the seconds the multiply ADDS capped by <paramref name="affinityCapSeconds"/> (the Pinning Shot
        /// chance shape applied to a duration). The multiplier is floored at 1.0, so a caller handing over 0.0 gets
        /// the base, never zero. A cap of 0 or less means uncapped. A non-positive base gives 0.
        /// </summary>
        public static double Duration(double baseSeconds, double affinityMultiplier, double affinityCapSeconds)
        {
            if (baseSeconds <= 0.0)
                return 0.0;

            var added = AddedSeconds(baseSeconds, affinityMultiplier, affinityCapSeconds);

            return baseSeconds + added;
        }

        /// <summary>The capped seconds the affinity multiply adds to <paramref name="baseSeconds"/> (never negative).</summary>
        public static double AddedSeconds(double baseSeconds, double affinityMultiplier, double affinityCapSeconds)
        {
            if (baseSeconds <= 0.0)
                return 0.0;

            var added = baseSeconds * Math.Max(1.0, affinityMultiplier) - baseSeconds;

            if (affinityCapSeconds > 0.0)
                added = Math.Min(added, affinityCapSeconds);

            return Math.Max(0.0, added);
        }

        /// <summary>
        /// Whether a trigger at <paramref name="now"/> may summon or refresh the Echo: always with a cooldown of 0
        /// or less, otherwise only once <paramref name="cooldownSeconds"/> have passed since the last trigger
        /// (<paramref name="lastTrigger"/>, 0 when there has never been one).
        /// </summary>
        public static bool CooldownReady(double now, double lastTrigger, double cooldownSeconds)
        {
            if (cooldownSeconds <= 0.0 || lastTrigger <= 0.0)
                return true;

            return now - lastTrigger >= cooldownSeconds;
        }

        /// <summary>
        /// THE CLOAK SPELL PROC ROLL GATE at Player.TakeDamage, where the roll runs BEFORE the health write.
        /// Today's post-write gate was "Health.Current > 0"; UpdateVitalDelta clamps at zero, so after a write of
        /// <paramref name="amount"/> health is max(0, current - amount), which is above zero exactly when
        /// current is above amount. So for a player who cannot reserve an Echo absorb this is the same decision,
        /// taken one statement earlier. A player who CAN reserve one rolls even on a hit that would kill: if the
        /// roll wins, the Echo takes that hit instead.
        /// </summary>
        public static bool ShouldRollCloakProc(uint healthCurrent, uint amount, bool canReserveEchoAbsorb)
        {
            return healthCurrent > amount || canReserveEchoAbsorb;
        }

        /// <summary>
        /// The floor-free gate over a rounded double hit: health above Math.Round(damage). Equivalent to a post-write
        /// Health.Current &gt; 0 for the same reason as <see cref="ShouldRollCloakProc(uint, uint, bool)"/>, but only for a
        /// target with no health floor; SpellProjectile.DamageTarget uses the floor-aware overload below.
        /// </summary>
        public static bool ShouldRollCloakProc(uint healthCurrent, double damage, bool canReserveEchoAbsorb)
        {
            return healthCurrent > Math.Round(damage) || canReserveEchoAbsorb;
        }

        /// <summary>
        /// The SpellProjectile.DamageTarget gate for ANY target, health floor included. A non-player creature with a
        /// P_HealthFloor (the speed-course boss floor) has its write pinned at the floor by Creature.UpdateVital, so
        /// after an overkill it is still alive and the old post-write target.IsAlive gate rolled; the plain
        /// "health above the hit" test would not. This models the write exactly - clamp at zero, then the floor rule
        /// (Creature.ClampToHealthFloor, the same function UpdateVital calls) - and asks whether health stays above 0.
        /// Pass a floor of 0 for a Player (UpdateVital never floors a player).
        /// </summary>
        public static bool ShouldRollCloakProc(uint healthCurrent, double damage, uint healthFloor, bool canReserveEchoAbsorb)
        {
            return WouldSurviveWrite(healthCurrent, damage, healthFloor) || canReserveEchoAbsorb;
        }

        /// <summary>Health after writing Math.Round(damage) is above zero, under the zero clamp and the health floor rule.</summary>
        public static bool WouldSurviveWrite(uint healthCurrent, double damage, uint healthFloor)
        {
            var written = Math.Round(damage);

            var proposed = written >= healthCurrent ? 0u : (uint)(healthCurrent - written);

            return ACE.Server.WorldObjects.Creature.ClampToHealthFloor(healthCurrent, proposed, healthFloor) > 0;
        }

        /// <summary>
        /// Metres within which a refreshed Echo already counts as on its owner, so the refresh skips the move (and with
        /// it the teleport broadcast and the client snap).
        /// </summary>
        public const double RepositionSkipDistance = 1.0;

        /// <summary>Whether a refreshed Echo <paramref name="distanceToOwner"/> metres from its owner is moved back onto them.</summary>
        public static bool ShouldReposition(double distanceToOwner) => !(distanceToOwner <= RepositionSkipDistance);

        /// <summary>
        /// What a re-trigger does with a live Echo: <see cref="EchoRefresh.Reposition"/> moves it back onto the owner
        /// (the Echo is layered on the player by design, so a refresh never leaves it standing where the owner used to
        /// be) when both share a landblock and an instance; otherwise <see cref="EchoRefresh.Replace"/> ends it and a
        /// fresh one is spawned on the owner.
        /// </summary>
        public static EchoRefresh RefreshAction(bool sameLandblock, bool sameInstance) =>
            sameLandblock && sameInstance ? EchoRefresh.Reposition : EchoRefresh.Replace;

        /// <summary>
        /// Whether a cloak spell proc that just won its roll hands its hit to the Echo: only when the roll won and
        /// the player could reserve the absorb (ability held, enabled, absorb enabled, off cooldown, a PvE attacker).
        /// </summary>
        public static bool ShouldAbsorb(bool cloakProcRolled, bool canReserveEchoAbsorb) => cloakProcRolled && canReserveEchoAbsorb;

        /// <summary>The Echo's translucency from its tunable, clamped to [0, MaxTranslucency].</summary>
        public static float Translucency(double tunable)
        {
            if (double.IsNaN(tunable))
                return 0.0f;

            return (float)Math.Clamp(tunable, 0.0, MaxTranslucency);
        }

        /// <summary>
        /// One damage type's mitigation snapshot from the owner: the armor term (1.0 for magic), the resistance term
        /// and the damage resist rating term multiplied, clamped to [MinMitigationFactor, MaxMitigationFactor]. The
        /// Echo's own weenie is neutral (no armor, every resist 1.0, no ratings), so multiplying an incoming hit by
        /// this factor approximates the owner taking it. A non-finite input degrades to 1.0 (no change).
        /// </summary>
        public static float MitigationFactor(double armorMod, double resistMod, double damageResistRatingMod)
        {
            var product = armorMod * resistMod * damageResistRatingMod;

            if (double.IsNaN(product) || double.IsInfinity(product))
                return 1.0f;

            return (float)Math.Clamp(product, MinMitigationFactor, MaxMitigationFactor);
        }

        /// <summary>
        /// The Echo's maximum health after the snapshot: it starts from the weenie's own maximum and its starting value
        /// is moved by the difference, so <paramref name="ownerMaxHealth"/> is what results. Returns the new starting
        /// value, never below 1.
        /// </summary>
        public static uint MatchedStartingValue(uint currentStartingValue, uint currentMaxValue, uint ownerMaxHealth)
        {
            var target = (long)currentStartingValue + ((long)ownerMaxHealth - currentMaxValue);

            return (uint)Math.Max(1L, Math.Min(uint.MaxValue, target));
        }

        /// <summary>
        /// The skill InitLevel that makes a creature skill's Current equal <paramref name="ownerCurrent"/>: the
        /// difference between the two is added to the existing InitLevel, never below 0.
        /// </summary>
        public static uint MatchedInitLevel(uint currentInitLevel, uint currentSkill, uint ownerCurrent)
        {
            var target = (long)currentInitLevel + ((long)ownerCurrent - currentSkill);

            return (uint)Math.Max(0L, Math.Min(uint.MaxValue, target));
        }

        // ------------------------------------------------------------ targeting (2026-11-10 retarget fix)

        /// <summary>
        /// The radar range the Echo shoots to, in metres, indoors and out alike (owner ruling 2026-11-11: "Indoor should
        /// not change the range. Projectile collision check should determine if the echo attacks the enemy"). It is the
        /// OUTDOOR radar constant (Player.MaxRadarRange_Outdoors, which also caps every spell's range); walls indoors are
        /// the sight test's job (CombatPet.HuntersEchoSightTest), not the range's. A constant, so reading it never runs
        /// Player's static initializer.
        /// </summary>
        public const float RadarRange = Player.MaxRadarRange_Outdoors;

        /// <summary>
        /// How far the Echo shoots: the full radar range, and never less than the bow's own range
        /// (Creature.GetMaxMissileRange, the old limit), so no target the Echo used to reach is lost. A non-finite or
        /// negative bow range counts as none.
        /// </summary>
        public static float EngageRange(float radarRange, float bowRange)
        {
            if (float.IsNaN(bowRange) || float.IsInfinity(bowRange) || bowRange < 0.0f)
                bowRange = 0.0f;

            return Math.Max(radarRange, bowRange);
        }

        /// <summary>
        /// The Echo's target pick, pure over its delegates. <paramref name="current"/> is kept while it is still
        /// eligible, within <paramref name="maxDistance"/> and in line of sight; otherwise (dead, gone, no longer
        /// engaged, out of range, behind a wall) the nearest candidate that is eligible, in range and in sight is taken,
        /// on this same call. <paramref name="isEligible"/> is the cheap rule (alive, a monster, engaged with the owner's
        /// side unless the owner has /summonaggro on) and runs first, so distance is only measured for eligible candidates; <paramref name="lineOfSight"/>
        /// is the expensive one (IsDirectVisible builds a physics object and runs a transition) and runs nearest first,
        /// stopping at the first that passes and never on more than <paramref name="maxSightTests"/> FRESH tests per call
        /// (the held target's own re-check is separate). Two passes share that budget: the first tests only candidates
        /// <paramref name="sightRecentlyFailed"/> does not report, so successive picks walk past a wall of near, blocked
        /// monsters to a clear farther one instead of re-testing the same nearest few forever; the second spends any
        /// budget left on the recent failures, nearest first, so a lone target that flickered behind cover for a tick is
        /// re-acquired on the next pick rather than shut out for the whole memory window (the caller remembers failures,
        /// see <see cref="HuntersEchoPickGate"/>). Returns null when nothing qualifies. A null
        /// <paramref name="lineOfSight"/> means no sight check (an Echo with no bow), and then no cap applies; a null
        /// <paramref name="sightRecentlyFailed"/> skips nothing.
        /// </summary>
        public static T SelectTarget<T>(T current, IEnumerable<T> candidates, Func<T, bool> isEligible, Func<T, float> distance,
            float maxDistance, Func<T, bool> lineOfSight, int maxSightTests, Func<T, bool> sightRecentlyFailed) where T : class
        {
            if (current != null && IsInReach(current, isEligible, distance, maxDistance, lineOfSight))
                return current;

            if (candidates == null)
                return null;

            var inRange = new List<KeyValuePair<T, float>>();

            foreach (var candidate in candidates)
            {
                // the current target was just judged above; a second sight check on it would only repeat the answer
                if (candidate == null || ReferenceEquals(candidate, current) || !isEligible(candidate))
                    continue;

                var d = distance(candidate);

                if (WithinRange(d, maxDistance))
                    inRange.Add(new KeyValuePair<T, float>(candidate, d));
            }

            inRange.Sort((a, b) => a.Value.CompareTo(b.Value));

            if (lineOfSight == null)
                return inRange.Count > 0 ? inRange[0].Key : null;

            var testsLeft = Math.Max(0, maxSightTests);

            // pass 1: candidates with no recent failure, nearest first
            List<T> recentlyFailed = null;

            foreach (var entry in inRange)
            {
                if (testsLeft == 0)
                    break;

                if (sightRecentlyFailed != null && sightRecentlyFailed(entry.Key))
                {
                    (recentlyFailed ??= new List<T>()).Add(entry.Key);
                    continue;
                }

                testsLeft--;

                if (lineOfSight(entry.Key))
                    return entry.Key;
            }

            // pass 2: whatever budget is left re-tests the recent failures, nearest first, so a target that only
            // flickered behind cover is not shut out for the whole memory window when nothing fresh is clear
            if (recentlyFailed != null)
            {
                foreach (var candidate in recentlyFailed)
                {
                    if (testsLeft == 0)
                        break;

                    testsLeft--;

                    if (lineOfSight(candidate))
                        return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// The most sight tests one pick runs, both passes together. Candidates that failed one recently are tested only
        /// with budget the fresh ones leave over (HuntersEchoPickGate.SightFailureTtlSeconds), so the next pick tests the
        /// next six first.
        /// </summary>
        public const int MaxSightTestsPerPick = 6;

        /// <summary>
        /// Seconds a failed sight test keeps a candidate from being re-tested. Longer than two empty-pick backoffs (0.6 s
        /// each), so three successive picks test three different sets of six: 18 candidates deep before the nearest set
        /// is retried.
        /// </summary>
        public const double SightFailureTtlSeconds = 1.5;

        /// <summary>Seconds an Echo holding no target waits after a pick that found nothing before it picks again.</summary>
        public const double EmptyPickBackoffSeconds = 0.6;

        /// <summary>
        /// The offset from the Echo to its target split into the flat distance and the height: positive when the target
        /// stands above the Echo, negative below. <paramref name="delta"/> is target minus Echo.
        /// </summary>
        public static (float horizontal, float height) HorizontalAndHeight(Vector3 delta)
            => ((float)Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y), delta.Z);

        private static bool IsInReach<T>(T target, Func<T, bool> isEligible, Func<T, float> distance, float maxDistance, Func<T, bool> lineOfSight)
        {
            if (!isEligible(target))
                return false;

            if (!WithinRange(distance(target), maxDistance))
                return false;

            return lineOfSight == null || lineOfSight(target);
        }

        private static bool WithinRange(float distance, float maxDistance) => !float.IsNaN(distance) && distance <= maxDistance;

        /// <summary>How much faster than the bare minimum an Echo arrow is launched, so a target at the edge of reach is hit.</summary>
        public const float ArrowSpeedMargin = 1.1f;

        /// <summary>
        /// The launch speed for one Echo arrow: the bow's own <paramref name="baseSpeed"/>, raised when that is too slow
        /// to reach a target <paramref name="horizontalDistance"/> metres away and <paramref name="heightDelta"/> metres
        /// up (negative for down). The minimum speed to reach that point under gravity g is
        /// sqrt(g * (h + sqrt(d^2 + h^2))); the result is that times <see cref="ArrowSpeedMargin"/>, never below the
        /// bow's speed and never above <paramref name="maxVelocity"/> (the physics cap). Without this a target past the
        /// bow's ballistic range (Creature.GetMaxMissileRange is v^2/g) gets no arrow at all: the trajectory solver
        /// returns a zero velocity and LaunchCosmeticMultiShotProjectile spawns nothing.
        /// </summary>
        public static float ArrowSpeed(float baseSpeed, float horizontalDistance, float heightDelta, float gravity, float maxVelocity)
        {
            var g = Math.Abs((double)gravity);
            var d = Math.Max(0.0, (double)horizontalDistance);
            var h = (double)heightDelta;

            if (double.IsNaN(d) || double.IsNaN(h) || double.IsInfinity(d) || double.IsInfinity(h) || g <= 0.0)
                return Math.Min(maxVelocity, baseSpeed);

            var needed = Math.Sqrt(g * (h + Math.Sqrt(d * d + h * h))) * ArrowSpeedMargin;

            return (float)Math.Min(maxVelocity, Math.Max(baseSpeed, needed));
        }

        // ------------------------------------------------------------ owner parity (2026-11-11)

        /// <summary>
        /// Seconds an Echo takes its snapshot weapon as still gone after a failed ownership check before it checks
        /// again (CombatPet.HuntersEchoWeaponStillOwned), so a missing weapon costs one inventory walk a second, not one
        /// a tick. A weapon put back is fired again within this long; a refresh clears it at once.
        /// </summary>
        public const double WeaponGoneRecheckSeconds = 1.0;

        /// <summary>
        /// The ammunition one Echo volley fires with (owner ruling 2026-11-11: the Echo keeps the weapon it spawned with).
        /// The snapshot's own ammo while it is still usable (owned by the owner and fits the snapshot launcher); else the
        /// ammo the owner has equipped NOW, if that fits the launcher (a stack the owner shot empty and replaced); else
        /// none, and the volley is not fired. Pure over its arguments.
        /// </summary>
        public static T SelectVolleyAmmo<T>(T snapshotAmmo, bool snapshotUsable, T equippedAmmo, bool equippedUsable) where T : class
        {
            if (snapshotAmmo != null && snapshotUsable)
                return snapshotAmmo;

            return equippedAmmo != null && equippedUsable ? equippedAmmo : null;
        }

        /// <summary>
        /// The accuracy bar an Echo shoots with when its owner has fired no missile shot this session: a full bar.
        /// A full bar is the most accurate shot (Player.GetAccuracyMod is 1.6 at 1.0) and the slowest refill (1.0 s,
        /// Player.LaunchMissile's repeat delay). That refill TERM equals the 1.0 s post-shot delay the Echo used before it
        /// took its owner's cadence (Creature.MissileDelay), but the whole cycle also runs the reload at the owner's
        /// speed, so the fallback Echo can still cycle faster than before. That is intended: it matches the owner.
        /// </summary>
        public const float DefaultShotAccuracyLevel = 1.0f;

        /// <summary>
        /// The accuracy bar the Echo takes from its owner's most recent missile shot (<paramref name="lastShotAccuracy"/>,
        /// null when the owner has fired none this session, then <see cref="DefaultShotAccuracyLevel"/>), clamped to the
        /// client's 0-1 range the way Player.HandleActionTargetedMissileAttack clamps it. A non-finite value reads as the
        /// default.
        /// </summary>
        public static float ShotAccuracyLevel(float? lastShotAccuracy)
        {
            if (lastShotAccuracy == null || float.IsNaN(lastShotAccuracy.Value) || float.IsInfinity(lastShotAccuracy.Value))
                return DefaultShotAccuracyLevel;

            return Math.Clamp(lastShotAccuracy.Value, 0.0f, 1.0f);
        }

        /// <summary>
        /// The reload animation speed the Echo takes from its owner: the speed the owner's most recent missile shot
        /// reloaded at (<paramref name="lastShotAnimSpeed"/>, Player.LaunchMissile's GetAnimSpeed, which carries the
        /// owner's Quickness, bow speed and class-ability attack speed), else <paramref name="liveAnimSpeed"/> (the
        /// owner's GetAnimSpeed for the bow it holds now). A non-positive or non-finite value falls through to the next;
        /// 1.0 when neither is usable.
        /// </summary>
        public static float ShotAnimSpeed(float? lastShotAnimSpeed, float liveAnimSpeed)
        {
            if (lastShotAnimSpeed != null && IsUsableSpeed(lastShotAnimSpeed.Value))
                return lastShotAnimSpeed.Value;

            return IsUsableSpeed(liveAnimSpeed) ? liveAnimSpeed : 1.0f;
        }

        private static bool IsUsableSpeed(float speed) => !float.IsNaN(speed) && !float.IsInfinity(speed) && speed > 0.0f;

        /// <summary>
        /// Seconds from one Echo shot to the next: the player's repeat-attack cycle (Player.LaunchMissile), term for
        /// term - the aim motion (<paramref name="launchTime"/>), the reload at the owner's speed
        /// (<paramref name="reloadTime"/>), the return to Ready (<paramref name="readyTime"/>), the reload-to-ready link
        /// (<paramref name="linkTime"/>) and the accuracy bar's refill, which Player.LaunchMissile waits out as
        /// AccuracyLevel seconds. A negative or non-finite term counts as 0.
        /// </summary>
        public static double ShotCycleSeconds(float launchTime, float reloadTime, float readyTime, float linkTime, float accuracyLevel)
        {
            return NonNegative(launchTime) + NonNegative(reloadTime) + NonNegative(readyTime) + NonNegative(linkTime) + NonNegative(accuracyLevel);
        }

        private static double NonNegative(float value) => float.IsNaN(value) || float.IsInfinity(value) || value < 0.0f ? 0.0 : value;

        /// <summary>
        /// The owner's chat line for an Echo arrow that hit: "[prefixes]&lt;echo&gt; deals N points of &lt;type&gt; damage to
        /// &lt;target&gt;!", in place of the client-built "You singe ..." attacker notification. The prefixes are the ones the
        /// server's own hit lines carry (SpellProjectile.DamageTarget: "Critical hit! ", "Overpower! ", "Sneak Attack! "),
        /// plus "Recklessness! " for the fourth attack condition; a critical hit the target's augmentation avoided gets
        /// the spell line's suffix. A damage type with no single name (a multi-type value) drops the type word.
        /// </summary>
        public static string FormatEchoHitMessage(string echoName, uint damage, DamageType damageType, string targetName, bool critical, AttackConditions conditions)
        {
            var prefix = (critical ? "Critical hit! " : "")
                + ((conditions & AttackConditions.Overpower) != 0 ? "Overpower! " : "")
                + ((conditions & AttackConditions.SneakAttack) != 0 ? "Sneak Attack! " : "")
                + ((conditions & AttackConditions.Recklessness) != 0 ? "Recklessness! " : "");

            var typeName = damageType.GetName();
            var typeWord = string.IsNullOrEmpty(typeName) ? "" : typeName.ToLowerInvariant() + " ";

            var suffix = (conditions & AttackConditions.CriticalProtectionAugmentation) != 0 ? " Your critical hit was avoided with their augmentation!" : "";

            return $"{prefix}{echoName} deals {damage} points of {typeWord}damage to {targetName}!{suffix}";
        }
    }

    /// <summary>
    /// The owner's REAL missile weapon, launcher and ammo-slot item the Echo fires with (owner ruling 2026-11-11: "Echo
    /// should continue firing with the weapon that was held when it spawned"). Snapshotted at spawn and at refresh
    /// (Player.CaptureHuntersEchoWeapons); the Echo's wielded clones are only its look. While an Echo volley is built
    /// and while an Echo arrow resolves, the owner's equipment getters answer from this set
    /// (Creature.HuntersEchoWeaponScope), so every read on those paths sees this weapon whatever the owner holds now.
    /// Immutable; runtime only.
    /// </summary>
    public sealed class HuntersEchoWeapons
    {
        /// <summary>The missile weapon slot item: the launcher, or the thrown weapon itself.</summary>
        public WorldObject Weapon { get; }

        /// <summary>The launcher (a MissileLauncher), or null for a thrown weapon.</summary>
        public WorldObject Launcher { get; }

        /// <summary>The ammo-slot item (arrows, bolts, darts), or null for a thrown weapon.</summary>
        public WorldObject AmmoSlot { get; }

        /// <summary>
        /// The Echo that fires with this set (owner ruling 2026-11-11: "Reflect should hit the echo if it fired the
        /// shot, not the main player"). Retaliation provoked inside the scope goes here (HuntersEchoRetaliation), or is
        /// dropped when it is gone. Null for a set not tied to a shooter.
        /// </summary>
        public WorldObject Shooter { get; }

        public HuntersEchoWeapons(WorldObject weapon, WorldObject launcher, WorldObject ammoSlot, WorldObject shooter = null)
        {
            Weapon = weapon;
            Launcher = launcher;
            AmmoSlot = ammoSlot;
            Shooter = shooter;
        }

        /// <summary>The projectile item the volley launches: the ammo for a launcher, the weapon itself when thrown.</summary>
        public WorldObject Ammo => Weapon != null && Weapon.IsAmmoLauncher ? AmmoSlot : Weapon;

        /// <summary>This set with <paramref name="ammoSlot"/> in the ammo slot (the volley's resolved ammo).</summary>
        public HuntersEchoWeapons WithAmmo(WorldObject ammoSlot) => new HuntersEchoWeapons(Weapon, Launcher, ammoSlot, Shooter);

        /// <summary>This set fired by <paramref name="shooter"/> (the Echo that snapshotted it).</summary>
        public HuntersEchoWeapons WithShooter(WorldObject shooter) => new HuntersEchoWeapons(Weapon, Launcher, AmmoSlot, shooter);

        /// <summary>
        /// The set an Echo arrow was fired with, read back off its cosmetic projectile (LaunchProjectile stamps the
        /// launcher and ammo it was given): a launched arrow's launcher is the weapon and its ammo the ammo slot; a thrown
        /// weapon has no launcher and is its own ammo. The shooter is the projectile's source (the Echo, stamped by
        /// LaunchProjectile). Null when the projectile carries neither launcher nor ammo.
        /// </summary>
        public static HuntersEchoWeapons FromProjectile(WorldObject projectile)
        {
            if (projectile == null)
                return null;

            var launcher = projectile.ProjectileLauncher;
            var ammo = projectile.ProjectileAmmo;

            if (launcher == null && ammo == null)
                return null;

            return launcher != null ? new HuntersEchoWeapons(launcher, launcher, ammo, projectile.ProjectileSource) : new HuntersEchoWeapons(ammo, null, null, projectile.ProjectileSource);
        }
    }

    /// <summary>
    /// What one Echo shot carries from its owner, snapshotted at spawn and at refresh (CombatPet.SnapshotHuntersEchoShot)
    /// and stamped on every arrow it fires (WorldObject.HuntersEchoShot, Creature.LaunchProjectile), so an arrow still in
    /// flight when the Echo ends or is refreshed resolves with the values it was fired with. Immutable; runtime only.
    /// </summary>
    public sealed class HuntersEchoShot
    {
        /// <summary>The Echo's name for the owner's hit line ("&lt;owner&gt;'s Echo"), carried so a destroyed Echo is never read.</summary>
        public string EchoName { get; }

        /// <summary>The 0-1 accuracy bar the arrow resolves with (HuntersEchoMath.ShotAccuracyLevel).</summary>
        public float AccuracyLevel { get; }

        /// <summary>The reload animation speed the Echo's shots cycle at (HuntersEchoMath.ShotAnimSpeed).</summary>
        public float AnimSpeed { get; }

        public HuntersEchoShot(string echoName, float accuracyLevel, float animSpeed)
        {
            EchoName = echoName;
            AccuracyLevel = accuracyLevel;
            AnimSpeed = animSpeed;
        }

        /// <summary>
        /// Resolves a shot from the owner's recorded last shot (null values when there is none) and its live anim speed.
        /// </summary>
        public static HuntersEchoShot Resolve(string echoName, float? lastShotAccuracy, float? lastShotAnimSpeed, float liveAnimSpeed)
        {
            return new HuntersEchoShot(echoName,
                HuntersEchoMath.ShotAccuracyLevel(lastShotAccuracy),
                HuntersEchoMath.ShotAnimSpeed(lastShotAnimSpeed, liveAnimSpeed));
        }
    }

    /// <summary>
    /// The Echo's empty-pick backoff (HuntersEchoMath.EmptyPickBackoffSeconds): once a pick with no held target finds
    /// nothing, the next pick waits for the backoff, so an Echo surrounded by walled-off monsters does not pay for up to
    /// MaxSightTestsPerPick sight tests on every 0.2 s tick. A held target is re-checked every tick regardless, and a
    /// refresh (<see cref="Reset"/>) picks at once. It also remembers which candidates failed a sight test, by guid,
    /// for HuntersEchoMath.SightFailureTtlSeconds, so the next pick tests them only after the fresh ones (SelectTarget's
    /// sightRecentlyFailed). Runtime state for one Echo, touched only on the Echo's own tick and refresh; pure over the
    /// times it is given.
    /// </summary>
    public sealed class HuntersEchoPickGate
    {
        /// <summary>Entries kept at most; past this, expired ones are pruned first and then the oldest.</summary>
        public const int MaxRememberedFailures = 64;

        private double nextPickAt;

        private readonly Dictionary<uint, double> sightFailedUntil = new Dictionary<uint, double>();

        /// <summary>How many sight failures are remembered right now (expired ones included until the next prune).</summary>
        public int RememberedFailures => sightFailedUntil.Count;

        /// <summary>Whether <paramref name="id"/> failed a sight test within the last SightFailureTtlSeconds.</summary>
        public bool SightRecentlyFailed(uint id, double now) => sightFailedUntil.TryGetValue(id, out var until) && now < until;

        /// <summary>Remembers that <paramref name="id"/> failed a sight test at <paramref name="now"/>, pruning expired entries.</summary>
        public void RecordSightFailure(uint id, double now)
        {
            Prune(now);

            if (sightFailedUntil.Count >= MaxRememberedFailures && !sightFailedUntil.ContainsKey(id))
            {
                var oldestKey = 0u;
                var oldest = double.MaxValue;

                foreach (var kvp in sightFailedUntil)
                {
                    if (kvp.Value < oldest)
                    {
                        oldest = kvp.Value;
                        oldestKey = kvp.Key;
                    }
                }

                sightFailedUntil.Remove(oldestKey);
            }

            sightFailedUntil[id] = now + HuntersEchoMath.SightFailureTtlSeconds;
        }

        private void Prune(double now)
        {
            List<uint> expired = null;

            foreach (var kvp in sightFailedUntil)
            {
                if (now >= kvp.Value)
                    (expired ??= new List<uint>()).Add(kvp.Key);
            }

            if (expired != null)
                foreach (var key in expired)
                    sightFailedUntil.Remove(key);
        }

        /// <summary>Whether to run the pick now: always while a target is held, otherwise once the backoff has passed.</summary>
        public bool ShouldPick(bool holdsTarget, double now) => holdsTarget || now >= nextPickAt;

        /// <summary>Records a pick's outcome: an empty one starts the backoff, a found target clears it.</summary>
        public void OnPicked(bool found, double now) => nextPickAt = found ? 0.0 : now + HuntersEchoMath.EmptyPickBackoffSeconds;

        /// <summary>Clears the backoff and the remembered sight failures (a refresh engages at once, from a fresh look).</summary>
        public void Reset()
        {
            nextPickAt = 0.0;
            sightFailedUntil.Clear();
        }
    }

    /// <summary>See <see cref="HuntersEchoMath.RefreshAction"/>.</summary>
    public enum EchoRefresh
    {
        Reposition,
        Replace,
    }
}
