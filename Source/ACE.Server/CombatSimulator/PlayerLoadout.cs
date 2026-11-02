using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// What a character fights with, chosen from everything it POSSESSES rather than from what it happened to
    /// have in hand when the snapshot was taken. A snapshot records the logout loadout, and a character commonly
    /// logs out holding a buffing wand while it fights with weapons from its pack (the first real snapshot
    /// profiled did exactly that). Built by PlayerAttackerSpecs.Primary.
    /// </summary>
    public sealed class PlayerLoadout
    {
        /// <summary>The attack skill the character fights with (its highest trained or specialized attack skill).</summary>
        public Skill Skill { get; init; }

        public PlayerAttackMode Mode { get; init; }

        /// <summary>The weapon, launcher or caster to put in hand; null for unarmed melee or wandless casting.</summary>
        public WorldObject Weapon { get; init; }

        /// <summary>Missile only: the ammo matching the launcher.</summary>
        public WorldObject Ammo { get; init; }

        /// <summary>Magic only: the war or void projectile (PlayerAttackerSpecs.BestWarOrVoidProjectile).</summary>
        public uint SpellId { get; init; }

        public override string ToString() => $"{Mode} via {Skill}: {Weapon?.Name ?? "nothing in hand"}{(Ammo != null ? $" + {Ammo.Name}" : "")}{(SpellId != 0 ? $", spell {SpellId}" : "")}";

        /// <summary>
        /// The attack skills that decide the mode, and the mode each implies. Retired skills (Axe, Sword, Bow, ...)
        /// are absent: ConvertToMoASkill already folds them into these.
        /// </summary>
        internal static readonly IReadOnlyDictionary<Skill, PlayerAttackMode> AttackSkills = new Dictionary<Skill, PlayerAttackMode>
        {
            { Skill.HeavyWeapons, PlayerAttackMode.Melee },
            { Skill.LightWeapons, PlayerAttackMode.Melee },
            { Skill.FinesseWeapons, PlayerAttackMode.Melee },
            { Skill.TwoHandedCombat, PlayerAttackMode.Melee },
            { Skill.MissileWeapons, PlayerAttackMode.Missile },
            { Skill.WarMagic, PlayerAttackMode.Magic },
            { Skill.VoidMagic, PlayerAttackMode.Magic },
        };

        /// <summary>
        /// The character's attack skill: the trained or specialized attack skill with the highest Current, then
        /// specialized before trained, then enum order. Null when no attack skill is trained.
        /// </summary>
        public static Skill? PrimaryAttackSkill(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            var trained = AttackSkills.Keys
                .Select(s => player.GetCreatureSkill(s))
                .Where(cs => cs.AdvancementClass == SkillAdvancementClass.Trained || cs.AdvancementClass == SkillAdvancementClass.Specialized)
                .OrderByDescending(cs => cs.Current)
                .ThenByDescending(cs => cs.AdvancementClass)
                .ThenBy(cs => cs.Skill)
                .FirstOrDefault();

            return trained?.Skill;
        }

        /// <summary>The slots that decide what is "in hand". A shield-slot item counts only when it is an off-hand weapon.</summary>
        private const EquipMask HandSlots = EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.MissileAmmo | EquipMask.Held | EquipMask.TwoHanded;

        /// <summary>
        /// Runs <paramref name="body"/> with this loadout in the player's hands, then puts everything back. Only
        /// for an offline Player that is in no landblock (one from ProfileBuilder.HydrateFromSnapshot or the
        /// offline hydration): it edits EquippedObjects and the items' CurrentWieldedLocation, ParentLocation and
        /// Wielder directly, with none of the side effects a real wield has.
        ///
        /// APPROXIMATION, disclosed: a real wield also casts the item's own spells on the wielder and removes the
        /// unwielded item's, and recomputes nothing else this read depends on. The player's enchantments are
        /// therefore the ones from the snapshot's loadout. An item's own enchantments (player-cast weapon buffs
        /// such as Blood Drinker, which live on the item) are read as stored.
        /// </summary>
        public T With<T>(Player player, Func<T> body)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (body == null)
                throw new ArgumentNullException(nameof(body));

            var removed = player.EquippedObjects
                .Where(kvp => kvp.Value.CurrentWieldedLocation is EquipMask slot
                    && ((slot & HandSlots) != 0 || (slot == EquipMask.Shield && !kvp.Value.IsShield)))
                .ToList();

            var placed = new List<(WorldObject item, EquipMask? slot, ParentLocation? parent, WorldObject wielder)>();

            foreach (var kvp in removed)
                player.EquippedObjects.Remove(kvp.Key);

            try
            {
                Place(player, Weapon, SlotFor(Weapon), placed);

                // A thrown weapon is its own ammo (Candidates: Weapon == Ammo). Placing it a second time in the ammo
                // slot would overwrite its MissileWeapon location, so GetEquippedMissileWeapon would find nothing
                // (code review 2026-10-23).
                if (!ReferenceEquals(Ammo, Weapon))
                    Place(player, Ammo, EquipMask.MissileAmmo, placed);

                return body();
            }
            finally
            {
                // Reverse order, so an item placed twice would come back to its FIRST captured state.
                for (var i = placed.Count - 1; i >= 0; i--)
                {
                    var (item, slot, parent, wielder) = placed[i];
                    player.EquippedObjects.Remove(item.Guid);

                    item.CurrentWieldedLocation = slot;
                    item.ParentLocation = parent;
                    item.Wielder = wielder;
                }

                foreach (var kvp in removed)
                    player.EquippedObjects[kvp.Key] = kvp.Value;
            }
        }

        private static void Place(Player player, WorldObject item, EquipMask slot, List<(WorldObject, EquipMask?, ParentLocation?, WorldObject)> placed)
        {
            if (item == null)
                return;

            // Dedupe: an item already placed keeps its first captured state (the one to restore).
            if (!placed.Any(p => ReferenceEquals(p.Item1, item)))
                placed.Add((item, item.CurrentWieldedLocation, item.ParentLocation, item.Wielder));

            item.CurrentWieldedLocation = slot;
            item.ParentLocation = slot == EquipMask.MissileAmmo ? (ParentLocation?)null : ACE.Entity.Enum.ParentLocation.RightHand;
            item.Wielder = player;

            player.EquippedObjects[item.Guid] = item;
        }

        private static EquipMask SlotFor(WorldObject item)
        {
            if (item == null)
                return EquipMask.None;

            if (item is Caster)
                return EquipMask.Held;

            if (item is MissileLauncher || (item.ValidLocations & EquipMask.MissileWeapon) != 0)
                return EquipMask.MissileWeapon;

            if ((item.ValidLocations & EquipMask.TwoHanded) != 0)
                return EquipMask.TwoHanded;

            return EquipMask.MeleeWeapon;
        }

        /// <summary>
        /// Every loadout the character could fight with in <paramref name="skill"/>: each possessed weapon that
        /// trains that skill and passes the production wield check (for missile, each launcher paired with each
        /// possessed ammo of its AmmoType that also passes; for magic, each wieldable caster and casting with no
        /// caster). Melee also offers fighting with nothing in hand. Never empty for melee or magic.
        /// </summary>
        internal static List<PlayerLoadout> Candidates(Player player, Skill skill, uint spellId)
        {
            var mode = AttackSkills[skill];
            var possessions = player.GetAllPossessions().GroupBy(i => i.Guid).Select(g => g.First()).ToList();
            var result = new List<PlayerLoadout>();

            switch (mode)
            {
                case PlayerAttackMode.Melee:
                    foreach (var weapon in possessions.Where(i => i is MeleeWeapon && player.ConvertToMoASkill(i.WeaponSkill) == skill && player.CanWieldForSimulator(i)))
                        result.Add(new PlayerLoadout { Skill = skill, Mode = mode, Weapon = weapon });

                    result.Add(new PlayerLoadout { Skill = skill, Mode = mode });
                    break;

                case PlayerAttackMode.Missile:
                    foreach (var launcher in possessions.Where(i => (i is MissileLauncher || (i.ValidLocations & EquipMask.MissileWeapon) != 0)
                        && player.ConvertToMoASkill(i.WeaponSkill) == skill && player.CanWieldForSimulator(i)))
                    {
                        if (!launcher.IsAmmoLauncher)
                        {
                            // thrown weapons are their own ammo
                            result.Add(new PlayerLoadout { Skill = skill, Mode = mode, Weapon = launcher, Ammo = launcher });
                            continue;
                        }

                        foreach (var ammo in possessions.Where(a => a is Ammunition && a.AmmoType == launcher.AmmoType && player.CanWieldForSimulator(a)))
                            result.Add(new PlayerLoadout { Skill = skill, Mode = mode, Weapon = launcher, Ammo = ammo });
                    }
                    break;

                case PlayerAttackMode.Magic:
                    foreach (var caster in possessions.Where(i => i is Caster && player.CanWieldForSimulator(i)))
                        result.Add(new PlayerLoadout { Skill = skill, Mode = mode, Weapon = caster, SpellId = spellId });

                    result.Add(new PlayerLoadout { Skill = skill, Mode = mode, SpellId = spellId });
                    break;
            }

            return result;
        }
    }
}
