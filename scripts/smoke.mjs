// End-to-end check against a running server: two clients, one board, one edit.
// Usage: node scripts/smoke.mjs

const URL = 'ws://localhost:5099/api/boards/demo/socket';

const hex = (n, width) => n.toString(16).padStart(width, '0');
const replicaId = (n) => hex(n, 16);
const hlc = (wall, logical, replica) => `${hex(wall, 12)}.${hex(logical, 4)}.${replicaId(replica)}`;

function connect() {
  const ws = new WebSocket(URL);
  const received = [];
  const waiters = [];

  ws.addEventListener('message', (event) => {
    const message = JSON.parse(event.data);
    received.push(message);

    for (let i = waiters.length - 1; i >= 0; i--) {
      if (waiters[i].match(message)) {
        waiters[i].resolve(message);
        waiters.splice(i, 1);
      }
    }
  });

  const open = new Promise((resolve, reject) => {
    ws.addEventListener('open', resolve);
    ws.addEventListener('error', reject);
  });

  return {
    ws,
    received,
    open,
    send: (message) => ws.send(JSON.stringify(message)),
    expect(type, timeoutMs = 3000) {
      const match = (m) => m.type === type;
      const already = received.find(match);
      if (already) return Promise.resolve(already);

      return new Promise((resolve, reject) => {
        const timer = setTimeout(
          () => reject(new Error(`timed out waiting for "${type}"`)), timeoutMs);
        waiters.push({ match, resolve: (m) => { clearTimeout(timer); resolve(m); } });
      });
    },
  };
}

const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};

const alice = connect();
const bob = connect();
await Promise.all([alice.open, bob.open]);

alice.send({ type: 'join', board: 'demo', replica: replicaId(1), have: {} });
bob.send({ type: 'join', board: 'demo', replica: replicaId(2), have: {} });
await Promise.all([alice.expect('welcome'), bob.expect('welcome')]);
console.log('both clients joined');

const now = Date.now();
alice.send({
  type: 'push',
  operations: [
    { op: 'set', at: hlc(now, 0, 1), shape: 's1', prop: 'kind', value: 'rect' },
    { op: 'set', at: hlc(now, 1, 1), shape: 's1', prop: 'x', value: 120.5 },
    { op: 'set', at: hlc(now, 2, 1), shape: 's1', prop: 'fill', value: '#ff0055' },
  ],
});

const ack = await alice.expect('ack');
assert(ack.accepted.length === 3, `expected 3 accepted, got ${ack.accepted.length}`);
console.log('alice acked:', ack.accepted.length, 'operations');

const broadcast = await bob.expect('broadcast');
assert(broadcast.operations.length === 3, 'bob should receive all three operations');
assert(broadcast.operations[1].value === 120.5, 'numeric value should survive the wire');
console.log('bob received broadcast:', broadcast.operations.length, 'operations');

assert(!alice.received.some((m) => m.type === 'broadcast'), 'alice must not receive her own echo');

alice.send({
  type: 'presence',
  state: {
    replica: replicaId(1), displayName: 'Alice', colour: '#f0f',
    cursorX: 10, cursorY: 20, selection: ['s1'],
  },
});

const presence = await bob.expect('peerPresence');
assert(presence.state.displayName === 'Alice', 'presence should carry the display name');
console.log('bob saw presence from', presence.state.displayName);

const board = await fetch('http://localhost:5099/api/boards/demo').then((r) => r.json());
assert(board.shapes.length === 1, `expected 1 shape, got ${board.shapes.length}`);
assert(board.shapes[0].properties.x === 120.5, 'materialised state should have x');
assert(board.shapes[0].properties.fill === '#ff0055', 'materialised state should have fill');
console.log('http read:', JSON.stringify(board.shapes[0]));

bob.send({
  type: 'push',
  operations: [{ op: 'set', at: hlc(now, 9, 1), shape: 's2', prop: 'kind', value: 'ellipse' }],
});

const rejected = await bob.expect('rejected');
console.log('forged push rejected:', rejected.reason);

alice.ws.close();
bob.ws.close();
console.log('\nALL CHECKS PASSED');
