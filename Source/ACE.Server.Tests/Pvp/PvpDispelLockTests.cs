using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The L4 dispel vuln lock (Docs/Pvp/DESIGN.md "PvP rules (levers)", choke points D1 -
    /// EnchantmentManager.SelectDispel - and D2 - Player_WeaponMods.ApplyWeaponModCleanse): after a recent
    /// PK attack, a vulnerability enchantment cast by ANOTHER player on the target cannot be dispelled away
    /// by pvp_dispel_vuln_lock_seconds. A monster-cast vulnerability, a SELF-cast vulnerability, and every
    /// non-vulnerability enchantment stay dispellable throughout.
    ///
    /// SEEDING: no PropertyManager key is read - the dials come from a swapped PvpRuleTunables.DialSource.
    /// DialSource and Observer are saved and restored per test. The target is a Player built with
    /// RuntimeHelpers.GetUninitializedObject (a real Player needs the world database), with only Biota and
    /// BiotaDatabaseLock set by reflection, exactly as PvpDamageCapTests.SeededPlayer does - enough for
    /// LastPkAttackTimestamp's GetProperty/SetProperty and Guid to work.
    /// </summary>
    [TestClass]
    public class PvpDispelLockTests
    {
        private const long LockSeconds = 300;
        private const uint PlayerCasterGuid = 0x50000002;
        private const uint MonsterCasterGuid = 0x80000001;

        private Func<PvpRuleDials> savedDialSource;
        private Action<PvpChokePoint, double, double> savedObserver;

        private List<(PvpChokePoint Point, double Before, double After)> observed;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedObserver = PvpRules.Observer;

            observed = new List<(PvpChokePoint, double, double)>();

            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            UseDials(Dials(LockSeconds));
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.Observer = savedObserver;
        }

        // ================= fixtures =================

        private static PvpRuleDials Dials(long lockSeconds, bool enabled = true) =>
            PvpRuleTunables.Defaults with { Enabled = enabled, DispelVulnLockSeconds = lockSeconds };

        private static void UseDials(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => dials;

        private const uint TargetGuid = 0x50000003;

        private static Player SeededPlayer(double lastPkAttackTimestamp, uint guid = TargetGuid)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "<Guid>k__BackingField", new ObjectGuid(guid));

            player.LastPkAttackTimestamp = lastPkAttackTimestamp;

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static PropertiesEnchantmentRegistry Entry(SpellCategory category, uint casterObjectId, int spellId = 521) => new PropertiesEnchantmentRegistry
        {
            SpellId = spellId,
            SpellCategory = category,
            CasterObjectId = casterObjectId,
            Duration = 300,
        };

        /// <summary>A vulnerability cast by ANOTHER player (Acid Vulnerability Other I's category, verified against the dat).</summary>
        private static PropertiesEnchantmentRegistry PlayerCastVuln() => Entry(SpellCategory.AcidVulnerability, PlayerCasterGuid);

        /// <summary>A monster-cast vulnerability, same category - the lock never touches this one.</summary>
        private static PropertiesEnchantmentRegistry MonsterCastVuln() => Entry(SpellCategory.AcidVulnerability, MonsterCasterGuid, spellId: 522);

        /// <summary>A player-cast harmful enchantment that is NOT a vulnerability (e.g. a DoT-style debuff category).</summary>
        private static PropertiesEnchantmentRegistry PlayerCastNonVulnHarmful() => Entry(SpellCategory.HealthDepleting, PlayerCasterGuid, spellId: 700);

        /// <summary>A vulnerability the TARGET cast on themself - CasterObjectId == TargetGuid. The lock never touches this one either.</summary>
        private static PropertiesEnchantmentRegistry SelfCastVuln() => Entry(SpellCategory.SlashVulnerability, TargetGuid, spellId: 1127);

        // ================= player-cast vuln kept inside the window =================

        [TestMethod]
        public void PlayerCastVuln_InsideTheWindow_IsExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln(), MonsterCastVuln(), PlayerCastNonVulnHarmful() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(2, result.Count);
            Assert.IsFalse(result.Contains(candidates[0]), "the player-cast vulnerability must be excluded");
            Assert.IsTrue(result.Contains(candidates[1]), "the monster-cast vulnerability stays dispellable");
            Assert.IsTrue(result.Contains(candidates[2]), "the non-vulnerability harmful enchantment stays dispellable");

            Assert.AreEqual(1, observed.Count, "an exclusion must be reported exactly once");
            Assert.AreEqual(PvpChokePoint.D1, observed[0].Point);
            Assert.AreEqual(3.0, observed[0].Before);
            Assert.AreEqual(2.0, observed[0].After);
        }

        // ================= controls: never excluded =================

        [TestMethod]
        public void MonsterCastVuln_IsNeverExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { MonsterCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, observed.Count, "nothing excluded means no Report");
        }

        [TestMethod]
        public void NonVulnerabilityHarmful_IsNeverExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastNonVulnHarmful() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, observed.Count);
        }

        // ================= outside the window =================

        [TestMethod]
        public void PlayerCastVuln_OutsideTheWindow_IsNotExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime() - (LockSeconds + 1));
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count, "outside the window, the player-cast vulnerability stays dispellable");
            Assert.AreEqual(0, observed.Count);
        }

        // ================= setting off =================

        [TestMethod]
        public void LockSecondsAtZero_IsOff()
        {
            UseDials(Dials(lockSeconds: 0));

            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void MasterSwitchOff_IsOff()
        {
            UseDials(Dials(LockSeconds, enabled: false));

            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, observed.Count);
        }

        // ================= non-player target =================

        [TestMethod]
        public void NonPlayerTarget_IsUnchanged_AndNeverReadsTheDials()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(LockSeconds); };

            var monster = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
            SetInherited(monster, "<Biota>k__BackingField", new Biota());
            SetInherited(monster, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(monster, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, dialReads, "a non-player target must never read the dials");
            Assert.AreEqual(0, observed.Count);
        }

        // ================= classify before reading any setting =================

        /// <summary>
        /// A Player target with no player-cast vulnerability from ANOTHER player is a PvE interaction (a
        /// monster-cast vuln, a self-cast vuln, and a non-vulnerability harmful enchantment are each never
        /// lockable), so it must never read the dials, exactly like NonPlayerTarget_IsUnchanged_AndNeverReadsTheDials.
        /// </summary>
        [TestMethod]
        public void PlayerTarget_WithNoOtherPlayersVuln_IsUnchanged_AndNeverReadsTheDials()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(LockSeconds); };

            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { MonsterCastVuln(), SelfCastVuln(), PlayerCastNonVulnHarmful() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(0, dialReads, "a PvE interaction (no other player's vuln present) must never read the dials");
            Assert.AreEqual(0, observed.Count);
        }

        /// <summary>Control: a Player target holding another player's vuln IS a PvP question, so it reads the dials exactly once.</summary>
        [TestMethod]
        public void PlayerTarget_WithAnotherPlayersVuln_ReadsTheDialsExactlyOnce()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(LockSeconds); };

            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(0, result.Count, "the other player's vulnerability must be excluded");
            Assert.AreEqual(1, dialReads, "a PvP question must read the dials exactly once");
        }

        // ================= empty / null input =================

        [TestMethod]
        public void EmptyCandidates_IsUnchanged_AndNeverReadsTheDials()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(LockSeconds); };

            var target = SeededPlayer(Time.GetUnixTime());

            var result = PvpRules.ApplyDispelVulnLock(target, new List<PropertiesEnchantmentRegistry>(), PvpChokePoint.D1);

            Assert.AreEqual(0, result.Count);
            Assert.AreEqual(0, dialReads);
        }

        // ================= self-cast is never locked =================

        [TestMethod]
        public void SelfCastVuln_IsNeverExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { SelfCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count, "a vulnerability the target cast on themself must stay dispellable");
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void SelfCastVuln_AlongsideAnotherPlayersVuln_OnlyTheOtherPlayersIsExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { SelfCastVuln(), PlayerCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains(candidates[0]), "self-cast stays dispellable");
            Assert.IsFalse(result.Contains(candidates[1]), "another player's vulnerability is excluded");

            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(2.0, observed[0].Before);
            Assert.AreEqual(1.0, observed[0].After);
        }

        // ================= D2: Player_WeaponMods.ApplyWeaponModCleanse =================

        [TestMethod]
        public void D2_AnotherPlayersVuln_InsideTheWindow_IsExcluded_AndReportedAtD2()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { PlayerCastVuln(), MonsterCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D2);

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains(candidates[1]));

            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.D2, observed[0].Point, "D2 must report itself, not D1");
        }

        [TestMethod]
        public void D2_SelfCastVuln_IsNeverExcluded()
        {
            var target = SeededPlayer(Time.GetUnixTime());
            var candidates = new List<PropertiesEnchantmentRegistry> { SelfCastVuln() };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D2);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, observed.Count);
        }

        // ================= round 2: same-category post-selection re-expansion =================
        //
        // ApplyWeaponModCleanse (D2) re-expands its chosen entry's category to EVERY caster's copy via
        // DispellingEdgeAbility.SelectAllLayers(entry, EnchantmentManager.GetEnchantments(entry.SpellCategory)),
        // AFTER the lever already filtered the pre-pick candidate list once. If that expansion is not
        // filtered too, a monster-cast vuln surviving the first filter (and getting picked as `entry`) can
        // drag another player's SAME-CATEGORY vuln back in when every layer of that category is re-gathered
        // fresh from the registry. Player_WeaponMods.cs now filters `layers` through the lever a second time
        // for exactly this reason (see the source-text pin below).
        //
        // EnchantmentManager.SelectDispel (D1) has NO equivalent re-expansion: WorldObject_Magic.
        // HandleCastSpell_Dispel passes SelectDispel's return value straight into Dispel with nothing in
        // between that re-gathers by category or caster (pinned below), so D1 needed no second filter.

        [TestMethod]
        public void SameCategory_MonsterAndOtherPlayer_D1_OnlyOtherPlayerExcluded()
        {
            // what a category-wide re-gather would hand D1 if one existed: the monster's copy and another
            // player's copy of the SAME category, both present at once
            var target = SeededPlayer(Time.GetUnixTime());
            var monsterCopy = Entry(SpellCategory.SlashVulnerability, MonsterCasterGuid, spellId: 1121);
            var otherPlayerCopy = Entry(SpellCategory.SlashVulnerability, PlayerCasterGuid, spellId: 1127);
            var candidates = new List<PropertiesEnchantmentRegistry> { monsterCopy, otherPlayerCopy };

            var result = PvpRules.ApplyDispelVulnLock(target, candidates, PvpChokePoint.D1);

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains(monsterCopy), "the monster's copy of the category stays dispellable");
            Assert.IsFalse(result.Contains(otherPlayerCopy), "another player's copy of the SAME category must still be excluded");
        }

        [TestMethod]
        public void SameCategory_MonsterAndOtherPlayer_D2_LayersReExpansion_OnlyOtherPlayerExcluded()
        {
            // simulates Player_WeaponMods.cs's `layers` list: DispellingEdgeAbility.SelectAllLayers pulled in
            // every caster's copy of the chosen entry's category, including another player's, which the
            // pre-pick `harmful` filter never saw because that copy was not yet in the top-layer snapshot
            var target = SeededPlayer(Time.GetUnixTime());
            var monsterCopy = Entry(SpellCategory.SlashVulnerability, MonsterCasterGuid, spellId: 1121);
            var otherPlayerCopy = Entry(SpellCategory.SlashVulnerability, PlayerCasterGuid, spellId: 1127);
            var layers = new List<PropertiesEnchantmentRegistry> { monsterCopy, otherPlayerCopy };

            var result = PvpRules.ApplyDispelVulnLock(target, layers, PvpChokePoint.D2);

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains(monsterCopy), "the monster's copy of the category stays dispellable");
            Assert.IsFalse(result.Contains(otherPlayerCopy), "another player's re-expanded copy of the SAME category must still be excluded");

            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.D2, observed[0].Point);
        }

        // ================= pure category classifier =================

        [TestMethod]
        public void IsPlayerCastVulnerability_Pure()
        {
            Assert.IsTrue(PvpRules.IsPlayerCastVulnerability(SpellCategory.AcidVulnerability, PlayerCasterGuid));
            Assert.IsTrue(PvpRules.IsPlayerCastVulnerability(SpellCategory.MeleeDefenseLowering, PlayerCasterGuid));
            Assert.IsTrue(PvpRules.IsPlayerCastVulnerability(SpellCategory.ArmorLowering, PlayerCasterGuid));
            Assert.IsFalse(PvpRules.IsPlayerCastVulnerability(SpellCategory.AcidVulnerability, MonsterCasterGuid), "monster caster");
            Assert.IsFalse(PvpRules.IsPlayerCastVulnerability(SpellCategory.HealthDepleting, PlayerCasterGuid), "not a vulnerability category");
        }

        // ================= call-site pin =================

        /// <summary>
        /// D1 is wired at exactly one call site, in EnchantmentManager.SelectDispel, beside the item-source
        /// exclusion and before the random count selection (`if (number == -1)`). Matched on CODE only.
        /// </summary>
        [TestMethod]
        public void ChokePointCallSite_IsPinned_AfterItemSourceExclusion_BeforeCountSelection()
        {
            var root = FindSourceRoot();
            var path = System.IO.Path.Combine(root, "ACE.Server", "WorldObjects", "Managers", "EnchantmentManager.cs");
            Assert.IsTrue(System.IO.File.Exists(path), $"missing {path}");

            var lines = System.IO.File.ReadAllLines(path);
            var itemSource = -1;
            var lever = -1;
            var countSelection = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var stripped = StripComment(lines[i]);

                if (stripped == "filtered = filtered.Where(e => e.Duration != -1);")
                    itemSource = i;

                if (stripped == "filtered = PvpRules.ApplyDispelVulnLock(WorldObject, filtered.ToList(), PvpChokePoint.D1);")
                    lever = i;

                if (stripped == "if (number == -1)")
                    countSelection = i;
            }

            Assert.AreNotEqual(-1, itemSource, "item-source exclusion line not found");
            Assert.AreNotEqual(-1, lever, "PvpRules.ApplyDispelVulnLock D1 call site not found");
            Assert.AreNotEqual(-1, countSelection, "random count selection guard not found");

            Assert.IsTrue(itemSource < lever, "D1 must sit AFTER the item-source exclusion");
            Assert.IsTrue(lever < countSelection, "D1 must sit BEFORE the random count selection");
        }

        /// <summary>
        /// D2 is wired at exactly one call site, in Player_WeaponMods.ApplyWeaponModCleanse, after the
        /// harmful-candidate selection and before the random pick (`var entry = harmful[...]`). Matched on
        /// CODE only.
        /// </summary>
        [TestMethod]
        public void D2ChokePointCallSite_IsPinned_AfterHarmfulSelection_BeforeRandomPick()
        {
            var root = FindSourceRoot();
            var path = System.IO.Path.Combine(root, "ACE.Server", "WorldObjects", "Player_WeaponMods.cs");
            Assert.IsTrue(System.IO.File.Exists(path), $"missing {path}");

            var lines = System.IO.File.ReadAllLines(path);
            var harmfulSelection = -1;
            var lever = -1;
            var randomPick = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var stripped = StripComment(lines[i]);

                if (stripped == "EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Undef), IsHarmfulSpell);")
                    harmfulSelection = i;

                if (stripped == "harmful = PvpRules.ApplyDispelVulnLock(this, harmful, PvpChokePoint.D2);")
                    lever = i;

                if (stripped == "var entry = harmful[ThreadSafeRandom.Next(0, harmful.Count - 1)];")
                    randomPick = i;
            }

            Assert.AreNotEqual(-1, harmfulSelection, "harmful-candidate selection line not found");
            Assert.AreNotEqual(-1, lever, "PvpRules.ApplyDispelVulnLock D2 call site not found");
            Assert.AreNotEqual(-1, randomPick, "random pick line not found");

            Assert.IsTrue(harmfulSelection < lever, "D2 must sit AFTER the harmful-candidate selection");
            Assert.IsTrue(lever < randomPick, "D2 must sit BEFORE the random pick");
        }

        /// <summary>
        /// D2's SECOND filter: after DispellingEdgeAbility.SelectAllLayers re-expands the chosen entry's
        /// category to every caster's copy, that re-expansion (`layers`) is filtered through the lock again,
        /// AFTER the expansion and BEFORE the Dispel call it feeds. Matched on CODE only.
        /// </summary>
        [TestMethod]
        public void D2LayersReExpansion_IsPinned_AfterSelectAllLayers_BeforeDispel()
        {
            var root = FindSourceRoot();
            var path = System.IO.Path.Combine(root, "ACE.Server", "WorldObjects", "Player_WeaponMods.cs");
            Assert.IsTrue(System.IO.File.Exists(path), $"missing {path}");

            var lines = System.IO.File.ReadAllLines(path);
            var expansion = -1;
            var lever = -1;
            var dispel = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var stripped = StripComment(lines[i]);

                if (stripped == "DispellingEdgeAbility.SelectAllLayers(entry, EnchantmentManager.GetEnchantments(entry.SpellCategory)), IsHarmfulSpell);")
                    expansion = i;

                if (stripped == "layers = PvpRules.ApplyDispelVulnLock(this, layers, PvpChokePoint.D2);")
                    lever = i;

                if (stripped == "EnchantmentManager.Dispel(layers);")
                    dispel = i;
            }

            Assert.AreNotEqual(-1, expansion, "SelectAllLayers re-expansion line not found");
            Assert.AreNotEqual(-1, lever, "the second PvpRules.ApplyDispelVulnLock(layers, ...) call site not found");
            Assert.AreNotEqual(-1, dispel, "EnchantmentManager.Dispel(layers) call not found");

            Assert.IsTrue(expansion < lever, "the second D2 filter must sit AFTER SelectAllLayers' re-expansion");
            Assert.IsTrue(lever < dispel, "the second D2 filter must sit BEFORE EnchantmentManager.Dispel(layers)");
        }

        /// <summary>
        /// D1 has no post-filter re-expansion to close: WorldObject_Magic.HandleCastSpell_Dispel passes
        /// EnchantmentManager.SelectDispel's return value straight into Dispel, with no line in between that
        /// re-gathers by category or caster (the way Player_WeaponMods.cs's SelectAllLayers does for D2).
        /// Matched on CODE only, and the two lines must be ADJACENT (ignoring only comment/blank lines), so
        /// any future line inserted between them that re-expands the list would break this pin.
        /// </summary>
        [TestMethod]
        public void D1_HasNoPostFilterReExpansion_SelectDispelFeedsDispelDirectly()
        {
            var root = FindSourceRoot();
            var path = System.IO.Path.Combine(root, "ACE.Server", "WorldObjects", "WorldObject_Magic.cs");
            Assert.IsTrue(System.IO.File.Exists(path), $"missing {path}");

            var lines = System.IO.File.ReadAllLines(path);
            var selectDispel = -1;
            var dispel = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var stripped = StripComment(lines[i]);

                if (stripped == "var removeSpells = target.EnchantmentManager.SelectDispel(spell);")
                    selectDispel = i;

                if (stripped == "target.EnchantmentManager.Dispel(removeSpells.Select(s => s.Enchantment).ToList());")
                    dispel = i;
            }

            Assert.AreNotEqual(-1, selectDispel, "SelectDispel call site not found");
            Assert.AreNotEqual(-1, dispel, "Dispel call site not found");

            // every line strictly between the two must be blank once comments are stripped - nothing
            // re-gathers enchantments by category or caster in between
            for (var i = selectDispel + 1; i < dispel; i++)
                Assert.AreEqual(string.Empty, StripComment(lines[i]), $"line {i + 1} sits between SelectDispel and Dispel and is not blank/comment: `{lines[i]}`");

            Assert.IsTrue(selectDispel < dispel);
        }

        private static string StripComment(string raw)
        {
            var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
            return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
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
