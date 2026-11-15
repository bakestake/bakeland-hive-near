import { generateKeyPairSync, sign } from 'node:crypto';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import bs58 from 'bs58';
import { Keypair } from '@solana/web3.js';
import { createNearIntentsRouter } from './router.js';
import { digestLinkProof, getLinkedNearAccount } from './wallet-link.js';

test('a verified wallet link maps the primary Solana ID and cannot be claimed by another profile', async t => {
  const wallets = [Keypair.generate(), Keypair.generate()].map(k => k.publicKey.toBase58());
  const rows = new Map();
  const store = {
    get: async id => rows.get(id),
    async create(item) {
      if (!rows.has(item.id)) rows.set(item.id, item);
      return rows.get(item.id);
    },
    async link(challenge, proof, profile, reverse) {
      if (!rows.has(challenge.id) || !rows.has(proof.id) || rows.has(profile.id) || rows.has(reverse.id))
        throw Object.assign(new Error(), { name: 'TransactionCanceledException' });
      rows.delete(challenge.id);
      rows.delete(proof.id);
      rows.set(profile.id, profile);
      rows.set(reverse.id, reverse);
    }
  };
  const app = express();
  app.use(express.json(), createNearIntentsRouter({
    env: { BAKELAND_NEAR_ENABLED: 'true', BAKELAND_NEAR_LINK_PAGE: 'https://api.example/near-intents/social-links/near/connect' },
    store, verify: async header => {
      const index = ['Bearer a', 'Bearer b'].indexOf(header);
      if (index < 0) throw new Error('Unauthorized');
      return { sub: header, wallet: wallets[index] };
    },
    hasFullAccess: async account => account === 'farmer.near'
  }));
  const server = app.listen(0, '127.0.0.1');
  t.after(() => new Promise(resolve => server.close(resolve)));
  await new Promise(resolve => server.once('listening', resolve));
  async function call(path, body, auth) {
    const res = await fetch('http://127.0.0.1:' + server.address().port + '/social-links/near' + path, {
      method: body === undefined ? 'GET' : 'POST',
      headers: { 'Content-Type': 'application/json', ...(auth && { Authorization: 'Bearer ' + auth }) },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    return [res.status, await res.json()];
  }
  const keys = generateKeyPairSync('ed25519');
  const publicKey = 'ed25519:' + bs58.encode(keys.publicKey.export({ format: 'der', type: 'spki' }).subarray(-32));
  async function approve(user) {
    const [status, { ticket, walletUrl }] = await call('/start', {}, user);
    assert.equal(status, 200);
    assert.equal(new URL(walletUrl).hash, '#' + ticket);
    const [, challenge] = await call('/connect/challenge', { ticket });
    const signature = sign(null, digestLinkProof(challenge), keys.privateKey).toString('base64');
    const proof = { ticket, publicKey, signature, accountId: 'farmer.near' };
    assert.equal((await call('/connect/proof', { ...proof, accountId: 'other.near' }))[0], 403);
    assert.equal((await call('/connect/proof', proof))[0], 200);
    return { ticket, nearAccountId: 'farmer.near' };
  }
  assert.equal((await call('/start', {}))[0], 401);
  const approval = await approve('a');
  assert.equal(await getLinkedNearAccount(store, wallets[0]), null);
  assert.equal((await call('/confirm', approval, 'b'))[0], 404);
  assert.deepEqual(await call('/confirm', approval, 'a'), [200, { linked: true, nearAccountId: 'farmer.near' }]);
  assert.deepEqual(await call('', undefined, 'a'), [200, { linked: true, nearAccountId: 'farmer.near' }]);
  assert.equal(await getLinkedNearAccount(store, wallets[0]), 'farmer.near');
  assert.equal((await call('/connect/challenge', { ticket: approval.ticket }))[0], 404);
  assert.equal((await call('/confirm', await approve('b'), 'b'))[0], 409);
});
