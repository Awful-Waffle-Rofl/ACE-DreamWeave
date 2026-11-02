using ACE.Server.KnotLoot;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Whether this player's Knot traits of <paramref name="kind"/> are suppressed: Weapon-kind traits follow the
        /// weapon-mod predicate, Equipment-kind traits the equipment-mod predicate, so the PK facet and PvP arena
        /// rules reach them with no rule of their own. Base AL, spells and set are never affected.
        /// </summary>
        public bool KnotTraitSuppressed(KnotTraitSuppressionKind kind) => kind == KnotTraitSuppressionKind.Weapon ? WeaponModSuppressed : EquipmentModSuppressed;

        /// <summary>
        /// Mirrorglass Haste: the buff cast-speed fraction (1.0 = +100%) on the equipped WAND, the same item Quick
        /// Refresh is read from. 0 when no wand is held, it lacks the trait, the mechanic is off, or Weapon-kind
        /// traits are suppressed. Read-only.
        /// </summary>
        public double GetKnotCastSpeedFraction()
        {
            return KnotTraitValue.ResolveOn(GetEquippedWand(), KnotTraitId.CastSpeed, this).Primary;
        }

        /// <summary>
        /// Overpowering Intent: the resist-check skill fraction X (0.05 = the War/Void skill counts as 1.05x) on the
        /// equipped WAND. 0 when no wand is held, it lacks the trait, the mechanic is off, or Weapon-kind traits are
        /// suppressed. Read-only.
        /// </summary>
        public double GetKnotOverpoweringIntentFraction()
        {
            return KnotTraitValue.ResolveOn(GetEquippedWand(), KnotTraitId.OverpoweringIntent, this).Primary;
        }

        /// <summary>
        /// Frugal Hand: the stamina and mana cost-reduction fraction (0.10 = -10%), summed over every equipped item
        /// that carries the trait. 0 when none does, the mechanic is off, or Equipment-kind traits are suppressed.
        /// Read-only.
        /// </summary>
        public double GetKnotFrugalFraction()
        {
            var equipped = EquippedObjects;

            if (equipped == null)
                return 0.0;

            var total = 0.0;

            foreach (var item in equipped.Values)
                total += KnotTraitValue.ResolveOn(item, KnotTraitId.FrugalHand, this).Primary;

            return total;
        }

        /// <summary>
        /// Shield of the Rose: the whole-number maximum-health penalty, summed over every WIELDED item (EquippedObjects
        /// holds wielded items only; a shield in a pack is not in it) that carries the ShieldTrade trait. Rounded the
        /// same way KnotTraitDisplay shows it, so the appraised and applied numbers agree. It is the other half of the
        /// ShieldArmorBonus trade and comes from the same ResolveOn call, so suppression or the mechanic switch zeroes
        /// both together. 0 when none is wielded. Read-only.
        /// </summary>
        public int GetKnotMaxHealthPenalty()
        {
            var equipped = EquippedObjects;

            if (equipped == null)
                return 0;

            // long accumulator, clamped on return: the sum of several items can never wrap an int
            long total = 0;

            foreach (var item in equipped.Values)
                total += (long)System.Math.Round(KnotTraitValue.ResolveOn(item, KnotTraitId.ShieldTrade, this).Secondary, System.MidpointRounding.AwayFromZero);

            return (int)System.Math.Clamp(total, 0, int.MaxValue);
        }

        /// <summary>
        /// Glass Turtle Bracelet: the signed rating added to <paramref name="kind"/>, summed over every WIELDED item that
        /// carries the WristTrade trait, each by the wrist it is worn on (its CurrentWieldedLocation). Two bracelets apply
        /// both trades and can net negative; the sum is returned as is, never floored. 0 when none is wielded, the
        /// mechanic is off, or Equipment-kind traits are suppressed. Read live on every call, never cached. Read-only.
        /// </summary>
        public int GetKnotWristRating(KnotRatingKind kind)
        {
            var equipped = EquippedObjects;

            if (equipped == null)
                return 0;

            // long accumulator, clamped on return: the signed sum of several items can never wrap an int
            long total = 0;

            foreach (var item in equipped.Values)
                total += KnotTraitCombat.WristRatingDelta(item.CurrentWieldedLocation, KnotTraitValue.ResolveOn(item, KnotTraitId.WristTrade, this), kind);

            return (int)System.Math.Clamp(total, int.MinValue, int.MaxValue);
        }

        /// <summary>
        /// The Knot max-health penalty the client was last told about (set by HandleMaxHealthUpdate). In memory only,
        /// never a property: a fresh Player starts at 0 and the first heartbeat settles it.
        /// </summary>
        private int knotMaxHealthPenaltySent;

        /// <summary>
        /// The heartbeat resync (Player.Heartbeat, after FacetPkHeartbeat). The penalty can change with no equip event -
        /// Equipment-kind suppression switching on or off, or a live setting change - so this resends max health within
        /// one heartbeat when, and only when, the resolved penalty differs from the last value sent. No item writes.
        /// </summary>
        public void KnotTraitHeartbeat()
        {
            // Caught here: an exception escaping into Player.Heartbeat would skip everything after this call.
            try
            {
                if (GetKnotMaxHealthPenalty() != knotMaxHealthPenaltySent)
                    HandleMaxHealthUpdate();
            }
            catch (System.Exception ex)
            {
                log.Error($"[KNOT] {Name} (0x{Guid.Full:X8}): the Knot trait heartbeat threw; the rest of the heartbeat is unaffected.", ex);
            }
        }
    }
}
