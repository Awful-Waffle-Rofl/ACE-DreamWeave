using System;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

namespace ACE.Server.WorldEvents
{
    /// <summary>The five elements a world event hazard can carry (HAZARDS-PLAN, Resolved decisions).</summary>
    public enum WorldEventHazardElement
    {
        Fire,
        Acid,
        Frost,
        Lightning,
        Earth,
    }

    /// <summary>
    /// Everything that differs between the five elements: the two weenies (wisp, ball), the three effect scripts,
    /// the damage type and the hit-line verb. The wisp and ball NAMES live on the weenies (content package C), not here.
    /// Pure data.
    /// </summary>
    public sealed record WorldEventHazardVisuals(uint WispWcid, uint BallWcid, uint PillarScript, uint BurstScript,
        uint MarkerScript, DamageType DamageType, string HitVerb);

    /// <summary>The element table. Wcids are the pre-assigned hazard block 1006950-1006999 (Content/wcid-registry.d/wcid-1006950.tsv).</summary>
    public static class WorldEventHazardElements
    {
        // Effect scripts, each from Content/effects/effect_donor_catalog_v2.tsv (setup -> script rows).
        // Pillar (the wisp burst column) and burst (the rain impact): fire plays the Pillar of Fire for both, so the
        // acid / frost / lightning elements play their own retail pillar for both. Those are the scenery Pillar of
        // Acid / Frost / Lightning (weenies 1002666 / 1002667 / 1002668: Setups 0x02001733 / 0x02001734 / 0x02001735
        // -> scripts 0x3300116A / 0x3300116B / 0x3300116C, endless emitters like the fire pillar).
        // Marker (the destination mark): the beam family, Setups 0x020006B3-0x020006BC, one script per colour as listed
        // in PuzzleGates/PuzzleGateColours.cs (green 0x33000796, blue 0x3300079A, purple 0x33000797); fire keeps its beam.
        private const uint PillarScriptFire = HazardDefaults.PillarScript;
        private const uint BurstScriptFire = HazardDefaults.PillarScript;
        private const uint MarkerScriptFire = HazardDefaults.WispDestMarkScript;

        private const uint PillarScriptAcid = 0x3300116A;
        private const uint PillarScriptFrost = 0x3300116B;
        private const uint PillarScriptLightning = 0x3300116C;

        private const uint MarkerScriptAcid = 0x33000796;
        private const uint MarkerScriptFrost = 0x3300079A;
        private const uint MarkerScriptLightning = 0x33000797;

        // Earth has no retail Pillar of Earth and no brown ball: the owner ruled 2026-10-20 that earth takes green, so its
        // pillar is the green Pillar of Acid. Its burst is Rock Fall (Setup 0x02001601 -> script 0x33001041, a non-endless, origin-anchored
        // falling-rock impact) and its marker is the grey beam (Setup 0x020006BB -> script 0x33000799); no brown beam exists.
        private const uint PillarScriptEarth = PillarScriptAcid;
        private const uint BurstScriptEarth = 0x33001041;
        private const uint MarkerScriptEarth = 0x33000799;

        private static readonly WorldEventHazardVisuals FireVisuals = new WorldEventHazardVisuals(
            1006965, 1006966, PillarScriptFire, BurstScriptFire, MarkerScriptFire, DamageType.Fire, "sears");

        private static readonly WorldEventHazardVisuals AcidVisuals = new WorldEventHazardVisuals(
            1006961, 1006957, PillarScriptAcid, PillarScriptAcid, MarkerScriptAcid, DamageType.Acid, "corrodes");

        private static readonly WorldEventHazardVisuals FrostVisuals = new WorldEventHazardVisuals(
            1006962, 1006958, PillarScriptFrost, PillarScriptFrost, MarkerScriptFrost, DamageType.Cold, "freezes");

        private static readonly WorldEventHazardVisuals LightningVisuals = new WorldEventHazardVisuals(
            1006963, 1006959, PillarScriptLightning, PillarScriptLightning, MarkerScriptLightning, DamageType.Electric, "jolts");

        private static readonly WorldEventHazardVisuals EarthVisuals = new WorldEventHazardVisuals(
            1006964, 1006960, PillarScriptEarth, BurstScriptEarth, MarkerScriptEarth, DamageType.Bludgeon, "crushes");

        public static WorldEventHazardVisuals For(WorldEventHazardElement e)
        {
            switch (e)
            {
                case WorldEventHazardElement.Fire: return FireVisuals;
                case WorldEventHazardElement.Acid: return AcidVisuals;
                case WorldEventHazardElement.Frost: return FrostVisuals;
                case WorldEventHazardElement.Lightning: return LightningVisuals;
                case WorldEventHazardElement.Earth: return EarthVisuals;
                default: throw new ArgumentOutOfRangeException(nameof(e), e, "unknown hazard element");
            }
        }

        /// <summary>Case-insensitive parse of fire|acid|frost|lightning|earth. Numeric text is not an element.</summary>
        public static bool TryParse(string text, out WorldEventHazardElement e)
        {
            e = default;

            switch (text?.Trim().ToLowerInvariant())
            {
                case "fire": e = WorldEventHazardElement.Fire; return true;
                case "acid": e = WorldEventHazardElement.Acid; return true;
                case "frost": e = WorldEventHazardElement.Frost; return true;
                case "lightning": e = WorldEventHazardElement.Lightning; return true;
                case "earth": e = WorldEventHazardElement.Earth; return true;
                default: return false;
            }
        }
    }
}
