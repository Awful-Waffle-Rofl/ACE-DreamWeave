using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>The six gear ratings a summon item carries. An absent rating is 0.</summary>
    public readonly record struct SummonRatings(int Damage, int DamageResist, int Crit, int CritResist, int CritDamage, int CritDamageResist)
    {
        public static SummonRatings Read(WorldObject wo)
        {
            return new SummonRatings(
                wo.GearDamage ?? 0,
                wo.GearDamageResist ?? 0,
                wo.GearCrit ?? 0,
                wo.GearCritResist ?? 0,
                wo.GearCritDamage ?? 0,
                wo.GearCritDamageResist ?? 0);
        }

        /// <summary>Writes all six, zeros included, so a zero REPLACES whatever the object carried.</summary>
        public void WriteTo(WorldObject wo)
        {
            wo.GearDamage = Damage;
            wo.GearDamageResist = DamageResist;
            wo.GearCrit = Crit;
            wo.GearCritResist = CritResist;
            wo.GearCritDamage = CritDamage;
            wo.GearCritDamageResist = CritDamageResist;
        }
    }

    public enum DaggerTarget { NotASummon, Summon, DuoGem, Dagger, PetOut }

    public enum DaggerDecision { Imprint, Apply, RefusedDuoGem, RefusedNotASummon, RefusedDagger, RefusedPetOut }

    /// <summary>
    /// The Sacrificial Dagger (Custom Summons Task 5). A non-wieldable Gem flagged PropertyBool.SacrificialDagger.
    ///
    /// Unimprinted, used on a summon item (retail or custom): destroys that summon and copies its six ratings onto
    /// the dagger, zeros included, and marks the dagger SummonImprinted. Imprinted, used on a summon item: replaces
    /// all six of the target's ratings with the imprint (never adds), then the dagger is destroyed. A Duo Gem is
    /// refused in both states (the hammer comes first), as is a dagger used on a dagger or a non-summon.
    ///
    /// The decision and the rating copy are pure. <see cref="UseOnTarget"/> is the Player-facing shell around them.
    /// </summary>
    public static class SacrificialDagger
    {
        public const string RefusedDuoGemText = "The dagger cannot be used on a Duo Summon Gem. Break the gem with a Summon Gem Hammer first.";
        public const string RefusedNotASummonText = "The Sacrificial Dagger only works on a summoning item.";
        public const string NoTargetText = "The Sacrificial Dagger must be used on a summoning item.";
        public const string RefusedDaggerText = "A Sacrificial Dagger cannot be used on another dagger.";
        public const string RefusedPetOutText = "Dismiss or wait out that summon's creature before using the dagger on it.";

        public static bool IsDagger(WorldObject wo) => wo != null && wo.GetProperty(PropertyBool.SacrificialDagger) == true;

        public static bool IsImprinted(WorldObject wo) => wo != null && wo.GetProperty(PropertyBool.SummonImprinted) == true;

        public static DaggerTarget KindOf(WorldObject target)
        {
            if (IsDagger(target))
                return DaggerTarget.Dagger;

            if (target.GetProperty(PropertyBool.DuoSummonGem) == true)
                return DaggerTarget.DuoGem;

            if (!(target is PetDevice device))
                return DaggerTarget.NotASummon;

            // same tests as the Duo Gem (DuoSummonGem.KindOf): a passive-pet essence never qualifies, and a device
            // whose pet is out is refused (trade, give, drop and vault refuse it too)
            if (device.PetClass == null || DuoSummonGem.PetWeenieTypeLookup((uint)device.PetClass.Value) != WeenieType.CombatPet)
                return DaggerTarget.NotASummon;

            if (device.Pet != null)
                return DaggerTarget.PetOut;

            return DaggerTarget.Summon;
        }

        public static DaggerDecision Decide(bool daggerImprinted, DaggerTarget target)
        {
            switch (target)
            {
                case DaggerTarget.Dagger: return DaggerDecision.RefusedDagger;
                case DaggerTarget.PetOut: return DaggerDecision.RefusedPetOut;
                case DaggerTarget.DuoGem: return DaggerDecision.RefusedDuoGem;
                case DaggerTarget.NotASummon: return DaggerDecision.RefusedNotASummon;
                default: return daggerImprinted ? DaggerDecision.Apply : DaggerDecision.Imprint;
            }
        }

        /// <summary>Copies the summon's six ratings (absent = 0) onto the dagger and marks it imprinted.</summary>
        public static void Imprint(WorldObject dagger, WorldObject summon)
        {
            SummonRatings.Read(summon).WriteTo(dagger);
            dagger.SetProperty(PropertyBool.SummonImprinted, true);
        }

        /// <summary>Replaces all six of the target's ratings with the dagger's imprint.</summary>
        public static void ApplyTo(WorldObject dagger, WorldObject target)
        {
            SummonRatings.Read(dagger).WriteTo(target);
        }

        /// <summary>
        /// Entry point from Gem.HandleActionUseOnTarget. Every exit sends the use-done event itself (the caller
        /// sends none). Refusals consume nothing.
        /// </summary>
        public static void UseOnTarget(Player player, Gem dagger, WorldObject target)
        {
            DoUse(player, dagger, target);
            player.SendUseDoneEvent();
        }

        private static void DoUse(Player player, Gem dagger, WorldObject target)
        {
            var decision = Decide(IsImprinted(dagger), KindOf(target));

            switch (decision)
            {
                case DaggerDecision.RefusedDuoGem: Say(player, RefusedDuoGemText); return;
                case DaggerDecision.RefusedNotASummon: Say(player, RefusedNotASummonText); return;
                case DaggerDecision.RefusedDagger: Say(player, RefusedDaggerText); return;
                case DaggerDecision.RefusedPetOut: Say(player, RefusedPetOutText); return;
            }

            // both items must be in the player's own pack (same rule the essence refill uses)
            if (PetDevice.VerifyUseRequirements(player, dagger, target) != WeenieError.None)
            {
                Say(player, "Both the dagger and the summoning item must be in your pack.");
                return;
            }

            if (decision == DaggerDecision.Imprint)
            {
                // imprint first, while the target still exists to be read; undo if the target cannot be consumed
                var daggerBefore = SummonRatings.Read(dagger);

                Imprint(dagger, target);

                if (!player.TryConsumeFromInventoryWithNetworking(target, 1))
                {
                    daggerBefore.WriteTo(dagger);
                    dagger.RemoveProperty(PropertyBool.SummonImprinted);
                    Say(player, "The dagger could not take the summon.");
                    return;
                }

                dagger.SaveBiotaToDatabase();

                Refresh(player, dagger);

                Say(player, "The summon crumbles and its ratings are drawn into the dagger.");
            }
            else
            {
                var before = SummonRatings.Read(target);

                ApplyTo(dagger, target);

                if (!player.TryConsumeFromInventoryWithNetworking(dagger, 1))
                {
                    // the dagger is still in the pack: put the target back as it was
                    before.WriteTo(target);
                    Say(player, "The dagger could not be spent.");
                    return;
                }

                target.SaveBiotaToDatabase();

                Refresh(player, target);

                Say(player, $"The dagger's ratings replace those of the {target.Name}, and the dagger is spent.");
            }
        }

        /// <summary>Rewrites the description and pushes a full object update so the new ratings and text reach the client.</summary>
        private static void Refresh(Player player, WorldObject item)
        {
            // retail essences keep their flavor LongDesc (their ratings already show in appraisal)
            if (SummonItemDescription.IsCustomSummon(item))
                player.UpdateProperty(item, PropertyString.LongDesc, SummonItemDescription.Compose(item));

            player.EnqueueBroadcast(new GameMessageUpdateObject(item));
        }

        private static void Say(Player player, string text)
        {
            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
