# Vendored upstream: Dereth.Network ACE mod

This directory holds an unmodified import of a third-party project. Do not edit
the imported files in place; local changes are recorded below.

- Upstream repository: https://github.com/dereth-network/dereth
- Upstream commit: f27fd114a6f6f6652451f68ee1326f47390c3664
- Date imported: 2026-11-14

## Imported paths

Each upstream path is placed under `third_party/dereth/` with the same relative path.

| Upstream path | License |
| --- | --- |
| `network/ace-mod` | AGPL-3.0 (GNU Affero General Public License v3, see `network/ace-mod/LICENSE`) |
| `network/conformance/vectors` | MIT (see `network/conformance/LICENSE`) |
| `network/conformance/LICENSE` | MIT license text covering the conformance vectors |

Embedded third-party data: `network/ace-mod/Dereth.Network/Core/Cp1252BestFit.cs` embeds a Windows-1252 best-fit table derived from a Unicode data file, used under the Unicode License v3. Its notice is carried in that file and in `network/ace-mod/README.md` (License section).

## Bump procedure

In a clone of the upstream repository (the pathspec must equal the Imported paths table above; a wider one pulls in files that are not vendored, and one missing path makes `git apply` reject the whole patch):

    git diff --output=bump.patch <old-sha>..<new-sha> -- network/ace-mod network/conformance/vectors network/conformance/LICENSE

Then, at the root of this repository:

    git apply --directory=third_party/dereth -3 bump.patch

Produce the patch with `--output`, never a PowerShell `>` redirect: PowerShell 5.1 rewrites LF to CRLF and the patch then fails to apply.

Update the commit and date above, and record any local changes below.

## Local modifications

Version `0.1.5-fork.1` (upstream `0.1.5`). Paths are relative to `network/ace-mod/`. New code lives in new files; every vendored edit is marked `Local modification` in place.

Server settings (bool, default true, read from ACE's PropertyManager on the world tick, never at Initialize): `dereth_network_enabled` (live master switch), `dereth_network_traffic_log` (traffic log on/off), `dereth_network_room_mirroring` (false = no bridge between this world's own rooms and Discord, either way; the Global room is untouched). They must be registered in ACE (this repository registers them separately); an unregistered key is read from the shard database on a cache miss. The first read that fails counts as true; a later read that fails keeps the last value read, so a passing fault neither lifts the veto nor brings a switched-off network back.

Behaviour worth knowing:

- Switching on reconciles what the hooks did not hear while the network was off: each character online at the switch off and gone now is logged out as at its logout (with its name and directory facts as at the switch off), and each online now that was not then is logged in as at its login, and is sent its remote friends' rows, since ACE sent its full friends list without them. A character that logged out and back in while off is treated as having stayed. A linked character that left while off has its away tell squelches cleared (they were not taken as it left), so every tell from this world's characters is held back while it is away until its next logout keeps them. A character deleted while off is not reconciled: its deletion hook passed to ACE alone, so the mod does not take it out of the directory. The reconcile is best effort a character at a time: one the host cannot read is logged and passed over, so the network always comes back on.
- The traffic log goes beside the first log4net file appender's file. With the stock example config that is a relative `ACE_Log.txt`, so locally the log lands in `<server exe folder>/dereth-network/`; with the docker config it is `/ace/Logs/dereth-network/`.

Vendored files changed:

