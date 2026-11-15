import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { createTransferEngine } from './transfer-engine.js';
import { base58Encode, base58Decode, encodeTransaction } from './near-tx.js';
import { checkTransfer, executionFailure } from './transfer-rules.js';

const NEAR = 10n ** 24n;
const ESCROW = 'escrow.bakeland.near';

/** The Durable Object SQL API (exec(...).toArray()) over node:sqlite. */
function sqlStore() {
  const db = new DatabaseSync(':memory:');
  return { exec(query, ...args) {
    const statement = db.prepare(query);
    const rows = /^\s*select/i.test(query) ? statement.all(...args) : (statement.run(...args), []);
    return { toArray: () => rows.map(row => ({ ...row })) };
  } };
}

async function escrowKey() {
  const pair = await crypto.subtle.generateKey({ name: 'Ed25519' }, true, ['sign', 'verify']);
  const jwk = await crypto.subtle.exportKey('jwk', pair.privateKey);
  const seed = Buffer.from(jwk.d, 'base64url'), pub = Buffer.from(jwk.x, 'base64url');
  return { secret: 'ed25519:' + base58Encode(Uint8Array.from([...seed, ...pub])), pub, verifyKey: pair.publicKey };
}

/** A fake NEAR RPC: access key, wrap.near storage_balance_of, and send_tx with a scripted outcome per call. */
function fakeRpc({ registered = true, outcomes = [], native = '100000000000000000000000000', wrapped = '0' } = {}) {
  const calls = [];
  let nonce = 41;   // advances when a transaction executes, as on chain
  const rpc = async (method, params) => {
    calls.push({ method, params });
    if (method === 'query' && params.request_type === 'view_access_key') {
      if (params.account_id !== ESCROW) throw new Error('NEAR RPC query: UNKNOWN_ACCESS_KEY');
      return { nonce, block_hash: base58Encode(new Uint8Array(32).fill(9)), permission: 'FullAccess' };
    }
    if (method === 'query' && params.request_type === 'view_account') return { amount: native };
    if (method === 'query' && params.request_type === 'call_function' && params.method_name === 'ft_balance_of') {
      return { result: [...new TextEncoder().encode(JSON.stringify(wrapped))] };
    }
    if (method === 'query' && params.request_type === 'call_function') {
      return { result: [...new TextEncoder().encode(registered ? '{"total":"1250000000000000000000","available":"0"}' : 'null')] };
    }
    if (method === 'send_tx') {
      const next = outcomes.shift() ?? 'ok';
      if (next === 'timeout') throw new Error('The operation was aborted due to timeout');
      nonce++;
      if (next === 'fail') return { status: { Failure: { ActionError: { kind: 'FunctionCallError' } } }, receipts_outcome: [] };
      return { status: { SuccessValue: '' }, receipts_outcome: [{ outcome: { status: { SuccessValue: '' } } }] };
    }
    throw new Error('unexpected ' + method);
  };
  return { rpc, calls, sends: () => calls.filter(c => c.method === 'send_tx') };
}

async function engine(options = {}, env = {}) {
  const key = await escrowKey();
  const near = fakeRpc(options);
  let clock = 1_000_000;
  const e = createTransferEngine({ sql: sqlStore(), rpc: near.rpc, now: () => clock, env: { ESCROW_ACCOUNT_ID: ESCROW,
    ESCROW_SECRET_KEY: key.secret, MAX_TRANSFER_YOCTO: String(5n * NEAR), DAILY_MAX_YOCTO: String(8n * NEAR), ...env } });
  return { e, near, key, advance: ms => { clock += ms; } };
}

const transfer = (id, amount = 2n * NEAR, extra = {}) => ({ asset: 'nep141:wrap.near', amount: String(amount),
  to: 'deposit-abc.near', memo: 'm', reference: `hive-payout-${id}`, ...extra });

test('refuses anything but a capped wNEAR transfer with a Hive reference', () => {
  const env = { ESCROW_ACCOUNT_ID: ESCROW, MAX_TRANSFER_YOCTO: String(5n * NEAR) };
  assert.throws(() => checkTransfer(transfer(1, NEAR, { asset: 'nep141:usdc.near' }), env), /Only nep141:wrap.near/);
  assert.throws(() => checkTransfer(transfer(1, 6n * NEAR), env), /per-transfer limit/);
  assert.throws(() => checkTransfer(transfer(1, NEAR, { to: 'Bad Account!' }), env), /Invalid NEAR account/);
  assert.throws(() => checkTransfer(transfer(1, NEAR, { to: ESCROW }), env), /cannot pay itself/);
  assert.throws(() => checkTransfer(transfer(1, NEAR, { reference: 'other-1' }), env), /Invalid reference/);
  assert.throws(() => checkTransfer(transfer(1, NEAR, { amount: '-5' }), env), /positive integer/);
  assert.throws(() => checkTransfer(transfer(1, NEAR), { ...env, MAX_TRANSFER_YOCTO: '' }), /per-transfer limit/);
  assert.equal(checkTransfer(transfer(1, NEAR), env).amount, NEAR);
});

