# RSyncStageCraft

RSyncStageCraft is a Dalamud plugin that keeps a shared scene in step between
players: placed furniture and lights, client-only actors and their animation,
and appearance copies. Every change goes through a server, which holds the
authoritative state; each client draws that state locally and sends nothing to
the game server.

The plugin requires an operator-provided compatible server address. A new
install has no server address set; enter the one you were given in
**Settings → Server**.

## Building

The solution is `RSyncStageCraft-Client.slnx`. It builds with the .NET 10 SDK
on Windows, with Dalamud installed where the Dalamud SDK expects it:

```
dotnet restore RSyncStageCraft-Client.slnx --locked-mode
dotnet build RSyncStageCraft-Client.slnx --configuration Release --no-restore
dotnet test RSyncStageCraft-Client.slnx --configuration Release --no-build
```

| Project | What it is |
|---|---|
| `src/RSyncStageCraft.Protocol` | wire contracts, codecs and shared value types (no dependencies) |
| `src/RSyncStageCraft.Client` | the client runtime, without Dalamud |
| `src/RSyncStageCraft` | the Dalamud plugin |
| `tests/*` | the tests of each |

## Licence

The protocol project and its tests are licensed under the MIT License; the
rest of the repository, including the client and the plugin, under
AGPL-3.0-only. See `NOTICE`, `LICENSE`, `src/RSyncStageCraft.Protocol/LICENSE`
and `THIRD-PARTY-NOTICES.md`.

Contributions: see `CONTRIBUTING.md`.
