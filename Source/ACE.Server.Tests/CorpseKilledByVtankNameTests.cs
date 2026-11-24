using System.Text.RegularExpressions;

using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// VTank decides corpse ownership by parsing the corpse LongDesc "Killed by X." with the two regexes copied
    /// below (VirindiTank utank2-i.dll decompile, 2026-11-02). A pet kill reads "Killed by Owner's Pet.", and the
    /// owner is recovered from the "'s " split. A pet name with a '(' in it broke the first match, so VTank never
    /// auto-looted the custom Revenant summons' kills.
    /// </summary>
    [TestClass]
    public class CorpseKilledByVtankNameTests
    {
        private static readonly Regex VtankKilledBy = new Regex("(?:Killed by )([a-zA-Z\\ \\-\\']*)(?:\\..*)");
        private static readonly Regex VtankPetOwner = new Regex("([a-zA-Z\\ \\-\\']*)\\'s ([^\\']*)");

        private static string VtankOwnerOf(string longDesc)
        {
            var match = VtankKilledBy.Match(longDesc);

            if (!match.Success || match.Groups[0].Index != 0)
                return null;

            var pet = VtankPetOwner.Match(match.Groups[1].Value);

            return pet.Success ? pet.Groups[1].Value : match.Groups[1].Value;
        }

        [DataTestMethod]
        [DataRow("Danie's Revenant Warden (Slashing)", "Danie's Revenant Warden Slashing")]
        [DataRow("Danie's Revenant Marksman (Fire)", "Danie's Revenant Marksman Fire")]
        [DataRow("Danie's Revenant Lich", "Danie's Revenant Lich")]
        [DataRow("+Admin Name", "Admin Name")]
        [DataRow("Olthoi-Slayer O'Brien", "Olthoi-Slayer O'Brien")]
        [DataRow("Thing 2 (Elder)", "Thing Elder")]
        [DataRow("  A   B  ", "A B")]
        [DataRow("O'Neil-Ray's Revenant Reaver (Acid)", "O'Neil-Ray's Revenant Reaver Acid")]
        [DataRow("+123", "123")]
        [DataRow("", "")]
        [DataRow(null, null)]
        public void Killer_name_keeps_only_vtank_characters(string name, string expected)
        {
            Assert.AreEqual(expected, Creature.ToVtankKillerName(name));
        }

        [TestMethod]
        public void Parenthesized_pet_kill_is_attributed_to_the_owner_by_vtank()
        {
            const string petName = "Danie's Revenant Warden (Slashing)";

            // positive control: the raw name is what VTank failed on
            Assert.IsNull(VtankOwnerOf($"Killed by {petName}."));

            Assert.AreEqual("Danie", VtankOwnerOf($"Killed by {Creature.ToVtankKillerName(petName)}."));
        }

        [DataTestMethod]
        [DataRow("(((")]
        [DataRow("123")]
        public void Name_with_no_vtank_characters_keeps_its_original_text(string name)
        {
            Assert.AreEqual(name, Creature.ToVtankKillerName(name));
        }
    }
}
