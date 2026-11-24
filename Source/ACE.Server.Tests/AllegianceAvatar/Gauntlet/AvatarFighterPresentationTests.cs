using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The fighter presents as an NPC (owner ruling 2026-11-23): the create a client receives says it is not attackable and
    /// carries a yellow radar blip, while monsters still take it as a target and hit it, and players and their pets still
    /// cannot. The client-side half is read back from the serialized create header (SerializeGameDataOnly), not from the
    /// property. Dat-free; no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class AvatarFighterPresentationTests
    {
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            EnsureInitialized();

            // WriteString16L encodes names as code page 1252, which .NET Core only has through the code-pages provider
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        /// <summary>The create header the client parses: weenie header flags and the ObjectDescriptionFlags word.</summary>
        internal static (WeenieHeaderFlag weenieFlags, ObjectDescriptionFlag descFlags) ReadCreateHeader(WorldObject wo)
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                wo.SerializeGameDataOnly(writer);

            ms.Position = 0;
            using var reader = new BinaryReader(ms);

            reader.ReadUInt32();                                     // guid
            var weenieFlags = (WeenieHeaderFlag)reader.ReadUInt32();
            var nameLength = reader.ReadUInt16();
            reader.ReadBytes(nameLength);
            var used = (2 + nameLength) % 4;
            if (used != 0) reader.ReadBytes(4 - used);               // String16L pads to 4 with its length
            ReadPacked(reader);                                      // wcid
            ReadPacked(reader);                                      // icon (known type)
            reader.ReadUInt32();                                     // ItemType
            var descFlags = (ObjectDescriptionFlag)reader.ReadUInt32();

            return (weenieFlags, descFlags);
        }

        private static void ReadPacked(BinaryReader reader)
        {
            var first = reader.ReadUInt16();
            if ((first & 0x8000) != 0)
                reader.ReadUInt16();
        }

        [TestMethod]
        public void TheClientIsToldItIsNotAttackable_TheServerKeepsAttackable()
        {
            var fighter = Fighter();

            Assert.IsTrue(fighter.Attackable, "the server keeps Attackable: monster targeting and the damage paths read it");
            Assert.IsFalse(ReadCreateHeader(fighter).descFlags.HasFlag(ObjectDescriptionFlag.Attackable), "the create sent to clients has no Attackable bit");

            Assert.IsTrue(ReadCreateHeader(Monster()).descFlags.HasFlag(ObjectDescriptionFlag.Attackable), "control: an ordinary monster's create keeps it");
        }

        [TestMethod]
        public void TheClientGetsAYellowNpcBlip_AndTheShopkeepersPkStatus()
        {
            var fighter = Fighter();

            Assert.IsTrue(ReadCreateHeader(fighter).weenieFlags.HasFlag(WeenieHeaderFlag.RadarBlipColor), "the create carries a radar colour");
            Assert.AreEqual(RadarColor.Yellow, fighter.RadarColor, "and it is the NPC yellow");
            Assert.AreEqual(PlayerKillerStatus.RubberGlue, fighter.PlayerKillerStatus);
            Assert.IsFalse(ReadCreateHeader(fighter).descFlags.HasFlag(ObjectDescriptionFlag.PlayerKiller));

            Assert.IsFalse(ReadCreateHeader(Monster()).weenieFlags.HasFlag(WeenieHeaderFlag.RadarBlipColor), "control: a monster with no colour sends none");
        }

        [TestMethod]
        public void ClientAttackable_ChangesOnlyTheFighter()
        {
            Assert.IsFalse(GauntletAvatarFighter.ClientAttackable(Fighter(), true));
            Assert.IsTrue(GauntletAvatarFighter.ClientAttackable(Monster(), true));
            Assert.IsFalse(GauntletAvatarFighter.ClientAttackable(Monster(), false));
            Assert.IsTrue(GauntletAvatarFighter.ClientAttackable(PlayerPet(), true), "a player's own pet is unchanged");
        }

        [TestMethod]
        public void MonstersStillTargetAndDamageIt_WithTheNewPresentation()
        {
            var monster = Monster();
            var fighter = Fighter();

            Assert.AreEqual(PlayerKillerStatus.RubberGlue, fighter.PhysicsObj.WeenieObj.PlayerKillerStatus, "fixture: physics sees the new status");

            monster.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { fighter.PhysicsObj });

            Assert.AreEqual(1, monster.PhysicsObj.ObjMaint.GetVisibleTargetsCount(), "a monster still admits the fighter as a target");
            Assert.IsTrue(fighter.Attackable, "GetAttackTargets' first filter (server Attackable) still passes it");
            Assert.IsTrue(monster.CanDamage(fighter), "a monster's hits still land");
        }

        [TestMethod]
        public void PlayersAndTheirPetsStillCannotHitIt()
        {
            var fighter = Fighter();

            Assert.IsFalse(BarePlayer().CanDamage(fighter), "a player's attack is refused");
            Assert.IsTrue(BarePlayer().CanDamage(Monster()), "control");
            Assert.IsFalse(PlayerPet().CanDamage(fighter), "a player's pet is refused");
        }
    }
}
