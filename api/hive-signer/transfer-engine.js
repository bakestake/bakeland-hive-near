// The Hive escrow signer's transfer logic (signer.js runs it inside one Durable Object). Separate from Workers
// imports so tests can drive it with node:sqlite and a fake NEAR RPC.
import { functionCall, importNearKey, signTransaction, toBase64, base58Decode } from './near-tx.js';
import { checkTransfer, executionFailure } from './transfer-rules.js';

const WRAP = 'wrap.near';
const STORAGE_DEPOSIT_YOCTO = 1_250_000_000_000_000_000_000n;   // wrap.near storage_balance_bounds().min (0.00125 NEAR)
const GAS = 30_000_000_000_000n;                                  // 30 Tgas per call
const DAY_MS = 24 * 60 * 60 * 1000;

export const TRANSFERS_TABLE = `CREATE TABLE IF NOT EXISTS transfers (
  reference TEXT PRIMARY KEY, to_account TEXT NOT NULL, amount TEXT NOT NULL, tx_hash TEXT NOT NULL,
  signed_tx TEXT NOT NULL, status TEXT NOT NULL, error TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)`;

export function nearRpc(url, fetchImpl = fetch) {
  let id = 0;
  return async function call(method, params) {
    const response = await fetchImpl(url, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: AbortSignal.timeout(60_000),
      body: JSON.stringify({ jsonrpc: '2.0', id: `hive-signer-${++id}`, method, params })
    });
    const body = await response.json().catch(() => null);
    if (!response.ok || !body) throw new Error(`NEAR RPC ${method} failed (${response.status})`);
    if (body.error) throw new Error(`NEAR RPC ${method}: ${body.error.cause?.name || body.error.name || 'error'} ${JSON.stringify(body.error.data ?? body.error.cause?.info ?? '').slice(0, 200)}`);
    return body.result;
  };
}

async function viewFunction(rpc, method, args) {
  const result = await rpc('query', { request_type: 'call_function', finality: 'final', account_id: WRAP, method_name: method,
    args_base64: toBase64(new TextEncoder().encode(JSON.stringify(args))) });
  return JSON.parse(new TextDecoder().decode(Uint8Array.from(result.result)));
}

/**
 * @param {{exec: (query: string, ...args: any[]) => {toArray: () => object[]}}} sql  Durable Object SQL storage
 * @param {object} env  ESCROW_ACCOUNT_ID, ESCROW_SECRET_KEY, MAX_TRANSFER_YOCTO, DAILY_MAX_YOCTO
 * @param {(method: string, params: object) => Promise<object>} rpc
 */