test('reads failures anywhere in the outcome', () => {
  assert.equal(executionFailure({ status: { SuccessValue: '' }, receipts_outcome: [] }), null);
  assert.match(executionFailure({ status: { SuccessValue: '' }, receipts_outcome: [{ outcome: { status: { Failure: { x: 1 } } } }] }), /x/);
  assert.match(executionFailure({ status: { Failure: { y: 1 } } }), /y/);
  assert.equal(executionFailure({}), 'Unknown outcome');
});

test('sends once per reference and signs a valid wrap.near transaction', async () => {
  const { e, near, key } = await engine();
  const first = await e.send(transfer(1));
  assert.equal(first.ok, true);
  assert.equal(near.sends().length, 1);
  const signed = Buffer.from(near.sends()[0].params.signed_tx_base64, 'base64');
  const body = signed.subarray(0, signed.length - 65);
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', body));
  assert.equal(signed[signed.length - 65], 0);
  assert.ok(await crypto.subtle.verify('Ed25519', key.verifyKey, signed.subarray(signed.length - 64), digest), 'signature verifies');
  assert.equal(first.tx, base58Encode(digest));
  const text = body.toString('latin1');
  assert.ok(text.includes(ESCROW) && text.includes('wrap.near') && text.includes('ft_transfer') && !text.includes('storage_deposit'));
  // nonce = access key nonce + 1
  const expected = encodeTransaction({ signerId: ESCROW, publicKey: key.pub, nonce: 42n, receiverId: 'wrap.near',
    blockHash: new Uint8Array(32).fill(9), actions: [] });
  assert.deepEqual([...body.subarray(0, expected.length - 4)], [...expected.subarray(0, expected.length - 4)]);

  const again = await e.send(transfer(1));
  assert.deepEqual([again.ok, again.repeated, again.tx], [true, true, first.tx]);
  assert.equal(near.sends().length, 1, 'a finished reference is never sent again');
  const changed = await e.send(transfer(1, 3n * NEAR));
  assert.equal(changed.refused, true);
});

test('wraps native NEAR for the shortfall in the same transaction, keeping a reserve', async () => {
  const { e, near } = await engine({ wrapped: String(NEAR / 2n) });
  assert.equal((await e.send(transfer(30, 2n * NEAR))).ok, true);
  const text = Buffer.from(near.sends()[0].params.signed_tx_base64, 'base64').toString('latin1');
  assert.ok(text.indexOf('near_deposit') >= 0 && text.indexOf('near_deposit') < text.indexOf('ft_transfer'), 'wrap before transfer');
  const { e: covered, near: quiet } = await engine({ wrapped: String(3n * NEAR) });
  await covered.send(transfer(31, 2n * NEAR));
  assert.ok(!Buffer.from(quiet.sends()[0].params.signed_tx_base64, 'base64').toString('latin1').includes('near_deposit'), 'no wrap when wNEAR covers it');
  const { e: poor, near: none } = await engine({ native: String(NEAR / 10n + 5n), wrapped: '0' });
  const refused = await poor.send(transfer(32, 1000n));
  assert.equal(refused.refused, true);
  assert.match(refused.error, /not hold enough NEAR/);
  assert.equal(none.sends().length, 0);
});

test('registers wrap.near storage for a new receiver in the same transaction', async () => {
  const { e, near } = await engine({ registered: false });
  assert.equal((await e.send(transfer(2))).ok, true);
  const text = Buffer.from(near.sends()[0].params.signed_tx_base64, 'base64').toString('latin1');
  assert.ok(text.indexOf('storage_deposit') >= 0 && text.indexOf('storage_deposit') < text.indexOf('ft_transfer'));
});

test('an unknown outcome is retried with the same signed bytes, and blocks new transfers until it settles', async () => {
  const { e, near } = await engine({ outcomes: ['timeout', 'timeout', 'ok', 'ok'] });
  const lost = await e.send(transfer(3));
  assert.deepEqual([lost.ok, lost.pending], [false, true]);
  const blocked = await e.send(transfer(4));
  assert.equal(blocked.pending, true);
  assert.match(blocked.error, /hive-payout-3 is still unconfirmed/);
  const [a, b] = near.sends();
  assert.equal(a.params.signed_tx_base64, b.params.signed_tx_base64, 'the retry re-sends the same transaction');
  const next = await e.send(transfer(4));            // the earlier one confirms first, then this one is signed and sent
  assert.equal(next.ok, true);
  assert.equal(near.sends()[2].params.signed_tx_base64, a.params.signed_tx_base64);
  assert.notEqual(near.sends()[3].params.signed_tx_base64, a.params.signed_tx_base64);
  assert.equal((await e.send(transfer(3))).repeated, true);
});

