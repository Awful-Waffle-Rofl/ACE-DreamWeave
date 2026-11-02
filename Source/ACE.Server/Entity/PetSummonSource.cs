using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Everything one summon needs from the thing that summons it: which pet weenie to create, the six gear
    /// ratings the pet inherits, and the (optional) bound spells a caster pet is given. A retail essence yields
    /// one source from its own PetClass and ratings; a device that carries two different pets (the Duo Gem)
    /// yields two. Pure data - CombatPet.ApplySummonStats consumes it without touching the world.
    /// </summary>
    public record PetSummonSource(
        uint PetWcid,
        int? GearDamage,
        int? GearDamageResist,
        int? GearCrit,
        int? GearCritResist,
        int? GearCritDamage,
        int? GearCritDamageResist,
        uint? BoundSpell1,
        uint? BoundSpell2)
    {
        /// <summary>
        /// The source a single device describes on its own: PetClass as the pet, the six gear ratings it
        /// carries today, and the bound spells (PropertyDataId.SummonBoundSpell1/2) if any. A retail device has
        /// no bound spells, so both come out null and the pet is exactly what it was before this seam existed.
        /// </summary>
        public static PetSummonSource FromDevice(PetDevice device)
        {
            return new PetSummonSource(
                (uint)(device.PetClass ?? 0),
                device.GearDamage,
                device.GearDamageResist,
                device.GearCrit,
                device.GearCritResist,
                device.GearCritDamage,
                device.GearCritDamageResist,
                device.GetProperty(PropertyDataId.SummonBoundSpell1),
                device.GetProperty(PropertyDataId.SummonBoundSpell2));
        }

        /// <summary>True when the pet is a caster: it carries at least one bound spell.</summary>
        public bool HasBoundSpells => BoundSpell1.HasValue || BoundSpell2.HasValue;
    }
}
