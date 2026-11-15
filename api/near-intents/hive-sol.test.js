import test from 'node:test';
import assert from 'node:assert/strict';
import { Keypair, Transaction } from '@solana/web3.js';
import { getAssociatedTokenAddressSync } from '@solana/spl-token';
import { PublicKey } from '@solana/web3.js';
import { createHiveSolana, makeHivePaymentTransaction, paymentRefundReference, AGENT_SOLANA_USDC } from './hive-sol.js';
import { createHiveSettlement } from './hive-settlement.js';

const TREASURY = Keypair.generate().publicKey.toBase58();
const PLAYER = Keypair.generate().publicKey.toBase58();
const SIG = '5'.repeat(88);

function harness({ rows = [], closed = [], signature = { confirmationStatus: 'confirmed', err: null }, height = 10,
  agentStatus = 'SUCCESS', sendResult, fundingError } = {}) {
  const store = new Map();
  const calls = [], sent = [], deposits = [];
  const db = () => ({
    get: async id => store.get(id),
    create: async row => { if (!store.has(row.id)) store.set(row.id, row); return store.get(row.id); },
    remove: async id => { store.delete(id); },
    scan: async prefix => [...store.values()].filter(r => r.id.startsWith(prefix))
  });
  const state = { signature, height };
  const solana = () => ({
    getSignatureStatuses: async () => ({ value: [state.signature] }),
    getBlockHeight: async () => state.height
  });
  const stdb = () => ({
    call: async (reducer, args) => {
      if (reducer === 'confirm_hive_job_funding' && fundingError) throw new Error(fundingError);
      calls.push([reducer, args]); return '';
    },
    sql: async query => {
      if (/hive_job_closure/.test(query)) return closed.map(job_id => ({ job_id, status: 2 }));
      return rows;
    }
  });
  const agent = async (path, body, key) => {
    if (body) { deposits.push({ path, body, key }); return { correlation_id: 'corr_' + key, details: { deposit_address: Keypair.generate().publicKey.toBase58() } }; }
    return { status: agentStatus };
  };
  const escrow = { sendSol: async transfer => { sent.push(transfer); return sendResult ?? { ok: true, signature: 'sig-' + sent.length }; } };
  const env = { BAKELAND_NEAR_HIVE_AGENT_ID: 'agent-1', BAKELAND_HIVE_PAYOUT_MIN_MICROS: '5000000' };
  const sol = createHiveSolana({ env, db, solana, agent, stdb, escrow, log: () => {} });
  return { sol, store, calls, sent, deposits, state, db, env, stdb, agent, escrow };
}

const open = { id: 'hive-pay-open:' + SIG, operationId: 'hive-job-0001', wallet: PLAYER, amountIn: '450000', signature: SIG, lastValidBlockHeight: 100 };

test('the payment is the player\'s USDC to the treasury with the job memo, and the player pays the fee', () => {
  const blockhash = Keypair.generate().publicKey.toBase58();
  const tx = makeHivePaymentTransaction({ wallet: PLAYER, treasury: TREASURY, micros: '450000', operationId: 'hive-job-0001', blockhash, lastValidBlockHeight: 5 });
  assert.equal(tx.feePayer.toBase58(), PLAYER);
  assert.equal(tx.instructions.length, 2, 'no account creation: the player pays no rent');
  const [transfer, memo] = tx.instructions;
  const usdc = new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v');
  assert.equal(transfer.keys[0].pubkey.toBase58(), getAssociatedTokenAddressSync(usdc, new PublicKey(PLAYER)).toBase58());
  assert.equal(transfer.keys[2].pubkey.toBase58(), getAssociatedTokenAddressSync(usdc, new PublicKey(TREASURY), true).toBase58());
  assert.equal(Buffer.from(transfer.data).readBigUInt64LE(1), 450000n);
  assert.equal(memo.data.toString(), 'hive:hive-job-0001');
  assert.ok(Transaction.from(tx.serialize({ requireAllSignatures: false })));
});

test('a confirmed payment funds the job once, with the Solana signature as its reference', async () => {
  const h = harness();
  h.store.set(open.id, open);
  assert.equal(await h.sol.advancePayment(open), 'FUNDED');
  assert.equal(await h.sol.advancePayment(open), 'FUNDED');
  assert.deepEqual(h.calls, [['confirm_hive_job_funding', ['hive-job-0001', 'sol:' + SIG, 450000, PLAYER]]]);
  assert.equal(h.store.has(open.id), false, 'the open payment is done');
  assert.equal(h.store.get('hive-sol-op:' + SIG).wallet, PLAYER);
});

test('an unconfirmed payment waits, and one that failed or expired funds nothing', async () => {
  const h = harness({ signature: null, height: 50 });
  h.store.set(open.id, open);
  assert.equal(await h.sol.advancePayment(open), 'PENDING');
  h.state.height = 200;   // past its last valid block height and never seen: it can no longer land
  assert.equal(await h.sol.advancePayment(open), 'EXPIRED');
  const failed = harness({ signature: { confirmationStatus: 'confirmed', err: { InstructionError: [0, 'Custom'] } } });
  assert.equal(await failed.sol.advancePayment(open), 'FAILED');
  assert.equal(h.calls.length + failed.calls.length, 0);
});

