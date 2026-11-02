using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using Biota = ACE.Entity.Models.Biota;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The pack operations the Duo Gem and the Summon Gem Hammer need, behind a seam so the ordering of every
    /// destroy and create is testable without a Session. <see cref="PlayerDuoGemInventory"/> is the live one.
    /// </summary>
    public interface IDuoGemInventory
    {
        /// <summary>TRUE when the object is in the player's own pack (main pack or a side pack), not equipped.</summary>
        bool HasInPack(WorldObject wo);

        /// <summary>Spends exactly one of the object: removes it from the pack and destroys it (its row is deleted).</summary>
        bool TryConsumeOne(WorldObject wo);

        /// <summary>Persists the object.</summary>
        void Save(WorldObject wo);

        /// <summary>Builds the restored object from a snapshot, with a NEW guid. Null when it cannot be built.</summary>
        WorldObject BuildRestored(DuoGemSnapshot snapshot);

        /// <summary>Destroys an object that was built but never added (or whose add was undone).</summary>
        void Discard(WorldObject wo);

        /// <summary>TRUE when every object fits at once (slots and burden).</summary>
        bool CanAddAll(IReadOnlyList<WorldObject> items);

        /// <summary>Adds the object to the pack IN MEMORY only: no network, no save. Undo with <see cref="UndoAdd"/>.</summary>
        bool TryAddToPack(WorldObject wo);

        /// <summary>Reverses <see cref="TryAddToPack"/> and destroys the object. It was never sent or saved.</summary>
        void UndoAdd(WorldObject wo);

        /// <summary>Sends an added object to the client and saves it. Called only once nothing can be rolled back.</summary>
        void CommitAdd(WorldObject wo);
    }

    public enum DuoTarget { NotASummon, Summon, BlankCaster, DuoGem, Stacked, PetOut }

    public enum DuoAbsorbDecision { Absorb, RefusedNotASummon, RefusedBlankCaster, RefusedDuoGem, RefusedGemFull, RefusedSameItem, RefusedStacked, RefusedPetOut }

    public enum DuoAbsorbResult { Absorbed, Bound, Refused, NotInPack, GemUnreadable, TooLarge, ConsumeFailed }

    /// <summary>
    /// The Duo Summon Gem (Custom Summons Task 7). A PetDevice weenie flagged PropertyBool.DuoSummonGem, bought blank
    /// with a use-on-target cursor and no PetClass, charges or cooldown, so it summons nothing.
    ///
    /// Used on a completed summon item (retail or custom; never a blank caster still carrying SummonSpellBinder, never
    /// another Duo Gem) it captures that item into slot A, then slot B, as a <see cref="DuoGemSnapshot"/>, and destroys
    /// it. After the second absorb the gem is bound (<see cref="ApplyBinding"/>): it summons slot A's pet, and with
    /// Summon 2x slot B's pet as well, from one combined pool of charges.
    ///
    /// ABSORB ORDER, and why it cannot duplicate: capture (read only) -> write the slot in memory -> consume the item
    /// (its row delete is enqueued) -> bind if second -> save the gem. If the consume fails the slot is cleared again,
    /// so nothing was recorded for an item that still exists. The shard queue is FIFO, so a crash after the delete can
    /// lose the item but never leave it both in the pack and in the gem. The same item can never be absorbed twice: it
    /// no longer exists, and a target whose guid slot A already recorded is refused anyway.
    /// </summary>
    public static class DuoSummonGem
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The combined pool a bound gem starts with (retail essences carry 50).</summary>
        public const int Charges = 50;

        /// <summary>PropertyInt.SharedCooldown of every summon. VTank detects a summon item by exactly this value.</summary>
        public const int SummonSharedCooldown = 213;

        /// <summary>The retail essence cooldown, used when neither absorbed item carries one.</summary>
        public const double DefaultCooldownSeconds = 45.0;

        public const string UnboundText = "This Duo Summon Gem must absorb two summoning items before it can summon.";
        public const string NotASummonText = "A Duo Summon Gem can only absorb a combat summoning item.";
        public const string BlankCasterText = "That summon has not been given its two spells yet, so the gem cannot absorb it.";
        public const string DuoGemText = "A Duo Summon Gem cannot absorb another Duo Summon Gem.";
        public const string FullText = "This Duo Summon Gem already holds two summons. Break it with a Summon Gem Hammer to free them.";
        public const string SameItemText = "The gem has already absorbed that summon.";
        public const string StackedText = "Separate the stack first: the gem works on one item at a time.";
        public const string PetOutText = "Dismiss or wait out that summon's creature before the gem absorbs it.";

        /// <summary>
        /// The WeenieType of a pet wcid, from the world weenie cache. A hook so tests need no database. Only a CombatPet
        /// qualifies: a passive Pet (WeenieType 69) in a gem would retire the combat pet summoned beside it.
        /// </summary>
        internal static Func<uint, WeenieType?> PetWeenieTypeLookup = wcid => DatabaseManager.World.GetCachedWeenie(wcid)?.WeenieType;

        /// <summary>TRUE when the object is not a single item (StackSize absent or 1).</summary>
        public static bool IsStacked(WorldObject wo) => wo != null && (wo.StackSize ?? 1) != 1;

        public static bool IsDuoGem(WorldObject wo) => wo != null && wo.GetProperty(PropertyBool.DuoSummonGem) == true;

        public static int FilledSlots(WorldObject gem)
        {
            var n = 0;
            if (!string.IsNullOrEmpty(gem.GetProperty(PropertyString.DuoSummonSlotA))) n++;
            if (!string.IsNullOrEmpty(gem.GetProperty(PropertyString.DuoSummonSlotB))) n++;
            return n;
        }

        /// <summary>TRUE once both slots hold a summon. Only a bound gem summons.</summary>
        public static bool IsBound(WorldObject gem) => IsDuoGem(gem) && FilledSlots(gem) == 2;

        public static DuoTarget KindOf(WorldObject gem, WorldObject target)
        {
            if (target == null)
                return DuoTarget.NotASummon;

            if (ReferenceEquals(gem, target) || IsDuoGem(target))
                return DuoTarget.DuoGem;

            if (!(target is PetDevice device) || device.PetClass == null)
                return DuoTarget.NotASummon;

            if (IsStacked(target))
                return DuoTarget.Stacked;

            if (target.GetProperty(PropertyBool.SummonSpellBinder) == true)
                return DuoTarget.BlankCaster;

            // same test as PetDevice.IsCombatPetEssence: a passive-pet essence never qualifies
            if (PetWeenieTypeLookup((uint)device.PetClass.Value) != WeenieType.CombatPet)
                return DuoTarget.NotASummon;

            // trade, give, drop and vault all refuse a device whose pet is out; so does the gem
            if (device.Pet != null)
                return DuoTarget.PetOut;

            return DuoTarget.Summon;
        }

        /// <summary>PURE. Whether a gem holding <paramref name="filledSlots"/> may absorb this target.</summary>
        public static DuoAbsorbDecision Decide(int filledSlots, uint? slotASourceGuid, DuoTarget target, uint targetGuid, bool gemStacked = false)
        {
            if (gemStacked)
                return DuoAbsorbDecision.RefusedStacked;

            switch (target)
            {
                case DuoTarget.Stacked: return DuoAbsorbDecision.RefusedStacked;
                case DuoTarget.PetOut: return DuoAbsorbDecision.RefusedPetOut;
                case DuoTarget.NotASummon: return DuoAbsorbDecision.RefusedNotASummon;
                case DuoTarget.BlankCaster: return DuoAbsorbDecision.RefusedBlankCaster;
                case DuoTarget.DuoGem: return DuoAbsorbDecision.RefusedDuoGem;
            }

            if (filledSlots >= 2)
                return DuoAbsorbDecision.RefusedGemFull;

            if (slotASourceGuid.HasValue && slotASourceGuid.Value == targetGuid)
                return DuoAbsorbDecision.RefusedSameItem;

            return DuoAbsorbDecision.Absorb;
        }

        public static string RefusalText(DuoAbsorbDecision decision)
        {
            switch (decision)
            {
                case DuoAbsorbDecision.RefusedNotASummon: return NotASummonText;
                case DuoAbsorbDecision.RefusedBlankCaster: return BlankCasterText;
                case DuoAbsorbDecision.RefusedDuoGem: return DuoGemText;
                case DuoAbsorbDecision.RefusedGemFull: return FullText;
                case DuoAbsorbDecision.RefusedSameItem: return SameItemText;
                case DuoAbsorbDecision.RefusedStacked: return StackedText;
                case DuoAbsorbDecision.RefusedPetOut: return PetOutText;
                default: return null;
            }
        }

        /// <summary>Both snapshots of a bound gem, or false when the gem is not bound or a slot will not read.</summary>
        public static bool TryReadSlots(WorldObject gem, out DuoGemSnapshot a, out DuoGemSnapshot b)
        {
            a = null;
            b = null;

            if (!IsBound(gem))
                return false;

            return DuoGemSnapshot.TryRead(gem.GetProperty(PropertyString.DuoSummonSlotA), out a, out _)
                && DuoGemSnapshot.TryRead(gem.GetProperty(PropertyString.DuoSummonSlotB), out b, out _);
        }

        /// <summary>
        /// The pets one activation of a bound gem brings out: slot A's, then slot B's (the Summon 2x second pet).
        /// Empty for a gem that is not bound or cannot be read, so it never summons.
        /// </summary>
        public static IReadOnlyList<PetSummonSource> GetSources(WorldObject gem)
        {
            if (!TryReadSlots(gem, out var a, out var b))
                return Array.Empty<PetSummonSource>();

            return new[] { a.ToSource(), b.ToSource() };
        }

        /// <summary>The slot lines SummonItemDescription shows for a gem: one per filled, readable slot, in order.</summary>
        public static IReadOnlyList<SummonSlotInfo> SlotInfos(WorldObject gem)
        {
            var list = new List<SummonSlotInfo>();

            foreach (var key in new[] { PropertyString.DuoSummonSlotA, PropertyString.DuoSummonSlotB })
            {
                if (DuoGemSnapshot.TryRead(gem.GetProperty(key), out var snap, out _))
                    list.Add(new SummonSlotInfo(snap.Name, snap.ToSource()));
            }

            return list;
        }

        /// <summary>
        /// Absorbs <paramref name="target"/> into the next free slot. See the class remarks for the order. Refusals
        /// consume nothing and leave the gem as it was.
        /// </summary>
        public static DuoAbsorbResult Absorb(IDuoGemInventory inventory, PetDevice gem, WorldObject target, out DuoAbsorbDecision decision)
        {
            var filled = FilledSlots(gem);

            DuoGemSnapshot slotA = null;

            if (filled == 1)
            {
                // read slot A BEFORE anything is spent: the binding below needs it, and an unreadable slot must
                // refuse while the target still exists
                if (!DuoGemSnapshot.TryRead(gem.GetProperty(PropertyString.DuoSummonSlotA), out slotA, out var error))
                {
                    decision = DuoAbsorbDecision.Absorb;
                    log.Error($"[DUO GEM] gem 0x{gem.Guid.Full:X8} slot A will not read ({error}); absorb refused");
                    return DuoAbsorbResult.GemUnreadable;
                }
            }

            decision = Decide(filled, slotA?.SourceGuid, KindOf(gem, target), target?.Guid.Full ?? 0, IsStacked(gem));

            if (decision != DuoAbsorbDecision.Absorb)
                return DuoAbsorbResult.Refused;

            if (!inventory.HasInPack(gem) || !inventory.HasInPack(target))
                return DuoAbsorbResult.NotInPack;

            var snapshot = DuoGemSnapshot.Capture(target);

            if (!snapshot.TrySerialize(out var json))
                return DuoAbsorbResult.TooLarge;

            var slot = filled == 0 ? PropertyString.DuoSummonSlotA : PropertyString.DuoSummonSlotB;

            var attunedBefore = gem.GetProperty(PropertyInt.Attuned);
            var bondedBefore = gem.GetProperty(PropertyInt.Bonded);

            // record first (memory only), then spend the item; undo the record if the item cannot be spent
            gem.SetProperty(slot, json);
            InheritRestrictions(gem, snapshot);

            if (!inventory.TryConsumeOne(target))
            {
                gem.RemoveProperty(slot);
                Restore(gem, PropertyInt.Attuned, attunedBefore);
                Restore(gem, PropertyInt.Bonded, bondedBefore);
                return DuoAbsorbResult.ConsumeFailed;
            }

            var bound = filled == 1;

            if (bound)
                ApplyBinding(gem, slotA, snapshot);

            inventory.Save(gem);

            return bound ? DuoAbsorbResult.Bound : DuoAbsorbResult.Absorbed;
        }

        /// <summary>
        /// An attuned or bonded summon must not shed its restriction by hiding inside a gem: the gem takes the stricter
        /// Attuned and Bonded status of anything it absorbs. A restored item keeps its own status from its snapshot.
        /// </summary>
        private static void InheritRestrictions(WorldObject gem, DuoGemSnapshot absorbed)
        {
            var attuned = StricterAttuned(gem.GetProperty(PropertyInt.Attuned), absorbed.Int(PropertyInt.Attuned));
            if (attuned.HasValue)
                gem.SetProperty(PropertyInt.Attuned, attuned.Value);

            var bonded = StricterBonded(gem.GetProperty(PropertyInt.Bonded), absorbed.Int(PropertyInt.Bonded));
            if (bonded.HasValue)
                gem.SetProperty(PropertyInt.Bonded, bonded.Value);
        }

        /// <summary>PURE. Attuned ordering is its numeric order: Normal (0) &lt; Attuned (1) &lt; Sticky (2). Null when both are absent.</summary>
        public static int? StricterAttuned(int? own, int? absorbed)
        {
            if (!own.HasValue) return absorbed;
            if (!absorbed.HasValue) return own;
            return Math.Max(own.Value, absorbed.Value);
        }

        /// <summary>
        /// Bonded restrictiveness, least to most: Normal (0), Slippery (-1, always dropped on death), Bonded (1),
        /// Sticky (2), Destroy (-2, destroyed rather than dropped). The numeric values are NOT this order, so they are
        /// ranked explicitly. An unknown value ranks as Normal.
        /// </summary>
        public static int BondedRank(int? bonded)
        {
            switch ((BondedStatus)(bonded ?? 0))
            {
                case BondedStatus.Slippery: return 1;
                case BondedStatus.Bonded: return 2;
                case BondedStatus.Sticky: return 3;
                case BondedStatus.Destroy: return 4;
                default: return 0;
            }
        }

        /// <summary>PURE. The more restrictive of two Bonded values by <see cref="BondedRank"/> (own kept on a tie). Null when both are absent.</summary>
        public static int? StricterBonded(int? own, int? absorbed)
        {
            if (!own.HasValue) return absorbed;
            if (!absorbed.HasValue) return own;
            return BondedRank(absorbed) > BondedRank(own) ? absorbed : own;
        }

        private static void Restore(WorldObject gem, PropertyInt key, int? before)
        {
            if (before.HasValue)
                gem.SetProperty(key, before.Value);
            else
                gem.RemoveProperty(key);
        }

        /// <summary>
        /// Makes a gem holding <paramref name="a"/> and <paramref name="b"/> (b absorbed last) summon-usable:
        /// PetClass = a's pet; Structure = MaxStructure = <see cref="Charges"/>; the use requirement of whichever
        /// demands more skill, with specialization required if either requires it and the higher character level;
        /// the element bits of b; SharedCooldown <see cref="SummonSharedCooldown"/> with the longer of the two
        /// cooldowns (retail 45 s when neither has one); no SummoningMastery; ItemUseable Contained. Memory only -
        /// the caller saves and refreshes the client.
        /// </summary>
        public static void ApplyBinding(PetDevice gem, DuoGemSnapshot a, DuoGemSnapshot b)
        {
            gem.PetClass = a.Int(PropertyInt.PetClass);

            gem.SetProperty(PropertyInt.MaxStructure, Charges);
            gem.SetProperty(PropertyInt.Structure, Charges);

            ApplyRequirements(gem, a, b);

            gem.SetProperty(PropertyInt.UiEffects, (int)WithElementOf(gem.GetProperty(PropertyInt.UiEffects), b.Int(PropertyInt.UiEffects)));

            gem.CooldownId = SummonSharedCooldown;
            var cooldown = Math.Max(Cooldown(a) ?? 0, Cooldown(b) ?? 0);
            gem.CooldownDuration = cooldown > 0 ? cooldown : DefaultCooldownSeconds;

            gem.RemoveProperty(PropertyInt.SummoningMastery);

            gem.SetProperty(PropertyInt.ItemUseable, (int)Usable.Contained);
        }

        private static double? Cooldown(DuoGemSnapshot s) => s.Item.Floats.TryGetValue((int)PropertyFloat.CooldownDuration, out var v) ? v : (double?)null;

        /// <summary>The four element bits VTank reads (UiEffects.cs: Fire, Lightning, Frost, Acid).</summary>
        public const UiEffects ElementBits = UiEffects.Fire | UiEffects.Lightning | UiEffects.Frost | UiEffects.Acid;

        /// <summary>PURE. The gem's own UiEffects with its element bits replaced by those of the last absorbed item.</summary>
        public static UiEffects WithElementOf(int? gemUiEffects, int? lastAbsorbedUiEffects)
        {
            var own = (UiEffects)(gemUiEffects ?? 0) & ~ElementBits;
            var element = (UiEffects)(lastAbsorbedUiEffects ?? 0) & ElementBits;

            return own | element;
        }

        /// <summary>PURE. The requirement fields the gem takes from its two absorbed items.</summary>
        public static void ApplyRequirements(WorldObject gem, DuoGemSnapshot a, DuoGemSnapshot b)
        {
            var levelA = a.Int(PropertyInt.UseRequiresSkillLevel) ?? 0;
            var levelB = b.Int(PropertyInt.UseRequiresSkillLevel) ?? 0;

            var winner = levelB > levelA ? b : a;
            var other = ReferenceEquals(winner, a) ? b : a;

            Copy(gem, PropertyInt.UseRequiresSkill, winner.Int(PropertyInt.UseRequiresSkill) ?? other.Int(PropertyInt.UseRequiresSkill));
            Copy(gem, PropertyInt.UseRequiresSkillLevel, Max(winner.Int(PropertyInt.UseRequiresSkillLevel), other.Int(PropertyInt.UseRequiresSkillLevel)));
            Copy(gem, PropertyInt.UseRequiresSkillSpec, winner.Int(PropertyInt.UseRequiresSkillSpec) ?? other.Int(PropertyInt.UseRequiresSkillSpec));
            Copy(gem, PropertyInt.UseRequiresLevel, Max(winner.Int(PropertyInt.UseRequiresLevel), other.Int(PropertyInt.UseRequiresLevel)));
        }

        private static int? Max(int? x, int? y)
        {
            if (!x.HasValue) return y;
            if (!y.HasValue) return x;
            return Math.Max(x.Value, y.Value);
        }

        private static void Copy(WorldObject gem, PropertyInt key, int? value)
        {
            if (value.HasValue)
                gem.SetProperty(key, value.Value);
            else
                gem.RemoveProperty(key);
        }

        // ---- Player-facing shell ---------------------------------------------------------------------------

        /// <summary>Entry point from PetDevice.HandleActionUseOnTarget for a Duo Gem. The caller sends use-done.</summary>
        public static void UseOnTarget(Player player, PetDevice gem, WorldObject target)
        {
            var result = Absorb(new PlayerDuoGemInventory(player), gem, target, out var decision);

            switch (result)
            {
                case DuoAbsorbResult.Refused:
                    Say(player, RefusalText(decision));
                    return;
                case DuoAbsorbResult.NotInPack:
                    Say(player, "Both the gem and the summoning item must be in your pack.");
                    return;
                case DuoAbsorbResult.GemUnreadable:
                    Say(player, "The gem's first summon cannot be read. Nothing was absorbed.");
                    return;
                case DuoAbsorbResult.TooLarge:
                    Say(player, "That summon carries too much to fit in the gem.");
                    return;
                case DuoAbsorbResult.ConsumeFailed:
                    Say(player, "The gem could not take the summon.");
                    return;
            }

            if (result == DuoAbsorbResult.Bound && player.FindObject(gem.Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(gem);

            player.UpdateProperty(gem, PropertyString.LongDesc, SummonItemDescription.Compose(gem));
            player.EnqueueBroadcast(new GameMessageUpdateObject(gem));

            Say(player, result == DuoAbsorbResult.Bound
                ? $"The {target.Name} is drawn into the gem. The {gem.Name} now holds two summons and is ready to use."
                : $"The {target.Name} is drawn into the gem. One more summon to go.");
        }

        internal static void Say(Player player, string text)
        {
            if (text != null)
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }

    /// <summary>The live <see cref="IDuoGemInventory"/>: the player's own pack.</summary>
    public sealed class PlayerDuoGemInventory : IDuoGemInventory
    {
        private readonly Player player;
        private readonly Dictionary<uint, Container> addedTo = new Dictionary<uint, Container>();

        public PlayerDuoGemInventory(Player player) => this.player = player;

        public bool HasInPack(WorldObject wo) => wo != null && player.FindObject(wo.Guid.Full, Player.SearchLocations.MyInventory) != null;

        public bool TryConsumeOne(WorldObject wo) => player.TryConsumeFromInventoryWithNetworking(wo, 1);

        public void Save(WorldObject wo) => wo.SaveBiotaToDatabase();

        public WorldObject BuildRestored(DuoGemSnapshot snapshot)
        {
            var guid = GuidManager.NewDynamicGuid();

            var weenie = DatabaseManager.World.GetCachedWeenie(snapshot.Item.WeenieClassId);
            var weenieBase = weenie == null ? null : ACE.Entity.Adapter.WeenieConverter.ConvertToBiota(weenie, guid.Full, false, false);

            var wo = WorldObjectFactory.CreateWorldObject(snapshot.BuildRestoredBiota(guid.Full, weenieBase));

            if (wo == null)
                GuidManager.RecycleDynamicGuid(guid);

            return wo;
        }

        public void Discard(WorldObject wo)
        {
            if (wo != null && !wo.IsDestroyed)
                wo.Destroy();
        }

        public bool CanAddAll(IReadOnlyList<WorldObject> items) => player.CanAddToInventory(items.ToList());

        public bool TryAddToPack(WorldObject wo)
        {
            if (!player.TryAddToInventory(wo, out var container))
                return false;

            addedTo[wo.Guid.Full] = container;
            return true;
        }

        public void UndoAdd(WorldObject wo)
        {
            player.TryRemoveFromInventory(wo.Guid, out _);
            addedTo.Remove(wo.Guid.Full);
            Discard(wo);
        }

        /// <summary>The network and save half of Player.TryCreateInInventoryWithNetworking, for an item already added.</summary>
        public void CommitAdd(WorldObject wo)
        {
            addedTo.TryGetValue(wo.Guid.Full, out var container);

            player.Session.Network.EnqueueSend(new GameMessageCreateObject(wo));
            player.Session.Network.EnqueueSend(
                new GameEventItemServerSaysContainId(player.Session, wo, container ?? player),
                new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.EncumbranceVal, player.EncumbranceVal ?? 0));

            wo.SaveBiotaToDatabase();
        }
    }
}
