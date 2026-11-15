import test from 'node:test';
import assert from 'node:assert/strict';
import { createHiveSettlement, isHiveOperation, spacetimeHttp } from './hive-settlement.js';

function harness({ escrowSend, escrowFails = false, clickStatus = 'SUCCESS', agentStatus = 'SUCCESS', rows = [], env: extra = {} } = {}) {
  const deposits = [];
  const store = new Map();
  const calls = [];
  const quotes = [];
  const sent = [];
  const env = {
    BAKELAND_NEAR_HIVE_ESCROW: 'hive-escrow.near',
    BAKELAND_NEAR_HIVE_PAYOUT_ACCOUNT: 'hive-earnings.near',
    ...extra
  };
  const hive = createHiveSettlement({
    env,
    db: () => ({
      get: async id => store.get(id),
      create: async row => { if (!store.has(row.id)) store.set(row.id, row); return store.get(row.id); }
    }),
    click: async (path, body) => {
      if (path.startsWith('/v0/status?')) return { status: clickStatus };
      quotes.push(body);
      return { quote: { depositAddress: 'refund-deposit.near', depositMemo: '' } };
    },
    agent: async (path, body, key) => {
      if (body) { deposits.push({ path, body, key }); return { correlation_id: 'op_' + key, details: { deposit_address: 'ab'.repeat(32) } }; }
      return { status: agentStatus };
    },
    spacetime: {
      call: async (reducer, args) => { calls.push([reducer, args]); return ''; },
      sql: async query => { assert.match(query, /hive_settlement WHERE status = 0/); return rows; }
    },
    escrow: escrowSend ? { send: async transfer => {
      sent.push(transfer);
      if (escrowFails) throw new Error('Escrow signer refused the transfer (504): pending');
      return 'tx-' + sent.length;
    } } : undefined,
    log: () => {}
  });
  return { hive, store, calls, quotes, sent, deposits };
}

const record = { operationId: 'hive-0123456789abcdef', wallet: 'AliceWallet', amountIn: '500000',
  amountOut: '1000000000000000000000000', recipient: 'hive-escrow.near' };

test('only Hive operations paid into the escrow fund a job, once', async () => {
  assert.equal(isHiveOperation('hive-0123456789abcdef'), true);
  assert.equal(isHiveOperation('operation-000000001'), false);
  const h = harness();
  await h.hive.onQuoteStatus({ ...record, recipient: 'someone.near' }, 'SUCCESS');
  await h.hive.onQuoteStatus({ ...record, operationId: 'operation-000000001' }, 'SUCCESS');
  await h.hive.onQuoteStatus(record, 'PENDING_DEPOSIT');
  assert.equal(h.calls.length, 0);
  await h.hive.onQuoteStatus(record, 'SUCCESS');
  await h.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(h.calls, [['confirm_hive_job_funding', ['hive-0123456789abcdef', 'near:hive-0123456789abcdef', 500000, 'AliceWallet']]]);
  assert.equal(h.store.get('hive-op:hive-0123456789abcdef').amountOut, record.amountOut);
});

test('a failed or refunded payment closes the job', async () => {
  const h = harness();
  await h.hive.onQuoteStatus(record, 'REFUNDED');
  assert.deepEqual(h.calls, [['reject_hive_job_funding', ['hive-0123456789abcdef', 'near:hive-0123456789abcdef:refunded']]]);
});

test('payouts go in proportion to the one Hive payout account and settle with the transfer hash', async () => {
  const rows = [{ settlement_id: 7, kind: 1, droid_id: 'HX-07', funding_reference: 'near:hive-0123456789abcdef', micros: 100000 }];
  const waiting = harness({ rows });
  await waiting.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await waiting.hive.sweep(), [[7, 'awaiting-escrow-signer']]);

  const done = harness({ rows, escrowSend: true });
  await done.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await done.hive.sweep(), [[7, 'settled']]);
  assert.deepEqual(done.sent, [{ asset: 'nep141:wrap.near', amount: '200000000000000000000000',   // 100000 / 500000 of the wNEAR
    to: 'hive-earnings.near', memo: '', reference: 'hive-payout-7' }]);
  assert.deepEqual(done.calls.at(-1), ['confirm_hive_settlement', [7, 'near-tx:tx-1']]);
  assert.deepEqual(await done.hive.sweep(), [[7, 'settled']]);   // the row is still pending in this fake: confirm again, never resend
  assert.equal(done.sent.length, 1, 'the escrow sends each settlement once');

  const own = harness({ rows, escrowSend: true, env: { BAKELAND_NEAR_WORKERS: JSON.stringify({ 'HX-07': { accountId: 'tiller.near' } }) } });
  await own.hive.onQuoteStatus(record, 'SUCCESS');
  await own.hive.sweep();
  assert.equal(own.sent[0].to, 'tiller.near', 'a droid with its own account is paid there');

  const none = harness({ rows, escrowSend: true, env: { BAKELAND_NEAR_HIVE_PAYOUT_ACCOUNT: '' } });
  await none.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await none.hive.sweep(), [[7, 'error']]);
  assert.equal(none.sent.length, 0);
});

