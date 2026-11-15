// Excerpt from Bakeland's API Worker (cloudflare/worker.js): how the API reaches the isolated signer and runs the
// settlement sweep. The rest of the Worker (other routes, D1 storage shim, other crons) is not part of this repository.
//
// The API never holds a signing key. `hiveEscrow` is passed to createNearIntentsRouter({ hiveEscrow }) and every move
// of funds goes over the HIVE_SIGNER service binding to bakeland-hive-signer (api/hive-signer), which has no public URL.
import { createNearIntentsRouter } from './near-intents/router.js';

export const hiveEscrow = {};
export const nearIntentsRouter = createNearIntentsRouter({ hiveEscrow });   // mounted at /near-intents

export function bindHiveSigner(env) {
  if (!env.HIVE_SIGNER) return;   // without the binding the sweep prepares each settlement and leaves it pending
  const call = async (path, body) => {
    const response = await env.HIVE_SIGNER.fetch('https://hive-signer' + path, body === undefined ? undefined : {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    });
    return { response, result: await response.json().catch(() => ({})) };
  };
  // NEAR: wrap.near transfers out of the escrow account (payouts and refunds of jobs paid through 1Click).
  hiveEscrow.send = async transfer => {
    const { response, result } = await call('/transfer', transfer);
    if (!response.ok) throw new Error(`Escrow signer refused the transfer (${response.status}): ${result.error || 'no reason'}`);
    return result.tx;
  };
  // Solana: USDC out of the treasury (grouped payouts into the Hive agent, refunds). { pending } until confirmed.
  hiveEscrow.sendSol = async transfer => {
    const { response, result } = await call('/sol-transfer', transfer);
    if (response.ok) return result;
    if (response.status === 504) return { ...result, ok: false, pending: true };
    throw new Error(`Treasury signer refused the transfer (${response.status}): ${result.error || 'no reason'}`);
  };
  hiveEscrow.check = async () => (await env.HIVE_SIGNER.fetch('https://hive-signer/check')).json();
  hiveEscrow.status = async () => (await env.HIVE_SIGNER.fetch('https://hive-signer/status')).json();
  hiveEscrow.solCheck = async () => (await env.HIVE_SIGNER.fetch('https://hive-signer/sol-check')).json();
  hiveEscrow.solStatus = async () => (await env.HIVE_SIGNER.fetch('https://hive-signer/sol-status')).json();
  hiveEscrow.clearFailed = async reference => (await call('/clear-failed', { reference })).result;
  hiveEscrow.solClearFailed = async reference => (await call('/sol-clear-failed', { reference })).result;
  hiveEscrow.registerEscrow = async () => (await call('/register-escrow', {})).result;
}

/** Run by the Worker's minute cron: funds confirmed payments, groups payouts, sends refunds. */
export async function sweepHiveSettlements(env) {
  if (env.BAKELAND_NEAR_ENABLED !== 'true' || env.BAKELAND_HIVE_SETTLEMENT_SWEEP !== 'true') return;
  const outcome = await nearIntentsRouter.hive.sweep();
  if (outcome.length) console.log('Hive settlements', JSON.stringify(outcome));
}
