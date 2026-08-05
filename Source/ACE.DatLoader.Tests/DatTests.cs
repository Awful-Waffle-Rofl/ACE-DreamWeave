using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize]

namespace ACE.DatLoader.Tests
{
    [TestClass]
    public class DatTests
    {
        // the expected file counts match the end-of-retail client dats
        private static string DAT_PATH = Environment.GetEnvironmentVariable("ACE_DAT_PATH") ?? @"C:\Turbine\Asheron's Call\";

        private static string cellDatLocation = Path.Combine(DAT_PATH, "client_cell_1.dat");
        private static int expectedCellDatFileCount = 805003;

        private static string portalDatLocation = Path.Combine(DAT_PATH, "client_portal.dat");
        private static int expectedPortalDatFileCount = 79694;

        private static string localEnglishDatLocation = Path.Combine(DAT_PATH, "client_local_English.dat");
        private static int expectedLocalEnglishDatFileCount = 118;

        /// <summary>
        /// Skips the calling test when the client .dat file isn't present or can't be opened,
        /// instead of failing on machines without a client install — or while the game client
        /// is running, which holds the .dat files with write access and blocks all readers.
        /// </summary>
        private static void RequireDatFile(string location)
        {
            if (!File.Exists(location))
                Assert.Inconclusive($"Skipped: {location} not found. Set the ACE_DAT_PATH environment variable to a directory containing the client .dat files.");

            try
            {
                using (new FileStream(location, FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException ex)
            {
                Assert.Inconclusive($"Skipped: {location} could not be opened for reading ({ex.Message}). Close any running client that holds the .dat files.");
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void LoadCellDat_NoExceptions()
        {
            RequireDatFile(cellDatLocation);

            DatDatabase dat = new DatDatabase(cellDatLocation);
            int count = dat.AllFiles.Count;
            //Assert.AreEqual(ExpectedCellDatFileCount, count);
            Assert.AreEqual(expectedCellDatFileCount, count, "Insufficient files parsed from .dat.", $"{expectedCellDatFileCount}", $"{count}");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void LoadPortalDat_NoExceptions()
        {
            RequireDatFile(portalDatLocation);

            // Init our text encoding options. This will allow us to use more than standard ANSI text, which the client also supports.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            DatDatabase dat = new DatDatabase(portalDatLocation);
            int count = dat.AllFiles.Count;
            //Assert.AreEqual(expectedPortalDatFileCount, count);
            Assert.AreEqual(expectedPortalDatFileCount, count, "Insufficient files parsed from .dat.", $"{expectedPortalDatFileCount}", $"{count}");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void LoadLocalEnglishDat_NoExceptions()
        {
            RequireDatFile(localEnglishDatLocation);

            // Init our text encoding options. This will allow us to use more than standard ANSI text, which the client also supports.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            DatDatabase dat = new DatDatabase(localEnglishDatLocation);
            int count = dat.AllFiles.Count;
            //Assert.AreEqual(expectedPortalDatFileCount, count);
            Assert.AreEqual(expectedLocalEnglishDatFileCount, count, "Insufficient files parsed from .dat.", $"{expectedLocalEnglishDatFileCount}", $"{count}");
        }


        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void UnpackCellDatFiles_NoExceptions()
        {
            RequireDatFile(cellDatLocation);

            var assembly = typeof(DatDatabase).GetTypeInfo().Assembly;
            var types = assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(DatFileTypeAttribute), false).Length > 0).ToList();

            if (types.Count == 0)
                throw new Exception("Failed to locate any types with DatFileTypeAttribute.");

            DatDatabase dat = new DatDatabase(cellDatLocation);

            foreach (var kvp in dat.AllFiles)
            {
                if (kvp.Key == 0xFFFF0001) // // Iteration info
                    continue;

                if (kvp.Value.FileSize == 0) // DatFileType.LandBlock files can be empty
                    continue;

                var fileType = kvp.Value.GetFileType(DatDatabaseType.Cell);

                if ((kvp.Key & 0xFFFF) == 0xFFFE) fileType = DatFileType.LandBlockInfo;
                if ((kvp.Key & 0xFFFF) == 0xFFFF) fileType = DatFileType.LandBlock;

                //Assert.IsNotNull(fileType, $"Key: 0x{kvp.Key:X8}, ObjectID: 0x{kvp.Value.ObjectId:X8}, FileSize: {kvp.Value.FileSize}, BitFlags:, 0x{kvp.Value.BitFlags:X8}");
                Assert.IsNotNull(fileType, $"Key: 0x{kvp.Key:X8}, ObjectID: 0x{kvp.Value.ObjectId:X8}, FileSize: {kvp.Value.FileSize}");

                var type = types
                    .SelectMany(m => m.GetCustomAttributes(typeof(DatFileTypeAttribute), false), (m, a) => new { m, a })
                    .Where(t => ((DatFileTypeAttribute)t.a).FileType == fileType)
                    .Select(t => t.m);

                var first = type.FirstOrDefault();

                if (first == null)
                    throw new Exception($"Failed to Unpack fileType: {fileType}");

                var obj = Activator.CreateInstance(first);

                var unpackable = obj as IUnpackable;

                if (unpackable == null)
                    throw new Exception($"Class for fileType: {fileType} does not implement IUnpackable.");

                var datReader = new DatReader(cellDatLocation, kvp.Value.FileOffset, kvp.Value.FileSize, dat.Header.BlockSize);

                using (var memoryStream = new MemoryStream(datReader.Buffer))
                using (var reader = new BinaryReader(memoryStream))
                {
                    unpackable.Unpack(reader);

                    if (memoryStream.Position != kvp.Value.FileSize)
                        throw new Exception($"Failed to parse all bytes for fileType: {fileType}, ObjectId: 0x{kvp.Value.ObjectId:X8}. Bytes parsed: {memoryStream.Position} of {kvp.Value.FileSize}");
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void UnpackPortalDatFiles_NoExceptions()
        {
            RequireDatFile(portalDatLocation);

            // We need to init the DatManager to load PortalDat.MasterProperty for the BaseProperty references for DbProperties
            // And we need the code page for some of the PortalDat autoload types
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            DatManager.Initialize(DAT_PATH, true, false);

            var assembly = typeof(DatDatabase).GetTypeInfo().Assembly;
            var types = assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(DatFileTypeAttribute), false).Length > 0).ToList();

            if (types.Count == 0)
                throw new Exception("Failed to locate any types with DatFileTypeAttribute.");

            DatDatabase dat = new DatDatabase(portalDatLocation);

            foreach (var kvp in dat.AllFiles)
            {
                if (kvp.Key == 0xFFFF0001) // Iteration info
                    continue;

                var fileType = kvp.Value.GetFileType(DatDatabaseType.Portal);

                //Assert.IsNotNull(fileType, $"Key: 0x{kvp.Key:X8}, ObjectID: 0x{kvp.Value.ObjectId:X8}, FileSize: {kvp.Value.FileSize}, BitFlags:, 0x{kvp.Value.BitFlags:X8}");
                Assert.IsNotNull(fileType, $"Key: 0x{kvp.Key:X8}, ObjectID: 0x{kvp.Value.ObjectId:X8}, FileSize: {kvp.Value.FileSize}");

                // These file types aren't converted yet
                if (fileType == DatFileType.KeyMap) continue; // 0x14, 2 files
                if (fileType == DatFileType.RenderMaterial) continue; // 0x16, 1 file
                if (fileType == DatFileType.MaterialModifier) continue; // 0x17, 1 file
                if (fileType == DatFileType.MaterialInstance) continue; // 0x18, 1 file

                var type = types
                    .SelectMany(m => m.GetCustomAttributes(typeof(DatFileTypeAttribute), false), (m, a) => new { m, a })
                    .Where(t => ((DatFileTypeAttribute)t.a).FileType == fileType)
                    .Select(t => t.m);

                var first = type.FirstOrDefault();

                if (first == null)
                    throw new Exception($"Failed to Unpack fileType: {fileType}");

                var obj = Activator.CreateInstance(first);

                var unpackable = obj as IUnpackable;

                if (unpackable == null)
                    throw new Exception($"Class for fileType: {fileType} does not implement IUnpackable.");

                var datReader = new DatReader(portalDatLocation, kvp.Value.FileOffset, kvp.Value.FileSize, dat.Header.BlockSize);

                using (var memoryStream = new MemoryStream(datReader.Buffer))
                using (var reader = new BinaryReader(memoryStream))
                {
                    unpackable.Unpack(reader);

                    if (memoryStream.Position != kvp.Value.FileSize)
                        throw new Exception($"Failed to parse all bytes for fileType: {fileType}, ObjectId: 0x{kvp.Value.ObjectId:X8}. Bytes parsed: {memoryStream.Position} of {kvp.Value.FileSize}");
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void UnpackLocalEnglishDatFiles_NoExceptions()
        {
            RequireDatFile(localEnglishDatLocation);

            // We need to init the DatManager to load PortalDat.MasterProperty for the BaseProperty references for UiLayout/LayoutDesc
            // And we need the code page for some of the PortalDat autoload types
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            DatManager.Initialize(DAT_PATH, true, false);

            var assembly = typeof(DatDatabase).GetTypeInfo().Assembly;
            var types = assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(DatFileTypeAttribute), false).Length > 0).ToList();

            if (types.Count == 0)
                throw new Exception("Failed to locate any types with DatFileTypeAttribute.");

            DatDatabase dat = new DatDatabase(localEnglishDatLocation);

            foreach (var kvp in dat.AllFiles)
            {
                if (kvp.Key == 0xFFFF0001) // Iteration info
                    continue;

                var fileType = kvp.Value.GetFileType(DatDatabaseType.Language);

                //Assert.IsNotNull(fileType, $"Key: 0x{kvp.Key:X8}, ObjectID: 0x{kvp.Value.ObjectId:X8}, FileSize: {kvp.Value.FileSize}, BitFlags:, 0x{kvp.Value.BitFlags:X8}");
                Assert.IsNotNull(fileType, $"Key: 0x{kvp.Key:X8}, ObjectID: 0x{kvp.Value.ObjectId:X8}, FileSize: {kvp.Value.FileSize}");

                var type = types
                    .SelectMany(m => m.GetCustomAttributes(typeof(DatFileTypeAttribute), false), (m, a) => new { m, a })
                    .Where(t => ((DatFileTypeAttribute)t.a).FileType == fileType)
                    .Select(t => t.m);

                var first = type.FirstOrDefault();

                if (first == null)
                    throw new Exception($"Failed to Unpack fileType: {fileType}");

                var obj = Activator.CreateInstance(first);

                var unpackable = obj as IUnpackable;

                if (unpackable == null)
                    throw new Exception($"Class for fileType: {fileType} does not implement IUnpackable.");

                var datReader = new DatReader(localEnglishDatLocation, kvp.Value.FileOffset, kvp.Value.FileSize, dat.Header.BlockSize);

                using (var memoryStream = new MemoryStream(datReader.Buffer))
                using (var reader = new BinaryReader(memoryStream))
                {
                    unpackable.Unpack(reader);

                    if (memoryStream.Position != kvp.Value.FileSize)
                        throw new Exception($"Failed to parse all bytes for fileType: {fileType}, ObjectId: 0x{kvp.Value.ObjectId:X8}. Bytes parsed: {memoryStream.Position} of {kvp.Value.FileSize}");
                }
            }
        }

        // uncomment if you want to run this
        // [TestMethod]
        public void ExtractCellDatByLandblock()
        {
            string output = @"c:\Turbine\cell_dat_export_by_landblock";
            CellDatDatabase db = new CellDatDatabase(cellDatLocation);
            db.ExtractLandblockContents(output);
        }

        // uncomment if you want to run this
        // [TestMethod]
        public void ExportPortalDatsWithTypeInfo()
        {
            string output = @"c:\Turbine\typed_portal_dat_export";
            PortalDatDatabase db = new PortalDatDatabase(portalDatLocation);
            db.ExtractCategorizedPortalContents(output);
        }
    }
}
