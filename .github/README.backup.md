<img src=".github/assets/banner.svg" alt="Tessera — many replicas, one picture" width="100%">

Tessera is a real-time collaborative canvas. Several people draw on one board at once, from
different machines, over unreliable networks — and every replica ends up showing the same picture.

The merge engine is written from scratch rather than imported. That is the point of the project.

![.NET 10](https://img.shields.io/badge/.NET-10-16202B?style=flat-square)
![C#](https://img.shields.io/badge/C%23-latest-2F6BA8?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-C99A3F?style=flat-square)

## The problem

A whiteboard is a shared mutable document edited concurrently with no lock. Three requirements
pull against each other:

**Convergence.** Two replicas that have seen the same edits must show the same document, whatever
order those edits arrived in.

**Local responsiveness.** A drag has to render on the next frame. A frame is 16.7ms; a round trip
to `us-east-1` is 30–80ms. The interface cannot wait for the server.

**Offline tolerance.** A client that loses the network keeps working, and rejoins without losing or
duplicating anything.

The second forces every edit to apply locally before the server has seen it. Combined with the
first, that forces a merge strategy — because two people will always change the same thing before
either learns about the other. The third rules out the server being the only source of truth.

<img src=".github/assets/convergence.svg" alt="Two replicas edit one shape while offline. One moves it, the other recolours it. On reconnect both edits survive." width="100%">

That case — one user dragging a shape while another recolours it — is the one naive designs get
wrong. Storing a shape as a single value with a single timestamp makes the later write discard the
earlier one wholesale, which users experience as "my changes disappeared".

## How it works

Every scalar property is its own mergeable register, so edits to different properties of one shape
never contend. Three problems fall out of that, and each has a specific answer:

| Problem | Approach |
| :-- | :-- |
| Machine clocks disagree by seconds to minutes, so wall-clock ordering lets a fast clock win every conflict forever | **Hybrid logical clocks** — a physical component that keeps "latest" meaning roughly what a human expects, and a logical counter that carries ordering when physical time cannot. Peers implausibly far ahead are rejected, so one wrong clock cannot poison the rest |
| Integer z-indices renumber every shape above an insertion, so concurrent reorders produce rewrites no merge rule can reconcile | **Fractional indexing** — variable-length base-62 keys with room between any two, so a reorder rewrites exactly one key. An integer part keeps appends at constant length, which matters because every new shape appends |
| Concurrent mutation of one document is where convergence bugs hide, and they are unreproducible by nature | **One actor per board** — every message funnels through a single channel consumer that owns the replica. No locks, deterministic order per document, and concurrency that lives between boards rather than inside one |

Deletion is add-wins: an edit concurrent with a delete keeps the shape. The costs are asymmetric —
a shape that wrongly survives is deleted again with one keystroke, while one wrongly removed is
work the user may never get back.

[**ARCHITECTURE.md**](ARCHITECTURE.md) covers the wire protocol, persistence, and the alternatives
that were rejected — including why not OT, and why not simply using Yjs.

## Status

Server-side is done and runnable. The browser client is not built yet.

| Component | State |
| :-- | :-- |
| CRDT core — clocks, order keys, merge | Done · 43 tests |
| Rooms, wire protocol, repository | Done · 37 tests |
| WebSocket server | Done |
| TypeScript client and canvas | Not started |
| Postgres repository | Not started |
| AWS infrastructure | Not started |

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd server && dotnet run --project src/Tessera.Server
```

| Endpoint | Purpose |
| :-- | :-- |
| `GET /health` | Liveness |
| `GET /api/boards/{id}` | The merged document, materialised |
| `GET /api/boards/{id}/socket` | Sync connection |

With the server up, `scripts/smoke.mjs` drives it end to end — two clients on one board, operations
broadcast to peers but never echoed to their author, presence that never touches the log, and a
push forged as another replica refused:

```bash
node scripts/smoke.mjs
```

## Tests

```bash
cd server && dotnet test
```

Convergence is checked as a property rather than by example. A generated 400-operation session
across three replicas is replayed in 200 random delivery orders, and every ordering must produce a
byte-identical document. Idempotence and associativity are checked the same way — the orderings
that break a merge are precisely the ones nobody thinks to write by hand.

The build runs with `TreatWarningsAsErrors` and the recommended analyzer set.

## Layout

```
server/src/Tessera.Crdt      merge engine — no transport, no storage, no framework
server/src/Tessera.Sync      rooms, wire protocol, repository abstraction
server/src/Tessera.Server    WebSocket host
server/tests                 xUnit suites
scripts/smoke.mjs            end-to-end check against a running server
```

The merge engine has no dependency on ASP.NET, a database, or a transport — it is a pure function
of the operations it is given, which is what lets it be tested without a network and reused on both
sides of one.

## License

MIT
