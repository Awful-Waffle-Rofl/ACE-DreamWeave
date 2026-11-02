using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Content pin for the Olthoi Matron (wcid 1007550) body parts: exactly the seven parts of retail 72428
    /// (Head, Torso, Tail, Arm, Leg, Claw, Breath), every one dealing Nether (DamageType 0x400 = 1024, owner pick
    /// 2026-10-28) with 72428's own d_Val. Pure file + regex parsing of the committed SQL (no dat, no DB), the
    /// VoidClawContentTests idiom.
    /// </summary>
    [TestClass]
    public class MatronBodyPartContentTests
    {
        private const int Wcid = 1007550;
        private const int Nether = 1024;

        private static readonly (int Key, int DVal)[] Expected =
        {
            (0, 500), (16, 5), (17, 500), (18, 500), (19, 15), (20, 500), (22, 600),
        };

        [TestMethod]
        public void Matron_BodyParts_AreTheSevenRetailKeys_AllNether_WithRetailDamageValues()
        {
            var text = File.ReadAllText(GetFile());

            var blocks = Regex.Matches(text, @"INSERT\s+INTO\s+`weenie_properties_body_part`.*?;\s*(\r?\n|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            Assert.AreEqual(1, blocks.Count, "one body_part INSERT");

            var body = Regex.Replace(blocks[0].Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var rows = Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,")
                .Cast<Match>()
                .Select(m => (Wcid: int.Parse(m.Groups[1].Value), Key: int.Parse(m.Groups[2].Value),
                              DType: int.Parse(m.Groups[3].Value), DVal: int.Parse(m.Groups[4].Value)))
                .ToList();

            Assert.AreEqual(7, rows.Count, "exactly seven body part rows: " + string.Join(",", rows.Select(r => r.Key)));
            Assert.IsTrue(rows.All(r => r.Wcid == Wcid), "every row belongs to " + Wcid);

            CollectionAssert.AreEqual(Expected.Select(e => e.Key).ToArray(), rows.Select(r => r.Key).ToArray(), "keys 0,16,17,18,19,20,22");

            foreach (var r in rows)
                Assert.AreEqual(Nether, r.DType, $"part {r.Key}: d_Type must be Nether (1024)");

            CollectionAssert.AreEqual(Expected.Select(e => e.DVal).ToArray(), rows.Select(r => r.DVal).ToArray(), "d_Val 500,5,500,500,15,500,600");
        }

        private static string GetFile()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    var file = Directory.EnumerateFiles(weenies, Wcid + " *.sql", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                    Assert.IsNotNull(file, $"{Wcid}: no weenie file found in {weenies}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
