// Hive Link escrow worker (14 Nov 2026). The SpacetimeDB module (bakeland-spacetime HiveJobs.cs) is the order book:
// it holds the job, the player's seeds, the droid's verified deliveries and the escrow's to-do list (HiveSettlement).
// This worker is the P2P market's settlement authority for those jobs (the same authority JWT issuers as
// ConfirmMarketSettlement). It:
//   1. confirms a job's funding when its NEAR Intents operation settles into the Hive escrow account
//      (ConfirmHiveJobFunding), or reports a failed or refunded payment (RejectHiveJobFunding);
//   2. works the pending HiveSettlement rows: a payout prepares a deposit into the droid's NEAR Intents agent for that
//      delivery's share, a refund prepares a 1Click swap back to the player's Solana USDC; the escrow signer sends the
//      wrapped NEAR, and the row is confirmed (ConfirmHiveSettlement) only once the provider reports SUCCESS.
//
// Shares are proportional: a settlement of M micros from a job that was paid A micros of USDC moves
// amountOut x M / A of the wrapped NEAR the escrow received for that operation.
//
// Moving funds out of the escrow needs its key, which this service does not hold: pass `escrow.send` (a signer the
// studio provisions). Without it the worker prepares each transfer, records it and leaves the row pending.

const WNEAR = 'nep141:wrap.near';
const SOLANA_USDC_ASSET = 'nep141:sol-5ce3bf3a31af18be40ba30f721101b4341690186.omft.near';
const PENDING = 0;
const PAYOUT = 1;
const REFUND = 2;
const HIVE_OPERATION = /^hive-[a-zA-Z0-9_-]{8,90}$/;

export function isHiveOperation(operationId) {
  return typeof operationId === 'string' && HIVE_OPERATION.test(operationId);
}

/** SpacetimeDB HTTP API with the worker's authority token. */
export function spacetimeHttp(env, fetchImpl = fetch) {
  const base = (env.BAKELAND_SPACETIME_URL || 'https://maincloud.spacetimedb.com').replace(/\/+$/, '');
  const database = env.BAKELAND_SPACETIME_DB;
  const token = env.BAKELAND_SPACETIME_WORKER_TOKEN;
  async function post(path, body, contentType) {
    if (!database || !token) throw new Error('SpacetimeDB worker access is not configured');
    const response = await fetchImpl(`${base}/v1/database/${encodeURIComponent(database)}${path}`, {
      method: 'POST', redirect: 'error', signal: AbortSignal.timeout(10_000),
      headers: { Authorization: 'Bearer ' + token, 'Content-Type': contentType }, body
    });
    const text = await response.text();
    if (!response.ok) throw new Error(`SpacetimeDB ${path} failed (${response.status}): ${text.slice(0, 200)}`);
    return text;
  }
  return {
    call: (reducer, args) => post('/call/' + encodeURIComponent(reducer), JSON.stringify(args), 'application/json'),
    async sql(query) {
      const results = JSON.parse(await post('/sql', query, 'text/plain'));
      const out = [];
      for (const result of results) {
        const names = result.schema.elements.map(e => (typeof e.name === 'string' ? e.name : e.name?.some));
        for (const row of result.rows) out.push(Object.fromEntries(names.map((name, i) => [name, row[i]])));
      }
      return out;
    }
  };
}

/**
 * @param {object} deps
 * @param {object} deps.env
 * @param {() => {get: Function, create: Function}} deps.db  the router's DynamoDB store
 * @param {(path: string, body?: object) => Promise<object>} deps.click  1Click API
 * @param {(path: string, body?: object, key?: string) => Promise<object>} [deps.agent]  NEAR Intents Agent API
 * @param {{call: Function, sql: Function}} [deps.spacetime]
 * @param {{send?: (transfer: object) => Promise<string>}} [deps.escrow]  the escrow signer; resolves with the hash of a
 *   transaction that finished successfully on NEAR, and throws otherwise
 * @param {(message: string) => void} [deps.log]
 */
