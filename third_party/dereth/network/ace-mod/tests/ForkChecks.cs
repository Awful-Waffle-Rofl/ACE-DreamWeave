// Local modification (not upstream's): checks of this fork's own changes to the mod.
//
//   units/fork-veto      the room-mirroring veto, both ways, the Global room untouched
//   units/fork-linkgone  the world half forgetting a switched-off link, and greeting the next
//   units/fork-traffic   the traffic log: its lines, its days, its bound, off, a failing disk, the
//                        redactions, the HTTPS handler, and the link's tap over loopback
//   units/fork-command   the mod registers no `dn` or `dng` command (DreamWeave's own /dn is the Drift
//                        Network recall), and its help names @derethnetwork
//   units/fork-control   the live switch: its pace, its decisions, never two links to the hub,
//                        and the world tick reading the switch before its null check

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dereth.Network.Harness;

internal static class ForkChecks
{
    private sealed class Clock
    {
        public double Now = 1000;
    }

    private static Address A(string s) => Address.Parse(s) ?? throw new ArgumentException(s);

    private const uint Ann = 0x5000_0001;
    private const uint Bo = 0x5000_0002;

    private static readonly Welcome Welcomed = new(Wire.Protocol, "alpha", "hub test",
        [new WorldInfo("alpha", "ALPHA", true), new WorldInfo("bravo", "BRAVO", true)], [Wire.GlobalRoom], Limits.Default);

    public static void Run(Checks c)
    {
        Veto(c);
        LinkGone(c);
        Traffic(c);
        Control(c);
        CommandName(c);
    }

    // ---- the veto -----------------------------------------------------------------------------

