import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { Keypair, Transaction, SystemProgram } from '@solana/web3.js';
import { createNearIntentsRouter } from './router.js';

test('quote, wallet approval, recovery and worker deposits use separate authorization', async t => {
  const payer = Keypair.generate();
  const wallet = payer.publicKey.toBase58();
  const deposit = Keypair.generate().publicKey;
  const workerKey = 'local-test-worker-key-32-characters';
  const rows = new Map();
  const broadcasts = [];
  const deposits = [];
  const env = {
    BAKELAND_NEAR_ENABLED: 'true',
    BAKELAND_NEAR_WORKER_KEY: workerKey,
    BAKELAND_NEAR_WORKERS: JSON.stringify({ farmer: { agentId: 'ag_farmer', accountId: 'farmer.near', originAssets: ['mock-sol-usdc', 'mock-base-usdc'] } })
  };
  const app = express();
  app.use(express.json());
  app.use(createNearIntentsRouter({
    env,
    verify: async header => {
      if (!['Bearer player', 'Bearer other'].includes(header)) throw new Error('Bad token');
      return { wallet, sub: header };
    },
    store: { get: async id => rows.get(id), create: async row => {
      if (!rows.has(row.id)) rows.set(row.id, row);
      return rows.get(row.id);
    } },
    rpc: {
      getGenesisHash: async () => '5eykt4UsFv8P8NJdTREpY1vzqKqZKvdpKuc147dw2N9d',
      getLatestBlockhash: async () => ({ blockhash: Keypair.generate().publicKey.toBase58(), lastValidBlockHeight: 500 }),
      getSignatureStatuses: async () => ({ value: [{ confirmationStatus: 'finalized', err: null }] }),
      sendRawTransaction: async bytes => {
        broadcasts.push(bytes.toString('base64'));
        if (broadcasts.length === 1) throw new Error('Lost RPC response');
      }
    },
    oneClick: async (path, body) => {
      if (path.startsWith('/v0/status?')) return { status: 'SUCCESS' };
      assert.equal(body.refundTo, wallet);
      return { quote: {
        amountIn: body.amount, amountOut: '2000000000000000000000000',
        minAmountOut: '1980000000000000000000000', deadline: body.deadline,
        depositAddress: body.dry ? undefined : deposit.toBase58(), depositMemo: 'test memo'
      } };
    },
    agentApi: async (path, body, key) => {
      if (body) deposits.push({ path, body, key });
      return { agent_id: 'ag_farmer', type: 'deposit', correlation_id: 'op_testing', status: 'PENDING_DEPOSIT' };
    }
  }));
  const server = app.listen(0, '127.0.0.1');
  t.after(() => new Promise(resolve => server.close(resolve)));
  await new Promise(resolve => server.once('listening', resolve));
  async function request(path, body, headers = { Authorization: 'Bearer player' }) {
    const res = await fetch(`http://127.0.0.1:${server.address().port}${path}`, {
      method: body === undefined ? 'GET' : 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    return [res.status, await res.json()];
  }
  const operationId = 'operation-000000001';
  const quote = { operationId, amountIn: '3000000', recipient: 'worker.near' };
  const path = '/quotes/' + operationId;
  assert.equal((await request('/quotes', quote, {}))[0], 401);
  assert.equal((await request('/quotes', quote))[0], 200);
  assert.equal(rows.size, 0);
  assert.equal((await request('/quotes', { ...quote, dry: false }))[0], 200);
  assert.equal((await request('/quotes', { ...quote, dry: false, amountIn: '4000000' }))[0], 409);
  assert.equal((await request(path, undefined, { Authorization: 'Bearer other' }))[0], 404);
  const [code, prepared] = await request(path + '/prepare', {});
  assert.equal(code, 200, JSON.stringify(prepared));
  const tx = Transaction.from(Buffer.from(prepared.transaction, 'base64'));
  const alternate = Transaction.from(Buffer.from(prepared.transaction, 'base64'));
  alternate.add(SystemProgram.transfer({ fromPubkey: payer.publicKey, toPubkey: deposit, lamports: 1 }));
  alternate.sign(payer);
  assert.equal((await request(path + '/submit', {
    preparationId: prepared.preparationId, signedTransaction: alternate.serialize().toString('base64')
  }))[0], 400);
  tx.sign(payer);
  const [submittedCode, submitted] = await request(path + '/submit', {
    preparationId: prepared.preparationId, signedTransaction: tx.serialize().toString('base64')
  });
  assert.equal(submittedCode, 200);
  assert.equal(submitted.status, 'BROADCAST_UNKNOWN');
  assert.equal((await request(path + '/prepare', {}))[0], 409);
  assert.equal((await request(path + '/rebroadcast', {}))[1].status, 'SUBMITTED');
  assert.equal(broadcasts[0], broadcasts[1]);
  assert.equal((await request(path))[1].sourceStatus, 'FINALIZED');

  const workerPath = '/workers/farmer/deposits/delivery-000000001';
  const workerBody = { originAsset: 'mock-base-usdc', amount: '3000000' };
  assert.equal((await request(workerPath, workerBody))[0], 401);
  assert.equal((await request(workerPath, workerBody, { 'x-api-key': workerKey }))[0], 200);
  assert.equal((await request(workerPath, workerBody, { 'x-api-key': workerKey }))[0], 200);
  assert.equal(deposits[0].key, deposits[1].key);
  assert.equal((await request('/workers/farmer/deposits/op_testing', undefined, { 'x-api-key': workerKey }))[0], 200);
});

test('a Hive job is paid with one signed USDC transfer to the treasury and funded on confirmation', async t => {
  const payer = Keypair.generate();
  const wallet = payer.publicKey.toBase58();
  const treasury = Keypair.generate().publicKey.toBase58();
  const rows = new Map();
  const broadcasts = [], calls = [];
  const app = express();
  app.use(express.json());
  app.use(createNearIntentsRouter({
    env: { BAKELAND_NEAR_ENABLED: 'true', BAKELAND_HIVE_TREASURY: treasury },
    verify: async header => { if (header !== 'Bearer player') throw new Error('Bad token'); return { wallet, sub: 'player' }; },
    store: {
      get: async id => rows.get(id),
      create: async row => { if (!rows.has(row.id)) rows.set(row.id, row); return rows.get(row.id); },
      remove: async id => { rows.delete(id); },
      scan: async prefix => [...rows.values()].filter(r => r.id.startsWith(prefix))
    },
    rpc: {
      getGenesisHash: async () => '5eykt4UsFv8P8NJdTREpY1vzqKqZKvdpKuc147dw2N9d',
      getLatestBlockhash: async () => ({ blockhash: Keypair.generate().publicKey.toBase58(), lastValidBlockHeight: 500 }),
      getSignatureStatuses: async () => ({ value: [broadcasts.length ? { confirmationStatus: 'confirmed', err: null } : null] }),
      getBlockHeight: async () => 10,
      sendRawTransaction: async bytes => { broadcasts.push(bytes.toString('base64')); }
    },
    spacetime: { call: async (reducer, args) => { calls.push([reducer, args]); return ''; }, sql: async () => [] }
  }));
  const server = app.listen(0, '127.0.0.1');
  t.after(() => new Promise(resolve => server.close(resolve)));
  await new Promise(resolve => server.once('listening', resolve));
  const request = async (path, body, headers = { Authorization: 'Bearer player' }) => {
    const res = await fetch(`http://127.0.0.1:${server.address().port}${path}`, { method: body === undefined ? 'GET' : 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
    return [res.status, await res.json()];
  };
  const operationId = 'hive-job-test-0001';
  assert.equal((await request('/hive/pay/prepare', { operationId, amountIn: '450000' }, {}))[0], 401);
  assert.equal((await request('/hive/pay/prepare', { operationId: 'not-a-hive-job', amountIn: '450000' }))[0], 400);
  assert.equal((await request('/hive/pay/prepare', { operationId, amountIn: '99000000' }))[0], 400, 'capped at 10 USDC');
  assert.deepEqual((await request('/hive/pay/' + operationId))[1].status, 'UNPAID');
  const [code, prepared] = await request('/hive/pay/prepare', { operationId, amountIn: '450000' });
  assert.equal(code, 200, JSON.stringify(prepared));
  assert.equal(prepared.treasury, treasury);
  const tx = Transaction.from(Buffer.from(prepared.transaction, 'base64'));
  assert.equal(tx.feePayer.toBase58(), wallet);
  const tampered = Transaction.from(Buffer.from(prepared.transaction, 'base64'));
  tampered.add(SystemProgram.transfer({ fromPubkey: payer.publicKey, toPubkey: payer.publicKey, lamports: 1 }));
  tampered.sign(payer);
  assert.equal((await request('/hive/pay/submit', { operationId, preparationId: prepared.preparationId,
    signedTransaction: tampered.serialize().toString('base64') }))[0], 400, 'only the prepared transfer is accepted');
  tx.sign(payer);
  const [submitted, result] = await request('/hive/pay/submit', { operationId, preparationId: prepared.preparationId,
    signedTransaction: tx.serialize().toString('base64') });
  assert.equal(submitted, 200, JSON.stringify(result));
  assert.equal(result.status, 'FUNDED');
  assert.deepEqual(calls, [['confirm_hive_job_funding', [operationId, 'sol:' + result.signature, 450000, wallet]]]);
  assert.equal((await request('/hive/pay/' + operationId))[1].status, 'FUNDED');
  assert.equal((await request('/hive/pay/prepare', { operationId, amountIn: '450000' }))[0], 409, 'a paid job is not paid twice');
});
