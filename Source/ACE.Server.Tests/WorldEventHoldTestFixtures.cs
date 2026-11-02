using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A hand-built Hold run view for engine tests: every value is a plain settable field, so a test drives the hooks without
    /// a WorldEvent, a spawner or a landblock. Same contract as WorldEventHoldRun (any thread, no creature writes).
    /// </summary>
    internal sealed class FakeHoldRunView : IWorldEventHoldRunView
    {
        public FakeHoldRunView(uint runId) => RunId = runId;

        public uint RunId { get; }
        public bool IsActive { get; set; } = true;
        public int WaveNumber { get; set; } = 1;
        public WorldEventHoldTuning Tuning { get; set; } = WorldEventHoldTuning.Defaults;
        public int ParticipantCount { get; set; } = 1;
        public IReadOnlyList<Creature> Knights { get; set; } = Array.Empty<Creature>();
        public IReadOnlyList<Creature> WaveMonsters { get; set; } = Array.Empty<Creature>();

        public double EnrageMult { get; set; } = 1.0;
        public int FinaleHits;
        public float FinaleDealt;
        public readonly List<uint> Healers = new List<uint>();

        public void NoteKnightHealer(Player healer, double now)
        {
            if (healer != null)
                Healers.Add(healer.Guid.Full);
        }

        public IReadOnlyList<uint> RecentHealers(double now) => Healers;

        public void NoteFinaleBossHit(Creature boss, Creature knight, float dealt, double now)
        {
            FinaleHits++;
            FinaleDealt += dealt;
        }

        public double FinaleEnrageMult(Creature boss, double now) => EnrageMult;

        public Func<WorldObject, Player, double, (bool Handled, string Reply)> Shrine { get; set; }

        public bool TryUseShrine(WorldObject shrine, Player player, double now, out string reply)
        {
            var r = Shrine?.Invoke(shrine, player, now) ?? (false, null);
            reply = r.Reply;
            return r.Handled;
        }
    }

    /// <summary>Creature and tuning builders shared by the Hold engine tests.</summary>
    internal static class HoldFixtures
    {
        private static uint nextGuid = 0x7E300000;

        /// <summary>A live, never-evented creature with real health (MaxHealth InitLevel <paramref name="maxHealth"/> plus the Endurance bonus).</summary>
        public static Creature MakeCreature(string name = "Hold Test Creature", uint maxHealth = 10000, bool attackable = true)
        {
            TestGameTables.EnsureInitialized();

            var guid = nextGuid++;

            var weenie = new Weenie
            {
                WeenieClassId = 0x7E300000u + (guid & 0xFFFF),
                ClassName = $"holdenginetest{guid:X8}",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, attackable } },
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = maxHealth },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
                },
            };

            var creature = new Creature(weenie, new ObjectGuid(guid));
            creature.Health.Current = creature.Health.MaxValue;
            return creature;
        }

        /// <summary>A creature tagged for <paramref name="run"/> as <paramref name="kind"/>, with the tag values given.</summary>
        public static Creature Tagged(IWorldEventHoldRunView run, HoldUnitKind kind, HoldDamageRole? role = null, bool seeker = false, bool hunter = false,
            bool corrupted = false, bool warded = false, float attackSpeedMult = 1f, float runRateMult = 1f, float holdDistance = 0f,
            uint forced = 0, uint avoid = 0, uint tint = 0, bool showHealthBar = false, string name = null)
        {
            var creature = MakeCreature(name ?? $"Hold {kind}", attackable: kind != HoldUnitKind.Knight);

            var tag = new WorldEventHoldTag
            {
                RunId = run.RunId,
                Kind = kind,
                Role = role ?? (kind == HoldUnitKind.Monster ? HoldDamageRole.Melee : HoldDamageRole.None),
                Seeker = seeker,
                Hunter = hunter,
                Corrupted = corrupted,
                Warded = warded,
                AttackSpeedMult = attackSpeedMult,
                RunRateMult = runRateMult,
                HoldDistance = holdDistance,
                ForcedTargetGuid = forced,
                AvoidTargetGuid = avoid,
                TintPalette = tint,
                ShowHealthBar = showHealthBar,
                Run = run,
            };

            if (!creature.SetWorldEventHoldTag(tag))
                throw new InvalidOperationException("sanity: a fresh creature takes its tag");

            return creature;
        }

        /// <summary>A tuning resolved from <paramref name="props"/> (setting key without the prefix to its text), for <paramref name="goal"/> (null allowed).</summary>
        public static WorldEventHoldTuning Tuning(IDictionary<string, string> props, GoalDef goal = null, int participants = 1)
        {
            var full = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var kv in props ?? new Dictionary<string, string>())
                full[K.PropertyPrefix + kv.Key] = kv.Value;

            return WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, p => full.TryGetValue(p, out var v) ? v : null), goal, participants);
        }

        public static Position At(float x, float y) => new Position(0x00010064, x, y, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
    }
}
