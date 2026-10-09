using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The L0 PvP classifier: which interactions are PvP, and which scope they get.
    ///
    /// SEEDING follows PvpPlayerHookTests: Players, Creatures and combat pets are GetUninitializedObject plus
    /// only the inherited WorldObject state the paths read, never a Player-declared static (that runs Player's
    /// World-database-reading type initializer). The arena seam is saved and restored around every test.
    /// </summary>
    [TestClass]
    public class PvpClassifierTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<Player, Player, bool> savedArenaScope;

        [TestInitialize]
        public void Setup()
        {
            savedArenaScope = PvpClassifier.ArenaScopeSource;
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        // ================= fixtures =================

        private static T Seeded<T>() where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return wo;
        }

        private static Player SeededPlayer(PlayerKillerStatus status = PlayerKillerStatus.PK)
        {
            var player = Seeded<Player>();
            player.PlayerKillerStatus = status;
            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static PvpMatch NewMatch(string modeKey = "arena_1v1")
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), modeKey, teams, Now);
        }

        private static PvpPlayerBinding Bind(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live) =>
            new PvpPlayerBinding(match, team, state, false, false, false, false, false);

        private static void AssertNone(PvpInteraction interaction, string why)
        {
            Assert.IsFalse(interaction.IsPvp, why);
            Assert.AreEqual(PvpScope.None, interaction.Scope, why);
            Assert.IsNull(interaction.Attacker, why);
            Assert.IsNull(interaction.Defender, why);
        }

        // ================= Classify / IsPvp =================

        [TestMethod]
        public void PlayerVsPlayer_IsPvp_OpenWorld()
        {
            var a = SeededPlayer();
            var b = SeededPlayer();

            var interaction = PvpClassifier.Classify(a, b);

            Assert.IsTrue(interaction.IsPvp);
            Assert.AreEqual(PvpScope.OpenWorld, interaction.Scope);
            Assert.AreSame(a, interaction.Attacker);
            Assert.AreSame(b, interaction.Defender);
            Assert.IsTrue(PvpClassifier.IsPvp(a, b));
        }

        [TestMethod]
        public void PlayerVsCreature_IsNone()
        {
            var player = SeededPlayer();
            var creature = Seeded<Creature>();

            AssertNone(PvpClassifier.Classify(player, creature), "player attacking a creature");
            Assert.IsFalse(PvpClassifier.IsPvp(player, creature));
        }

        [TestMethod]
        public void CreatureVsPlayer_IsNone()
        {
            var creature = Seeded<Creature>();
            var player = SeededPlayer();

            AssertNone(PvpClassifier.Classify(creature, player), "creature attacking a player");
            Assert.IsFalse(PvpClassifier.IsPvp(creature, player));
        }

        [TestMethod]
        public void CreatureVsCreature_IsNone()
        {
            AssertNone(PvpClassifier.Classify(Seeded<Creature>(), Seeded<Creature>()), "creature attacking a creature");
        }

        [TestMethod]
        public void Self_IsNone()
        {
            var player = SeededPlayer();

            AssertNone(PvpClassifier.Classify(player, player), "a player acting on themself");
            Assert.IsFalse(PvpClassifier.IsPvp(player, player));
        }

        [TestMethod]
        public void CombatPetSource_IsNone()
        {
            var pet = Seeded<CombatPet>();
            var player = SeededPlayer();

            AssertNone(PvpClassifier.Classify(pet, player), "a combat pet attacking a player");
            Assert.IsFalse(PvpClassifier.IsPvp(pet, player));
        }

        [TestMethod]
        public void NullSourceOrTarget_IsNone()
        {
            var player = SeededPlayer();

            AssertNone(PvpClassifier.Classify(null, player), "null source");
            AssertNone(PvpClassifier.Classify(player, null), "null target");
            Assert.IsFalse(PvpClassifier.IsPvp(null, player));
            Assert.IsFalse(PvpClassifier.IsPvp(player, null));
        }

        // ================= IsPlayerPairIncludingSelf (the pre-classifier inline semantics) =================

        [TestMethod]
        public void IncludingSelf_SelfPair_IsTrue_WhileIsPvpIsFalse()
        {
            var player = SeededPlayer();

            Assert.IsTrue(PvpClassifier.IsPlayerPairIncludingSelf(player, player), "self is a player pair under the old inline semantics");
            Assert.IsFalse(PvpClassifier.IsPvp(player, player), "self is NOT PvP for the classifier");
        }

        [TestMethod]
        public void IncludingSelf_TwoPlayers_IsTrue()
        {
            Assert.IsTrue(PvpClassifier.IsPlayerPairIncludingSelf(SeededPlayer(), SeededPlayer()));
        }

        [TestMethod]
        public void IncludingSelf_NonPlayerPairs_AreFalse()
        {
            var player = SeededPlayer();
            var creature = Seeded<Creature>();

            Assert.IsFalse(PvpClassifier.IsPlayerPairIncludingSelf(player, creature), "player/creature");
            Assert.IsFalse(PvpClassifier.IsPlayerPairIncludingSelf(creature, player), "creature/player");
            Assert.IsFalse(PvpClassifier.IsPlayerPairIncludingSelf(creature, creature), "creature self");
            Assert.IsFalse(PvpClassifier.IsPlayerPairIncludingSelf(Seeded<CombatPet>(), player), "combat pet/player");
            Assert.IsFalse(PvpClassifier.IsPlayerPairIncludingSelf(null, player), "null source");
            Assert.IsFalse(PvpClassifier.IsPlayerPairIncludingSelf(player, null), "null target");
        }

        /// <summary>A PvE pair never consults the arena seam: a seam that would throw is never reached.</summary>
        [TestMethod]
        public void NonPvp_NeverConsultsArenaSeam()
        {
            var calls = 0;
            PvpClassifier.ArenaScopeSource = (x, y) => { calls++; throw new InvalidOperationException("must not be read"); };

            PvpClassifier.Classify(SeededPlayer(), Seeded<Creature>());
            PvpClassifier.Classify(Seeded<Creature>(), SeededPlayer());

            var p = SeededPlayer();
            PvpClassifier.Classify(p, p);

            Assert.AreEqual(0, calls);
        }

        // ================= arena scope =================

        [TestMethod]
        public void SameLiveMatch_IsArena()
        {
            var match = NewMatch();
            var a = SeededPlayer();
            var b = SeededPlayer();
            a.SetPvpBindingForTests(Bind(match, 0));
            b.SetPvpBindingForTests(Bind(match, 1));

            var interaction = PvpClassifier.Classify(a, b);

            Assert.IsTrue(interaction.IsPvp);
            Assert.AreEqual(PvpScope.Arena, interaction.Scope);
        }

        [TestMethod]
        public void SameMatchNotLive_IsOpenWorld()
        {
            var match = NewMatch();
            var a = SeededPlayer();
            var b = SeededPlayer();
            a.SetPvpBindingForTests(Bind(match, 0, PvpMatchState.Countdown));
            b.SetPvpBindingForTests(Bind(match, 1, PvpMatchState.Live));

            Assert.AreEqual(PvpScope.OpenWorld, PvpClassifier.Classify(a, b).Scope);
        }

        [TestMethod]
        public void DifferentMatches_IsOpenWorld()
        {
            var a = SeededPlayer();
            var b = SeededPlayer();
            a.SetPvpBindingForTests(Bind(NewMatch(), 0));
            b.SetPvpBindingForTests(Bind(NewMatch(), 1));

            Assert.AreEqual(PvpScope.OpenWorld, PvpClassifier.Classify(a, b).Scope);
        }

        [TestMethod]
        public void OnlyOneBound_IsOpenWorld()
        {
            var a = SeededPlayer();
            var b = SeededPlayer();
            a.SetPvpBindingForTests(Bind(NewMatch(), 0));

            Assert.AreEqual(PvpScope.OpenWorld, PvpClassifier.Classify(a, b).Scope);
        }

        [TestMethod]
        public void SameLiveBattlegroundMatch_IsBattleground()
        {
            var match = NewMatch(BattlegroundModes.KothModeKey);
            var a = SeededPlayer();
            var b = SeededPlayer();
            a.SetPvpBindingForTests(Bind(match, 0));
            b.SetPvpBindingForTests(Bind(match, 1));

            var interaction = PvpClassifier.Classify(a, b);

            Assert.IsTrue(interaction.IsPvp);
            Assert.AreEqual(PvpScope.Battleground, interaction.Scope);
            Assert.IsTrue(interaction.IsMatchScope, "a battleground pair still counts as a match scope for every old Arena test");
        }

        [TestMethod]
        public void ArenaModeKeys_StayArena()
        {
            foreach (var modeKey in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.FfaKey })
            {
                var match = NewMatch(modeKey);
                var a = SeededPlayer();
                var b = SeededPlayer();
                a.SetPvpBindingForTests(Bind(match, 0));
                b.SetPvpBindingForTests(Bind(match, 1));

                Assert.AreEqual(PvpScope.Arena, PvpClassifier.Classify(a, b).Scope, modeKey);
            }
        }

        [TestMethod]
        public void UnboundPair_IsOpenWorld_NotBattleground()
        {
            var interaction = PvpClassifier.Classify(SeededPlayer(), SeededPlayer());

            Assert.AreEqual(PvpScope.OpenWorld, interaction.Scope);
            Assert.IsFalse(interaction.IsMatchScope);
        }

        [TestMethod]
        public void SeamSaysArena_ButNoBinding_IsArena()
        {
            PvpClassifier.ArenaScopeSource = (x, y) => true;

            Assert.AreEqual(PvpScope.Arena, PvpClassifier.Classify(SeededPlayer(), SeededPlayer()).Scope, "tests swap the seam without bindings");
        }

        [TestMethod]
        public void IsBattlegroundModeKey_TrueForEveryBattlegroundMode_FalseForEveryArenaMode()
        {
            foreach (var mode in BattlegroundModes.All(BattlegroundTunables.Defaults))
                Assert.IsTrue(BattlegroundModes.IsBattlegroundModeKey(mode.ModeKey), mode.ModeKey);

            Assert.IsTrue(BattlegroundModes.IsBattlegroundModeKey(BattlegroundModes.KothModeKey));

            foreach (var key in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.FfaKey, "arena_1v1", "", null })
                Assert.IsFalse(BattlegroundModes.IsBattlegroundModeKey(key), key ?? "(null)");
        }

        [TestMethod]
        public void ThrowingArenaSeam_FallsBackToOpenWorld()
        {
            PvpClassifier.ArenaScopeSource = (x, y) => throw new InvalidOperationException("seam failure");

            var interaction = PvpClassifier.Classify(SeededPlayer(), SeededPlayer());

            Assert.IsTrue(interaction.IsPvp);
            Assert.AreEqual(PvpScope.OpenWorld, interaction.Scope);
        }

        // ================= call-site pin: the retail branches keep the self-inclusive predicate =================

        /// <summary>
        /// The retail PvP branches a self interaction can reach (self-cast DoT, self-Harm) must stay on
        /// IsPlayerPairIncludingSelf: swapping one to IsPvp / Classify would silently change self-cast
        /// behavior (e.g. an NPK's self-cast DoT would start dealing damage). Source-text pin, matched on CODE
        /// only: each line is stripped of any // comment first, and the whole statement must match.
        /// </summary>
        [TestMethod]
        public void RetailSelfReachableSites_UseIncludingSelfPredicate()
        {
            var pins = new (string File, string Code, int Count)[]
            {
                ("ACE.Server/Entity/DamageEvent.cs", "var pkBattle = PvpClassifier.IsPlayerPairIncludingSelf(attacker, defender);", 1),
                ("ACE.Server/WorldObjects/Managers/EnchantmentManager.cs", "if (PvpClassifier.IsPlayerPairIncludingSelf(damager, WorldObject))", 2),
                ("ACE.Server/WorldObjects/Creature_Combat.cs", "var hollowPvp = PvpClassifier.IsPlayerPairIncludingSelf(attacker, this);", 1),
                ("ACE.Server/WorldObjects/Creature_Properties.cs", "var hollowPvp = PvpClassifier.IsPlayerPairIncludingSelf(attacker, this);", 1),
                ("ACE.Server/WorldObjects/WorldObject_Magic.cs", "if (PvpClassifier.IsPlayerPairIncludingSelf(player, target))", 1),
            };

            var sourceRoot = FindSourceRoot();

            foreach (var pin in pins)
            {
                var path = System.IO.Path.Combine(sourceRoot, pin.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Assert.IsTrue(System.IO.File.Exists(path), $"missing {path}");

                var hits = 0;

                foreach (var raw in System.IO.File.ReadAllLines(path))
                {
                    var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                    var code = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                    if (code == pin.Code)
                        hits++;
                }

                Assert.AreEqual(pin.Count, hits, $"{pin.File}: expected {pin.Count} code line(s) exactly `{pin.Code}`");
            }
        }

        private static string FindSourceRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
