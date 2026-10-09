using System;
using System.Collections.Generic;

namespace ACE.Server.Managers.Market
{
    /// <summary>The values ARE the market_listing.status column.</summary>
    public enum MarketListingStatus
    {
        Active = 0,
        Sold = 1,
        Delisted = 2,
        Invalidated = 3,
    }

    /// <summary>
    /// The values ARE the market_transaction.status column. That column is a plain int, so a new
    /// member needs no migration - but the column COMMENT in
    /// Database/Updates/Shard/2026-08-30-01-Add-Market-Transaction.sql was written before value 4
    /// existed and is not updated by an already-applied script. This enum is the authority.
    /// </summary>
    public enum MarketTransactionStatus
    {
        Pending = 0,
        Completed = 1,
        Refunded = 2,
        Failed = 3,

        /// <summary>
        /// The buyer's debit came back with the ledger state UNKNOWN: the pyreals may or may not have
        /// left the pool, and no unwind is safe on a guess.
        ///
        /// DELIBERATELY NOT Failed. Failed means the buyer provably was not charged - an operator can
        /// read the whole row set and act on it. These rows are the only ones where a buyer may have
        /// paid and received nothing, and boot recovery cannot help: it scans Pending rows only, and
        /// this row IS resolved. Reconcile it by hand against the [MARKET] LEDGER STATE UNKNOWN line
        /// and the account_bank row it names.
        /// </summary>
        DebitLedgerUnknown = 4,
    }

    /// <summary>The values ARE the market_buy_order.status column (WANTED-DESIGN 5.3).</summary>
    public enum MarketBuyOrderStatus
    {
        /// <summary>Row written, debit not yet confirmed. Boot recovery refunds these on the same rule as a Pending transaction.</summary>
        Pending = 0,
        Active = 1,
        Filled = 2,
        Cancelled = 3,
        Expired = 4,
        /// <summary>Debit refused at placement: the buyer provably was not charged and nothing is held.</summary>
        Failed = 5,
        /// <summary>
        /// The debit came back with the ledger state UNKNOWN. NOT Failed, for exactly the reason
        /// MarketTransactionStatus.DebitLedgerUnknown is not: it is the only status under which a buyer
        /// may have paid and holds nothing, and it is reconciled by hand from the LEDGER STATE UNKNOWN log line.
        /// </summary>
        DebitLedgerUnknown = 6,
    }

    /// <summary>
    /// WHAT a Wanted order is for. The values ARE the market_buy_order.order_Kind column.
    ///
    /// SalvageBag MUST stay 0. Every row written before this column existed defaults to 0, and every
    /// default-valued path in C# - a freshly constructed DTO, a row read back with the column absent -
    /// lands on 0 as well. Renumbering would silently reinterpret both as hammer orders.
    ///
    /// The two kinds are matched by different predicates and are NOT interchangeable, even for the same
    /// material: a hammer is <see cref="ACE.Entity.Enum.ItemType.TinkeringMaterial"/> carrying the same
    /// MaterialType as its bag, so the kind is the only thing that keeps a bag order from being filled
    /// with a Hammer worth ten of them. See MarketSalvageMaterials.MatchesOrder.
    /// </summary>
    public enum MarketBuyOrderKind
    {
        /// <summary>An ordinary full salvage bag: Structure at MaxStructure, no SalvageToolCharges at all.</summary>
        SalvageBag = 0,

        /// <summary>
        /// A FULL-CHARGE multi-charge salvage Hammer (SalvageToolCharges above 0, Structure at
        /// MaxStructure). Only the five materials in SalvageForge.MaterialTable have one.
        /// </summary>
        SalvageHammer = 1,
    }

    /// <summary>The values ARE the market_transaction.channel column.</summary>
    public enum MarketChannel
    {
        Web = 0,
        InGame = 1,
    }

    /// <summary>
    /// The values ARE the market_rejected_attempt.operation column. Which market operation the actor
    /// was refused, so a reason code shared by two operations (count_unavailable, not_owner) still
    /// says what the player was trying to do.
    /// </summary>
    public enum MarketRejectOperation
    {
        List = 0,
        Buy = 1,
        Delist = 2,
        PlaceOrder = 3,
        FillOrder = 4,
        CancelOrder = 5,
    }

    /// <summary>
    /// Every way a market operation can refuse. STABLE: each member's wire code is published in
    /// Docs/Market/market-api-v1.yaml, so a member may be added but a code may never be renamed.
    /// </summary>
    public enum MarketError
    {
        None = 0,
        ListingNotActive,
        InsufficientFunds,
        VaultUnavailable,

        /// <summary>
        /// The BUYER's vault has no room for the item being purchased. A refusal, not a transient
        /// condition like VaultUnavailable - the buyer must free a slot before buying, so a client must
        /// not retry automatically.
        /// </summary>
        VaultFull,

        RateLimited,
        PriceChanged,
        NotOwner,
        BadCredentials,
        CountUnavailable,
        ItemNotFound,
        InvalidPrice,
        AlreadyListed,
        Timeout,
        Disabled,
        ServerError,

        /// <summary>
        /// A currency move that neither committed nor provably failed - the player's bank may or may
        /// not have been charged.
        ///
        /// Inside the server it is what makes MarketManager.Buy resolve the row to
        /// <see cref="MarketTransactionStatus.DebitLedgerUnknown"/> instead of Failed. On the wire it
        /// answers the same HTTP 500 as ServerError but carries its OWN code, because a client must
        /// not tell the player "something went wrong, try again" about a purchase that may have taken
        /// their money.
        /// </summary>
        LedgerUnknown,