- `Dereth.Network/Ace/Mod.cs` - `Start` first logs `build <version>+<commit sha>` (`Mod.Build.cs`); `Runtime.StartLinkOnce` starts no link once the mod is stopping; `Mod` made partial; the link gets the traffic log as its tap (`StartControl`); the `/derethnetwork verify` HttpClient wrapped in `TrafficHttpHandler`; `namesGuarded` set once the names patch is on; `Dispose` stops the switch and flushes the traffic log; `Stop` also saves and flushes a parked (switched-off) runtime.
- `Dereth.Network/Ace/Patches.cs` - `TickPostfix` calls `Mod.Poll()` before its null check, so a switched-off network can come back; the `@` name guards (`CharacterCreatePrefix`, `RenamePrefix`) gated on `Mod.NamesGuarded` instead of the running network, so they hold while it is switched off.
- `Dereth.Network/Core/HubLink.cs` - optional `TrafficLog? tap` constructor parameter, tapped on the link's own tasks (up, down, refused, each frame in, each frame sent, each frame dropped unsent: for a gone connection, too long, a failed send, or still queued when the link stopped); header comment describes the tap.
- `Dereth.Network/Core/NetworkWorld.cs` - `WorldConfig` made partial, for the veto flag.
- `Dereth.Network/Core/NetworkWorld.Rooms.cs` - `CarriedRoom` skips the world's own rooms while the veto is on (outbound).
- `Dereth.Network/Core/NetworkWorld.Say.cs` - `SayRoom` skips the world's own rooms while the veto is on, so the hub's say is refused `room_off` (inbound).
- Command names: `Dereth.Network/Ace/Commands.cs` registers only `derethnetwork` (upstream also registers `dn` and `dng`), because DreamWeave's own `/dn` is the Drift Network recall and a mod command overrides a built-in one. Global is `@derethnetwork global <text>`; there is no short alias. Every player-facing help line, usage line, refusal and log text in `Dereth.Network/Ace/*.cs` and `Dereth.Network/Core/*.cs` that named `@dn`, `/dn` or `@dng` now names `@derethnetwork`, and the console's operator help is typed `derethnetwork`. The harness's `tests/*.cs` expected strings follow, except the hub's Discord `/dn` slash command in `tests/SayHubChecks.cs`, which is the hub's own. `network/ace-mod/README.md` is upstream's and still says `@dn`; those references do not apply here. Text the hub sends and the mod only relays is not ours to change.
- `Dereth.Network/Dereth.Network.csproj`, `Dereth.Network/Meta.json` - version `0.1.5-fork.1`; upstream's `Mod.Version` constant is left at its own value.
- `tests/UnitChecks.cs` - runs `ForkChecks`.
- `tests/FakeWorld.cs` - `FakeHost.Faulty`: characters whose facts and friend rows throw, for the resume's best-effort check.

Files added:

- `Dereth.Network/Ace/Mod.Build.cs` - `Mod.BuildVersion`, the assembly's informational version (the csproj version plus the `+<commit sha>` the .NET SDK appends from git), logged at start so a host running an outdated copy of the mod folder shows it.
- `Dereth.Network/Ace/Mod.Control.cs` - reads the three settings once a second on the world thread and drives `LiveControl` with them, pushes the veto and the traffic log flag, stops switching first thing at Stop (writing a `mod.stop` record), and finds the log folder (first log4net file appender, nested ones too; else `<mod folder>/logs`).
- `Dereth.Network/Core/LiveSwitch.cs` - the switch, ACE-free so the harness drives it: poll pace, never a new link before the old link's task has ended (or 15 s), and `LiveControl`, the off/on sequence (park before the hooks stop seeing the runtime; reconcile before publishing the new one; a stop under the same lock, after which nothing switches).
- `Dereth.Network/Core/NetworkWorld.Fork.cs` - the veto flag on `WorldConfig`, `NetworkWorld.LinkGone()`, and `OnlineNow()`/`ResumeAfterOff()`, which reconcile logins and logouts missed while off.
- `Dereth.Network/Core/TrafficLog.cs` - the traffic log: bounded queue, one JSONL file per UTC day under `<log dir>/dereth-network/`, ASCII-escaped, redacting the registry key, the `/derethnetwork verify` token and `person.result` link codes; never deletes.
- `Dereth.Network/Core/TrafficHttpHandler.cs` - records the HTTPS verify and answer calls, handing on at most 64 KiB of an answer as HubVerify reads.
- `tests/ForkChecks.cs` - harness sections `units/fork-veto`, `units/fork-linkgone`, `units/fork-traffic`, `units/fork-control`, `units/fork-command`.

Outside this directory, private to this repository: `.github/workflows/dereth-network-mod.yml` builds the mod, runs the harness and PatchApply, and uploads the built mod (upstream's own CI file is not vendored).