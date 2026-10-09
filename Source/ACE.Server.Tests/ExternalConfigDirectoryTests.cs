using System;

using ACE.Common;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers ExternalConfigDirectory.Resolve, the priority logic that decides where Program.cs
    /// reads/scaffolds Config.js and log4net.config from: ACE_CONFIG_DIR (if set) wins over the
    /// container default, which wins over "no external directory" (use the exe directory, as
    /// before this mechanism existed).
    ///
    /// Pure static logic - no session, no database, no world. The environment variable is saved
    /// and restored around every test so tests cannot leak state into each other or into a real
    /// developer environment that happens to have ACE_CONFIG_DIR set.
    /// </summary>
    [TestClass]
    public class ExternalConfigDirectoryTests
    {
        private string savedEnvironmentVariable;

        [TestInitialize]
        public void SaveEnvironmentVariable()
        {
            savedEnvironmentVariable = Environment.GetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName);
        }

        [TestCleanup]
        public void RestoreEnvironmentVariable()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, savedEnvironmentVariable);
        }

        [TestMethod]
        public void EnvironmentVariable_WinsOverContainer()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, @"C:\SomeDir\Config");

            var resolved = ExternalConfigDirectory.Resolve(isRunningInContainer: true);

            Assert.AreEqual(@"C:\SomeDir\Config", resolved);
        }

        [TestMethod]
        public void EnvironmentVariable_WinsWhenNotInContainer()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, @"C:\SomeDir\Config");

            var resolved = ExternalConfigDirectory.Resolve(isRunningInContainer: false);

            Assert.AreEqual(@"C:\SomeDir\Config", resolved);
        }

        [TestMethod]
        public void Unset_InContainer_ReturnsContainerDefault()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, null);

            var resolved = ExternalConfigDirectory.Resolve(isRunningInContainer: true);

            Assert.AreEqual(ExternalConfigDirectory.ContainerConfigDirectory, resolved);
        }

        [TestMethod]
        public void Unset_NotInContainer_ReturnsNull()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, null);

            var resolved = ExternalConfigDirectory.Resolve(isRunningInContainer: false);

            Assert.IsNull(resolved);
        }

        [TestMethod]
        public void EmptyString_TreatedAsUnset()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, "");

            Assert.IsNull(ExternalConfigDirectory.Resolve(isRunningInContainer: false));
            Assert.AreEqual(ExternalConfigDirectory.ContainerConfigDirectory, ExternalConfigDirectory.Resolve(isRunningInContainer: true));
        }

        [TestMethod]
        public void WhitespaceOnly_TreatedAsUnset()
        {
            Environment.SetEnvironmentVariable(ExternalConfigDirectory.EnvironmentVariableName, "   ");

            Assert.IsNull(ExternalConfigDirectory.Resolve(isRunningInContainer: false));
            Assert.AreEqual(ExternalConfigDirectory.ContainerConfigDirectory, ExternalConfigDirectory.Resolve(isRunningInContainer: true));
        }
    }
}