        /// <summary>
        /// The vault row the caller asked to barrel carries an ACTIVE listing. A refusal, not a
        /// transient condition: the seller has to take the listing down first, so a client must not
        /// retry it.
        /// </summary>
        ItemListed,

        /// <summary>
        /// The account's barrel container could not be reached - it has not finished loading, could
        /// not be created, or is full. Transient in every case except the last, and the client is
        /// told to try again in a moment either way.
        /// </summary>
        BarrelUnavailable,

        /// <summary>
        /// The account already holds the most ACTIVE listings market_max_listings_per_account allows.
        /// A refusal the seller must act on - take one down - not a transient condition, so a client
        /// must not retry it.
        ///
        /// Distinct from <see cref="AlreadyListed"/>: AlreadyListed means THIS item is already up;
        /// ListingLimit means the ACCOUNT is at its cap and no new item can go up until one comes down.
        /// </summary>
        ListingLimit,

        /// <summary>This account already has an Active order for that material. Cancel it to re-post.</summary>
        OrderExists,
        OrderNotActive,
        /// <summary>The seller's vault holds fewer matching full bags than the fill asked for. Nothing moved.</summary>
        NoMatchingItems,
        InvalidMaterial,
        /// <summary>The market_buy_orders_enabled kill switch is off. Cancel still works.</summary>
        BuyOrdersDisabled,
        /// <summary>A fill is in flight on this order; a cancel must wait a moment. Transient.</summary>
        OrderBusy,

        /// <summary>
        /// A SalvageHammer order named a material that has no Hammer. Distinct from
        /// <see cref="InvalidMaterial"/>, which means the market knows no salvage of that material at
        /// all: this one names a perfectly orderable material for which only the BAG kind exists, and
        /// the caller's next step is to re-post it as a bag order rather than to pick another material.
        ///
        /// Refused at placement rather than accepted, because an order nothing in the world can fill
        /// would sit on the Wanted page holding the buyer's escrow until it expired.
        /// </summary>
        NoHammerForMaterial,

        /// <summary>The slug does not exist, is malformed, or names a character with no active link. Also answered for a missing or deleted character, so a slug cannot be probed for WHY it is absent.</summary>
        SheetNotFound,

        /// <summary>The charsheet_enabled kill switch is off.</summary>
        SheetsDisabled,

        /// <summary>The sheet build could not complete right now (an online build is not ticking, an offline load was refused or timed out, or the offline build cap is full). Transient.</summary>
        SheetBusy,

        /// <summary>The account is not a server Admin, no longer exists, or is banned. /v1/admin routes only.</summary>
        NotAdmin,

        /// <summary>The admin_web_enabled kill switch is off. Only ever answered to an Admin (see AdminAuthorizer/MapAdmin ordering).</summary>
        AdminDisabled,

        /// <summary>POST /v1/admin/settings/{key} only: {key} fails the route pattern or is in no Default*Properties dictionary.</summary>
        SettingNotFound,

        /// <summary>POST /v1/admin/settings/{key} only: the key's config-metadata.tsv row is sensitive, or has no row (or no metadata is loaded). Refused before any value is read.</summary>
        SettingSensitive,

        /// <summary>POST /v1/admin/settings/{key} only: the body is over 8 KiB or unreadable, value or expected_current is absent/null, or value fails the type's parse.</summary>
        InvalidSettingValue,

        /// <summary>POST /v1/admin/settings/{key} only: expected_current differs from the live value. The envelope carries current_value.</summary>
        SettingChanged,

        /// <summary>POST /v1/admin/announce only: the text is missing, empty after normalization, longer than 500 characters, or the body is over 8 KiB or unreadable.</summary>
        InvalidAnnouncement,

        /// <summary>/v1/admin/commands routes only: the admin_web_commands_enabled kill switch is off. Only ever answered to an Admin, and only after admin_web_enabled.</summary>
        CommandsDisabled,

        /// <summary>POST /v1/admin/commands/run only: the body is unreadable, over 8 KiB, malformed or has no text, or the text is empty, whitespace-only, over 1000 characters, or contains a control character, a format character, a line or paragraph separator, or an unpaired surrogate.</summary>
        InvalidCommandText,

        /// <summary>POST /v1/admin/commands/run only: no command is registered under that name (sudo never is).</summary>
        UnknownCommand,

        /// <summary>POST /v1/admin/commands/run only: the command's access level is above the account's, or (in-game character bucket) the character's flags do not permit it.</summary>
        CommandNotPermitted,

        /// <summary>POST /v1/admin/commands/run only: the command is not runnable from the web (bucket in_game_only, or its live handler no longer matches the classified one).</summary>
        CommandInGameOnly,

        /// <summary>POST /v1/admin/commands/run only: this account already has a web command running, or the web worker is busy. The envelope carries busy_command.</summary>
        CommandBusy,

        /// <summary>POST /v1/admin/commands/run only: an in-game character command, and no eligible character of this account is online (checked before enqueue and again on the world thread).</summary>
        NoCharacterOnline,

        /// <summary>POST /v1/admin/commands/run only: the command was not started within the run timeout. Guaranteed not run.</summary>
        CommandNotStarted,

        /// <summary>/v1/admin/world-events routes only: the world_events_enabled switch is off.</summary>
        WorldEventsDisabled,

        /// <summary>POST /v1/admin/world-events/start only: a run is already live (hard cap of one). The envelope carries run_id.</summary>
        WorldEventRunning,

        /// <summary>POST /v1/admin/world-events/stop only: no run is live.</summary>
        WorldEventNotRunning,

        /// <summary>POST /v1/admin/world-events/stop only: the live run is not expected_run_id. The envelope carries the live run_id.</summary>
        WorldEventRunChanged,

