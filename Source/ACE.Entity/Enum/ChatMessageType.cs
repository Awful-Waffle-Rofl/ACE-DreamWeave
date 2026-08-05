namespace ACE.Entity.Enum
{
    /// <summary>
    /// The ChatMessageType categorizes chat window messages to control color and filtering.<para />
    /// Used with 02BB: Creature Message<para />
    ///     0x02, 0x0C, 0x11
    /// Used with 02BC: Creature Message (Ranged)<para />
    ///     0x0C
    /// Used with F7B0 02BD: Game Event -> Someone has sent you a @tell.<para />
    ///     0x03,
    /// Used with F7E0: Server Message
    ///     0x00, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0D, 0x10, 0x11, 0x17, 0x18
    /// </summary>
    public enum ChatMessageType: uint
    {
        /* Client-verified rendering 2026-07-24 (retail client, /chatcolors probe; screenshot
         * Content/preview/quest_stamps/clipboard_chatcolors_palette.png, pixel-sampled):
         *
         *   0x00  Broadcast        UNVERIFIED - see note below
         *   0x01  AllChannels      RGB(127,255,126)  pale green
         *   0x02  Speech           RGB(255,255,255)  white
         *   0x03  Tell             RGB(255,255, 62)  yellow
         *   0x04  OutgoingTell     RGB(210,210, 99)  dull yellow
         *   0x05  System           RGB(255,126,255)  pink / magenta
         *   0x06  Combat           RGB(255, 62, 62)  red
         *   0x07  Magic            RGB( 62,190,255)  bright blue
         *   0x08  Channel          RGB(255,149,149)  light pink
         *   0x09  ChannelSend      RGB(255,149,149)  light pink
         *   0x0A  Social           RGB(255,255, 62)  yellow
         *   0x0B  SocialSend       RGB(210,210, 99)  dull yellow
         *   0x0C  Emote            RGB(210,210,199)  pale gray
         *   0x0D  Advancement      RGB( 62,220,220)  TEAL - the only true teal in the client
         *   0x0E  Abuse            RGB(180,220,239)  pale steel-blue
         *   0x0F  Help             RGB(255, 62, 62)  red
         *   0x10  Appraisal        RGB(127,255,126)  pale green
         *   0x11  Spellcasting     RGB( 62,190,255)  bright blue
         *   0x12  Allegiance       RGB(237,146, 30)  orange
         *   0x13  Fellowship       RGB(255,255, 62)  yellow
         *   0x14  WorldBroadcast   RGB(127,255,126)  pale green
         *   0x15  CombatEnemy      RGB(255, 62, 62)  red
         *   0x16  CombatSelf       RGB(244,117,113)  salmon
         *   0x17  Recall           RGB(127,255,126)  pale green
         *   0x18  Craft            RGB(127,255,126)  pale green
         *   0x19  Salvaging        RGB(127,255,126)  pale green
         *   0x1A  (not a member - commented out below; client displays nothing)
         *   0x1B  x1B              RGB(180,220,239)  pale steel-blue
         *   0x1C  x1C              RGB(180,220,239)  pale steel-blue
         *   0x1D  x1D              RGB(180,220,239)  pale steel-blue
         *   0x1E  x1E              RGB(180,220,239)  pale steel-blue
         *   0x1F  AdminTell        RGB(255,255, 62)  yellow
         *
         * WHY THIS TABLE EXISTS: the per-member doc comments below describe the channel each type is used
         * for, and their color claims are NOT reliable for GameMessageSystemChat. The client picks a color
         * from the message-carrying opcode as well as the type byte, so a type documented from one opcode can
         * render differently through another. Two claims here were wrong by direct observation: 0x1E,
         * documented "Light Cyan", renders dark navy; 0x03 Tell renders yellow even though a real NPC tell
         * (sent via a different opcode) renders bright cyan. Trust this table for GameMessageSystemChat and
         * the member comments for channel semantics. Re-probe with the Developer "/chatcolors" command
         * rather than reasoning from the names.
         *
         * 0x00 Broadcast is UNVERIFIED. /chatcolors does send it (it is a defined member and the loop is
         * ordered from 0x00), but no line for it was observed in the probe output, so the client most likely
         * displays nothing for it through this opcode - the same behavior already documented for 0x1A. It
         * could equally have scrolled out of the capture. Confirm before relying on it. */

        /// <summary>
        /// allegiance MOTD
        /// 
        /// Failed to leave chat room.
        /// 
        /// You give Mr Muscles 10 Gems of Knowledge.
        /// 
        /// You roast the cacao beans.
        /// 
        /// Your chat privileges have been restored.
        /// 
        /// Buff Dude is online
        /// 
        /// The Mana Stone drains 3,502 points of mana from the Staff.
        /// The Staff is destroyed.
        /// 
        /// The Mana Stone gives 26,693 points of mana to the following items: Sparring Pants, Chainmail Tassets, Sollerets, Platemail Gauntlets, Sparring Shirt, Celdon Breastplate, Bracelet, Chainmail Bracers, Veil of Darkness
        /// Your items are fully charged.
        /// 
        /// The Mana Stone gives 3,123 points of mana to the following items: Frigid Bracelet, Silifi of Crimson Night, Tunic, Sleeves of Inexhaustibility, Breeches
        /// You need 3,640 more mana to fully charge your items.
        /// 
        /// LogTextTypeEnumMapper: Default
        /// </summary>
        Broadcast           = 0x00,

        /// <summary>
        /// The client sends this for message HandleActionModifyCharacterSquelch
        /// 
        /// LogTextTypeEnumMapper: AllChannels
        /// </summary>
        AllChannels         = 0x01,

        /// <summary>
        /// LogTextTypeEnumMapper: Speech
        /// </summary>
        Speech              = 0x02,

        /// <summary>
        /// 
        /// Via F7E0:
        /// Buff Dude has added you to their home's guest list.  You now have access to their home.,
        /// Buff Dude has granted you access to their home's storage.,
        /// Buff DudeRipley has removed all house guests, including yourself.,
        /// 
        /// LogTextTypeEnumMapper: Tell
        /// </summary>
        Tell                = 0x03,

        /// <summary>
        /// You tell ...
        /// 
        /// LogTextTypeEnumMapper: Speech_Direct_Send
        /// </summary>
        OutgoingTell        = 0x04,

        /// <summary>
        /// Warning!  You have not paid your maintenance costs for the last 30 day maintenance period.  Please pay these costs by this deadline or you will lose your house, and all your items within it.
        /// Some Guy has discovered the Wayfarer's Pearl!
        /// 
        /// LogTextTypeEnumMapper: System
        /// </summary>
        System              = 0x05,

        /// <summary>
        /// You receive 18 points of periodic nether damage.
        /// You suffer 4 points of minor impact damage.
        /// Dirty Fighting! Big Guy delivers a Unbalancing Blow to Armored Tusker!,
        /// 
        /// LogTextTypeEnumMapper: Combat
        /// </summary>
        Combat              = 0x06,

        /// <summary>
        /// Prismatic Amuli Leggings cast Epic Quickness on you
        /// The spell Brogard's Defiance on Baggy Pants has expired.
        /// You resist the spell cast by Someone
        /// Some Guy tried and failed to cast a spell at you!
        /// You cast Incantation of Revitalize Self and restore 172 points of your stamina.
        /// 
        /// LogTextTypeEnumMapper: Magic
        /// </summary>
        Magic                = 0x07,

        /// <summary>
        /// Light Pink Text - Both of the following two were associated with the following channels: admin, audit, av1, av2, av3, sentinel
        /// output: You say on the [channel name] channel, "message here"
        /// 
        /// LogTextTypeEnumMapper: Channel
        /// </summary>
        Channel             = 0x08,
                
        /// <summary>
        /// LogTextTypeEnumMapper: Channel_Send
        /// </summary>
        ChannelSend         = 0x09,

        /// <summary>
        /// Bright Yellow Text
        /// LogTextTypeEnumMapper: Social
        /// </summary>
        Social              = 0x0A,

        /// <summary>
        /// Light Yellow Text
        /// LogTextTypeEnumMapper: Social_Send
        /// </summary>
        SocialSend          = 0x0B,

        /// <summary>
        /// Via 0x02BB:
        /// All's quiet on this front!Enum_000C = 0x000C 
        /// Lemme alone.  And keep my door closed!
        /// Haha!
        /// Au virinaa! Au... baeaa?
        /// Suvani hasode, Metresa, tamaa Dar Hallae.
        /// Arena One is now available for new warriors!
        /// 
        /// Via 0x02BC:
        /// Did you know there are other people who collect other items, like gromnie teeth?  You might find them in some towns.  I'll take them, but I won't pay you for them!
        /// I collect only phyntos wasp wings.  I'll reward you well for any you happen to have.
        /// Avoid the great crater, I say; the caves there are none too pleasant, even for me!  Oh, the fumes!
        /// If we cannot think of anything quieter and tidier than that... We are not any better than these humans...
        /// There are other beings that will exchange items and currency with you for the remains of creatures such as husks and skeletal pieces.
        /// Very rarely, you can get a perfect red phyntos wasp wing.  If you can give one to me, I'll pay you for it.  I will also pay you for the tails of rats.
        /// I can take the hides of certain creatures and turn them into items of value.
        /// Some say that two brews based on two yeasts of varying qualities are indistinguishable from one another. To those people I say, "You have the taste of a Drudge and the brains of a Banderling!" Heathens, I say. Heathens!
        /// Phyntos wasps are not my favorite creature, but I do admire the wings.
        /// Have you a drudge charm, swamp stone, rat tail, or such? I'll pay you good money or items if you give them to me. They're hard to come by.
        /// Have you the skins of armoredillos, gromnies, or reedsharks?  I can use them in my craft.
        /// Damnable Mukkir...  They get everywhere...
        /// 
        /// LogTextTypeEnumMapper: Emote
        /// </summary>
        Emote               = 0x0C,

        /// <summary>
        /// You are now level 5!
        /// Your base Heavy Weapons skill is now 70!
        /// 
        /// You are now level 68!
        /// You have 100,647,873 experience points and 3 skill credits available to raise skills and attributes.
        /// You will earn another skill credit at level 70.,
        /// 
        /// LogTextTypeEnumMapper: Advancement
        /// </summary>
        Advancement         = 0x0D,

        /// <summary>
        /// Light Cyan (skyblue?) Text - Would seem to be associated with the following channel: Abuse
        /// (client-verified: pale steel-blue RGB(180,220,239), not cyan - see the table at the top)
        /// output: You say on the Abuse channel, "message here"
        /// 
        /// LogTextTypeEnumMapper: Abuse
        /// </summary>
        Abuse               = 0x0E,

        /// <summary>
        /// Red Text - Possibly OutgoingHelpSay, not even sure if that showed up on the client when you sent out an urgent help command
        /// 
        /// LogTextTypeEnumMapper: Help
        /// </summary>
        Help                = 0x0F,

        /// <summary>
        /// Mr Sneaky tried and failed to assess you!
        /// 
        /// LogTextTypeEnumMapper: Appraisal
        /// </summary>
        Appraisal           = 0x10,

        /// <summary>
        /// Via 02BB:
        /// Malar Quaril
        /// Puish Zharil
        /// 
        /// Via F7E0:
        /// Aetheria surges on Pyreal Target Drudge with the power of Surge of Affliction!
        /// The cloak of Some Guy weaves the magic of Cloaked in Skill!
        /// 
        /// LogTextTypeEnumMapper: Spellcasting
        /// </summary>
        Spellcasting        = 0x11,

        /// <summary>
        /// Fellow warriors, aid me!
        /// 
        /// LogTextTypeEnumMapper: Allegiance
        /// </summary>
        Allegiance           = 0x12,

        /// <summary>
        /// Bright Yellow Text
        /// 
        /// LogTextTypeEnumMapper: Fellowship
        /// </summary>
        Fellowship           = 0x13,

        /// <summary>
        /// Green Text
        /// 
        /// LogTextTypeEnumMapper: World_Broadcast
        /// </summary>
        WorldBroadcast      = 0x14,

        /// <summary>
        /// Red Text
        /// 
        /// LogTextTypeEnumMapper: Combat_Enemy
        /// </summary>
        CombatEnemy         = 0x15,

        /// <summary>
        /// Pink Text
        /// (client-verified: salmon RGB(244,117,113) - a different pink from 0x05 System's magenta
        /// RGB(255,126,255), which the shared "Pink" wording hides)
        ///
        /// LogTextTypeEnumMapper: Combat_Self
        /// </summary>
        CombatSelf          = 0x16,

        /// <summary>
        /// Player is recalling home.
        /// 
        /// LogTextTypeEnumMapper: Recall
        /// </summary>
        Recall              = 0x17,

        /// <summary>
        /// Super Tink fails to apply the Fire Opal Salvage (workmanship 10.00) to the White Sapphire Fire Baton. The target is destroyed.
        /// Super Tink successfully applies the Steel Salvage (workmanship 10.00) to the Silver Signet Crown.,
        /// 
        /// LogTextTypeEnumMapper: Craft
        /// </summary>
        Craft               = 0x18,

        /// <summary>
        /// Green Text
        /// LogTextTypeEnumMapper: Salvaging
        /// </summary>
        Salvaging           = 0x19,

        /// <summary>
        /// Does Nothing - Unknown purpose/filter?
        /// Client doesn't display it. Commenting out because why offer it as an option?
        /// </summary>
        // x1A                 = 0x1A,

        /// <summary>
        /// Light cyan(sky blue) - Unknown purpose/filter?
        /// (client-verified: 0x1B, 0x1C and 0x1D all render pale steel-blue RGB(180,220,239), not cyan)
        /// </summary>
        x1B                 = 0x1B,
        x1C                 = 0x1C,
        x1D                 = 0x1D,

        /// <summary>
        /// Light Cyan (skyblue?) Text - Unknown purpose/filter?
        /// (client-verified: pale steel-blue RGB(180,220,239). Sent through GameMessageSystemChat it reads
        /// as dark navy against the chat background - this claim is what sent the quest stamp message
        /// hunting for a readable teal in the first place.)
        /// </summary>
        x1E                 = 0x1E,

        /// <summary>
        /// Bright Yellow Text
        /// 
        /// LogTextTypeEnumMapper: Admin_Tell
        /// </summary>
        AdminTell           = 0x1F,
    }

    public static class ChatMessageTypeExtensions
    {
        public static SquelchMask ToMask(this ChatMessageType type)
        {
            if (type == ChatMessageType.AllChannels)
                return SquelchMask.AllChannels;
            else
                return (SquelchMask)(1 << (int)type);
        }
    }
}
