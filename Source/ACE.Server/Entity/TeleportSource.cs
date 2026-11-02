namespace ACE.Server.Entity
{
    /// <summary>
    /// The src= label on a [TELEPORT] line: what started the teleport. Logging only; nothing branches on it. A
    /// teleport given no label reads as <see cref="Portal"/> when it came from a portal and <see cref="Other"/>
    /// otherwise. These are the only labels: add one here before tagging a new call site with it.
    /// </summary>
    public static class TeleportSource
    {
        public const string Portal = "portal";
        public const string Lifestone = "lifestone";
        public const string House = "house";
        public const string Mansion = "mansion";
        public const string Marketplace = "marketplace";
        public const string Drift = "drift";
        public const string Town = "town";
        public const string Allegiance = "allegiance";
        public const string PkArena = "pkarena";
        public const string PklArena = "pklarena";
        public const string Spell = "spell";
        public const string Gem = "gem";
        public const string Death = "death";
        public const string Emote = "emote";
        public const string Other = "other";

        public static readonly string[] All =
        {
            Portal, Lifestone, House, Mansion, Marketplace, Drift, Town, Allegiance,
            PkArena, PklArena, Spell, Gem, Death, Emote, Other,
        };

        public static string Resolve(string source, bool fromPortal)
        {
            return source ?? (fromPortal ? Portal : Other);
        }
    }
}
