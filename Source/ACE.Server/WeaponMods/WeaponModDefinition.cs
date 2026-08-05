using System;
using System.Globalization;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// One row of the modifier table. A TIER A entry writes a NATIVE property the server already reads, so v1
    /// needed no combat hook of any kind. A TIER B entry writes NOTHING native and is read live at combat time
    /// by the hooks in Player_WeaponMods.cs.
    ///
    /// STORAGE, TIER A: THE APPLIED MAGNITUDE, NOT A POTENCY (HARD). This is a deliberate divergence from
    /// ACE.Server.EquipmentMods, and a reader who knows that system will expect the opposite.
    /// <see cref="Record"/> holds the exact number this system ADDED to <see cref="NativeInt"/> /
    /// <see cref="NativeFloat"/>, and reversal subtracts that recorded number back off.
    ///
    /// The reason is that Tier A writes to properties that may already carry a loot-generated value. If the
    /// stored number were a potency scalar and weapon_mod_magnitude_scale moved between application and
    /// reversal, the subtraction would be wrong and the native property would drift permanently, on an item
    /// nobody can audit. Accepted consequence: retuning <see cref="MaxRoll"/> does NOT retune modifiers already
    /// sitting on existing weapons.
    ///
    /// STORAGE, TIER B: SIMPLER, AND DELIBERATELY SO. DO NOT ASSUME THE TIER A REVERSAL MACHINERY ABOVE
    /// APPLIES. A Tier B row has NO native property (<see cref="NativeInt"/> and <see cref="NativeFloat"/> are
    /// both null, so <see cref="WritesNative"/> is false). Nothing is added to anything, so there is nothing
    /// to subtract: <see cref="Record"/> holds the applied magnitude as a plain FRACTION (0.04 for 4%, 0.15
    /// for 15%), and reversal is a bare RemoveProperty with no arithmetic and no native to restore.
    /// <see cref="ApplyValue"/> / <see cref="ReverseValue"/> are never reached for a Tier B row, and
    /// <see cref="WeaponModTinkerSet.ClearSpecials"/> clears Tier B records exactly the same way it clears
    /// Tier A ones, because it walks the whole registry.
    ///
    /// The consequence a reader should take from that: a Tier B magnitude is INERT unless a combat hook reads
    /// it. Adding a Tier B row without wiring its hook produces a record that costs a slot and does nothing.
    /// </summary>
    public class WeaponModDefinition
    {
        /// <summary>The potency floor every non-binary modifier rolls above, so a rolled special is never a no-op.</summary>
        public const double DefaultMinPotency = 0.25;

        public WeaponModId Id;

        /// <summary>
        /// Which half of the catalog this row belongs to. Drives the storage contract above and nothing else -
        /// the roll, slot and reversal arithmetic is shared, and since 2026-07-30 so is the gate: both tiers
        /// ride <see cref="WeaponModRegistry.Enabled"/>.
        /// </summary>
        public WeaponModTier Tier = WeaponModTier.A;

        /// <summary>Player-facing name, as shown in the appraisal "Property Details:" block.</summary>
        public string DisplayName;

        /// <summary>
        /// The PropertyFloat (8130-8135) carrying the APPLIED MAGNITUDE this system added. Presence means the
        /// weapon holds this modifier; the value is what reversal subtracts.
        /// </summary>
        public PropertyFloat Record;

        /// <summary>
        /// The native integer property this modifier feeds. On a TIER A row exactly one of this and
        /// <see cref="NativeFloat"/> is set; on a TIER B row BOTH are null.
        /// </summary>
        public PropertyInt? NativeInt;

        /// <summary>
        /// The native float property this modifier feeds. On a TIER A row exactly one of this and
        /// <see cref="NativeInt"/> is set; on a TIER B row BOTH are null.
        /// </summary>
        public PropertyFloat? NativeFloat;

        /// <summary>Magnitude at a perfect roll on a workmanship 10 weapon, in the native property's own units.</summary>
        public double MaxRoll;

        /// <summary>
        /// What the engine reads when the native property is ABSENT. Both application and reversal combine
        /// against this, and a reversal landing back on it removes the row rather than writing it - which is
        /// what restores "absent" rather than leaving a value the engine would read identically but an admin
        /// would read as tinkered.
        /// </summary>
        public double NativeDefault;

        /// <summary>
        /// Floor the native property may never be driven below by a reversal. 0 everywhere except Cleaving,
        /// where the stored number is a TOTAL target count including the primary, so 1 - not 0 - is the bottom.
        /// </summary>
        public double NativeFloor;

        /// <summary>
        /// TRUE when a reversal that lands exactly on <see cref="NativeDefault"/> should REMOVE the row instead
        /// of writing the default back. Safe only where absent and the default read identically at EVERY read
        /// site, which is the case for the gear ratings (absent unambiguously reads 0) and REQUIRED for Cleave:
        /// <c>Cleaving</c> is consumed as a null test (WorldObject_Weapon.cs:47-62), so writing its default of 1
        /// explicitly would leave the weapon permanently flagged as cleaving.
        ///
        /// FALSE for Swift Flight, and this is not cosmetic. <c>MaximumVelocity</c>'s read sites DISAGREE on
        /// their fallback - WeaponProfile.cs:57 falls back to 1.0 while Creature_Missile.cs:322/:517 fall back to
        /// DefaultMaxVelocity 20.0 - and five weenies (518, 521, 531, 537, 23109) carry the property at exactly
        /// 20.0. Removing the row on a reversal there would leave the appraisal panel reporting a velocity of
        /// 1.0 on a weapon that is unchanged in combat, so Swift Flight always writes the restored value back.
        /// </summary>
        public bool RemoveOnDefault = true;

        /// <summary>Lowest potency this modifier rolls at, in [0, 1). See <see cref="DefaultMinPotency"/>.</summary>
        public double MinPotency = DefaultMinPotency;

        /// <summary>
        /// TRUE = the modifier has no magnitude axis and always applies exactly <see cref="MaxRoll"/>, ignoring
        /// potency, workmanship and the scale tunable. Cleave only: a fractional cleave target is meaningless.
        /// </summary>
        public bool Binary;

        /// <summary>Which weapon classes may roll this modifier.</summary>
        public WeaponClass Classes;

        /// <summary>Composite format string, applied to (magnitude x <see cref="DisplayScale"/>).</summary>
        public string DisplayFormat;

        /// <summary>Multiplier taking a magnitude into display units. 1 everywhere except Shield Bypass, which reads as a percentage.</summary>
        public double DisplayScale = 1.0;

        /// <summary>
        /// TRUE when this row writes a native property at all. FALSE for every Tier B row, which is the whole
        /// reason <see cref="ReadNative"/> and <see cref="WriteNative"/> below have to tolerate a row with no
        /// native rather than dereferencing <see cref="NativeFloat"/> unconditionally.
        /// </summary>
        public bool WritesNative => NativeInt != null || NativeFloat != null;

        /// <summary>
        /// TRUE when the rolled magnitude must be a whole number, which is exactly when it is destined for an
        /// integer-valued NATIVE property - <see cref="WeaponModDefinition.ApplyValue"/> rounds before writing
        /// it, so a fractional value there would be silently quantized.
        ///
        /// THIS IS THE DISCRIMINATOR <see cref="WeaponModValue.Resolve"/> USES, and it is what keeps a Tier B
        /// magnitude fractional. A Tier B row writes no native at all, so <see cref="NativeInt"/> is null and
        /// this is false: 0.04 stays 0.04 rather than rounding to 0 and then being floored to 1, which is what
        /// the integer branch would have done to it. Asserted per row in the tests rather than left implicit.
        /// </summary>
        public bool IsInteger => NativeInt != null;

        public bool AppliesTo(WeaponClass weaponClass) => weaponClass != WeaponClass.None && (Classes & weaponClass) != 0;

        public string Format(double magnitude) =>
            string.Format(CultureInfo.InvariantCulture, DisplayFormat, magnitude * DisplayScale);

        // ---------------- native property access ----------------

        /// <summary>
        /// The weapon's current native value, or null when the property is absent OR when this row has no
        /// native property at all (every Tier B row). The <see cref="WritesNative"/> guard is load-bearing:
        /// without it this method dereferences a null <see cref="NativeFloat"/> on the first Tier B apply.
        /// </summary>
        public double? ReadNative(WorldObject weapon)
        {
            if (weapon == null || !WritesNative)
                return null;

            if (NativeInt != null)
            {
                var raw = weapon.GetProperty(NativeInt.Value);
                return raw == null ? (double?)null : raw.Value;
            }

            return weapon.GetProperty(NativeFloat.Value);
        }

        /// <summary>
        /// Writes the native value, or removes the row when <paramref name="value"/> is null. A NO-OP on a row
        /// with no native property (every Tier B row): there is nothing to write, and the record alone is the
        /// whole of that modifier's persisted state.
        /// </summary>
        public void WriteNative(WorldObject weapon, double? value)
        {
            if (weapon == null || !WritesNative)
                return;

            if (NativeInt != null)
            {
                if (value == null)
                    weapon.RemoveProperty(NativeInt.Value);
                else
                    weapon.SetProperty(NativeInt.Value, (int)Math.Round(value.Value, MidpointRounding.AwayFromZero));

                return;
            }

            if (value == null)
                weapon.RemoveProperty(NativeFloat.Value);
            else
                weapon.SetProperty(NativeFloat.Value, value.Value);
        }

        // ---------------- pure apply / reverse arithmetic ----------------

        /// <summary>
        /// The native value after adding <paramref name="magnitude"/> to a current value of
        /// <paramref name="current"/> (null = absent). Pure - no item, no player, no database.
        /// </summary>
        public double ApplyValue(double? current, double magnitude)
        {
            var value = (current ?? NativeDefault) + magnitude;

            if (IsInteger)
                value = Math.Round(value, MidpointRounding.AwayFromZero);

            return Math.Max(NativeFloor, value);
        }

        /// <summary>
        /// The native value after taking <paramref name="magnitude"/> back off, or NULL when the row should be
        /// REMOVED because the result landed back on the engine default AND <see cref="RemoveOnDefault"/> allows
        /// it. Pure.
        ///
        /// Note there is deliberately NO "below the default, therefore remove" rule here, unlike the layer 1
        /// materials: a special always subtracts exactly what it added, so a weapon whose loot-generated value
        /// was legitimately below the engine default (a bow slower than DefaultMaxVelocity, say) must get that
        /// value back rather than have the row deleted out from under it.
        ///
        /// Landing exactly ON the default is the case <see cref="RemoveOnDefault"/> governs, and it is NOT
        /// hypothetical: five weenies carry MaximumVelocity at exactly Swift Flight's 20.0 default. An earlier
        /// revision of this comment asserted the case could not arise. It can, it is reachable by any Swift
        /// Flight reversal on one of those launchers, and removing the row there desyncs the appraisal panel
        /// from combat. See <see cref="RemoveOnDefault"/> for the read sites.
        /// </summary>
        public double? ReverseValue(double? current, double magnitude)
        {
            var value = Math.Max(NativeFloor, (current ?? NativeDefault) - magnitude);

            if (IsInteger)
                value = Math.Round(value, MidpointRounding.AwayFromZero);

            if (RemoveOnDefault && Math.Abs(value - NativeDefault) <= WeaponModRegistry.Epsilon)
                return null;

            return value;
        }
    }
}
