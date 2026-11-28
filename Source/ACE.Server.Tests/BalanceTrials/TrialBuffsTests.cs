using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Network;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The buff profile's adminAll preconditions (TrialBuffs.CheckAdminAll), the soft reset's mana fill over every possession
    /// (TrialReset.Recharge), its item-spell reactivation (TrialReset.NeedsSpellActivation, Player.ActivateTrialItemSpells)
    /// and its long profile buffs (TrialReset.Lengthen). Reads no PropertyManager key. The two
    /// TrialBuffs seams it sets (Level8Installed, Warn) are restored in a finally.
    /// </summary>
    [TestClass]
    public class TrialBuffsTests
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Session>k__BackingField")]
        private static extern ref Session SessionField(Player p);

        /// <summary>An uninitialized rep with a session at <paramref name="level"/>, or no session when null. Nothing else is set.</summary>
        private static Player Rep(AccessLevel? level)
        {
            var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(p) = new ObjectGuid(0x59000100);

            if (level.HasValue)
            {
                var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
                s.SetAccessLevel(level.Value);
                SessionField(p) = s;
            }

            return p;
        }

        [TestMethod]
        public void AdminAll_refuses_a_rep_below_Sentinel_or_without_a_session()
        {
            // Below Sentinel, CreateSentinelBuffPlayers returns without casting and without an error: the rep would run unbuffed.
            var low = Assert.ThrowsExactly<InvalidOperationException>(() => TrialBuffs.Apply(Rep(AccessLevel.Advocate), "adminAll:8"));
            StringAssert.Contains(low.Message, "Sentinel");
            StringAssert.Contains(low.Message, "Advocate");

            var none = Assert.ThrowsExactly<InvalidOperationException>(() => TrialBuffs.Apply(Rep(null), "adminAll:8"));
            StringAssert.Contains(none.Message, "session");
        }

        [TestMethod]
        public void AdminAll_level_8_fallback_to_level_7_is_warned()
        {
            var installed = TrialBuffs.Level8Installed;
            var warn = TrialBuffs.Warn;
            var warnings = new List<string>();

            try
            {
                TrialBuffs.Warn = warnings.Add;

                TrialBuffs.Level8Installed = () => false;
                TrialBuffs.CheckAdminAll(Rep(AccessLevel.Developer), "adminAll:8", 8);
                Assert.AreEqual(1, warnings.Count, "level 8 asked for and not installed");
                StringAssert.Contains(warnings[0], "level 7");

                TrialBuffs.CheckAdminAll(Rep(AccessLevel.Sentinel), "adminAll:7", 7);
                Assert.AreEqual(1, warnings.Count, "level 7 asked for: nothing falls back");

                TrialBuffs.Level8Installed = () => true;
                TrialBuffs.CheckAdminAll(Rep(AccessLevel.Developer), "adminAll:8", 8);
                Assert.AreEqual(1, warnings.Count, "level 8 installed: no warning");
            }
            finally
            {
                TrialBuffs.Level8Installed = installed;
                TrialBuffs.Warn = warn;
            }
        }

        private static string Body(string path, string signature)
            => Regex.Replace(Regex.Replace(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), signature), @"//[^\n]*", ""), @"\s+", " ");

        [TestMethod]
        public void Apply_checks_adminAll_before_casting()
            => StringAssert.Contains(Body("Source/ACE.Server/BalanceTrials/TrialBuffs.cs", "public static int Apply(Player p, string profile)"),
                "CheckAdminAll(p, profile, level); p.CreateSentinelBuffPlayers(new[] { p }, true, (ulong)level);");

        // ================================================================ mana on every possession

        private static uint nextItemGuid = 0x80FE0000;

        private static WorldObject ManaItem(int? curMana, int? maxMana, EquipMask? worn)
        {
            var weenie = new Weenie { WeenieClassId = 1001, WeenieType = WeenieType.Clothing, PropertiesInt = new Dictionary<PropertyInt, int>() };
            if (curMana.HasValue) weenie.PropertiesInt[PropertyInt.ItemCurMana] = curMana.Value;
            if (maxMana.HasValue) weenie.PropertiesInt[PropertyInt.ItemMaxMana] = maxMana.Value;
            if (worn.HasValue) weenie.PropertiesInt[PropertyInt.CurrentWieldedLocation] = (int)worn.Value;
            return new Clothing(weenie, new ObjectGuid(nextItemGuid++));
        }

        [TestMethod]
        public void Soft_reset_fills_mana_on_every_possession_not_only_worn_ones()
        {
            var worn = ManaItem(10, 5000, EquipMask.ChestArmor);
            var packed = ManaItem(0, 3000, null);
            var full = ManaItem(800, 800, null);
            var noMana = ManaItem(null, null, null);

            Assert.AreEqual(2, TrialReset.Recharge(new[] { worn, packed, full, noMana }));
            Assert.AreEqual(5000, worn.ItemCurMana);
            Assert.AreEqual(3000, packed.ItemCurMana, "a pack item is filled too");
            Assert.AreEqual(800, full.ItemCurMana);
            Assert.IsNull(noMana.ItemCurMana, "an item with no ItemMaxMana is left alone");

            var soft = Body("Source/ACE.Server/BalanceTrials/TrialReset.cs",
                "public static TrialSoftResetSummary Soft(Player p, TrialArchetype a, IReadOnlyDictionary<uint, long> baseline, string buffProfile)");
            StringAssert.Contains(soft, "var recharged = Recharge(p.GetAllPossessions());", "step 5 walks every possession (GetAllPossessions: pack, side packs and worn)");
        }

        // ================================================================ item spells back on

        private static WorldObject SpellItem(bool affecting, int? curMana, params int[] spells)
        {
            var item = ManaItem(curMana, 5000, EquipMask.ChestArmor);
            item.Biota.PropertiesSpellBook = new Dictionary<int, float>();
            foreach (var s in spells)
                item.Biota.PropertiesSpellBook[s] = 2f;
            if (affecting)
                item.OnSpellsActivated();
            return item;
        }

        private static readonly PropertiesEnchantmentRegistry[] NoEntries = new PropertiesEnchantmentRegistry[0];

        private static PropertiesEnchantmentRegistry CastBy(WorldObject item, int spellId)
            => new PropertiesEnchantmentRegistry { SpellId = spellId, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Beneficial, Duration = -1, CasterObjectId = item.Guid.Full };

        [TestMethod]
        public void Soft_reset_turns_spells_back_on_only_for_a_worn_item_that_ran_dry()
        {
            Assert.IsTrue(TrialReset.NeedsSpellActivation(SpellItem(false, 5000, 2783), NoEntries), "spells off, mana refilled: turned back on");
            Assert.IsFalse(TrialReset.NeedsSpellActivation(SpellItem(false, 0, 2783), NoEntries), "no mana: the wield path would not activate it");
            Assert.IsFalse(TrialReset.NeedsSpellActivation(SpellItem(false, null, 2783), NoEntries), "no mana property");
            Assert.IsFalse(TrialReset.NeedsSpellActivation(SpellItem(false, 5000), NoEntries), "no spells");

            // Already active with its spells in force: never cast twice.
            var active = SpellItem(true, 5000, 2783);
            Assert.IsFalse(TrialReset.NeedsSpellActivation(active, new[] { CastBy(active, 2783) }), "active and in force");

            var soft = Body("Source/ACE.Server/BalanceTrials/TrialReset.cs",
                "public static TrialSoftResetSummary Soft(Player p, TrialArchetype a, IReadOnlyDictionary<uint, long> baseline, string buffProfile)");
            var apply = "var cast = TrialBuffs.Apply(p, buffProfile);";
            var activate = "var activated = p.ActivateTrialItemSpells(); if (activated > 0) Recharge(p.EquippedObjects.Values);";
            StringAssert.Contains(soft, "var recharged = Recharge(p.GetAllPossessions());");
            StringAssert.Contains(soft, activate, "after the mana fill, and topped up again after the activation's one point");
            Assert.IsTrue(soft.IndexOf("var recharged = Recharge(p.GetAllPossessions());", StringComparison.Ordinal) < soft.IndexOf(activate, StringComparison.Ordinal));
            Assert.IsTrue(soft.IndexOf(activate, StringComparison.Ordinal) < soft.IndexOf(apply, StringComparison.Ordinal), "item spells before the buff profile");
            StringAssert.Contains(soft, "recharged, cast, lengthened, activated);", "the count reaches the summary");

            // The player's own wield-time path, not a hand-rolled cast, with the buffed checks waived (the PvP template rule).
            var player = Body("Source/ACE.Server/WorldObjects/Player_Trial.cs", "internal int ActivateTrialItemSpells()");
            StringAssert.Contains(player, "if (!TrialReset.NeedsSpellActivation(item, registry)) continue; var wieldError = CheckWieldRequirements(item, waiveBuffedRequirements: true); if (wieldError != WeenieError.None)");
            StringAssert.Contains(player, "bool on; BeginFacetSpellActivationWaiver(item.Guid.Full); try { on = TryActivateSpells(item); } finally { EndFacetSpellActivationWaiver(item.Guid.Full); }");
            StringAssert.Contains(player, "if (on && item.IsAffecting) activated++; else log.Warn(", "a refused activation is not counted, and is logged");
            Assert.IsFalse(player.Contains("CreateItemSpell"), "no hand-rolled cast");
        }

        /// <summary>
        /// HandleManaDepleted strips an item's spells 2 s after switching it off. An activation inside that window reads
        /// IsAffecting true afterwards with nothing in force, so the selection reads the entries, not the flag alone.
        /// </summary>
        [TestMethod]
        public void An_item_marked_on_with_nothing_in_force_is_activated_again_and_a_pending_removal_is_seen()
        {
            var stripped = SpellItem(true, 5000, 2783, 2784);
            Assert.IsTrue(TrialReset.NeedsSpellActivation(stripped, NoEntries), "on, but nothing it cast is in force");

            var unrelated = new PropertiesEnchantmentRegistry { SpellId = 2783, Duration = -1, CasterObjectId = 0x80FF7777 };
            Assert.IsTrue(TrialReset.NeedsSpellActivation(stripped, new[] { unrelated }), "an entry cast by another item does not count");
            Assert.IsFalse(TrialReset.NeedsSpellActivation(stripped, new[] { CastBy(stripped, 2783) }), "one entry in force: not all missing, not re-cast");

            // An item-school spell lands on the item itself (Creature.CreateItemSpell), so the item's own registry counts too.
            stripped.Biota.PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry> { CastBy(stripped, 2784) };
            Assert.IsFalse(TrialReset.NeedsSpellActivation(stripped, NoEntries), "in force on the item itself");

            // Pending removal: off, entries still there (the 2 s removal has not run).
            var justDry = SpellItem(false, 5000, 2783);
            Assert.IsTrue(TrialReset.ItemSpellRemovalPending(justDry, new[] { CastBy(justDry, 2783) }));
            Assert.IsFalse(TrialReset.ItemSpellRemovalPending(justDry, NoEntries), "already stripped: nothing pending");
            Assert.IsFalse(TrialReset.ItemSpellRemovalPending(active(), NoEntries), "an active item has nothing pending");

            var soft = Body("Source/ACE.Server/BalanceTrials/TrialReset.cs",
                "public static TrialSoftResetSummary Soft(Player p, TrialArchetype a, IReadOnlyDictionary<uint, long> baseline, string buffProfile)");
            StringAssert.Contains(soft, "var removalPending = p.EquippedObjects.Values.Any(i => ItemSpellRemovalPending(i, registryNow)); var activated = p.ActivateTrialItemSpells();",
                "pending is read BEFORE the activation changes the state");
            StringAssert.Contains(soft, "if (removalPending) RecheckItemSpellsLater(p);");

            var recheck = Body("Source/ACE.Server/BalanceTrials/TrialReset.cs", "private static void RecheckItemSpellsLater(Player p)");
            StringAssert.Contains(recheck, "var session = p.TrialSession;", "the session is captured when the recheck is scheduled");
            StringAssert.Contains(recheck, "if (session == null || !ReferenceEquals(p.TrialSession, session)) return;", "and the recheck runs only for that session");
            Assert.IsTrue(TrialReset.ItemSpellRecheckSeconds > 2.0, "past HandleManaDepleted's 2 s delay");

            static WorldObject active() => SpellItem(true, 5000, 2783);
        }
        // ================================================================ long profile buffs

        private const uint RepGuid = 0x59000100;
        private const uint WornGuid = 0x80FF0001;

        [TestMethod]
        public void Profile_buffs_get_24_hours_stay_timed_and_keep_the_digest()
        {
            var worn = new HashSet<uint> { WornGuid };
            var profileBuff = new PropertiesEnchantmentRegistry { SpellId = 2783, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Beneficial, Duration = 3600, StartTime = -12, CasterObjectId = RepGuid };
            var itemBane = new PropertiesEnchantmentRegistry { SpellId = 4400, StatModType = EnchantmentTypeFlags.Beneficial, Duration = 1800, StartTime = 0, CasterObjectId = RepGuid };
            var itemOwnSpell = new PropertiesEnchantmentRegistry { SpellId = 1600, StatModType = EnchantmentTypeFlags.Skill, Duration = -1, CasterObjectId = WornGuid };
            var wornCast = new PropertiesEnchantmentRegistry { SpellId = 1700, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Beneficial, Duration = 30, StartTime = -5, CasterObjectId = WornGuid };
            var cooldown = new PropertiesEnchantmentRegistry { SpellId = 0x8000 | 100, SpellCategory = (SpellCategory)0x8000, StatModType = EnchantmentTypeFlags.Cooldown, Duration = 45, StartTime = -3 };

            var all = new[] { profileBuff, itemBane, itemOwnSpell, wornCast, cooldown };
            var digestBefore = TrialDigest.EnchantmentText(new TrialDigestInput(new TrialDigestEquipped[0], new Dictionary<uint, long>(), new Dictionary<string, int>(), 0,
                TrialDigest.EnchantmentEntries("p", all, RepGuid)));

            var changed = TrialReset.Lengthen(all, worn);

            CollectionAssert.AreEquivalent(new[] { profileBuff, itemBane }, changed, "only the timed entries the profile cast");
            foreach (var e in changed)
            {
                Assert.AreEqual(TrialReset.ProfileBuffDurationSeconds, e.Duration);
                Assert.AreEqual(0.0, e.StartTime, "24 h from now");
            }
            Assert.AreEqual(86400.0, TrialReset.ProfileBuffDurationSeconds);

            Assert.AreEqual(-1.0, itemOwnSpell.Duration, "an item's own spell is untouched");
            Assert.AreEqual(30.0, wornCast.Duration, "a worn item's cast is untouched");
            Assert.AreEqual(-5.0, wornCast.StartTime);
            Assert.AreEqual(45.0, cooldown.Duration, "a cooldown is untouched");

            // Still timed, so the next soft reset's step 2 removes them: nothing carries or stacks into the next rep.
            foreach (var e in changed)
                Assert.IsTrue(TrialReset.IsCarriedTimedEnchantment(e, worn), $"spell {e.SpellId} is cleared before the next rep");

            // The Enchantments digest reads (target, spell, caster-is-self), never the duration.
            var digestAfter = TrialDigest.EnchantmentText(new TrialDigestInput(new TrialDigestEquipped[0], new Dictionary<uint, long>(), new Dictionary<string, int>(), 0,
                TrialDigest.EnchantmentEntries("p", all, RepGuid)));
            Assert.AreEqual(digestBefore, digestAfter);
        }

        [TestMethod]
        public void Soft_reset_clears_worn_items_and_lengthens_the_profile_after_casting_it()
        {
            var soft = Body("Source/ACE.Server/BalanceTrials/TrialReset.cs",
                "public static TrialSoftResetSummary Soft(Player p, TrialArchetype a, IReadOnlyDictionary<uint, long> baseline, string buffProfile)");

            var itemClear = "foreach (var item in p.EquippedObjects.Values) { var itemCarried = item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock).Where(e => IsCarriedTimedEnchantment(e, wornGuids)).ToList(); if (itemCarried.Count > 0) item.EnchantmentManager.Dispel(itemCarried);";
            var apply = "var cast = TrialBuffs.Apply(p, buffProfile); var lengthened = LengthenProfileBuffs(p, wornGuids); p.SetMaxVitals();";

            StringAssert.Contains(soft, itemClear);
            StringAssert.Contains(soft, apply);
            Assert.IsTrue(soft.IndexOf(itemClear, StringComparison.Ordinal) < soft.IndexOf(apply, StringComparison.Ordinal), "worn items are cleared before the profile is cast");

            var lengthen = Body("Source/ACE.Server/BalanceTrials/TrialReset.cs", "internal static int LengthenProfileBuffs(Player p, IReadOnlySet<uint> wornGuids)");
            StringAssert.Contains(lengthen, "var mine = Lengthen(p.Biota.PropertiesEnchantmentRegistry.Clone(p.BiotaDatabaseLock), wornGuids);");
            StringAssert.Contains(lengthen, "p.Session?.Network.EnqueueSend(new GameEventMagicUpdateMultipleEnchantments(p.Session, mine.Select(e => new Enchantment(p, e)).ToList()));");
            StringAssert.Contains(lengthen, "foreach (var item in p.EquippedObjects.Values) { var onItem = Lengthen(item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock), wornGuids);");
        }
    }
}
