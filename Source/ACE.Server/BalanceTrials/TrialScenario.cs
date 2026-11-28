using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.BalanceTrials
{
    /// <summary>How the gem seed moves between the reps of a block.</summary>
    public enum TrialSeedMode
    {
        /// <summary>Every rep uses the same seed.</summary>
        Fixed,

        /// <summary>Rep n uses seed + (rep - 1).</summary>
        Step,
    }

    /// <summary>The gem a trial is built from. PinnedMods null means the modifier set is rolled; a list pins it (the A/B form).</summary>
    public sealed record TrialGem(int Level, int Tier, string Dungeon, string Family, IReadOnlyList<(string Id, double Magnitude)> PinnedMods, int Seed, TrialSeedMode SeedMode);

    /// <summary>One wave: the first Count plan entries of Role. Pull is always "all" for now.</summary>
    public sealed record TrialWave(DungeonRole Role, int Count);

    /// <summary>
    /// One scenario file: the room, the gem, the waves and the block shape. BuffProfile, when not null, overrides the
    /// archetype's buffProfile at rep setup (TrialBuffs.EffectiveProfile); null leaves the archetype's in force. Rotation, when not
    /// null, forces each rep's family (and optionally dungeon) in list order (TrialRotation); the gem's family is then "any".
    /// StartGraceSecs is the start-of-rep aggro grace: wave 0 of every rep spawns held (no target, no attack) for that many
    /// seconds, or until the player attacks a held creature (Creature_Trial.cs). 0 turns it off.
    /// </summary>
    public sealed record TrialScenario(string Id, string Room, TrialGem Gem, int Group, IReadOnlyList<TrialWave> Waves, string Hazards, int Reps, int TimeoutSecs, int HardCopyEvery, string BuffProfile = null, IReadOnlyList<TrialRotationEntry> Rotation = null, int StartGraceSecs = TrialScenarioLoader.DefaultStartGraceSecs);

    /// <summary>The scenario schema and loader. Closed key set: an unknown key is an error, so a typo cannot silently drop a rule.</summary>
    public static class TrialScenarioLoader
    {
        public const int MinReps = 1;
        public const int MaxReps = 1000;
        public const int MinTimeoutSecs = 30;
        public const int MaxTimeoutSecs = 7200;

        /// <summary>startGraceSecs when the key is absent: long enough for a bot client to start fighting.</summary>
        public const int DefaultStartGraceSecs = 6;
        public const int MinStartGraceSecs = 0;
        public const int MaxStartGraceSecs = 30;

        public static TrialScenario Parse(string json)
        {
            using var doc = TrialJson.ParseDocument(json);
            var root = TrialJson.Obj(doc.RootElement, "scenario",
                new[] { "id", "room", "gem", "group", "waves", "hazards", "reps", "timeoutSecs" }, "hardCopyEvery", "buffProfile", "rotation", "startGraceSecs");

            var id = TrialJson.Str(root["id"], "id");
            if (!TrialJson.IsId(id))
                throw new TrialArchetypeException($"id '{id}' must be lower-case letters, digits and hyphens");

            var room = TrialJson.Str(root["room"], "room");
            if (!TrialJson.IsId(room))
                throw new TrialArchetypeException($"room '{room}' must be lower-case letters, digits and hyphens");

            var gem = ParseGem(root["gem"]);

            var group = TrialJson.Int(root["group"], "group");
            if (group != 1)
                throw new TrialArchetypeException($"group {group} is not supported: group scenarios arrive with Plan D");

            var waves = ParseWaves(root["waves"]);

            var hazards = TrialJson.Str(root["hazards"], "hazards");
            if (hazards != "off")
                throw new TrialArchetypeException($"hazards '{hazards}' must be \"off\": Threads has no hazard mechanism yet");

            var reps = TrialJson.Int(root["reps"], "reps");
            if (reps < MinReps || reps > MaxReps)
                throw new TrialArchetypeException($"reps {reps} must be {MinReps}..{MaxReps}");

            var timeout = TrialJson.Int(root["timeoutSecs"], "timeoutSecs");
            if (timeout < MinTimeoutSecs || timeout > MaxTimeoutSecs)
                throw new TrialArchetypeException($"timeoutSecs {timeout} must be {MinTimeoutSecs}..{MaxTimeoutSecs}");

            var startGrace = DefaultStartGraceSecs;
            if (root.TryGetValue("startGraceSecs", out var sg))
            {
                startGrace = TrialJson.Int(sg, "startGraceSecs");
                if (startGrace < MinStartGraceSecs || startGrace > MaxStartGraceSecs)
                    throw new TrialArchetypeException($"startGraceSecs {startGrace} must be {MinStartGraceSecs}..{MaxStartGraceSecs} (0 turns the start grace off)");
            }

            var hardCopy = 0;
            if (root.TryGetValue("hardCopyEvery", out var hc))
            {
                hardCopy = TrialJson.Int(hc, "hardCopyEvery");
                if (hardCopy < 0)
                    throw new TrialArchetypeException($"hardCopyEvery {hardCopy} must be 0 or more (0 re-copies on drift only)");
            }

            // Optional: overrides the archetype's profile, validated by the rule TrialBuffs.Apply enforces, so a bad value fails
            // here (and in ACE.Content.Tools trialdatacheck, which loads scenarios through this loader) instead of at a rep.
            string buffProfile = null;
            if (root.TryGetValue("buffProfile", out var bp))
            {
                buffProfile = TrialJson.Str(bp, "buffProfile");
                if (!TrialBuffs.IsValidProfile(buffProfile))
                    throw new TrialArchetypeException($"buffProfile '{buffProfile}' must be {TrialBuffs.OwnSpellbook} or {TrialBuffs.AdminAllPrefix}1..8");
            }

            IReadOnlyList<TrialRotationEntry> rotation = null;
            if (root.TryGetValue("rotation", out var rot))
            {
                rotation = ParseRotation(rot);

                // The rotation owns the family, so the gem must not also pin one.
                if (!string.Equals(gem.Family, DungeonGemSpec.Any, StringComparison.Ordinal))
                    throw new TrialArchetypeException($"a scenario with a rotation needs gem family \"any\", got '{gem.Family}'");
            }

            return new TrialScenario(id, room, gem, group, waves, hazards, reps, timeout, hardCopy, buffProfile, rotation, startGrace);
        }

        private static IReadOnlyList<TrialRotationEntry> ParseRotation(JsonElement e)
        {
            var items = TrialJson.Arr(e, "rotation");
            if (items.Count == 0)
                throw new TrialArchetypeException("rotation must not be empty");

            var entries = new List<TrialRotationEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in items)
            {
                var o = TrialJson.Obj(item, "rotation entry", new[] { "family" }, "dungeon");

                var family = TrialJson.Str(o["family"], "family");
                if (family.Length == 0 || string.Equals(family, DungeonGemSpec.Any, StringComparison.OrdinalIgnoreCase))
                    throw new TrialArchetypeException("a rotation entry must name a concrete family id (not empty, not \"any\")");
                if (!seen.Add(family))
                    throw new TrialArchetypeException($"rotation family '{family}' appears twice");

                string dungeon = null;
                if (o.TryGetValue("dungeon", out var d))
                {
                    dungeon = TrialJson.Str(d, "dungeon");
                    if (dungeon.Length == 0 || string.Equals(dungeon, DungeonGemSpec.Any, StringComparison.OrdinalIgnoreCase))
                        throw new TrialArchetypeException($"rotation entry '{family}': dungeon must name a concrete Threads dungeon id (not empty, not \"any\")");
                }

                entries.Add(new TrialRotationEntry(family, dungeon));
            }

            return entries;
        }

        private static TrialGem ParseGem(JsonElement e)
        {
            var g = TrialJson.Obj(e, "gem", new[] { "level", "tier", "dungeon", "family", "mods", "seed" }, "seedMode");

            var level = TrialJson.Int(g["level"], "level");
            if (level < 1 || level > DungeonGemSpec.MaxRunLevel)
                throw new TrialArchetypeException($"level {level} must be 1..{DungeonGemSpec.MaxRunLevel}");

            var tier = TrialJson.Int(g["tier"], "tier");
            if (tier < 1 || tier > DungeonGemSpec.MaxTier)
                throw new TrialArchetypeException($"tier {tier} must be 1..{DungeonGemSpec.MaxTier}");

            var dungeon = TrialJson.Str(g["dungeon"], "dungeon");
            if (dungeon.Length == 0 || string.Equals(dungeon, "any", StringComparison.OrdinalIgnoreCase))
                throw new TrialArchetypeException("dungeon must name a concrete Threads dungeon id (not empty, not \"any\"): the live plan needs one dungeon entry");

            var family = TrialJson.Str(g["family"], "family");
            if (family.Length == 0)
                throw new TrialArchetypeException("family must be a family id or \"any\"");

            var seed = TrialJson.Int(g["seed"], "seed");
            if (seed < 1 || seed > int.MaxValue - 1)
                throw new TrialArchetypeException($"seed {seed} must be 1..{int.MaxValue - 1}: seed 0 reads as an unpressed fragment everywhere a gem is consumed");

            var seedMode = TrialSeedMode.Fixed;
            if (g.TryGetValue("seedMode", out var sm))
            {
                var text = TrialJson.Str(sm, "seedMode");
                seedMode = text switch
                {
                    "fixed" => TrialSeedMode.Fixed,
                    "step" => TrialSeedMode.Step,
                    _ => throw new TrialArchetypeException($"seedMode '{text}' must be \"fixed\" or \"step\""),
                };
            }

            return new TrialGem(level, tier, dungeon, family, ParseMods(g["mods"]), seed, seedMode);
        }

        private static IReadOnlyList<(string Id, double Magnitude)> ParseMods(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                if (e.GetString() == "rolled")
                    return null;
                throw new TrialArchetypeException($"mods '{e.GetString()}' must be \"rolled\" or a list of {{id, magnitude}}");
            }

            var pinned = new List<(string Id, double Magnitude)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in TrialJson.Arr(e, "mods"))
            {
                var o = TrialJson.Obj(item, "mod", new[] { "id", "magnitude" });
                var modId = TrialJson.Str(o["id"], "mod id");
                if (modId.Length == 0)
                    throw new TrialArchetypeException("a pinned mod needs a non-empty id");
                if (!seen.Add(modId))
                    throw new TrialArchetypeException($"pinned mod '{modId}' appears twice");
                pinned.Add((modId, TrialJson.Finite(o["magnitude"], $"mod '{modId}' magnitude")));
            }
            return pinned;
        }

        private static IReadOnlyList<TrialWave> ParseWaves(JsonElement e)
        {
            var items = TrialJson.Arr(e, "waves");
            if (items.Count == 0)
                throw new TrialArchetypeException("waves must not be empty");

            var waves = new List<TrialWave>();
            foreach (var item in items)
            {
                var w = TrialJson.Obj(item, "wave", new[] { "role", "count" }, "pull");

                var roleText = TrialJson.Str(w["role"], "role");
                var role = roleText switch
                {
                    "Trash" => DungeonRole.Trash,
                    "Elite" => DungeonRole.Elite,
                    "Boss" => DungeonRole.Boss,
                    _ => throw new TrialArchetypeException($"wave role '{roleText}' must be exactly Trash, Elite or Boss"),
                };

                var count = TrialJson.Int(w["count"], "count");
                if (count < 1)
                    throw new TrialArchetypeException($"wave count {count} for {role} must be at least 1");
                if (role == DungeonRole.Boss && count > 1)
                    throw new TrialArchetypeException($"a scenario has at most one Boss, got count {count}");

                if (w.TryGetValue("pull", out var pull) && TrialJson.Str(pull, "pull") != "all")
                    throw new TrialArchetypeException($"wave pull '{pull.GetString()}' must be \"all\"");

                if (waves.Any(x => x.Role == role))
                    throw new TrialArchetypeException($"role {role} appears in more than one wave");

                waves.Add(new TrialWave(role, count));
            }
            return waves;
        }

        /// <summary>
        /// Loads every *.json in the directory in ordinal file-name order, each with the sha256 of its exact text
        /// (<see cref="TrialArchetypeLoader.Sha256(string)"/>). The file name must equal the id, which also makes ids unique.
        /// </summary>
        public static IReadOnlyList<(TrialScenario Scenario, string Sha256)> LoadDirectory(string dir)
        {
            if (!Directory.Exists(dir))
                throw new TrialArchetypeException($"scenario directory '{dir}' does not exist");

            var result = new List<(TrialScenario Scenario, string Sha256)>();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                TrialScenario s;
                string sha;
                try
                {
                    s = Parse(text);
                    sha = TrialArchetypeLoader.Sha256(text);
                }
                catch (TrialArchetypeException ex)
                {
                    throw new TrialArchetypeException($"{Path.GetFileName(file)}: {ex.Message}");
                }

                if (!string.Equals(Path.GetFileNameWithoutExtension(file), s.Id, StringComparison.Ordinal))
                    throw new TrialArchetypeException($"{Path.GetFileName(file)}: file name must equal id '{s.Id}'");

                result.Add((s, sha));
            }
            return result;
        }
    }
}