    /// <summary>
    /// A world `alpha` carrying the Global room in Roleplay, whose General a hub last said it
    /// mirrors, linked and welcomed: Ann on the network and linked by her own `/derethnetwork link`, Bo listening.
    /// </summary>
    private static (TestWorld W, MemoryLink Link, Clock Clock) Ready(NetworkStore? store = null)
    {
        var clock = new Clock();
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now, mirrored: [RoomSlot.General], store: store);
        var link = (MemoryLink)w.Link;
        w.Host.Add(Ann, "Ann");
        w.Host.Add(Bo, "Bo");
        link.Up(1);
        w.Pump();
        link.Frame(1, Welcomed);
        w.Pump();
        w.World.Dn(Ann, ["on"]);
        w.World.Dn(Ann, ["link"]);
        clock.Now += 1;
        w.Pump();
        return (w, link, clock);
    }

    private static List<RoomSay> Said(MemoryLink link, int from) =>
        link.Sent.Skip(from).Select(s => Wire.Read(s.Frame).Message).OfType<RoomSay>().ToList();

    /// <summary>The hub asks the world to say `text` as Ann in `room`: the answer it gets.</summary>
    private static SayResult? HubSays(TestWorld w, MemoryLink link, Clock clock, string id, string room)
    {
        var sent = link.Sent.Count;
        link.FrameText(1, SayWire.Write(new SayAsk(id, room, A("Ann@alpha"), "from discord " + id, "discord")));
        clock.Now += 0.01;
        w.Pump();
        return link.Sent.Skip(sent).Select(s => SayWire.Read(s.Frame)).Where(r => r.Status == ReadStatus.Ok)
            .Select(r => r.Message).OfType<SayResult>().SingleOrDefault(r => r.Id == id);
    }

    private static void Veto(Checks c)
    {
        const string s = "units/fork-veto";
        var (w, link, clock) = Ready();
        c.Check(s, !w.World.Config.RoomMirroringVetoed, "a world starts without the veto");

        // Unvetoed, as upstream: the world's own room goes to the hub.
        var from = link.Sent.Count;
        w.Host.SayInRoom(Ann, RoomSlot.General, "before the veto");
        w.Pump();
        c.Check(s, Said(link, from) is [{ Room: "general" }], "unvetoed, a line in General goes to the hub");

        w.World.Config.RoomMirroringVetoed = true;
        clock.Now += 60;
        from = link.Sent.Count;
        var shown = w.Host.Shown.Count;
        w.Host.SayInRoom(Ann, RoomSlot.General, "vetoed line");
        w.Pump();
        c.Check(s, Said(link, from).Count == 0, "vetoed, a line in General sends no room.say");
        c.Check(s, w.Host.Shown.Skip(shown).Count(x => x.Kind == "room") == 2 && !w.Host.Shown.Skip(shown).Any(x => x.Kind == "system"),
            "and is still heard here by every listener, its speaker told nothing");
        w.Host.SayInRoom(Ann, RoomSlot.Roleplay, "global line");
        w.Pump();
        c.Check(s, Said(link, from) is [{ Room: "global", Text: "global line" }], "vetoed, a Global room line is still sent");
        clock.Now += 60;
        var general = HubSays(w, link, clock, "v1", "general");
        c.Check(s, general is { Outcome: SayOutcome.Refused, Reason: SayReasons.RoomOff },
            "vetoed, the hub's say into General is refused room_off: " + general);
        var global = HubSays(w, link, clock, "v2", "global");
        c.Check(s, global is { Outcome: SayOutcome.Said }, "vetoed, the hub's say into the Global room is still said: " + global);
        c.Check(s, w.World.MirroredSlots is [RoomSlot.General], "the veto leaves the mirrored rooms the hub said as they were");

        w.World.Config.RoomMirroringVetoed = false;
        clock.Now += 60;
        from = link.Sent.Count;
        w.Host.SayInRoom(Ann, RoomSlot.General, "after the veto");
        w.Pump();
        c.Check(s, Said(link, from) is [{ Room: "general", Text: "after the veto" }], "lifted, a line in General goes to the hub again");
        clock.Now += 60;
        var again = HubSays(w, link, clock, "v3", "general");
        c.Check(s, again is { Outcome: SayOutcome.Said }, "lifted, the hub's say into General is said again: " + again);
        c.Check(s, !link.SentMessages().OfType<RoomJoin>().Skip(2).Any(), "the veto sends the hub nothing of its own");
    }

    // ---- the link gone ------------------------------------------------------------------------

    private static void LinkGone(Checks c)
    {
        const string s = "units/fork-linkgone";
        var (w, link, clock) = Ready();
        c.Check(s, w.World.AddFriend(Ann, "Bob@bravo"), "a remote friend is added");
        link.Frame(1, new Presence(A("Ann@alpha"), A("Bob@bravo"), true));
        w.Pump();
        c.Check(s, Bob(w) == true, "and shown online");
        c.Check(s, w.World.Link == LinkState.Up, "the link is up before");

        var shown = w.Host.Shown.Count;
        w.World.LinkGone();
        c.Check(s, w.World.Link == LinkState.Down, "LinkGone takes the link down");
        c.Check(s, Bob(w) == false
            && w.Host.Shown.Skip(shown).Any(x => x is { Kind: "friend", Online: false }),
            "remote friends show offline the usual way");
        w.World.LinkGone();
        c.Check(s, w.World.Link == LinkState.Down, "a second LinkGone changes nothing");

        // What the old link still delivers is not this world's any more.
        link.Frame(1, new Tell("old1", A("Bob@bravo"), A("Ann@alpha"), "from the old link", true));
        w.Pump();
        c.Check(s, !w.Host.To(Ann).Any(x => x.Text == "from the old link"), "a frame of the old epoch is ignored");

        // A new link, as the switch makes one: its own epoch, greeted again.
        var next = new MemoryLink();
        w.Link = next;
        next.Up(7);
        clock.Now += 1;
        w.Pump();
        c.Check(s, next.Sent.Count > 0 && next.Sent.All(x => x.Epoch == 7) && next.SentMessages().FirstOrDefault() is Hello,
            "the new link's epoch gets hello first: " + string.Join(" | ", next.Sent.Select(x => x.Frame)));
        next.Frame(7, Welcomed);
        w.Pump();
        c.Check(s, w.World.Link == LinkState.Up, "and is welcomed");
        next.Frame(7, new Presence(A("Ann@alpha"), A("Bob@bravo"), true));
        w.Pump();
        c.Check(s, Bob(w) == true, "the remote friend shows online again");

        LoggedOutWhileOff(c, s);
        LoggedInWhileOff(c, s);
        OffNetworkLoggedInWhileOff(c, s);
        ListedLeftWhileOff(c, s);
        FaultyCharacterAtTheResume(c, s);
    }

    /// <summary>A new link comes up for `w` and is welcomed: the frames it sent.</summary>
    private static MemoryLink Relinked(TestWorld w, Clock clock, long epoch)
    {
        var next = new MemoryLink();
        w.Link = next;
        next.Up(epoch);
        clock.Now += 0.5;
        w.Pump();
        next.Frame(epoch, Welcomed);
        clock.Now += 0.5;
        w.Pump();
        return next;
    }

    private static PersonFriends View(string to, params (string Handle, string[] Characters)[] persons) =>
        new(A(to), true, persons.Select(p => new PersonFriend(A(p.Handle), p.Characters.Select(A).ToList())).ToList());

    /// <summary>A linked character, listed by a person friend, logs out while the network is off.</summary>
    private static void LoggedOutWhileOff(Checks c, string s)
    {
        var store = new NetworkStore();
        var (w, link, clock) = Ready(store);
        // An earlier logout kept her tell squelches: none, so every local character reached her away.
        w.Host.Characters[Ann].Online = false;
        w.World.OnLogout(Ann, "Ann", null, new LocalSquelches(new HashSet<uint>(), new HashSet<string>(StringComparer.Ordinal), false));
        w.Host.Characters[Ann].Online = true;
        w.World.OnLogin(Ann);
        clock.Now += 0.5;
        w.Pump();
        c.Check(s, store.Characters[Ann].AwaySquelches is { Everyone: false, Characters.Count: 0 },
            "before: her squelches from an earlier logout are kept");
        // Ready asked for her link code; the hub answers it.
        var code = link.SentMessages().OfType<PersonRequest>().Last(p => p.Op == PersonOp.Code);
        link.Frame(1, new PersonResult(code.Id, PersonOutcome.Ok, "K7QD-2MXP", 600, "https://example.org/link/#K7QD-2MXP", []));
        clock.Now += 0.5;
        w.Pump();
        link.Frame(1, View("Ann@alpha", ("Bob@bravo", ["Bob@bravo", "Alt@bravo"])));
        clock.Now += 0.5;
        w.Pump();
        c.Check(s, w.World.ListedByPersons(Ann).Count == 2 && w.World.PersonStateOf(Ann) is { Linked: true },
            "before: Ann is linked and lists her person friend's characters");

        var atOff = w.World.OnlineNow();
        w.World.LinkGone();
        c.Check(s, atOff.ContainsKey(Ann) && atOff[Ann].Name == "Ann", "the switch takes who is online as it goes off");
        // Off: ACE logs her out, and the world half hears nothing.
        w.Host.Characters[Ann].Online = false;
        c.Check(s, w.World.ListedByPersons(Ann).Count == 2, "control: without the reconcile her person state stays as at the switch off");
        var shown = w.Host.Shown.Count;
        w.World.ResumeAfterOff(atOff);
        c.Check(s, w.World.ListedByPersons(Ann).Count == 0 && w.World.PersonStateOf(Ann) is null,
            "after the resume she is logged out as at a logout: no person state, nothing listed by persons");
        c.Check(s, w.Host.Shown.Skip(shown).All(x => x.To != Ann), "and nothing is shown to a character who is not there");
        c.Check(s, w.World.Link == LinkState.Down, "the link stays down until a new one comes");
        c.Check(s, store.Characters[Ann].AwaySquelches is null,
            "her squelches, not taken as she left while off, are cleared rather than kept from the earlier logout");

        // The next link: nothing watches or tells for her, and a local teller does not reach her away.
        var next = Relinked(w, clock, 11);
        var about = next.SentMessages().Where(m => m is Presence or Watch or PersonWatch)
            .Where(m => m switch
            {
                Presence p => p.Of.Name == "Ann" || p.To.Name == "Ann",
                Watch x => x.Owner.Name == "Ann" || x.Target.Name == "Ann",
                PersonWatch p => p.Who.Name == "Ann",
                _ => false,
            }).ToList();
        c.Check(s, next.Sent.Count > 0 && about.Count == 0,
            $"the next welcome sends no presence, watch or person watch for the departed character ({about.Count}: {string.Join(" | ", about)})");
        var mark = next.Sent.Count;
        next.FrameText(11, ReachWire.Write(new ReachAsk("q1", A("Bo@alpha"), A("Ann@alpha"))));
        clock.Now += 0.5;
        w.Pump();
        var answer = next.Sent.Skip(mark).Select(x => ReachWire.Read(x.Frame)).Where(r => r.Status == ReadStatus.Ok).Select(r => r.Message).ToList();
        c.Check(s, answer is [ReachResult { Id: "q1", Reachable: false }],
            "a local teller does not reach her away: " + string.Join(" | ", answer));
    }

    /// <summary>A character logs in while the network is off.</summary>
    private static void LoggedInWhileOff(Checks c, string s)
    {
        var clock = new Clock();
        var store = new NetworkStore();
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now, store: store);
        var link = (MemoryLink)w.Link;
        w.Host.Add(Ann, "Ann");
        var cy = w.Host.Add(0x5000_0003, "Cy");
        link.Up(1);
        w.Pump();
        link.Frame(1, Welcomed);
        w.Pump();
        w.World.Dn(cy.Id, ["on"]);
        c.Check(s, w.World.AddFriend(cy.Id, "Bob@bravo"), "Cy lists a remote friend");
        cy.Online = false;
        w.World.OnLogout(cy.Id, "Cy");
        // While Cy was away the hub's operator took her out of the directory.
        store.Characters[cy.Id].TakenOut = TakenOut.Operator;

        var atOff = w.World.OnlineNow();
        w.World.LinkGone();
        cy.Online = true;
        var shown = w.Host.Shown.Count;
        c.Check(s, !atOff.ContainsKey(cy.Id) && atOff.ContainsKey(Ann), "she was not online at the switch off");
        w.World.ResumeAfterOff(atOff);
        var toCy = w.Host.Shown.Skip(shown).Where(x => x.To == cy.Id).ToList();
        c.Check(s, toCy.Any(x => x is { Kind: "system", Text: NetworkWorld.TakenDownLine }) && store.Characters[cy.Id].TakenOut is null,
            "after the resume she is told at once what her login would have told her: " + string.Join(" | ", toCy.Select(x => x.Kind + ":" + x.Text)));
        c.Check(s, toCy.Any(x => x is { Kind: "friend", Row: FriendRowKind.Added, Name: "Bob@bravo", Online: false }),
            "and her friends panel, sent while the network was off, gains her remote friend's row");
        c.Check(s, !w.Host.Shown.Skip(shown).Any(x => x.To == Ann), "a character online throughout is left alone");
        var next = new MemoryLink();
        w.Link = next;
        next.Up(9);
        w.Pump();
        next.Frame(9, Welcomed);
        w.Pump();
        c.Check(s, next.SentMessages().OfType<Watch>().Any(x => x.On && x.Target.ToString() == "Bob@bravo"),
            "the next welcome watches her remote friend again");
    }

    /// <summary>A character off the network, who still lists a remote friend, logs in while the network is off.</summary>
    private static void OffNetworkLoggedInWhileOff(Checks c, string s)
    {
        var clock = new Clock();
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now);
        var link = (MemoryLink)w.Link;
        var dee = w.Host.Add(0x5000_0004, "Dee");
        link.Up(1);
        w.Pump();
        link.Frame(1, Welcomed);
        w.Pump();
        w.World.Dn(dee.Id, ["on"]);
        c.Check(s, w.World.AddFriend(dee.Id, "Bob@bravo"), "Dee lists a remote friend");
        w.World.Dn(dee.Id, ["off"]);
        dee.Online = false;
        w.World.OnLogout(dee.Id, "Dee");
        clock.Now += 1;
        w.Pump();

        var atOff = w.World.OnlineNow();
        w.World.LinkGone();
        dee.Online = true;
        var shown = w.Host.To(dee.Id).Count;
        var sent = link.Sent.Count;
        w.World.ResumeAfterOff(atOff);
        clock.Now += 1;
        w.Pump();
        c.Check(s, w.Host.To(dee.Id).Count == shown,
            "a character off the network is shown nothing at the resume, not even her remote friend's row: "
            + string.Join(" | ", w.Host.To(dee.Id).Skip(shown).Select(x => x.Kind + ":" + x.Name + x.Text)));
        c.Check(s, link.Sent.Count == sent, "and nothing goes to the hub for her");
    }

    /// <summary>A listed character whose profile changed since its listing logs out while the network is off.</summary>
    private static void ListedLeftWhileOff(Checks c, string s)
    {
        var clock = new Clock();
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now);
        var link = (MemoryLink)w.Link;
        var gus = w.Host.Add(0x5000_0005, "Gus");
        link.Up(1);
        w.Pump();
        link.Frame(1, Welcomed);
        w.Pump();
        w.World.Dn(gus.Id, ["on"]);
        clock.Now += 1;
        w.Pump();
        var listing = link.SentListings().OfType<Listing>().LastOrDefault();
        if (listing is not null)
            link.FrameText(1, DirectoryWire.Write(new ListingResult(listing.Id, ListingOutcome.Ok, null, "https://example.org/directory/character/")));
        clock.Now += 1;
        w.Pump();
        c.Check(s, listing is not null && w.World.ListingOf(gus.Id) is { Owed: false }, "before: Gus is listed and owes nothing");

        gus.Level = 77;
        var atOff = w.World.OnlineNow();
        w.World.LinkGone();
        gus.Online = false;
        c.Check(s, atOff.TryGetValue(gus.Id, out var at) && at.Facts?.Own.Level == 77 && w.World.ListingOf(gus.Id) is { Owed: false },
            "the switch off takes his facts as they are then; control: nothing is owed yet");
        w.World.ResumeAfterOff(atOff);
        c.Check(s, w.World.ListingOf(gus.Id) is { Owed: true },
            "after the resume his changed profile is owed to the directory, as at a logout");
    }

    /// <summary>A character the host cannot read at the switch off or the resume does not hold the network back.</summary>
    private static void FaultyCharacterAtTheResume(Checks c, string s)
    {
        var clock = new Clock();
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now);
        var link = (MemoryLink)w.Link;
        var hal = w.Host.Add(0x5000_0006, "Hal");
        var ivy = w.Host.Add(0x5000_0007, "Ivy");
        link.Up(1);
        w.Pump();
        link.Frame(1, Welcomed);
        w.Pump();
        foreach (var who in new[] { hal, ivy })
        {
            w.World.Dn(who.Id, ["on"]);
            w.World.AddFriend(who.Id, "Bob@bravo");
            who.Online = false;
            w.World.OnLogout(who.Id, who.Name);
        }
        var jo = w.Host.Add(0x5000_0008, "Jo");
        w.World.Dn(jo.Id, ["on"]);
        clock.Now += 1;
        w.Pump();
        w.Host.Faulty.Add(jo.Id);
        IReadOnlyDictionary<uint, OnlineAtOff>? atOff = null;
        var threw = false;
        try
        {
            atOff = w.World.OnlineNow();
        }
        catch (Exception)
        {
            threw = true;
        }
        c.Check(s, !threw && atOff is not null && atOff.ContainsKey(jo.Id) && atOff[jo.Id].Facts is null,
            "a character whose facts throw at the switch off is still taken, without them");
        w.World.LinkGone();
        jo.Online = false;
        hal.Online = true;
        ivy.Online = true;
        w.Host.Faulty.Add(hal.Id);
        var shown = w.Host.To(ivy.Id).Count;
        threw = false;
        try
        {
            w.World.ResumeAfterOff(atOff ?? new Dictionary<uint, OnlineAtOff>());
        }
        catch (Exception)
        {
            threw = true;
        }
        c.Check(s, !threw, "a character that throws at the resume does not stop it");
        c.Check(s, w.Host.To(ivy.Id).Skip(shown).Any(x => x is { Kind: "friend", Row: FriendRowKind.Added, Name: "Bob@bravo" }),
            "the others are still reconciled");
        c.Check(s, w.Logs.Any(l => l.Contains($"the login of character {hal.Id} while the network was off could not be reconciled", StringComparison.Ordinal)),
            "and the one that could not be is logged");
    }

    /// <summary>Whether Ann's remote friend Bob@bravo shows online; null when she does not list him.</summary>
    private static bool? Bob(TestWorld w)
    {
        var rows = w.World.FriendRows(Ann).Where(r => r.Name == "Bob@bravo").ToList();
        return rows.Count == 1 ? rows[0].Online : null;
    }

    // ---- the traffic log ----------------------------------------------------------------------

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dn-fork-" + Guid.NewGuid().ToString("N"));
        return d;
    }

    private static void Delete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
            else if (File.Exists(dir))
                File.Delete(dir);
        }
        catch (Exception)
        {
            // A temporary folder left behind.
        }
    }

    /// <summary>A day file's bytes, read as an operator's tool would while the log may hold it open.</summary>
    private static byte[] Bytes(string path)
    {
        if (!File.Exists(path))
            return [];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var m = new MemoryStream();
        f.CopyTo(m);
        return m.ToArray();
    }

    private static string Text(string path) => Encoding.ASCII.GetString(Bytes(path));

    private static List<JsonElement> Lines(string path) =>
        Text(path).Split('\n').Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;

    private const string Key = "drth_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcde";

    private static void Traffic(Checks c)
    {
        const string s = "units/fork-traffic";
        Shape(c, s);
        Days(c, s);
        Bound(c, s);
        Off(c, s);
        Failing(c, s);
        Redactions(c, s);
        Http(c, s);
        Tap(c, s);
    }

    private static void Shape(Checks c, string s)
    {
        var dir = TempDir();
        var at = new DateTimeOffset(2026, 11, 14, 12, 0, 0, 250, TimeSpan.Zero);
        var log = new TrafficLog(dir, Key, (_, _) => { }, () => at) { Enabled = true };
        var e = ((char)0xE9).ToString();
        log.Frame(true, 3, "{\"type\":\"tell\",\"text\":\"caf" + e + "\"}");
        log.Frame(false, 3, "not json");
        log.Meta("link.down", 3, "closed by the hub");
        log.Https("https.answer", "https://hub.example/v1/servers/alpha/verify/answer", 200, "{\"nonce\":\"ab\"}", "{}");
        c.Check(s, log.Flush(TimeSpan.FromSeconds(5)), "a flush returns once everything is written");
        var path = log.PathFor(at);
        c.Check(s, Path.GetFileName(path) == "traffic-2026-11-14.jsonl" && File.Exists(path), "one file for the UTC day: " + path);
        var bytes = Bytes(path);
        c.Check(s, bytes.Length > 0 && bytes.All(b => b is (byte)'\n' or >= 0x20 and < 0x7F) && bytes[0] == (byte)'{',
            "the file is printable ASCII lines, no BOM");
        var lines = Lines(path);
        c.Check(s, lines.Count == 4, $"one line for each record ({lines.Count})");
        if (lines.Count == 4)
        {
            var first = Text(path).Split('\n')[0];
            c.Check(s, first.StartsWith("{\"utc\":\"2026-11-14T12:00:00.250Z\",\"dir\":\"in\",\"type\":\"tell\",\"epoch\":3,\"frame\":", StringComparison.Ordinal),
                "a frame's line: utc, dir, type read from the frame, epoch, then the frame: " + first);
            c.Check(s, first.Contains("caf\\" + "u00e9", StringComparison.Ordinal) && Str(lines[0], "frame") == "{\"type\":\"tell\",\"text\":\"caf" + e + "\"}",
                "a character past ASCII is escaped, and reads back as it was carried");
            c.Check(s, Str(lines[1], "dir") == "out" && Str(lines[1], "type") == "(unparsed)" && Str(lines[1], "frame") == "not json",
                "a frame that is not JSON is (unparsed), kept verbatim");
            c.Check(s, Str(lines[2], "dir") == "meta" && Str(lines[2], "type") == "link.down" && Num(lines[2], "epoch") == 3
                && Str(lines[2], "detail") == "closed by the hub", "a meta record carries its detail");
            c.Check(s, Str(lines[3], "type") == "https.answer" && Num(lines[3], "status") == 200 && Str(lines[3], "url").EndsWith("/answer", StringComparison.Ordinal)
                && Str(lines[3], "request") == "{\"nonce\":\"ab\"}" && Str(lines[3], "body") == "{}",
                "an HTTPS record carries url, status, request and body");
        }
        log.Stop(TimeSpan.FromSeconds(5));
        // Appended, never replaced.
        var again = new TrafficLog(dir, Key, (_, _) => { }, () => at) { Enabled = true };
        again.Meta("link.up", 4);
        again.Stop(TimeSpan.FromSeconds(5));
        c.Check(s, Lines(path).Count == 5, "a second log appends to the day's file");
        Delete(dir);
    }

    private static void Days(Checks c, string s)
    {
        var dir = TempDir();
        var at = new DateTimeOffset(2026, 11, 14, 23, 59, 59, 900, TimeSpan.Zero);
        var log = new TrafficLog(dir, null, (_, _) => { }, () => at) { Enabled = true };
        log.Meta("before", 1);
        var before = at;
        at = at.AddMilliseconds(200);
        log.Meta("after", 1);
        log.Stop(TimeSpan.FromSeconds(5));
        var a = Lines(log.PathFor(before));
        var b = Lines(log.PathFor(at));
        c.Check(s, a is [var x] && Str(x, "type") == "before" && b is [var y] && Str(y, "type") == "after"
            && Path.GetFileName(log.PathFor(at)) == "traffic-2026-11-15.jsonl",
            "a record goes to the file of its own UTC day, so the file rolls at midnight UTC");
        Delete(dir);
    }

    private static void Bound(Checks c, string s)
    {
        // The record bound.
        var dir = TempDir();
        var hold = new TaskCompletionSource();
        var log = new TrafficLog(dir, null, (_, _) => { }, holdWriter: hold.Task) { Enabled = true };
        for (var i = 0; i < TrafficLog.MaxQueuedRecords + 7; i++)
            log.Meta("m", i);
        c.Check(s, log.Dropped == 7, $"past {TrafficLog.MaxQueuedRecords} waiting records, the rest are dropped and counted ({log.Dropped})");
        hold.SetResult();
        log.Flush(TimeSpan.FromSeconds(30));
        log.Meta("next", 0);
        log.Stop(TimeSpan.FromSeconds(30));
        var lines = Lines(log.PathFor(DateTimeOffset.UtcNow));
        var marks = lines.Where(l => Str(l, "type") == "dropped").ToList();
        c.Check(s, lines.Count(l => Str(l, "type") == "m") == TrafficLog.MaxQueuedRecords && marks.Count == 1 && Num(marks[0], "count") == 7
            && Str(marks[0], "dir") == "meta" && log.Dropped == 0,
            $"the next write says how many went: {{\"dir\":\"meta\",\"type\":\"dropped\",\"count\":7}} ({marks.Count} marks)");
        Delete(dir);

        // The text bound.
        dir = TempDir();
        hold = new TaskCompletionSource();
        log = new TrafficLog(dir, null, (_, _) => { }, holdWriter: hold.Task) { Enabled = true };
        var big = new string('x', 2 * 1024 * 1024);
        for (var i = 0; i < 5; i++)
            log.Frame(true, 1, big);
        c.Check(s, log.Dropped == 1, $"past 8 MiB of waiting text, a record is dropped ({log.Dropped})");
        hold.SetResult();
        log.Stop(TimeSpan.FromSeconds(30));
        Delete(dir);
    }

    private static void Off(Checks c, string s)
    {
        var dir = TempDir();
        var log = new TrafficLog(dir, null, (_, _) => { });
        log.Frame(true, 1, "{\"type\":\"tell\"}");
        log.Meta("link.up", 1);
        log.Https("https.verify", "https://hub.example/verify", 200, "{}", "{}");
        log.Flush(TimeSpan.FromSeconds(5));
        c.Check(s, !Directory.Exists(dir), "a log never switched on writes no file");
        log.Enabled = true;
        log.Meta("on", 1);
        log.Enabled = false;
        log.Meta("while off", 1);
        log.Flush(TimeSpan.FromSeconds(5));
        var lines = Lines(log.PathFor(DateTimeOffset.UtcNow));
        c.Check(s, lines is [var only] && Str(only, "type") == "on", "switched off, it takes nothing more");
        // Off, the file is closed: it can be deleted and nothing reopens it.
        var deleted = false;
        try
        {
            File.Delete(log.PathFor(DateTimeOffset.UtcNow));
            deleted = !File.Exists(log.PathFor(DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
        }
        c.Check(s, deleted, "switched off, the file is closed");
        log.Stop(TimeSpan.FromSeconds(5));
        Delete(dir);
    }

    private static void Failing(Checks c, string s)
    {
        // The log's folder is a file: every write fails.
        var dir = TempDir();
        File.WriteAllText(dir, "in the way");
        var warnings = new List<string>();
        var log = new TrafficLog(dir, Key, (l, m) => { lock (warnings) warnings.Add($"{l}: {m}"); }, retryWait: TimeSpan.Zero) { Enabled = true };
        var threw = false;
        try
        {
            log.Meta("a", 1);
            log.Flush(TimeSpan.FromSeconds(5));
            log.Meta("b", 1);
            log.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            threw = true;
        }
        c.Check(s, !threw && log.Dropped == 2, $"a write that fails throws nothing; its records are dropped and counted ({log.Dropped})");
        lock (warnings)
            c.Check(s, warnings.Count == 1 && warnings[0].StartsWith("Warn: the traffic log cannot be written", StringComparison.Ordinal),
                "and it warns once, not for every batch: " + string.Join(" | ", warnings));
        // The disk is back: the next write goes, with the count of what went.
        File.Delete(dir);
        log.Meta("c", 1);
        log.Stop(TimeSpan.FromSeconds(5));
        var lines = Lines(log.PathFor(DateTimeOffset.UtcNow));
        c.Check(s, lines.Count == 2 && Str(lines[0], "type") == "dropped" && Num(lines[0], "count") == 2 && Str(lines[1], "type") == "c",
            "once the folder can be written, the next write says how many went, then goes on");
        Delete(dir);
    }

    private static void Redactions(Checks c, string s)
    {
        var dir = TempDir();
        var log = new TrafficLog(dir, Key, (_, _) => { }) { Enabled = true };
        log.Frame(false, 1, "{\"type\":\"tell\",\"text\":\"my key is " + Key + "\"}");
        log.Https("https.verify", "https://hub.example/v1/servers/alpha/verify", 200,
            "{\"token\":\"dnv_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345\",\"by\":\"Ann\"}", "{\"approval\":\"approved\"}");
        log.Frame(true, 1, "{\"type\":\"person.result\",\"id\":\"p1\",\"outcome\":\"ok\",\"code\":\"7KQ2-M9XA\",\"url\":\"https://hub.example/link/7KQ2-M9XA\"}");
        log.Frame(true, 1, "{\"type\":\"tell\",\"text\":\"7KQ2-M9XA is just words here\"}");
        log.Meta("link.down", 1, "the hub refused " + Key);
        log.Frame(true, 1, "{\"type\":\"" + Key + "\"}");
        log.Stop(TimeSpan.FromSeconds(5));
        var path = log.PathFor(DateTimeOffset.UtcNow);
        var text = Text(path);
        var lines = Lines(path);
        c.Check(s, lines.Count == 6, $"every record written ({lines.Count})");
        c.Check(s, lines.Count == 6 && Str(lines[5], "type") == "(key)", "the key is (key) in a frame's type too");
        c.Check(s, !text.Contains(Key, StringComparison.Ordinal) && lines.Count == 6 && Str(lines[0], "frame").Contains("my key is (key)", StringComparison.Ordinal)
            && Str(lines[4], "detail") == "the hub refused (key)", "the registry key is (key) wherever it appears");
        c.Check(s, !text.Contains("dnv_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", StringComparison.Ordinal) && lines.Count == 6
            && Str(lines[1], "request") == "{\"token\":\"(token)\",\"by\":\"Ann\"}", "a /derethnetwork verify request's token is (token)");
        c.Check(s, lines.Count == 6 && Str(lines[2], "frame").Contains("\"code\":\"(code)\"", StringComparison.Ordinal)
            && Str(lines[2], "frame").Contains("/link/(code)", StringComparison.Ordinal) && !Str(lines[2], "frame").Contains("7KQ2", StringComparison.Ordinal),
            "a person.result frame's link codes are (code), in its url too");
        c.Check(s, lines.Count == 6 && Str(lines[3], "frame").Contains("7KQ2-M9XA", StringComparison.Ordinal),
            "a code-shaped word in any other frame is kept verbatim");
        c.Check(s, TrafficLog.RedactCodes("AB12-CD34 0000-000I x7KQ2-M9XA 7KQ2-M9XAB") == "(code) 0000-000I x7KQ2-M9XA 7KQ2-M9XAB",
            "only whole codes of the code alphabet are taken");
        Delete(dir);
    }

    private sealed class FixedAnswer(int length) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes(new string('a', length))),
            };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(r);
        }
    }

    private static void Http(Checks c, string s)
    {
        var dir = TempDir();
        var log = new TrafficLog(dir, Key, (_, _) => { }) { Enabled = true };
        var inner = new FixedAnswer(100 * 1024);
        using var http = new HttpClient(new TrafficHttpHandler(inner, () => log));
        var route = new Uri("https://hub.example/v1/servers/alpha/verify");
        var line = HubVerify.SendAsync(http, route, Key, new VerifyRequest("dnv_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", "Ann", null), CancellationToken.None)
            .GetAwaiter().GetResult();
        c.Check(s, inner.Calls == 1 && line == "The Dereth Network did not verify this world: the hub answered 403.",
            "/derethnetwork verify through the handler reads the answer as it would without it: " + line);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route, "verify/answer"));
        using var response = http.SendAsync(request).GetAwaiter().GetResult();
        var kept = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        c.Check(s, kept.Length == TrafficHttpHandler.MaxBody && response.Content.Headers.ContentType?.MediaType == "application/json",
            $"the answer handed on is its first 64 KiB, its headers kept ({kept.Length} bytes)");
        log.Stop(TimeSpan.FromSeconds(5));
        var lines = Lines(log.PathFor(DateTimeOffset.UtcNow));
        c.Check(s, lines.Count == 2 && Str(lines[0], "type") == "https.verify" && Num(lines[0], "status") == 403
            && Str(lines[0], "request").Contains("(token)", StringComparison.Ordinal) && Str(lines[0], "body").Length == TrafficHttpHandler.MaxBody
            && Str(lines[1], "type") == "https.answer",
            "each call is recorded: its type, status, request (token redacted) and body");

        // Off: passed on untouched.
        var offLog = new TrafficLog(dir + "-off", Key, (_, _) => { });
        var inner2 = new FixedAnswer(100 * 1024);
        using var http2 = new HttpClient(new TrafficHttpHandler(inner2, () => offLog));
        using var r2 = http2.SendAsync(new HttpRequestMessage(HttpMethod.Get, route)).GetAwaiter().GetResult();
        c.Check(s, r2.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length == 100 * 1024 && !Directory.Exists(dir + "-off"),
            "with the log off, the answer is handed on whole and nothing is written");
        offLog.Stop(TimeSpan.FromSeconds(5));
        Delete(dir);
    }

    private static void Tap(Checks c, string s)
    {
        var dir = TempDir();
        var log = new TrafficLog(dir, Key, (_, _) => { }) { Enabled = true };
        using var hub = new LoopbackHub(["{\"type\":\"welcome\",\"n\":1}", "{\"type\":\"rooms\",\"n\":2}"]);
        var link = new HubLink(hub.Uri, Key, (_, _) => { }, new LinkTiming { Backoff = [TimeSpan.FromSeconds(30)] }, tap: log);
        link.Start();
        var epoch = WaitUp(link);
        c.Check(s, epoch > 0, "the tapped link comes up");
        link.Send(epoch, "{\"type\":\"hello\",\"proto\":1}");
        link.Send(epoch + 1000, "{\"type\":\"tell\",\"stale\":true}");
        c.Check(s, hub.WaitReceived(1, TimeSpan.FromSeconds(10)), "the hub receives the world's frame");
        var running = link.Running;
        link.Stop();
        HubLink.WaitStopped(running, TimeSpan.FromSeconds(10));
        log.Stop(TimeSpan.FromSeconds(5));
        var lines = Lines(log.PathFor(DateTimeOffset.UtcNow));
        var types = lines.Select(l => Str(l, "dir") + ":" + Str(l, "type")).ToList();
        c.Check(s, types.FirstOrDefault() == "meta:link.up" && types.Contains("in:welcome") && types.Contains("in:rooms")
            && types.Contains("out:hello") && types.LastOrDefault() == "meta:link.down",
            "the link's tap writes up, every frame in, every frame sent, then down: " + string.Join(", ", types));
        c.Check(s, lines.Where(l => Str(l, "dir") is "in" or "out").All(l => Num(l, "epoch") == epoch),
            "each frame carries its connection's epoch");
        c.Check(s, lines.Any(l => Str(l, "type") == "frame.dropped" && Str(l, "detail") == "for a link that has gone"),
            "a frame for another connection is recorded as dropped, not sent");
        c.Check(s, !types.Contains("out:tell"), "and never as sent");
        Delete(dir);
    }

    private static long WaitUp(HubLink link)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            while (link.TryTake(out var e))
            {
                if (e.Kind == LinkEventKind.Up)
                    return e.Epoch;
            }
            Thread.Sleep(20);
        }
        return 0;
    }

    // ---- the live switch ----------------------------------------------------------------------

    private static void Control(Checks c)
    {
        const string s = "units/fork-control";
        var clock = new Clock();
        var sw = new LiveSwitch(() => clock.Now);
        c.Check(s, sw.Due() && !sw.Due(), "the first poll reads at once, the next not within the second");
        clock.Now += 0.5;
        c.Check(s, !sw.Due(), "nor half a second on");
        clock.Now += 0.5;
        c.Check(s, sw.Due(), "a second on, it reads again");

        c.Check(s, sw.Decide(false, true, false) == LiveSwitch.Step.Off, "off while on: off");
        c.Check(s, sw.Decide(true, true, false) == LiveSwitch.Step.None && sw.Decide(false, false, true) == LiveSwitch.Step.None,
            "on while on, or off while off: nothing");
        c.Check(s, sw.Decide(true, false, true) == LiveSwitch.Step.On, "on while parked, no old link: on");
        c.Check(s, sw.Decide(true, false, false) == LiveSwitch.Step.None, "on with nothing parked (the network never started): nothing");

        // A link that never started (switched off at boot): nothing to wait for.
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now);
        var never = new HubLink(new Uri("ws://127.0.0.1:9/v1/servers/alpha/link"), Key, (_, _) => { });
        sw.TakeOff(w.World, never);
        c.Check(s, never.Running is null && sw.MayResume && sw.Decide(true, false, true) == LiveSwitch.Step.On,
            "off before the first link started: on again at once");
        sw.Resumed();

        // A link held by a hub: on waits for the old link's task, so the hub never sees two.
        using (var hub = new LoopbackHub([]))
        {
            var first = new HubLink(hub.Uri, Key, (_, _) => { }, new LinkTiming { Backoff = [TimeSpan.FromSeconds(30)] });
            first.Start();
            var e1 = WaitUp(first);
            c.Check(s, e1 > 0 && hub.WaitOpen(1, TimeSpan.FromSeconds(10)), "the first link is up");
            var task = first.Running;
            sw.TakeOff(w.World, first);
            c.Check(s, first.Running is null && task is not null, "stopping forgets the link's task, so the switch takes it first");
            var waited = !sw.MayResume && sw.Decide(true, false, true) == LiveSwitch.Step.None;
            c.Check(s, waited, "just after off, on waits: the old link's task is still running");
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!sw.MayResume && DateTime.UtcNow < until)
                Thread.Sleep(20);
            c.Check(s, sw.MayResume && task!.IsCompleted, "once the old link's task has ended, on may go");
            var second = new HubLink(hub.Uri, Key, (_, _) => { }, new LinkTiming { Backoff = [TimeSpan.FromSeconds(30)] });
            sw.Resumed();
            second.Start();
            var e2 = WaitUp(second);
            c.Check(s, e2 > e1, $"the new link takes a new epoch ({e1} then {e2})");
            c.Check(s, hub.Accepted == 2 && hub.MaxOpen == 1, $"the hub never held two links at once (accepted {hub.Accepted}, most open {hub.MaxOpen})");
            var t2 = second.Running;
            second.Stop();
            HubLink.WaitStopped(t2, TimeSpan.FromSeconds(10));
        }

        // A link that will not end soon: on waits its time, then goes.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var stuck = new HubLink(new Uri($"ws://127.0.0.1:{port}/v1/servers/alpha/link"), Key, (_, _) => { },
                new LinkTiming { ConnectTimeout = TimeSpan.FromSeconds(30) });
            stuck.Start();
            Thread.Sleep(100);
            var task = stuck.Running;
            sw.TakeOff(w.World, stuck);
            c.Check(s, task is { IsCompleted: false } && !sw.MayResume, "a link still connecting holds on back");
            clock.Now += LiveSwitch.OldLinkWaitSecs - 0.1;
            c.Check(s, !sw.MayResume, "until just short of its time");
            clock.Now += 0.2;
            c.Check(s, sw.MayResume, $"after {LiveSwitch.OldLinkWaitSecs} s on goes whatever the old link does");
            HubLink.WaitStopped(task, TimeSpan.FromSeconds(10));
        }
        finally
        {
            silent.Stop();
        }

        Sequence(c, s);
        TickReadsTheSwitchFirst(c, s);
        NamesGuardLifecycle(c, s);
    }

    private sealed record Rt(NetworkWorld World, HubLink Link);

    private static HubLink Unstarted() => new(new Uri("ws://127.0.0.1:9/v1/servers/alpha/link"), Key, (_, _) => { });

    /// <summary>
    /// The switch's sequence over a runtime, as the mod's ACE half wires it: off parks the runtime
    /// before the hooks stop seeing it, on reconciles before it publishes, and a stop ends all
    /// switching, finding the runtime to save wherever it is.
    /// </summary>
    private static void Sequence(Checks c, string s)
    {
        var clock = new Clock();
        var kept = new NetworkStore();
        var w = TestWorld.Make("alpha", new MemoryLink(), () => clock.Now, store: kept);
        var cy = w.Host.Add(0x5000_0003, "Cy");
        // Cy is on the network, and away when the switch goes off.
        w.World.Dn(cy.Id, ["on"]);
        cy.Online = false;
        w.World.OnLogout(cy.Id, "Cy");
        Rt? current = new(w.World, Unstarted());
        LiveControl<Rt>? control = null;
        var published = new List<Rt?>();
        var heldAtEveryPublish = true;
        var reconciledBeforePublish = true;
        var relinks = 0;
        Func<Rt, Rt> relink = p =>
        {
            relinks++;
            return p with { Link = Unstarted() };
        };
        control = new LiveControl<Rt>(new LiveSwitch(() => clock.Now), () => current,
            r =>
            {
                // At every publish the runtime is held somewhere a stop can find it.
                if (r is null && control!.Parked is null)
                    heldAtEveryPublish = false;
                if (r is not null && !w.Host.Shown.Any(x => x is { To: 0x5000_0003, Kind: "system", Text: NetworkWorld.TakenDownLine }))
                    reconciledBeforePublish = false;
                published.Add(r);
                current = r;
            },
            r => r.World, r => r.Link, p => relink(p));
        var first = current;
        c.Check(s, control.Apply(true) == LiveSwitch.Step.None && published.Count == 0, "on while on: nothing is published");
        c.Check(s, control.Apply(false) == LiveSwitch.Step.Off && current is null && control.Parked == first && heldAtEveryPublish,
            "off: the runtime is parked before the hooks stop seeing it");
        c.Check(s, w.World.Link == LinkState.Down && first!.Link.Running is null, "and its link is taken off");
        c.Check(s, control.World == w.World, "the parked world half is still the one the veto is pushed to");
        c.Check(s, control.Apply(false) == LiveSwitch.Step.None, "off while off: nothing");

        // While off, Cy logs in; ACE alone hears it.
        cy.Online = true;
        // The hub's operator took her out of the directory while she was away.
        kept.Characters[cy.Id].TakenOut = TakenOut.Operator;
        var on = control.Apply(true);
        c.Check(s, on == LiveSwitch.Step.On && relinks == 1 && current is not null && current != first && current.Link != first.Link
            && control.Parked is null, "on: a new runtime on a new link is published, nothing parked");
        c.Check(s, reconciledBeforePublish, "the logins missed while off are reconciled before the new runtime is published");

        // A stop while running hands back the running runtime, and nothing switches after it.
        var running = current;
        var count = published.Count;
        c.Check(s, control.Stop() == running && control.Stopped, "a stop while on hands back the running runtime");
        c.Check(s, control.Apply(false) == LiveSwitch.Step.None && published.Count == count && current == running,
            "after a stop, switching off does nothing");

        // A stop while off hands back the parked runtime.
        current = new Rt(w.World, Unstarted());
        var c2 = new LiveControl<Rt>(new LiveSwitch(() => clock.Now), () => current, r => current = r, r => r.World, r => r.Link,
            p => p with { Link = Unstarted() });
        var second = current;
        c2.Apply(false);
        c.Check(s, c2.Stop() == second && c2.Parked is null && c2.Apply(true) == LiveSwitch.Step.None && current is null,
            "a stop while off hands back the parked runtime, and nothing comes back on after it");

        // A stop that comes while a switch on is under way: nothing is published, and the stop
        // still finds the parked runtime.
        current = new Rt(w.World, Unstarted());
        LiveControl<Rt>? c3 = null;
        Thread? stopper = null;
        Rt? stoppedWith = null;
        var publishedOn = false;
        c3 = new LiveControl<Rt>(new LiveSwitch(() => clock.Now), () => current, r => { if (r is not null) publishedOn = true; current = r; },
            r => r.World, r => r.Link,
            p =>
            {
                stopper = new Thread(() => stoppedWith = c3!.Stop());
                stopper.Start();
                var until = DateTime.UtcNow.AddSeconds(5);
                while (!c3!.Stopped && DateTime.UtcNow < until)
                    Thread.Sleep(1);
                return p with { Link = Unstarted() };
            });
        var third = current;
        c3.Apply(false);
        var mid = c3.Apply(true);
        stopper?.Join(TimeSpan.FromSeconds(5));
        c.Check(s, mid == LiveSwitch.Step.None && !publishedOn && current is null && stoppedWith == third,
            $"a stop during a switch on: nothing published, and the stop gets the parked runtime ({mid}, published {publishedOn})");
    }


    /// <summary>
    /// The world tick (ACE's side, which the harness cannot load) must read the switch before it
    /// returns for a network that is off, or a switched-off network never comes back: pinned in its
    /// source.
    /// </summary>
    private static void TickReadsTheSwitchFirst(Checks c, string s)
    {
        var path = Path.Combine(SourceDir() ?? "", "..", "Dereth.Network", "Ace", "Patches.cs");
        if (!File.Exists(path))
        {
            c.Fail(s, "Patches.cs not found beside the harness's sources: " + path);
            return;
        }
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var body = MethodBody(text, "public static void TickPostfix()\n");
        var poll = body.IndexOf("\n        Mod.Poll();\n", StringComparison.Ordinal);
        var check = body.IndexOf("\n        if (r is null)\n            return;\n", StringComparison.Ordinal);
        c.Check(s, poll > 0 && check > 0 && poll < check,
            $"TickPostfix calls Mod.Poll() before its `if (r is null) return;` (poll at {poll}, null check at {check})");
        var before = poll > 0 ? Code(body[..poll]) : "";
        c.Check(s, poll > 0 && !System.Text.RegularExpressions.Regex.IsMatch(before, @"\b(return|throw)\b"),
            "and nothing before Mod.Poll() in TickPostfix can leave it first: " + before.Trim().Replace("\n", " | "));

        // The `@` name guards hold while the network is switched off: their first statement is the
        // names flag, never the running network.
        foreach (var (method, head) in new[]
                 {
                     ("public static bool CharacterCreatePrefix(", "if (!Mod.NamesGuarded)"),
                     ("public static bool RenamePrefix(", "if (!Mod.NamesGuarded || __1 is null)"),
                 })
        {
            var b = MethodBody(text, method);
            var statements = Code(b).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != "{").Skip(1).Take(2).ToList();
            c.Check(s, statements is [var first, "return true;"] && first == head && !b.Contains("R is null", StringComparison.Ordinal)
                    && !b.Contains("var r = R;", StringComparison.Ordinal),
                $"{method.Trim('(')} gates on Mod.NamesGuarded first, not on the running network: {string.Join(" | ", statements)}");
        }
    }

    /// <summary>
    /// The names guard's flag (ACE's side, source only): set from the names patch's result in
    /// Mod.cs, cleared only at Dispose (StopControl), and never touched by the switch, so switching
    /// the network off never lets a name holding `@` through.
    /// </summary>
    private static void NamesGuardLifecycle(Checks c, string s)
    {
        var ace = Path.Combine(SourceDir() ?? "", "..", "Dereth.Network", "Ace");
        var core = Path.Combine(SourceDir() ?? "", "..", "Dereth.Network", "Core");
        if (!Directory.Exists(ace) || !Directory.Exists(core))
        {
            c.Fail(s, "the mod's sources are not beside the harness's: " + ace);
            return;
        }
        var assign = new System.Text.RegularExpressions.Regex(@"\bnamesGuarded\s*=(?!=)");
        var found = new List<(string File, string Line)>();
        foreach (var file in Directory.GetFiles(ace, "*.cs").Concat(Directory.GetFiles(core, "*.cs")))
            foreach (var line in Code(File.ReadAllText(file).Replace("\r\n", "\n")).Split('\n'))
                if (assign.IsMatch(line))
                    found.Add((Path.GetFileName(file), line.Trim()));
        c.Check(s, found.Count == 2
                && found.Count(x => x is ("Mod.cs", "namesGuarded = on.Contains(PatchTargets.Names);")) == 1
                && found.Count(x => x is ("Mod.Control.cs", "namesGuarded = false;")) == 1,
            "namesGuarded is set only from the names patch (Mod.cs) and cleared only once (Mod.Control.cs): "
            + string.Join(" | ", found.Select(x => x.File + ": " + x.Line)));
        var control = File.ReadAllText(Path.Combine(ace, "Mod.Control.cs")).Replace("\r\n", "\n");
        var stop = Code(MethodBody(control, "private static void StopControl()"));
        var poll = Code(MethodBody(control, "private static void PollNow("));
        c.Check(s, stop.Contains("namesGuarded = false;", StringComparison.Ordinal) && poll.Length > 0
                && !poll.Contains("namesGuarded", StringComparison.Ordinal) && !Code(MethodBody(control, "internal static void Poll()")).Contains("namesGuarded", StringComparison.Ordinal),
            "the clear is in StopControl, and the settings poll (the off path among it) never touches the flag");
    }

    // ---- the command name ---------------------------------------------------------------------

    /// <summary>
    /// The mod's only command is `derethnetwork`: no `[CommandHandler("dn"` or `("dng"` attribute
    /// anywhere in its sources (ACE's loader would let it override DreamWeave's own `/dn`), and the
    /// help a player or the console reads names `@derethnetwork`, never `@dn` or `@dng`.
    /// </summary>
    private static void CommandName(Checks c)
    {
        const string s = "units/fork-command";
        var root = Path.Combine(SourceDir() ?? "", "..", "Dereth.Network");
        if (!Directory.Exists(root))
        {
            c.Fail(s, "the mod's sources are not beside the harness's: " + root);
            return;
        }
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        var banned = new System.Text.RegularExpressions.Regex(@"CommandHandler(?:Attribute)?\(\s*""dng?""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var any = new System.Text.RegularExpressions.Regex(@"\[CommandHandler\(\s*""derethnetwork""");
        var programmatic = new System.Text.RegularExpressions.Regex(@"\bTryAddCommand\b|\bCommandManager\.");
        var offenders = new List<string>();
        var registrars = new List<string>();
        var kept = 0;
        foreach (var file in files)
        {
            var code = Code(File.ReadAllText(file).Replace("\r\n", "\n"));
            if (banned.IsMatch(code))
                offenders.Add(Path.GetFileName(file));
            if (programmatic.IsMatch(code))
                registrars.Add(Path.GetFileName(file));
            kept += any.Matches(code).Count;
        }
        c.Check(s, files.Count > 0 && offenders.Count == 0,
            "no [CommandHandler(\"dn\" or (\"dng\" attribute exists in the mod (found in: " + string.Join(", ", offenders) + ")");
        c.Check(s, files.Count > 0 && registrars.Count == 0,
            "and no code in the mod registers a command by hand (TryAddCommand or CommandManager.; found in: " + string.Join(", ", registrars) + ")");
        c.Check(s, kept == 1, $"and exactly one [CommandHandler(\"derethnetwork\" attribute does ({kept})");
        var help = NetworkWorld.Help() + "\n" + NetworkWorld.AdminHelp("@derethnetwork");
        var stale = new System.Text.RegularExpressions.Regex(@"[@/]dng?\b");
        c.Check(s, help.Contains("@derethnetwork global <text> - ", StringComparison.Ordinal)
                && help.Contains("@derethnetwork on - ", StringComparison.Ordinal)
                && !stale.IsMatch(help),
            "the help names @derethnetwork, with Global as `@derethnetwork global <text>`, and never @dn or @dng");
    }

    /// <summary>A method's text, from its signature to its closing brace; empty when it is not found.</summary>
    private static string MethodBody(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return start < 0 || end < 0 ? "" : text[start..end];
    }

    /// <summary>Source text without its line comments.</summary>
    private static string Code(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string? SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);

    /// <summary>
    /// A hub on 127.0.0.1 that answers each upgrade itself, sends its frames, then reads the world's
    /// frames until the world closes: it counts the links it holds open at once.
    /// </summary>
    private sealed class LoopbackHub : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly IReadOnlyList<string> frames;
        private int open;
        private int maxOpen;
        private int accepted;
        private int received;
        private volatile bool stopped;

        /// <summary>How long the hub takes to answer a close.</summary>
        private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(500);

        public LoopbackHub(IReadOnlyList<string> frames)
        {
            this.frames = frames;
            listener.Start();
            Uri = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/servers/alpha/link");
            _ = Task.Run(Accept);
        }

        public Uri Uri { get; }
        public int Accepted => Volatile.Read(ref accepted);
        public int MaxOpen => Volatile.Read(ref maxOpen);

        public bool WaitOpen(int n, TimeSpan limit) => Wait(() => Volatile.Read(ref open) >= n, limit);

        public bool WaitReceived(int n, TimeSpan limit) => Wait(() => Volatile.Read(ref received) >= n, limit);

        private static bool Wait(Func<bool> done, TimeSpan limit)
        {
            var until = DateTime.UtcNow + limit;
            while (!done() && DateTime.UtcNow < until)
                Thread.Sleep(20);
            return done();
        }

        private async Task Accept()
        {
            while (!stopped)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }
                Interlocked.Increment(ref accepted);
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            var now = Interlocked.Increment(ref open);
            var counted = true;
            int seen;
            while (now > (seen = Volatile.Read(ref maxOpen)) && Interlocked.CompareExchange(ref maxOpen, now, seen) != seen)
            {
            }
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && stream.Read(one, 0, 1) == 1)
                        head.Append((char)one[0]);
                    var key = head.ToString().Split("\r\n")
                        .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                                         $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));
                    foreach (var f in frames)
                    {
                        var payload = Encoding.UTF8.GetBytes(f);
                        stream.Write([0x81, (byte)payload.Length]);
                        stream.Write(payload);
                    }
                    while (true)
                    {
                        var h = ReadExactly(stream, 2);
                        if (h is null)
                            return;
                        var op = h[0] & 0x0F;
                        long len = h[1] & 0x7F;
                        if (len == 126)
                            len = ReadExactly(stream, 2) is { } l2 ? (l2[0] << 8) | l2[1] : -1;
                        else if (len == 127)
                            len = ReadExactly(stream, 8) is { } l8 ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(l8) : -1;
                        if (len < 0 || len > 1 << 20)
                            return;
                        if ((h[1] & 0x80) != 0 && ReadExactly(stream, 4) is null)
                            return;
                        if (len > 0 && ReadExactly(stream, (int)len) is null)
                            return;
                        if (op == 0x8)
                        {
                            // A hub that takes a moment to close; it holds the link no longer once it answers.
                            Thread.Sleep(CloseDelay);
                            counted = false;
                            Interlocked.Decrement(ref open);
                            stream.Write([0x88, 0x00]);
                            return;
                        }
                        if (op == 0x1)
                            Interlocked.Increment(ref received);
                    }
                }
            }
            catch (Exception)
            {
                // The world dropped the connection.
            }
            finally
            {
                if (counted)
                    Interlocked.Decrement(ref open);
            }
        }

        private static byte[]? ReadExactly(NetworkStream stream, int n)
        {
            var b = new byte[n];
            var got = 0;
            while (got < n)
            {
                var r = stream.Read(b, got, n - got);
                if (r == 0)
                    return null;
                got += r;
            }
            return b;
        }

        public void Dispose()
        {
            stopped = true;
            listener.Stop();
        }
    }
}