export function createTransferEngine({ sql, env, rpc, now = () => Date.now() }) {
  sql.exec(TRANSFERS_TABLE);
  let queue = Promise.resolve();

  async function broadcast(reference, signedText, hash) {
    let result;
    try {
      result = await rpc('send_tx', { signed_tx_base64: signedText, wait_until: 'FINAL' });
    } catch (error) {
      // Unknown outcome (timeout, RPC error): the row stays pending and the next call re-sends the same bytes.
      return { ok: false, pending: true, tx: hash, error: error.message };
    }
    const failure = executionFailure(result);
    sql.exec('UPDATE transfers SET status = ?, error = ?, updated_at = ? WHERE reference = ?',
      failure ? 'failed' : 'sent', failure, now(), reference);
    // A failed transaction moved nothing (wrap.near refunds a failed ft_transfer); the row records why, and the same
    // reference keeps returning that failure until an operator clears the row.
    return failure ? { ok: false, tx: hash, error: `Transfer failed on chain: ${failure}` } : { ok: true, tx: hash };
  }

  async function send(request) {
    let transfer;
    try { transfer = checkTransfer(request, env); }
    catch (error) { return { ok: false, refused: true, error: error.message }; }
    const at = now();

    const existing = sql.exec('SELECT * FROM transfers WHERE reference = ?', transfer.reference).toArray()[0];
    if (existing) {
      if (existing.to_account !== transfer.to || existing.amount !== transfer.amount.toString()) {
        return { ok: false, refused: true, error: 'This reference was already used for a different transfer' };
      }
      if (existing.status === 'sent') return { ok: true, tx: existing.tx_hash, repeated: true };
      if (existing.status === 'failed') return { ok: false, tx: existing.tx_hash, error: `Transfer failed on chain: ${existing.error}` };
      return broadcast(transfer.reference, existing.signed_tx, existing.tx_hash);
    }

    // An earlier transfer with an unknown outcome holds the nonce this one would take: settle it first.
    const unconfirmed = sql.exec(`SELECT * FROM transfers WHERE status = 'pending' ORDER BY created_at LIMIT 1`).toArray()[0];
    if (unconfirmed) {
      const earlier = await broadcast(unconfirmed.reference, unconfirmed.signed_tx, unconfirmed.tx_hash);
      if (earlier.pending) return { ok: false, pending: true, error: `Earlier transfer ${unconfirmed.reference} is still unconfirmed` };
    }

    const dayTotal = sql.exec(`SELECT amount FROM transfers WHERE created_at > ? AND status != 'failed'`, at - DAY_MS)
      .toArray().reduce((sum, row) => sum + BigInt(row.amount), 0n);
    const dailyMax = BigInt(env.DAILY_MAX_YOCTO || '0');
    if (dailyMax <= 0n || dayTotal + transfer.amount > dailyMax) {
      return { ok: false, refused: true, error: 'The escrow signer reached its daily limit' };
    }

    const signerId = env.ESCROW_ACCOUNT_ID;
    if (!signerId) return { ok: false, error: 'ESCROW_ACCOUNT_ID is not set' };
    const key = await importNearKey(env.ESCROW_SECRET_KEY);
    const accessKey = await rpc('query', { request_type: 'view_access_key', finality: 'final', account_id: signerId, public_key: key.publicKeyText });
    const registered = await viewFunction(rpc, 'storage_balance_of', { account_id: transfer.to });
    const actions = [];
    if (!registered) actions.push(functionCall('storage_deposit', { account_id: transfer.to, registration_only: true }, GAS, STORAGE_DEPOSIT_YOCTO));
    // 1Click delivers player payments to the escrow as native NEAR (14 Nov 2026: 0.114 NEAR arrived, 0 wNEAR), so wrap
    // the shortfall in the same transaction (near_deposit), keeping NATIVE_RESERVE_YOCTO for gas and storage.
    const wrapped = BigInt(await viewFunction(rpc, 'ft_balance_of', { account_id: signerId }) ?? '0');
    if (wrapped < transfer.amount) {
      const shortfall = transfer.amount - wrapped;
      const account = await rpc('query', { request_type: 'view_account', finality: 'final', account_id: signerId });
      const spendable = BigInt(account.amount) - BigInt(env.NATIVE_RESERVE_YOCTO || '100000000000000000000000');
      if (shortfall > spendable) return { ok: false, refused: true, error: 'The escrow does not hold enough NEAR for this transfer' };
      actions.push(functionCall('near_deposit', {}, GAS, shortfall));
    }
    actions.push(functionCall('ft_transfer', { receiver_id: transfer.to, amount: transfer.amount.toString(),
      ...(transfer.memo && { memo: transfer.memo }) }, GAS, 1n));
    const { signed, hash } = await signTransaction({ signerId, publicKey: key.publicKey, nonce: BigInt(accessKey.nonce) + 1n,
      receiverId: WRAP, blockHash: base58Decode(accessKey.block_hash), actions }, key);
    const signedText = toBase64(signed);
    // Recorded before broadcasting: whatever happens next, a retry sends these exact bytes and never a second transfer.
    sql.exec(`INSERT INTO transfers (reference, to_account, amount, tx_hash, signed_tx, status, created_at, updated_at)
      VALUES (?, ?, ?, ?, ?, 'pending', ?, ?)`, transfer.reference, transfer.to, transfer.amount.toString(), hash, signedText, at, at);
    return broadcast(transfer.reference, signedText, hash);
  }

  /** Read-only check that the stored key can sign for the escrow account. Returns public facts only, never the key. */
  async function check() {
    const signerId = env.ESCROW_ACCOUNT_ID;
    const out = { escrow: signerId || null };
    try {
      const key = await importNearKey(env.ESCROW_SECRET_KEY);
      out.publicKey = key.publicKeyText;
      if (!signerId) return { ...out, ok: false, problem: 'ESCROW_ACCOUNT_ID is not set' };
      try {
        const accessKey = await rpc('query', { request_type: 'view_access_key', finality: 'final', account_id: signerId, public_key: key.publicKeyText });
        out.fullAccess = accessKey.permission === 'FullAccess';
      } catch (error) {
        return { ...out, ok: false, problem: `This key is not an access key of ${signerId} (${error.message.slice(0, 120)})` };
      }
      const account = await rpc('query', { request_type: 'view_account', finality: 'final', account_id: signerId });
      out.nearBalanceYocto = String(account.amount);
      out.wnearBalanceYocto = String(await viewFunction(rpc, 'ft_balance_of', { account_id: signerId }));
      out.wrapStorageRegistered = Boolean(await viewFunction(rpc, 'storage_balance_of', { account_id: signerId }));
      out.ok = out.fullAccess;
      if (!out.fullAccess) out.problem = 'The key is a function-call key; ft_transfer needs a full-access key (1 yocto deposit)';
      return out;
    } catch (error) {
      return { ...out, ok: false, problem: error.message };
    }
  }

  /**
   * Registers the escrow account itself with wrap.near (storage_deposit, 0.00125 NEAR) so incoming wNEAR from player
   * payments can land. A no-op when it is already registered. Runs in the same queue as transfers (shared nonce).
   */
  async function registerEscrow() {
    const signerId = env.ESCROW_ACCOUNT_ID;
    if (!signerId) return { ok: false, error: 'ESCROW_ACCOUNT_ID is not set' };
    if (await viewFunction(rpc, 'storage_balance_of', { account_id: signerId })) return { ok: true, alreadyRegistered: true };
    const key = await importNearKey(env.ESCROW_SECRET_KEY);
    const accessKey = await rpc('query', { request_type: 'view_access_key', finality: 'final', account_id: signerId, public_key: key.publicKeyText });
    const { signed, hash } = await signTransaction({ signerId, publicKey: key.publicKey, nonce: BigInt(accessKey.nonce) + 1n,
      receiverId: WRAP, blockHash: base58Decode(accessKey.block_hash),
      actions: [functionCall('storage_deposit', { account_id: signerId, registration_only: true }, GAS, STORAGE_DEPOSIT_YOCTO)] }, key);
    let result;
    try { result = await rpc('send_tx', { signed_tx_base64: toBase64(signed), wait_until: 'FINAL' }); }
    catch (error) { return { ok: false, pending: true, tx: hash, error: error.message }; }
    const failure = executionFailure(result);
    return failure ? { ok: false, tx: hash, error: `Registration failed on chain: ${failure}` } : { ok: true, tx: hash };
  }

  /** Forgets a transfer that failed on chain (nothing moved), so the same reference can be signed again. */
  function clearFailed(reference) {
    const row = sql.exec('SELECT status FROM transfers WHERE reference = ?', String(reference)).toArray()[0];
    if (!row) return { reference, cleared: false };
    if (row.status !== 'failed') return { reference, cleared: false, status: row.status };
    sql.exec(`DELETE FROM transfers WHERE reference = ? AND status = 'failed'`, String(reference));
    return { reference, cleared: true };
  }

  return {
    check,
    clearFailed,
    registerEscrow() {
      const run = queue.then(() => registerEscrow());
      queue = run.catch(() => {});
      return run;
    },
    /** Transfers run one after another so nonces and the daily total never race. */
    send(request) {
      const run = queue.then(() => send(request));
      queue = run.catch(() => {});
      return run;
    },
    status() {
      const recent = sql.exec('SELECT reference, to_account, amount, tx_hash, status, error, created_at FROM transfers ORDER BY created_at DESC LIMIT 20').toArray();
      const day = sql.exec(`SELECT amount FROM transfers WHERE created_at > ? AND status != 'failed'`, now() - DAY_MS).toArray()
        .reduce((sum, row) => sum + BigInt(row.amount), 0n);
      return { escrow: env.ESCROW_ACCOUNT_ID || null, dayTotalYocto: day.toString(), dailyMaxYocto: env.DAILY_MAX_YOCTO || '0',
        maxTransferYocto: env.MAX_TRANSFER_YOCTO || '0', recent };
    }
  };
}
