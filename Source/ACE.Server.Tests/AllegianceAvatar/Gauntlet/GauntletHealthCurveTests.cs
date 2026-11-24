using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Allegiance Gauntlet Task 3: the health curve gains a ceiling above the run ceiling (500) for the Gauntlet only, and
    /// every Thread value stays bit for bit what it was.
    ///
    /// The golden values are the IEEE-754 bit patterns of DungeonHealthCurve.Target at the default anchors and the default
    /// top level, for every level 1..600, CAPTURED BY RUNNING THE PRE-TASK-3 CODE (branch base a7196df94, whose
    /// Source/ACE.Server/ThreadDungeons tree has an empty diff against origin/master e1b86c577) on 2026-11-21, before any
    /// Task 3 edit, through both the four-argument form and Target(level, limits), which agreed on every level. They are
    /// never recomputed from the code under test, so a change to the curve's arithmetic or to how either Thread form
    /// reaches it fails here. Do not edit by hand.
    /// </summary>
    [TestClass]
    public class GauntletHealthCurveTests
    {
        private const double Low = DungeonHealthCurve.DefaultAnchorLow;
        private const double High = DungeonHealthCurve.DefaultAnchorHigh;

        /// <summary>Target(level, 890, 13100) for level = index + 1, from the pre-Task-3 code. See the class summary.</summary>
        private static readonly long[] MasterTargetBits =
        {
            0x405074C89B75C370L, 0x4050B0D53139AA85L, 0x4050EDBCE60E2767L, 0x40512B82D987F347L, 0x40516A2A36A179C2L, 0x4051A9B633E46FADL,
            0x4051EA2A13940195L, 0x40522B8923D79C3BL, 0x40526DD6BEE65121L, 0x4052B1164B32D974L, 0x4052F54B3B983991L, 0x40533A790F870769L,
            0x405380A353335621L, 0x4053C7CD9FC34938L, 0x40540FFB9B7E51ADL, 0x40545930F9FD177EL, 0x4054A3717C5A11FEL, 0x4054EEC0F162D180L,
            0x40553B2335C9FCD6L, 0x4055889C345A0546L, 0x4055D72FE6289375L, 0x405626E252CAB006L, 0x405677B79089AA85L, 0x4056C9B3C498C160L,
            0x40571CDB234B8DA9L, 0x40577131F04D3573L, 0x4057C6BC7ED8678DL, 0x40581D7F31F02397L, 0x4058757E7C99513AL, 0x4058CEBEE215298EL,
            0x40592944F61C75A7L, 0x405985155D1BA54CL, 0x4059E234CC6FC0F3L, 0x405A40A80AA43A2BL, 0x405AA073EFB19D83L, 0x405B019D653D2948L,
            0x405B642966D94C46L, 0x405BC81D02470FE5L, 0x405C2D7D57B870EEL, 0x405C944F9A13AA7CL, 0x405CFC990F377668L, 0x405D665F104046D5L,
            0x405DD1A709CE7C45L, 0x405E3E767C4D9BECL, 0x405EACD2FC3C89DDL, 0x405F1CC23276CAC5L, 0x405F8E49DC7ED0F7L, 0x406000B7E664AC4CL,
            0x40603B1CF584EB64L, 0x406076571A3FFFDCL, 0x4060B2695E2333D1L, 0x4060EF56D5D12153L, 0x40612D22A12A23DAL, 0x40616BCFEB755D47L,
            0x4061AB61EB8A509FL, 0x4061EBDBE3FB148DL, 0x40622D41233F1FE1L, 0x40626F9503DEB23FL, 0x4062B2DAEC9EDB32L, 0x4062F71650AE21E8L,
            0x40633C4AAFD1CFDFL, 0x4063827B9693E0DBL, 0x4063C9AC9E719A79L, 0x406411E16E0ACDC7L, 0x40645B1DB951C550L, 0x4064A56541BBE209L,
            0x4064F0BBD672E9A0L, 0x40653D25548708C4L, 0x40658AA5A7218BD3L, 0x4065D940C7B850B3L, 0x406628FABE41F454L, 0x406679D7A16ABEA5L,
            0x4066CBDB96CA4F9AL, 0x40671F0AD31A1017L, 0x406773699A6C6982L, 0x4067C8FC4064C6D1L, 0x40681FC7287061F7L, 0x406877CEC5FFE08FL,
            0x4068D1179CC1C2CEL, 0x40692BA640DDA7A5L, 0x4069877F5730692AL, 0x4069E4A795891457L, 0x406A4323C2E6BF4BL, 0x406AA2F8B7B74124L,
            0x406B042B5E16CEBDL, 0x406B66C0B2107F88L, 0x406BCABDC1DFBDDCL, 0x406C3027AE32A606L, 0x406C9703AA6D578EL, 0x406CFF56FCEE3C22L,
            0x406D6926FF5347A1L, 0x406DD4791EC034E4L, 0x406E4152DC25C2C0L, 0x406EAFB9CC89F510L, 0x406F1FB399515D5EL, 0x406F914600896EECL,
            0x4070023B6A99F108L, 0x40703CA5FFC99549L, 0x407077E5BEBC0287L, 0x4070B3FDB1480FB5L, 0x4070F0F0EC5AF01BL, 0x40712EC29020A89DL,
            0x40716D75C82D18A4L, 0x4071AD0DCBA598C4L, 0x4071ED8DDD6B3137L, 0x40722EF94C456A72L, 0x40727153730DB9EAL, 0x4072B49FB8DB8D4FL,
            0x4072F8E19130F688L, 0x40733E1C7C27FAA2L, 0x4073845406A08621L, 0x4073CB8BCA6F08FDL, 0x407413C76E8BBCB0L, 0x40745D0AA74296CEL,
            0x4074A7593663EA81L, 0x4074F2B6EB75BB95L, 0x40753F27A3E5C565L, 0x40758CAF4B3C3867L, 0x4075DB51DB4F30D9L, 0x40762B135C76E934L,
            0x40767BF7E5C2AB24L, 0x4076CE039D2E81A3L, 0x4077213AB7D9AF09L, 0x407775A17A3DE9D5L, 0x4077CB3C3867630AL, 0x4078220F562D98F9L,
            0x40787A1F476CF965L, 0x4078D370904155F2L, 0x40792E07C5412DEBL, 0x407989E98BB9D057L, 0x4079E71A99EC597EL, 0x407A459FB74B8EFFL,
            0x407AA57DBCBA9D9FL, 0x407B06B994CCBC06L, 0x407B69583C05B5B8L, 0x407BCD5EC11B6190L, 0x407C32D24538071AL, 0x407C99B7FC3DB636L,
            0x407D02152D0A9476L, 0x407D6BEF31BE23B9L, 0x407DD74B77FF859BL, 0x407E442F8144BF2CL, 0x407EB2A0E31B00DCL, 0x407F22A5476FF5FDL,
            0x407F94426CDC1FF2L, 0x408003BF1377204BL, 0x40803E2F2F3BEBEBL, 0x4080797488ED5C3DL, 0x4080B5922AABDBFEL, 0x4080F28B29AF3EBBL,
            0x40813062A66F39F0L, 0x40816F1BCCCC71D0L, 0x4081AEB9D43A1BD4L, 0x4081EF3FFFE83944L, 0x408230B19EEE6BCAL, 0x408273120C77665BL,
            0x4082B664AFECFC97L, 0x4082FAACFD24D304L, 0x40833FEE748DB241L, 0x4083862CA35D7FB6L, 0x4083CD6B23BFDDF2L, 0x408415AD9D05773BL,
            0x40845EF7C3D3F4A3L, 0x4084A94D5A56A42EL, 0x4084F4B2306FD075L, 0x4085412A23EACC5FL, 0x40858EB920AEB571L, 0x4085DD6320F1EF5BL,
            0x40862D2C2D6E5B5DL, 0x40867E185D964E3DL, 0x4086D02BD7CA477BL, 0x4087236AD18F6C84L, 0x408777D98FC6CAB6L, 0x4087CD7C66E5630AL,
            0x40882457BB2D023EL, 0x40887C7000E5E86BL, 0x4088D5C9BC9942FFL, 0x40893069834C7C1CL, 0x40898C53FABD625CL, 0x4089E98DD99F2C1DL,
            0x408A481BE7D85974L, 0x408AA802FEC177E1L, 0x408B09480964CB1FL, 0x408B6BF004BEDE2EL, 0x408BD00000000000L, 0x408C357D1CCEAF20L,
            0x408C9C6C8F8AF7ADL, 0x408D04D39F92C737L, 0x408D6EB7A78739DFL, 0x408DDA1E1592E46AL, 0x408E470C6BB11EC6L, 0x408EB5883FF652B8L,
            0x408F25973CD95260L, 0x408F973F217DBA60L, 0x40900542E0FFB1B6L, 0x40903FB883DF7394L, 0x40907B0378D79E1FL, 0x4090B726CA5236D0L,
            0x4090F4258DD1B88EL, 0x40913202E4199092L, 0x409170C1F9572F19L, 0x4091B066054BADE7L, 0x4091F0F24B760EC4L, 0x4092326A1B3E1426L,
            0x409274D0D01FB62CL, 0x4092B829D1D73638L, 0x4092FC78948DD350L, 0x409341C0990721B1L, 0x409388056CCF07C3L, 0x4093CF4AAA6862EFL,
            0x40941793F97C56A2L, 0x409460E50F0A47EDL, 0x4094AB41AD988840L, 0x4094F6ADA565B1C4L, 0x4095432CD49AB7C3L, 0x409590C3277DADCDL,
            0x4095DF7498A5481EL, 0x40962F45312D17FAL, 0x4096803908EA86A3L, 0x4096D25446A29199L, 0x4097259B20404B04L, 0x40977A11DB0C20E9L,
            0x4097CFBCCBE3EE20L, 0x409826A05773D7E3L, 0x40987EC0F26FFAD2L, 0x4098D82321CEEA81L, 0x409932CB7B050660L, 0x40998EBEA440A746L,
            0x4099EC0154A72873L, 0x409A4A985492CF5FL, 0x409AAA887DD19565L, 0x409B0BD6BBE4D692L, 0x409B6E880C41E8CEL, 0x409BD2A17E939EBAL,
            0x409C382834FCB9A0L, 0x409C9F21645B4DC9L, 0x409D0792548D1CD3L, 0x409D718060B4E969L, 0x409DDCF0F780C7ECL, 0x409E49E99B716FBCL,
            0x409EB86FE32290C5L, 0x409F2889799430E7L, 0x409F9A3C1E751530L, 0x40A006C6D3371D44L, 0x40A04141FDB7B0E7L, 0x40A07C928E7E59A3L,
            0x40A0B8BB903EBEA9L, 0x40A0F5C018C60944L, 0x40A133A349236596L, 0x40A172684DD1172DL, 0x40A1B2125EDE2371L, 0x40A1F2A4C0189424L,
            0x40A23422C138541BL, 0x40A2768FBE0AA856L, 0x40A2B9EF1E9E47BEL, 0x40A2FE44577013C7L, 0x40A34392E998744AL, 0x40A389DE62F958D8L,
            0x40A3D12A5E6CE1F3L, 0x40A4197A83F4B48BL, 0x40A462D288E9FA2DL, 0x40A4AD36302E1057L, 0x40A4F8A94A5BE977L, 0x40A5452FB5FA2217L,
            0x40A592CD5FADCCCBL, 0x40A5E186426DF77DL, 0x40A6315E67B7ECAFL, 0x40A68259E7C4337BL, 0x40A6D47CE9BC50EBL, 0x40A727CBA3F14D80L,
            0x40A77C4A5C1301AEL, 0x40A7D1FD67682C19L, 0x40A828E92B075483L, 0x40A881121C107E4CL, 0x40A8DA7CBFE7AD7FL, 0x40A9352DAC704165L,
            0x40A99129884927ADL, 0x40A9EE750B09EB46L, 0x40AA4D14FD80A201L, 0x40AAAD0E39F0BC31L, 0x40AB0E65AC52B977L, 0x40AB71205294C610L,
            0x40ABD5433CDC43E3L, 0x40AC3AD38DC842B7L, 0x40ACA1D67AB4EAEEL, 0x40AD0A514BFFDE51L, 0x40AD74495D4D924FL, 0x40ADDFC41DCFA758L,
            0x40AE4CC7108C40E1L, 0x40AEBB57CCA661BFL, 0x40AF2B7BFDA75096L, 0x40AF9D3963C90804L, 0x40B0084AEA20DB40L, 0x40B042CB9CC828D8L,
            0x40B07E21C9E52097L, 0x40B0BA507C75125CL, 0x40B0F75ACA8FDCE7L, 0x40B13543D590726FL, 0x40B1740ECA3DF112L, 0x40B1B3BEE0F5513FL,
            0x40B1F4575DD3AC2FL, 0x40B235DB90E11CA7L, 0x40B2784ED63C3C37L, 0x40B2BBB496463F1CL, 0x40B3001045CFB125L, 0x40B345656645D5CCL,
            0x40B38BB785E0ADEDL, 0x40B3D30A3FD1A563L, 0x40B41B613C72EB04L, 0x40B464C031777555L, 0x40B4AF2AE21BB67DL, 0x40B4FAA51F5701ECL,
            0x40B54732C80DA64AL, 0x40B594D7C943BE2CL, 0x40B5E3981E50BA42L, 0x40B63377D113A78FL, 0x40B6847AFA283465L, 0x40B6D6A5C11C76D6L,
            0x40B729FC5CA77768L, 0x40B77E8312E082C1L, 0x40B7D43E3977453EL, 0x40B82B3235ECB339L, 0x40B883637DCCC109L, 0x40B8DCD696E8ED8AL,
            0x40B937901793A258L, 0x40B99394A6DC6CA8L, 0x40B9F0E8FCCD11E2L, 0x40BA4F91E2A78326L, 0x40BAAF943324B2D6L, 0x40BB10F4DAB44F72L,
            0x40BB73B8D7BD66F8L, 0x40BBD7E53ADFF62EL, 0x40BC3D7F27376717L, 0x40BCA48BD29E021FL, 0x40BD0D1085F1554EL, 0x40BD77129D579520L,
            0x40BDE2978885FA83L, 0x40BE4FA4CB08219FL, 0x40BEBE3FFC886D06L, 0x40BF2E6EC9197112L, 0x40BFA036F1806B22L, 0x40C009CF25C0644AL,
            0x40C04455611460B2L, 0x40C07FB12B0F851FL, 0x40C0BBE58EF8D115L, 0x40C0F8F5A332DFDBL, 0x40C136E4896470E5L, 0x40C175B56EA1842DL,
            0x40C1B56B8B950C82L, 0x40C1F60A24AB3A0DL, 0x40C237948A3C5F20L, 0x40C27A0E18B87187L, 0x40C2BD7A38D32AA6L, 0x40C301DC5FB0C88AL,
            0x40C347380F137259L, 0x40C38D90D589425DL, 0x40C3D4EA4E9AF80EL, 0x40C41D4822FB5485L, 0x40C466AE08B723BEL, 0x40C4B11FC365F528L,
            0x40C4FCA1245B85F3L, 0x40C549360AD9DFBFL, 0x40C596E264442E21L, 0x40C5E5AA2C524DADL, 0x40C635916D451724L, 0x40C6869C401B6973L,
            0x40C6D8CECCC7F53AL, 0x40C72C2D4A67CCA4L, 0x40C780BBFF79BA58L, 0x40C7D67F42166254L, 0x40C82D7B78292FA1L, 0x40C885B517AA11BBL,
            0x40C8DF30A6D80CB0L, 0x40C939F2BC749EF0L, 0x40C9960000000000L, 0x40C9F35D29F63A1EL, 0x40CA520F040D2521L, 0x40CAB21A69734073L,
            0x40CB1384470F74B6L, 0x40CB76519BC1BD1CL, 0x40CBDA8778A4BCDCL, 0x40CC402B01504404L, 0x40CCA7416C1CC6EFL, 0x40CD0FD00267CBFFL,
            0x40CD79DC20D95305L, 0x40CDE56B37AA39DEL, 0x40CE5282CAEBA206L, 0x40CEC12872CF5A99L, 0x40CF3161DBF152A7L, 0x40CFA334C7A21778L,
            0x40D00B5386193159L, 0x40D045DF4A9FDE16L, 0x40D08140B20119B5L, 0x40D0BD7AC7CD9A58L, 0x40D0FA90A2B2BEDBL, 0x40D1388564A31B1FL,
            0x40D1775C3AFF983AL, 0x40D1B7185EC12AC1L, 0x40D1F7BD14A32147L, 0x40D2394DAD4E0D3EL, 0x40D27BCD85834865L, 0x40D2BF4006491910L,
            0x40D303A8A517777AL, 0x40D3490AE405767AL, 0x40D38F6A51F751EDL, 0x40D3D6CA8ACD2529L, 0x40D41F2F37924BEEL, 0x40D4689C0EAD702FL,
            0x40D4B314D411473DL, 0x40D4FE9D596E00C8L, 0x40D54B397E636A47L, 0x40D598ED30B3C94AL, 0x40D5E7BC6C776F6DL, 0x40D637AB3C510A6CL,
            0x40D688BDB9A2B331L, 0x40D6DAF80CC3BE6CL, 0x40D72E5E6D375198L, 0x40D782F521E3BF23L, 0x40D7D8C0814AAC9FL, 0x40D82FC4F1C205D1L,
            0x40D88806E9ADBF94L, 0x40D8E18AEFBA6D84L, 0x40D93C559B18AD63L, 0x40D9986B93B96B66L, 0x40D9F5D1928B0256L, 0x40DA548C61B73ACFL,
            0x40DAB4A0DCE22CB2L, 0x40DB1613F16A0606L, 0x40DB78EA9EA7BAA0L, 0x40DBDD29F6309FCCL, 0x40DC42D71C18F759L, 0x40DCA9F747376D8AL,
            0x40DD128FC1698D34L, 0x40DD7CA5E7D92DC2L, 0x40DDE83F2B42DE78L, 0x40DE5561103D52C3L, 0x40DEC4112F81D31CL, 0x40DF34553635B640L,
            0x40DFA632E634E695L, 0x40E00CD80B2EBBB2L, 0x40E04769596E26F9L, 0x40E082D05EBD712BL, 0x40E0BF1026F70DFEL, 0x40E0FC2BC91326FCL,
            0x40E13A2667502B9AL, 0x40E179032F5BF554L, 0x40E1B8C55A7D81E3L, 0x40E1F9702DBF45C3L, 0x40E23B06FA1A1919L, 0x40E27D8D1CA0C14EL,
            0x40E2C105FEAC1974L, 0x40E305751607DBDCL, 0x40E34ADDE5200F1BL, 0x40E39143FB2F18C5L, 0x40E3D8AAF46C7854L, 0x40E421167A3C2C8BL,
            0x40E46A8A435EC5D7L, 0x40E4B50A1422280BL, 0x40E50099BE92FE16L, 0x40E54D3D22AEE223L, 0x40E59AF82E973CBEL, 0x40E5E9CEDEC4DDA6L,
            0x40E639C53E3C50D9L, 0x40E68ADF66C2F29CL, 0x40E6DD218114C539L, 0x40E7308FC51B0B1FL, 0x40E7852E7A23A850L, 0x40E7DB01F7194DDEL,
            0x40E8320EA2BC7260L, 0x40E88A58F3DD1A48L, 0x40E8E3E571957319L, 0x40E93EB8B3854470L, 0x40E99AD7620E39FCL, 0x40E9F84636910971L,
            0x40EA5709FBAB779BL, 0x40EAB7278D773FC6L, 0x40EB18A3D9C9E0AEL, 0x40EB7B83E075523AL, 0x40EBDFCCB389A761L, 0x40EC458377979F81L,
            0x40ECACAD63F42AAEL, 0x40ED154FC2FCE451L, 0x40ED7F6FF25D87B6L, 0x40EDEB13635661FAL, 0x40EE583F9B03C522L, 0x40EEC6FA32A67FCDL,
            0x40EF3748D7ED5D71L, 0x40EFA9314D3FB2B6L, 0x40F00E5CB5047CF5L, 0x40F048F38D82C1A8L, 0x40F0846031481EA8L, 0x40F0C0A5AC78CC3DL,
            0x40F0FDC71657C5ACL, 0x40F13BC7916F5D31L, 0x40F17AAA4BBA63F0L, 0x40F1BA727ECDE82BL, 0x40F1FB2370038BC2L, 0x40F23CC070A4752AL,
            0x40F27F4CDE14DD20L, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
            0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL, 0x40F2C2CC22003B4AL,
        };

        private static double Master(int level) => BitConverter.Int64BitsToDouble(MasterTargetBits[level - 1]);

        /// <summary>Thread-shaped limits: the struct the ctor builds with every optional argument left at its default.</summary>
        private static DungeonPopulationLimits ThreadLimits() => new DungeonPopulationLimits(10, 1.0, 1.0, 8, 1.0);

        [TestMethod]
        public void ThreadTargets_UnchangedAtEveryLevel_1To500()
        {
            Assert.AreEqual(600, MasterTargetBits.Length);

            // Positive controls on the capture itself: the class comment's own published points.
            Assert.AreEqual(Low, Master(DungeonHealthCurve.AnchorLowLevel));
            Assert.AreEqual(High, Master(DungeonHealthCurve.AnchorHighLevel));
            Assert.AreEqual(76845.0, Math.Round(Master(500)));

            var limits = ThreadLimits();
            var failures = new List<string>();

            void Check(string form, int level, double expected, double actual)
            {
                if (!(actual == expected))
                    failures.Add($"{form} L{level}: expected {expected:R} actual {actual:R}");
            }

            for (var level = 1; level <= 600; level++)
            {
                var expected = Master(level);

                Check("3-arg", level, expected, DungeonHealthCurve.Target(level, Low, High));
                Check("4-arg top 500", level, expected, DungeonHealthCurve.Target(level, Low, High, 500));
                Check("limits", level, expected, DungeonHealthCurve.Target(level, limits));
                Check("5-arg ceiling 500", level, expected, DungeonHealthCurve.Target(level, Low, High, 500, DungeonGemSpec.MaxRunLevel));

                // A Thread top level above the run ceiling still reads as the run ceiling, exactly as before.
                Check("4-arg top 1000", level, expected, DungeonHealthCurve.Target(level, Low, High, 1000));
                Check("4-arg top long.Max", level, expected, DungeonHealthCurve.Target(level, Low, High, long.MaxValue));

                // Every other Thread top level reads the master value at min(level, top) - the clamp is the only thing a
                // top level does - with the sanitizer's [375, 500] window applied.
                foreach (var top in new long[] { 0, 374, 375, 420, 499 })
                {
                    var at = Math.Min(level, (int)Math.Clamp(top, 375, 500));
                    Check($"4-arg top {top}", level, Master(at), DungeonHealthCurve.Target(level, Low, High, top));
                }
            }

            Assert.AreEqual(0, failures.Count, $"{failures.Count} Thread target(s) moved:\n" + string.Join("\n", failures.Take(40)));
        }

        [TestMethod]
        public void ThreadDefaultLimits_CeilingIs500()
        {
            Assert.AreEqual(500, DungeonPopulationLimits.DefaultHealthCurveCeilingLevel);
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, ThreadLimits().HealthCurveCeilingLevel);
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, ThreadLimits().HealthCurveTopLevel);

            // The single-argument sanitizer keeps its [375, 500] window.
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(0));
            Assert.AreEqual(500, DungeonHealthCurve.SanitizeTopLevel(1000));
            Assert.AreEqual(500, DungeonHealthCurve.SanitizeTopLevel(long.MaxValue));

            // The live Thread reader never sets the ceiling, whatever the top-level dial holds.
            try
            {
                DefaultPropertyManager.LoadDefaultProperties();

                var read = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(DungeonGemSpec.MaxRunLevel, read.HealthCurveCeilingLevel, "ReadLimits ceiling at the defaults");
                Assert.AreEqual(DungeonGemSpec.MaxRunLevel, read.HealthCurveTopLevel, "ReadLimits top level at the defaults");

                Assert.IsTrue(PropertyManager.ModifyLong("dynamic_dungeons_health_curve_top_level", 1000), "dynamic_dungeons_health_curve_top_level not registered");
                read = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(DungeonGemSpec.MaxRunLevel, read.HealthCurveCeilingLevel, "ReadLimits ceiling with the dial at 1000");
                Assert.AreEqual(DungeonGemSpec.MaxRunLevel, read.HealthCurveTopLevel, "a Thread dial of 1000 still reads as 500");
                Assert.AreEqual(Master(500), DungeonHealthCurve.Target(800, read), "a Thread run at 800 prices level 500");
            }
            finally
            {
                DefaultPropertyManager.LoadDefaultProperties();
            }
        }

        [TestMethod]
        public void GauntletCeiling1000_ExtrapolatesTo1000_FlatAbove()
        {
            var limits = ThreadLimits().WithHealthCurveTopLevel(1000, 1000);
            Assert.AreEqual(1000, limits.HealthCurveTopLevel);
            Assert.AreEqual(1000, limits.HealthCurveCeilingLevel);

            // At and below 500 the Gauntlet curve IS the Thread curve.
            for (var level = 1; level <= 500; level++)
                Assert.AreEqual(Master(level), DungeonHealthCurve.Target(level, limits), $"L{level}");

            // Above 500 it keeps growing on the same per-level ratio g, strictly.
            var g = DungeonHealthCurve.RatioPerLevel(Low, High);

            for (var level = 501; level <= 1000; level++)
            {
                var here = DungeonHealthCurve.Target(level, limits);
                var below = DungeonHealthCurve.Target(level - 1, limits);

                Assert.IsTrue(here > below, $"L{level}: {here} not above L{level - 1}: {below}");
                Assert.AreEqual(g, here / below, 1e-9, $"L{level}: per-level ratio");
            }

            // The spec's figure: T(1000) is about 91 million band-standard health.
            var t1000 = DungeonHealthCurve.Target(1000, limits);
            Assert.AreEqual(Master(500) * Math.Pow(g, 500), t1000, t1000 * 1e-9);
            Assert.IsTrue(t1000 > 85e6 && t1000 < 95e6, $"T(1000) = {t1000}");

            // Above the top level it is flat at T(1000).
            foreach (var level in new[] { 1001, 1250, 2000, int.MaxValue })
                Assert.AreEqual(t1000, DungeonHealthCurve.Target(level, limits), $"L{level}");

            // The five-argument form gives the same values as the limits form.
            foreach (var level in new[] { 1, 375, 500, 501, 750, 1000, 1500 })
                Assert.AreEqual(DungeonHealthCurve.Target(level, limits), DungeonHealthCurve.Target(level, Low, High, 1000, 1000), $"L{level}");

            // The master switch still turns the curve off.
            var off = new DungeonPopulationLimits(10, 1.0, 1.0, 8, 1.0, healthCurveEnabled: false).WithHealthCurveTopLevel(1000, 1000);
            Assert.AreEqual(0.0, DungeonHealthCurve.Target(750, off));
        }

        [TestMethod]
        public void Ceiling_NeverBelowAnchorHigh()
        {
            Assert.AreEqual(DungeonHealthCurve.AnchorHighLevel, DungeonHealthCurve.SanitizeCeiling(0));
            Assert.AreEqual(DungeonHealthCurve.AnchorHighLevel, DungeonHealthCurve.SanitizeCeiling(-5));
            Assert.AreEqual(DungeonHealthCurve.AnchorHighLevel, DungeonHealthCurve.SanitizeCeiling(374));
            Assert.AreEqual(600, DungeonHealthCurve.SanitizeCeiling(600));
            Assert.AreEqual(DungeonHealthCurve.MaxCeilingLevel, DungeonHealthCurve.SanitizeCeiling(long.MaxValue));

            // The top level clamps into [375, sanitized ceiling].
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(1000, 100));
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(0, 1000));
            Assert.AreEqual(800, DungeonHealthCurve.SanitizeTopLevel(800, 1000));
            Assert.AreEqual(1000, DungeonHealthCurve.SanitizeTopLevel(5000, 1000));
            Assert.AreEqual(DungeonHealthCurve.MaxCeilingLevel, DungeonHealthCurve.SanitizeTopLevel(long.MaxValue, int.MaxValue));

            // A ceiling under the high anchor flattens the curve at 375, never inside the measured range.
            var low = ThreadLimits().WithHealthCurveTopLevel(1000, 100);
            Assert.AreEqual(375, low.HealthCurveCeilingLevel);
            Assert.AreEqual(375, low.HealthCurveTopLevel);

            for (var level = 1; level <= 600; level++)
                Assert.AreEqual(Master(Math.Min(level, 375)), DungeonHealthCurve.Target(level, low), $"L{level}");

            // A limits struct nobody filled in (ceiling 0, top 0) keeps the 375 reading it had before the ceiling existed.
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(default(DungeonPopulationLimits).HealthCurveTopLevel, default(DungeonPopulationLimits).HealthCurveCeilingLevel));

            // The hard ceiling keeps the curve finite.
            Assert.IsTrue(double.IsFinite(DungeonHealthCurve.Target(int.MaxValue, Low, High, long.MaxValue, int.MaxValue)));
            Assert.IsTrue(double.IsFinite(DungeonHealthCurve.Target(int.MaxValue, DungeonHealthCurve.MaxAnchorLow, DungeonHealthCurve.MaxAnchorHigh, long.MaxValue, int.MaxValue)));
        }

        // ---- the live plan builder -------------------------------------------------------------------------------

        private const int Tier = 8;
        private const int Seed = 424242;

        /// <summary>One shaped plan: the plan, the limits the build saw, and each entry's shaped max health in plan order.</summary>
        private sealed record Shaped(DungeonSpawnPlan Plan, DungeonPopulationLimits Limits, List<(DungeonSpawnPlanEntry Entry, uint MaxHealth)> Entries)
        {
            public string Fingerprint() => ShippedRosterFixture.Fingerprint(Plan) + "||" + BuildLivePlanGoldenTests.PlanExtras(Plan) + "||"
                + string.Join(",", Entries.Select(e => $"{e.Entry.Wcid}:{(int)e.Entry.Role}:{e.MaxHealth}"));
        }

        /// <summary>
        /// Builds the fixture plan through BuildLivePlan and shapes every entry through ShapeCreature on the probe's
        /// synthetic creature, non-bosses first so the boss sees the observed pack maximum, as the live populate does.
        /// </summary>
        private static Shaped BuildAndShape(DungeonEntryDef site, ThreadDungeonStore store, int level, string family, int seed,
            Func<DungeonPopulationLimits, DungeonPopulationLimits> adjust, bool passAdjust)
        {
            ThreadPlanCache.Invalidate();

            var spec = new DungeonGemSpec(site.Id, level, Tier, family, seed, Array.Empty<(string, double)>(), 0, 0);
            var sources = BuildLivePlanGoldenTests.FixtureSources(new BuildLivePlanGoldenTests.FixtureCalls(), store);

            ThreadDungeonSpawner.LivePlanBuildStats stats;
            var plan = passAdjust
                ? ThreadDungeonSpawner.BuildLivePlan(spec, site, GroupScaling.Solo, sources, () => $"gauntlet curve L{level}", out stats, adjust)
                : ThreadDungeonSpawner.BuildLivePlan(spec, site, GroupScaling.Solo, sources, () => $"gauntlet curve L{level}", out stats);

            var options = new CreatureShapeOptions { Rewards = true, GroupEffort = GroupScaling.Solo.Effort, LogTag = $"gauntlet curve L{level}" };
            var shapedByEntry = new Dictionary<DungeonSpawnPlanEntry, uint>();
            uint observedMaxNonBoss = 0;

            foreach (var e in plan.Entries.Where(e => e.Role != DungeonRole.Boss).Concat(plan.Entries.Where(e => e.Role == DungeonRole.Boss)))
            {
                var creature = GauntletPlanProbeTests.MakeCreature(e.Wcid, level);
                ThreadDungeonSpawner.ShapeCreature(creature, plan, e, observedMaxNonBoss, options);

                shapedByEntry[e] = creature.Health.MaxValue;

                if (e.Role != DungeonRole.Boss && creature.Health.MaxValue > observedMaxNonBoss)
                    observedMaxNonBoss = creature.Health.MaxValue;
            }

            return new Shaped(plan, stats.Limits, plan.Entries.Select(e => (e, shapedByEntry[e])).ToList());
        }

        private static void WithFixture(Action<DungeonEntryDef, ThreadDungeonStore> body)
        {
            TestGameTables.EnsureInitialized();
            Assert.IsNotNull(ShippedRosterFixture.Tables, "species tables not found");

            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.IsTrue(store.Dungeons.TryGetValue(BuildLivePlanGoldenTests.OutdoorSiteId, out var site), "outdoor site missing from the shipped store");

            var seeded = new List<BuildLivePlanGoldenTests.SeededWeenie>();

            try
            {
                DefaultPropertyManager.LoadDefaultProperties();
                BuildLivePlanGoldenTests.SeedWeenieCache(store, seeded);
                body(site, store);
            }
            finally
            {
                ThreadPlanCache.Invalidate();
                DefaultPropertyManager.LoadDefaultProperties();
                BuildLivePlanGoldenTests.UnseedWeenieCache(seeded);
            }
        }

        [TestMethod]
        public void BuildLivePlanOverload_NullAdjust_IdenticalPlan()
        {
            WithFixture((site, store) =>
            {
                // Anchor to master first: the golden row this site and level were captured for still reproduces through the
                // six-argument call (the pre-Task-3 call shape).
                var golden = BuildLivePlanGoldenTests.Golden.Single(c => c.CaseId == "L500-any-s1234-g3-cache-out");

                try
                {
                    BuildLivePlanGoldenTests.SeedProperties(golden);
                    ThreadPlanCache.Invalidate();

                    var dungeon = BuildLivePlanGoldenTests.DungeonFor(golden, store);
                    var spec = BuildLivePlanGoldenTests.SpecFor(golden, dungeon);
                    var group = BuildLivePlanGoldenTests.GroupFor(golden);

                    var six = ThreadDungeonSpawner.BuildLivePlan(spec, dungeon, group,
                        BuildLivePlanGoldenTests.FixtureSources(new BuildLivePlanGoldenTests.FixtureCalls(), store), () => golden.CaseId, out _);
                    Assert.AreEqual(golden.PlanHash, ShippedRosterFixture.Hash(six), "the six-argument call reproduces master's golden plan");

                    ThreadPlanCache.Invalidate();
                    var nullAdjust = ThreadDungeonSpawner.BuildLivePlan(spec, dungeon, group,
                        BuildLivePlanGoldenTests.FixtureSources(new BuildLivePlanGoldenTests.FixtureCalls(), store), () => golden.CaseId, out _, null);
                    Assert.AreEqual(golden.PlanHash, ShippedRosterFixture.Hash(nullAdjust), "a null adjustment reproduces master's golden plan");
                    Assert.AreEqual(golden.PlanExtras, BuildLivePlanGoldenTests.PlanExtras(nullAdjust), "a null adjustment reproduces master's golden extras");
                }
                finally
                {
                    ThreadPlanCache.Invalidate();
                    DefaultPropertyManager.LoadDefaultProperties();
                }

                // Then the shaped creatures: six-argument, explicit null and identity adjustments give the same plan and the
                // same max health on every entry, at levels on both sides of the run ceiling.
                foreach (var level in new[] { 200, 500, 750 })
                {
                    var six = BuildAndShape(site, store, level, "any", Seed ^ level, null, passAdjust: false);
                    var nul = BuildAndShape(site, store, level, "any", Seed ^ level, null, passAdjust: true);
                    var identity = BuildAndShape(site, store, level, "any", Seed ^ level, l => l, passAdjust: true);

                    Assert.IsTrue(six.Entries.Count > 0, $"L{level}: positive control, the plan populated");
                    Assert.AreEqual(six.Fingerprint(), nul.Fingerprint(), $"L{level}: null adjustment");
                    Assert.AreEqual(six.Fingerprint(), identity.Fingerprint(), $"L{level}: identity adjustment");
                    Assert.AreEqual(DungeonGemSpec.MaxRunLevel, nul.Limits.HealthCurveCeilingLevel, $"L{level}: the limits the build saw");
                }
            });
        }

        /// <summary>
        /// The consumer pin for spike 1B's finding: the ceiling must reach the archetype uplift's health standard (the shared
        /// Target(level, limits) overload, DungeonPopulationBuilder's uplift context), or uplifted trash keeps level-500 health.
        /// Asserts on the max health ShapeCreature actually stamps on the uplifted non-boss creatures, never on the curve.
        /// </summary>
        [TestMethod]
        public void GauntletCeiling1000_UpliftedTrashShapedHealth_RisesAbove500()
        {
            WithFixture((site, store) =>
            {
                var adjust = GauntletPlanProbeTests.GauntletAdjust(1000);
                // Rising levels only up to 850: the stretch maps picks up to the band's high edge above the run level, so from
                // about 870 on the mapped levels pass the top level (1000) and the pack sits at T(1000). Asserted separately below.
                var levels = new[] { 500, 600, 700, 800, 850 };
                var gauntletMedians = new List<double>();

                static double MedianUplifted(Shaped s, string label)
                {
                    var hp = s.Entries.Where(e => e.Entry.Role != DungeonRole.Boss && e.Entry.UpliftLevel > 0).Select(e => (double)e.MaxHealth).OrderBy(h => h).ToList();
                    Assert.IsTrue(hp.Count > 0, $"{label}: positive control, the plan has uplifted non-boss entries");
                    return hp[hp.Count / 2];
                }

                foreach (var level in levels)
                {
                    var thread = BuildAndShape(site, store, level, "any", Seed, null, passAdjust: false);
                    var gauntlet = BuildAndShape(site, store, level, "any", Seed, adjust, passAdjust: true);

                    Assert.AreEqual(1000, gauntlet.Limits.HealthCurveCeilingLevel, $"L{level}: the limits the build saw");

                    var threadMedian = MedianUplifted(thread, $"L{level} thread");
                    var gauntletMedian = MedianUplifted(gauntlet, $"L{level} gauntlet");
                    Console.WriteLine($"L{level}: uplifted trash median shaped health thread={threadMedian} gauntlet={gauntletMedian} " +
                        $"(uplifted {gauntlet.Entries.Count(e => e.Entry.Role != DungeonRole.Boss && e.Entry.UpliftLevel > 0)} of {gauntlet.Entries.Count(e => e.Entry.Role != DungeonRole.Boss)} non-boss)");

                    if (level == DungeonGemSpec.MaxRunLevel)
                    {
                        // At the run ceiling the curve itself agrees (T(500) is T(500)), but the family stretch prices its picks at
                        // their MAPPED level, which sits above the run level (up to the band's high edge). The Thread clamp reads
                        // those as T(500); the Gauntlet ceiling reads them on the curve. So the Gauntlet pack at 500 is at least
                        // the Thread pack, and a top level equal to the run level reproduces the Thread plan exactly.
                        Assert.IsTrue(gauntletMedian >= threadMedian, $"L500: Gauntlet {gauntletMedian} below Thread {threadMedian}");

                        var topAtRun = BuildAndShape(site, store, level, "any", Seed, l => l.WithHealthCurveTopLevel(level, 1000), passAdjust: true);
                        Assert.AreEqual(thread.Fingerprint(), topAtRun.Fingerprint(), "L500: top level 500 under a 1000 ceiling is the Thread plan");
                    }
                    else
                        Assert.IsTrue(gauntletMedian > threadMedian * 1.5, $"L{level}: Gauntlet uplifted trash {gauntletMedian} not well above Thread {threadMedian}");

                    gauntletMedians.Add(gauntletMedian);

                    // The boss: its pack term is priced from the stretched picks at their mapped levels (PoolMaxBase), so it
                    // must follow the ceiling too.
                    var threadBoss = thread.Entries.Where(e => e.Entry.Role == DungeonRole.Boss).Select(e => (double)e.MaxHealth).DefaultIfEmpty(0).Max();
                    var gauntletBoss = gauntlet.Entries.Where(e => e.Entry.Role == DungeonRole.Boss).Select(e => (double)e.MaxHealth).DefaultIfEmpty(0).Max();
                    Console.WriteLine($"L{level}: boss shaped health thread={threadBoss} gauntlet={gauntletBoss} poolMax thread={thread.Plan.PoolMaxBase} gauntlet={gauntlet.Plan.PoolMaxBase}");
                    Assert.IsTrue(threadBoss > 0 && gauntletBoss > 0, $"L{level}: positive control, both plans have a boss");

                    if (level > DungeonGemSpec.MaxRunLevel)
                    {
                        Assert.IsTrue(gauntletBoss > threadBoss * 1.5, $"L{level}: Gauntlet boss {gauntletBoss} not well above Thread {threadBoss}");
                        Assert.IsTrue(gauntlet.Plan.PoolMaxBase > thread.Plan.PoolMaxBase * 1.5, $"L{level}: Gauntlet pool max {gauntlet.Plan.PoolMaxBase} not well above Thread {thread.Plan.PoolMaxBase}");
                    }
                }

                for (var i = 1; i < levels.Length; i++)
                    Assert.IsTrue(gauntletMedians[i] > gauntletMedians[i - 1],
                        $"L{levels[i]}: Gauntlet uplifted trash health {gauntletMedians[i]} not above L{levels[i - 1]}: {gauntletMedians[i - 1]}");

                // Near and at the top level the pack is flat at the top level's value, never above it and never falling.
                var at900 = MedianUplifted(BuildAndShape(site, store, 900, "any", Seed, adjust, passAdjust: true), "L900 gauntlet");
                var at1000 = MedianUplifted(BuildAndShape(site, store, 1000, "any", Seed, adjust, passAdjust: true), "L1000 gauntlet");
                Console.WriteLine($"L900: {at900}  L1000: {at1000}");
                Assert.IsTrue(at900 >= gauntletMedians[^1], $"L900 {at900} below L850 {gauntletMedians[^1]}");
                Assert.AreEqual(at900, at1000, "L900 and L1000: every mapped level is past the top level, so both read T(1000)");
            });
        }

        /// <summary>
        /// The same consumer pin with the archetype uplift OFF. A stretched pick's health floor is then the larger of the run's
        /// trash floor (half the plan's curve target, DungeonPopulationBuilder's Target(spec.Level, limits)) and the stretch's
        /// health standard at its mapped level (the healthStandards Target(m, limits)). Either one carrying the ceiling keeps
        /// this green, so it pins the PAIR: mutation-proved by reverting both to the ceiling-less Thread form together (each
        /// alone survives, because the other still lifts the floor). With the uplift on (the test above) the uplift's standard
        /// sets the health instead.
        /// </summary>
        [TestMethod]
        public void GauntletCeiling1000_StretchedTrashShapedHealth_RisesAbove500_UpliftOff()
        {
            WithFixture((site, store) =>
            {
                Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_archetype_uplift", false), "dynamic_dungeons_archetype_uplift not registered");

                var adjust = GauntletPlanProbeTests.GauntletAdjust(1000);
                var previous = 0.0;

                foreach (var level in new[] { 600, 700, 800 })
                {
                    var thread = BuildAndShape(site, store, level, "any", Seed, null, passAdjust: false);
                    var gauntlet = BuildAndShape(site, store, level, "any", Seed, adjust, passAdjust: true);

                    Assert.IsFalse(gauntlet.Limits.ArchetypeUplift, $"L{level}: positive control, the uplift is off");

                    var stretched = gauntlet.Entries.Where(e => e.Entry.Stretched).Select(e => (double)e.MaxHealth).OrderBy(h => h).ToList();
                    var threadStretched = thread.Entries.Where(e => e.Entry.Stretched).Select(e => (double)e.MaxHealth).OrderBy(h => h).ToList();
                    Assert.IsTrue(stretched.Count > 0 && threadStretched.Count > 0, $"L{level}: positive control, the plan has stretched entries");

                    var median = stretched[stretched.Count / 2];
                    var threadMedian = threadStretched[threadStretched.Count / 2];
                    Console.WriteLine($"L{level} uplift off: stretched median shaped health thread={threadMedian} gauntlet={median} ({stretched.Count} stretched)");

                    Assert.IsTrue(median > threadMedian * 1.5, $"L{level}: Gauntlet stretched trash {median} not well above Thread {threadMedian}");
                    Assert.IsTrue(median > previous, $"L{level}: Gauntlet stretched trash {median} not above the previous level's {previous}");
                    previous = median;
                }
            });
        }
    }
}
