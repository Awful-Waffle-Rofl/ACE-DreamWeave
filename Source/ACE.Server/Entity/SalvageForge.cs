using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The Hollow Hammer (WaffleACE, wcid 1002650): a tool with no material of its own that is used on a
    /// FULL salvage bag of one of the five multi-charge-Hammer materials - Tiger Eye, Obsidian, Tourmaline,
    /// Amethyst, Serpentine - already in the player's pack. It consumes that bag plus a matching number of
    /// OTHER full bags of the same material, and the Hollow Hammer itself, and creates one full 10-charge
    /// Hammer of that material (1001918/1001912/1001910/1001911/1001916 - see <see cref="MaterialTable"/>).
    ///
    /// THE PRICE SCALES WITH SKILL. The player's CURRENT (buffed) value in the tinkering skill that material
    /// answers to - <see cref="MaterialInfo.Skill"/> - is read at use time through
    /// <c>player.GetCreatureSkill(skill).Current</c>, and <see cref="BagsRequired(int)"/> turns that into a
    /// bag count via three PropertyManager thresholds: under 500 costs 10 bags, 500-699 costs 9, 700-899
    /// costs 8, 900+ costs 7. A Hammer normally costs 10 single-use bags one at a time (see SalvageTool's own
    /// remarks on why a Hammer exists at all); this is a discount for a player who has actually trained the
    /// matching skill, not an alternate acquisition path - the Hammer produced is identical to one bought
    /// sealed from Bevan Tolliver.
    ///
    /// WHY MATERIAL, NOT TARGET-SPECIFIC. Every consumer here matches by MaterialType alone: a full bag of
    /// Tiger Eye counts toward a Tiger Eye Hammer regardless of which Tiger Eye bag was actually clicked, and
    /// <see cref="CountFullBags"/> sums every eligible bag across the WHOLE inventory (main pack, side packs,
    /// via Player.GetAllPossessions) rather than just the one bag used as the target. The targeted bag is
    /// always included among those consumed - see <see cref="Apply"/> - but it is not privileged beyond that.
    ///
    /// A HAMMER CAN NEVER BE EATEN AS A BAG. SalvageTool.IsSalvageTool is checked on every candidate bag
    /// (<see cref="IsEligibleBag"/>), which is what keeps an existing multi-charge Hammer - itself a stand-in
    /// for a full bag under EquipmentModManager/WeaponModManager/SpellRerollManager/TigerEyeArmorTinker - from
    /// being silently spent as raw salvage here. Only an ordinary bag (no SalvageToolCharges property at all)
    /// counts.
    ///
    /// THE HOLLOW HAMMER ITSELF CARRIES NO MATERIALTYPE, NO SALVAGETOOLCHARGES AND NO STRUCTURE, so it can
    /// never be mistaken by any of the other four consumers for a bag or for one of their own Hammers - see
    /// weenies/1002650 Hollow Hammer.sql's header for the full reasoning. It is matched purely by
    /// WeenieClassId (<see cref="IsHollowHammer"/>); no new property was registered for this feature.
    ///
    /// PRICE FIRST, THEN MUTATE. <see cref="VerifyUseRequirements"/> counts and re-verifies before
    /// <see cref="Apply"/> spends anything, and Apply removes bags one at a time via
    /// player.TryConsumeFromInventoryWithNetworking - the same "a false return leaves the item untouched"
    /// contract SalvageTool.TryConsume documents - before ever touching the Hollow Hammer or creating the
    /// resulting Hammer. If a consume fails partway through, the method stops immediately and reports a
    /// generic craft error rather than creating the Hammer; whatever bags were already consumed before the
    /// failure stay consumed; this mirrors the shape of ApplyToArmor's caller in TigerEyeArmorTinker.Apply.
    ///
    /// THE UX SHAPE - busy/combat guards, VerifyUseRequirements, a ClapHands chain that re-verifies inside the
    /// action, then SendUseDoneEvent - follows TigerEyeArmorTinker.UseObjectOnTarget, which itself follows
    /// PrismaticDriftStone and CorePlating. There is no server-side confirmation dialog for the same reason
    /// as every other salvage-bag intercept: the source is ItemType.TinkeringMaterial, so the client fires
    /// its own generic tinkering confirmation before the server is ever contacted.
    /// </summary>
    public static class SalvageForge
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The Hollow Hammer's wcid. Matched directly - see the class remarks for why no property was added.</summary>
        public const uint HollowHammerWcid = 1002650;

        /// <summary>
        /// Default salvage bag capacity, restated locally for the same reason every sibling manager restates
        /// it (see TigerEyeArmorTinker.DefaultMaxStructure's remarks) - a salvage bag with no MaxStructure of
        /// its own defaults to 100 (Player_Crafting.TryAddSalvage), and any fullness test must use the same
        /// fallback or it will reject every legitimate bag.
        /// </summary>
        public const int DefaultMaxStructure = 100;

        /// <summary>
        /// One material this forge answers to: the tinkering skill it prices against, the resulting Hammer's
        /// wcid, and the display name used in player-facing messages and the appraisal panel.
        /// </summary>
        public class MaterialInfo
        {
            public MaterialType Material;
            public Skill Skill;
            public uint HammerWcid;
            public string DisplayName;
        }

        /// <summary>
        /// The five materials, keyed by MaterialType. TigerEye and Obsidian answer to Armor Tinkering,
        /// Tourmaline and Amethyst to Weapon Tinkering, Serpentine to Magic Item Tinkering - matching the
        /// existing bag/Hammer pairs (see Content/wcid-registry.tsv's salvage-hammers block, 1001910-1001919).
        /// </summary>
        public static readonly Dictionary<MaterialType, MaterialInfo> MaterialTable = new List<MaterialInfo>
        {
            new MaterialInfo { Material = MaterialType.TigerEye,   Skill = Skill.ArmorTinkering,     HammerWcid = 1001918, DisplayName = "Tiger Eye" },
            new MaterialInfo { Material = MaterialType.Obsidian,   Skill = Skill.ArmorTinkering,     HammerWcid = 1001912, DisplayName = "Obsidian" },
            new MaterialInfo { Material = MaterialType.Tourmaline, Skill = Skill.WeaponTinkering,    HammerWcid = 1001910, DisplayName = "Tourmaline" },
            new MaterialInfo { Material = MaterialType.Amethyst,   Skill = Skill.WeaponTinkering,    HammerWcid = 1001911, DisplayName = "Amethyst" },
            new MaterialInfo { Material = MaterialType.Serpentine, Skill = Skill.MagicItemTinkering, HammerWcid = 1001916, DisplayName = "Serpentine" },
        }.ToDictionary(m => m.Material);

        /// <summary>
        /// The three materials in <see cref="MaterialTable"/> order, grouped by the skill they answer to -
        /// the order <see cref="GetAppraisalLines"/> renders in and the order the class remarks list them in.
        /// </summary>
        private static readonly List<Skill> SkillOrder = new List<Skill> { Skill.ArmorTinkering, Skill.WeaponTinkering, Skill.MagicItemTinkering };

        // ---------------- source / target classification ----------------

        /// <summary>TRUE for the Hollow Hammer itself, matched by wcid alone - see the class remarks.</summary>
        public static bool IsHollowHammer(WorldObject wo) => wo != null && wo.WeenieClassId == HollowHammerWcid;

        /// <summary>
        /// TRUE if a source carries a whole application's worth of a supported material: a full bag (not a
        /// salvage tool - see the class remarks) of a MaterialType this forge answers to.
        /// </summary>
        public static bool IsFullBag(int? structure, int? maxStructure)
        {
            var max = maxStructure ?? DefaultMaxStructure;

            return max > 0 && (structure ?? 0) >= max;
        }

        public static bool IsFullBag(WorldObject wo) => wo != null && IsFullBag(wo.Structure, wo.MaxStructure);

        /// <summary>
        /// TRUE for an ordinary, full, supported-material salvage bag - never a salvage tool (a Hammer), and
        /// never an unsupported material. This is the single rule <see cref="CountFullBags"/> and
        /// <see cref="VerifyUseRequirements"/> both apply, so the two can never drift apart.
        /// </summary>
        public static bool IsEligibleBag(WorldObject wo, MaterialType material)
        {
            if (wo == null)
                return false;

            if (wo.ItemType != ItemType.TinkeringMaterial)
                return false;

            if (wo.MaterialType != material)
                return false;

            if (SalvageTool.IsSalvageTool(wo))
                return false;

            return IsFullBag(wo);
        }

        /// <summary>
        /// TRUE for a FULL-CHARGE multi-charge Hammer of <paramref name="material"/>. It shares
        /// <see cref="IsEligibleBag"/>'s first two clauses verbatim (TinkeringMaterial, matching
        /// MaterialType) and differs from it in TWO: the salvage-tool clause is INVERTED, and the
        /// fullness test is NARROWED to require a real MaxStructure rather than falling back to
        /// <see cref="DefaultMaxStructure"/> - see the note below for why that second difference is
        /// not optional. It lives here beside IsEligibleBag so the two rules cannot drift, the same
        /// reason MarketSalvageMaterials delegates to IsEligibleBag rather than restating it.
        ///
        /// A HAMMER AT 7 OF 10 CHARGES IS NOT ELIGIBLE. Nothing here consumes a Hammer partially, so
        /// this is not the salvage forge's rule leaking - it is the Wanted market's: a buyer paying a
        /// fixed price per Hammer cannot inspect the charges before the fill runs, and a part-spent
        /// Hammer is worth strictly less than a fresh one. Requiring a full charge count is the same
        /// protection the full-BAG rule gives the buyer of a bag order, expressed on the field a tool
        /// keeps its count in.
        ///
        /// NOTE the fullness test is deliberately NOT <see cref="IsFullBag"/>: that method falls back
        /// to <see cref="DefaultMaxStructure"/> (100) when MaxStructure is absent, which is right for a
        /// bag and wrong for a tool - a 10-charge Hammer would read as 10 of 100 and never be full. A
        /// tool with no MaxStructure at all is malformed and is refused rather than assumed.
        /// </summary>
        public static bool IsEligibleHammer(WorldObject wo, MaterialType material)
        {
            if (wo == null)
                return false;

            if (wo.ItemType != ItemType.TinkeringMaterial)
                return false;

            if (wo.MaterialType != material)
                return false;

            if (!SalvageTool.IsSalvageTool(wo))
                return false;

            var max = wo.MaxStructure ?? 0;

            return max > 0 && (wo.Structure ?? 0) >= max;
        }

        /// <summary>
        /// Every eligible full bag of <paramref name="material"/> across the player's whole inventory - main
        /// pack, side packs, and (harmlessly) anything wielded - via Player.GetAllPossessions.
        /// </summary>
        public static List<WorldObject> FindFullBags(Player player, MaterialType material)
        {
            return player.GetAllPossessions().Where(wo => IsEligibleBag(wo, material)).ToList();
        }

        public static int CountFullBags(Player player, MaterialType material) => FindFullBags(player, material).Count;

        // ---------------- pricing ----------------

        /// <summary>
        /// Bags required at a given skill value, using the live PropertyManager tunables
        /// salvage_forge_skill_for_9 / _8 / _7 (defaults 500/700/900).
        /// </summary>
        public static int BagsRequired(int skill)
        {
            var skillFor9 = PropertyManager.GetLong("salvage_forge_skill_for_9").Item;
            var skillFor8 = PropertyManager.GetLong("salvage_forge_skill_for_8").Item;
            var skillFor7 = PropertyManager.GetLong("salvage_forge_skill_for_7").Item;

            return BagsRequired(skill, skillFor9, skillFor8, skillFor7);
        }

        /// <summary>
        /// The pure band lookup, split out so it is directly testable without PropertyManager. Bands are
        /// half-open on the low end: skill &lt; skillFor9 costs 10; [skillFor9, skillFor8) costs 9;
        /// [skillFor8, skillFor7) costs 8; skill &gt;= skillFor7 costs 7.
        /// </summary>
        public static int BagsRequired(int skill, long skillFor9, long skillFor8, long skillFor7)
        {
            if (skill >= skillFor7)
                return 7;

            if (skill >= skillFor8)
                return 8;

            if (skill >= skillFor9)
                return 9;

            return 10;
        }

        // ---------------- entry point ----------------

        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target)
        {
            if (player.IsBusy)
            {
                player.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var allowCraftInCombat = PropertyManager.GetBool("allow_combat_mode_crafting").Item;

            if (!allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                player.SendUseDoneEvent(WeenieError.YouMustBeInPeaceModeToTrade);
                return;
            }

            var useError = VerifyUseRequirements(player, source, target, out _, out _);

            if (useError != WeenieError.None)
            {
                player.SendUseDoneEvent(useError);
                return;
            }

            var motionCommand = MotionCommand.ClapHands;

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var currentStance = player.CurrentMotionState.Stance;

            var clapTime = Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand);

            actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(clapTime);

            nextUseTime += clapTime;

            actionChain.AddAction(player, () =>
            {
                // TOCTOU GUARD - DO NOT DELETE. The player had the whole ClapHands animation to move, drop,
                // equip or destroy the Hammer, the target bag, or any of the OTHER bags this counted, so
                // every requirement - including the bag count - is checked a second time here, against the
                // state as it is NOW rather than as it was when the chain was queued.
                var reverifyError = VerifyUseRequirements(player, source, target, out var info, out var required);

                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                Apply(player, source, target, info, required);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        // ---------------- verification ----------------

        /// <summary>
        /// Every requirement, in the order a player most usefully learns about them. On success,
        /// <paramref name="info"/> and <paramref name="required"/> carry what <see cref="Apply"/> needs so it
        /// never re-derives them.
        /// </summary>
        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target, out MaterialInfo info, out int required)
        {
            info = null;
            required = 0;

            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                SendCraftMessage(player, $"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // dispatch guard: the RecipeManager intercept already tested this, so a failure here means the
            // caller was wrong rather than the player, and there is nothing useful to tell them
            if (!IsHollowHammer(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (player.FindObject(source.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
            {
                SendCraftMessage(player, $"The {source.Name} must be somewhere you can move it from.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
            {
                SendCraftMessage(player, $"The {target.Name} must be somewhere you can move it from.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (target.ItemType != ItemType.TinkeringMaterial || target.MaterialType == null || !MaterialTable.TryGetValue(target.MaterialType.Value, out info))
            {
                SendCraftMessage(player, $"The {source.Name} must be used on a full bag of tiger eye, obsidian, tourmaline, amethyst or serpentine salvage.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsEligibleBag(target, info.Material))
            {
                if (SalvageTool.IsSalvageTool(target))
                    SendCraftMessage(player, $"The {target.Name} is a Hammer, not a bag of salvage. Use the {source.Name} on a full bag instead.");
                else
                    SendCraftMessage(player, $"The {target.Name} is not full. A complete unit of salvage is required.");

                info = null;
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var skill = player.GetCreatureSkill(info.Skill).Current;

            required = BagsRequired((int)skill);

            var have = CountFullBags(player, info.Material);

            if (have < required)
            {
                var skillName = SkillName(info.Skill);
                SendCraftMessage(player, $"You need {required} full bags of {info.DisplayName} salvage to forge a hammer with your {skillName} of {skill}; you have {have}.");

                info = null;
                required = 0;
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        // ---------------- application ----------------

        /// <summary>
        /// SHARED with RefireStations.SalvageForgeStation.Apply (the Salvage Forge give-to-NPC path, added
        /// 2026-08-23) so the Hollow Hammer path and the Salvage Forge path can never drift on what "forging a
        /// Hammer" actually does: consumes <paramref name="required"/> full bags of <paramref name="info"/>'s
        /// material - <paramref name="targetBag"/> first (it is guaranteed eligible), then other eligible bags
        /// ordered by guid so a repeated failure is reproducible - and creates the resulting Hammer in the
        /// player's pack. PRICE FIRST, THEN MUTATE still applies; this method assumes whatever price the
        /// CALLER charges has already happened or is charged inside <paramref name="afterBagsConsumed"/>.
        ///
        /// <paramref name="afterBagsConsumed"/> runs after the bags are consumed and before the Hammer is
        /// created, in the SAME position the Hollow Hammer's own consumption has always run (bags, then the
        /// Hollow Hammer, then create - see <see cref="Apply"/>) - it is how the Hollow Hammer path inserts
        /// its extra "consume the Hollow Hammer itself" step without this method needing to know that step
        /// exists. Pass null for a caller with nothing extra to consume there (the Salvage Forge path - the
        /// Trade Notes were already charged by RefireStationCommon before Apply ran). A FALSE return from the
        /// delegate aborts with no Hammer created, and this method sends no chat line of its own for it -
        /// <paramref name="message"/> comes back null and the CALLER supplies any player-facing text, exactly
        /// as it does for the other two silent failures described below. The delegate itself is not expected
        /// to message; today's only delegate (the Hollow Hammer consume in <see cref="Apply"/>) logs and
        /// returns false without sending anything.
        ///
        /// Returns TRUE and sets <paramref name="message"/> to the player-facing success line on success.
        /// Returns FALSE on failure: <paramref name="message"/> is the "Contact staff" line when salvage was
        /// already spent (Hammer creation failed after everything else succeeded) and null for the two silent
        /// TOCTOU failures that predate any spend or happen mid-spend - matching the Hollow Hammer path's
        /// original silence there (it only ever sent SendUseDoneEvent for those, no chat line). A caller that
        /// needs player-facing text even for those silent cases (the Salvage Forge give path, which has no use
        /// of SendUseDoneEvent to fall back on) must supply its own fallback when <paramref name="message"/>
        /// comes back null.
        /// </summary>
        public static bool TryForgeHammer(Player player, WorldObject targetBag, MaterialInfo info, int required, Func<bool> afterBagsConsumed, out string message)
        {
            message = null;

            var bags = FindFullBags(player, info.Material).OrderBy(wo => wo.Guid.Full).ToList();

            var toConsume = new List<WorldObject> { targetBag };
            toConsume.AddRange(bags.Where(wo => wo.Guid != targetBag.Guid).Take(required - 1));

            if (toConsume.Count < required)
            {
                // TOCTOU: the caller's own reverify should have caught this, so reaching here means the state
                // changed again in the instant between that reverify and this method running
                log.Error($"SalvageForge.TryForgeHammer({player.Name}, {targetBag.Name}): only found {toConsume.Count} of {required} bags at apply time");
                return false;
            }

            var consumedCount = 0;

            foreach (var bag in toConsume)
            {
                if (!player.TryConsumeFromInventoryWithNetworking(bag))
                {
                    log.Error($"SalvageForge.TryForgeHammer({player.Name}, {targetBag.Name}): failed to consume bag {bag.Name} ({bag.Guid}) after consuming {consumedCount} of {required}");
                    return false;
                }

                consumedCount++;
            }

            if (afterBagsConsumed != null && !afterBagsConsumed())
                return false;

            var hammer = WorldObjectFactory.CreateNewWorldObject(info.HammerWcid);

            if (hammer == null || !player.TryCreateInInventoryWithNetworking(hammer))
            {
                log.Error($"SalvageForge.TryForgeHammer({player.Name}, {targetBag.Name}): consumed {consumedCount} bags but failed to create the resulting Hammer (wcid {info.HammerWcid})");

                // the bags (and whatever afterBagsConsumed spent) are already gone above; a hammer that failed
                // to reach inventory would otherwise leak its dynamic guid - see Tailoring.Finalize's own
                // "cleanup for guid manager" precedent (Source/ACE.Server/Entity/Tailoring.cs:336)
                hammer?.Destroy();

                message = $"Your salvage was spent, but the {info.DisplayName} Hammer failed to appear. Contact staff.";
                return false;
            }

            message = $"You forge a {hammer.Name} from {required} full bag{(required == 1 ? "" : "s")} of {info.DisplayName} salvage.";
            return true;
        }

        private static void Apply(Player player, WorldObject source, WorldObject target, MaterialInfo info, int required)
        {
            var forged = TryForgeHammer(player, target, info, required, afterBagsConsumed: () =>
            {
                if (!player.TryConsumeFromInventoryWithNetworking(source))
                {
                    log.Error($"SalvageForge.Apply({player.Name}, {source.Name}, {target.Name}): consumed {required} bags but failed to consume the Hollow Hammer itself");
                    return false;
                }

                return true;
            }, out var message);

            if (!forged)
            {
                if (message != null)
                    SendCraftMessage(player, message);

                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            SendCraftMessage(player, message);

            player.SendUseDoneEvent();
        }

        // ---------------- appraisal ----------------

        /// <summary>
        /// The Hollow Hammer's appraisal lines: one per tinkering skill it answers to, showing the examining
        /// player's OWN current value in that skill and what it currently prices a forge at. Empty for
        /// anything that is not the Hollow Hammer.
        /// </summary>
        public static List<string> GetAppraisalLines(WorldObject wo, Player examiner)
        {
            var lines = new List<string>();

            if (!IsHollowHammer(wo) || examiner == null)
                return lines;

            foreach (var skill in SkillOrder)
            {
                var materials = MaterialTable.Values.Where(m => m.Skill == skill).Select(m => m.DisplayName).ToList();

                if (materials.Count == 0)
                    continue;

                var current = examiner.GetCreatureSkill(skill).Current;
                var required = BagsRequired((int)current);

                lines.Add($"- {SkillName(skill)} {current}: {required} bag{(required == 1 ? "" : "s")} ({string.Join(", ", materials)})");
            }

            return lines;
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// Player-facing skill name, matching the enum's own spacing convention (e.g. "Armor Tinkering").
        /// Public (not private) so RefireStations.SalvageForgeStation can reuse it verbatim in its own
        /// "you need N bags..." refusal, rather than restating the same switch.
        /// </summary>
        public static string SkillName(Skill skill)
        {
            switch (skill)
            {
                case Skill.ArmorTinkering: return "Armor Tinkering";
                case Skill.WeaponTinkering: return "Weapon Tinkering";
                case Skill.MagicItemTinkering: return "Magic Item Tinkering";
                default: return skill.ToString();
            }
        }

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }
    }
}