test('with a Hive agent, payouts go to an agent deposit address and settle on SUCCESS from the Agent API', async () => {
  const rows = [{ settlement_id: 12, kind: 1, droid_id: 'HX-07', funding_reference: 'near:hive-0123456789abcdef', micros: 100000 }];
  const waiting = harness({ rows, escrowSend: true, agentStatus: 'PENDING_DEPOSIT', env: { BAKELAND_NEAR_HIVE_AGENT_ID: 'agent-7c4a' } });
  await waiting.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await waiting.hive.sweep(), [[12, 'in-flight']]);
  assert.deepEqual(await waiting.hive.sweep(), [[12, 'in-flight']]);
  assert.equal(waiting.deposits.length, 1, 'one deposit address per settlement');
  assert.deepEqual(waiting.deposits[0], { path: '/v1/agents/agent-7c4a/deposit', key: 'hive-payout-12',
    body: { origin_asset: 'nep141:wrap.near', amount: '200000000000000000000000' } });
  assert.equal(waiting.sent.length, 1, 'sent once');
  assert.equal(waiting.sent[0].to, 'ab'.repeat(32));
  assert.equal(waiting.calls.filter(c => c[0] === 'confirm_hive_settlement').length, 0, 'not settled before the agent credits it');

  const done = harness({ rows, escrowSend: true, env: { BAKELAND_NEAR_HIVE_AGENT_ID: 'agent-7c4a' } });
  await done.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await done.hive.sweep(), [[12, 'settled']]);
  assert.deepEqual(done.calls.at(-1), ['confirm_hive_settlement', [12, 'agent:op_hive-payout-12']]);
});

test('a send with an unknown outcome is not confirmed and is retried through the signer', async () => {
  const rows = [{ settlement_id: 8, kind: 1, droid_id: 'HX-07', funding_reference: 'near:hive-0123456789abcdef', micros: 100000 }];
  const h = harness({ rows, escrowSend: true, escrowFails: true });
  await h.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await h.hive.sweep(), [[8, 'error']]);
  assert.deepEqual(await h.hive.sweep(), [[8, 'error']]);
  assert.equal(h.sent.length, 2, 'retried; the signer re-sends the same signed bytes for the same reference');
  assert.equal(h.calls.filter(c => c[0] === 'confirm_hive_settlement').length, 0);
});

test('refunds swap back to the payer and need the funding record', async () => {
  const rows = [{ settlement_id: 9, kind: 2, droid_id: 'HX-07', funding_reference: 'near:hive-0123456789abcdef', micros: 250000 }];
  const h = harness({ rows, escrowSend: true });
  assert.deepEqual(await h.hive.sweep(), [[9, 'no-funding-record']]);
  await h.hive.onQuoteStatus(record, 'SUCCESS');
  assert.deepEqual(await h.hive.sweep(), [[9, 'settled']]);
  assert.equal(h.quotes[0].recipient, 'AliceWallet');
  assert.equal(h.quotes[0].amount, '500000000000000000000000');
  assert.equal(h.quotes[0].originAsset, 'nep141:wrap.near');
  assert.deepEqual(h.calls.at(-1), ['confirm_hive_settlement', [9, 'near-refund:refund-deposit.near']]);
});

test('SpacetimeDB SQL rows map to named columns', async () => {
  const fetched = [];
  const stdb = spacetimeHttp({ BAKELAND_SPACETIME_DB: 'bakeland-test', BAKELAND_SPACETIME_WORKER_TOKEN: 'token' }, async (url, init) => {
    fetched.push([url, init.body]);
    return { ok: true, text: async () => JSON.stringify([{ schema: { elements: [{ name: { some: 'settlement_id' } }, { name: 'kind' }] }, rows: [[3, 1]] }]) };
  });
  assert.deepEqual(await stdb.sql('SELECT 1'), [{ settlement_id: 3, kind: 1 }]);
  assert.equal(fetched[0][0], 'https://maincloud.spacetimedb.com/v1/database/bakeland-test/sql');
  await assert.rejects(spacetimeHttp({}).call('X', []), /not configured/);
});