        /// <summary>POST /v1/admin/world-events/start and preview only: the location failed server-side validation. The envelope carries reason.</summary>
        InvalidLocation,

        /// <summary>POST /v1/admin/world-events/start and preview only: the source is not startable from the web (webStartable false).</summary>
        SourceNotWebStartable,

        /// <summary>/v1/admin/world-events writes only: the body is unreadable, a field is out of range, or the composer refused. The envelope carries reason.</summary>
        WorldEventRefused,

        /// <summary>GET /v1/accounts/me/suit/inventory only: the account's vault is still loading (or the shard could not serve it right now). Transient; the client retries. Distinct from VaultUnavailable so the suit builder can show a loading state.</summary>
        VaultLoading,

        /// <summary>The /v1/accounts/me/suit/inventory and /v1/accounts/me/characters/{guid}/suit-profile routes only: the suit_builder_enabled switch is off.</summary>
        SuitBuilderDisabled,

        /// <summary>Suit transfers: the named character is not online on this account's session (or is logging out). Also a rejected transfer's reason when it went offline before the world thread started it.</summary>
        CharacterOffline,

        /// <summary>Suit transfers (a rejected transfer's reason): the character is not standing where the account vault may be used (account_vault_allowlist / account_vault_denylist).</summary>
        NotInVaultArea,

        /// <summary>Suit transfers (a rejected transfer's reason): the character's main pack lacks the free slots, or the character the burden headroom, for the whole batch. Nothing was withdrawn.</summary>
        PackFull,

        /// <summary>POST /v1/accounts/me/suit/transfers only: this account already has a transfer queued or running.</summary>
        TransferInProgress,

        /// <summary>POST /v1/accounts/me/suit/transfers only: more than 24 lines, or more than 24 objects in total across the lines.</summary>
        TooManyItems,

        /// <summary>Suit transfers (a rejected transfer's reason): the character is busy, trading, dying, teleporting, or inside the PK timer.</summary>
        CharacterBusy,

        /// <summary>Suit transfers (a rejected transfer's reason): the character is standing at a vault vendor (the vault panel), which must not run beside a transfer.</summary>
        VaultPanelOpen,

        /// <summary>POST /v1/accounts/me/suit/transfers only: the body is unreadable or a field is missing or out of range.</summary>
        InvalidTransfer,

        /// <summary>GET /v1/accounts/me/suit/transfers/{transfer_id} only: no such transfer on this account (unknown, expired, or another account's).</summary>
        TransferNotFound,

        /// <summary>Suit transfers (a rejected transfer's reason): the character is in a PvP match or under a PvP template, the facet switch's first two gates.</summary>
        InPvp,
    }

    /// <summary>
    /// The single translation from MarketError to its wire code and its player-facing sentence,
    /// here rather than at the call sites so the API and the /market commands cannot drift.
    /// </summary>
    public static class MarketErrorCodes
    {
        public static string ToCode(MarketError error)
        {
            switch (error)
            {
                case MarketError.ListingNotActive:  return "listing_not_active";
                case MarketError.InsufficientFunds: return "insufficient_funds";
                case MarketError.VaultUnavailable:  return "vault_unavailable";
                case MarketError.VaultFull:         return "vault_full";
                case MarketError.RateLimited:       return "rate_limited";
                case MarketError.PriceChanged:      return "price_changed";
                case MarketError.NotOwner:          return "not_owner";
                case MarketError.BadCredentials:    return "bad_credentials";
                case MarketError.CountUnavailable:  return "count_unavailable";
                case MarketError.ItemNotFound:      return "item_not_found";
                case MarketError.InvalidPrice:      return "invalid_price";
                case MarketError.AlreadyListed:     return "already_listed";
                case MarketError.Timeout:           return "timeout";
                case MarketError.Disabled:          return "disabled";
                case MarketError.ServerError:       return "server_error";
                case MarketError.LedgerUnknown:     return "ledger_unknown";
                case MarketError.ItemListed:        return "item_listed";
                case MarketError.BarrelUnavailable: return "barrel_unavailable";
                case MarketError.ListingLimit:      return "listing_limit";
                case MarketError.OrderExists:       return "order_exists";
                case MarketError.OrderNotActive:    return "order_not_active";
                case MarketError.NoMatchingItems:   return "no_matching_items";
                case MarketError.InvalidMaterial:   return "invalid_material";
                case MarketError.BuyOrdersDisabled: return "buy_orders_disabled";
                case MarketError.OrderBusy:         return "order_busy";
                case MarketError.NoHammerForMaterial: return "no_hammer_for_material";
                case MarketError.SheetNotFound:     return "sheet_not_found";
                case MarketError.SheetsDisabled:    return "sheets_disabled";
                case MarketError.SheetBusy:         return "sheet_busy";
                case MarketError.NotAdmin:          return "not_admin";
                case MarketError.AdminDisabled:     return "admin_disabled";
                case MarketError.SettingNotFound:   return "setting_not_found";
                case MarketError.SettingSensitive:  return "setting_sensitive";
                case MarketError.InvalidSettingValue: return "invalid_setting_value";
                case MarketError.SettingChanged:    return "setting_changed";
                case MarketError.InvalidAnnouncement: return "invalid_announcement";
                case MarketError.CommandsDisabled:  return "commands_disabled";
                case MarketError.InvalidCommandText: return "invalid_command_text";
                case MarketError.UnknownCommand:    return "unknown_command";
                case MarketError.CommandNotPermitted: return "command_not_permitted";
                case MarketError.CommandInGameOnly: return "command_in_game_only";
                case MarketError.CommandBusy:       return "command_busy";
                case MarketError.NoCharacterOnline: return "no_character_online";
                case MarketError.CommandNotStarted: return "command_not_started";
                case MarketError.WorldEventsDisabled: return "world_events_disabled";
                case MarketError.WorldEventRunning: return "world_event_running";
                case MarketError.WorldEventNotRunning: return "world_event_not_running";
                case MarketError.WorldEventRunChanged: return "world_event_run_changed";
                case MarketError.InvalidLocation:   return "invalid_location";
                case MarketError.SourceNotWebStartable: return "source_not_web_startable";
                case MarketError.WorldEventRefused: return "world_event_refused";
                case MarketError.VaultLoading:      return "vault_loading";
                case MarketError.SuitBuilderDisabled: return "suit_builder_disabled";
                case MarketError.CharacterOffline:  return "character_offline";
                case MarketError.NotInVaultArea:    return "not_in_vault_area";
                case MarketError.PackFull:          return "pack_full";
                case MarketError.TransferInProgress: return "transfer_in_progress";
                case MarketError.TooManyItems:      return "too_many_items";
                case MarketError.CharacterBusy:     return "character_busy";
                case MarketError.VaultPanelOpen:    return "vault_panel_open";
                case MarketError.InvalidTransfer:   return "invalid_transfer";
                case MarketError.TransferNotFound:  return "transfer_not_found";
                case MarketError.InPvp:             return "in_pvp";
                default:                            return "server_error";
            }
        }

