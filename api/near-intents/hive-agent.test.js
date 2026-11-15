import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { Wallet } from 'ethers';
import { mountHiveAgent, HIVE_AGENT_POLICY, ownerKeyMessage } from './hive-agent.js';

function fail(status, message) { throw Object.assign(new Error(message), { status }); }

async function harness() {
  const rows = new Map();
  const calls = [];
  const env = { NEAR_INTENTS_AGENT_API_KEY: 'partner-key' };
  const route = handler => async (req, res) => {
    try { res.json(await handler(req)); } catch (e) { res.status(e.status || 502).json({ error: e.message }); }
  };
  const fetchImpl = async (url, init) => {
    calls.push({ url, init });
    const path = new URL(url).pathname;
    const body = path === '/v1/generate-intent'
      ? { correlation_id: 'intent_1', signer: 'studio.near', intent: { standard: 'eip712', payload: { domain: {}, types: {}, primaryType: 'X', message: {} } },
          preview: { summary: 'Terms ... risk notice' }, expires_at: '2026-11-15T00:00:00Z' }
      : path === '/v1/submit-intent' ? { correlation_id: 'intent_1', status: 'PROCESSING' }
      : path === '/v1/status' ? { status: 'SUCCESS', agent_id: 'ag_hive' }
      : { data: [{ agent_id: 'ag_hive', name: 'Bakeland Hive', status: 'ACTIVE', wallet: { near_account_id: 'abc.near' } }] };
    return { ok: true, status: 200, json: async () => body };
  };
  const router = express.Router();
  mountHiveAgent(router, {
    env, route, fail, fetchImpl, agentApiBase: () => 'https://api.demo.agentsonintents.com',
    db: () => ({ get: async id => rows.get(id), create: async row => { if (!rows.has(row.id)) rows.set(row.id, row); return rows.get(row.id); } }),
    requireServiceKey: req => { if (req.get('x-api-key') !== 'service') fail(401, 'Service authentication required'); }
  });
  const app = express(); app.use(express.json()); app.use('/near-intents', router);
  const server = app.listen(0); await new Promise(r => server.once('listening', r));
  const base = `http://127.0.0.1:${server.address().port}/near-intents`;
  const post = (path, body, headers = {}) => fetch(base + path, { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body ?? {}) });
  return { server, base, post, rows, calls };
}

test('a single-use signing link creates the Hive agent with the no-spend policy and keeps the acceptance', async () => {
  const h = await harness();
  try {
    assert.equal((await h.post('/hive/agent/session')).status, 401, 'the link needs the service key');
    const { url } = await (await h.post('/hive/agent/session', {}, { 'x-api-key': 'service' })).json();
    assert.match(url, /\/near-intents\/hive\/agent\/sign#[a-f0-9]{64}$/);
    const t = url.split('#')[1];

    const page = await fetch(h.base + '/hive/agent/sign');
    assert.match(page.headers.get('content-security-policy'), /script-src 'nonce-/);
    assert.match(await page.text(), /Create the Bakeland Hive agent/);

    const owner = Wallet.createRandom(), other = Wallet.createRandom();
    const proof = await owner.signMessage(ownerKeyMessage(t));
    assert.equal((await h.post('/hive/agent/sign/generate', { t: 'f'.repeat(64), address: owner.address, signature: proof })).status, 410);
    assert.equal((await h.post('/hive/agent/sign/generate', { t, address: other.address, signature: proof })).status, 400,
      'the confirmation must come from the claimed wallet');
    assert.equal((await h.post('/hive/agent/sign/generate', { t, address: owner.address, signature: await owner.signMessage('other text') })).status, 400,
      'the confirmation must be the message for this link');
    const generated = await (await h.post('/hive/agent/sign/generate', { t, address: owner.address, signature: proof })).json();
    assert.equal(generated.intent.standard, 'eip712');
    const sent = JSON.parse(h.calls[0].init.body);
    assert.equal(h.calls[0].init.headers['X-API-Key'], 'partner-key');
    assert.deepEqual(sent.owner, { type: 'evm', address: owner.address.toLowerCase(), chain_id: 1,
      public_key: '0x' + owner.signingKey.publicKey.slice(4).toLowerCase() });
    assert.match(sent.owner.public_key, /^0x[0-9a-f]{128}$/);
    assert.deepEqual(sent.policy, HIVE_AGENT_POLICY);
    assert.deepEqual(sent.policy.actions, [], 'the agent can spend nothing at creation');

    const signed = { standard: 'eip712', payload: generated.intent.payload, signature: '0x' + 'ab'.repeat(65) };
    assert.equal((await h.post('/hive/agent/sign/submit', { t, accepted: false, signed_data: signed })).status, 400, 'terms must be accepted');
    assert.equal((await h.post('/hive/agent/sign/submit', { t, accepted: true, signed_data: { ...signed, standard: 'nep413' } })).status, 400);
    const done = await (await h.post('/hive/agent/sign/submit', { t, accepted: true, signed_data: signed })).json();
    assert.deepEqual(done, { status: 'SUCCESS', agent_id: 'ag_hive', failure_code: null });
    const evidence = h.rows.get(`hive-agent-session:${t}:accepted`);
    assert.equal(evidence.owner, owner.address.toLowerCase());
    assert.match(evidence.signedSha256, /^[a-f0-9]{64}$/);

    const list = await (await fetch(h.base + '/hive/agent', { headers: { 'x-api-key': 'service' } })).json();
    assert.equal(list.agents[0].agent_id, 'ag_hive');
  } finally { h.server.close(); }
});
