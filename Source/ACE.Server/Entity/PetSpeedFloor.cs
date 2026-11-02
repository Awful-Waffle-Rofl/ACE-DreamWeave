using System;
using System.Threading;

using log4net;

using ACE.Server.Physics.Animation;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Combat pet movement-speed FLOOR (WaffleACE). Every CombatPet - essence summons and class-ability summons
    /// alike - moves at least as fast as the reference wasp swarm summon. A pet that is already faster is left
    /// exactly as it is: this only ever raises a speed, never lowers one.
    ///
    /// The lever is MovementParameters.Speed on the server mirrored by MoveToParameters.Speed on the wire, the
    /// same pair base ACE uses for the player melee charge (1.5f) and Pinning Shot uses for its slow. The Run skill
    /// is NOT usable for this: MovementSystem.GetRunRate caps at 4.5 at 800 skill, and a monster's MoveSpeed and
    /// RunRate are cached once behind "if (MoveSpeed == 0.0f)".
    ///
    /// Pure apart from the one lazy dat read in ReferenceSpeed, so the arithmetic is testable without dats.
    /// </summary>
    public static class PetSpeedFloor
    {
        /// <summary>The server tunable that switches the floor off. Default TRUE.</summary>
        public const string EnabledKey = "pet_speed_floor";

        // REFERENCE CONSTANTS. These mirror the level 200 wasp swarm summon, wcid 49135 ace49135-swarm (its siblings
        // 49142 / 49149 / 49156 carry identical values): WeenieType 71 (CombatPet), PropertyDataId.MotionTable
        // 150995304 (0x09000168), PropertyFloat.DefaultScale 0.8, Quickness init 160, and no Run skill row, so its
        // Run skill is the attribute formula alone. Copied as constants rather than read from the world DB, so no
        // database is touched on any movement path. If that weenie is retuned, update these to match.
        public const uint ReferenceMotionTableId = 150995304;
        public const float ReferenceObjScale = 0.8f;
        public const int ReferenceRunSkill = 160;

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The resolved reference speed, or 0 while unresolved. Only ever written with a POSITIVE value, so a
        /// failed resolve (dat not loaded yet) is retried on the next call instead of disabling the floor for
        /// the process lifetime. A float write is atomic and the value is idempotent, so concurrent resolves on
        /// different landblock threads at worst compute it twice; volatile makes the cached value visible.
        /// </summary>
        private static volatile float referenceSpeed;

        /// <summary>1 once the "reference speed is 0" warning has been logged, so it is logged once per process.</summary>
        private static int zeroSpeedWarned;

        /// <summary>
        /// Where CombatPet reads the target speed from. ReferenceSpeed in production; a seam so ACE.Server.Tests
        /// can drive MovementSpeedFactor without dats. A test that replaces it must restore it in a finally.
        /// </summary>
        public static Func<float> ReferenceSpeedSource = () => ReferenceSpeed;

        /// <summary>
        /// The reference ground speed in metres per second, computed exactly the way
        /// Monster_Navigation.GetMovementSpeed computes a monster's MoveSpeed:
        /// GetRunSpeed(motion table) * GetRunRate(0 burden, run skill, 1) * scale.
        ///
        /// Cached for the process lifetime once it resolves to a positive value. 0 when the motion table cannot
        /// be read (no dats loaded, or the table has no RunForward cycle); a 0 reference disables the floor
        /// (GetFactor returns 1.0), is NOT cached, and logs one warning per process. Each CombatPet caches its
        /// own factor on its first move, so a retry costs one dat lookup per newly summoned pet, never per tick.
        /// </summary>
        public static float ReferenceSpeed
        {
            get
            {
                var cached = referenceSpeed;
                if (cached > 0.0f)
                    return cached;

                float runSpeed;
                Exception error = null;

                try
                {
                    runSpeed = MotionTable.GetRunSpeed(ReferenceMotionTableId);
                }
                catch (Exception ex)
                {
                    // DatManager.PortalDat is null outside a booted server (unit tests); treat it as "dat missing",
                    // which disables the floor rather than failing the move
                    runSpeed = 0.0f;
                    error = ex;
                }

                var speed = ComputeReferenceSpeed(runSpeed);

                if (speed > 0.0f)
                {
                    referenceSpeed = speed;
                    return speed;
                }

                if (Interlocked.Exchange(ref zeroSpeedWarned, 1) == 0)
                {
                    var reason = error != null ? $"{error.GetType().Name}: {error.Message}" : $"GetRunSpeed returned {runSpeed}";
                    log.Warn($"PetSpeedFloor: reference speed for motion table 0x{ReferenceMotionTableId:X8} resolved to 0 ({reason}); the combat pet speed floor is inactive for pets summoned until it resolves");
                }

                return 0.0f;
            }
        }

        /// <summary>
        /// The reference speed for a given RunForward anim speed (MotionTable.GetRunSpeed of the reference table).
        /// A non-positive or non-finite anim speed yields 0, which disables the floor.
        /// </summary>
        public static float ComputeReferenceSpeed(float runAnimSpeed)
        {
            if (!(runAnimSpeed > 0.0f) || float.IsInfinity(runAnimSpeed))
                return 0.0f;

            var runRate = (float)MovementSystem.GetRunRate(0.0f, ReferenceRunSkill, 1.0f);

            return runAnimSpeed * runRate * ReferenceObjScale;
        }

        /// <summary>
        /// The speed multiplier that brings a pet moving at <paramref name="petSpeed"/> m/s up to
        /// <paramref name="targetSpeed"/> m/s: max(1.0, target / pet). Exactly 1.0 - no change - when the floor is
        /// disabled, when either speed is unknown (0, negative, NaN or infinite), or when the pet is already at
        /// least as fast as the target. Never below 1.0.
        /// </summary>
        public static float GetFactor(bool enabled, float targetSpeed, float petSpeed)
        {
            if (!enabled)
                return 1.0f;

            if (!(targetSpeed > 0.0f) || float.IsInfinity(targetSpeed))
                return 1.0f;

            if (!(petSpeed > 0.0f) || float.IsInfinity(petSpeed))
                return 1.0f;

            var factor = targetSpeed / petSpeed;

            if (!(factor > 1.0f) || float.IsInfinity(factor))
                return 1.0f;

            return factor;
        }
    }
}
