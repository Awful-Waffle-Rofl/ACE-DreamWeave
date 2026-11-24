// Local modification (not upstream's): what the operator's live switches need of the world half.
//
// The room-mirroring veto (`dereth_network_room_mirroring` false): no line goes between this world's
// own TurbineChat rooms and Discord, either way. A line said in one of them is not sent to the hub
// (CarriedRoom), and a line the hub asks the world to say in one from Discord is refused `room_off`
// (SayRoom). The Global room is not touched by it, nor are the rooms the world joins on the link or
// what the store keeps of them: lifting the veto restores everything at once.
//
// The link gone (`dereth_network_enabled` false): the world half forgets its link exactly as when a
// link goes down, and forgets its epoch, so that the next link's frames are its own.
//
// While switched off every hook passes to ACE, so the world half hears no login or logout. At the
// switch off it takes who is online (OnlineNow); when the network comes back, before any hook sees
// it again, ResumeAfterOff runs the world half's own logout for each character that left meanwhile
// and its own login for each that came. Presence and watches need nothing: a lost link forgets them,
// and the next welcome asks for them again for everyone online. A character that left and came back
// while the network was off is seen as having stayed.

namespace Dereth.Network;

/// <summary>A character online when the network was switched off: its name then, and its facts then when it was listed.</summary>
public sealed record OnlineAtOff(string Name, CharacterFacts? Facts);

public sealed partial class WorldConfig
{
    private bool roomMirroringVetoed;

    /// <summary>
    /// The operator's veto on mirroring this world's own rooms into Discord, both ways. Written by the
    /// world tick's settings poll, read under the world half's lock.
    /// </summary>
    public bool RoomMirroringVetoed
    {
        get => Volatile.Read(ref roomMirroringVetoed);
        set => Volatile.Write(ref roomMirroringVetoed, value);
    }
}

public sealed partial class NetworkWorld
{
    /// <summary>
    /// The link is switched off: everything a lost link does (what waits is held, remote friends show
    /// offline, the operator's requests are given up), and no epoch is current.
    /// </summary>
    public void LinkGone()
    {
        lock (gate)
        {
            OnLinkDown();
            epoch = 0;
        }
    }

    /// <summary>
    /// Who is online now, as the switch takes it when it switches the network off. A character whose
    /// facts cannot be read is taken without them (its profile is then not refreshed at the resume).
    /// </summary>
    public IReadOnlyDictionary<uint, OnlineAtOff> OnlineNow()
    {
        lock (gate)
        {
            var online = new Dictionary<uint, OnlineAtOff>();
            foreach (var id in host.OnlineCharacters())
            {
                if (host.OnlineName(id) is not { } name)
                    continue;
                CharacterFacts? facts = null;
                try
                {
                    facts = IsListed(id) ? host.Facts(id) : null;
                }
                catch (Exception e)
                {
                    log(LogLevel.Warn, $"the facts of character {id} could not be taken at the switch off ({e.GetType().Name}); its profile is not refreshed at the resume");
                }
                online[id] = new OnlineAtOff(name, facts);
            }
            return online;
        }
    }

    /// <summary>
    /// The network is coming back (the link is still down): what the skipped logins and logouts
    /// would have done is done now. A character online at the switch off (`atOff`) and gone now
    /// leaves as it would have at its logout, with its name and facts as they were at the switch
    /// off; one online now that was not then arrives as at its login, and its friends panel, whose
    /// full list ACE sent without the network's rows, is given each remote friend's row.
    ///
    /// The tell squelches a linked character keeps while away are not known for one that left while
    /// the network was off: they are cleared, so that every tell from this world's characters is held
    /// back while it is away (as for one that kept none) until its next logout keeps them again.
    /// Nothing here sends: while the link is down the sends it would make are not made, and the next
    /// welcome watches everyone online again.
    ///
    /// Best effort, a character at a time: one that cannot be reconciled is logged and passed over,
    /// and nothing here throws, so the network always comes back on.
    /// </summary>
    public void ResumeAfterOff(IReadOnlyDictionary<uint, OnlineAtOff> atOff)
    {
        lock (gate)
        {
            HashSet<uint> now;
            try
            {
                now = host.OnlineCharacters().ToHashSet();
            }
            catch (Exception e)
            {
                log(LogLevel.Warn, $"who is online could not be read at the resume ({e.GetType().Name}); the logins and logouts missed while off are not reconciled");
                return;
            }
            foreach (var (id, left) in atOff)
            {
                if (now.Contains(id))
                    continue;
                try
                {
                    if (Record(id) is { Settings.On: true, AskedLink: true })
                        RecordMut(id).AwaySquelches = null;
                    DirectoryLeft(id, left.Name, left.Facts);
                    OnLogoutLocked(id, left.Name);
                }
                catch (Exception e)
                {
                    log(LogLevel.Warn, $"the logout of character {id} while the network was off could not be reconciled ({e.GetType().Name}: {e.Message})");
                }
                dirty = true;
            }
            foreach (var id in now)
            {
                if (atOff.ContainsKey(id))
                    continue;
                try
                {
                    OnLoginLocked(id);
                    if (IsOn(id) && Record(id) is { } r)
                    {
                        foreach (var f in r.Friends.Values.ToList())
                            if (StandIn(f) is uint standIn)
                                host.ShowFriendRow(id, FriendRowKind.Added, standIn, f.ToString(), false);
                    }
                }
                catch (Exception e)
                {
                    log(LogLevel.Warn, $"the login of character {id} while the network was off could not be reconciled ({e.GetType().Name}: {e.Message})");
                }
                dirty = true;
            }
        }
    }
}