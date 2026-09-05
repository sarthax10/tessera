# Tessera

A real-time collaborative canvas. Several people draw on one board at once, from different
machines, over unreliable networks, and every replica converges on the same picture.

The merge engine is written from scratch rather than imported — see [ARCHITECTURE.md](ARCHITECTURE.md)
for the design and the alternatives that were rejected.

## Status

Server-side is done and runnable. The browser client is not built yet.

| | |
|---|---|
| CRDT core | done — 43 tests |
| Rooms, protocol, repository | done — 37 tests |
| WebSocket server | done |
| TypeScript client + canvas | not started |
| Postgres repository | not started |
| AWS infrastructure | not started |

## Running

Requires the .NET 10 SDK.

```bash
cd server && dotnet run --project src/Tessera.Server
```

`GET /health`, `GET /api/boards/{id}` for the merged document, and
`GET /api/boards/{id}/socket` for the sync connection.

With the server running, `scripts/smoke.mjs` drives it end to end — two clients on one board,
operations broadcast to peers but not echoed to their author, presence that never reaches the log,
and a forged push refused:

```bash
node scripts/smoke.mjs
```

## Tests

```bash
cd server && dotnet test
```

Convergence is checked as a property rather than by example: a generated 400-operation session
across three replicas is replayed in 200 random delivery orders, and every ordering must produce an
identical document. Idempotence and associativity are checked the same way — the orderings that
break a merge are the ones nobody writes by hand.

The build runs with `TreatWarningsAsErrors` and the recommended analyzers.

## Layout

```
server/src/Tessera.Crdt      merge engine — no transport, no storage, no framework
server/src/Tessera.Sync      rooms, wire protocol, repository abstraction
server/src/Tessera.Server    WebSocket host
server/tests                 xUnit suites
scripts/smoke.mjs            end-to-end check against a running server
```
