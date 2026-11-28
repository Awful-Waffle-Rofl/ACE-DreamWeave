using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.BalanceTrials
{
    /// <summary>One equipped item as the digest sees it. ItemCurMana is deliberately absent: item mana drains in every fight.</summary>
    public sealed record TrialDigestEquipped(uint Wcid, int Location, int? Structure, int? MaxStructure, int? ItemMaxMana);

    /// <summary>One enchantment registry entry as the digest sees it. Target is "p" (the player) or "i&lt;CurrentWieldedLocation&gt;" (an equipped item).</summary>
    public sealed record TrialDigestEnchantment(string Target, int SpellId, bool CasterIsSelf);

    /// <summary>
    /// One possession (pack or equipped) as the stack split sees it. Stack is the possession's units: its StackSize, or for
    /// a salvage bag its Structure (see <see cref="TrialDigest.StackOf"/>).
    /// </summary>
    public sealed record TrialDigestStack(uint Wcid, WeenieType Type, long Stack);

    /// <summary>
    /// Everything a digest is computed from, projected off a Player (live or offline) so the hash itself is pure.
    /// <paramref name="Consumables"/> is the consumable stacks summed by wcid (see <see cref="TrialDigest.IsConsumable"/>):
    /// carried beside the digest so the soft reset can restore them, and never part of either hash.
    /// </summary>
    public sealed record TrialDigestInput(
        IReadOnlyList<TrialDigestEquipped> Equipped,
        IReadOnlyDictionary<uint, long> PackStacks,
        IReadOnlyDictionary<string, int> ClassAbilityRanks,
        int ActiveFacetSlot,
        IReadOnlyList<TrialDigestEnchantment> Enchantments,
        IReadOnlyDictionary<uint, long> Consumables = null);

    /// <summary>
    /// What a session reads off the offline master, once: its digest. The consumable wcids only decide what the Core leaves out;
    /// the master's consumable counts are not used (the soft reset restores to the rep's own baseline).
    /// </summary>
    public sealed record TrialMasterSnapshot(TrialDigest Digest)
    {
        /// <summary>The LoadOfflinePlayerAsync projection. Reads only.</summary>
        public static TrialMasterSnapshot Project(Player p, IReadOnlyCollection<uint> consumableWcids)
        {
            var input = TrialDigest.Project(p, consumableWcids);
            return new TrialMasterSnapshot(TrialDigest.Of(input));
        }
    }

    /// <summary>
    /// The state digest of a trial character (Docs/BalanceTrials/DESIGN.md 6.3, narrowed by Plan C Task 7).
    ///
    /// Core: sha256 of the canonical text of equipped items (wcid, CurrentWieldedLocation, Structure, MaxStructure,
    /// ItemMaxMana), pack units summed by wcid, ClassAbility_* ranks and the active facet slot. A pack item's units are its
    /// StackSize, except a salvage bag's, which are its Structure: combining partial bags through the salvage panel
    /// (Player.HandleSalvaging, a client action) repacks the same units into fewer bags, and a repack is not state the rep
    /// fights with (<see cref="StackOf"/>). Consumables are left out of
    /// Core, equipped or in the pack (<see cref="IsConsumable"/>): they burn in every fight, and the soft reset restores them
    /// to the master's counts instead (final review F1). Enchantments: sha256 of the
    /// sorted (target, spell id, caster-is-self) entries. A rep drifts when its Core differs from the master's Core, or its
    /// Enchantments differ from the block's first rep. state_digest records Core.
    ///
    /// The narrowing: the master carries no buffs (the buff profile is applied at rep setup), and item mana drains in every
    /// fight, so comparing everything against the master would flag every rep.
    /// </summary>
    public sealed record TrialDigest(string Core, string Enchantments)
    {
        /// <summary>
        /// Projects <paramref name="p"/> and hashes it. Reads only; safe as a LoadOfflinePlayerAsync projection (it writes
        /// nothing, not even the class-ability rank cache: ranks are read off the quest registry directly).
        /// </summary>
        public static TrialDigest Compute(Player p, IReadOnlyCollection<uint> consumableWcids) => Of(Project(p, consumableWcids));

        /// <summary>The archetype's LiftUp.Consumables wcids: consumables by fiat, whatever their weenie type.</summary>
        public static IReadOnlyCollection<uint> ConsumableWcids(TrialArchetype a)
            => (a?.LiftUp?.Consumables ?? Array.Empty<TrialConsumable>()).Select(c => c.Wcid).ToHashSet();

        /// <summary>The consumable weenie types: what burns in a fight (components, food, kits, mana stones, ammunition).</summary>
        public static bool IsConsumableType(WeenieType type)
            => type == WeenieType.SpellComponent || type == WeenieType.Food || type == WeenieType.Healer
               || type == WeenieType.ManaStone || type == WeenieType.Ammunition;

        /// <summary>A consumable: a consumable weenie type, or one of the archetype's consumable wcids.</summary>
        public static bool IsConsumable(uint wcid, WeenieType type, IReadOnlyCollection<uint> consumableWcids)
            => IsConsumableType(type) || (consumableWcids != null && consumableWcids.Contains(wcid));

        /// <summary>
        /// The stack split: non-consumable stacks summed by wcid (they go into Core) and consumable stacks summed by wcid
        /// (carried beside the digest, never hashed). Pure.
        /// </summary>
        public static (IReadOnlyDictionary<uint, long> Core, IReadOnlyDictionary<uint, long> Consumables) SplitStacks(
            IEnumerable<TrialDigestStack> stacks, IReadOnlyCollection<uint> consumableWcids)
        {
            var core = new Dictionary<uint, long>();
            var consumables = new Dictionary<uint, long>();

            foreach (var st in stacks)
            {
                var into = IsConsumable(st.Wcid, st.Type, consumableWcids) ? consumables : core;
                into[st.Wcid] = (into.TryGetValue(st.Wcid, out var n) ? n : 0) + st.Stack;
            }

            return (core, consumables);
        }

        public static TrialDigestInput Project(Player p, IReadOnlyCollection<uint> consumableWcids)
        {
            // An equipped consumable (the wielded ammunition stack) is a consumable, not an equipped entry: a stack shot to
            // nothing is dequipped and destroyed (Player.UpdateAmmoAfterLaunch).
            var equippedItems = p.EquippedObjects.Values.Where(i => !IsConsumable(i.WeenieClassId, i.WeenieType, consumableWcids)).ToList();
            var equippedGuids = new HashSet<uint>(equippedItems.Select(i => i.Guid.Full));

            var rest = p.GetAllPossessions().Where(i => !equippedGuids.Contains(i.Guid.Full));
            var (packStacks, consumables) = SplitStacks(rest.Select(StackOf), consumableWcids);

            var quests = p.Character == null
                ? Enumerable.Empty<(string, int)>()
                : ACE.Database.Models.Shard.CharacterExtensions.GetQuests(p.Character, p.CharacterDatabaseLock).Select(q => (q.QuestName, q.NumTimesCompleted));

            var enchantments = EnchantmentEntries("p", p.Biota.PropertiesEnchantmentRegistry.Clone(p.BiotaDatabaseLock), p.Guid.Full);

            foreach (var item in equippedItems)
                enchantments.AddRange(EnchantmentEntries("i" + (item.CurrentWieldedLocation.HasValue ? (int)item.CurrentWieldedLocation.Value : 0),
                    item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock), p.Guid.Full));

            return new TrialDigestInput(
                equippedItems.Select(EquippedEntry).ToList(),
                packStacks,
                ClassAbilityRanks(quests),
                p.ActiveFacetSlot,
                enchantments,
                consumables);
        }

        /// <summary>
        /// The digest's view of one possession. A salvage bag (ItemType.TinkeringMaterial, the test Player.TryAddSalvage
        /// itself uses to tell a bag from loot) counts its Structure, so bags summed by wcid give the material's total units
        /// whatever the split: combining partial bags conserves Structure exactly (SalvageCombine.Pour) and only changes
        /// how many bags hold it. Everything else counts its StackSize (1 when it has none).
        /// </summary>
        public static TrialDigestStack StackOf(WorldObject item)
            => new TrialDigestStack(item.WeenieClassId, item.WeenieType,
                item.ItemType == ItemType.TinkeringMaterial ? item.Structure ?? 0 : item.StackSize ?? 1);

        public static TrialDigest Of(TrialDigestInput input) => new TrialDigest(Sha256(CoreText(input)), Sha256(EnchantmentText(input)));

        /// <summary>The digest's view of one equipped item. Reads ItemMaxMana and never ItemCurMana.</summary>
        public static TrialDigestEquipped EquippedEntry(WorldObject item) => new TrialDigestEquipped(
            item.WeenieClassId,
            item.CurrentWieldedLocation.HasValue ? (int)item.CurrentWieldedLocation.Value : 0,
            item.GetProperty(PropertyInt.Structure),
            item.GetProperty(PropertyInt.MaxStructure),
            item.GetProperty(PropertyInt.ItemMaxMana));

        /// <summary>The ClassAbility_* quest rows as ability name -> rank, clamped as GetClassAbilityCache clamps them; rank 0 is left out.</summary>
        public static IReadOnlyDictionary<string, int> ClassAbilityRanks(IEnumerable<(string QuestName, int NumTimesCompleted)> quests)
        {
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (name, num) in quests)
            {
                if (!ClassAbilityRegistry.TryGetByQuestKey(name, out var skill))
                    continue;

                var rank = Math.Clamp(num, 0, skill.MaxRank);
                if (rank > 0)
                    ranks[skill.Name] = rank;
            }

            return ranks;
        }

        /// <summary>
        /// The digest entries of one enchantment registry. Cooldown entries are left out: the soft reset clears them, and
        /// they are bookkeeping, not state the rep fights with.
        /// </summary>
        public static List<TrialDigestEnchantment> EnchantmentEntries(string target, IEnumerable<PropertiesEnchantmentRegistry> registry, uint selfGuid)
            => registry
                .Where(e => e != null && !TrialReset.IsCooldownEntry(e))
                .Select(e => new TrialDigestEnchantment(target, e.SpellId, e.CasterObjectId == selfGuid))
                .ToList();

        /// <summary>
        /// The drift rule: Core against the master's Core, Enchantments against the block's first rep. A null master Core or
        /// block value is never a match (the caller refuses a rep before that can happen).
        /// </summary>
        public static bool IsDrift(TrialDigest rep, string masterCore, string blockEnchantments, out string reason)
        {
            reason = null;

            if (!string.Equals(rep.Core, masterCore, StringComparison.Ordinal))
                reason = $"core {Short(rep.Core)} differs from the master's {Short(masterCore)}";

            if (!string.Equals(rep.Enchantments, blockEnchantments, StringComparison.Ordinal))
                reason = (reason == null ? "" : reason + "; ") + $"enchantments {Short(rep.Enchantments)} differ from the block's first rep {Short(blockEnchantments)}";

            return reason != null;
        }

        private static string Short(string sha) => sha == null ? "(none)" : sha.Length > 12 ? sha.Substring(0, 12) : sha;

        internal static string CoreText(TrialDigestInput i)
        {
            // Every number goes through the invariant culture: the digest must not depend on the server's locale.
            // v2: salvage bags count their Structure, not one per bag (StackOf).
            var sb = new StringBuilder("core-v2\n");

            foreach (var e in i.Equipped.OrderBy(e => e.Location).ThenBy(e => e.Wcid).ThenBy(e => e.Structure).ThenBy(e => e.MaxStructure).ThenBy(e => e.ItemMaxMana))
                sb.Append("E|").Append(I(e.Wcid)).Append('|').Append(I(e.Location)).Append('|').Append(N(e.Structure)).Append('|').Append(N(e.MaxStructure)).Append('|').Append(N(e.ItemMaxMana)).Append('\n');

            foreach (var kv in i.PackStacks.OrderBy(kv => kv.Key))
                sb.Append("P|").Append(I(kv.Key)).Append('|').Append(I(kv.Value)).Append('\n');

            foreach (var kv in i.ClassAbilityRanks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("C|").Append(kv.Key).Append('|').Append(I(kv.Value)).Append('\n');

            sb.Append("F|").Append(I(i.ActiveFacetSlot)).Append('\n');

            return sb.ToString();
        }

        internal static string EnchantmentText(TrialDigestInput i)
        {
            var sb = new StringBuilder("ench-v1\n");

            foreach (var e in i.Enchantments.OrderBy(e => e.Target, StringComparer.Ordinal).ThenBy(e => e.SpellId).ThenBy(e => e.CasterIsSelf))
                sb.Append(e.Target).Append('|').Append(I(e.SpellId)).Append('|').Append(e.CasterIsSelf ? '1' : '0').Append('\n');

            return sb.ToString();
        }

        private static string I(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static string N(int? v) => v.HasValue ? I(v.Value) : "-";

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
