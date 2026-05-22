using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace webcam_recorder.Tests
{
    [TestClass]
    public class ProgramTests
    {
        [TestMethod]
        public void LoadConfig_Reads_FileLogging_FilePath_When_Present()
        {
            var configPath = WriteTempConfig(@"{
  ""Server"": { ""Host"": ""localhost"", ""Port"": 5001 },
  ""FileLogging"": { ""FilePath"": ""custom\\server.log"" }
}");

            try
            {
                var config = Program.LoadConfig(configPath);

                Assert.AreEqual(@"custom\server.log", config.FileLogging.FilePath);
            }
            finally
            {
                File.Delete(configPath);
            }
        }

        [TestMethod]
        public void LoadConfig_Falls_Back_To_Legacy_Logging_FilePath()
        {
            var configPath = WriteTempConfig(@"{
  ""Server"": { ""Host"": ""localhost"", ""Port"": 5001 },
  ""Logging"": { ""FilePath"": ""legacy\\server.log"" }
}");

            try
            {
                var config = Program.LoadConfig(configPath);

                Assert.AreEqual(@"legacy\server.log", config.FileLogging.FilePath);
            }
            finally
            {
                File.Delete(configPath);
            }
        }

        [DataTestMethod]
        [DataRow("http://localhost:8080", "http://localhost:8080/request-register/videoEvidence/save")]
        [DataRow("http://localhost:8080/", "http://localhost:8080/request-register/videoEvidence/save")]
        public void BuildVideoEvidenceUrl_Appends_Fixed_Path_Once(string baseUrl, string expected)
        {
            var actual = Program.BuildVideoEvidenceUrl(baseUrl);

            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow("172.16.3.5", "video_evidences", "060065912", @"\\172.16.3.5\video_evidences\060065912\")]
        [DataRow("172.16.3.5", "incoming/root", "060065912", @"\\172.16.3.5\incoming\root\060065912\")]
        [DataRow("172.16.3.5", "", "060065912", @"\\172.16.3.5\060065912\")]
        [DataRow("172.16.3.5", "video_evidences", "", @"\\172.16.3.5\video_evidences\")]
        public void BuildRemoteVideoDirectory_Returns_Expected_Unc_Directory(string ftpHost, string ftpRemoteDirectory, string applicationId, string expected)
        {
            var actual = Program.BuildRemoteVideoDirectory(ftpHost, ftpRemoteDirectory, applicationId);

            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow("060065912_video001", "060065912")]
        [DataRow("060065912", "060065912")]
        [DataRow("", "")]
        [DataRow(null, "")]
        public void GetApplicationId_Returns_Expected_Prefix(string id, string expected)
        {
            var actual = Program.GetApplicationId(id);

            Assert.AreEqual(expected, actual);
        }

        static string WriteTempConfig(string json)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }
    }
}
