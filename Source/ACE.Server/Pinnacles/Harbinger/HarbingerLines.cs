using System;

namespace ACE.Server.Pinnacles.Harbinger
{
    /// <summary>
    /// Player-facing lines (orchestrator-written copy, harbinger-lines.md; ASCII, no dashes). Each constant names its purpose.
    /// </summary>
    public static class HarbingerLines
    {
        public const string LineStart = "You climb into my hall uninvited, little one. I have kept these four doors since before your kind learned fire. Kneel, or be unmade by each of them.";

        public const string LineThresholdAcid = "You sting, so let the walls sting back. The western door stands open, and my acid rises while I rest.";

        public const string LineThresholdLightning = "The storm answers me, not you. Seek the eastern door, if your legs survive the lightning.";

        public const string LineThresholdFrost = "Then freeze where you stand. The southern door lies open, and the hail will slow every step you take toward it.";

        public const string LineThresholdFire = "Burn, then. The northern door holds my last ember, and it will not go quietly.";

        /// <summary>Said once per element, when the owner first reaches the alcove.</summary>
        public const string LineAlcove = "You found the door. Now face what guards it.";

        public const string LineCrystalAcid = "The acid ebbs. Come down to me, and we begin again.";

        public const string LineCrystalLightning = "The storm is spent. Come down, little one; I am not.";

        public const string LineCrystalFrost = "The cold breaks. Come back down and feel my fists instead.";

        public const string LineCrystalFire = "My last ember dies. Come down and see what remains of me.";

        /// <summary>Said when the last guardian of an alcove falls and the crystal's shield drops (round 2 A5).</summary>
        public const string LineWardBroken = "My wardens fall, and the crystal stands bare. Strike it, if your hands still answer you.";

        /// <summary>Said when a Twin conductors guardian rises again because its twin still stands (round 2 B2, the lightning alcove).</summary>
        public const string LineTwinRevive = "Rise, warden. The door is not yet yours to take.";

        /// <summary>Said once per alcove when the doorway pressure begins (round 2 B1): the alcove's signature turns harder.</summary>
        public const string LinePressure = "You linger at my door. Then let the alcove remember whose hall this is.";

        public const string LineFinal = "Four doors, four wounds. Then take all four at once!";

        /// <summary>Said once per resume, when the owner comes back after a death (RestartPhase).</summary>
        public const string LineReturn = "Back again, little one? The doors remember you, and so do I.";

        public const string LineDeath = "The doors close, and no one is left to keep them. Remember this hall, little one. It will be empty now.";

        /// <summary>The threshold taunt of an element's phase.</summary>
        public static string Threshold(HarbingerElement element)
            => element switch
            {
                HarbingerElement.Acid => LineThresholdAcid,
                HarbingerElement.Lightning => LineThresholdLightning,
                HarbingerElement.Frost => LineThresholdFrost,
                HarbingerElement.Fire => LineThresholdFire,
                _ => throw new ArgumentOutOfRangeException(nameof(element), element, "no such Harbinger element"),
            };

        /// <summary>The line said when an element's crystal dies.</summary>
        public static string Crystal(HarbingerElement element)
            => element switch
            {
                HarbingerElement.Acid => LineCrystalAcid,
                HarbingerElement.Lightning => LineCrystalLightning,
                HarbingerElement.Frost => LineCrystalFrost,
                HarbingerElement.Fire => LineCrystalFire,
                _ => throw new ArgumentOutOfRangeException(nameof(element), element, "no such Harbinger element"),
            };
    }
}
