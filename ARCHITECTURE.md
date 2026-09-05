# Architecture

## The problem

A whiteboard is a shared mutable document edited concurrently with no lock. Three requirements:

1. **Convergence.** Two replicas that have seen the same operations show the same document,
   whatever order those operations arrived in.
2. **Local responsiveness.** A drag renders on the next frame. A frame is 16.7ms; a round trip to
   `us-east-1` is 30–80ms. The UI cannot wait for the server.
3. **Offline tolerance.** A client that loses the network keeps working and rejoins without losing
   or duplicating work.

(2) forces optimistic local application. (1) plus (2) forces a merge strategy, because two clients
will apply conflicting changes before either learns about the other. (3) rules out the server being
the only source of truth.

## Document model

A board is a map of shape id to shape. Shapes are flat records; nesting is a `parent` property,
which keeps the CRDT a map rather than a sequence and avoids the hard cases.

Every scalar property is an independently mergeable register. Two users editing the same shape's
different properties — one dragging, one recolouring — must both win. A shape treated as one opaque
value with a single timestamp would make the recolour discard the drag, which is the bug users
report as "my changes disappeared".

## Where the merge happens

```
Browser:  Renderer <- Replica (CRDT) <- Sync client <- IndexedDB
                                   |
                          JSON over WebSocket
                                   |
Server:   Room actor (one per board, single consumer)
            |- Replica (same CRDT, C#)
            |- Sequencer, fan-out
            +- Presence (ephemeral, never persisted)
          Append-only op log + snapshots -> Postgres
```

Both sides run the CRDT. The client needs it to apply local edits instantly and merge remote ones.
The server needs it to materialise state for snapshots, REST reads, thumbnails and permission
checks — a process that could only relay opaque bytes would have to replay the whole log to answer
"what does this board look like now?"

That means the merge logic exists twice, in C# and TypeScript. Cross-language conformance vectors
keep them honest: sequences of operations paired with the exact expected state, loaded by both the
xUnit and Vitest suites, with a vector added for every merge bug found.

## The CRDT

An op-based CRDT with per-property last-writer-wins registers.

**Identity.** Shape and operation ids are minted client-side, so an offline replica can keep
creating shapes with no coordination.

**Time.** Operations are timestamped with a hybrid logical clock — `(wallMillis, logical, replica)`,
ordered lexicographically. `Date.now()` alone is wrong: client clocks disagree by seconds to
minutes, so a fast clock wins every conflict forever and a slow one can never edit anything. A pure
Lamport clock fixes causality but drifts arbitrarily far from real time, so "last writer" stops
meaning "most recent". The hybrid keeps both — causal ordering, within clock-skew distance of wall
time — in about forty lines. Peers more than five minutes ahead are rejected, so one machine with a
wrong clock cannot drag every replica it syncs with into the future.

**Registers and deletion.** Each property holds `(value, timestamp)`; merge takes the higher
timestamp. Deletion is a tombstone, and creation is add-wins: a delete concurrent with an edit
keeps the shape. The costs are asymmetric — a shape that wrongly survives is deleted again with one
keystroke, one wrongly removed is work the user may never get back.

**Z-order.** An integer index would renumber every shape above an insertion point, and concurrent
reorders would produce overlapping rewrites no merge rule reconciles. Shapes instead carry a
fractional index: a variable-length base-62 string compared lexicographically, with room between
any two distinct keys. Reordering rewrites one key. Appending increments an integer part rather
than extending the string — a pure-fraction scheme grows keys by roughly one character per five
appends, and appending is what every newly drawn shape does.

## Wire protocol

JSON over one WebSocket. MessagePack is the intended destination but is an optimisation; reading a
frame in the network tab is worth more right now, and the encoding is confined to one class.

Two streams share the socket. **Document** traffic is durable, ordered and replayable: `Push`,
`Ack`, `Broadcast`, and a join carrying a version vector. **Presence** — cursors and selection — is
ephemeral and never persisted. Cursor updates run about 30/sec per participant and are worthless a
frame later; logging them would dominate storage and replay cost for data nobody reads twice.
Presence is dropped under backpressure, document operations never are.

Reconnect is a version-vector diff rather than a reload, so a client offline for a week costs one
diff.

## Server

**One actor per room.** Every message for a board flows through a single channel consumer that owns
the replica: no locks, no concurrent mutation, deterministic order per document. Rooms are
independent, so concurrency lives between boards and never inside one. A lock around a shared
replica would let the same operations interleave differently on different servers — exactly the
nondeterminism that makes a convergence bug impossible to reproduce.

**Event-sourced persistence.** Operations append to a log; state is a fold over it. Version history
and point-in-time recovery come free.

**Backpressure.** Each subscriber has a bounded outbound queue. Presence is dropped when it fills;
a client too slow for document traffic is disconnected and resyncs on return, rather than being
allowed to become unbounded server memory.

**Trust boundary.** A client may only submit operations stamped with its own replica id. Otherwise
anyone could forge writes as another participant, or fabricate a far-future timestamp under their
identity that none of their genuine edits could outrank.

## Deployment

WebSockets are long-lived, which rules out Lambda. ECS Fargate behind an ALB, Aurora Serverless v2
for the log, S3 and CloudFront for the client, CDK for the infrastructure.

Clients on one board must reach the same instance or two actors own one document, so boards are
routed by id at the edge with a Redis-backed ownership table for failover. Full pub/sub fan-out
between instances is simpler to build but adds a hop to every operation and gives up the
single-writer guarantee.

## Rejected alternatives

**Operational transformation** needs a transformation function for every pair of operation types,
and those are notoriously easy to get subtly wrong — published OT algorithms have shipped with
convergence bugs that took years to surface. It also assumes a central server orders operations,
which conflicts with offline editing. For a map of shapes the CRDT formulation is simpler and
provably convergent.

**Yjs** is excellent and is what production should use. It is not used here because the merge
engine is the point of the project; importing it would leave a whiteboard UI wired to someone
else's library. This is a deliberately reinvented wheel.

**Server-authoritative reconciliation** (the Replicache model) was the closest call — genuinely
simpler, with strong consistency. Rejected because rebasing requires re-running mutation logic on
the server, coupling server deployments to client versions, and because it degrades badly for the
long-offline case.
