using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>The Avatar look records and their JSON form. Pure: no world, no database, no PropertyManager reads.</summary>
    [TestClass]
    public class AvatarLookTests
    {
        internal static AvatarLook FullLook(DateTime? at = null)
        {
            var body = new AvatarLookBody(
                new Dictionary<PropertyInt, int> { { PropertyInt.HeritageGroup, 1 }, { PropertyInt.Gender, 2 }, { PropertyInt.Hairstyle, 5 } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, 1.05 } },
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.HeadObject, 0x01000AAAu }, { PropertyDataId.Setup, 0x02000001u } },
                new Dictionary<PropertyString, string> { { PropertyString.Sex, "Female" }, { PropertyString.HeritageGroup, "Viamontian" } },
                0x05001111u, 0x05002222u, MotionStance.SwordCombat, MotionCommand.ATOYOT);

            var piece = new AvatarLookPiece(
                1234u, EquipMask.HeadWear,
                new Dictionary<PropertyInt, int> { { PropertyInt.PaletteTemplate, 85 } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.Shade, 0.8 } },
                new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, 0x10000AAAu }, { PropertyDataId.VisualEffectScript, 0x33001234u } },
                new Dictionary<PropertyBool, bool> { { PropertyBool.IgnoreCloIcons, true } },
                new List<AvatarLookObjDescRow>
                {
                    new AvatarLookObjDescRow(AvatarLookRowKind.Texture, 9, 0x05002C23u, 0x05002D52u),
                    new AvatarLookObjDescRow(AvatarLookRowKind.Palette, 0x04001234u, 8, 16),
                },
                "Koujia Helm",
                new Dictionary<PropertyString, string> { { PropertyString.WornModelOverrides, "x" } });

            return new AvatarLook(AvatarLookJson.CurrentVersion, at ?? new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc), body, new List<AvatarLookPiece> { piece });
        }

        [TestMethod]
        public void Json_round_trips_a_full_look()
        {
            var look = FullLook();

            var json = AvatarLookJson.Serialize(look);
            var back = AvatarLookJson.Parse(json);

            Assert.IsNotNull(back);
            // record equality here is by content: the records override Equals for their Dictionaries
            Assert.AreEqual(look, back);
            Assert.AreEqual(look.DressedAtUtc, back.DressedAtUtc);
            Assert.AreEqual(MotionCommand.ATOYOT, back.Body.Pose);
            Assert.AreNotEqual(look.Body, back.Body with { Pose = null }, "Pose is part of body equality");

            // a look stored before Pose existed has no Pose key and still parses, with a null Pose
            var old = AvatarLookJson.Parse(json.Replace(",\"Pose\":1107296505", ""));
            Assert.IsNotNull(old);
            Assert.IsNull(old.Body.Pose);
            // enum keys are written as numeric ids, so a renamed member never breaks a stored look
            StringAssert.Contains(json, "\"188\":1");

            // and the equality is real: one changed value breaks it
            back.Pieces[0].Ints[PropertyInt.PaletteTemplate] = 86;
            Assert.AreNotEqual(look, back);
        }

        [TestMethod]
        public void Json_round_trips_reset_look()
        {
            var reset = new AvatarLook(AvatarLookJson.CurrentVersion, new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc), null, new List<AvatarLookPiece>());

            var back = AvatarLookJson.Parse(AvatarLookJson.Serialize(reset));

            Assert.IsNotNull(back);
            Assert.IsNull(back.Body);
            Assert.AreEqual(0, back.Pieces.Count);
            Assert.AreEqual(reset, back);
        }

        [TestMethod]
        public void Parse_rejects_corrupt_unknown_version_and_oversize()
        {
            Assert.IsNull(AvatarLookJson.Parse("{"));
            Assert.IsNull(AvatarLookJson.Parse(null));
            Assert.IsNull(AvatarLookJson.Parse(""));

            var v2 = AvatarLookJson.Serialize(FullLook() with { Version = 2 });
            Assert.IsNull(AvatarLookJson.Parse(v2));

            Assert.IsNull(AvatarLookJson.Parse(new string(' ', AvatarLookJson.MaxBytes + 1)));
            Assert.AreEqual(16384, AvatarLookJson.MaxBytes);
        }

        [TestMethod]
        public void Parse_rejects_structurally_incomplete_looks()
        {
            Assert.IsNull(AvatarLookJson.Parse("{\"Version\":1,\"Pieces\":[],\"Body\":{}}"));
            Assert.IsNull(AvatarLookJson.Parse("{\"Version\":1,\"Pieces\":[null],\"Body\":null}"));
            Assert.IsNull(AvatarLookJson.Parse("{\"Version\":1,\"Pieces\":[{\"Wcid\":1}],\"Body\":null}"));
            Assert.IsNotNull(AvatarLookJson.Parse("{\"Version\":1,\"Pieces\":[],\"Body\":null}"));
        }

        [TestMethod]
        public void Parse_accepts_a_look_exactly_at_the_limit_boundary_only_when_valid()
        {
            // padding with whitespace up to MaxBytes keeps a valid look valid
            var json = AvatarLookJson.Serialize(FullLook());
            var padded = json + new string(' ', AvatarLookJson.MaxBytes - json.Length);
            Assert.IsNotNull(AvatarLookJson.Parse(padded));
        }
    }
}