        /// <summary>One sentence, for chat and for the API's human-readable message field.</summary>
        public static string ToMessage(MarketError error)
        {
            switch (error)
            {
                case MarketError.ListingNotActive:  return "That listing is no longer available.";
                case MarketError.InsufficientFunds: return "You do not have enough banked trade notes.";
                case MarketError.VaultUnavailable:  return "A vault is unavailable, try again in a moment.";
                case MarketError.VaultFull:         return "Your vault is full. Withdraw something from it before buying.";
                case MarketError.RateLimited:       return "You are using the market too quickly.";
                case MarketError.PriceChanged:      return "The price changed before your purchase went through.";
                case MarketError.NotOwner:          return "That listing is not yours.";
                case MarketError.BadCredentials:    return "That account name or password is not correct.";
                case MarketError.CountUnavailable:  return "That many are no longer available.";
                case MarketError.ItemNotFound:      return "That is not in your vault.";
                case MarketError.InvalidPrice:      return "That price is not allowed. It must be a whole number of trade notes within the market's limits.";
                case MarketError.AlreadyListed:     return "That is already listed.";
                case MarketError.Timeout:           return "The market is busy, try again in a moment.";
                case MarketError.Disabled:          return "The market is not enabled on this server.";
                case MarketError.LedgerUnknown:     return "The market could not confirm your payment. Do not retry - check your bank balance and ask an admin.";
                case MarketError.ItemListed:        return "Take the listing down before you barrel that.";
                case MarketError.BarrelUnavailable: return "The barrel is unavailable, try again in a moment.";
                case MarketError.ListingLimit:      return "You already have as many listings up as your account allows. Take one down, or raise your limit in game with /market upgrade.";
                case MarketError.OrderExists:       return "You already have an open order for that material. Cancel it first to post a new one.";
                case MarketError.OrderNotActive:    return "That order is no longer open.";
                case MarketError.NoMatchingItems:   return "Your vault does not hold that many full bags of that material.";
                case MarketError.InvalidMaterial:   return "That is not a material the market knows salvage of.";
                case MarketError.BuyOrdersDisabled: return "Buy orders are not enabled on this server.";
                case MarketError.OrderBusy:         return "A sale into that order is finishing. Try again in a moment.";
                case MarketError.NoHammerForMaterial: return "There is no salvage hammer in that material. Order bags of it instead.";
                case MarketError.SheetNotFound:      return "That character sheet does not exist or is not public.";
                case MarketError.SheetsDisabled:     return "Character sheets are not enabled on this server.";
                case MarketError.SheetBusy:          return "That character sheet is busy, try again in a moment.";
                case MarketError.NotAdmin:           return "That account is not a server administrator.";
                case MarketError.AdminDisabled:      return "The web admin panel is turned off on this server.";
                case MarketError.SettingNotFound:    return "There is no server setting with that key.";
                case MarketError.SettingSensitive:   return "That setting holds a secret and cannot be edited from the web.";
                case MarketError.InvalidSettingValue: return "That value is not valid for this setting's type.";
                case MarketError.SettingChanged:     return "That setting changed before your edit was applied.";
                case MarketError.InvalidAnnouncement: return "That announcement is empty or longer than 500 characters.";
                case MarketError.CommandsDisabled:   return "The web command console is turned off on this server.";
                case MarketError.InvalidCommandText: return "That command text is missing, empty, longer than 1000 characters, or contains a control character, an invisible formatting character, a line or paragraph separator, or an unpaired surrogate.";
                case MarketError.UnknownCommand:     return "There is no server command with that name.";
                case MarketError.CommandNotPermitted: return "That command needs a higher access level than this account or its online character has. If the account was promoted recently, relog the character.";
                case MarketError.CommandInGameOnly:  return "That command is not available from the web console.";
                case MarketError.CommandBusy:        return "The web console is already running a command. Try again when it finishes.";
                case MarketError.NoCharacterOnline:  return "No character of this account is online, so that in-game character command was not run.";
                case MarketError.CommandNotStarted:  return "The world did not start that command in time. It was not run.";
                case MarketError.WorldEventsDisabled: return "World events are turned off on this server.";
                case MarketError.WorldEventRunning:  return "A world event is already running. Stop it before starting another.";
                case MarketError.WorldEventNotRunning: return "No world event is running.";
                case MarketError.WorldEventRunChanged: return "The running world event changed since this page last read it. Check the status before stopping.";
                case MarketError.InvalidLocation:    return "That location is not valid for this world event.";
                case MarketError.SourceNotWebStartable: return "That event source cannot be started from the web.";
                case MarketError.WorldEventRefused:  return "The server refused that world event request.";
                case MarketError.VaultLoading:       return "Your vault is still loading, try again in a moment.";
                case MarketError.SuitBuilderDisabled: return "The suit builder is turned off on this server.";
                case MarketError.CharacterOffline:   return "That character is not online. Log it in to move items from your vault.";
                case MarketError.NotInVaultArea:     return "That character is not standing where your vault can be used.";
                case MarketError.PackFull:           return "That character's pack does not have the room or the burden for everything asked for. Nothing was moved.";
                case MarketError.TransferInProgress: return "A vault transfer for this account is already running. Wait for it to finish.";
                case MarketError.TooManyItems:       return "That is too many items for one transfer. Move at most 24 at a time.";
                case MarketError.CharacterBusy:      return "That character is too busy to receive items right now.";
                case MarketError.VaultPanelOpen:     return "That character is standing at a vault vendor. Step away from it first.";
                case MarketError.InvalidTransfer:    return "That transfer request is missing a field or has one out of range.";
                case MarketError.TransferNotFound:   return "There is no such transfer on this account.";
                case MarketError.InPvp:              return "That character is in a PvP match or under a PvP template and cannot receive items from the vault.";
                default:                            return "The market could not complete that request.";
            }
        }
    }