test('a payment its job cannot take is refunded in full to the payer', async () => {
  const h = harness({ fundingError: 'SpacetimeDB /call failed (530): The payer is not this job\'s player.' });
  h.store.set(open.id, open);
  assert.equal(await h.sol.advancePayment(open), 'REFUNDING');
  const outcome = await h.sol.sweep([]);
  assert.deepEqual(h.sent, [{ asset: 'solana:usdc', amount: '450000', to: PLAYER, memo: 'hive refund hive-job-0001', reference: paymentRefundReference(SIG) }]);
  assert.match(paymentRefundReference(SIG), /^hive-sol-refund-p[a-f0-9]{40}$/);
  assert.deepEqual(outcome.at(-1), ['pay:hive-job-0001', 'refunded']);
  await h.sol.sweep([]);
  assert.equal(h.sent.length, 1, 'refunded once');

  const transient = harness({ fundingError: 'SpacetimeDB /call failed (503): unavailable' });
  await assert.rejects(transient.sol.advancePayment(open), /unavailable/);
  assert.equal(transient.store.has('hive-payrefund:' + SIG), false, 'a transient error is retried, not refunded');
});

const payout = (settlement_id, job_id, micros) => ({ settlement_id, job_id, kind: 1, micros, status: 0, funding_reference: 'sol:' + SIG });

test('payouts wait until their job closes, then go to the Hive agent in one Solana USDC transfer', async () => {
  const rows = [payout(1, 7, 300000), payout(2, 7, 300000), payout(3, 8, 100000)];
  const h = harness({ rows });
  h.store.set('hive-sol-op:' + SIG, { id: 'hive-sol-op:' + SIG, wallet: PLAYER, operationId: 'hive-job-0001' });
  await h.sol.sweep(rows);
  assert.equal(h.sent.length, 0, 'open jobs under the minimum wait');

  const closed = harness({ rows, closed: [7] });
  const outcome = await closed.sol.sweep(rows);
  assert.deepEqual(closed.deposits, [{ path: '/v1/agents/agent-1/deposit', body: { origin_asset: AGENT_SOLANA_USDC, amount: '600000' }, key: 'hive-sol-payout-b1' }]);
  assert.equal(closed.sent.length, 1);
  assert.deepEqual([closed.sent[0].amount, closed.sent[0].reference], ['600000', 'hive-sol-payout-b1']);
  assert.deepEqual(closed.calls, [['confirm_hive_settlement', [1, 'agent:corr_hive-sol-payout-b1']], ['confirm_hive_settlement', [2, 'agent:corr_hive-sol-payout-b1']]]);
  assert.ok(outcome.some(o => o[0] === 1 && o[1] === 'settled'));
});

test('a long job sends its payouts once they reach 5 USDC', async () => {
  const rows = [payout(1, 9, 2_500_000), payout(2, 9, 2_500_000), payout(3, 10, 1_000_000)];
  const h = harness({ rows });
  await h.sol.sweep(rows);
  assert.deepEqual(h.deposits.map(d => d.body.amount), ['5000000']);
  assert.deepEqual(h.calls.map(c => c[1][0]), [1, 2]);
});

test('a batch is sent once and settles only when the agent reports the deposit', async () => {
  const rows = [payout(4, 7, 600000)];
  const h = harness({ rows, closed: [7], agentStatus: 'PENDING' });
  assert.deepEqual((await h.sol.sweep(rows)).at(-1), ['hive-sol-payout-b4', 'in-flight']);
  await h.sol.sweep(rows);
  assert.equal(h.sent.length, 1, 'never a second transfer');
  assert.equal(h.deposits.length, 1, 'one agent deposit per batch');
  assert.equal(h.calls.length, 0);
});

test('a refund row sends the micros back to the payer', async () => {
  const rows = [{ settlement_id: 5, job_id: 7, kind: 2, micros: 120000, status: 0, funding_reference: 'sol:' + SIG }];
  const h = harness({ rows });
  h.store.set('hive-sol-op:' + SIG, { id: 'hive-sol-op:' + SIG, wallet: PLAYER, operationId: 'hive-job-0001' });
  await h.sol.sweep(rows);
  assert.deepEqual(h.sent, [{ asset: 'solana:usdc', amount: '120000', to: PLAYER, memo: 'hive refund hive-job-0001', reference: 'hive-sol-refund-5' }]);
  assert.deepEqual(h.calls, [['confirm_hive_settlement', [5, 'sol:sig-1']]]);
});

test('the settlement sweep leaves Solana-funded rows to hive-sol and NEAR-funded rows to the 1Click path', async () => {
  const h = harness();
  const rows = [{ settlement_id: 5, job_id: 7, kind: 2, micros: 120000, status: 0, funding_reference: 'sol:' + SIG },
    { settlement_id: 6, job_id: 8, kind: 2, micros: 1000, status: 0, funding_reference: 'near:hive-old-operation' }];
  h.store.set('hive-sol-op:' + SIG, { id: 'hive-sol-op:' + SIG, wallet: PLAYER, operationId: 'hive-job-0001' });
  const hive = createHiveSettlement({ env: h.env, db: h.db, click: async () => ({}), agent: h.agent, escrow: h.escrow, log: () => {},
    spacetime: { call: h.stdb().call, sql: async () => rows },
    solanaPayments: ({ stdb }) => createHiveSolana({ env: h.env, db: h.db, solana: () => ({}), agent: h.agent, stdb, escrow: h.escrow, log: () => {} }) });
  const outcome = await hive.sweep();
  assert.deepEqual(outcome, [[5, 'settled'], [6, 'no-funding-record']]);
});
