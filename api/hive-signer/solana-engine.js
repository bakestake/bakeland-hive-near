// The Hive signer's Solana side (15 Nov 2026): USDC out of the lootbox treasury (TREASURY_SOLANA_ADDRESS) for Hive
// Link payouts (grouped deposits into the Hive agent) and refunds (back to the player's wallet). Runs in the same
// Durable Object as the NEAR transfers (signer.js), one transfer at a time.
//
// No double sends: the signed transaction is stored under the caller's reference before it is broadcast, and a retry
// re-sends those exact bytes. A Solana transaction is only valid until its blockhash expires, so a stored one is
// signed again (new blockhash) only once a FINALIZED block height is past its last valid height and the network has
// no record of its signature: then it can never land, and nothing moved.
import { solanaAddress, importSolanaKey, compileMessage, signMessage, usdcTransferInstructions, associatedTokenAddress } from './solana-tx.js';

export const SOLANA_USDC_ASSET = 'solana:usdc';
const DAY_MS = 24 * 60 * 60 * 1000;
const REFERENCE = /^hive-sol-(payout|refund)-[a-zA-Z0-9_-]{1,60}$/;

export const SOL_TRANSFERS_TABLE = `CREATE TABLE IF NOT EXISTS sol_transfers (
  reference TEXT PRIMARY KEY, to_owner TEXT NOT NULL, micros TEXT NOT NULL, memo TEXT NOT NULL, signature TEXT NOT NULL,
  signed_tx TEXT NOT NULL, last_valid INTEGER NOT NULL, status TEXT NOT NULL, error TEXT,
  created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)`;

export function solanaRpc(url, fetchImpl = fetch) {
  let id = 0;
  return async function call(method, params) {
    const response = await fetchImpl(url, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: AbortSignal.timeout(20_000),
      body: JSON.stringify({ jsonrpc: '2.0', id: `hive-signer-${++id}`, method, params })
    });
    const body = await response.json().catch(() => null);
    if (!response.ok || !body) throw new Error(`Solana RPC ${method} failed (${response.status})`);
    if (body.error) throw Object.assign(new Error(`Solana RPC ${method}: ${String(body.error.message || 'error').slice(0, 300)}`), { rpcError: body.error });
    return body.result;
  };
}

/** Validates a USDC transfer request against the signer's limits. Throws with the reason. */
export function checkSolTransfer(request, env) {
  const { asset, amount, to, reference } = request ?? {};
  if (asset !== SOLANA_USDC_ASSET) throw new Error(`Only ${SOLANA_USDC_ASSET} can be sent`);
  solanaAddress(to);
  if (to === env.TREASURY_SOLANA_ADDRESS) throw new Error('The treasury cannot pay itself');
  if (typeof amount !== 'string' || !/^[1-9]\d{0,15}$/.test(amount)) throw new Error('Amount must be a positive integer in USDC micros');
  if (typeof reference !== 'string' || !REFERENCE.test(reference)) throw new Error('Invalid reference');
  const max = BigInt(env.MAX_USDC_TRANSFER_MICROS || '0');
  if (max <= 0n || BigInt(amount) > max) throw new Error('Amount is over the per-transfer limit');
  const memo = typeof request.memo === 'string' ? request.memo.slice(0, 120) : '';
  return { micros: BigInt(amount), to, reference, memo };
}

