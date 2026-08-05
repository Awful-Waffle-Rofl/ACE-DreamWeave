using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.Structure
{
    /// <summary>
    /// Handles calculating and sending all object appraisal info
    /// </summary>
    public class AppraiseInfo
    {
        private const uint EnchantmentMask = 0x80000000;

        public IdentifyResponseFlags Flags;

        public bool Success;    // assessment successful?

        public Dictionary<PropertyInt, int> PropertiesInt;
        public Dictionary<PropertyInt64, long> PropertiesInt64;
        public Dictionary<PropertyBool, bool> PropertiesBool;
        public Dictionary<PropertyFloat, double> PropertiesFloat;
        public Dictionary<PropertyString, string> PropertiesString;
        public Dictionary<PropertyDataId, uint> PropertiesDID;
        public Dictionary<PropertyInstanceId, uint> PropertiesIID;

        public List<uint> SpellBook;

        public ArmorProfile ArmorProfile;
        public CreatureProfile CreatureProfile;
        public WeaponProfile WeaponProfile;
        public HookProfile HookProfile;

        public ArmorMask ArmorHighlight;
        public ArmorMask ArmorColor;
        public WeaponMask WeaponHighlight;
        public WeaponMask WeaponColor;
        public ResistMask ResistHighlight;
        public ResistMask ResistColor;

        public ArmorLevel ArmorLevels;

        // This helps ensure the item will identify properly. Some "items" are technically "Creatures".
        private bool NPCLooksLikeObject;

        public AppraiseInfo()
        {
            Flags = IdentifyResponseFlags.None;
            Success = false;
        }

        /// <summary>
        /// Construct all of the info required for appraising any WorldObject
        /// </summary>
        public AppraiseInfo(WorldObject wo, Player examiner, bool success = true)
        {
            BuildProfile(wo, examiner, success);
        }

        public void BuildProfile(WorldObject wo, Player examiner, bool success = true)
        {
            //Console.WriteLine("Appraise: " + wo.Guid);
            Success = success;

            BuildProperties(wo);
            BuildSpells(wo);

            // Help us make sure the item identify properly
            NPCLooksLikeObject = wo.GetProperty(PropertyBool.NpcLooksLikeObject) ?? false;

            if (PropertiesIID.ContainsKey(PropertyInstanceId.AllowedWielder) && !PropertiesBool.ContainsKey(PropertyBool.AppraisalHasAllowedWielder))
                PropertiesBool.Add(PropertyBool.AppraisalHasAllowedWielder, true);

            if (PropertiesIID.ContainsKey(PropertyInstanceId.AllowedActivator) && !PropertiesBool.ContainsKey(PropertyBool.AppraisalHasAllowedActivator))
                PropertiesBool.Add(PropertyBool.AppraisalHasAllowedActivator, true);

            if (PropertiesString.ContainsKey(PropertyString.ScribeAccount) && !examiner.IsAdmin && !examiner.IsSentinel && !examiner.IsEnvoy && !examiner.IsArch && !examiner.IsPsr)
                PropertiesString.Remove(PropertyString.ScribeAccount);

            if (PropertiesString.ContainsKey(PropertyString.HouseOwnerAccount) && !examiner.IsAdmin && !examiner.IsSentinel && !examiner.IsEnvoy && !examiner.IsArch && !examiner.IsPsr)
                PropertiesString.Remove(PropertyString.HouseOwnerAccount);

            if (PropertiesInt.ContainsKey(PropertyInt.Lifespan))
                PropertiesInt[PropertyInt.RemainingLifespan] = wo.GetRemainingLifespan();

            if (PropertiesInt.TryGetValue(PropertyInt.Faction1Bits, out var faction1Bits))
            {
                // hide any non-default factions, prevent client from displaying ???
                // this is only needed for non-standard faction creatures that use templates, to hide the ??? in the client
                var sendBits = faction1Bits & (int)FactionBits.ValidFactions;
                if (sendBits != faction1Bits)
                {
                    if (sendBits != 0)
                        PropertiesInt[PropertyInt.Faction1Bits] = sendBits;
                    else
                        PropertiesInt.Remove(PropertyInt.Faction1Bits);
                }
            }

            // armor / clothing / shield
            if (wo is Clothing || wo.IsShield)
                BuildArmor(wo);

            if (wo is Creature creature)
                BuildCreature(creature);

            if (wo.Damage != null && !(wo is Clothing) || wo is MeleeWeapon || wo is Missile || wo is MissileLauncher || wo is Ammunition || wo is Caster)
                BuildWeapon(wo);

            // TODO: Resolve this issue a better way?
            // Because of the way ACE handles default base values in recipe system (or rather the lack thereof)
            // we need to check the following weapon properties to see if they're below expected minimum and adjust accordingly
            // The issue is that the recipe system likely added 0.005 to 0 instead of 1, which is what *should* have happened.
            if (wo.WeaponMagicDefense.HasValue && wo.WeaponMagicDefense.Value > 0 && wo.WeaponMagicDefense.Value < 1 && ((wo.GetProperty(PropertyInt.ImbueStackingBits) ?? 0) & 1) != 0)
                PropertiesFloat[PropertyFloat.WeaponMagicDefense] += 1;
            if (wo.WeaponMissileDefense.HasValue && wo.WeaponMissileDefense.Value > 0 && wo.WeaponMissileDefense.Value < 1 && ((wo.GetProperty(PropertyInt.ImbueStackingBits) ?? 0) & 1) != 0)
                PropertiesFloat[PropertyFloat.WeaponMissileDefense] += 1;

            // Mask real value of AbsorbMagicDamage and/or Add AbsorbMagicDamage for ImbuedEffectType.IgnoreSomeMagicProjectileDamage
            if (PropertiesFloat.ContainsKey(PropertyFloat.AbsorbMagicDamage) || wo.HasImbuedEffect(ImbuedEffectType.IgnoreSomeMagicProjectileDamage))
                PropertiesFloat[PropertyFloat.AbsorbMagicDamage] = 1;

            if (wo is Door || wo is Chest)
            {
                // If wo is not locked, do not send ResistLockpick value. If ResistLockpick is sent for unlocked objects, id panel shows bonus to Lockpick skill
                if (!wo.IsLocked && PropertiesInt.ContainsKey(PropertyInt.ResistLockpick))
                    PropertiesInt.Remove(PropertyInt.ResistLockpick);

                // If wo is locked, append skill check percent, as int, to properties for id panel display on chances of success
                if (wo.IsLocked)
                {
                    var resistLockpick = LockHelper.GetResistLockpick(wo);

                    if (resistLockpick != null)
                    {
                        PropertiesInt[PropertyInt.ResistLockpick] = (int)resistLockpick;

                        var pickSkill = examiner.Skills[Skill.Lockpick].Current;

                        var successChance = SkillCheck.GetSkillChance((int)pickSkill, (int)resistLockpick) * 100;

                        if (!PropertiesInt.ContainsKey(PropertyInt.AppraisalLockpickSuccessPercent))
                            PropertiesInt.Add(PropertyInt.AppraisalLockpickSuccessPercent, (int)successChance);
                    }
                }
                // if wo has DefaultLocked property and is unlocked, add that state to the property buckets
                else if (PropertiesBool.ContainsKey(PropertyBool.DefaultLocked))
                    PropertiesBool[PropertyBool.Locked] = false;
            }

            if (wo is Corpse)
            {
                PropertiesBool.Clear();
                PropertiesDID.Clear();
                PropertiesFloat.Clear();
                PropertiesInt64.Clear();

                var discardInts = PropertiesInt.Where(x => x.Key != PropertyInt.EncumbranceVal && x.Key != PropertyInt.Value).Select(x => x.Key).ToList();
                foreach (var key in discardInts)
                    PropertiesInt.Remove(key);
                var discardString = PropertiesString.Where(x => x.Key != PropertyString.LongDesc).Select(x => x.Key).ToList();
                foreach (var key in discardString)
                    PropertiesString.Remove(key);

                PropertiesInt[PropertyInt.Value] = 0;
            }

            if (wo is Portal)
            {
                if (PropertiesInt.ContainsKey(PropertyInt.EncumbranceVal))
                    PropertiesInt.Remove(PropertyInt.EncumbranceVal);
            }

            if (wo is SlumLord slumLord)
            {                
                PropertiesBool.Clear();
                PropertiesDID.Clear();
                PropertiesFloat.Clear();
                PropertiesIID.Clear();
                //PropertiesInt.Clear();
                PropertiesInt64.Clear();
                PropertiesString.Clear();

                var longDesc = "";

                if (slumLord.HouseOwner.HasValue && slumLord.HouseOwner.Value > 0)
                {
                    longDesc = $"The current maintenance has {(slumLord.IsRentPaid() || !PropertyManager.GetBool("house_rent_enabled").Item ? "" : "not ")}been paid.\n";

                    PropertiesInt.Clear();
                }
                else
                {
                    //longDesc = $"This house is {(slumLord.HouseStatus == HouseStatus.Disabled ? "not " : "")}available for purchase.\n"; // this was the retail msg.
                    longDesc = $"This {(slumLord.House.HouseType == HouseType.Undef ? "house" : slumLord.House.HouseType.ToString().ToLower())} is {(slumLord.House.HouseStatus == HouseStatus.Disabled ? "not " : "")}available for purchase.\n";

                    var discardInts = PropertiesInt.Where(x => x.Key != PropertyInt.HouseStatus && x.Key != PropertyInt.HouseType && x.Key != PropertyInt.MinLevel && x.Key != PropertyInt.MaxLevel && x.Key != PropertyInt.AllegianceMinLevel && x.Key != PropertyInt.AllegianceMaxLevel).Select(x => x.Key).ToList();
                    foreach (var key in discardInts)
                        PropertiesInt.Remove(key);
                }

                if (slumLord.HouseRequiresMonarch)
                    longDesc += "You must be a monarch to purchase and maintain this dwelling.\n";

                if (slumLord.AllegianceMinLevel.HasValue)
                {
                    var allegianceMinLevel = PropertyManager.GetLong("mansion_min_rank", -1).Item;
                    if (allegianceMinLevel == -1)
                        allegianceMinLevel = slumLord.AllegianceMinLevel.Value;

                    longDesc += $"Restricted to characters of allegiance rank {allegianceMinLevel} or greater.\n";
                }

                PropertiesString.Add(PropertyString.LongDesc, longDesc);
            }

            if (wo is Container)
            {
                if (PropertiesInt.ContainsKey(PropertyInt.Value))
                    PropertiesInt[PropertyInt.Value] = DatabaseManager.World.GetCachedWeenie(wo.WeenieClassId).GetValue() ?? 0; // Value is masked to base value of Weenie
            }

            if (wo is Storage)
            {
                var longDesc = "";

                if (wo.HouseOwner.HasValue && wo.HouseOwner.Value > 0)
                    longDesc = $"Owned by {wo.ParentLink.HouseOwnerName}\n";

                var discardString = PropertiesString.Where(x => x.Key != PropertyString.Use).Select(x => x.Key).ToList();
                foreach (var key in discardString)
                    PropertiesString.Remove(key);

                PropertiesString.Add(PropertyString.LongDesc, longDesc);
            }

            if (wo is Hook)
            {
                // If the hook has any inventory, we need to send THOSE properties instead.
                var hook = wo as Container;

                string baseDescString = "";
                if (wo.ParentLink.HouseOwner != null)
                {
                    // This is for backwards compatibility. This value was not set/saved in earlier versions.
                    // It will get the player's name and save that to the HouseOwnerName property of the house. This is now done when a player purchases a house.
                    if (wo.ParentLink.HouseOwnerName == null)
                    {
                        var houseOwnerPlayer = PlayerManager.FindByGuid((uint)wo.ParentLink.HouseOwner);
                        if (houseOwnerPlayer != null)
                        {
                            wo.ParentLink.HouseOwnerName = houseOwnerPlayer.Name;
                            wo.ParentLink.SaveBiotaToDatabase();
                        }
                    }
                    baseDescString = "This hook is owned by " + wo.ParentLink.HouseOwnerName + ". "; //if house is owned, display this text
                }

                var containsString = "";
                if (hook.Inventory.Count == 1)
                {
                    WorldObject hookedItem = hook.Inventory.First().Value;

                    // Hooked items have a custom "description", containing the desc of the sub item and who the owner of the house is (if any)
                    BuildProfile(hookedItem, examiner, success);

                    containsString = "It contains: \n";

                    if (!string.IsNullOrWhiteSpace(hookedItem.LongDesc))
                    {
                        containsString += hookedItem.LongDesc;
                    }
                    //else if (PropertiesString.ContainsKey(PropertyString.ShortDesc) && PropertiesString[PropertyString.ShortDesc] != null)
                    //{
                    //    containsString += PropertiesString[PropertyString.ShortDesc];
                    //}
                    else
                    {
                        containsString += hookedItem.Name;
                    }

                    BuildHookProfile(hookedItem);
                }

                //if (PropertiesString.ContainsKey(PropertyString.LongDesc) && PropertiesString[PropertyString.LongDesc] != null)
                //    PropertiesString[PropertyString.LongDesc] = baseDescString + containsString;
                ////else if (PropertiesString.ContainsKey(PropertyString.ShortDesc) && PropertiesString[PropertyString.ShortDesc] != null)
                ////    PropertiesString[PropertyString.LongDesc] = baseDescString + containsString;
                //else
                //    PropertiesString[PropertyString.LongDesc] = baseDescString + containsString;

                PropertiesString[PropertyString.LongDesc] = baseDescString + containsString;

                PropertiesInt.Remove(PropertyInt.Structure);

                // retail should have removed this property and then server side built the same result for the hook longdesc replacement but didn't and ends up with some odd looking appraisals as seen on video/pcaps
                //PropertiesInt.Remove(PropertyInt.AppraisalLongDescDecoration);
            }

            if (wo is ManaStone)
            {
                var useMessage = "";

                if (wo.ItemCurMana.HasValue)
                    useMessage = "Use on a magic item to give the stone's stored Mana to that item.";
                else
                    useMessage = "Use on a magic item to destroy that item and drain its Mana.";

                PropertiesString[PropertyString.Use] = useMessage;
            }

            // A multi-charge salvage tool (WaffleACE - the Hammers) is EXEMPT from this suppression, and the
            // reason is that Structure means something different on each. On a salvage bag it is a FRACTION of
            // one unit of salvage, which retail deliberately hides; on a salvage tool it is a COUNT of
            // remaining applications, which is exactly what the client's green uses-remaining bar is for (the
            // same bar a summoning device or healing kit draws). Tested with SalvageTool.IsSalvageTool rather
            // than a wcid list on purpose: a list would silently fail to cover the next tool added.
            if (wo is CraftTool && !SalvageTool.IsSalvageTool(wo) && (wo.ItemType == ItemType.TinkeringMaterial || wo.WeenieClassId >= 36619 && wo.WeenieClassId <= 36628 || wo.WeenieClassId >= 36634 && wo.WeenieClassId <= 36636))
            {
                if (PropertiesInt.ContainsKey(PropertyInt.Structure))
                    PropertiesInt.Remove(PropertyInt.Structure);
            }

            // PROTOTYPE: custom ACE-only mechanics (MultiShot today, more planned per the user's broader custom-
            // gameplay project - chain spells, thorns, summon-on-kill pets, etc.) have no client-side renderer,
            // so they're surfaced the same way a reference player-run server does it (confirmed via a live
            // screenshot of their identify panel): a "Property Details:" section with one "- Name: effect" bullet
            // per mechanic, so it reads as a continuation of the client's own auto-rendered "Properties:"/
            // imbue-lock line rather than as ordinary flavor text. Computed live from current property values on
            // every appraisal, never a stored string, so a future crafting/imbue mechanic that adjusts these
            // values stays accurate without re-authoring the item's description.
            //
            // This runs LAST, after every type-specific branch above, because the slot it writes is shared: see
            // WritePropertyDetails for which slot and why, and for the ManaStone interaction the ordering closes.
            var propertyDetails = new List<string>();

            // Life ("blood") casters go FIRST: these lines replace client lines that would otherwise sit at
            // the top of the panel, so they read best in the same position. The client cannot label
            // DamageType.Health, so its own versions are suppressed just below and re-rendered here.
            propertyDetails.AddRange(LifeCasterDisplay.GetAppraisalLines(wo));
            LifeCasterDisplay.SuppressUnlabelledClientLines(wo, PropertiesInt, PropertiesFloat);

            if (wo.IsMultiShot)
            {
                var arc = wo.MultiShotSpreadAngle * 2;
                propertyDetails.Add($"- Multi Shot: Fires {wo.MultiShotCount} additional arrow{(wo.MultiShotCount == 1 ? "" : "s")} in a {arc:0}° arc.");
            }

            // Equipment mods (WaffleACE) ride the same block. The item stores only a POTENCY scalar 0-1 on a
            // custom PropertyFloat in the reserved 8100-8199 band; those ids carry no [AssessmentProperty], so
            // the raw scalar is never sent to the client and this is the only surface a player sees. The
            // magnitude shown is resolved live through the registry (potency x max x equipment_mod_potency_scale),
            // which is exactly why retuning a mod's magnitude - or the global scale - instantly retunes every
            // existing item in the world without touching a single shard row.
            if (PropertyManager.GetBool("equipment_mods_enabled").Item)
                propertyDetails.AddRange(EquipmentModDisplay.GetAppraisalLines(wo));

            // Weapon mods (WaffleACE) ride the same block. Unlike the equipment mods above, these entries write
            // to NATIVE properties the client already renders elsewhere - the Gear* ratings, Cleaving,
            // IgnoreShield, MaximumVelocity - so this block is what tells a player which part of those numbers
            // came from this system. The 8130-8135 records themselves carry no [AssessmentProperty] and are
            // never sent; the magnitudes shown here are read straight off them server side.
            if (PropertyManager.GetBool("weapon_mods_enabled").Item)
                propertyDetails.AddRange(WeaponModDisplay.GetAppraisalLines(wo));

            // Multi-charge salvage tools (WaffleACE) - the Hammer items. UNGATED on purpose: unlike the two
            // blocks above, the remaining charge count is the item's OWN state rather than a rendering of a
            // feature's effect, so a player must be able to read what they are holding regardless of which of
            // equipment_mods_enabled / weapon_mods_enabled happens to be on. PropertyInt 9035 carries no
            // [AssessmentProperty], so this line is the only place the count reaches the client.
            propertyDetails.AddRange(SalvageTool.GetAppraisalLines(wo));

            WritePropertyDetails(PropertiesString, propertyDetails);

            if (!Success)
            {
                // todo: what specifically to keep/what to clear

                //PropertiesBool.Clear();
                //PropertiesDID.Clear();
                //PropertiesFloat.Clear();
                //PropertiesIID.Clear();
                //PropertiesInt.Clear();
                //PropertiesInt64.Clear();
                //PropertiesString.Clear();
            }

            BuildFlags();
        }

        /// <summary>
        /// Writes the "Property Details:" block into PropertyString.Use, appending to whatever Use string the
        /// item (or an earlier branch) already supplied. Does nothing when there are no lines to show.
        ///
        /// WHY Use, AND NOT LongDesc. VERIFIED 2026-07-30 by a live client probe (@appraiseprobe in
        /// Command/Handlers/WeaponModTestCommands.cs, run by the repo owner): the command stamps a distinct
        /// two-line marker into each of the three free-text slots AppraiseInfo can write - ShortDesc, LongDesc
        /// and Use - and one examine then shows where the client draws each. The result was that Use renders
        /// HIGHEST of the three, and that both of its marker lines rendered, so the slot preserves embedded
        /// newlines. This block was previously appended to LongDesc, which the client draws at the BOTTOM of the
        /// panel, below the spell list - far from the "Properties:" lines it annotates.
        ///
        /// WHY THE CALL SITE RUNS LAST. `wo is ManaStone` overwrites Use unconditionally, and it is the only
        /// other writer of this slot. The two are kept apart by ORDER rather than by a type test: this write
        /// happens after every type-specific branch, so a mana stone that somehow carried a mod line would show
        /// its own use message first and the block appended below it, instead of the block being silently eaten.
        /// Do not move this call back above the ManaStone branch.
        /// </summary>
        internal static void WritePropertyDetails(Dictionary<PropertyString, string> propertiesString, IReadOnlyList<string> details)
        {
            if (propertiesString == null || details == null || details.Count == 0)
                return;

            var detailsBlock = "Property Details:\n" + string.Join("\n", details);

            // NO padding newlines of our own around the block (both removed 2026-08-02 after seeing them
            // rendered live). The client already draws its own gap between the Use slot and the section above
            // it, so a leading "\n" produced a DOUBLE gap under "Properties:", and a trailing "\n" drew a
            // stray empty line under the last bullet with nothing after it.
            //
            // The one separator that IS ours to add is between an item's own Use text and the block, since
            // those two share this slot and the client puts nothing between them.
            if (propertiesString.TryGetValue(PropertyString.Use, out var use) && !string.IsNullOrEmpty(use))
                propertiesString[PropertyString.Use] = use + "\n\n" + detailsBlock;
            else
                propertiesString[PropertyString.Use] = detailsBlock;
        }

        private void BuildProperties(WorldObject wo)
        {
            PropertiesInt = wo.GetAllPropertyIntWhere(AssessmentProperties.PropertiesInt);
            PropertiesInt64 = wo.GetAllPropertyInt64Where(AssessmentProperties.PropertiesInt64);
            PropertiesBool = wo.GetAllPropertyBoolsWhere(AssessmentProperties.PropertiesBool);
            PropertiesFloat = wo.GetAllPropertyFloatWhere(AssessmentProperties.PropertiesDouble);
            PropertiesString = wo.GetAllPropertyStringWhere(AssessmentProperties.PropertiesString);
            PropertiesDID = wo.GetAllPropertyDataIdWhere(AssessmentProperties.PropertiesDataId);
            PropertiesIID = wo.GetAllPropertyInstanceIdWhere(AssessmentProperties.PropertiesInstanceId);

            if (wo is Player player)
            {
                // handle character options
                if (!player.GetCharacterOption(CharacterOption.AllowOthersToSeeYourDateOfBirth))
                    PropertiesString.Remove(PropertyString.DateOfBirth);
                if (!player.GetCharacterOption(CharacterOption.AllowOthersToSeeYourAge))
                    PropertiesInt.Remove(PropertyInt.Age);
                if (!player.GetCharacterOption(CharacterOption.AllowOthersToSeeYourChessRank))
                    PropertiesInt.Remove(PropertyInt.ChessRank);
                if (!player.GetCharacterOption(CharacterOption.AllowOthersToSeeYourFishingSkill))
                    PropertiesInt.Remove(PropertyInt.FakeFishingSkill);
                if (!player.GetCharacterOption(CharacterOption.AllowOthersToSeeYourNumberOfDeaths))
                    PropertiesInt.Remove(PropertyInt.NumDeaths);
                if (!player.GetCharacterOption(CharacterOption.AllowOthersToSeeYourNumberOfTitles))
                    PropertiesInt.Remove(PropertyInt.NumCharacterTitles);

                // handle dynamic properties for appraisal
                if (player.Allegiance != null && player.AllegianceNode != null)
                {
                    if (player.Allegiance.AllegianceName != null)
                        PropertiesString[PropertyString.AllegianceName] = player.Allegiance.AllegianceName;

                    if (player.AllegianceNode.IsMonarch)
                    {
                        PropertiesInt[PropertyInt.AllegianceFollowers] = player.AllegianceNode.TotalFollowers;
                    }
                    else
                    {
                        var monarch = player.Allegiance.Monarch;
                        var patron = player.AllegianceNode.Patron;

                        PropertiesString[PropertyString.MonarchsTitle] = AllegianceTitle.GetTitle((HeritageGroup)(monarch.Player.Heritage ?? 0), (Gender)(monarch.Player.Gender ?? 0), monarch.Rank) + " " + monarch.Player.Name;
                        PropertiesString[PropertyString.PatronsTitle] = AllegianceTitle.GetTitle((HeritageGroup)(patron.Player.Heritage ?? 0), (Gender)(patron.Player.Gender ?? 0), patron.Rank) + " " + patron.Player.Name;
                    }
                }

                if (player.Fellowship != null)
                    PropertiesString[PropertyString.Fellowship] = player.Fellowship.FellowshipName;
            }

            AddPropertyEnchantments(wo);
        }

        private void AddPropertyEnchantments(WorldObject wo)
        {
            if (wo == null) return;

            if (PropertiesInt.ContainsKey(PropertyInt.ArmorLevel))
                PropertiesInt[PropertyInt.ArmorLevel] += wo.EnchantmentManager.GetArmorMod();

            if (wo.ItemSkillLimit != null)
                PropertiesInt[PropertyInt.AppraisalItemSkill] = (int)wo.ItemSkillLimit;
            else
                PropertiesInt.Remove(PropertyInt.AppraisalItemSkill);

            if (PropertiesFloat.ContainsKey(PropertyFloat.WeaponDefense) && !(wo is Ammunition))
            {
                var defenseMod = wo.EnchantmentManager.GetDefenseMod();
                var auraDefenseMod = wo.Wielder != null && wo.IsEnchantable ? wo.Wielder.EnchantmentManager.GetDefenseMod() : 0.0f;

                PropertiesFloat[PropertyFloat.WeaponDefense] += defenseMod + auraDefenseMod;
            }

            if (PropertiesFloat.TryGetValue(PropertyFloat.ManaConversionMod, out var manaConvMod))
            {
                if (manaConvMod != 0)
                {
                    // hermetic link/void
                    var enchantmentMod = ResistMaskHelper.GetManaConversionMod(wo);

                    if (enchantmentMod != 1.0f)
                    {
                        PropertiesFloat[PropertyFloat.ManaConversionMod] *= enchantmentMod;

                        ResistHighlight = ResistMaskHelper.GetHighlightMask(wo);
                        ResistColor = ResistMaskHelper.GetColorMask(wo);
                    }
                }
                else if (!PropertyManager.GetBool("show_mana_conv_bonus_0").Item)
                {
                    PropertiesFloat.Remove(PropertyFloat.ManaConversionMod);
                }
            }

            if (PropertiesFloat.ContainsKey(PropertyFloat.ElementalDamageMod))
            {
                var enchantmentBonus = ResistMaskHelper.GetElementalDamageBonus(wo);

                if (enchantmentBonus != 0)
                {
                    PropertiesFloat[PropertyFloat.ElementalDamageMod] += enchantmentBonus;

                    ResistHighlight = ResistMaskHelper.GetHighlightMask(wo);
                    ResistColor = ResistMaskHelper.GetColorMask(wo);
                }
            }

            var appraisalLongDescDecoration = AppraisalLongDescDecorations.None;

            if (wo.ItemWorkmanship > 0)
                appraisalLongDescDecoration |= AppraisalLongDescDecorations.PrependWorkmanship;
            if (wo.MaterialType > 0)
                appraisalLongDescDecoration |= AppraisalLongDescDecorations.PrependMaterial;
            if (wo.GemType > 0 && wo.GemCount > 0)
                appraisalLongDescDecoration |= AppraisalLongDescDecorations.AppendGemInfo;

            if (appraisalLongDescDecoration > 0 && wo.LongDesc != null && wo.LongDesc.StartsWith(wo.Name))
                PropertiesInt[PropertyInt.AppraisalLongDescDecoration] = (int)appraisalLongDescDecoration;
            else
                PropertiesInt.Remove(PropertyInt.AppraisalLongDescDecoration);
        }

        private void BuildSpells(WorldObject wo)
        {
            SpellBook = new List<uint>();

            if (wo is Creature)
                return;

            // add primary spell, if exists
            if (wo.SpellDID.HasValue)
                SpellBook.Add(wo.SpellDID.Value);

            // add proc spell, if exists
            if (wo.ProcSpell.HasValue)
                SpellBook.Add(wo.ProcSpell.Value);

            var woSpellDID = wo.SpellDID;   // prevent recursive lock
            var woProcSpell = wo.ProcSpell;

            foreach (var spellId in wo.Biota.GetKnownSpellsIdsWhere(i => i != woSpellDID && i != woProcSpell, wo.BiotaDatabaseLock))
                SpellBook.Add((uint)spellId);
        }

        private void AddEnchantments(WorldObject wo)
        {
            if (wo == null) return;

            // get all currently active item enchantments on the item
            var woEnchantments = wo.EnchantmentManager.GetEnchantments(MagicSchool.ItemEnchantment);

            foreach (var enchantment in woEnchantments)
                SpellBook.Add((uint)enchantment.SpellId | EnchantmentMask);

            // show auras from wielder, if applicable

            // this technically wasn't a feature in retail

            if (wo.Wielder != null && wo.IsEnchantable && wo.WeenieType != WeenieType.Clothing && !wo.IsShield && PropertyManager.GetBool("show_aura_buff").Item)
            {
                // get all currently active item enchantment auras on the player
                var wielderEnchantments = wo.Wielder.EnchantmentManager.GetEnchantments(MagicSchool.ItemEnchantment);

                // Only show reflected Auras from player appropriate for wielded weapons
                foreach (var enchantment in wielderEnchantments)
                {
                    if (wo is Caster)
                    {
                        // Caster weapon only item Auras
                        if ((enchantment.SpellCategory == SpellCategory.DefenseModRaising)
                            || (enchantment.SpellCategory == SpellCategory.DefenseModRaisingRare)
                            || (enchantment.SpellCategory == SpellCategory.ManaConversionModRaising)
                            || (enchantment.SpellCategory == SpellCategory.SpellDamageRaising))
                        {
                            SpellBook.Add((uint)enchantment.SpellId | EnchantmentMask);
                        }
                    }
                    else if (wo is Missile || wo is Ammunition)
                    {
                        if ((enchantment.SpellCategory == SpellCategory.DamageRaising)
                            || (enchantment.SpellCategory == SpellCategory.DamageRaisingRare))
                        {
                            SpellBook.Add((uint)enchantment.SpellId | EnchantmentMask);
                        }
                    }
                    else
                    {
                        // Other weapon type Auras
                        if ((enchantment.SpellCategory == SpellCategory.AttackModRaising)
                            || (enchantment.SpellCategory == SpellCategory.AttackModRaisingRare)
                            || (enchantment.SpellCategory == SpellCategory.DamageRaising)
                            || (enchantment.SpellCategory == SpellCategory.DamageRaisingRare)
                            || (enchantment.SpellCategory == SpellCategory.DefenseModRaising)
                            || (enchantment.SpellCategory == SpellCategory.DefenseModRaisingRare)
                            || (enchantment.SpellCategory == SpellCategory.WeaponTimeRaising)
                            || (enchantment.SpellCategory == SpellCategory.WeaponTimeRaisingRare))
                        {
                            SpellBook.Add((uint)enchantment.SpellId | EnchantmentMask);
                        }
                    }
                }
            }
        }

        private void BuildArmor(WorldObject wo)
        {
            if (!Success)
                return;

            ArmorProfile = new ArmorProfile(wo);
            ArmorHighlight = ArmorMaskHelper.GetHighlightMask(wo);
            ArmorColor = ArmorMaskHelper.GetColorMask(wo);

            AddEnchantments(wo);
        }

        private void BuildCreature(Creature creature)
        {
            CreatureProfile = new CreatureProfile(creature, Success);

            // only creatures?
            ResistHighlight = ResistMaskHelper.GetHighlightMask(creature);
            ResistColor = ResistMaskHelper.GetColorMask(creature);

            if (Success && (creature is Player || !creature.Attackable))
                ArmorLevels = new ArmorLevel(creature);

            AddRatings(creature);

            if (NPCLooksLikeObject)
            {
                var weenie = creature.Weenie ?? DatabaseManager.World.GetCachedWeenie(creature.WeenieClassId);

                if (!weenie.GetProperty(PropertyInt.EncumbranceVal).HasValue)
                    PropertiesInt.Remove(PropertyInt.EncumbranceVal);
            }
            else
                PropertiesInt.Remove(PropertyInt.EncumbranceVal);

            // see notes in CombatPet.Init()
            if (creature is CombatPet && PropertiesInt.ContainsKey(PropertyInt.Faction1Bits))
                PropertiesInt.Remove(PropertyInt.Faction1Bits);
        }

        private void AddRatings(Creature creature)
        {
            if (!Success)
                return;

            var damageRating = creature.GetDamageRating();

            // include heritage / weapon type rating?
            var weapon = creature.GetEquippedWeapon() ?? creature.GetEquippedWand();
            if (creature.GetHeritageBonus(weapon))
                damageRating += 5;

            // factor in weakness here?

            var damageResistRating = creature.GetDamageResistRating();

            // factor in nether dot damage here?

            var critRating = creature.GetCritRating();
            var critDamageRating = creature.GetCritDamageRating();

            var critResistRating = creature.GetCritResistRating();
            var critDamageResistRating = creature.GetCritDamageResistRating();

            var healingBoostRating = creature.GetHealingBoostRating();
            var dotResistRating = creature.GetDotResistanceRating();
            var netherResistRating = creature.GetNetherResistRating();

            var lifeResistRating = creature.GetLifeResistRating();  // drain / harm resistance
            var gearMaxHealth = creature.GetGearMaxHealth();

            var pkDamageRating = creature.GetPKDamageRating();
            var pkDamageResistRating = creature.GetPKDamageResistRating();

            if (damageRating != 0)
                PropertiesInt[PropertyInt.DamageRating] = damageRating;
            if (damageResistRating != 0)
                PropertiesInt[PropertyInt.DamageResistRating] = damageResistRating;

            if (critRating != 0)
                PropertiesInt[PropertyInt.CritRating] = critRating;
            if (critDamageRating != 0)
                PropertiesInt[PropertyInt.CritDamageRating] = critDamageRating;

            if (critResistRating != 0)
                PropertiesInt[PropertyInt.CritResistRating] = critResistRating;
            if (critDamageResistRating != 0)
                PropertiesInt[PropertyInt.CritDamageResistRating] = critDamageResistRating;

            if (healingBoostRating != 0)
                PropertiesInt[PropertyInt.HealingBoostRating] = healingBoostRating;
            if (netherResistRating != 0)
                PropertiesInt[PropertyInt.NetherResistRating] = netherResistRating;
            if (dotResistRating != 0)
                PropertiesInt[PropertyInt.DotResistRating] = dotResistRating;

            if (lifeResistRating != 0)
                PropertiesInt[PropertyInt.LifeResistRating] = lifeResistRating;
            if (gearMaxHealth != 0)
                PropertiesInt[PropertyInt.GearMaxHealth] = gearMaxHealth;

            if (pkDamageRating != 0)
                PropertiesInt[PropertyInt.PKDamageRating] = pkDamageRating;
            if (pkDamageResistRating != 0)
                PropertiesInt[PropertyInt.PKDamageResistRating] = pkDamageResistRating;

            // add ratings from equipped items?

            // CLIENT DISPLAY, VERIFIED 2026-08-03 by disassembling acclient.exe. Of everything sent above,
            // the assess panel prints exactly five paired lines: 307/314 "Dmg/CritDmg Rating", 308/316
            // "Dmg/CritDmg Resist", 381/382 "PK Dmg/Res", 386/387 "Overpower %", 350/351 "DoT/Life".
            // CritRating 313 and CritResistRating 315 are read ONLY as visibility gates for the first two
            // of those lines and are never printed; HealingBoostRating 323 is read into a stack slot the
            // client never reads back. NetherResistRating 331 is not referenced anywhere in .text, so it is
            // sent and dropped as well; DotResistRating 350 and LifeResistRating 351 do print, as the pair.
            //
            // Do NOT try to route those three through their Gear* partners (GearCrit 372, GearCritResist
            // 373, GearHealingBoost 376). The renderer that draws the Gear* "Ratings:" block is chosen only
            // when the appraisal carries NO CreatureProfile, and BuildCreature always sends one for a
            // creature, so that renderer never runs for a player and the ids are a silent no-op. The full
            // trace, with addresses, is in RatingAbility.GenerateAll.
            //
            // GearMaxHealth 379 just above is the one member of the Gear* family that is not display-only:
            // the client also reads it in its vital-max computation at 0x00592D20 (alongside Enlightenment
            // 390), which is why Player_Vitals.GetNetworkGearMaxHealth sends a compensated value on the
            // property-update path. That computation is reached only from call sites outside the assess
            // panel's code range, so the raw value sent here is display data and does not feed it.
            // GearCrit 372, GearCritResist 373 and GearHealingBoost 376 have no such second site at all:
            // each appears exactly once in .text, inside the Gear* renderer.
        }

        private void BuildWeapon(WorldObject weapon)
        {
            if (!Success)
                return;

            var weaponProfile = new WeaponProfile(weapon);

            //WeaponHighlight = WeaponMaskHelper.GetHighlightMask(weapon, wielder);
            //WeaponColor = WeaponMaskHelper.GetColorMask(weapon, wielder);
            WeaponHighlight = WeaponMaskHelper.GetHighlightMask(weaponProfile);
            WeaponColor = WeaponMaskHelper.GetColorMask(weaponProfile);

            if (!(weapon is Caster))
                WeaponProfile = weaponProfile;

            // item enchantments can also be on wielder currently
            AddEnchantments(weapon);
        }

        private void BuildHookProfile(WorldObject hookedItem)
        {
            HookProfile = new HookProfile();
            if (hookedItem.Inscribable)
                HookProfile.Flags |= HookFlags.Inscribable;
            if (hookedItem is Healer)
                HookProfile.Flags |= HookFlags.IsHealer;
            if (hookedItem is Food)
                HookProfile.Flags |= HookFlags.IsFood;
            if (hookedItem is Lockpick)
                HookProfile.Flags |= HookFlags.IsLockpick;
            if (hookedItem.ValidLocations != null)
                HookProfile.ValidLocations = hookedItem.ValidLocations.Value;
            if (hookedItem.AmmoType != null)
                HookProfile.AmmoType = hookedItem.AmmoType.Value;
        }

        /// <summary>
        /// Constructs the bitflags for appraising a WorldObject
        /// </summary>
        private void BuildFlags()
        {
            if (PropertiesInt.Count > 0)
                Flags |= IdentifyResponseFlags.IntStatsTable;
            if (PropertiesInt64.Count > 0)
                Flags |= IdentifyResponseFlags.Int64StatsTable;         				
			if (PropertiesBool.Count > 0)
                Flags |= IdentifyResponseFlags.BoolStatsTable;
            if (PropertiesFloat.Count > 0)
                Flags |= IdentifyResponseFlags.FloatStatsTable;
            if (PropertiesString.Count > 0)
                Flags |= IdentifyResponseFlags.StringStatsTable;
            if (PropertiesDID.Count > 0)
                Flags |= IdentifyResponseFlags.DidStatsTable;
            if (SpellBook.Count > 0)
                Flags |= IdentifyResponseFlags.SpellBook;

            if (ResistHighlight != 0)
                Flags |= IdentifyResponseFlags.ResistEnchantmentBitfield;
            if (ArmorProfile != null)
                Flags |= IdentifyResponseFlags.ArmorProfile;
            if (CreatureProfile != null && !NPCLooksLikeObject)
                Flags |= IdentifyResponseFlags.CreatureProfile;
            if (WeaponProfile != null)
                Flags |= IdentifyResponseFlags.WeaponProfile;
            if (HookProfile != null)
                Flags |= IdentifyResponseFlags.HookProfile;
            if (ArmorHighlight != 0)
                Flags |= IdentifyResponseFlags.ArmorEnchantmentBitfield;
            if (WeaponHighlight != 0)
                Flags |= IdentifyResponseFlags.WeaponEnchantmentBitfield;
            if (ArmorLevels != null)
                Flags |= IdentifyResponseFlags.ArmorLevels;
        }
    }

    public static class AppraiseInfoExtensions
    {
        /// <summary>
        /// Writes the AppraiseInfo to the network stream
        /// </summary>
        public static void Write(this BinaryWriter writer, AppraiseInfo info)
        {
            writer.Write((uint)info.Flags);
            writer.Write(Convert.ToUInt32(info.Success));
            if (info.Flags.HasFlag(IdentifyResponseFlags.IntStatsTable))
                writer.Write(info.PropertiesInt);
            if (info.Flags.HasFlag(IdentifyResponseFlags.Int64StatsTable))
                writer.Write(info.PropertiesInt64);
            if (info.Flags.HasFlag(IdentifyResponseFlags.BoolStatsTable))
                writer.Write(info.PropertiesBool);
            if (info.Flags.HasFlag(IdentifyResponseFlags.FloatStatsTable))
                writer.Write(info.PropertiesFloat);
            if (info.Flags.HasFlag(IdentifyResponseFlags.StringStatsTable))
                writer.Write(info.PropertiesString);
            if (info.Flags.HasFlag(IdentifyResponseFlags.DidStatsTable))
                writer.Write(info.PropertiesDID);
            if (info.Flags.HasFlag(IdentifyResponseFlags.SpellBook))
                writer.Write(info.SpellBook);
            if (info.Flags.HasFlag(IdentifyResponseFlags.ArmorProfile))
                writer.Write(info.ArmorProfile);
            if (info.Flags.HasFlag(IdentifyResponseFlags.CreatureProfile))
                writer.Write(info.CreatureProfile);
            if (info.Flags.HasFlag(IdentifyResponseFlags.WeaponProfile))
                writer.Write(info.WeaponProfile);
            if (info.Flags.HasFlag(IdentifyResponseFlags.HookProfile))
                writer.Write(info.HookProfile);
            if (info.Flags.HasFlag(IdentifyResponseFlags.ArmorEnchantmentBitfield))
            {
                writer.Write((ushort)info.ArmorHighlight);
                writer.Write((ushort)info.ArmorColor);
            }
            if (info.Flags.HasFlag(IdentifyResponseFlags.WeaponEnchantmentBitfield))
            {
                writer.Write((ushort)info.WeaponHighlight);
                writer.Write((ushort)info.WeaponColor);
            }
            if (info.Flags.HasFlag(IdentifyResponseFlags.ResistEnchantmentBitfield))
            {
                writer.Write((ushort)info.ResistHighlight);
                writer.Write((ushort)info.ResistColor);
            }
            if (info.Flags.HasFlag(IdentifyResponseFlags.ArmorLevels))
                writer.Write(info.ArmorLevels);
        }

        private static readonly PropertyIntComparer PropertyIntComparer = new PropertyIntComparer(16);
        private static readonly PropertyInt64Comparer PropertyInt64Comparer = new PropertyInt64Comparer(8);
        private static readonly PropertyBoolComparer PropertyBoolComparer = new PropertyBoolComparer(8);
        private static readonly PropertyFloatComparer PropertyFloatComparer = new PropertyFloatComparer(8);
        private static readonly PropertyStringComparer PropertyStringComparer = new PropertyStringComparer(8);
        private static readonly PropertyDataIdComparer PropertyDataIdComparer = new PropertyDataIdComparer(8);

        // TODO: generics
        public static void Write(this BinaryWriter writer, Dictionary<PropertyInt, int> _properties)
        {
            PackableHashTable.WriteHeader(writer, _properties.Count, PropertyIntComparer.NumBuckets);

            var properties = new SortedDictionary<PropertyInt, int>(_properties, PropertyIntComparer);

            foreach (var kvp in properties)
            {
                writer.Write((uint)kvp.Key);
                writer.Write(kvp.Value);
            }
        }

        public static void Write(this BinaryWriter writer, Dictionary<PropertyInt64, long> _properties)
        {
            PackableHashTable.WriteHeader(writer, _properties.Count, PropertyInt64Comparer.NumBuckets);

            var properties = new SortedDictionary<PropertyInt64, long>(_properties, PropertyInt64Comparer);

            foreach (var kvp in properties)
            {
                writer.Write((uint)kvp.Key);
                writer.Write(kvp.Value);
            }
        }

        public static void Write(this BinaryWriter writer, Dictionary<PropertyBool, bool> _properties)
        {
            PackableHashTable.WriteHeader(writer, _properties.Count, PropertyBoolComparer.NumBuckets);

            var properties = new SortedDictionary<PropertyBool, bool>(_properties, PropertyBoolComparer);

            foreach (var kvp in properties)
            {
                writer.Write((uint)kvp.Key);
                writer.Write(Convert.ToUInt32(kvp.Value));
            }
        }

        public static void Write(this BinaryWriter writer, Dictionary<PropertyFloat, double> _properties)
        {
            PackableHashTable.WriteHeader(writer, _properties.Count, PropertyFloatComparer.NumBuckets);

            var properties = new SortedDictionary<PropertyFloat, double>(_properties, PropertyFloatComparer);

            foreach (var kvp in properties)
            {
                writer.Write((uint)kvp.Key);
                writer.Write(kvp.Value);
            }
        }

        public static void Write(this BinaryWriter writer, Dictionary<PropertyString, string> _properties)
        {
            PackableHashTable.WriteHeader(writer, _properties.Count, PropertyStringComparer.NumBuckets);

            var properties = new SortedDictionary<PropertyString, string>(_properties, PropertyStringComparer);

            foreach (var kvp in properties)
            {
                writer.Write((uint)kvp.Key);
                writer.WriteString16L(kvp.Value);
            }
        }

        public static void Write(this BinaryWriter writer, Dictionary<PropertyDataId, uint> _properties)
        {
            PackableHashTable.WriteHeader(writer, _properties.Count, PropertyDataIdComparer.NumBuckets);

            var properties = new SortedDictionary<PropertyDataId, uint>(_properties, PropertyDataIdComparer);

            foreach (var kvp in properties)
            {
                writer.Write((uint)kvp.Key);
                writer.Write(kvp.Value);
            }
        }
    }
}
