using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>The pure Seat and summon rules. No world, no PropertyManager.</summary>
    [TestClass]
    public class AvatarSeatRulesTests
    {
        private static readonly AvatarTuning T = AvatarTuning.Defaults;
        private static readonly DateTime Now = new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc);

        private const uint Monarch = 1000;
        private const uint Keeper = 1001;
        private const uint Other = 1002;

        [TestMethod]
        public void Mansion_OwnedByMonarch_Qualifies()
        {
            Assert.IsTrue(AvatarSeatRules.Qualifies(HouseType.Mansion, Monarch, Monarch, Monarch));
            Assert.IsTrue(AvatarSeatRules.Qualifies(HouseType.Mansion, Monarch, Monarch, Keeper), "the monarch's mansion qualifies while someone else acts as Keeper");
        }

        [TestMethod]
        public void Mansion_OwnedByActingKeeper_Qualifies()
        {
            Assert.IsTrue(AvatarSeatRules.Qualifies(HouseType.Mansion, Keeper, Monarch, Keeper));
        }

        [TestMethod]
        public void Mansion_OwnerSworeAway_DoesNotQualify()
        {
            // The owner held the Keeper's rights when it was seated; now the monarch keeps it again (or the owner left).
            Assert.IsFalse(AvatarSeatRules.Qualifies(HouseType.Mansion, Keeper, Monarch, Monarch));
            Assert.IsFalse(AvatarSeatRules.Qualifies(HouseType.Mansion, Other, Monarch, Keeper));
            Assert.IsFalse(AvatarSeatRules.Qualifies(HouseType.Mansion, null, Monarch, Monarch), "an unowned mansion never qualifies");
        }

        [TestMethod]
        public void Villa_NeverQualifies()
        {
            foreach (var type in new[] { HouseType.Villa, HouseType.Cottage, HouseType.Apartment, HouseType.Undef })
                Assert.IsFalse(AvatarSeatRules.Qualifies(type, Monarch, Monarch, Monarch), type.ToString());
        }

        [TestMethod]
        public void Reseat_InsideCooldown_Refused()
        {
            var days = T.ReseatCooldownDays;

            Assert.IsTrue(AvatarSeatRules.CanReseat(null, Now, T), "never seated");
            Assert.IsFalse(AvatarSeatRules.CanReseat(Now, Now, T), "just seated");
            Assert.IsFalse(AvatarSeatRules.CanReseat(Now.AddDays(-days).AddSeconds(1), Now, T), "one second inside the cooldown");
            Assert.IsTrue(AvatarSeatRules.CanReseat(Now.AddDays(-days), Now, T), "exactly the cooldown");

            Assert.AreEqual(days, AvatarSeatRules.DaysUntilReseat(Now, Now, T));
            Assert.AreEqual(1, AvatarSeatRules.DaysUntilReseat(Now.AddDays(-days).AddSeconds(1), Now, T), "a part day still reads as one day");
            Assert.AreEqual(0, AvatarSeatRules.DaysUntilReseat(Now.AddDays(-days), Now, T));
        }

        [TestMethod]
        public void SeatPrompt_CarriesTheCooldownTunable()
        {
            Assert.AreEqual("Seat the Avatar here? You can't move it again for 7 days.", AvatarSeatRules.SeatPrompt(T));
            Assert.AreEqual("Seat the Avatar here? You can't move it again for 3 days.", AvatarSeatRules.SeatPrompt(T with { ReseatCooldownDays = 3 }));
            Assert.AreEqual("The Avatar has left the mansion at 1.0N, 2.0E and wanders again.", AvatarSeatRules.UnseatLine("the mansion at 1.0N, 2.0E"));
        }

        [TestMethod]
        public void SeatBelongsTo_MatchesLandblockAndInstance()
        {
            var seat = new AvatarSeat(0x70090123, new Position(0x00900011, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0));

            Assert.IsTrue(AvatarSeatRules.SeatBelongsTo(seat, 0x0090, 0));
            Assert.IsFalse(AvatarSeatRules.SeatBelongsTo(seat, 0x0091, 0), "another landblock");
            Assert.IsFalse(AvatarSeatRules.SeatBelongsTo(seat, 0x0090, 1), "another instance of the same landblock");
            Assert.IsFalse(AvatarSeatRules.SeatBelongsTo(null, 0x0090, 0));
        }

        [TestMethod]
        public void OwnerAmong_FindsTheMonarchOrTheKeeper_OnlyForThatHouse()
        {
            const uint house = 0x70090123;
            var owned = new Dictionary<uint, uint?> { [Monarch] = null, [Keeper] = house, [Other] = house };
            uint? Of(uint g) => owned.TryGetValue(g, out var h) ? h : null;

            Assert.AreEqual(Keeper, AvatarSeatRules.OwnerAmong(house, Monarch, Keeper, Of));
            Assert.IsNull(AvatarSeatRules.OwnerAmong(house, Monarch, Monarch, Of), "the owner is not the monarch nor the Keeper");
            owned[Monarch] = house;
            Assert.AreEqual(Monarch, AvatarSeatRules.OwnerAmong(house, Monarch, Keeper, Of));
            Assert.IsNull(AvatarSeatRules.OwnerAmong(0x70090999, Monarch, Keeper, Of), "another house");
        }

        [TestMethod]
        public void SummonArea_OutdoorsOnly_AndTheDenylist_NoAllowlist()
        {
            var outdoors = new Position(0x00900011, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);
            var indoors = new Position(0x00900105, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);
            Func<Position, bool> none = p => false;
            Func<Position, bool> deny0090 = p => p.LandblockShort == 0x0090;

            Assert.IsNull(AvatarSeatRules.SummonAreaRefusal(outdoors, none), "any outdoor landblock off the denylist is allowed");
            Assert.AreEqual(AvatarSeatRules.SummonOutdoorsMessage, AvatarSeatRules.SummonAreaRefusal(indoors, none));
            Assert.AreEqual("The Avatar can only be summoned outdoors.", AvatarSeatRules.SummonOutdoorsMessage);
            Assert.AreEqual(AvatarSeatRules.SummonOutdoorsMessage, AvatarSeatRules.SummonAreaRefusal(null, none));
            Assert.AreEqual(AvatarSeatRules.SummonDenylistedMessage, AvatarSeatRules.SummonAreaRefusal(outdoors, deny0090));

            Assert.IsTrue(AvatarSeatRules.IsPlacementRungPermitted(outdoors, none));
            Assert.IsFalse(AvatarSeatRules.IsPlacementRungPermitted(indoors, none));
            Assert.IsFalse(AvatarSeatRules.IsPlacementRungPermitted(outdoors, deny0090));
        }
    }
}
