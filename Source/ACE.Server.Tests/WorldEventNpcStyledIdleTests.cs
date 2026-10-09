using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The World Events npc styled idle (Creature_WorldEventNpc.cs): the weave_spiral Loz attendants
    /// (1002639-1002641) carry a HeartBeat emote set styled Magic/Ready whose actions are MagicPowerUp10 then
    /// MagicSelfHead. Stock ACE never selects it (a creature starts NonCombat) and never plays it (the Motion
    /// branch plays styled motions only in NonCombat). Only a creature marked by the World Events npc path may
    /// do either; every other creature must behave exactly as before.
    ///
    /// Harness limit: the Motion branch itself needs a landblock and a player in range, so playback is pinned
    /// at its decision rule (EmoteManager.PlaysStyledIdle, the exact predicate the branch calls) and selection
    /// at GetEmoteSet, which runs here without a landblock.
    /// </summary>
    [TestClass]
    public class WorldEventNpcStyledIdleTests
    {
        // Kir Loz motion table, the one the three Loz attendants ship (weenie_properties_d_i_d type 2).
        private const uint LozMotionTable = 0x0900020E;

        private static PropertiesEmote HeartBeat(MotionStance? style, params MotionCommand[] motions)
        {
            var set = new PropertiesEmote
            {
                Category = EmoteCategory.HeartBeat,
                Probability = 1,
                Style = style,
                Substyle = style == null ? (MotionCommand?)null : MotionCommand.Ready,
            };

            foreach (var motion in motions)
                set.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.Motion, Motion = motion, Extent = 1 });

            return set;
        }

        /// <summary>The Loz attendants' shipped HeartBeat set, verbatim in shape.</summary>
        private static PropertiesEmote LozCastingLoop() =>
            HeartBeat(MotionStance.Magic, MotionCommand.MagicPowerUp10, MotionCommand.MagicSelfHead);

        private static Creature Npc(params PropertiesEmote[] emotes)
        {
            var creature = TestCreatures.CreateQuestBearer("Styled Idle Npc");
            creature.MotionTableId = LozMotionTable;
            creature.Biota.PropertiesEmote = new List<PropertiesEmote>(emotes);
            return creature;
        }

        private static MotionStance Stance(Creature creature)
        {
            creature.GetCurrentMotionState(out var stance, out _);
            return stance;
        }

        // ---- the discriminating pair: marked vs unmarked, same weenie shape ----

        [TestMethod]
        public void MarkedNpc_MagicStyledHeartBeat_IsSelectedAndMayPlay()
        {
            var npc = Npc(LozCastingLoop());

            var applied = npc.EnableWorldEventStyledIdle(out _);

            Assert.AreEqual(MotionStance.Magic, applied);
            Assert.IsTrue(npc.WorldEventStyledIdle);
            Assert.AreEqual(MotionStance.Magic, Stance(npc));
            npc.GetCurrentMotionState(out _, out var forward);
            Assert.AreEqual(MotionCommand.Ready, forward, "Substyle Ready must match or GetEmoteSet filters the set out");

            var picked = npc.EmoteManager.GetEmoteSet(EmoteCategory.HeartBeat, useRNG: false);
            Assert.IsNotNull(picked, "a marked npc started in Magic must select its Magic-styled HeartBeat set");
            Assert.AreEqual(MotionStance.Magic, picked.Style);

            Assert.IsTrue(EmoteManager.PlaysStyledIdle(MotionStance.Magic, npc), "a marked npc may play a Magic-styled idle");
        }

        [TestMethod]
        public void UnmarkedCreature_MagicStyledHeartBeat_IsNeitherSelectedNorPlayed()
        {
            var creature = Npc(LozCastingLoop());

            Assert.IsFalse(creature.WorldEventStyledIdle);
            Assert.AreEqual(MotionStance.NonCombat, Stance(creature));
            Assert.IsNull(creature.EmoteManager.GetEmoteSet(EmoteCategory.HeartBeat, useRNG: false),
                "an unmarked creature keeps the stock NonCombat start, so a Magic-styled set stays dormant");

            Assert.IsFalse(EmoteManager.PlaysStyledIdle(MotionStance.Magic, creature),
                "an unmarked creature must keep the stock NonCombat-only playback rule");

            // Even forced into Magic, an unmarked creature still may not play it (the second stock gate).
            creature.CurrentMotionState = new Motion(MotionStance.Magic, MotionCommand.Ready);
            Assert.IsNotNull(creature.EmoteManager.GetEmoteSet(EmoteCategory.HeartBeat, useRNG: false));
            Assert.IsFalse(EmoteManager.PlaysStyledIdle(MotionStance.Magic, creature));
        }

        // ---- stock behaviour for everyone else is unchanged ----

        [TestMethod]
        public void UnmarkedCreature_NonCombatIdle_BehavesAsBefore()
        {
            var creature = Npc(HeartBeat(MotionStance.NonCombat, MotionCommand.Wave));

            Assert.AreEqual(MotionStance.NonCombat, Stance(creature));
            Assert.IsNotNull(creature.EmoteManager.GetEmoteSet(EmoteCategory.HeartBeat, useRNG: false));
            Assert.IsTrue(EmoteManager.PlaysStyledIdle(MotionStance.NonCombat, creature));

            // Every other styled stance stays dormant for an unmarked creature, as in stock ACE.
            foreach (var stance in new[] { MotionStance.HandCombat, MotionStance.SwordCombat, MotionStance.Magic, MotionStance.BowCombat })
                Assert.IsFalse(EmoteManager.PlaysStyledIdle(stance, creature), stance.ToString());

            // A non-Creature object takes the stock rule too.
            Assert.IsTrue(EmoteManager.PlaysStyledIdle(MotionStance.NonCombat, null));
            Assert.IsFalse(EmoteManager.PlaysStyledIdle(MotionStance.Magic, null));
        }

        [TestMethod]
        public void MarkedNpc_WithANonCombatIdle_KeepsNonCombat()
        {
            var npc = Npc(HeartBeat(MotionStance.NonCombat, MotionCommand.Wave));

            Assert.IsNull(npc.EnableWorldEventStyledIdle(out _));
            Assert.AreEqual(MotionStance.NonCombat, Stance(npc));
            Assert.IsNotNull(npc.EmoteManager.GetEmoteSet(EmoteCategory.HeartBeat, useRNG: false));
        }

        [TestMethod]
        public void MarkedNpc_WithoutAMotionTable_IsNotRestanced()
        {
            var npc = Npc(LozCastingLoop());
            npc.MotionTableId = 0;

            Assert.IsNull(npc.EnableWorldEventStyledIdle(out _));
            Assert.AreEqual(MotionStance.NonCombat, Stance(npc));
        }

        [TestMethod]
        public void MarkingNeverMakesTheNpcAMonster()
        {
            var npc = Npc(LozCastingLoop());
            npc.Attackable = false;
            npc.SetMonsterState();

            npc.EnableWorldEventStyledIdle(out _);

            Assert.IsFalse(npc.IsMonster);
            Assert.IsFalse(npc.Attackable);
            Assert.AreEqual(CombatMode.NonCombat, npc.CombatMode);
        }

        // ---- source scans: comment-stripped, so a commented-out call never satisfies them ----

        private static string ServerSourceRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);

            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ACE.Server", "WorldEvents", "WorldEventSpawner.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldEvents/WorldEventSpawner.cs by walking up from {System.AppContext.BaseDirectory}");

            return System.IO.Path.Combine(dir.FullName, "ACE.Server");
        }

        /// <summary>
        /// C# source with every // line comment and /* */ block comment removed (newlines kept), leaving
        /// string and char literals intact - including regular, verbatim (@"") and interpolated ($"") strings,
        /// so a "//" inside a literal is not mistaken for a comment.
        /// </summary>
        internal static string StripComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            var i = 0;

            while (i < source.Length)
            {
                var c = source[i];
                var next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                        i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                    {
                        if (source[i] == '\n')
                            sb.Append('\n');
                        i++;
                    }
                    i += 2;
                    continue;
                }

                // verbatim string: @"..." or $@"..." / @$"...", where "" is an escaped quote
                var verbatim = (c == '@' && next == '"')
                    || ((c == '$' || c == '@') && (next == '@' || next == '$') && i + 2 < source.Length && source[i + 2] == '"');

                if (verbatim)
                {
                    while (source[i] != '"')
                        sb.Append(source[i++]);
                    sb.Append(source[i++]);

                    while (i < source.Length)
                    {
                        if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i += 2;
                            continue;
                        }

                        sb.Append(source[i]);
                        if (source[i++] == '"')
                            break;
                    }
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    sb.Append(source[i++]);

                    while (i < source.Length)
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            sb.Append(source[i]).Append(source[i + 1]);
                            i += 2;
                            continue;
                        }

                        sb.Append(source[i]);
                        if (source[i++] == quote || source[i - 1] == '\n')
                            break;
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static string ReadStripped(params string[] relative)
        {
            var parts = new List<string> { ServerSourceRoot() };
            parts.AddRange(relative);
            return StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(parts.ToArray())));
        }

        [TestMethod]
        public void StripComments_RemovesBothCommentFormsButNotLiterals()
        {
            var src = "a(); // creature.X();\n/* creature.Y(); */ b();\nvar s = \"http://x\"; var v = @\"c:\\\"\"//\"\"\"; /* multi\nline */ c();";
            var stripped = StripComments(src);

            Assert.IsFalse(stripped.Contains("creature.X"));
            Assert.IsFalse(stripped.Contains("creature.Y"));
            Assert.IsFalse(stripped.Contains("multi"));
            Assert.IsTrue(stripped.Contains("a();"));
            Assert.IsTrue(stripped.Contains("b();"));
            Assert.IsTrue(stripped.Contains("c();"));
            Assert.IsTrue(stripped.Contains("\"http://x\""), "a // inside a string literal is not a comment");
            Assert.IsTrue(stripped.Contains("@\"c:\\\"\"//\"\"\""), "a // inside a verbatim string is not a comment");
        }

        [TestMethod]
        public void OnlyPlaceNpcsMarks_AndItMarksBeforeEnterWorld()
        {
            const string call = "creature.EnableWorldEventStyledIdle(";
            var root = ServerSourceRoot();

            // Scope: exactly one live call in the whole server, and it is in WorldEventSpawner.cs.
            var callers = new List<string>();
            foreach (var file in System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                var text = StripComments(System.IO.File.ReadAllText(file));
                for (var i = text.IndexOf(".EnableWorldEventStyledIdle("); i >= 0; i = text.IndexOf(".EnableWorldEventStyledIdle(", i + 1))
                    callers.Add(System.IO.Path.GetFileName(file));
            }
            CollectionAssert.AreEqual(new[] { "WorldEventSpawner.cs" }, callers, "EnableWorldEventStyledIdle must be called only by WorldEventSpawner.PlaceNpcs");

            // Ordering, bound to PlaceNpcs's own body: the mark (and stance) precede EnterWorld, so the stance
            // rides in the CreateObject rather than needing a broadcast after the object is in view.
            var spawner = ReadStripped("WorldEvents", "WorldEventSpawner.cs");
            var start = spawner.IndexOf("private void PlaceNpcs(");
            Assert.IsTrue(start >= 0, "PlaceNpcs not found");
            var end = spawner.IndexOf("public static Position NpcPosition(", start);
            Assert.IsTrue(end > start, "NpcPosition (the method after PlaceNpcs) not found");
            var body = spawner.Substring(start, end - start);

            var mark = body.IndexOf(call);
            var enter = body.IndexOf("entered = creature.EnterWorld();");
            Assert.IsTrue(mark >= 0, "PlaceNpcs does not mark its npcs");
            Assert.IsTrue(enter >= 0, "PlaceNpcs EnterWorld call not found");
            Assert.IsTrue(mark < enter, "PlaceNpcs must mark the npc before EnterWorld");
        }

        [TestMethod]
        public void EmoteMotionBranch_CallsPlaysStyledIdle_NotTheStockNonCombatTest()
        {
            var emote = ReadStripped("WorldObjects", "Managers", "EmoteManager.cs");

            var start = emote.IndexOf("case EmoteType.Motion:");
            Assert.IsTrue(start >= 0, "case EmoteType.Motion: not found");
            var end = emote.IndexOf("case EmoteType.Move:", start);
            Assert.IsTrue(end > start, "case EmoteType.Move: (the label after Motion) not found");
            var motionCase = emote.Substring(start, end - start);

            Assert.IsTrue(motionCase.Contains("PlaysStyledIdle(startingMotion.Stance, WorldObject)"),
                "the Motion branch's styled-idle playback must go through EmoteManager.PlaysStyledIdle");
            Assert.IsFalse(motionCase.Contains("startingMotion.Stance == MotionStance.NonCombat"),
                "the stock NonCombat-only test must not be back in the Motion branch (it would ignore WorldEventStyledIdle)");
        }

        // ---- StyledIdleStance: the stance vote ----

        private static PropertiesEmote Set(EmoteCategory category, MotionStance? style, params MotionCommand[] motions)
        {
            var set = HeartBeat(style, motions);
            set.Category = category;
            return set;
        }

        [TestMethod]
        public void StyledIdleStance_Rules()
        {
            Assert.AreEqual(MotionStance.Magic, Creature.StyledIdleStance(new[] { LozCastingLoop() }), "the shipped Loz shape resolves to Magic");

            Assert.IsNull(Creature.StyledIdleStance(null));
            Assert.IsNull(Creature.StyledIdleStance(new PropertiesEmote[0]));
            Assert.IsNull(Creature.StyledIdleStance(new[] { HeartBeat(null, MotionCommand.Wave) }));
            Assert.IsNull(Creature.StyledIdleStance(new[] { HeartBeat(MotionStance.NonCombat, MotionCommand.Wave) }));
            Assert.IsNull(Creature.StyledIdleStance(new[] { LozCastingLoop(), HeartBeat(MotionStance.NonCombat, MotionCommand.Wave) }),
                "any NonCombat-styled idle keeps the stock start so it is never switched off");
            Assert.IsNull(Creature.StyledIdleStance(new[] { LozCastingLoop(), HeartBeat(MotionStance.HandCombat, MotionCommand.Wave) }),
                "disagreeing styles keep the stock start");

            var notHeartBeat = LozCastingLoop();
            notHeartBeat.Category = EmoteCategory.Use;
            Assert.IsNull(Creature.StyledIdleStance(new[] { notHeartBeat }), "only HeartBeat sets vote");

            // A non-Motion set in another category does not veto.
            Assert.AreEqual(MotionStance.Magic, Creature.StyledIdleStance(new[] { LozCastingLoop(), Set(EmoteCategory.Use, null) }));
        }

        [TestMethod]
        public void StyledIdleStance_VetoedByAMotionEmoteThatWouldEscapeTheStance()
        {
            bool blocked;

            // Style-null Use set with a Motion: EmoteManager's vendor/other-motions branch would reset NonCombat.
            Assert.IsNull(Creature.StyledIdleStance(new[] { LozCastingLoop(), Set(EmoteCategory.Use, null, MotionCommand.Wave) }, out blocked));
            Assert.IsTrue(blocked);

            // NonCombat-styled Use Motion: silently dropped while the creature stands in Magic.
            Assert.IsNull(Creature.StyledIdleStance(new[] { LozCastingLoop(), Set(EmoteCategory.Use, MotionStance.NonCombat, MotionCommand.Wave) }, out blocked));
            Assert.IsTrue(blocked);

            // Style-null HeartBeat with a Motion: same escape, even though unstyled sets do not vote on the stance.
            Assert.IsNull(Creature.StyledIdleStance(new[] { LozCastingLoop(), HeartBeat(null, MotionCommand.Wave) }, out blocked));
            Assert.IsTrue(blocked);

            // A Vendor set with a Motion always takes the vendor branch, styled or not.
            Assert.IsNull(Creature.StyledIdleStance(new[] { LozCastingLoop(), Set(EmoteCategory.Vendor, MotionStance.Magic, MotionCommand.Wave) }, out blocked));
            Assert.IsTrue(blocked);

            // No styled idle asked for: no veto reported, nothing to warn about.
            Assert.IsNull(Creature.StyledIdleStance(new[] { Set(EmoteCategory.Use, null, MotionCommand.Wave) }, out blocked));
            Assert.IsFalse(blocked);

            // The shipped Loz shape: not blocked.
            Assert.AreEqual(MotionStance.Magic, Creature.StyledIdleStance(new[] { LozCastingLoop() }, out blocked));
            Assert.IsFalse(blocked);
        }

        [TestMethod]
        public void MarkedNpc_WithAnEscapingMotionEmote_KeepsNonCombatAndReportsIt()
        {
            var npc = Npc(LozCastingLoop(), Set(EmoteCategory.Use, null, MotionCommand.Wave));

            Assert.IsNull(npc.EnableWorldEventStyledIdle(out var blocked));
            Assert.IsTrue(blocked);
            Assert.AreEqual(MotionStance.NonCombat, Stance(npc));
        }
    }
}