    /// <summary>
    /// Who is acting, flattened away from Player so every market type stays unit-testable.
    /// AccountId 0 means unresolved and must be denied rather than treated as anyone.
    /// </summary>
    public readonly struct MarketActor
    {
        public uint AccountId { get; }

        public uint CharacterGuid { get; }

        public string Name { get; }

        public MarketActor(uint accountId, uint characterGuid, string name)
        {
            AccountId = accountId;
            CharacterGuid = characterGuid;
            Name = name ?? string.Empty;
        }
    }

    /// <summary>
    /// The searchable projection captured at listing time (DESIGN 5.1) and serialized to
    /// market_listing.snapshot_Json. Handed to the web app verbatim, so adding a field is a
    /// contract change. Search runs against this, never against live objects.
    ///
    /// It is refreshed by the snapshot backfill, which re-projects ACTIVE listings from their live
    /// items. That runs two ways: automatically, shortly after each world start, over listings whose
    /// <see cref="SnapshotVersion"/> is below <see cref="MarketSnapshot.CurrentVersion"/>; and on
    /// demand, when an operator runs /marketbackfill run. So a CLOSED listing carries forever what
    /// it captured, while an Active one may carry a projection newer than its created_At - which is
    /// why every "added on date X" note below says what a listing from before X reads and not simply
    /// that it is stuck there.
    /// </summary>
    public sealed class ListingSnapshot
    {
        /// <summary>
        /// Which projection built this document (<see cref="MarketSnapshot.CurrentVersion"/>), so
        /// staleness is a fact rather than a guess.
        ///
        /// ABSENT (null) means "predates versioning", which is every snapshot stored before
        /// 2026-09-06 and therefore every snapshot that is missing <see cref="Slots"/>,
        /// <see cref="EquipmentSet"/> and <see cref="WeaponClass"/>. A stored 1 is newer than that
        /// but still older than <see cref="Structure"/>, <see cref="MaxStructure"/> and
        /// <see cref="SalvageToolCharges"/>, which arrived with version 2. It has to be a number rather
        /// than an inference from the fields present, because JsonOptions omits nulls: absent and
        /// null are the SAME BYTES in the stored document, so nothing downstream can tell "predates
        /// the field" from "legitimately has none". This field is the one exception, and only
        /// because null and 0 mean the SAME thing here - "older than version 1" - so its own
        /// absence carries no ambiguity to resolve.
        ///
        /// NULLABLE so a placeholder omits it entirely rather than writing a version it has not
        /// earned; a placeholder is deliberately left unstamped - see
        /// <see cref="MarketSnapshot.CurrentVersion"/> - so it is picked up again later.
        /// </summary>
        public int? SnapshotVersion { get; set; }

        public string Name { get; set; }
        public uint Wcid { get; set; }
        public int ItemType { get; set; }
        public string ItemTypeName { get; set; }
        public int? MaterialType { get; set; }
        public string MaterialName { get; set; }

        /// <summary>
        /// The PER UNIT workmanship a player is shown (1 to 10), NOT the raw accumulated
        /// PropertyInt.ItemWorkmanship, which on a salvage bag is the sum over every unit in it and
        /// reads as e.g. 117. See MarketAppraisal.PerUnitWorkmanship for the exact rule.
        ///
        /// WAS int?. An older snapshot_Json row stores a bare integer here; System.Text.Json reads a
        /// JSON integer into a double? without complaint, so nothing had to be rewritten for the
        /// retype and nothing was. An old listing keeps whatever it captured until it is re-listed or
        /// an operator runs /marketbackfill run, and a consumer must accept a whole number here
        /// either way.
        /// </summary>
        public double? Workmanship { get; set; }

        /// <summary>The denominator behind Workmanship. Carried so the web can show a bag's unit count.</summary>
        public int? NumItemsInMaterial { get; set; }

        /// <summary>PropertyInt.UiEffects - the enchantment glow the client draws over the icon.</summary>
        public int? UiEffects { get; set; }

