using System.Collections.Generic;
using System.Threading;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // The web class ability panel's Player half (Docs/Market/market-api-v1.yaml, .../class-abilities/panel).
        // The route and its world-thread service live in ACE.Server.Managers.Market; everything here is either
        // thread-safe by construction (the revision) or runs on the world thread (the Can* checks).

        private ClassAbilityRevisionCounter classAbilityRevision;

        /// <summary>
        /// Bumped by <see cref="AdjustClassAbilityPoints"/> and <see cref="InvalidateClassAbilityHookCaches"/>.
        /// Random epoch per Player instance, so a relog always answers a new revision.
        ///
        /// Created on first use rather than by a field initializer, with a CompareExchange so a request thread
        /// and the world thread racing to create it agree on one instance: a Player built without its
        /// constructor (the test harness's uninitialized objects) never runs field initializers, and
        /// InvalidateClassAbilityHookCaches must not throw on one.
        /// </summary>
        internal ClassAbilityRevisionCounter ClassAbilityRevision
        {
            get
            {
                var current = Volatile.Read(ref classAbilityRevision);
                if (current != null)
                    return current;

                Interlocked.CompareExchange(ref classAbilityRevision, new ClassAbilityRevisionCounter(), null);
                return Volatile.Read(ref classAbilityRevision);
            }
        }

        /// <summary>The panel's opaque revision string. Safe from any thread (no world hop).</summary>
        public string ClassAbilityPanelRevision => ClassAbilityRevision.Current;

        /// <summary>A copy of the live rank cache (owned ranks by ability id). World thread.</summary>
        public Dictionary<ClassAbilityId, int> ClassAbilityRanksSnapshot() => new Dictionary<ClassAbilityId, int>(GetClassAbilityCache());

        /// <summary>
        /// The ability ids this character holds an unused training token for (PropertyInt.ClassAbilityTokenId on
        /// any possession) - the set form of ClassAbilityTrainer.HoldsVoucherFor, so one panel snapshot walks the
        /// possessions once instead of once per ability. World thread.
        /// </summary>
        public HashSet<int> ClassAbilityVoucherIdsHeld()
        {
            var held = new HashSet<int>();

            foreach (var wo in GetAllPossessions())
            {
                var id = wo.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0;
                if (id > 0)
                    held.Add(id);
            }

            return held;
        }

        /// <summary>
        /// Side-effect-free: would a panel RAISE of <paramref name="skill"/> by one rank apply right now? Runs the
        /// exact validator <see cref="LearnClassAbility"/> runs (<see cref="ValidateClassAbilityLearn"/>), then the
        /// class trainer's "already holding an unused voucher" refusal. <paramref name="heldVouchers"/> is an
        /// optional precomputed <see cref="ClassAbilityVoucherIdsHeld"/>; null walks the possessions through
        /// ClassAbilityTrainer.HoldsVoucherFor. Does not check class_abilities_enabled (the route does). World thread.
        /// </summary>
        public bool CanRaiseClassAbility(ClassAbilityDefinition skill, out string refusal, ISet<int> heldVouchers = null)
        {
            if (!ValidateClassAbilityLearn(skill, out _, out _, out refusal))
                return false;

            var holds = heldVouchers != null
                ? heldVouchers.Contains((int)skill.Id)
                : ClassAbilityTrainer.HoldsVoucherFor(this, skill.Id);

            if (holds)
            {
                refusal = ClassAbilityTrainer.VoucherHeldRefusal(skill);
                return false;
            }

            refusal = null;
            return true;
        }

        /// <summary>
        /// Side-effect-free: would a panel LOWER of <paramref name="skill"/> by one rank apply right now? Runs the
        /// exact validator <see cref="LowerClassAbilityRank"/> and <see cref="UnlearnClassAbility"/> run
        /// (<see cref="ValidateClassAbilityRankRemoval"/>), then the removal fee's affordability with the same
        /// sentence the charge refuses with. A lower that refuses is never offered as an unlearn either: an
        /// unlearn removes MORE ranks, so it can only strand more. World thread.
        /// </summary>
        public bool CanLowerClassAbility(ClassAbilityDefinition skill, out string refusal)
        {
            if (!ValidateClassAbilityRankRemoval(skill, lowerOneRank: true, CapLedgerReason.Lower, out _, out _, out refusal))
                return false;

            return CanAffordClassAbilityRemovalFee(out refusal);
        }

        /// <summary>
        /// Side-effect-free: would a panel UNLEARN (to_rank 0) of <paramref name="skill"/> apply right now? The exact
        /// validator <see cref="UnlearnClassAbility"/> runs (so a strand refusal says "unlearn"), then the fee's
        /// affordability. World thread.
        /// </summary>
        public bool CanUnlearnClassAbility(ClassAbilityDefinition skill, out string refusal)
        {
            if (!ValidateClassAbilityRankRemoval(skill, lowerOneRank: false, CapLedgerReason.Unlearn, out _, out _, out refusal))
                return false;

            return CanAffordClassAbilityRemovalFee(out refusal);
        }

        /// <summary>The removal fee's affordability, with the same rule and sentence <see cref="TryChargeClassAbilityRemovalFee"/> charges by.</summary>
        private bool CanAffordClassAbilityRemovalFee(out string refusal)
        {
            var fee = ClassAbilityRemovalFee();
            var spendable = GetSpendableLuminance();

            if (fee > 0 && spendable < fee)
            {
                refusal = ClassAbilityRemovalFeeRefusal(fee, spendable);
                return false;
            }

            refusal = null;
            return true;
        }
    }
}
