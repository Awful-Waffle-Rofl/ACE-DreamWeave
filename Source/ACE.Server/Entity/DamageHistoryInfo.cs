using System;

using ACE.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    public class DamageHistoryInfo
    {
        public readonly WeakReference<WorldObject> Attacker;

        public readonly ObjectGuid Guid;
        public readonly string Name;

        public float TotalDamage;

        public readonly WeakReference<Player> PetOwner;

        public bool IsPlayer => Guid.IsPlayer();

        public readonly bool IsOlthoiPlayer;

        public DamageHistoryInfo(WorldObject attacker, float totalDamage = 0.0f)
        {
            Attacker = new WeakReference<WorldObject>(attacker);

            Guid = attacker.Guid;
            Name = attacker.Name;

            IsOlthoiPlayer = attacker is Player player && player.IsOlthoiPlayer;

            TotalDamage = totalDamage;

            if (attacker is CombatPet combatPet && combatPet.P_PetOwner != null)
                PetOwner = new WeakReference<Player>(combatPet.P_PetOwner);
        }

        public WorldObject TryGetAttacker()
        {
            Attacker.TryGetTarget(out var attacker);

            return attacker;
        }

        public Player TryGetPetOwner()
        {
            PetOwner.TryGetTarget(out var petOwner);

            return petOwner;
        }

        public WorldObject TryGetPetOwnerOrAttacker()
        {
            if (PetOwner != null)
                return TryGetPetOwner();
            else
                return TryGetAttacker();
        }

        /// <summary>
        /// A kill made by a CombatPet is, for rare/treasure-map eligibility, a kill made by the pet's owner:
        /// the pet is its own DamageHistory entry (keyed by its own attacker guid) and IsPlayer is false for
        /// it, so a raw killer.IsPlayer test excludes a pet kill entirely. When <paramref name="killer"/>
        /// carries a live PetOwner, this returns a new DamageHistoryInfo built from that owner instead, so
        /// every downstream IsPlayer/Guid/Name/TryGetAttacker read (the rare gate, TryGenerateRare,
        /// TryDropTreasureMap, and the Threads pooled-loot equivalents) sees the owner exactly as if the
        /// owner had made the kill. A pet whose owner reference has been collected (PetOwner non-null but
        /// TryGetPetOwner() null) falls back to the original killer unchanged - the same "vanished killer"
        /// behaviour a disconnected player killer already gets, not a special case. A non-pet killer, or a
        /// null killer, is returned as the same instance.
        /// </summary>
        public static DamageHistoryInfo ResolvePetOwnerAsKiller(DamageHistoryInfo killer)
        {
            if (killer?.PetOwner == null)
                return killer;

            var owner = killer.TryGetPetOwner();

            return owner != null ? new DamageHistoryInfo(owner) : killer;
        }
    }
}