        /// <summary>
        /// PropertyInt.DamageType, as its numeric flags value. Populated for EVERY item that carries
        /// the property, deliberately including a Caster: MarketAppraisal's panel lines omit a
        /// caster's damage type entirely (AddWeapon returns early for WeenieType.Caster), so this
        /// field is the only channel by which a caster's element reaches the web.
        /// </summary>
        public int? DamageType { get; set; }

        /// <summary>PropertyFloat.DamageMod, as the RAW multiplier (2.63), not a percentage.</summary>
        public double? DamageMod { get; set; }

        public int? ElementalDamageBonus { get; set; }

        /// <summary>PropertyFloat.ElementalDamageMod, as the RAW multiplier (1.08), not a percentage.</summary>
        public double? ElementalDamageMod { get; set; }

        /// <summary>
        /// PropertyDataId.ProcSpell - the "Surge" a cloak or a piece of aetheria fires. It is NOT in
        /// the biota spellbook, which is why it was missing from SpellIds before.
        /// </summary>
        public int? ProcSpellId { get; set; }

        public string ProcSpellName { get; set; }

        public int? WieldSkillType { get; set; }
        public int? WieldDifficulty { get; set; }
        public int? ArmorLevel { get; set; }
        public int? DamageHigh { get; set; }
        public int? DamageLow { get; set; }
        public int? Value { get; set; }
        public int? EncumbranceVal { get; set; }
        public int? MaxStackSize { get; set; }

        /// <summary>
        /// The listed object's own StackSize at listing time - how many units the ONE biota behind
        /// this listing contains. Null for a ledger listing, where the listing's own count already IS
        /// the number of units and there is no biota to read a stack size from.
        ///
        /// It matters because a stored stack that did not collapse to the ledger is listed and sold
        /// WHOLE at a count of 1: a listing of "1 at 50 MMD" over a stored stack of 200 hands over all
        /// 200 for 50 MMD, so a client rendering "Price each" against the count alone misprices it by
        /// the stack size.
        ///
        /// ADDED 2026-09-04. A snapshot is captured at listing time and refreshed by the backfill (automatically
        /// after a world start, or by /marketbackfill run), which touches ACTIVE listings only - so a listing
        /// created before that date returns null here unless a backfill has since re-projected it,
        /// and a CLOSED listing created before that date always will. A consumer must fall back
        /// rather than assume a number, exactly as it must for <see cref="Workmanship"/>'s retype.
        /// </summary>
        public int? StackSize { get; set; }

        /// <summary>
        /// PropertyInt.Structure and PropertyInt.MaxStructure, so a consumer can tell a FULL salvage
        /// bag (Structure >= MaxStructure) from a partial one without a server round trip. ADDED
        /// 2026-09-05, in projection version 2, so a listing whose stored
        /// <see cref="SnapshotVersion"/> is below 2 carries null and a consumer must treat that as
        /// "not known to be full", never as zero. An ACTIVE one is repaired by the backfill, which
        /// selects on exactly that version; a CLOSED one carries null forever.
        /// </summary>
        public int? Structure { get; set; }
        public int? MaxStructure { get; set; }

        /// <summary>
        /// PropertyInt.SalvageToolCharges: a multi-charge salvage Hammer's fixed capacity. Its presence
        /// above 0 is the ONLY thing that distinguishes a Hammer from an ordinary salvage bag - the two
        /// share an ItemType, a MaterialType and a full Structure (see SalvageTool's own remarks).
        ///
        /// IT SELECTS ON A WANTED ORDER'S KIND, it does not merely exclude. A client counting what it
        /// holds against an order requires MaterialType to match and Structure >= MaxStructure > 0 for
        /// either kind, and then this null or 0 for a <see cref="MarketBuyOrderKind.SalvageBag"/> order
        /// and above 0 for a <see cref="MarketBuyOrderKind.SalvageHammer"/> one. Counting a Hammer
        /// toward a bag order and counting a bag toward a hammer order are the same error in opposite
        /// directions, and the server refuses both (MarketSalvageMaterials.MatchesOrder).
        ///
        /// Null when the property is absent. Same ADDED / version / backfill rule as Structure - and a
        /// snapshot below <see cref="MarketSnapshot.CurrentVersion"/> carries null because the field was
        /// never projected, NOT because the item is a bag, so such a row counts toward NEITHER kind.
        /// </summary>
        public int? SalvageToolCharges { get; set; }

        /// <summary>The item's own description text, shown under the panel lines. Null when it has none.</summary>
        public string LongDesc { get; set; }

        public uint IconId { get; set; }
        public uint? IconOverlayId { get; set; }
        public uint? IconUnderlayId { get; set; }
        public int? PaletteTemplate { get; set; }
        public List<int> SpellIds { get; set; } = new List<int>();
        public List<string> SpellNames { get; set; } = new List<string>();
        public List<string> PanelLines { get; set; } = new List<string>();

        /// <summary>
        /// One of a fixed lowercase token set (MarketWeaponClass.Classify) - light/heavy/finesse/
        /// two_handed/unarmed/sword/axe/mace/spear/dagger/staff/bow/crossbow/atlatl/thrown/
        /// ammunition/caster - or null when the item isn't a weapon or can't be classified. This
        /// token set is a CONTRACT with the web app's "Weapon Type" filter: do not add, rename, or
        /// reorder tokens without updating both sides.
        ///
        /// ADDED 2026-09-04. A snapshot is captured at listing time and refreshed by the backfill (automatically
        /// after a world start, or by /marketbackfill run), which touches ACTIVE listings only - so a listing
        /// created before that date carries null here regardless of whether the underlying item is a
        /// weapon, unless a backfill has since re-projected it; a CLOSED listing from before that
        /// date always carries null.
        /// </summary>
        public string WeaponClass { get; set; }

