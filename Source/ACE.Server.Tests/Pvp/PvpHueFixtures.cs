using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>Shared fixtures for the team hue coordinator tests that live in the battleground test classes (their rigs are private).</summary>
    internal static class PvpHueFixtures
    {
        public const uint ChestCb = 0x1000018E;

        public static readonly int PaletteKey = (int)PropertyInt.PaletteTemplate;

        /// <summary>The chest ClothingBase lists 2, 14, 85, 88, 90.</summary>
        public static IReadOnlyCollection<uint> Palettes(uint clothingBase) => clothingBase == ChestCb ? new uint[] { 2, 14, 85, 88, 90 } : null;

        public static uint Icon(uint clothingBase, uint palette) => 0x06000000u + palette;

        public static PvpTeamHueDials Dials() => new PvpTeamHueDials(true,
            new PvpTeamHueLook(85, 0.0), new PvpTeamHueLook(88, 0.0), new PvpTeamHueLook(90, 0.0),
            new Dictionary<(uint, KitHue), PvpTeamHueLook>());

        /// <summary>The same dials with the Tugak palette pool set: a mode other than Tugak must ignore it entirely.</summary>
        public static PvpTeamHueDials DialsWithFfaPool() => Dials() with { FfaPalettes = new[] { 2, 14, 90 }, FfaShade = 0.4 };

        /// <summary>A template row whose kit is one worn chest piece with its own palette 14.</summary>
        public static PvpTemplateRecord Row(string key, string modes)
        {
            var chest = new PvpTemplateKitItem { WeenieClassId = 1001, WieldLocation = 0x200 };
            chest.DataIds[(int)PropertyDataId.ClothingBase] = ChestCb;
            chest.Ints[PaletteKey] = 14;
            chest.Floats[(int)PropertyFloat.Shade] = 0.5;

            var definition = new PvpTemplateDefinition { Key = key, Version = 1, DisplayName = "Duelist" };
            definition.Kit.Add(chest);

            return new PvpTemplateRecord
            {
                TemplateKey = key,
                DisplayName = "Duelist",
                Version = 1,
                Enabled = true,
                Modes = modes,
                DefinitionJson = PvpTemplateJson.SerializeDefinition(definition),
                SnapshotAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                SnapshotBy = "test",
            };
        }

        public static int ChestPalette(PvpTemplateDefinition d) => d.Kit[0].Ints[PaletteKey];

        public static string Json(PvpTemplateDefinition d) => PvpTemplateJson.SerializeDefinition(d);
    }
}