using System.Globalization;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// Which core combat site reads a mod's aggregated value. Purely a dispatch/documentation axis for the
    /// later hook phase - Phase 1 stores it so the registry is self-describing and so a reviewer can see, per
    /// mod, whether it touches a core choke point or lives entirely inside an ability handler.
    ///
    /// The scalability rule (plan decision 13) is that core combat logic stays limited to a handful of
    /// stream-aggregated reads: adding a mod must never add a per-mod branch at a combat site.
    /// </summary>
    public enum EquipmentModHookKind
    {
        /// <summary>Percent added to the outgoing melee damage stream.</summary>
        StreamDamagePercentMelee,

        /// <summary>Percent added to the outgoing missile damage stream.</summary>
        StreamDamagePercentMissile,

        /// <summary>
        /// Percent added to the outgoing spell damage stream. The member name still says "War" for the
        /// append-only reason enum members always keep their spelling, but the stream stopped being War
        /// Magic only on 2026-09-13 when Overchannel widened to every school - see
        /// Player.GetClassAbilitySpellDamageMod.
        /// </summary>
        StreamDamagePercentWar,

        /// <summary>Percent added to the outgoing void-spell damage stream.</summary>
        StreamDamagePercentVoid,

        /// <summary>Flat damage added after mitigation (the Poison Weapon proc stream).</summary>
        FlatPostMitigation,

        /// <summary>Attack-speed term, composed INSIDE the class_ability_attack_speed_ceiling clamp.</summary>
        AttackSpeed,

        /// <summary>Fraction of the equipped shield's armor level reflected back at the attacker.</summary>
        ReflectShieldAL,

        /// <summary>
        /// The mod's term is read inside the owning ability's own handler, where that ability's tunables are
        /// already read fresh per event. Adds ZERO new core hooks. Used by machinery mods (which require the
        /// base ability to be active at all) - see <see cref="EquipmentModDefinition.Standalone"/>.
        /// </summary>
        AbilityMachinery,

        /// <summary>
        /// Like <see cref="AbilityMachinery"/> in that the read happens at the owning ability's compose site,
        /// but the mod is standalone: the site must also be reachable at ability rank 0 so a player who does
        /// not own the ability still gets the magnitude. (Bulwark's damage-reduction pool, Eagle Eye's
        /// effective-skill term, Bloodlust's leech fraction, Empowered Summons' pet scalar.)
        /// </summary>
        AbilityCompose,
    }

    /// <summary>
    /// One row of the equipment-mod table. Adding a mod is ONE new
    /// <see cref="EquipmentModRegistry"/> row plus (for machinery mods) one extra term inside the ability
    /// handler that already fires - never a new branch at a combat site.
    ///
    /// CRITICAL: <see cref="MaxMagnitude"/> is the effect at a PERFECT 100% roll, expressed in the hook's own
    /// units (a fraction for percent/percentage-point mods, an absolute for flat mods). The item itself stores
    /// only a potency scalar in [0, 1]; the applied value is always resolved through
    /// <see cref="EquipmentModValue.Resolve"/> as potency x MaxMagnitude x equipment_mod_potency_scale.
    /// Never persist a resolved magnitude on an item - retuning MaxMagnitude here is what rebalances every
    /// existing item in the world without a shard migration.
    /// </summary>
    public class EquipmentModDefinition
    {
        public EquipmentModId Id;

        /// <summary>
        /// The item property that carries this mod's potency scalar. Must sit in the reserved 8100-8199 band
        /// and must be unique across the registry.
        /// </summary>
        public PropertyFloat Property;

        /// <summary>Player-facing name, as shown in the appraisal "Property Details:" block.</summary>
        public string DisplayName;

        /// <summary>
        /// The class ability this mod belongs to. Machinery mods amplify this ability's own machinery and do
        /// nothing without it; standalone mods use it only to say where the term composes.
        /// </summary>
        public ClassAbilityId LinkedAbility;

        /// <summary>
        /// TRUE = the magnitude stands alone in the same formula at ability rank 0, so the mod is useful to a
        /// player who has never learned <see cref="LinkedAbility"/>. FALSE = machinery amplifier, inert
        /// without the base ability active.
        /// </summary>
        public bool Standalone;

        /// <summary>
        /// Effect at a perfect 100% roll, in hook units (see the class remarks). Always &gt; 0.
        /// </summary>
        public double MaxMagnitude;

        /// <summary>
        /// Lowest potency this mod may ever be rolled or applied at, in [0, 1). LEAVE IT UNSET to take
        /// <see cref="EquipmentModRoller.DefaultMinPotency"/> (0.10); declare a value here only to raise the
        /// floor above that default, and write the derivation next to it on the registry row.
        ///
        /// EVERY mod is floored. The governing rule (user, 2026-07-25, restated 2026-08-01) is "set floors so
        /// that a single mod can never be useless" / "it should not be possible to have a fully useless
        /// special mod". This field originally carried a narrower reading of that rule - that only a mod whose
        /// effect is QUANTIZED to an integer can roll into a dead band, so mods on continuous pipelines
        /// (percentages folded into a damage stream rounded once, proc chances against a float roll, animation
        /// speeds) "need no floor and leave this at 0". That reading was wrong in practice, twice over:
        ///   - a continuous mod still reaches a display of zero, because the resolved magnitude is rendered
        ///     through <see cref="DisplayFormat"/>. The reported case was "Frenzied Pace: +0%" - MaxMagnitude
        ///     0.0015 at DisplayScale 100 renders through a 3-decimal format as 0 for any potency below
        ///     0.00333, roughly 1 unfloored roll in 300;
        ///   - and a roll one notch above that prints a nonzero number while still moving nothing a player can
        ///     feel, which is the same useless item with better cosmetics.
        /// Quantization therefore decides HOW HIGH a mod's floor must be, never WHETHER it needs one.
        ///
        /// Declaring 0.0 here is NOT an exemption - see <see cref="EquipmentModRoller.MinPotency"/>, which
        /// cannot distinguish a declared 0.0 from an unset field and reads both as "use the default".
        ///
        /// The floor applies to BOTH ends of the application flow: a conversion or reroll draws uniformly
        /// over [MinPotency, 1], and a low-tier application takes the greater of the low-tier tunable and
        /// this floor.
        /// </summary>
        public double MinPotency;

        /// <summary>
        /// Cross-item cap on this mod type's TOTAL potency across ALL of a creature's equipped items, in
        /// potency-sum units where 1.0 is one perfect (100%) roll. Entirely separate from the per-INSTANCE
        /// [0, 1] clamp applied everywhere else (shard-contamination defense on a single item's stored
        /// value) - this field caps the SUM across items, after that per-instance clamp has already run.
        ///
        /// 0 (the default, unset) means uncapped: the sum grows without bound as more equipped items carry
        /// this mod type, which is exactly the no-cross-item-cap ruling (user, 2026-07-24 - RNG scarcity and
        /// one-mod-type-per-item are the governors, not a hard ceiling). That ruling still holds for every
        /// row that leaves this at 0.
        ///
        /// Declare a nonzero value only where an uncapped sum creates a real problem - in practice, a
        /// proc-CHANCE mod whose stack can walk a chance-on-hit ability toward (or past) a 100% guarantee.
        /// See the AcidProc registry row for the worked-out numbers behind why 3.0 was chosen there.
        /// </summary>
        public double StackCap;

        /// <summary>Which core site (if any) aggregates this mod. Consumed by the later hook phase.</summary>
        public EquipmentModHookKind HookKind;

        /// <summary>
        /// Composite format string for the resolved value, applied to (value x <see cref="DisplayScale"/>) -
        /// e.g. "+{0:0.##}% missile damage", "+{0:0.##} poison per hit", "+{0:0.##}s stack window".
        /// </summary>
        public string DisplayFormat;

        /// <summary>
        /// Multiplier taking a resolved hook-unit value into display units: 100 for fraction-valued mods
        /// (percent and percentage-point), 1 for mods whose hook unit is already the display unit (flat
        /// damage, seconds). Format strings cannot multiply, hence the separate scalar.
        /// </summary>
        public double DisplayScale = 100.0;

        /// <summary>
        /// Renders an already-resolved magnitude for display. Callers pass the output of
        /// <see cref="EquipmentModValue.Resolve"/>, never a raw potency.
        /// </summary>
        public string Format(double resolvedValue) =>
            string.Format(CultureInfo.InvariantCulture, DisplayFormat, resolvedValue * DisplayScale);
    }
}