        /// <summary>
        /// The display label for <see cref="WeaponClass"/> (MarketWeaponClass.Label), e.g. "Light
        /// Weapons" for "light". Carried alongside the token so the web app never has to keep its own
        /// copy of the label table in sync. Null exactly when WeaponClass is null.
        /// </summary>
        public string WeaponClassName { get; set; }

        /// <summary>
        /// The fixed lowercase slot tokens the item occupies (MarketEquipSlots.Classify) - head/
        /// chest/abdomen/upper_arm/lower_arm/hands/upper_leg/lower_leg/feet/neck/wrist/finger/
        /// trinket/cloak/shield. This token set is a CONTRACT with the web app's "Slot" filter: do
        /// not add, rename, or reorder tokens without updating both sides.
        ///
        /// NULL means "not known" (e.g. a listing created before this field shipped); an EMPTY list
        /// means the item is known to occupy none of the offered slots (a pure weapon, for
        /// instance). A consumer must tell these two apart rather than treating both as "no slots".
        ///
        /// ADDED 2026-09-06. A snapshot is captured at listing time and refreshed by the backfill (automatically
        /// after a world start, or by /marketbackfill run), which touches ACTIVE listings only - so a listing
        /// created before that date carries null here regardless of what the underlying item
        /// actually occupies, unless a backfill has since re-projected it; a CLOSED listing from
        /// before that date always carries null.
        /// </summary>
        public List<string> Slots { get; set; }

        /// <summary>
        /// The canonical token for the item's PropertyInt.EquipmentSetId (MarketEquipmentSet.Classify)
        /// - the spaced EquipmentSet enum name, the same text MarketAppraisal's "Set: ..." panel line
        /// already shows. Null when the item carries no set, or an unrecognized/junk set id.
        ///
        /// ADDED 2026-09-06. A snapshot is captured at listing time and refreshed by the backfill (automatically
        /// after a world start, or by /marketbackfill run), which touches ACTIVE listings only - so a listing
        /// created before that date carries null here regardless of whether the underlying item
        /// carries a set, unless a backfill has since re-projected it; a CLOSED listing from before
        /// that date always carries null.
        /// </summary>
        public string EquipmentSet { get; set; }

        /// <summary>
        /// The display label for <see cref="EquipmentSet"/> (MarketEquipmentSet.Label) - the spaced
        /// name, except for a handful of overrides where that reads poorly (e.g. the four Olthoi
        /// armor sets, and the Shou-jen Shozoku set). Null exactly when EquipmentSet is null.
        /// </summary>
        public string EquipmentSetName { get; set; }

        /// <summary>
        /// Every custom mod on the item (MarketItemMods.Apply): weapon mods first, then equipment mods,
        /// each group in registry order. NULL when the item carries none - never an empty list - because
        /// null and absent are the same bytes on the wire and because MarketSnapshotFieldTests' mirror test
        /// compares FromItem's value against FromWeenie's, which is always null.
        ///
        /// Each group appears only while its switch is on (weapon_mods_enabled, equipment_mods_enabled),
        /// matching AppraiseInfo, so the web never shows a mod the game hides.
        ///
        /// ADDED 2026-09-14, in projection version 4. A snapshot below version 4 carries null whether or
        /// not the item is modded; an ACTIVE listing is repaired by the backfill, a CLOSED one carries null
        /// forever.
        /// </summary>
        public List<ItemMod> Mods { get; set; }

        /// <summary>
        /// PropertyInt.GearModCapacity, when it is above 0 and equipment_mods_enabled is on; otherwise
        /// null. May be set on an item with no Mods at all (it can take mods but has none yet). Same
        /// ADDED / version rule as <see cref="Mods"/>.
        /// </summary>
        public int? ModCapacity { get; set; }

        /// <summary>
        /// WeaponQualityTiers.NameFor(Evaluate(item)) - Exceptional, Elite or God - when the tier is not
        /// None and weapon_mods_enabled is on; otherwise null. Same ADDED / version rule as
        /// <see cref="Mods"/>.
        /// </summary>
        public string WeaponQualityTier { get; set; }
    }

    /// <summary>
    /// One custom mod as the web shows it: the parts of one appraisal "Property Details:" line. Built only by
    /// MarketItemMods, from the same code paths WeaponModDisplay.Describe and EquipmentModDisplay.Describe
    /// use.
    /// </summary>
    public sealed class ItemMod
    {
        /// <summary>"weapon" for a WeaponModId row, "armor" for an EquipmentModId row (MarketItemMods.KindWeapon / KindArmor).</summary>
        public string Kind { get; set; }

        /// <summary>
        /// The filter token: the enum member name split into proper-cased words (MarketItemMods.Token), e.g.
        /// "Weak Point". Unique within a kind, not across kinds. A CONTRACT with the web app's mod filters and
        /// every saved Browse URL: never rename an existing enum member.
        /// </summary>
        public string Token { get; set; }

        /// <summary>The row's DisplayName, as appraisal prints it.</summary>
        public string Name { get; set; }

        /// <summary>1-100, or null when IntensityPercent returns 0 (unreportable) - exactly when appraisal drops the bracket.</summary>
        public int? IntensityPct { get; set; }

        /// <summary>The effect text alone, without name or bracket, e.g. "+4 critical damage rating".</summary>
        public string Effect { get; set; }
    }

    /// <summary>
    /// One listing as the manager, the API and the commands see it. NOT the EF row - that is
    /// ACE.Database.Models.Shard.MarketListing, aliased ShardMarketListing wherever both are in scope.
    /// Seq is the feed cursor, in memory only, so the feeds also carry a generation token.
    /// </summary>
    public sealed class MarketListing
    {
        public uint Id { get; set; }
        public uint SellerAccountId { get; set; }
        public uint SellerCharacterGuid { get; set; }
        public string SellerCharacterName { get; set; }
        public uint? ItemGuid { get; set; }
        public uint Wcid { get; set; }
        public int Count { get; set; }
        public long PriceMmd { get; set; }
        public MarketListingStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public ListingSnapshot Snapshot { get; set; }
        public long Seq { get; set; }

