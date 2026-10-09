using System;

using ACE.DatLoader.FileTypes;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The game logic's view of the portal dat formula tables, injected once at boot
    /// (Program.cs, right after DatManager initializes). Formula consumers read these instead
    /// of DatManager at call time, so the tables' source is decided at the composition root:
    /// the server hands in the real dat tables, unit tests hand in synthetic ones.
    /// Must be initialized before the first Creature is constructed.
    /// </summary>
    public static class GameTables
    {
        private static SkillTable skillTable;
        private static SecondaryAttributeTable secondaryAttributeTable;

        public static SkillTable SkillTable => skillTable ?? throw NotInitialized();

        public static SecondaryAttributeTable SecondaryAttributeTable => secondaryAttributeTable ?? throw NotInitialized();

        public static void Initialize(SkillTable skillTable, SecondaryAttributeTable secondaryAttributeTable)
        {
            GameTables.skillTable = skillTable;
            GameTables.secondaryAttributeTable = secondaryAttributeTable;
        }

        private static InvalidOperationException NotInitialized()
        {
            // fail fast with the cure: a bare NullReferenceException deep in AttributeFormula
            // would leave a reader hunting for what was never wired up
            return new InvalidOperationException(
                "GameTables.Initialize has not been called. The server injects the portal dat tables in " +
                "Program.cs immediately after DatManager.Initialize; unit tests inject synthetic tables via " +
                "ACE.Server.Tests.TestGameTables.EnsureInitialized().");
        }
    }
}