test('an on-chain failure is recorded and not re-sent', async () => {
  const { e, near } = await engine({ outcomes: ['fail'] });
  const failed = await e.send(transfer(5));
  assert.equal(failed.ok, false);
  assert.match(failed.error, /failed on chain/);
  const again = await e.send(transfer(5));
  assert.equal(again.ok, false);
  assert.equal(near.sends().length, 1);
  assert.equal(e.status().recent[0].status, 'failed');
});

test('enforces the rolling daily limit, and failed transfers do not count', async () => {
  const { e, advance } = await engine({ outcomes: ['ok', 'fail', 'ok', 'ok', 'ok'] });
  assert.equal((await e.send(transfer(10, 4n * NEAR))).ok, true);
  assert.equal((await e.send(transfer(11, 4n * NEAR))).ok, false);           // failed on chain: not counted
  assert.equal((await e.send(transfer(12, 4n * NEAR))).ok, true);            // 8 of 8 NEAR
  assert.match((await e.send(transfer(13, 1n))).error, /daily limit/);
  advance(24 * 60 * 60 * 1000 + 1);
  assert.equal((await e.send(transfer(13, NEAR))).ok, true);
  assert.equal(e.status().dayTotalYocto, String(NEAR));
});

test('the self-check confirms the key belongs to the escrow account without exposing it', async () => {
  const { e, key } = await engine();
  const good = await e.check();
  assert.equal(good.ok, true);
  assert.equal(good.fullAccess, true);
  assert.equal(good.publicKey, 'ed25519:' + base58Encode(key.pub));
  assert.equal(good.nearBalanceYocto, '100000000000000000000000000');
  assert.ok(!JSON.stringify(good).includes(key.secret.slice(8)), 'never returns the secret');
  const { e: other } = await engine({}, { ESCROW_ACCOUNT_ID: 'someone-else.near' });
  const wrong = await other.check();
  assert.equal(wrong.ok, false);
  assert.match(wrong.problem, /not an access key of someone-else.near/);
});

test('registers the escrow with wrap.near once, and not when it already is', async () => {
  const { e, near } = await engine({ registered: false });
  const done = await e.registerEscrow();
  assert.equal(done.ok, true);
  assert.equal(near.sends().length, 1);
  const text = Buffer.from(near.sends()[0].params.signed_tx_base64, 'base64').toString('latin1');
  assert.ok(text.includes('storage_deposit') && text.includes(`"account_id":"${ESCROW}"`) && !text.includes('ft_transfer'));
  const { e: registered, near: quiet } = await engine({ registered: true });
  assert.deepEqual(await registered.registerEscrow(), { ok: true, alreadyRegistered: true });
  assert.equal(quiet.sends().length, 0);
});

test('an operator can clear a failed transfer so it is signed again, but never a sent one', async () => {
  const { e, near } = await engine({ outcomes: ['fail', 'ok'] });
  assert.equal((await e.send(transfer(40))).ok, false);
  assert.deepEqual(e.clearFailed('hive-payout-40'), { reference: 'hive-payout-40', cleared: true });
  assert.equal((await e.send(transfer(40))).ok, true);
  assert.equal(near.sends().length, 2);
  assert.deepEqual(e.clearFailed('hive-payout-40'), { reference: 'hive-payout-40', cleared: false, status: 'sent' });
  assert.equal((await e.send(transfer(40))).repeated, true);
  assert.deepEqual(e.clearFailed('hive-payout-41'), { reference: 'hive-payout-41', cleared: false });
});

test('refuses a key whose public half does not belong to it', async () => {
  const a = await escrowKey(), b = await escrowKey();
  const seed = base58Decode(a.secret.slice(8)).slice(0, 32);
  const mismatched = 'ed25519:' + base58Encode(Uint8Array.from([...seed, ...b.pub]));
  const near = fakeRpc();
  const e = createTransferEngine({ sql: sqlStore(), rpc: near.rpc, env: { ESCROW_ACCOUNT_ID: ESCROW, ESCROW_SECRET_KEY: mismatched,
    MAX_TRANSFER_YOCTO: String(5n * NEAR), DAILY_MAX_YOCTO: String(8n * NEAR) } });
  await assert.rejects(e.send(transfer(20)), /does not match/);
  assert.equal(near.sends().length, 0);
});
