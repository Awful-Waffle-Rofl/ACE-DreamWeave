namespace ACE.Database.LoadTest.Scenarios
{
    public interface IScenario
    {
        string Name { get; }

        string Description { get; }

        /// <summary>
        /// Writes any data it needs and cleans it up again. Safe to run against a real (non-production!) shard database.
        /// </summary>
        void Run(ScenarioArgs args);
    }
}
