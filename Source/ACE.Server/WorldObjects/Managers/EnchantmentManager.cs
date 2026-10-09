using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.Structure;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects.Managers
{
    public enum StackType
    {
        None,
        Initial,
        Surpass,
        Refresh,
        Surpassed,
    };

    /// <summary>
    /// THIS FILE NOW DIVERGES FROM UPSTREAM 08471633e (2026-08-17), for the first time for weapon mods.
    /// Longevity (WeaponModId.Longevity) is wired at both duration sites - the refresh path in Add() and
    /// BuildEntry() - immediately after the pre-existing AugmentationIncreasedSpellDuration term, which is
    /// the precedent this follows (same guard shape, same "!isWeaponSpell && spell.DotDuration == 0"-style
    /// scoping, here expressed as "caster == WorldObject" for a self-cast rather than a DoT exclusion). Both
    /// sites are required: leaving BuildEntry unwired would make Longevity dead on the first cast of a buff,
    /// and leaving the refresh path unwired would make it dead on every re-cast of one already active. See
    /// WeaponModRegistry.cs's Tier B v4 class remarks and Longevity's row comment for the full context.
    ///
    /// The AugmentationIncreasedSpellDuration term itself now also carries the Custom Dreamweave spell
    /// duration augmentation (WaffleACE, DreamWeave, 2026-09-05), additively inside the SAME term via
    /// CustomAugmentations.SpellDurationMultiplier - deliberately not as a third multiplicative factor
    /// alongside Longevity. It is wired at those two sites AND at AddEnchantmentResult.BuildStack, which is
    /// where the surpass/surpassed comparison reads a duration; all three or the effect is inconsistent.
    /// </summary>
    public class EnchantmentManager
    {
        public WorldObject WorldObject { get; }
        public Player Player { get; }

        /// <summary>
        /// Returns TRUE if this object has any active enchantments in the registry
        /// </summary>
        public virtual bool HasEnchantments => WorldObject.Biota.PropertiesEnchantmentRegistry.HasEnchantments(WorldObject.BiotaDatabaseLock);

        /// <summary>
        /// Returns TRUE If this object has a vitae penalty
        /// </summary>
        public virtual bool HasVitae => WorldObject.Biota.PropertiesEnchantmentRegistry.HasEnchantment((uint)SpellId.Vitae, WorldObject.BiotaDatabaseLock);

        /// <summary>
        /// Constructs a new EnchantmentManager for a WorldObject
        /// </summary>
        public EnchantmentManager(WorldObject obj)
        {
            WorldObject = obj;
            Player = obj as Player;
        }


        /// <summary>
        /// Returns TRUE if registry contains spellId for this creature
        /// </summary>
        public bool HasSpell(uint spellId)
        {
            return WorldObject.Biota.PropertiesEnchantmentRegistry.HasEnchantment(spellId, WorldObject.BiotaDatabaseLock);
        }

        /// <summary>
        /// Returns the enchantments for a specific spell
        /// </summary>
        public PropertiesEnchantmentRegistry GetEnchantment(uint spellID, uint? casterGuid = null)
        {
            return WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentBySpell((int)spellID, casterGuid, WorldObject.BiotaDatabaseLock);
        }

        /// <summary>
        /// Returns the enchantments for a specific spell from an equipment set
        /// </summary>
        public PropertiesEnchantmentRegistry GetEnchantment(uint spellID, EquipmentSet equipmentSet)
        {
            return WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentBySpellSet((int)spellID, equipmentSet, WorldObject.BiotaDatabaseLock);
        }

        /// <summary>
        /// Returns a list of all the active enchantments for a magic school
        /// </summary>
        public List<PropertiesEnchantmentRegistry> GetEnchantments(MagicSchool magicSchool)
        {
            var spells = new List<PropertiesEnchantmentRegistry>();

            var topLayerEnchantments = WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentsTopLayer(WorldObject.BiotaDatabaseLock, SpellSet.SetSpells);

            foreach (var enchantment in topLayerEnchantments)
            {
                if (enchantment.SpellId > SpellCategory_Cooldown)
                    continue;

                var spell = new Spell(enchantment.SpellId);

                if (spell.NotFound)
                {
                    Console.WriteLine($"EnchantmentManager.GetEnchantments({magicSchool}): couldn't find spell {enchantment.SpellId} for {WorldObject.Name}");
                    continue;
                }

                if (spell.School == magicSchool)
                    spells.Add(enchantment);
            }

            return spells;
        }

        /// <summary>
        /// Returns all of the enchantments for a category
        /// </summary>
        public List<PropertiesEnchantmentRegistry> GetEnchantments(SpellCategory spellCategory)
        {
            return WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentsByCategory(spellCategory, WorldObject.BiotaDatabaseLock);
        }

        /// <summary>
        /// Returns the top layers in each spell category for a StatMod type
        /// </summary>
        public List<PropertiesEnchantmentRegistry> GetEnchantments_TopLayer(EnchantmentTypeFlags statModType)
        {
            return WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentsTopLayerByStatModType(statModType, WorldObject.BiotaDatabaseLock, SpellSet.SetSpells);
        }

        /// <summary>
        /// Returns the top layers in each spell category for a StatMod type + key
        /// </summary>
        public List<PropertiesEnchantmentRegistry> GetEnchantments_TopLayer(EnchantmentTypeFlags statModType, uint statModKey, bool handleMultiple = false)
        {
            return WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentsTopLayerByStatModType(statModType, statModKey, WorldObject.BiotaDatabaseLock, SpellSet.SetSpells, handleMultiple);
        }

        /// <summary>
        /// Add/update an enchantment in this object's registry
        /// </summary>
        public virtual AddEnchantmentResult Add(Spell spell, WorldObject caster, WorldObject weapon, bool equip = false, bool isWeaponSpell = false)
        {
            var result = new AddEnchantmentResult();

            // check for existing spell in this category
            var entries = GetEnchantments(spell.Category);

            // if none, add new record
            if (entries.Count == 0)
            {
                var newEntry = BuildEntry(spell, caster, weapon, equip, isWeaponSpell);
                newEntry.LayerId = 1;
                WorldObject.Biota.PropertiesEnchantmentRegistry.AddEnchantment(newEntry, WorldObject.BiotaDatabaseLock);
                WorldObject.ChangesDetected = true;

                result.Enchantment = newEntry;
                result.StackType = StackType.Initial;
                return result;
            }

            result.BuildStack(entries, spell, caster, equip, isWeaponSpell);

            // handle cases:
            // surpassing: new spell is written to next layer
            // refreshing: - key by caster guid
            // surpassed:  - underpowered spell is written to next layer?

            // note that these cases are not exclusive,
            // consider case: strength 3 -> strength 6 -> strength 3
            // for the 2nd cast of strength 3, it would have 1 refresh and 1 surpassed
            // would 2nd cast of strength 3 refresh the 1st, but still be surpassed by 6?

            var refreshSpell = result.Refresh.Count > 0 ? result.RefreshCaster : null;

            if (refreshSpell == null)
            {
                var newEntry = BuildEntry(spell, caster, weapon, equip);
                newEntry.LayerId = result.NextLayerId;
                WorldObject.Biota.PropertiesEnchantmentRegistry.AddEnchantment(newEntry, WorldObject.BiotaDatabaseLock);

                result.Enchantment = newEntry;
            }
            else
            {
                // for multiple void casters casting the same DoT,
                // we might want to sort by StatModValue in GetEnchantments_TopLayer()

                // for the same void caster re-casting the same DoT,
                // should be update the StatModVal here?

                var duration = spell.Duration;

                // The retail Archmage's Endurance augmentation and Fi's Lingering Casting (the Custom
                // Dreamweave +10% spell duration augmentation) compose ADDITIVELY inside this one term - see
                // CustomAugmentations.SpellDurationMultiplier for why it is not a second multiplicative
                // factor. HasSpellDurationAug replaces ONLY the old "retail > 0" clause, which skipped the
                // whole term and would have zeroed a custom-only holder; !isWeaponSpell and
                // spell.DotDuration == 0 are preserved verbatim.
                if (caster is Player player && CustomAugmentations.HasSpellDurationAug(player.AugmentationIncreasedSpellDuration, player.AugmentationSpellDurationCustom) && !isWeaponSpell && spell.DotDuration == 0)
                    duration *= CustomAugmentations.SpellDurationMultiplier(player.AugmentationIncreasedSpellDuration, player.AugmentationSpellDurationCustom, CustomAugBroker.SpellDurationBonus);

                // Longevity (WeaponModId.Longevity): extends duration on a self-cast beneficial enchantment.
                // caster == WorldObject is "the caster cast this on themselves" - the same test the class
                // remarks on Player_WeaponMods.cs use for a single-item read, here applied to identify a
                // self-target rather than a carrier item. !isWeaponSpell excludes a wand's built-in cast, the
                // same exclusion the AugmentationIncreasedSpellDuration term two lines above already carries -
                // a wand's self-targeting beneficial spell must not additionally benefit from a weapon mod
                // rolled on that same wand.
                if (caster is Player longevityCaster && caster == WorldObject && !isWeaponSpell && spell.IsBeneficial)
                    duration *= 1.0f + (float)longevityCaster.GetCasterOnlyModValue(WeaponModId.Longevity);

                // Malediction (Blood Mage T2): a refresh recomputes the duration from the spell rather than
                // rebuilding the entry, so the extension has to be reapplied here or re-casting the debuff
                // would clip it back to the base length. The refreshed entry keeps its original (already
                // boosted) StatModValue - see the comment above; intensity is set once, at BuildEntry.
                if (caster is Player maledictionRefreshCaster &&
                    maledictionRefreshCaster.TryGetMaledictionMods(spell, out _, out var maledictionRefreshDuration))
                {
                    duration = MaledictionAbility.ExtendDuration(duration, maledictionRefreshDuration);
                }

                var timeRemaining = refreshSpell.Duration + refreshSpell.StartTime;

                if (duration > timeRemaining)
                {
                    refreshSpell.StartTime = 0;
                    refreshSpell.Duration = duration;
                }

                result.Enchantment = refreshSpell;
            }
            WorldObject.ChangesDetected = true;

            // output message is from StackType,
            // which is the largest of the combined StackTypes

            return result;
        }

        /// <summary>
        /// Builds an enchantment registry entry from a spell ID
        /// </summary>
        private PropertiesEnchantmentRegistry BuildEntry(Spell spell, WorldObject caster = null, WorldObject weapon = null, bool equip = false, bool isWeaponSpell = false)
        {
            var entry = new PropertiesEnchantmentRegistry();

            entry.EnchantmentCategory = (uint)spell.MetaSpellType;
            entry.SpellId = (int)spell.Id;
            entry.SpellCategory = spell.Category;
            entry.PowerLevel = spell.Power;

            if (caster is Creature)
            {
                entry.Duration = spell.Duration;

                // Additive retail + Custom Dreamweave duration term. Mirrors the refresh path in Add()
                // exactly; see that site's comment. Both are required, or the effect is dead on either the
                // first cast (BuildEntry) or every re-cast (Add) - the same two-site rule this file's class
                // remark records for WeaponModId.Longevity.
                if (caster is Player player && CustomAugmentations.HasSpellDurationAug(player.AugmentationIncreasedSpellDuration, player.AugmentationSpellDurationCustom) && !isWeaponSpell && spell.DotDuration == 0)
                    entry.Duration *= CustomAugmentations.SpellDurationMultiplier(player.AugmentationIncreasedSpellDuration, player.AugmentationSpellDurationCustom, CustomAugBroker.SpellDurationBonus);

                // Longevity (WeaponModId.Longevity): extends duration on a self-cast beneficial enchantment.
                // See the refresh-path comment above (Add) for the caster == WorldObject self-cast test and
                // why !isWeaponSpell is required.
                if (caster is Player longevityCaster && caster == WorldObject && !isWeaponSpell && spell.IsBeneficial)
                    entry.Duration *= 1.0f + (float)longevityCaster.GetCasterOnlyModValue(WeaponModId.Longevity);
            }
            else
            {
                if (!equip)
                {
                    entry.Duration = spell.Duration;
                }
                else
                {
                    // enchantments from equipping items are active until the item is dequipped
                    entry.Duration = -1.0;
                    entry.StartTime = 0;
                }
            }

            if (caster == null)
                entry.CasterObjectId = WorldObject.Guid.Full;
            else
                entry.CasterObjectId = caster.Guid.Full;

            entry.DegradeModifier = spell.DegradeModifier;
            entry.DegradeLimit = spell.DegradeLimit;
            entry.StatModType = spell.StatModType;
            entry.StatModKey = spell.StatModKey;
            entry.StatModValue = spell.StatModVal;

            if (spell.IsBeneficial) // should "server" data be fixed or is this the better way to do this?
                entry.StatModType |= EnchantmentTypeFlags.Beneficial;

            if (spell.IsDamageOverTime)
            {
                var heartbeatInterval = WorldObject.HeartbeatInterval ?? 5.0f;

                // scale StatModValue for HeartbeatIntervals other than the default 5s
                if (heartbeatInterval != 5.0f)
                {
                    entry.StatModValue = spell.GetDamagePerTick((float)heartbeatInterval);
                }

                // calculate runtime StatModValue for enchantment
                if (caster != null)
                {
                    entry.StatModValue = caster.CalculateDotEnchantment_StatModValue(spell, WorldObject, weapon, entry.StatModValue);
                }
                //Console.WriteLine($"enchantment_statModVal: {entry.StatModValue}");
            }

            // Malediction (Blood Mage T2): a Vulnerability / Imperil the PLAYER applies lands harder and
            // lasts longer. Applied here, while the registry entry is being built, for the same reason
            // CalculateDotEnchantment_StatModValue is applied here: the value written to the registry is the
            // one every reader sees, so the whole party's attacks benefit, not just the caster's.
            //
            // Deliberately AFTER the stack decision in Add(): stacking is keyed off spell power level, so a
            // boosted Vulnerability VI still refreshes a plain Vulnerability VI rather than surpassing it.
            if (caster is Player maledictionCaster &&
                maledictionCaster.TryGetMaledictionMods(spell, out var maledictionIntensity, out var maledictionDuration))
            {
                entry.StatModValue = MaledictionAbility.ScaleStatModValue(spell.StatModType, entry.StatModValue, maledictionIntensity);
                entry.Duration = MaledictionAbility.ExtendDuration(entry.Duration, maledictionDuration);
            }

            // handle equipment sets
            if (caster != null && caster.HasItemSet && caster.ItemSetContains(spell.Id))
            {
                entry.HasSpellSetId = true;
                entry.SpellSetId = (EquipmentSet)caster.EquipmentSetId;
            }

            return entry;
        }

        /// <summary>
        /// Starts (or restarts) a shared cooldown in the enchantment registry.
        ///
        /// An object holds at most ONE entry per cooldown spell id. Starting a cooldown that is already running
        /// refreshes the existing entry to the full duration instead of appending a second one. That case is
        /// reachable: Soul Tether (Player.CanSkipCombatPetSummonCooldown) lets a summoning device through
        /// CheckUseRequirements while its shared cooldown is still live, and Gem.OnActivate starts a cooldown on
        /// its busy/dead refusal paths before any cooldown check has run. A blind append left two entries for
        /// one spell id at layer 1, which the shard's unique index over (object_Id, spell_Id, layer_Id) cannot
        /// store - BiotaUpdater then persisted the older, shorter one and logged a warning on every save.
        ///
        /// The refreshed entry keeps its original CasterObjectId even when a different item sharing the same
        /// CooldownId restarted it, so the save is an in-place UPDATE of the existing row rather than a
        /// delete-plus-insert under that unique index (see PropertiesEnchantmentRegistryExtensions.AddOrRefreshBySpell).
        /// Code that needs "the cooldown for this CooldownId" must therefore look it up by spell id alone, never
        /// by the caster guid of the item just used.
        /// </summary>
        public virtual bool StartCooldown(WorldObject item)
        {
            var cooldownID = item.CooldownId;
            if (cooldownID == null)
                return false;

            var newEntry = new PropertiesEnchantmentRegistry();

            // TODO: BiotaPropertiesEnchantmentRegistry.SpellId should be uint
            newEntry.SpellId = (int)GetCooldownSpellID(cooldownID.Value);
            newEntry.SpellCategory = (SpellCategory)SpellCategory_Cooldown;
            newEntry.HasSpellSetId = true;
            newEntry.StartTime = 0;
            newEntry.Duration = item.CooldownDuration ?? 0.0f;
            newEntry.CasterObjectId = item.Guid.Full;
            newEntry.DegradeLimit = -666;
            newEntry.StatModType = EnchantmentTypeFlags.Cooldown;
            newEntry.EnchantmentCategory = (uint)EnchantmentMask.Cooldown;

            newEntry.LayerId = 1;      // cooldown at layer 1, any spells at layer 2?
            var entry = WorldObject.Biota.PropertiesEnchantmentRegistry.AddOrRefreshBySpell(newEntry, WorldObject.BiotaDatabaseLock, out var removed);
            WorldObject.ChangesDetected = true;

            if (Player != null)
            {
                // Extras only exist if something appended a duplicate before this refresh ran. The client removes
                // an enchantment by (spell id, layer), so an extra at the survivor's own layer needs no remove - the
                // update below replaces that slot - and removing it would only blank the survivor's display too.
                if (removed != null)
                {
                    foreach (var extra in removed)
                    {
                        if (extra.LayerId != entry.LayerId)
                            Player.Session.Network.EnqueueSend(new GameEventMagicRemoveEnchantment(Player.Session, (ushort)extra.SpellId, extra.LayerId));
                    }
                }

                Player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(Player.Session, new Enchantment(Player, entry)));
            }

            return true;
        }

        /// <summary>
        /// Adds (or refreshes) a hand-made class-ability enchantment on this object, in a synthetic
        /// <see cref="SpellCategory_ClassAbility_Base"/> category. Built from <see cref="StartCooldown"/>
        /// rather than <see cref="Add"/> deliberately: Add's whole job is the retail stack/surpass/refresh
        /// duel against other entries in the SAME category, and a class ability that wants to sum with retail
        /// magic rather than compete with it is precisely the case that must not go through it.
        ///
        /// <paramref name="spell"/> supplies only the client-facing identity - the spell id the enchantment
        /// bar renders, and the power level used to order layers. It is NOT cast, resisted, or otherwise run;
        /// pick a real spell whose icon and name describe the effect.
        ///
        /// Refreshing rather than layering is the rule here: a second application of the same
        /// (category, spell id) resets the existing entry's clock instead of adding a second layer, so a
        /// repeated proc can never stack its own magnitude on itself.
        /// </summary>
        public PropertiesEnchantmentRegistry AddClassAbilityDebuff(Spell spell, WorldObject caster, SpellCategory category, EnchantmentTypeFlags statModType, uint statModKey, float statModValue, double durationSeconds, bool refreshOnlyOwnCaster = false)
        {
            return AddClassAbilityDebuff(spell.Id, spell.Power, caster, category, statModType, statModKey, statModValue, durationSeconds, refreshOnlyOwnCaster);
        }

        /// <summary>
        /// The Spell-free core of <see cref="AddClassAbilityDebuff(Spell, WorldObject, SpellCategory, EnchantmentTypeFlags, uint, float, double, bool)"/>,
        /// taking the client-facing identity as raw values. This is the virtual one, so
        /// <see cref="EnchantmentManagerWithCaching"/> only has to override a single method to cover both
        /// entry points - and it is what a unit test calls, since constructing a <see cref="Spell"/> requires
        /// a loaded client dat.
        ///
        /// <paramref name="refreshOnlyOwnCaster"/>: when true, the refresh lookup also requires the existing
        /// entry's caster to match <paramref name="caster"/>. Needed when a hand-made application uses a REAL
        /// spell id in its REAL category (e.g. Tinkerer's Inspiration reusing the retail Self Incantations) -
        /// without this guard, the lookup would find and clip a player's own longer-duration cast of the same
        /// spell instead of laying down an independent layer.
        /// </summary>
        public virtual PropertiesEnchantmentRegistry AddClassAbilityDebuff(uint spellId, uint powerLevel, WorldObject caster, SpellCategory category, EnchantmentTypeFlags statModType, uint statModKey, float statModValue, double durationSeconds, bool refreshOnlyOwnCaster = false)
        {
            // refresh an existing application of the same (category, spell) instead of layering a second one
            var existing = GetEnchantments(category).FirstOrDefault(e => e.SpellId == (int)spellId
                && (!refreshOnlyOwnCaster || e.CasterObjectId == (caster?.Guid.Full ?? 0)));

            if (existing != null)
            {
                existing.StartTime = 0;
                existing.Duration = durationSeconds;
                existing.StatModValue = statModValue;
                existing.CasterObjectId = caster?.Guid.Full ?? 0;

                WorldObject.ChangesDetected = true;

                if (Player != null)
                    Player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(Player.Session, new Enchantment(Player, existing)));

                return existing;
            }

            var newEntry = new PropertiesEnchantmentRegistry
            {
                // TODO: BiotaPropertiesEnchantmentRegistry.SpellId should be uint
                SpellId = (int)spellId,
                SpellCategory = category,
                // LayerId is assigned by AddEnchantmentAtFreeLayer below, not here. It used to be a hardcoded
                // 1, which is only safe while nothing else can hold this spell id: with refreshOnlyOwnCaster
                // the refresh lookup deliberately ignores an entry cast by anyone else, so a real spell id
                // reused in its real category (Tinkerer's Inspiration) laid a SECOND layer-1 entry beside the
                // player's own self-cast of the same spell. The shard table's unique index over
                // (object_Id, spell_Id, layer_Id) cannot hold both, so the biota save failed with a duplicate
                // entry and the player was disconnected (prod, 2026-09-06 through 09-08, four players, spells
                // 4566 and 4592). The layer is now the lowest free one for this spell id, which is also what
                // this call site's own documentation always claimed it was doing: an independent layer.
                CasterObjectId = caster?.Guid.Full ?? 0,
                PowerLevel = powerLevel,
                StartTime = 0,
                Duration = durationSeconds,
                StatModType = statModType,
                StatModKey = statModKey,
                StatModValue = statModValue,
                // BuildEntry writes the SPELL TYPE here for a real cast (see :224), not an EnchantmentMask
                // value - the field is read back as both, depending on the entry. Enchantment is what a
                // retail debuff spell would put here, so a hand-made debuff must match it.
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            WorldObject.Biota.PropertiesEnchantmentRegistry.AddEnchantmentAtFreeLayer(newEntry, WorldObject.BiotaDatabaseLock);
            WorldObject.ChangesDetected = true;

            // monsters have no session; only a player target sees the enchantment bar update. Sent AFTER the
            // layer is assigned - Enchantment copies LayerId onto the wire, so this has to follow the append.
            if (Player != null)
                Player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(Player.Session, new Enchantment(Player, newEntry)));

            return newEntry;
        }

        /// <summary>
        /// Removes a spell from the enchantment registry, and
        /// sends the relevant network messages for spell removal
        /// </summary>
        public virtual void Remove(PropertiesEnchantmentRegistry entry, bool sound = true)
        {
            if (entry == null)
                return;

            var spellID = entry.SpellId;

            if (WorldObject.Biota.PropertiesEnchantmentRegistry.TryRemoveEnchantment(entry, WorldObject.BiotaDatabaseLock))
                WorldObject.ChangesDetected = true;

            if (Player != null)
            {
                var layer = (entry.SpellId == (uint)SpellId.Vitae) ? (ushort)0 : entry.LayerId; // this line is to force vitae to be layer 0 to match retail pcaps. We save it as layer 1 to make EF Core happy.
                Player.Session.Network.EnqueueSend(new GameEventMagicRemoveEnchantment(Player.Session, (ushort)entry.SpellId, layer));

                if (sound && entry.SpellCategory != (SpellCategory)SpellCategory_Cooldown)
                    Player.Session.Network.EnqueueSend(new GameMessageSound(Player.Guid, Sound.SpellExpire, 1.0f));
            }
            else
            {
                var ownerID = WorldObject.OwnerId ?? WorldObject.WielderId;

                if (ownerID != null)
                {
                    var owner = PlayerManager.GetOnlinePlayer((uint)ownerID);

                    if (owner != null)
                    {
                        var spell = new Spell(spellID);

                        owner.Session.Network.EnqueueSend(new GameMessageSystemChat($"The spell {spell.Name} on {WorldObject.Name} has expired.", ChatMessageType.Magic));

                        if (sound)
                            owner.Session.Network.EnqueueSend(new GameMessageSound(owner.Guid, Sound.SpellExpire, 1.0f));
                    }
                }
            }
        }

        /// <summary>
        /// Removes all enchantments except for vitae and item spells
        /// Called on player death
        /// </summary>
        public virtual void RemoveAllEnchantments()
        {
            // exclude cooldowns and enchantments from items
            var spellsToExclude = WorldObject.Biota.PropertiesEnchantmentRegistry.Clone(WorldObject.BiotaDatabaseLock).Where(i => i.Duration == -1 || i.SpellId > short.MaxValue).Select(i => i.SpellId);

            WorldObject.Biota.PropertiesEnchantmentRegistry.RemoveAllEnchantments(spellsToExclude, WorldObject.BiotaDatabaseLock);
            WorldObject.ChangesDetected = true;
        }

        /// <summary>
        /// Removes all enchantments except for beneficial enchantments, vitae and item spells
        /// Called on player death
        /// </summary>
        public virtual void RemoveAllBadEnchantments()
        {
            // exclude beneficial enchantments, cooldowns and enchantments from items
            var spellsToExclude = WorldObject.Biota.PropertiesEnchantmentRegistry.Clone(WorldObject.BiotaDatabaseLock).Where(i => i.StatModType.HasFlag(EnchantmentTypeFlags.Beneficial) || i.Duration == -1 || i.SpellId > short.MaxValue).Select(i => i.SpellId);

            WorldObject.Biota.PropertiesEnchantmentRegistry.RemoveAllEnchantments(spellsToExclude, WorldObject.BiotaDatabaseLock);
            WorldObject.ChangesDetected = true;
        }

        /// <summary>
        /// For a caller that edits the registry rows directly (the PvP template overlay restores saved rows with
        /// their own layers, which Add cannot do): drops any cached derived values so the next read recomputes
        /// them. A no-op here; EnchantmentManagerWithCaching clears its caches.
        /// </summary>
        public virtual void InvalidateCaches()
        {
        }

        /// <summary>
        /// Returns the vitae enchantment
        /// </summary>
        public PropertiesEnchantmentRegistry GetVitae()
        {
            return GetEnchantment((uint)SpellId.Vitae);
        }

        /// <summary>
        /// Returns the minimum vitae for a player level
        /// </summary>
        public float GetMinVitae(uint level)
        {
            var propVitae = 1.0 - PropertyManager.GetDouble("vitae_penalty_max").Item;

            var maxPenalty = (level - 1) * 3;
            if (maxPenalty < 1)
                maxPenalty = 1;

            var globalMax = 100 - (uint)Math.Round(propVitae * 100);
            if (maxPenalty > globalMax)
                maxPenalty = globalMax;

            var minVitae = (100 - maxPenalty) / 100.0f;
            if (minVitae < propVitae)
                minVitae = (float)propVitae;

            return minVitae;
        }

        /// <summary>
        /// Called on player death
        /// </summary>
        public virtual float UpdateVitae()
        {
            if (Player == null) return 0;
            PropertiesEnchantmentRegistry vitae;

            if (!HasVitae)
            {
                // TODO refactor this so it uses the existing Add() method.

                // add entry for new vitae
                var spell = new Spell(SpellId.Vitae);

                vitae = BuildEntry(spell);
                vitae.EnchantmentCategory = (uint)EnchantmentMask.Vitae;
                vitae.LayerId = 1; // This should be 0 but EF Core seems to be very unhappy with 0 as the layer id now that we're using layer as part of the composite key.
                vitae.StatModValue = 1.0f - (float)PropertyManager.GetDouble("vitae_penalty").Item;
                WorldObject.Biota.PropertiesEnchantmentRegistry.AddEnchantment(vitae, WorldObject.BiotaDatabaseLock);
                WorldObject.ChangesDetected = true;
            }
            else
            {
                // update existing vitae
                vitae = GetVitae();
                vitae.StatModValue -= (float)PropertyManager.GetDouble("vitae_penalty").Item;
                WorldObject.ChangesDetected = true;
            }

            var minVitae = GetMinVitae((uint)Player.Level);

            if (vitae.StatModValue < minVitae)
                vitae.StatModValue = minVitae;
            if (vitae.StatModValue > 1.0f)
                vitae.StatModValue = 1.0f;

            return vitae.StatModValue;
        }

        /// <summary>
        /// Called when player crosses the VitaeCPPool threshold
        /// </summary>
        public virtual float ReduceVitae()
        {
            var vitae = GetVitae();
            vitae.StatModValue += 0.01f;

            if (vitae.StatModValue.EpsilonEquals(1.0f) || vitae.StatModValue > 1.0f)
                return 1.0f;

            return vitae.StatModValue;
        }

        /// <summary>
        /// Removes the vitae penalty for a player
        /// </summary>
        public void RemoveVitae()
        {
            if (Player == null)
                return;

            var vitae = GetVitae();

            Remove(vitae);
        }


        /// <summary>
        /// Silently removes a spell from the enchantment registry, and sends the relevant network message for dispel
        /// </summary>
        public virtual void Dispel(PropertiesEnchantmentRegistry entry)
        {
            if (entry == null)
                return;

            var spellID = entry.SpellId;

            if (WorldObject.Biota.PropertiesEnchantmentRegistry.TryRemoveEnchantment(entry, WorldObject.BiotaDatabaseLock))
                WorldObject.ChangesDetected = true;

            if (Player != null)
                Player.Session.Network.EnqueueSend(new GameEventMagicDispelEnchantment(Player.Session, (ushort)entry.SpellId, entry.LayerId));
        }

        /// <summary>
        /// Silently removes multiple spells from the enchantment registry, and sends the relevent network messages for dispel
        /// </summary>
        public virtual void Dispel(List<PropertiesEnchantmentRegistry> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            foreach (var entry in entries)
            {
                if (WorldObject.Biota.PropertiesEnchantmentRegistry.TryRemoveEnchantment(entry, WorldObject.BiotaDatabaseLock))
                    WorldObject.ChangesDetected = true;
            }
            if (Player != null)
                Player.Session.Network.EnqueueSend(new GameEventMagicDispelMultipleEnchantments(Player.Session, entries));
        }

        /// <summary>
        /// Removes all enchantments from the player on server, and sends network messages to silently dispel the enchantments
        /// </summary>
        public void DispelAllEnchantments()
        {
            var enchantments = WorldObject.Biota.PropertiesEnchantmentRegistry.Clone(WorldObject.BiotaDatabaseLock);

            Dispel(enchantments);
        }

        /// <summary>
        /// Selects a list of spells to dispel
        /// </summary>
        /// <param name="spell">The dispel spell</param>
        public List<SpellEnchantment> SelectDispel(Spell spell)
        {
            // NOTE: in the default 16PY db,
            // there are a lot of dispels where the actual #s do not match up with the spell descriptions...
            // ie. the description will say it dispels 3-6 spells, and it will only dispel 2-4 etc.

            // dispel factors:
            // min_power - the minimum power level of spell to dispel (unused?)
            // max_power - the maximum power level of spell to dispel
            // power_variance - rng for power level, unused?
            // dispel_school - the magic school to dispel, 0 if all
            // align - type of spells to dispel: positive, negative, or all
            // number - the maximum # of spells to dispel
            // number_variance - number * number_variance = the minimum # of spells to dispel
            var minPower = spell.MinPower;
            var maxPower = spell.MaxPower;
            var powerVariance = spell.PowerVariance;
            var dispelSchool = spell.DispelSchool;
            var align = spell.Align;
            var number = spell.Number;
            var numberVariance = spell.NumberVariance;

            //var enchantments = GetEnchantments_TopLayer(WorldObject.Biota.GetEnchantments(WorldObject.BiotaDatabaseLock));
            var enchantments = WorldObject.Biota.PropertiesEnchantmentRegistry.Clone(WorldObject.BiotaDatabaseLock);

            var filtered = enchantments.Where(e => e.PowerLevel >= minPower && e.PowerLevel <= maxPower);

            // no dispel for enchantments from item sources (and vitae)
            filtered = filtered.Where(e => e.Duration != -1);

            // PvP dispel vuln lock (D1): after a recent PK attack, a player-cast vulnerability debuff on the
            // target cannot be dispelled away. No-op outside PvP / with the lever off.
            filtered = PvpRules.ApplyDispelVulnLock(WorldObject, filtered.ToList(), PvpChokePoint.D1);

            // for dispelSchool and align,
            // we probably could do some calculations to figure out these values directly from the enchantments
            // but it would be far easier and more reliable to just do them through the spells
            // since dispels are not a time-critical function, this should still be fine
            var spells = new List<SpellEnchantment>();
            foreach (var filter in filtered)
            {
                var spellEnchantment = new SpellEnchantment(filter);

                if (!spellEnchantment.Spell.NotFound)
                    spells.Add(spellEnchantment);
            }

            var filterSpells = spells;
            if (dispelSchool != MagicSchool.None)
                filterSpells = filterSpells.Where(s => s.Spell != null && s.Spell.School == dispelSchool).ToList();

            if (align != DispelType.All)
            {
                if (align == DispelType.Positive)
                    filterSpells = filterSpells.Where(s => s.Spell != null && s.Spell.IsBeneficial).ToList();
                else if (align == DispelType.Negative)
                    filterSpells = filterSpells.Where(s => s.Spell != null && s.Spell.IsHarmful).ToList();
            }

            // dispel all
            if (number == -1)
                return filterSpells;

            // get number of spells to dispel
            var dispelNum = number;
            if (numberVariance != 1.0f)
            {
                var maxDispelNum = dispelNum;
                var minDispelNum = (int)Math.Round(dispelNum * (1.0f - numberVariance));

                // factor in rng variance
                dispelNum = ThreadSafeRandom.Next(minDispelNum, maxDispelNum);
            }

            // randomize the filtered spell list
            filterSpells.Shuffle();

            // select the required # of spells
            return filterSpells.Take(dispelNum).ToList();
        }


        /// <summary>
        /// Gets the VitalRate key for a CreatureVital
        /// </summary>
        public PropertyFloat GetVitalRateKey(CreatureVital vital)
        {
            switch (vital.Vital)
            {
                case PropertyAttribute2nd.MaxHealth:
                    return PropertyFloat.HealthRate;
                case PropertyAttribute2nd.MaxStamina:
                    return PropertyFloat.StaminaRate;
                case PropertyAttribute2nd.MaxMana:
                    return PropertyFloat.ManaRate;
            }
            return 0;
        }

        /// <summary>
        /// Gets the ArmorModVsType key for a DamageType
        /// </summary>
        public PropertyFloat GetImpenBaneKey(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Slash:
                    return PropertyFloat.ArmorModVsSlash;
                case DamageType.Pierce:
                    return PropertyFloat.ArmorModVsPierce;
                case DamageType.Bludgeon:
                    return PropertyFloat.ArmorModVsBludgeon;
                case DamageType.Fire:
                    return PropertyFloat.ArmorModVsFire;
                case DamageType.Cold:
                    return PropertyFloat.ArmorModVsCold;
                case DamageType.Acid:
                    return PropertyFloat.ArmorModVsAcid;
                case DamageType.Electric:
                    return PropertyFloat.ArmorModVsElectric;
                case DamageType.Nether:
                    return PropertyFloat.ArmorModVsNether;
            }
            return 0;
        }

        /// <summary>
        /// Gets the resistance PropertyFloat for a DamageType
        /// </summary>
        public PropertyFloat GetResistanceKey(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Slash:
                    return PropertyFloat.ResistSlash;
                case DamageType.Pierce:
                    return PropertyFloat.ResistPierce;
                case DamageType.Bludgeon:
                    return PropertyFloat.ResistBludgeon;
                case DamageType.Fire:
                    return PropertyFloat.ResistFire;
                case DamageType.Cold:
                    return PropertyFloat.ResistCold;
                case DamageType.Acid:
                    return PropertyFloat.ResistAcid;
                case DamageType.Electric:
                    return PropertyFloat.ResistElectric;
                case DamageType.Nether:
                    return PropertyFloat.ResistNether;
            }
            return 0;
        }

        // refactor me

        /// <summary>
        /// Returns the additive modifers to an attribute from enchantments
        /// </summary>
        public virtual int GetAttributeMod_Additive(PropertyAttribute attribute)
        {
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.Additive, (uint)attribute, true);

            var attributeMod = 0;
            foreach (var enchantment in enchantments)
                attributeMod += (int)enchantment.StatModValue;

            return attributeMod;
        }

        /// <summary>
        /// Returns the multiplicative modifiers to an attribute from enchantments
        /// </summary>
        public virtual float GetAttributeMod_Multiplier(PropertyAttribute attribute)
        {
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.Multiplicative, (uint)attribute, true);

            var multiplier = 1.0f;
            foreach (var enchantment in enchantments)
                multiplier *= enchantment.StatModValue;

            return multiplier;
        }

        /// <summary>
        /// Gets the additive modifiers to a vital / secondary attribute
        /// </summary>
        public virtual float GetVitalMod_Additives(CreatureVital vital)
        {
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.Additive, (uint)vital.Vital, true);

            // additive
            var modifier = 0.0f;
            foreach (var enchantment in enchantments)
                modifier += enchantment.StatModValue;

            return modifier;
        }

        /// <summary>
        /// Gets the multiplicative modifiers to a vital / secondary attribute
        /// </summary>
        public virtual float GetVitalMod_Multiplier(CreatureVital vital)
        {
            // multiplicatives (asheron's lesser benediction)
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.Multiplicative, (uint)vital.Vital, true);

            var multiplier = 1.0f;
            foreach (var enchantment in enchantments)
                multiplier *= enchantment.StatModValue;

            return multiplier;
        }

        /// <summary>
        /// Returns the additive bonus from XP enchantments, such as Augmented Understanding
        /// </summary>
        public virtual float GetXPBonus()
        {
            var enchantments = GetEnchantments(SpellCategory.TrinketXPRaising);

            // TODO: temporary code to handle both additive and multiplicative mods
            // should be additive in database, update when everything is in sync
            var modifier = 0.0f;

            foreach (var enchantment in enchantments.OrderByDescending(i => i.PowerLevel).Take(1))
            {
                if (enchantment.StatModType.HasFlag(EnchantmentTypeFlags.Multiplicative))
                    modifier += enchantment.StatModValue - 1.0f;
                else
                    modifier += enchantment.StatModValue;
            }
            return modifier;
        }

        /// <summary>
        /// Returns the additive modifiers to a skill from enchantments
        /// </summary>
        public virtual int GetSkillMod_Additives(Skill skill)
        {
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive, (uint)skill, true);

            var skillMod = 0;
            foreach (var enchantment in enchantments)
                skillMod += (int)enchantment.StatModValue;

            if (SkillHelper.DefenseSkills.Contains(skill))
                skillMod += GetDefenseDebuffMod();

            if (SkillHelper.AttackSkills.Contains(skill))
                skillMod += GetAttackDebuffMod();

            return skillMod;
        }

        /// <summary>
        /// Returns the multiplicative modifiers to a skill from enchantments
        /// </summary>
        public virtual float GetSkillMod_Multiplier(Skill skill)
        {
            // shroud spells
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Multiplicative, (uint)skill, true);

            var multiplier = 1.0f;
            foreach (var enchantment in enchantments)
                multiplier *= enchantment.StatModValue;

            return multiplier;
        }

        /// <summary>
        /// Returns the sum of the StatModValues for an EnchantmentTypeFlag
        /// </summary>
        public int GetModifier(EnchantmentTypeFlags type, bool? positive = null)
        {
            var enchantments = GetEnchantments_TopLayer(type);

            var modifier = 0;
            foreach (var enchantment in enchantments)
            {
                var statModVal = (int)enchantment.StatModValue;

                if (positive == null || positive.Value && statModVal > 0 || !positive.Value && statModVal < 0)
                {
                    modifier += statModVal;
                }
            }
            return modifier;
        }

        /// <summary>
        /// Returns the sum of the modifiers for a StatModKey
        /// </summary>
        public int GetAdditiveMod(PropertyInt statModKey)
        {
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.Additive, (uint)statModKey);

            var modifier = 0;
            foreach (var enchantment in enchantments.Where(e => (e.StatModType & EnchantmentTypeFlags.Skill) == 0))
                modifier += (int)enchantment.StatModValue;

            return modifier;
        }

        /// <summary>
        /// Returns the sum of the enchantment statmod values
        /// </summary>
        public float GetAdditiveMod(List<PropertiesEnchantmentRegistry> enchantments)
        {
            var modifier = 0.0f;
            foreach (var enchantment in enchantments)
                modifier += enchantment.StatModValue;

            return modifier;
        }

        public float GetAdditiveMod(PropertyFloat statModKey)
        {
            var typeFlags = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;

            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)statModKey);

            var modifier = 0.0f;
            foreach (var enchantment in enchantments)
                modifier += enchantment.StatModValue;

            return modifier;
        }

        /// <summary>
        /// Returns the product of the modifiers for a StatModKey
        /// </summary>
        public float GetMultiplicativeMod(PropertyFloat statModKey)
        {
            var enchantments = GetEnchantments_TopLayer(EnchantmentTypeFlags.Multiplicative, (uint)statModKey);

            // multiplicative
            var modifier = 1.0f;
            foreach (var enchantment in enchantments)
                modifier *= enchantment.StatModValue;

            return modifier;
        }


        /// <summary>
        /// Returns the base armor modifier from enchantments
        /// </summary>
        public virtual int GetBodyArmorMod()
        {
            return GetModifier(EnchantmentTypeFlags.BodyArmorValue);
        }

        /// <summary>
        /// Returns either the positive body armor from life spells (ie. Armor Self)
        /// or the negative body armor (ie. Imperil)
        /// </summary>
        public virtual int GetBodyArmorMod(bool positive)
        {
            return GetModifier(EnchantmentTypeFlags.BodyArmorValue, positive);
        }

        /// <summary>
        /// Gets the resistance modifier for a damage type
        /// </summary>
        public virtual float GetResistanceMod(DamageType damageType)
        {
            var typeFlags = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;
            var resistance = GetResistanceKey(damageType);
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)resistance);

            // multiplicative
            var modifier = 1.0f;
            foreach (var enchantment in enchantments)
                modifier *= enchantment.StatModValue;

            return modifier;
        }

        /// <summary>
        /// Gets the resistance modifier for a damage type
        /// </summary>
        public virtual float GetProtectionResistanceMod(DamageType damageType)
        {
            var typeFlags = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;
            var resistance = GetResistanceKey(damageType);
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)resistance);

            // multiplicative
            var modifier = 1.0f;
            foreach (var enchantment in enchantments)
            {
                if (enchantment.StatModValue < 1.0f)
                    modifier *= enchantment.StatModValue;
            }

            return modifier;
        }

        /// <summary>
        /// Gets the resistance modifier for a damage type
        /// </summary>
        public virtual float GetVulnerabilityResistanceMod(DamageType damageType)
        {
            var typeFlags = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;
            var resistance = GetResistanceKey(damageType);
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)resistance);

            // multiplicative
            var modifier = 1.0f;
            foreach (var enchantment in enchantments)
            {
                if (enchantment.StatModValue > 1.0f)
                    modifier *= enchantment.StatModValue;
            }

            return modifier;
        }

        /// <summary>
        /// Gets the regeneration modifier for a vital type
        /// (regeneration / rejuvenation / mana renewal)
        /// </summary>
        public virtual float GetRegenerationMod(CreatureVital vital)
        {
            var typeFlags = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;
            var vitalKey = GetVitalRateKey(vital);
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)vitalKey);

            // multiplicative
            var modifier = 1.0f;
            foreach (var enchantment in enchantments)
                modifier *= enchantment.StatModValue;

            return modifier;
        }


        /// <summary>
        /// Returns the weapon damage bonus, ie. Blood Drinker
        /// </summary>
        public virtual int GetDamageBonus()
        {
            var damageMod = GetAdditiveMod(PropertyInt.Damage);
            var auraDamageMod = GetAdditiveMod(PropertyInt.WeaponAuraDamage);

            // there is an unfortunate situation in the spell db,
            // where blood drinker 1-7 are defined as PropertyInt.Damage
            // (possibly from also being cast as direct item spells elsewhere?)
            // and blood drinker 8 is properly defined as aura...

            /*if (WorldObject is Creature && auraDamageMod != 0)
                return auraDamageMod;
            else
                return damageMod;*/

            return auraDamageMod + damageMod;
        }

        /// <summary>
        /// Returns the DamageMod for bow / crossbow
        /// </summary>
        public virtual float GetDamageMod()
        {
            return GetAdditiveMod(PropertyFloat.DamageMod);
        }

        /// <summary>
        /// Returns the attack skill modifier, ie. Heart Seeker
        /// </summary>
        public virtual float GetAttackMod()
        {
            var offenseMod = GetAdditiveMod(PropertyFloat.WeaponOffense);
            var auraOffenseMod = GetAdditiveMod(PropertyFloat.WeaponAuraOffense);

            /*if (WorldObject is Creature && auraOffenseMod != 0)
                return auraOffenseMod;
            else
                return offenseMod;*/

            return auraOffenseMod + offenseMod;
        }

        /// <summary>
        /// Returns the weapon speed modifier, ie. Swift Killer
        /// </summary>
        public virtual int GetWeaponSpeedMod()
        {
            var speedMod = GetAdditiveMod(PropertyInt.WeaponTime);
            var auraSpeedMod = GetAdditiveMod(PropertyInt.WeaponAuraSpeed);

            /*if (WorldObject is Creature && auraSpeedMod != 0)
                return auraSpeedMod;
            else
                return speedMod;*/

            return auraSpeedMod + speedMod;
        }

        /// <summary>
        /// Returns the defense skill modifier, ie. Defender
        /// </summary>
        public virtual float GetDefenseMod()
        {
            var defenseMod = GetAdditiveMod(PropertyFloat.WeaponDefense);
            var auraDefenseMod = GetAdditiveMod(PropertyFloat.WeaponAuraDefense);

            /*if (WorldObject is Creature && auraDefenseMod != 0)
                return auraDefenseMod;
            else
                return defenseMod;*/

            return auraDefenseMod + defenseMod;
        }

        /// <summary>
        /// Returns the mana conversion bonus modifier, ie. Hermetic Link / Void
        /// </summary>
        public virtual float GetManaConvMod()
        {
            var manaConvMod = GetMultiplicativeMod(PropertyFloat.ManaConversionMod);
            var manaConvAuraMod = GetMultiplicativeMod(PropertyFloat.WeaponAuraManaConv);

            /*if (WorldObject is Creature && manaConvAuraMod != 1.0f)
                return manaConvAuraMod;
            else
                return manaConvMod;*/

            return manaConvAuraMod * manaConvMod;
        }

        /// <summary>
        /// Returns the elemental damage bonus modifier, ie. Spirit Drinker / Loather
        /// </summary>
        public virtual float GetElementalDamageMod()
        {
            var elementalDamageMod = GetAdditiveMod(PropertyFloat.ElementalDamageMod);
            var elementalDamageAuraMod = GetAdditiveMod(PropertyFloat.WeaponAuraElemental);

            /*if (WorldObject is Creature && elementalDamageAuraMod != 0)
                return elementalDamageAuraMod;
            else
                return elementalDamageMod;*/

            return elementalDamageAuraMod + elementalDamageMod;
        }

        /// <summary>
        /// Returns the weapon damage variance modifier
        /// </summary>
        public virtual float GetVarianceMod()
        {
            return GetMultiplicativeMod(PropertyFloat.DamageVariance);
        }

        /// <summary>
        /// Returns the additive armor level modifier, ie. Impenetrability
        /// </summary>
        public virtual int GetArmorMod()
        {
            return GetAdditiveMod(PropertyInt.ArmorLevel);
        }

        /// <summary>
        /// Gets the additive armor level vs type modifier, ie. banes
        /// </summary>
        public virtual float GetArmorModVsType(DamageType damageType)
        {
            var typeFlags = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;
            var key = GetImpenBaneKey(damageType);
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)key);

            // additive
            var modifier = 0.0f;
            foreach (var enchantment in enchantments)
                modifier += enchantment.StatModValue;

            return modifier;
        }

        /// <summary>
        /// Returns the defense skill debuffs for Dirty Fighting
        /// </summary>
        public int GetDefenseDebuffMod()
        {
            var typeFlags = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.DefenseSkills;
            var enchantments = GetEnchantments_TopLayer(typeFlags, 0);

            // additive
            return (int)Math.Round(GetAdditiveMod(enchantments));
        }

        /// <summary>
        /// Returns the attack skill debuffs for Dirty Fighting
        /// </summary>
        public int GetAttackDebuffMod()
        {
            var typeFlags = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills;
            var enchantments = GetEnchantments_TopLayer(typeFlags, 0);

            // additive
            return (int)Math.Round(GetAdditiveMod(enchantments));
        }

        /// <summary>
        /// Returns the ResistLockpick enchantment additives, ie. Strengthen/Weaken Lock
        /// </summary>
        /// <returns></returns>
        public virtual int GetResistLockpick()
        {
            return GetAdditiveMod(PropertyInt.ResistLockpick);
        }


        /// <summary>
        /// Returns a rating enchantment modifier
        /// </summary>
        /// <param name="property">The rating to return an enchantment modifier</param>
        public virtual int GetRating(PropertyInt property)
        {
            var typeFlags = EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)property);

            return (int)Math.Round(GetAdditiveMod(enchantments));
        }

        public virtual int GetNetherDotDamageRating()
        {
            var type = EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;
            var netherDots = GetEnchantments_TopLayer(type, (uint)PropertyInt.NetherOverTime);

            // this function produces a similar value to the original ACE function,
            // but is using the actual retail calculation method
            var totalBaseDamage = 0.0f;
            foreach (var netherDot in netherDots)
            {
                // normally we could just use netherDot.StatModValue here,
                // but in case WorldObject has a non-default HeartbeatInterval,
                // we want this value to still be based on the damage per default heartbeat interval
                totalBaseDamage += GetDamagePerTick(netherDot, 5.0);
            }
            var rating = (int)Math.Round(totalBaseDamage / 8.0f);   // thanks to Xenocide for this formula!
            //Console.WriteLine($"{WorldObject.Name}.NetherDotDamageRating: {rating}");
            return rating;
        }

        /// <summary>
        ///  Returns the damage over time (DoT) enchantment mod
        /// </summary>
        public float GetDamageOverTimeMod()
        {
            var typeFlags = EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;
            var enchantments = GetEnchantments_TopLayer(typeFlags, (uint)PropertyInt.DamageOverTime);

            // additive float
            return GetAdditiveMod(enchantments);
        }

        public static ushort SpellCategory_Cooldown = 0x8000;

        /// <summary>
        /// Base of a fork-reserved band of SYNTHETIC spell categories for class-ability enchantments.
        ///
        /// Real <see cref="SpellCategory"/> values top out at 733 (GauntletCriticalDamageReductionRatingRaising,
        /// the last member of ACE.Entity.Enum.SpellCategory) and 0x8000 is already taken by
        /// <see cref="SpellCategory_Cooldown"/>, so 0x4000 + n sits clear of both. A class ability that needs
        /// its own category takes 0x4000 + n, one n per ability, never reused.
        ///
        /// The POINT of a private category, and the only reason this band exists: effective values are summed
        /// over the top layer of EACH spell category (see PropertiesEnchantmentRegistryExtensions.
        /// GetEnchantmentsTopLayerByStatModType, which groups by SpellCategory and takes one winner per group,
        /// and GetAttackDebuffMod above, which then adds those winners up). An entry in a category of its own
        /// therefore stacks ADDITIVELY with same-StatModType entries from retail spells, instead of duelling
        /// them for a single slot the way two entries in one category do.
        /// </summary>
        public const ushort SpellCategory_ClassAbility_Base = 0x4000;

        /// <summary>
        /// Slot 1 of the band: Pocket Sand (Rogue T3), whose -30 attack-skill debuff must SUM with retail
        /// Dirty Fighting's rather than duel it for one category slot. One named constant per ability, never
        /// re-used - a second ability quietly sharing this number would silently make the two overwrite each
        /// other, which is precisely the failure the band exists to avoid.
        /// </summary>
        public const ushort SpellCategory_ClassAbility_PocketSand = SpellCategory_ClassAbility_Base + 1;

        /// <summary>
        /// Slot 2 of the band: Hunter's Mark (Archer T1), a "more damage taken" debuff that must MULTIPLY with
        /// every Vulnerability, Sundermark, Elemental Rend and rending-weapon term instead of duelling them for
        /// one per-element Vulnerability slot (owner ruling 2026-09-14: "a stackable separate debuff").
        ///
        /// Read differently from Pocket Sand's slot: no StatModType aggregator ever sees these entries (they
        /// carry HuntersMarkAbility.MarkStatModType, a type no GetEnchantments_TopLayer reader selects), and
        /// the ONLY reader is Creature.GetHuntersMarkMod, which combines EVERY entry in this category - one per
        /// archer, across all layers - rather than taking a top layer. One named constant per ability, never
        /// re-used and never written as a literal.
        /// </summary>
        public const ushort SpellCategory_ClassAbility_HuntersMark = SpellCategory_ClassAbility_Base + 2;

        /// <summary>
        /// Adds 0x8000 to the sharedCooldownID
        /// </summary>
        public uint GetCooldownSpellID(int sharedCooldownID)
        {
            return (uint)(SpellCategory_Cooldown | sharedCooldownID);
        }

        /// <summary>
        /// Returns the seconds until this item's cooldown expires
        /// </summary>
        public float GetCooldown(int sharedCooldownID)
        {
            var cooldownSpellID = GetCooldownSpellID(sharedCooldownID);

            var cooldown = GetEnchantment(cooldownSpellID);

            if (cooldown != null)
                return (float)(cooldown.Duration - Math.Abs(cooldown.StartTime));
            else
                return 0.0f;
        }

        /// <summary>
        /// Returns TRUE if this item can be activated at this time
        /// </summary>
        public bool CheckCooldown(int? sharedCooldownID)
        {
            if (sharedCooldownID == null)
                return true;

            return GetCooldown(sharedCooldownID.Value) == 0.0f;
        }

        /// <summary>
        /// Called every ~5 seconds for active object
        /// </summary>
        public void HeartBeat(double heartbeatInterval)
        {
            var topLayerEnchantments = WorldObject.Biota.PropertiesEnchantmentRegistry.GetEnchantmentsTopLayer(WorldObject.BiotaDatabaseLock, SpellSet.SetSpells);

            HeartBeat_DamageOverTime(topLayerEnchantments);

            var expired = WorldObject.Biota.PropertiesEnchantmentRegistry.HeartBeatEnchantmentsAndReturnExpired(heartbeatInterval, WorldObject.BiotaDatabaseLock);

            foreach (var enchantment in expired)
                Remove(enchantment);
        }

        /// <summary>
        /// Applies damage from DoTs every ~5 seconds
        /// </summary>
        /// <param name="enchantments">A list of active enchantments at the top layers</param>
        public void HeartBeat_DamageOverTime(List<PropertiesEnchantmentRegistry> enchantments)
        {
            var dots = new List<PropertiesEnchantmentRegistry>();
            var netherDots = new List<PropertiesEnchantmentRegistry>();
            var aetheriaDots = new List<PropertiesEnchantmentRegistry>();
            var heals = new List<PropertiesEnchantmentRegistry>();

            foreach (var enchantment in enchantments)
            {
                // combine DoTs from multiple sources
                if (enchantment.StatModKey == (int)PropertyInt.DamageOverTime)
                {
                    if (enchantment.SpellCategory == SpellCategory.AetheriaProcDamageOverTimeRaising)
                        aetheriaDots.Add(enchantment);
                    else
                        dots.Add(enchantment);
                }
                else if (enchantment.StatModKey == (int)PropertyInt.NetherOverTime)
                    netherDots.Add(enchantment);

                else if (enchantment.StatModKey == (int)PropertyInt.HealOverTime)
                    heals.Add(enchantment);
            }

            // apply damage over time (DoTs)
            if (dots.Count > 0)
                ApplyDamageTick(dots, DamageType.Undef);

            if (netherDots.Count > 0)
                ApplyDamageTick(netherDots, DamageType.Nether);

            if (aetheriaDots.Count > 0)
                ApplyDamageTick(aetheriaDots, DamageType.Undef, true);

            // apply healing over time (HoTs)
            if (heals.Count > 0)
                ApplyHealingTick(heals);
        }

        public void ApplyHealingTick(List<PropertiesEnchantmentRegistry> enchantments)
        {
            var creature = WorldObject as Creature;
            if (creature == null || creature.IsDead) return;

            // get the total tick amount
            var tickAmountTotal = 0.0f;
            foreach (var enchantment in enchantments)
            {
                //var totalAmount = enchantment.StatModValue;
                //var totalTicks = GetNumTicks(enchantment);
                var tickAmount = enchantment.StatModValue;

                tickAmountTotal += tickAmount;
            }

            // apply healing ratings?
            tickAmountTotal *= creature.GetHealingRatingMod();

            // PvP rules choke point HL4 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_healing_mod on a POSITIVE
            // heal-over-time tick (Aetheria heal procs included; a negative tick such as Spectral Fountain Sip is
            // untouched) received by a player bound to a Live arena match (arena only). Floored when scaled, so the Math.Round below never rounds
            // a scaled tick up.
            if (tickAmountTotal > 0 && creature is Player tickHealedPlayer)
                tickAmountTotal = PvpRules.ApplyHealingMod(PvpChokePoint.HL4, tickHealedPlayer, tickAmountTotal);

            // do healing
            var healAmount = creature.UpdateVitalDelta(creature.Health, (int)Math.Round(tickAmountTotal));

            // account for negative HealOverTime spells, such as 5172 - Spectral Fountain Sip
            if (healAmount >= 0)
                creature.DamageHistory.OnHeal((uint)healAmount);
            else
                creature.DamageHistory.Add(creature, DamageType.Health, (uint)-healAmount);

            if (creature is Player player)
                player.SendMessage($"You receive {Math.Abs(healAmount)} points of periodic {((healAmount >= 0) ? "healing" : "harm")}.", PropertyManager.GetBool("aetheria_heal_color").Item ? ChatMessageType.Broadcast : ChatMessageType.Combat);

            if (creature.IsDead)
            {
                creature.OnDeath(creature.DamageHistory.LastDamager, DamageType.Health, false);
                creature.Die();
            }
        }

        /// <summary>
        /// Pure gate for Withering's nether DoT tick scaling, used by the tick loop in
        /// <see cref="ApplyDamageTick"/>. PvE only: true only for a nether tick from a Player source
        /// whose target is not a Player, matching Hemomancy's own PvP carve-out at the same site
        /// (<paramref name="isPlayerPairIncludingSelf"/> is true for a self-cast tick too, via
        /// <see cref="PvpClassifier.IsPlayerPairIncludingSelf"/>, so a self-cast tick is excluded as well).
        /// </summary>
        internal static bool ShouldApplyWitheringVoidDotMod(DamageType damageType, bool sourceIsPlayer, bool isPlayerPairIncludingSelf)
            => damageType == DamageType.Nether && sourceIsPlayer && !isPlayerPairIncludingSelf;

        /// <summary>
        /// Applies 1 tick of damage from a DoT spell
        /// </summary>
        /// <param name="enchantments">The damage over time (DoT) spells</param>
        public void ApplyDamageTick(List<PropertiesEnchantmentRegistry> enchantments, DamageType damageType, bool aetheria = false)
        {
            var creature = WorldObject as Creature;
            if (creature == null || creature.IsDead) return;

            // every contribution in enchantment-list order, grouped per damager below - the ORDER of that
            // grouping decides who is recorded as landing the killing blow, see the block after the loop
            var contributions = new List<KeyValuePair<WorldObject, float>>();

            var targetPlayer = WorldObject as Player;

            // Accumulate the RAW, UNCAPPED tick. This loop used to clamp its running total to the victim's
            // current Health as it went, and latch an `isDead` flag to break out once it had. That clamp was
            // a second, upstream instance of the 2026-09-08 Mana Barrier overkill bug, and it survived the
            // fix at the five damage sites because it sits above all of them: a defence handed a
            // health-clamped figure can ALWAYS leave the victim alive, since any nonzero absorb applied to a
            // number that is at most current Health leaves Health strictly positive. A player at 300 Health
            // hit by a stacked DoT nominally worth 5000 had the tick clamped to 300, absorbed 90 of it, took
            // 210 and lived at 90 - the same "no tick of any size can kill a barrier carrier" property the
            // barrier fix was supposed to have removed. Sanguine Ward had the identical exposure at the
            // identical line, which is why both are handled together below.
            //
            // The removed `isDead` latch only ever broke this loop. The real death gate is the
            // `if (!creature.IsAlive) return;` after the TakeDamageOverTime call further down, which is
            // unchanged, as is every damage, resistance and rating term computed inside the loop.
            foreach (var enchantment in enchantments)
            {
                //var totalAmount = enchantment.StatModValue;
                //var totalTicks = GetNumTicks(enchantment);
                var tickAmount = enchantment.StatModValue;

                // run tick amount through damage calculation functions?
                // it appears retail might have done an initial damage calc,
                // and then applied that to the enchantment StatModVal beforehand
                // for each damage tick, this pre-calc would then be multiplied
                // against the realtime resistances

                var damager = WorldObject.CurrentLandblock?.GetObject(enchantment.CasterObjectId);
                if (damager == null)
                {
                    //Console.WriteLine($"{WorldObject.Name}.ApplyDamageTick() - couldn't find damager {enchantment.CasterObjectId:X8}");
                    continue;
                }

                var resistanceMod = creature.GetResistanceMod(damageType, damager, null);

                var sourcePlayer = damager as Player;

                // Doctide-ported arena spell rules (Docs/Pvp/DESIGN.md "Arena spell rules"): inside an arena
                // match, a DoT tick from another match player deals no damage unless the match is Live, and
                // none at all in FFA even when Live. Checked ahead of every other term below, so a suppressed
                // tick contributes nothing at all - not even a zeroed, still-recorded contribution.
                //
                // The bindings are read once here rather than through PvpTunables.DialSource() per tick: both
                // suppression flags are baked into the binding at match creation (PvpMatchCoordinator.Binding),
                // the same shape as the five suppression masks. The cheap binding-null check runs first, before
                // the RestrictSpells flag read, so a non-arena tick (the overwhelmingly common case) never goes
                // near PvpArenaSpellRules at all.
                var targetPvpBinding = targetPlayer?.PvpBinding;
                var damagerPvpBinding = sourcePlayer?.PvpBinding;

                // A respawning side (battlegrounds) is suppressed whatever RestrictSpells says: the pen is out of the fight.
                // Spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): a tick from another player inside the target's window
                // is suppressed too; the clock is read only when the target actually carries a window.
                // The window counts only while it is still running: a lapsed one leaves the check exactly as it was before spawn protection.
                var spawnProtectedNow = targetPvpBinding != null && targetPvpBinding.HasSpawnProtection ? PvpArenaHookSettings.UtcNow() : (DateTime?)null;
                // The position is read only for a target that carries protection, never on an ordinary tick.
                var targetAt = spawnProtectedNow.HasValue ? targetPlayer?.Location : null;
                var targetSpawnProtected = spawnProtectedNow.HasValue && targetPvpBinding.IsSpawnProtected(spawnProtectedNow.Value, targetAt);

                if (targetPvpBinding != null && damagerPvpBinding != null
                    && (targetPvpBinding.RestrictSpells || targetPvpBinding.Respawning || damagerPvpBinding.Respawning || targetSpawnProtected)
                    && PvpArenaSpellRules.ShouldSuppressDotTick(targetPvpBinding, damagerPvpBinding, targetSpawnProtected ? spawnProtectedNow : null, targetAt))
                {
                    continue;
                }

                // Marketplace combat-free zone (pvp_safe_landblocks, Docs/Runbook.md): a player-sourced DoT
                // applied before the victim walked into a safe landblock must not keep damaging them once they
                // are standing in one. Cheap check first - both sides must be players - before touching
                // PropertyManager at all, since the overwhelming majority of ticks are not player-vs-player.
                if (targetPlayer != null && sourcePlayer != null)
                {
                    var safeZoneTargetLandblock = targetPlayer.Location?.LandblockShort;
                    var safeZoneDamagerLandblock = sourcePlayer.Location?.LandblockShort;

                    if (safeZoneTargetLandblock != null && safeZoneDamagerLandblock != null
                        && PvpSafeZoneRules.IsPvpSafe(
                               (ushort)safeZoneDamagerLandblock.Value, sourcePlayer.Location.RealmID, sourcePlayer.Location.IsEphemeralRealm,
                               (ushort)safeZoneTargetLandblock.Value, targetPlayer.Location.RealmID, targetPlayer.Location.IsEphemeralRealm,
                               PvpSafeZoneTunables.DialSource().SafeLandblocks))
                    {
                        continue;
                    }
                }

                // class ability: Withering scales the caster's void (nether) DoT tick damage. PvE only:
                // the tick's TARGET must not be a Player (a self-cast tick counts as a Player target
                // too), via PvpClassifier.IsPlayerPairIncludingSelf.
                if (ShouldApplyWitheringVoidDotMod(damageType, sourcePlayer != null, PvpClassifier.IsPlayerPairIncludingSelf(damager, WorldObject)))
                    tickAmount *= sourcePlayer.GetWitheringVoidDotMod();

                // class ability: Hemomancy scales the caster's DoT tick damage, EVERY school - unlike
                // Withering above, not gated by damageType. PvE only: the tick's TARGET must not be a
                // Player, matching Withering's own PvP carve-out at this site.
                if (sourcePlayer != null && targetPlayer == null)
                    tickAmount *= sourcePlayer.GetHemomancyDotMod();

                if (PvpClassifier.IsPlayerPairIncludingSelf(damager, WorldObject))
                {
                    // if a PKType with Enduring Enchantment has died, ensure they don't continue to take DoT from PK sources
                    if (!targetPlayer.IsPKType)
                        continue;

                    // void spell projectile direct damage was modified to apply this pvp modifier *on top of* the player's natural resistance to nether,
                    // which supposedly brings the direct damage from void spells in pvp closer to retail

                    // however, dots were already supposedly on par, so we replace resistanceMod with void_pvp_modifier for dots,
                    // instead of applying it on top like direct damage

                    if (damageType == DamageType.Nether)
                        resistanceMod = (float)PropertyManager.GetDouble("void_pvp_modifier").Item;
                }

                // with the halvening, this actually seems like the fairest balance currently..
                var useNetherDotDamageRating = targetPlayer != null;

                var damageResistRatingMod = creature.GetDamageResistRatingMod(CombatType.Magic, useNetherDotDamageRating, attacker: damager);   // df?

                if (PvpClassifier.IsPlayerPairIncludingSelf(damager, WorldObject))
                {
                    var pkDamageResistRatingMod = Creature.GetNegativeRatingMod(targetPlayer.GetPKDamageResistRating());

                    damageResistRatingMod = Creature.AdditiveCombine(damageResistRatingMod, pkDamageResistRatingMod);
                }

                var dotResistRatingMod = Creature.GetNegativeRatingMod(creature.GetDotResistanceRating());  // should this be here, or somewhere else?
                                                                                                            // should this affect NetherDotDamageRating?

                //Console.WriteLine("DR: " + Creature.ModToRating(damageRatingMod));
                //Console.WriteLine("DRR: " + Creature.NegativeModToRating(damageResistRatingMod));
                //Console.WriteLine("NRR: " + Creature.NegativeModToRating(netherResistRatingMod));

                tickAmount *= resistanceMod * damageResistRatingMod * dotResistRatingMod;

                contributions.Add(new KeyValuePair<WorldObject, float>(damager, tickAmount));
            }

            // Group the contributions per damager, in LAST-CONTRIBUTION order, and freeze that order for
            // everything below - the total, the split, the DamageHistory writes and the per-damager combat
            // lines all walk this one list.
            //
            // THE ORDER IS DELIBERATE AND LOAD-BEARING. DamageHistory.Add is replayed over this list, and
            // DamageHistory.LastDamager resolves to the attacker on the LAST negative-amount log entry, so
            // this order decides who is recorded as landing the killing blow on a finishing tick. The
            // original code called Add once per enchantment as it walked the list, which is
            // last-contribution order by construction. Regrouping without preserving it silently reassigns
            // the kill: a plain Dictionary enumerates in FIRST-insertion order, because updating a value
            // does not move its key, so three top-layer DoTs in list order [A, B, A] - one caster holding
            // two spell categories on the target - would replay as [A, B] and hand the kill to B where the
            // original hands it to A. Consumers that would break: Player_KillFillVessel and
            // Player_MuleFormToken (both credit ONLY the killing blow, deliberately, over a fellow who did
            // more damage), the death and kill messages in Monster_Combat / Player_Combat, and the
            // TargetingTactic.LastDamager aggro tactic.
            //
            // A List rather than a Dictionary for the second reason too: both passes below are guaranteed
            // to see the same order, which is what makes DotTickCredits' running-cumulative split exact.
            var entries = DotTickCredits.GroupByLastContribution(contributions);
            var rawAmounts = entries.Select(entry => entry.Value).ToList();

            var rawTotal = DotTickCredits.Total(rawAmounts);

            var damageTotal = rawTotal;

            // THE VICTIM'S PRE-WRITE ABSORBERS, HOISTED HERE out of Player.TakeDamageOverTime so each runs
            // exactly once against the whole uncapped tick rather than against a figure already clamped to
            // the victim's health. This is the only caller of TakeDamageOverTime, so nothing loses coverage
            // by the move. The order the mitigations run in belongs to the hook (IPreWriteDamageAbility's
            // MitigationOrder), not to this site, so it cannot drift between the five damage sites.
            //
            // isDamageOverTime: TRUE only here. A tick carries no source whatsoever, which is NOT the same
            // as an excluded attacker - Mana Barrier absorbs an unattributed tick but never a PvP hit, and
            // it needs to tell those two apart.
            //
            // Skipped for the two states in which TakeDamageOverTime discards the tick outright (Invincible,
            // and lifestone protection, which diverts to HandleLifestoneProtection instead), so neither
            // absorber is charged for damage that was never going to land. IsDead is already excluded by the
            // guard at the top of this method.
            if (targetPlayer != null && damageTotal > 0.0f && !targetPlayer.Invincible && !targetPlayer.UnderLifestoneProtection)
            {
                var beforeAbsorb = (uint)Math.Round(damageTotal);

                damageTotal = targetPlayer.ApplyPreWriteDamageClassAbilities(null, damageType, beforeAbsorb, isDamageOverTime: true);
            }

            // CREDIT ACCOUNTING IS CLAMPED, THE VITAL WRITE IS NOT. UpdateVitalDelta floors Health at zero
            // by itself - which is exactly how the four non-DoT absorb sites already work - so the write
            // needs no clamp and an overkill tick still kills. The credits do need one: damage history and
            // the per-damager combat lines must never report damage that was never dealt, or an overkill
            // tick would hand out kill credit sized off the nominal roll.
            var appliedTotal = Math.Max(0.0f, Math.Min(damageTotal, creature.Health.Current));
            var appliedRounded = (uint)Math.Round(appliedTotal);

            // Proportional split by running cumulative total - see DotTickCredits for why that shape and
            // not a per-damager rounding. The credits sum to appliedRounded exactly and none exceeds it.
            var credits = DotTickCredits.Split(rawAmounts, appliedRounded);

            // Before the tick is applied, as it was before: TakeDamageOverTime's death path resolves the
            // killer through DamageHistory.LastDamager.
            for (var i = 0; i < entries.Count; i++)
                creature.DamageHistory.Add(entries[i].Key, damageType, credits[i]);

            creature.TakeDamageOverTime(damageTotal, damageType);

            if (!creature.IsAlive) return;

            for (var i = 0; i < entries.Count; i++)
            {
                var damager = entries[i].Key;
                float amount = credits[i];

                if (creature.Invincible)
                    amount = 0;

                var damageSourcePlayer = damager as Player;
                if (damageSourcePlayer != null)
                {
                    creature.TakeDamageOverTime_NotifySource(damageSourcePlayer, damageType, amount, aetheria);

                    if (creature.IsAlive)
                        creature.EmoteManager.OnDamage(damageSourcePlayer);

                    // class ability: Hemomancy heals the caster for a flat fraction of THIS tick's own
                    // APPLIED damage (post overkill-clamp - the same `amount` DamageHistory and the notify
                    // call above just used), never the raw uncapped roll. PvE only: targetPlayer == null
                    // was already required to earn the tick bonus above, and this reuses that same
                    // constraint rather than re-deriving it. Rank-gated (0.0 at rank 0) and NOT
                    // affinity-scaled - see HemomancyAbility's doc comment. Skipped on a killing tick along
                    // with everything else in this loop, since the method returns before reaching it once
                    // the target dies.
                    if (targetPlayer == null && amount > 0)
                    {
                        var healFraction = damageSourcePlayer.GetHemomancyHealFraction();

                        if (healFraction > 0.0)
                        {
                            var healAmount = (int)Math.Round(amount * healFraction);

                            if (healAmount > 0)
                                damageSourcePlayer.UpdateVitalDelta(damageSourcePlayer.Health, healAmount);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Writes the EnchantmentRegistry to the network stream
        /// </summary>
        public void SendRegistry(BinaryWriter writer)
        {
            if (Player == null) return;
            var enchantmentRegistry = new EnchantmentRegistry(Player);
            writer.Write(enchantmentRegistry);
        }

        /// <summary>
        /// Writes UpdateEnchantment vitae to the network stream
        /// </summary>
        public void SendUpdateVitae()
        {
            if (Player == null) return;
            var vitae = new Enchantment(Player, GetVitae());
            Player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(Player.Session, vitae));
        }

        /// <summary>
        /// Returns the number of ticks for a DoT enchantment
        /// </summary>
        public int GetNumTicks(PropertiesEnchantmentRegistry enchantment, double? heartbeatInterval = null)
        {
            // assumed to be DoT enchantment

            if (heartbeatInterval == null)
                heartbeatInterval = WorldObject.HeartbeatInterval ?? 5.0;

            // it's possible retail had a separate ticking mechanism for these,
            // that ensured ticks every 5s, instead of heartbeat intervals
            return (int)Math.Ceiling(enchantment.Duration / heartbeatInterval.Value);
        }

        /// <summary>
        /// Returns the total damage for a DoT enchantment
        /// </summary>
        public float GetTotalDamage(PropertiesEnchantmentRegistry enchantment)
        {
            // assumed to be DoT enchantment
            return enchantment.StatModValue * GetNumTicks(enchantment);
        }

        public float GetDamagePerTick(PropertiesEnchantmentRegistry enchantment, double? heartbeatInterval = null)
        {
            // assumed to be DoT enchantment

            var creatureHeartbeatInterval = WorldObject.HeartbeatInterval ?? 5.0;

            if (heartbeatInterval == null)
                heartbeatInterval = creatureHeartbeatInterval;

            if (heartbeatInterval == creatureHeartbeatInterval)
                return enchantment.StatModValue;

            // calculate the total damage w/ creature heartbeat interval
            var totalDamage = GetTotalDamage(enchantment);

            // divide totalDamage by the requested tick interval
            var numTicks = GetNumTicks(enchantment, heartbeatInterval);

            return totalDamage / numTicks;
        }
    }
}
