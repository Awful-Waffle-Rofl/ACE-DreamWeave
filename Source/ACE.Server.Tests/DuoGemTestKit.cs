using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Shared fixtures for the Duo Summon Gem and Summon Gem Hammer tests (Custom Summons Task 7): real PetDevice / Gem
    /// objects built from in-memory weenies, and a fake pack whose every operation is logged in order and can be made
    /// to fail, so the destroy/create ordering is tested without a Session.
    /// </summary>
    internal static class DuoGemTestKit
    {
        private static int nextGuid = 0x7F5C0000;
        private static uint nextWcid = 997000;

        /// <summary>A PetClass the fixture pet-type lookup answers as a PASSIVE pet (WeenieType.Pet); every other wcid is a CombatPet.</summary>
        public const int PassivePetClass = 6001;

        /// <summary>Installs the fixture pet-type lookup and returns the previous one, for the caller's ClassCleanup to restore.</summary>
        public static Func<uint, WeenieType?> InstallPetTypes()
        {
            var previous = DuoSummonGem.PetWeenieTypeLookup;
            DuoSummonGem.PetWeenieTypeLookup = wcid => wcid == PassivePetClass ? WeenieType.Pet : WeenieType.CombatPet;
            return previous;
        }

        public static ObjectGuid NextGuid() => new ObjectGuid((uint)Interlocked.Increment(ref nextGuid));

        /// <summary>A completed summon item: PetClass, charges, ratings, requirement and element as given.</summary>
        public static PetDevice Summon(string name, int petClass, int structure = 50, int reqLevel = 570, bool reqSpec = true,
            UiEffects element = UiEffects.Undef, int damage = 0, int crit = 0, int? mastery = null)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.PetClass, petClass },
                { PropertyInt.MaxStructure, 50 },
                { PropertyInt.Structure, structure },
                { PropertyInt.UseRequiresSkill, (int)Skill.Summoning },
                { PropertyInt.UseRequiresSkillLevel, reqLevel },
                { PropertyInt.SharedCooldown, DuoSummonGem.SummonSharedCooldown },
                { PropertyInt.GearDamage, damage },
                { PropertyInt.GearCrit, crit },
                { PropertyInt.ItemUseable, (int)Usable.Contained },
                { PropertyInt.Value, 1234 },
            };

            if (reqSpec) ints[PropertyInt.UseRequiresSkillSpec] = (int)Skill.Summoning;
            if (element != UiEffects.Undef) ints[PropertyInt.UiEffects] = (int)element;
            if (mastery.HasValue) ints[PropertyInt.SummoningMastery] = mastery.Value;

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = ints,
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.CooldownDuration, 45.0 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            var device = new PetDevice(weenie, NextGuid());

            // the weenie constructor refills charges; the item under test carries what it was given
            device.SetProperty(PropertyInt.Structure, structure);

            return device;
        }

        /// <summary>A blank Duo Summon Gem as sold: a PetDevice with a use-on-target cursor and no pet, charges or cooldown.</summary>
        public static PetDevice BlankGem()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemUseable, (int)Usable.SourceContainedTargetContained } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.DuoSummonGem, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Duo Summon Gem" } },
            };

            return new PetDevice(weenie, NextGuid());
        }

        public static Gem Dagger()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.SacrificialDagger, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sacrificial Dagger" } },
            };

            return new Gem(weenie, NextGuid());
        }

        public static Gem Hammer()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.SummonGemHammer, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Summon Gem Hammer" } },
            };

            return new Gem(weenie, NextGuid());
        }

        /// <summary>A gem with both slots filled from <paramref name="a"/> then <paramref name="b"/>, through the real absorb.</summary>
        public static PetDevice BoundGem(FakeDuoGemInventory pack, PetDevice a, PetDevice b)
        {
            var gem = BlankGem();
            pack.Put(gem, a, b);

            if (DuoSummonGem.Absorb(pack, gem, a, out _) != DuoAbsorbResult.Absorbed)
                throw new InvalidOperationException("fixture: first absorb failed");
            if (DuoSummonGem.Absorb(pack, gem, b, out _) != DuoAbsorbResult.Bound)
                throw new InvalidOperationException("fixture: second absorb failed");

            pack.Log.Clear();
            pack.Destroyed.Clear();
            return gem;
        }
    }

    /// <summary>A pack of real objects. Every call is appended to <see cref="Log"/> as "op:name".</summary>
    internal sealed class FakeDuoGemInventory : IDuoGemInventory
    {
        private static int nextRestoredGuid = 0x7F5D0000;

        public readonly HashSet<WorldObject> Pack = new HashSet<WorldObject>();
        public readonly List<string> Log = new List<string>();
        public readonly List<WorldObject> Destroyed = new List<WorldObject>();
        public readonly List<WorldObject> Committed = new List<WorldObject>();
        public readonly List<WorldObject> Discarded = new List<WorldObject>();

        /// <summary>How many objects CanAddAll accepts at once.</summary>
        public int FreeSlots = 10;

        /// <summary>When set, TryConsumeOne returns false for that object.</summary>
        public WorldObject FailConsumeOf;

        /// <summary>When set, TryConsumeOne removes that object from the pack and then throws, before destroying it.</summary>
        public WorldObject ThrowAfterRemoving;

        /// <summary>When set, TryAddToPack fails (or throws) on this 1-based add.</summary>
        public int FailAddNumber;
        public bool ThrowOnFailedAdd;

        /// <summary>When set, BuildRestored returns null for this 1-based build.</summary>
        public int FailBuildNumber;

        private int adds;
        private int builds;

        public void Put(params WorldObject[] items)
        {
            foreach (var wo in items) Pack.Add(wo);
        }

        public bool HasInPack(WorldObject wo) => wo != null && Pack.Contains(wo);

        public bool TryConsumeOne(WorldObject wo)
        {
            Log.Add($"consume:{wo.Name}");

            if (ReferenceEquals(wo, FailConsumeOf) || !Pack.Contains(wo))
                return false;

            // like Player.TryConsumeFromInventoryWithNetworking(wo, 1): a stack loses one and stays in the pack
            var stack = wo.StackSize ?? 1;
            if (stack > 1)
            {
                wo.SetProperty(PropertyInt.StackSize, stack - 1);
                return true;
            }

            Pack.Remove(wo);

            // like a throw between TryRemoveFromInventory and Destroy inside the live consume
            if (ReferenceEquals(wo, ThrowAfterRemoving))
                throw new InvalidOperationException("fixture: consume threw after the removal");

            Destroyed.Add(wo);
            return true;
        }

        public void Save(WorldObject wo) => Log.Add($"save:{wo.Name}");

        public WorldObject BuildRestored(DuoGemSnapshot snapshot)
        {
            builds++;
            Log.Add($"build:{snapshot.Name}");

            if (builds == FailBuildNumber)
                return null;

            var guid = (uint)Interlocked.Increment(ref nextRestoredGuid);

            return WorldObjectFactory.CreateWorldObject(snapshot.BuildRestoredBiota(guid));
        }

        public void Discard(WorldObject wo)
        {
            Log.Add($"discard:{wo.Name}");
            Discarded.Add(wo);
        }

        public bool CanAddAll(IReadOnlyList<WorldObject> items)
        {
            Log.Add($"canadd:{items.Count}");
            return items.Count <= FreeSlots;
        }

        public bool TryAddToPack(WorldObject wo)
        {
            adds++;
            Log.Add($"add:{wo.Name}");

            if (adds == FailAddNumber)
            {
                if (ThrowOnFailedAdd)
                    throw new InvalidOperationException("fixture: add threw");
                return false;
            }

            Pack.Add(wo);
            return true;
        }

        public void UndoAdd(WorldObject wo)
        {
            Log.Add($"undo:{wo.Name}");
            Pack.Remove(wo);
            Discarded.Add(wo);
        }

        public void CommitAdd(WorldObject wo)
        {
            Log.Add($"commit:{wo.Name}");
            Committed.Add(wo);
        }

        public int Count(string prefix) => Log.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

        public int IndexOf(string entry) => Log.IndexOf(entry);
    }
}
