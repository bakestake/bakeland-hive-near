// bakeland-hive-signer (14 Nov 2026): the only place the Hive Link escrow key lives. bakeland-api's settlement sweep
// (near-intents/hive-settlement.js) asks it, over a service binding, to move wrapped NEAR out of the escrow account:
// a payout into a droid's NEAR Intents agent deposit address, or a refund into a 1Click deposit address.
//
// It has no public URL (workers_dev off, no routes). Every transfer runs in one Durable Object, one at a time, and
// (transfer-engine.js):
//   - only sends wrap.near, at most MAX_TRANSFER_YOCTO per transfer and DAILY_MAX_YOCTO per rolling 24 h;
//   - records the signed transaction under the caller's reference before broadcasting it, so a retry re-sends the
//     same bytes (NEAR executes a transaction once) and a finished reference returns its first hash;
//   - registers the receiver's wrap.near storage when it has none, in the same transaction as the ft_transfer.
// Since 15 Nov 2026 it also holds the lootbox treasury key (TREASURY_SOLANA_SECRET_KEY) and sends Solana USDC from it
// (solana-engine.js: /sol-transfer, /sol-status, /sol-check, /sol-clear-failed) under its own USDC limits.
import { DurableObject } from 'cloudflare:workers';
import { createTransferEngine, nearRpc } from './transfer-engine.js';
import { createSolanaEngine, solanaRpc } from './solana-engine.js';

export class HiveSigner extends DurableObject {
  #engine;
  #solana;
  #solanaQueue = Promise.resolve();

  constructor(ctx, env) {
    super(ctx, env);
    this.#engine = createTransferEngine({ sql: ctx.storage.sql, env,
      rpc: nearRpc(env.NEAR_RPC_URL || 'https://rpc.mainnet.fastnear.com') });
    this.#solana = createSolanaEngine({ sql: ctx.storage.sql, env,
      rpc: solanaRpc(env.SOLANA_RPC_URL || 'https://api.mainnet-beta.solana.com') });
  }

  /** USDC from the lootbox treasury (solana-engine.js), one transfer at a time. */
  sendSolana(transfer) {
    const run = this.#solanaQueue.then(() => this.#solana.send(transfer));
    this.#solanaQueue = run.catch(() => {});
    return run;
  }

  solanaStatus() { return this.#solana.status(); }

  solanaCheck() { return this.#solana.check(); }

  solanaClearFailed(reference) { return this.#solana.clearFailed(reference); }

  send(transfer) { return this.#engine.send(transfer); }

  status() { return this.#engine.status(); }

  check() { return this.#engine.check(); }

  registerEscrow() { return this.#engine.registerEscrow(); }

  clearFailed(reference) { return this.#engine.clearFailed(reference); }
}

export default {
  // Reached only through bakeland-api's HIVE_SIGNER service binding: there is no route or workers.dev URL.
  async fetch(request, env) {
    const url = new URL(request.url);
    const signer = env.HIVE_SIGNER_DO.get(env.HIVE_SIGNER_DO.idFromName('escrow'));
    if (request.method === 'POST' && url.pathname === '/transfer') {
      const transfer = await request.json().catch(() => null);
      const result = await signer.send(transfer);
      const status = result.ok ? 200 : result.refused ? 422 : result.pending ? 504 : 502;
      return Response.json(result, { status });
    }
    if (request.method === 'POST' && url.pathname === '/sol-transfer') {
      const result = await signer.sendSolana(await request.json().catch(() => null));
      const status = result.ok ? 200 : result.refused ? 422 : result.pending ? 504 : 502;
      return Response.json(result, { status });
    }
    if (request.method === 'GET' && url.pathname === '/sol-status') return Response.json(await signer.solanaStatus());
    if (request.method === 'GET' && url.pathname === '/sol-check') return Response.json(await signer.solanaCheck());
    if (request.method === 'POST' && url.pathname === '/sol-clear-failed') {
      const body = await request.json().catch(() => ({}));
      if (typeof body?.reference !== 'string' || !/^hive-sol-(payout|refund)-[a-zA-Z0-9_-]{1,60}$/.test(body.reference)) {
        return Response.json({ cleared: false, error: 'Invalid reference' }, { status: 400 });
      }
      return Response.json(await signer.solanaClearFailed(body.reference));
    }
    if (request.method === 'GET' && url.pathname === '/status') return Response.json(await signer.status());
    if (request.method === 'GET' && url.pathname === '/check') return Response.json(await signer.check());
    if (request.method === 'POST' && url.pathname === '/clear-failed') {
      const body = await request.json().catch(() => ({}));
      if (typeof body?.reference !== 'string' || !/^hive-(payout|refund)-\d{1,20}$/.test(body.reference)) {
        return Response.json({ cleared: false, error: 'Invalid reference' }, { status: 400 });
      }
      return Response.json(await signer.clearFailed(body.reference));
    }
    if (request.method === 'POST' && url.pathname === '/register-escrow') {
      const result = await signer.registerEscrow();
      return Response.json(result, { status: result.ok ? 200 : result.pending ? 504 : 502 });
    }
    return new Response('Not found', { status: 404 });
  }
};
