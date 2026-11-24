using System;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Proves one declared ceiling binds: ClampedAtSaturation drives the SAME pure helper the combat hook calls
    /// with a saturating input, DeclaredMax is the metadata's Value(Defaults) in the helper's own unit. They
    /// must agree within 1e-9 (ClassAbilityCeilingClampTests). Units: the ceiling's Value() is RAW (a fraction for
    /// pct tunables, display-scaled for gear); DeclaredMax must be in whatever unit the helper returns.
    /// </summary>
    public sealed record CeilingProbe(ClassAbilityId Ability, string CeilingId, Func<double> ClampedAtSaturation, Func<double> DeclaredMax);
}