export function createSolanaEngine({ sql, env, rpc, now = () => Date.now(), sleep = ms => new Promise(r => setTimeout(r, ms)) }) {
  sql.exec(SOL_TRANSFERS_TABLE);
  const row = reference => sql.exec('SELECT * FROM sol_transfers WHERE reference = ?', reference).toArray()[0];
  const mark = (reference, status, error = null) =>
    sql.exec('UPDATE sol_transfers SET status = ?, error = ?, updated_at = ? WHERE reference = ?', status, error, now(), reference);

  async function signatureStatus(signature) {
    const result = await rpc('getSignatureStatuses', [[signature], { searchTransactionHistory: true }]);
    return result?.value?.[0] ?? null;
  }

  /** Where a stored transfer stands: 'sent', 'failed', 'expired' (never landed, safe to re-sign) or 'pending'. */
  async function resolve(stored) {
    const status = await signatureStatus(stored.signature);
    if (status?.err) { mark(stored.reference, 'failed', JSON.stringify(status.err).slice(0, 300)); return 'failed'; }
    if (status && (status.confirmationStatus === 'confirmed' || status.confirmationStatus === 'finalized')) {
      mark(stored.reference, 'sent'); return 'sent';
    }
    if (!status) {
      const finalizedHeight = await rpc('getBlockHeight', [{ commitment: 'finalized' }]);
      if (finalizedHeight > stored.last_valid) return 'expired';
    }
    return 'pending';
  }

  async function broadcast(stored) {
    try {
      await rpc('sendTransaction', [stored.signed_tx, { encoding: 'base64', skipPreflight: false, preflightCommitment: 'confirmed', maxRetries: 3 }]);
    } catch (error) {
      // A simulation failure means the RPC refused to forward it: nothing landed. Anything else is an unknown outcome.
      if (error.rpcError?.code === -32002 && !/already been processed/i.test(error.message)) {
        mark(stored.reference, 'failed', error.message.slice(0, 300));
        return { ok: false, signature: stored.signature, error: `Transfer refused by simulation: ${error.message}` };
      }
    }
    for (let i = 0; i < 12; i++) {
      await sleep(2500);
      const state = await resolve(stored).catch(() => 'pending');
      if (state === 'sent') return { ok: true, signature: stored.signature };
      if (state === 'failed') return { ok: false, signature: stored.signature, error: `Transfer failed on chain: ${row(stored.reference)?.error}` };
      if (state === 'expired') break;
    }
    return { ok: false, pending: true, signature: stored.signature, error: 'Transfer not confirmed yet' };
  }

  async function sign(transfer) {
    const key = await importSolanaKey(env.TREASURY_SOLANA_SECRET_KEY, env.TREASURY_SOLANA_ADDRESS);
    const { instructions, source } = await usdcTransferInstructions({ from: key.address, to: transfer.to, micros: transfer.micros,
      memo: transfer.memo, priorityMicroLamports: Number(env.SOLANA_PRIORITY_MICROLAMPORTS || 0) });
    const balance = await rpc('getTokenAccountBalance', [source, { commitment: 'confirmed' }]);
    if (BigInt(balance?.value?.amount ?? '0') < transfer.micros) throw Object.assign(new Error('The treasury does not hold enough USDC'), { refused: true });
    const { value } = await rpc('getLatestBlockhash', [{ commitment: 'finalized' }]);
    const { message } = compileMessage({ payer: key.address, recentBlockhash: value.blockhash, instructions });
    const { wire, signature } = await signMessage(message, key);
    return { wire, signature, lastValid: Number(value.lastValidBlockHeight) };
  }

  async function send(request) {
    let transfer;
    try { transfer = checkSolTransfer(request, env); }
    catch (error) { return { ok: false, refused: true, error: error.message }; }
    if (!env.TREASURY_SOLANA_SECRET_KEY || !env.TREASURY_SOLANA_ADDRESS) return { ok: false, error: 'The treasury key is not configured' };

    let stored = row(transfer.reference);
    if (stored) {
      if (stored.to_owner !== transfer.to || stored.micros !== transfer.micros.toString()) {
        return { ok: false, refused: true, error: 'This reference was already used for a different transfer' };
      }
      if (stored.status === 'sent') return { ok: true, signature: stored.signature, repeated: true };
      if (stored.status === 'failed') return { ok: false, signature: stored.signature, error: `Transfer failed on chain: ${stored.error}` };
      const state = await resolve(stored);
      if (state === 'sent') return { ok: true, signature: stored.signature };
      if (state === 'failed') return { ok: false, signature: stored.signature, error: `Transfer failed on chain: ${row(stored.reference).error}` };
      if (state === 'pending') return broadcast(stored);
      // Expired without landing: sign the same transfer again with a fresh blockhash.
      try {
        const signed = await sign(transfer);
        sql.exec('UPDATE sol_transfers SET signature = ?, signed_tx = ?, last_valid = ?, updated_at = ? WHERE reference = ?',
          signed.signature, signed.wire, signed.lastValid, now(), transfer.reference);
      } catch (error) { return { ok: false, refused: Boolean(error.refused), error: error.message }; }
      return broadcast(row(transfer.reference));
    }

    // Another transfer with an unknown outcome: settle it first, so the daily total and the treasury balance are real.
    const unconfirmed = sql.exec(`SELECT * FROM sol_transfers WHERE status = 'pending' ORDER BY created_at LIMIT 1`).toArray()[0];
    if (unconfirmed) {
      const state = await resolve(unconfirmed);
      if (state === 'pending') return { ok: false, pending: true, error: `Earlier transfer ${unconfirmed.reference} is still unconfirmed` };
      if (state === 'expired') return { ok: false, pending: true, error: `Earlier transfer ${unconfirmed.reference} expired; retry it first` };
    }

    const at = now();
    const dayTotal = sql.exec(`SELECT micros FROM sol_transfers WHERE created_at > ? AND status != 'failed'`, at - DAY_MS)
      .toArray().reduce((sum, r) => sum + BigInt(r.micros), 0n);
    const dailyMax = BigInt(env.DAILY_MAX_USDC_MICROS || '0');
    if (dailyMax <= 0n || dayTotal + transfer.micros > dailyMax) return { ok: false, refused: true, error: 'The treasury signer reached its daily limit' };

    let signed;
    try { signed = await sign(transfer); }
    catch (error) { return { ok: false, refused: Boolean(error.refused), error: error.message }; }
    // Recorded before broadcasting: a retry sends these exact bytes and never a second transfer.
    sql.exec(`INSERT INTO sol_transfers (reference, to_owner, micros, memo, signature, signed_tx, last_valid, status, created_at, updated_at)
      VALUES (?, ?, ?, ?, ?, ?, ?, 'pending', ?, ?)`, transfer.reference, transfer.to, transfer.micros.toString(), transfer.memo,
      signed.signature, signed.wire, signed.lastValid, at, at);
    return broadcast(row(transfer.reference));
  }

  /** Read-only: the treasury key matches its address, and its SOL and USDC balances. Never returns the key. */
  async function check() {
    const out = { treasury: env.TREASURY_SOLANA_ADDRESS || null };
    try {
      const key = await importSolanaKey(env.TREASURY_SOLANA_SECRET_KEY, env.TREASURY_SOLANA_ADDRESS);
      out.keyMatches = true;
      out.solLamports = String((await rpc('getBalance', [key.address, { commitment: 'confirmed' }]))?.value ?? 0);
      const ata = await associatedTokenAddress(key.address);
      out.usdcAccount = ata;
      out.usdcMicros = String((await rpc('getTokenAccountBalance', [ata, { commitment: 'confirmed' }]))?.value?.amount ?? '0');
      out.ok = true;
    } catch (error) {
      out.ok = false; out.problem = error.message;
    }
    return out;
  }

  function status() {
    const recent = sql.exec('SELECT reference, to_owner, micros, signature, status, error, created_at FROM sol_transfers ORDER BY created_at DESC LIMIT 20').toArray();
    const day = sql.exec(`SELECT micros FROM sol_transfers WHERE created_at > ? AND status != 'failed'`, now() - DAY_MS).toArray()
      .reduce((sum, r) => sum + BigInt(r.micros), 0n);
    return { treasury: env.TREASURY_SOLANA_ADDRESS || null, dayTotalMicros: day.toString(), dailyMaxMicros: env.DAILY_MAX_USDC_MICROS || '0',
      maxTransferMicros: env.MAX_USDC_TRANSFER_MICROS || '0', recent };
  }

  function clearFailed(reference) {
    const r = row(String(reference));
    if (!r) return { reference, cleared: false };
    if (r.status !== 'failed') return { reference, cleared: false, status: r.status };
    sql.exec(`DELETE FROM sol_transfers WHERE reference = ? AND status = 'failed'`, String(reference));
    return { reference, cleared: true };
  }

  return { send, check, status, clearFailed };
}