export function createHiveSettlement({ env, db, click, agent, spacetime, escrow, solanaPayments, log = console.warn }) {
  const stdb = () => spacetime ??= spacetimeHttp(env);
  // Jobs paid by direct Solana USDC (funding_reference "sol:...", 15 Nov 2026) are worked by hive-sol.js.
  const sol = typeof solanaPayments === 'function' ? solanaPayments({ stdb }) : null;
  const escrowAccount = () => env.BAKELAND_NEAR_HIVE_ESCROW;
  // Droid earnings go into the Hive agent (BAKELAND_NEAR_HIVE_AGENT_ID, 15 Nov 2026): each payout gets one of the
  // agent's deposit addresses and settles when the Agent API reports the deposit SUCCESS. Without an agent they go to
  // one plain NEAR account; a droid listed in BAKELAND_NEAR_WORKERS with its own accountId is paid there instead.
  const hiveAgent = () => env.BAKELAND_NEAR_HIVE_AGENT_ID || '';
  const payoutAccount = droidId => JSON.parse(env.BAKELAND_NEAR_WORKERS || '{}')[droidId]?.accountId
    || env.BAKELAND_NEAR_HIVE_PAYOUT_ACCOUNT;

  /** Called with every status read of a quote (GET /quotes/:id). Never throws: a failure retries on the next read. */
  async function onQuoteStatus(record, status) {
    if (!isHiveOperation(record?.operationId)) return;
    if (!escrowAccount() || record.recipient !== escrowAccount()) return;   // not paid into the escrow: no funding
    const marker = `hive-funding:${record.operationId}`;
    try {
      if (await db().get(marker)) return;
      if (status === 'SUCCESS') {
        // Keep what payouts and refunds need, keyed by the operation (the module only knows the operation id).
        await db().create({ id: `hive-op:${record.operationId}`, operationId: record.operationId, wallet: record.wallet,
          amountIn: record.amountIn, amountOut: record.amountOut, recipient: record.recipient });
        await stdb().call('confirm_hive_job_funding', [record.operationId, 'near:' + record.operationId,
          Number(record.amountIn), record.wallet]);
        await db().create({ id: marker, status });
      } else if (status === 'REFUNDED' || status === 'FAILED') {
        await stdb().call('reject_hive_job_funding', [record.operationId, `near:${record.operationId}:${status.toLowerCase()}`]);
        await db().create({ id: marker, status });
      }
    } catch (error) {
      log(`[hive] funding ${record.operationId}: ${error.message}`);
    }
  }

  function share(op, micros) {
    const paid = BigInt(op.amountIn);
    if (paid <= 0n) throw new Error('Funding record has no amount');
    return (BigInt(op.amountOut) * BigInt(micros) / paid).toString();
  }

  async function prepare(row, op, amount) {
    const key = `hive-settle:${row.settlement_id}`;
    const existing = await db().get(key);
    if (existing) return existing;
    let prepared;
    if (Number(row.kind) === PAYOUT && hiveAgent()) {
      if (!agent) throw new Error('The Agent API is not available');
      const result = await agent('/v1/agents/' + encodeURIComponent(hiveAgent()) + '/deposit',
        { origin_asset: WNEAR, amount }, `hive-payout-${row.settlement_id}`);
      const depositAddress = result?.details?.deposit_address;
      if (!depositAddress || !result.correlation_id) throw new Error('The agent deposit has no address');
      prepared = { id: key, kind: 'payout', amount, depositAddress, depositMemo: result.details?.memo ?? '',
        correlationId: result.correlation_id, agentId: hiveAgent() };
    } else if (Number(row.kind) === PAYOUT) {
      const to = payoutAccount(row.droid_id);
      if (!to) throw new Error(`No payout account for droid ${row.droid_id}: set BAKELAND_NEAR_HIVE_PAYOUT_ACCOUNT`);
      prepared = { id: key, kind: 'payout', amount, depositAddress: to, depositMemo: '' };
    } else {
      // The player's payment reached the escrow as on-chain wrap.near (recipientType DESTINATION_CHAIN in router.js),
      // so the refund is an on-chain deposit too: the signer ft_transfers wNEAR to the quote's deposit address, and a
      // failed swap returns it to the escrow account on NEAR.
      const { quote } = await click('/v0/quote', {
        dry: false, swapType: 'EXACT_INPUT', slippageTolerance: 100, originAsset: WNEAR,
        destinationAsset: SOLANA_USDC_ASSET, amount, depositType: 'ORIGIN_CHAIN', recipient: op.wallet,
        recipientType: 'DESTINATION_CHAIN', refundTo: escrowAccount(), refundType: 'ORIGIN_CHAIN',
        deadline: new Date(Date.now() + 30 * 60_000).toISOString()
      });
      if (!quote?.depositAddress) throw new Error('Refund quote has no deposit address');
      prepared = { id: key, kind: 'refund', amount, depositAddress: quote.depositAddress, depositMemo: quote.depositMemo ?? '' };
    }
    return db().create(prepared);
  }

  async function settled(prepared, sent) {
    // An agent payout settles when the Agent API credits the deposit; a plain one when the signer's transfer succeeded.
    if (prepared.kind === 'payout' && prepared.correlationId) {
      const result = await agent('/v1/status?' + new URLSearchParams({ correlation_id: prepared.correlationId }));
      return result?.status === 'SUCCESS' ? 'agent:' + prepared.correlationId : null;
    }
    if (prepared.kind === 'payout') return sent?.tx ? 'near-tx:' + sent.tx : null;
    const query = new URLSearchParams({ depositAddress: prepared.depositAddress });
    if (prepared.depositMemo) query.set('memo', prepared.depositMemo);
    const result = await click('/v0/status?' + query);
    return result.status === 'SUCCESS' ? 'near-refund:' + prepared.depositAddress : null;
  }

  // The SpacetimeDB HTTP API names reducers, tables and columns in snake_case (confirm_hive_job_funding, hive_settlement,
  // settlement_id); the C# names are refused as nonexistent (14 Nov 2026: a paid job stayed unpaid because of this).
  /** One pass over the escrow's pending settlements. Returns what happened to each row (for logs and tests). */
  async function sweep() {
    const outcome = [];
    const rows = await stdb().sql(`SELECT * FROM hive_settlement WHERE status = ${PENDING}`);
    if (sol) await sol.sweep(rows, outcome);
    for (const row of rows) {
      const id = row.settlement_id;
      if (String(row.funding_reference || '').startsWith('sol:')) continue;
      try {
        const operationId = String(row.funding_reference || '').replace(/^near:/, '');
        const op = await db().get(`hive-op:${operationId}`);
        if (!op) { outcome.push([id, 'no-funding-record']); continue; }
        const amount = share(op, row.micros);
        if (BigInt(amount) <= 0n) { outcome.push([id, 'zero']); continue; }
        const prepared = await prepare(row, op, amount);
        let sent = await db().get(prepared.id + ':sent');
        if (!sent) {
          if (!escrow?.send) { outcome.push([id, 'awaiting-escrow-signer']); continue; }
          const tx = await escrow.send({ asset: WNEAR, amount: prepared.amount, to: prepared.depositAddress,
            memo: prepared.depositMemo, reference: `hive-${prepared.kind}-${id}` });
          sent = await db().create({ id: prepared.id + ':sent', tx: String(tx ?? ''), sentAt: Date.now() });
        }
        const reference = await settled(prepared, sent);
        if (!reference) { outcome.push([id, 'in-flight']); continue; }
        await stdb().call('confirm_hive_settlement', [Number(id), reference]);
        outcome.push([id, 'settled']);
      } catch (error) {
        log(`[hive] settlement ${id}: ${error.message}`);
        outcome.push([id, 'error']);
      }
    }
    return outcome;
  }

  return { onQuoteStatus, sweep, solana: sol };
}

export const hiveSettlementKinds = { PAYOUT, REFUND };
