using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// A single scalar "how strong is this ability right now" readout for one class ability owned at one
    /// rank, in DISPLAY units already (a "%" readout carries 18.0 for +18%, never 0.18). Backs the
    /// per-ability line in /abilities list (see ClassAbilityCommands.FormatReadoutLine). Optional -
    /// implement <see cref="IAbilityReadout"/> on a handler only when its effect boils down to one
    /// player-meaningful number; abilities that only reshape combat (a pure proc trigger, a stance change)
    /// leave it unimplemented and the list prints rank only.
    ///
    /// The three inputs and the two outputs are the whole contract:
    ///
    /// - <see cref="Skill"/> is the bonus from the ability's own RANK alone (the inline rank*perRank term
    ///   every handler already computes).
    /// - <see cref="Affinity"/> is the legacy-skill rider (a GetClassAbilityScaling call against a Trained/
    ///   Specialized source skill - Weapon Tinkering, Item Tinkering, Recklessness, etc).
    /// - <see cref="Gear"/> is the equipment-mod contribution (a GetEquippedModValue call).
    ///
    /// All three are in the SAME unit as <see cref="Unit"/>, so they sum meaningfully: <see cref="Total"/>
    /// is their sum, uncapped - "what the three inputs alone would produce if nothing downstream clamped
    /// them."
    ///
    /// - <see cref="Effective"/> is what combat actually uses after every clamp the mechanic applies
    ///   (an affinity-rider cap, a cross-ability composed ceiling, etc). It equals <see cref="Total"/> when
    ///   nothing bit this call. When a cap bit, Effective &lt; Total and <see cref="CapNote"/> names the cap
    ///   in a short player-readable phrase (e.g. "affinity cap", "anim ceiling"). Never set CapNote when
    ///   the cap didn't actually reduce anything this call - a cap that exists but isn't currently biting
    ///   must render as an uncapped line.
    /// </summary>
    public readonly struct ClassAbilityReadout
    {
        /// <summary>
        /// FALSE = this ability has no single scalar worth printing (a pure shape/behavior change, e.g. a
        /// stance swap or an unconditional proc trigger with no tunable "how much"). The display then
        /// prints the ability name and rank only, with no numbers.
        /// </summary>
        public bool HasValue { get; init; }

        /// <summary>Bonus from the ability's own rank alone, in <see cref="Unit"/> display units.</summary>
        public double Skill { get; init; }

        /// <summary>Bonus from the legacy-skill affinity rider (e.g. Weapon Tinkering, Recklessness), same unit as <see cref="Skill"/>.</summary>
        public double Affinity { get; init; }

        /// <summary>Bonus from equipped gear mods, same unit as <see cref="Skill"/>.</summary>
        public double Gear { get; init; }

        /// <summary>
        /// What combat actually uses after every clamp. Equals <see cref="Total"/> unless a cap bit this
        /// call, in which case it is smaller and <see cref="CapNote"/> is set.
        /// </summary>
        public double Effective { get; init; }

        /// <summary>Display unit: one of "%", "" (flat), "s", "m", "x".</summary>
        public string Unit { get; init; }

        /// <summary>
        /// Optional leading character(s) rendered immediately before <see cref="Effective"/> (e.g. "+" for a
        /// flat stat bonus so the line reads "+50 skills" instead of "50 stat"). Null/empty for every
        /// readout that doesn't opt in - FormatReadoutLine treats null the same as "".
        /// </summary>
        public string Prefix { get; init; }

        /// <summary>Short player-facing noun phrase, e.g. "melee dmg", "proc", "shield AL". Keep under ~14 chars.</summary>
        public string Label { get; init; }

        /// <summary>"/stack", "/charge", or null when the readout isn't a per-unit rate.</summary>
        public string Per { get; init; }

        /// <summary>
        /// Player-readable name of the cap that reduced Effective below Total this call, or null/empty when
        /// nothing bit. Setting this is what makes <see cref="Capped"/> true.
        /// </summary>
        public string CapNote { get; init; }

        /// <summary>Sum of the three raw inputs, before any clamp. See the type doc for what this means relative to Effective.</summary>
        public double Total => Skill + Affinity + Gear;

        /// <summary>TRUE when a cap reduced Effective below Total this call (CapNote is set).</summary>
        public bool Capped => !string.IsNullOrEmpty(CapNote);
    }

    /// <summary>
    /// Implemented by a class ability handler whose effect boils down to one player-meaningful scalar, so
    /// /abilities list can print a live "how strong is this right now" number instead of just a rank.
    /// See <see cref="ClassAbilityReadout"/> for the full contract.
    /// </summary>
    public interface IAbilityReadout
    {
        /// <summary>
        /// Computes the current readout for this ability at the given owned rank. Implementations must
        /// mirror the ability's real bonus expression (the same one its combat hook uses) rather than
        /// restate it independently, so the display can never drift from what combat actually applies.
        /// </summary>
        ClassAbilityReadout GetReadout(Player player, int rank);
    }
}
