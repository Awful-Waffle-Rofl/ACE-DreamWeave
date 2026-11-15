using System;
using System.Collections.Concurrent;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Fray Effigies and the cosmetic follower pet. The look uses DreamWeave's existing effigy models, so you can summon
    /// a pet to show it off and collect your own codex.
    ///
    /// A Fray Effigy (dreamweave-content 2000094, a copy of the Beast Effigy) drops already filled with one family's form:
    /// MuleFormWcid set and Structure full, so the existing Beast Effigy flow can also reshape a /mule with it. Using it
    /// records the family in the Codex ("effigy:key"), which unlocks the form for /fray pet. The pet is a retail passive pet
    /// (the Baby Drudge, 33918, called through its device 33919) built wearing the family creature's body with the mule's
    /// own look code (MuleFormBody.CloneWithDonorBody: physics and motion match the body). It cannot fight, be fought or
    /// loot; like a retail passive pet it takes the pet slot, so calling it dismisses a combat pet.
    /// </summary>
    public static class FraysEffigies
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const uint EffigyWcid = 2000094;
        public const uint PassivePetWcid = 33918;   // Pet Baby Drudge (retail passive pet)
        public const uint PetDeviceWcid = 33919;    // Baby Drudge Crate (its device)

        /// <summary>The pet stands about this tall, whatever the creature (MuleFormBody scales the body to it).</summary>
        public const float PetHeight = 1.0f;

        private static readonly ConcurrentDictionary<uint, Pet> pets = new ConcurrentDictionary<uint, Pet>();

        /// <summary>The creature a family's effigy and pet wear.</summary>
        public static uint FormWcid(FraysFamily family) => family == null ? 0 : family.WcidFor(FraysRole.Standard);

        /// <summary>A filled Fray Effigy of <paramref name="family"/> into the player's pack. Returns false when it could not be made or placed.</summary>
        public static bool Give(Player player, FraysFamily family, string why)
        {
            var wcid = FormWcid(family);

            if (player == null || wcid == 0)
                return false;

            var item = WorldObjectFactory.CreateNewWorldObject(EffigyWcid);

            if (item == null)
            {
                log.Error($"[FRAYS] weenie {EffigyWcid} is not in the world database; import C:\\dev\\dreamweave-content\\2000094-fray-effigy.sql");
                return false;
            }

            item.SetProperty(PropertyInt.MuleFormWcid, (int)wcid);
            item.Structure = item.MaxStructure ?? 100;
            item.Name = $"Fray Effigy of the {family.Name}";

            if (!player.TryCreateInInventoryWithNetworking(item))
            {
                item.Destroy();
                FraysManager.Tell(player, $"Your pack is full: a Fray Effigy of the {family.Name} was lost. Make room before the next Snarl.");
                return false;
            }

            FraysManager.Tell(player, why);
            return true;
        }

        /// <summary>Using a Fray Effigy (a plain use, no target): records its family in the Codex.</summary>
        public static void OnUse(Player player, WorldObject item)
        {
            var wcid = (uint)(item.GetProperty(PropertyInt.MuleFormWcid) ?? 0);

            if (FraysFamilies.Of(wcid) is not FraysFamily family)
                return;

            var rec = FraysManager.Record(player);

            if (FraysCodex.Note(rec, FraysCodex.Effigy + family.Key))
            {
                FraysManager.Tell(player, $"Fray Codex: the {family.Name} form is yours ({FraysCodex.Count(rec, FraysCodex.Effigy)} of {FraysCodex.FamiliesTotal}). Call it with /fray pet {family.Key}, and make it a companion with /frays companion {family.Key} warder, mender or striker (the style is locked once chosen).");
                FraysManager.Shout(player, $"Collected: {family.Name}");
                FraysFire.Achieve(player, rec);
            }
        }

        /// <summary>/fray pet [family | off]: calls (or sends away) your follower in a collected form.</summary>
        public static string Pet(Player player, string arg)
        {
            var rec = FraysManager.Record(player);
            var owned = FraysFamilies.All.Where(f => rec.Codex.Contains(FraysCodex.Effigy + f.Key)).ToList();
            arg = (arg ?? "").Trim().ToLowerInvariant();

            if (arg == "off" || arg == "dismiss")
            {
                Dismiss(player);
                return "Your follower fades.";
            }

            if (arg.Length == 0)
                return owned.Count == 0 ? "You have no forms yet: use a Fray Effigy (Snarls drop them) to collect one."
                    : $"Your forms: {string.Join(", ", owned.Select(f => $"{f.Name} ({f.Key})"))}. /fray pet <name> calls one, /fray pet off sends it away.";

            var family = owned.FirstOrDefault(f => f.Key.Equals(arg, StringComparison.OrdinalIgnoreCase))
                ?? owned.FirstOrDefault(f => f.Name.ToLowerInvariant().Contains(arg));

            if (family == null)
                return $"You have not collected that form. Yours: {(owned.Count == 0 ? "none yet" : string.Join(", ", owned.Select(f => f.Key)))}.";

            // Calling a styled form makes it the active companion.
            if (rec.Companions.TryGetValue(family.Key, out var form) && !string.IsNullOrEmpty(form.Role) && rec.ActiveCompanion != family.Key)
            {
                rec.ActiveCompanion = family.Key;
                FraysManager.SaveRecord(player, rec);
                FraysManager.Tell(player, $"Your {family.Name} ({form.Role}, level {FraysCompanions.Level(form.Xp)}) is your companion now.");
            }

            return Summon(player, family);
        }

        /// <summary>
        /// The pet keeps up with its owner: a small creature body runs
        /// at its own slow pace, shrunk by its scale, so its run rate is raised until it runs a little faster than the owner.
        /// </summary>
        private static void KeepPace(Pet pet, Player owner)
        {
            try
            {
                pet.FraysRunRateMult = 1.0f;

                var petRun = ACE.Server.Physics.Animation.MotionTable.GetRunSpeed(pet.MotionTableId);
                var ownerRun = ACE.Server.Physics.Animation.MotionTable.GetRunSpeed(owner.MotionTableId);
                var petSpeed = (petRun > 0 ? petRun : 2.5f) * pet.GetRunRate() * (pet.ObjScale ?? 1.0f);
                var ownerSpeed = (ownerRun > 0 ? ownerRun : 4.0f) * owner.GetRunRate();

                if (petSpeed > 0)
                    pet.FraysRunRateMult = Math.Clamp(ownerSpeed * 1.15f / petSpeed, 1.0f, 8.0f);

                pet.MoveSpeed = 0;   // recomputed with the new rate on its next move
                pet.GetMovementSpeed();
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] pacing {owner.Name}'s pet threw", ex);
            }
        }

        private static string Summon(Player player, FraysFamily family)
        {
            try
            {
                var baseWeenie = DatabaseManager.World.GetCachedWeenie(PassivePetWcid);
                var donor = DatabaseManager.World.GetCachedWeenie(FormWcid(family));
                var body = MuleFormBody.CloneWithDonorBody(baseWeenie, donor, PetHeight, MuleFormToken.DatSetupHeight);

                if (body == null)
                    return "That form will not take shape as a pet.";

                if (WorldObjectFactory.CreateNewWorldObject(body) is not Pet pet || WorldObjectFactory.CreateNewWorldObject(PetDeviceWcid) is not PetDevice device)
                    return "Your follower could not be called (its retail pet weenies are missing).";

                Dismiss(player);
                pet.Name = family.Name;

                if (pet.Init(player, device, ACE.Server.Entity.PetSummonSource.FromDevice(device)) != true)
                {
                    pet.Destroy();
                    return "Your follower could not be called here.";
                }

                KeepPace(pet, player);
                pets[player.Guid.Full] = pet;
                FraysManager.Effect(pet, ACE.Entity.Enum.PlayScript.EnchantUpOrange);
                return $"Your {family.Name} follows you.";
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] calling a {family.Key} follower threw", ex);
                return "Your follower could not be called.";
            }
        }

        public static void Dismiss(Player player)
        {
            if (player != null && pets.TryRemove(player.Guid.Full, out var pet) && pet != null && !pet.IsDestroyed)
                pet.Destroy();
        }
    }
}
