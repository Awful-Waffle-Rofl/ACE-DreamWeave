using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SOURCE-TEXT PINS proving MlMapEventBossMagicDefense.ScaleInitLevel is actually invoked on BOTH
    /// map-event boss spawn paths (RoZ round 19 owner ruling M1). Neither path can be driven behaviourally
    /// under this harness - MlDigsiteSpawner.TrySpawn needs a live world (MlDigsiteEncounterTests' own
    /// remarks), and MlRelariaSpawner.PrepareBoss reads PropertyManager directly, which throws with no
    /// shard DB (MlRelariaSpawnTests' own remarks) - so this file follows MlDigsiteBossMechanicWiringTests'
    /// shape: pin CODE, never comments, so a refactor that keeps the words but drops the call still fails.
    /// </summary>
    [TestClass]
    public class MlMapEventBossMagicDefenseWiringTests
    {
        // ---- digsite path ---------------------------------------------------------------------------

        [TestMethod]
        public void Digsite_TrySpawn_scales_MagicDefense_for_every_boss_weight_role()
        {
            var trySpawn = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs"),
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            StringAssert.Contains(trySpawn, "role == MlDigsiteRole.Boss || role == MlDigsiteRole.MiniBoss || role == MlDigsiteRole.Checkpoint || role == MlDigsiteRole.Priority");
            StringAssert.Contains(trySpawn, "ApplyMapEventBossMagicDefenseScale(creature);");
        }

        [TestMethod]
        public void Digsite_ApplyMapEventBossMagicDefenseScale_calls_the_shared_pure_helper()
        {
            var apply = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs"),
                "private static void ApplyMapEventBossMagicDefenseScale(Creature creature)");

            StringAssert.Contains(apply, "PropertyManager.GetDouble(\"ml_mapevent_boss_magic_defense_scale\"");
            StringAssert.Contains(apply, "GetCreatureSkill(Skill.MagicDefense)");
            StringAssert.Contains(apply, "MlMapEventBossMagicDefense.ScaleInitLevel(");
            StringAssert.Contains(apply, "magicDefense.InitLevel =");
        }

        // ---- Aun Relaria path -------------------------------------------------------------------------

        [TestMethod]
        public void Relaria_PrepareBoss_calls_the_same_shared_pure_helper()
        {
            var prepareBoss = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlTreasure/MlRelariaSpawner.cs"),
                "public static void PrepareBoss(Creature boss, int bossWcid)");

            StringAssert.Contains(prepareBoss, "PropertyManager.GetDouble(\"ml_mapevent_boss_magic_defense_scale\"");
            StringAssert.Contains(prepareBoss, "boss.GetCreatureSkill(Skill.MagicDefense)");
            StringAssert.Contains(prepareBoss, "MlMapEventBossMagicDefense.ScaleInitLevel(");
            StringAssert.Contains(prepareBoss, "magicDefense.InitLevel =");
        }
    }
}