        /// <summary>
        /// A CLASS listing's line display id (VaultEntry.ClassDisplayId, 32 lowercase hex), or null for
        /// a stored-item or ledger listing. A class listing ALSO carries a null
        /// <see cref="ItemGuid"/>, so "ItemGuid is null" no longer means "ledger" on its own - every
        /// such test must also ask this. <see cref="IsLedger"/> does.
        /// </summary>
        public string ClassKey { get; set; }

        /// <summary>A collapsed ledger stack: no guid AND no class key. A class listing is NOT a ledger listing.</summary>
        public bool IsLedger => ItemGuid == null && ClassKey == null;

        /// <summary>A listing of a counted class line, anchored on <see cref="ClassKey"/>.</summary>
        public bool IsClass => ClassKey != null;
    }

    /// <summary>
    /// The result of deciding which of one account's Active listings /marketadmin invalidate would
    /// close, before anything is written. See MarketManager.FindInvalidationCandidates and
    /// MarketManager.ApplyInvalidation.
    /// </summary>
    internal sealed class AccountInvalidationCandidates
    {
        public AccountInvalidationCandidates(uint accountId, bool storeNotReady, IReadOnlyList<MarketListing> listings)
        {
            AccountId = accountId;
            StoreNotReady = storeNotReady;
            Listings = listings;
        }

        public uint AccountId { get; }

        /// <summary>True when the account's vault store was not ready to be asked at all - distinct from "asked, and it holds nothing".</summary>
        public bool StoreNotReady { get; }

        /// <summary>Active listings whose backing is gone. Empty (never null) when StoreNotReady is true.</summary>
        public IReadOnlyList<MarketListing> Listings { get; }
    }

    /// <summary>One transaction as the manager, the API and the commands see it.</summary>
    public sealed class MarketTransaction
    {
        public uint Id { get; set; }
        public uint ListingId { get; set; }
        public uint? BuyOrderId { get; set; }
        public uint BuyerAccountId { get; set; }
        public uint BuyerCharacterGuid { get; set; }
        public string BuyerCharacterName { get; set; }
        public uint SellerAccountId { get; set; }
        public uint SellerCharacterGuid { get; set; }
        public string SellerCharacterName { get; set; }
        public uint Wcid { get; set; }
        public string ItemName { get; set; }
        public int Count { get; set; }
        public long PriceMmdTotal { get; set; }
        public DateTime Timestamp { get; set; }
        public MarketChannel Channel { get; set; }
        public MarketTransactionStatus Status { get; set; }
        public long Seq { get; set; }
    }

    /// <summary>One Wanted buy order as the manager, the API and the feed see it. NOT the EF row (alias that ShardMarketBuyOrder).</summary>
    public sealed class MarketBuyOrder
    {
        public uint Id { get; set; }
        public uint BuyerAccountId { get; set; }
        public uint BuyerCharacterGuid { get; set; }
        public string BuyerCharacterName { get; set; }
        public int MaterialType { get; set; }
        public string MaterialName { get; set; }

        /// <summary>
        /// Bag or Hammer. Defaults to <see cref="MarketBuyOrderKind.SalvageBag"/>, which is what every
        /// row written before order_Kind existed means.
        /// </summary>
        public MarketBuyOrderKind Kind { get; set; }

        /// <summary>The wcid of the thing actually wanted: the bag wcid for a bag order, the HAMMER wcid for a hammer order.</summary>
        public uint Wcid { get; set; }

        /// <summary>The display name of <see cref="Wcid"/>. Named for the bag because the wire field is bag_name; a hammer order carries the Hammer's name here.</summary>
        public string BagName { get; set; }
        public long PriceMmd { get; set; }
        public int CountTotal { get; set; }
        public int CountRemaining { get; set; }
        public long EscrowMmd { get; set; }
        public MarketBuyOrderStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public uint IconId { get; set; }
        public uint? IconOverlayId { get; set; }
        public uint? IconUnderlayId { get; set; }
        public long Seq { get; set; }

        /// <summary>
        /// Bags reserved by fills between their step 0 and their step 6 (WANTED-DESIGN 6.6). In-memory
        /// only: never persisted, never copied by MarketManager.Copy, never on the wire. Read and
        /// written under MarketManager's indexLock only.
        /// </summary>
        internal int InFlight { get; set; }
    }

    /// <summary>
    /// A market operation's outcome. CurrentPriceMmd is meaningful ONLY when Error is PriceChanged,
    /// where DESIGN 5.4 requires the live price to travel back with the rejection.
    /// </summary>
    public sealed class MarketResult<T>
    {
        public bool Ok { get; }
        public T Value { get; }
        public MarketError Error { get; }
        public long CurrentPriceMmd { get; }

        private MarketResult(bool ok, T value, MarketError error, long currentPriceMmd)
        {
            Ok = ok;
            Value = value;
            Error = error;
            CurrentPriceMmd = currentPriceMmd;
        }

        public static MarketResult<T> Success(T value) => new MarketResult<T>(true, value, MarketError.None, 0);

        public static MarketResult<T> Fail(MarketError error) => new MarketResult<T>(false, default, error, 0);

        public static MarketResult<T> PriceChanged(long currentPriceMmd)
            => new MarketResult<T>(false, default, MarketError.PriceChanged, currentPriceMmd);
    }
}
