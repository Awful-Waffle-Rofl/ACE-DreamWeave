# ACE-DreamWeave

An open-source Asheron's Call server. This is the server that runs DreamWeave, built on [ACEmulator](https://github.com/ACEmulator/ACE).

**This is the engine, not the game.** It is a complete, buildable, runnable AC server. It is not a copy of the DreamWeave world. The custom content - the weenies, quests, dungeons and placements that make DreamWeave what it is - lives in a separate private repository along with the tooling used to author it, and is not published here.

That split is deliberate rather than grudging. The server code is genuinely useful to anyone running an AC shard; one shard's content is only useful to that shard.

***
## Disclaimer

**This project is for educational and non-commercial purposes only. Use of the game client is for interoperability with the emulated server.**
- Asheron's Call was a registered trademark of Turbine, Inc. and WB Games Inc., which has since expired.
- This project is not associated or affiliated in any way with Turbine, Inc. or WB Games Inc.
***

## Relationship to ACEmulator

This repository shares ACEmulator's real history. Every commit before [`08471633`](https://github.com/ACEmulator/ACE/commit/08471633e4f8eec7077ecca5b5bde80223b853df) is theirs, unmodified, and the contributor list is overwhelmingly theirs. The fork's own server changes sit above that point as a single commit.

ACEmulator is the upstream project, and the place to start if you want a stock AC server rather than this fork's variations. Their [wiki](https://github.com/ACEmulator/ACE/wiki) remains the best general documentation for developing and hosting an ACE server, and nearly all of it applies here unchanged.

Licensed AGPL-3.0, inherited from upstream. Note that the AGPL's network clause concerns running a modified server for players, not only distributing the code.

## What the fork adds

On top of stock ACEmulator: a class-ability system with trainers and skill tokens, equipment and weapon modifiers, in-game banking, a mule and storage system, a realm and instancing layer, and a network of linked portals.

These are server-side systems. The content that surfaces them in game is not included here, so a stock world database will run fine but most of them will not be reachable in play.

## Build

```
dotnet build Source/ACE.sln -c Debug -p:Platform=x64
```

The `-p:Platform=x64` is not optional. `ACE.Server`, the test projects and `ACE.Database.LoadTest` target x64 specifically, and without it the wrong platform is selected for those.

## Run

`ACE.Server` needs `Config.js` and `log4net.config` beside the built executable - copy them from the adjacent `.example` files, or let `Program.cs` scaffold them on first run.

You will also need three MySQL or MariaDB databases: `ace_auth`, `ace_shard` and `ace_world`, one connection block each under `Config.js`'s `MySql` section. Schema for all three is in `Database/Base/`. The world database itself is distributed separately by the upstream project - see [ACE-World-16PY-Patches](https://github.com/ACEmulator/ACE-World-16PY-Patches).

## Tests

```
dotnet test Source/ACE.Server.Tests --test-adapter-path:.
dotnet test Source/ACE.Database.Tests --test-adapter-path:.
dotnet test Source/ACE.DatLoader.Tests --test-adapter-path:.
```

Tests that need a resource you do not have skip themselves rather than failing: those needing MySQL, those needing the client's `.dat` files, and a handful that cross-check server code against the fork's private content. A clean checkout should report zero failures, with skips.

CI stands up a real MySQL and imports the upstream world database, so pull requests get meaningful coverage rather than a wall of skips.

One local-only caveat: several tests locate repository files by walking up from the test assembly, so do not redirect test output outside the tree with `--artifacts-path`. That breaks the walk and produces failures unrelated to your change.

## Contributing

Pull requests are welcome, and `build-test` must pass.

Changes touching the custom gameplay systems are harder to verify from here than you might expect, because the tests that cross-check them against content cannot run without that content. Those checks run on our side, so expect to be asked for a little patience on anything in that area.

If your change is really an upstream fix rather than something specific to this fork, [ACEmulator](https://github.com/ACEmulator/ACE) is the better home for it. It will reach far more people there, and it flows back down here.

This project inherits upstream's [Contributor Code of Conduct](CODE_OF_CONDUCT.md). By participating you agree to abide by its terms.
