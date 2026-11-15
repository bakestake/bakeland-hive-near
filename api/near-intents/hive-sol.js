// Hive Link with direct Solana USDC (15 Nov 2026). The player pays a job with one USDC transfer from their own wallet
// into the studio treasury (BAKELAND_HIVE_TREASURY, the lootbox treasury), memo "hive:<clientJobId>", and pays only the
// network fee: no NEAR wallet, no 1Click deposit address (each of which cost the player ~0.002 SOL of token account
// rent). Once the transfer is confirmed on Solana this worker funds the job (ConfirmHiveJobFunding "sol:<signature>").
//
// The NEAR side stays in the payout: droid earnings go into the Hive's NEAR Intents agent (Agent API deposit with a
// Solana USDC origin), grouped so each transfer is worth sending. A job's pending payouts go together when the job
// closes (HiveJobClosure) or once they reach BAKELAND_HIVE_PAYOUT_MIN_MICROS (5 USDC). Refunds go back to the payer's
// wallet as USDC. The treasury key lives only in the signer Worker (escrow.sendSol); this file never holds it.
import { createHash } from 'node:crypto';
import { PublicKey, Transaction, TransactionInstruction } from '@solana/web3.js';
import { createTransferCheckedInstruction, getAssociatedTokenAddressSync } from '@solana/spl-token';

const USDC = new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v');
const MEMO = new PublicKey('MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr');
export const AGENT_SOLANA_USDC = 'nep141:sol-5ce3bf3a31af18be40ba30f721101b4341690186.omft.near';
const PENDING = 0;
const PAYOUT = 1;
const REFUND = 2;
// ConfirmHiveJobFunding errors that a retry cannot fix: the payment goes back to the payer instead.
const PERMANENT_FUNDING_ERROR = /Job not found|short of the order|not this job's player|funded by another operation/i;

/** The player's payment: their USDC account -> the treasury's, with the job's memo. The player is the fee payer. */
export function makeHivePaymentTransaction({ wallet, treasury, micros, operationId, blockhash, lastValidBlockHeight }) {
  const owner = new PublicKey(wallet);
  const source = getAssociatedTokenAddressSync(USDC, owner);
  const target = getAssociatedTokenAddressSync(USDC, new PublicKey(treasury), true);
  const tx = new Transaction({ feePayer: owner, blockhash, lastValidBlockHeight });
  tx.add(createTransferCheckedInstruction(source, USDC, target, owner, BigInt(micros), 6));
  tx.add(new TransactionInstruction({ programId: MEMO, keys: [{ pubkey: owner, isSigner: true, isWritable: false }],
    data: Buffer.from('hive:' + operationId) }));
  return tx;
}

/** A short, stable signer reference for a payment refund (Solana signatures are too long for the signer's references). */
export const paymentRefundReference = signature =>
  'hive-sol-refund-p' + createHash('sha256').update(signature).digest('hex').slice(0, 40);

/**
 * @param {object} deps
 * @param {object} deps.env
 * @param {() => {get, create, remove, scan}} deps.db
 * @param {() => import('@solana/web3.js').Connection} deps.solana
 * @param {(path: string, body?: object, key?: string) => Promise<object>} deps.agent
 * @param {() => {call: Function, sql: Function}} deps.stdb
 * @param {{sendSol?: (transfer: object) => Promise<{ok?: boolean, pending?: boolean, signature?: string}>}} [deps.escrow]
 */
export function createHiveSolana({ env, db, solana, agent, stdb, escrow, log = console.warn }) {
  const minPayout = () => BigInt(env.BAKELAND_HIVE_PAYOUT_MIN_MICROS || '5000000');
  const maxBatch = () => BigInt(env.BAKELAND_HIVE_PAYOUT_MAX_MICROS || '20000000');

  async function signatureState(signature, lastValidBlockHeight) {
    const { value: [status] } = await solana().getSignatureStatuses([signature], { searchTransactionHistory: true });
    if (status?.err) return 'failed';
    if (status && (status.confirmationStatus === 'confirmed' || status.confirmationStatus === 'finalized')) return 'confirmed';
    if (!status && lastValidBlockHeight && await solana().getBlockHeight('finalized') > lastValidBlockHeight) return 'expired';
    return 'pending';
  }

  /**
   * Advances one submitted payment (a `hive-pay-open:` row): funds the job once the transfer is confirmed, or queues a
   * full refund when the job cannot take it. Returns FUNDED, PENDING, FAILED, EXPIRED or REFUNDING. Safe to repeat.
   */
  async function advancePayment(open) {
    const done = await db().get(`hive-funding:${open.operationId}:${open.signature}`);
    if (done) { await db().remove(open.id).catch(() => {}); return done.status; }
    const state = await signatureState(open.signature, open.lastValidBlockHeight);
    if (state === 'pending') return 'PENDING';
    if (state === 'failed' || state === 'expired') {
      // Nothing moved. The job stays awaiting funding until it expires (seeds back), or the player pays again.
      await db().create({ id: `hive-funding:${open.operationId}:${open.signature}`, status: state.toUpperCase() });
      await db().remove(open.id).catch(() => {});
      return state.toUpperCase();
    }
    // Confirmed: this exact transfer (the signed bytes matched the prepared message) moved amountIn to the treasury.
    const reference = 'sol:' + open.signature;
    await db().create({ id: `hive-sol-op:${open.signature}`, operationId: open.operationId, wallet: open.wallet,
      amountIn: open.amountIn, signature: open.signature });
    let status = 'FUNDED';
    try {
      await stdb().call('confirm_hive_job_funding', [open.operationId, reference, Number(open.amountIn), open.wallet]);
    } catch (error) {
      if (!PERMANENT_FUNDING_ERROR.test(error.message)) throw error;   // transient: the next pass retries
      await db().create({ id: `hive-payrefund:${open.signature}`, operationId: open.operationId, wallet: open.wallet,
        amountIn: open.amountIn, signature: open.signature, reason: error.message.slice(0, 200) });
      status = 'REFUNDING';
    }
    await db().create({ id: `hive-funding:${open.operationId}:${open.signature}`, status });
    await db().remove(open.id).catch(() => {});
    return status;
  }

  async function sendSol(transfer) {
    if (typeof escrow?.sendSol !== 'function') return { awaiting: true };
    return escrow.sendSol({ asset: 'solana:usdc', ...transfer });
  }

  /** Full refunds of payments their job could not take. */
  async function sweepPaymentRefunds(outcome) {
    for (const refund of await db().scan('hive-payrefund:')) {
      if (refund.settled) continue;
      try {
        const sent = await sendSol({ amount: String(refund.amountIn), to: refund.wallet, memo: `hive refund ${refund.operationId}`,
          reference: paymentRefundReference(refund.signature) });
        if (sent?.ok) {
          await db().remove(refund.id);
          await db().create({ id: `hive-payrefunded:${refund.signature}`, refundSignature: sent.signature, wallet: refund.wallet, amountIn: refund.amountIn });
          outcome.push([`pay:${refund.operationId}`, 'refunded']);
        } else outcome.push([`pay:${refund.operationId}`, sent?.awaiting ? 'awaiting-signer' : 'in-flight']);
      } catch (error) {
        log(`[hive] payment refund ${refund.operationId}: ${error.message}`);
        outcome.push([`pay:${refund.operationId}`, 'error']);
      }
    }
  }

  async function refund(row, outcome) {
    const id = row.settlement_id;
    const op = await db().get(`hive-sol-op:${String(row.funding_reference).slice(4)}`);
    if (!op) { outcome.push([id, 'no-funding-record']); return; }
    const sent = await sendSol({ amount: String(row.micros), to: op.wallet, memo: `hive refund ${op.operationId}`,
      reference: `hive-sol-refund-${id}` });
    if (!sent?.ok) { outcome.push([id, sent?.awaiting ? 'awaiting-signer' : 'in-flight']); return; }
    await stdb().call('confirm_hive_settlement', [Number(id), 'sol:' + sent.signature]);
    outcome.push([id, 'settled']);
  }

  /** Picks the payouts to group now: every pending payout of a closed job, and any job's that reached the minimum. */
  function readyPayouts(rows, closed, batched) {
    const byJob = new Map();
    for (const row of rows) {
      if (batched.has(String(row.settlement_id))) continue;
      const list = byJob.get(String(row.job_id)) ?? [];
      list.push(row);
      byJob.set(String(row.job_id), list);
    }
    const ready = [];
    for (const [jobId, list] of byJob) {
      const total = list.reduce((sum, r) => sum + BigInt(r.micros), 0n);
      if (closed.has(jobId) || total >= minPayout()) ready.push(...list);
    }
    // One transfer stays under the signer's per-transfer cap; the rest waits for the next pass.
    ready.sort((a, b) => Number(a.settlement_id) - Number(b.settlement_id));
    const batch = [];
    let total = 0n;
    for (const row of ready) {
      if (batch.length && total + BigInt(row.micros) > maxBatch()) break;
      batch.push(row); total += BigInt(row.micros);
    }
    return { batch, total };
  }

  async function sendBatch(batch, outcome) {
    const ids = batch.ids;
    let prepared = batch;
    if (!prepared.depositAddress) {
      const agentId = env.BAKELAND_NEAR_HIVE_AGENT_ID;
      if (!agentId) { outcome.push([batch.reference, 'no-agent']); return; }
      const result = await agent('/v1/agents/' + encodeURIComponent(agentId) + '/deposit',
        { origin_asset: AGENT_SOLANA_USDC, amount: batch.micros }, batch.reference);
      const depositAddress = result?.details?.deposit_address;
      if (!depositAddress || !result.correlation_id) throw new Error('The agent deposit has no address');
      new PublicKey(depositAddress);
      prepared = { ...batch, id: batch.id + ':deposit', depositAddress, depositMemo: result.details?.memo ?? '',
        correlationId: result.correlation_id, agentId };
      prepared = await db().create(prepared);
    }
    let sent = await db().get(batch.id + ':sent');
    if (!sent) {
      const result = await sendSol({ amount: batch.micros, to: prepared.depositAddress, memo: prepared.depositMemo || undefined,
        reference: batch.reference });
      if (!result?.ok) { outcome.push([batch.reference, result?.awaiting ? 'awaiting-signer' : 'in-flight']); return; }
      sent = await db().create({ id: batch.id + ':sent', signature: result.signature, sentAt: Date.now() });
    }
    const status = await agent('/v1/status?' + new URLSearchParams({ correlation_id: prepared.correlationId }));
    if (status?.status !== 'SUCCESS') { outcome.push([batch.reference, 'in-flight']); return; }
    for (const id of ids) {
      await stdb().call('confirm_hive_settlement', [Number(id), `agent:${prepared.correlationId}`]);
      outcome.push([Number(id), 'settled']);
    }
  }

  /** One pass over Solana-funded settlements (funding_reference "sol:..."), payments and payment refunds. */
  async function sweep(rows, outcome = []) {
    for (const open of await db().scan('hive-pay-open:')) {
      try { outcome.push([`pay:${open.operationId}`, await advancePayment(open)]); }
      catch (error) { log(`[hive] payment ${open.operationId}: ${error.message}`); outcome.push([`pay:${open.operationId}`, 'error']); }
    }
    await sweepPaymentRefunds(outcome);

    const sol = rows.filter(r => String(r.funding_reference || '').startsWith('sol:') && Number(r.status ?? PENDING) === PENDING);
    for (const row of sol.filter(r => Number(r.kind) === REFUND)) {
      try { await refund(row, outcome); }
      catch (error) { log(`[hive] settlement ${row.settlement_id}: ${error.message}`); outcome.push([row.settlement_id, 'error']); }
    }

    const payouts = sol.filter(r => Number(r.kind) === PAYOUT);
    if (!payouts.length) return outcome;
    try {
      const batched = new Map();
      for (const row of payouts) {
        const marker = await db().get(`hive-settle:${row.settlement_id}`);
        if (marker?.batch) batched.set(String(row.settlement_id), marker.batch);
      }
      const closed = new Set((await stdb().sql('SELECT * FROM hive_job_closure')).map(r => String(r.job_id)));
      const { batch, total } = readyPayouts(payouts, closed, batched);
      if (batch.length) {
        const first = String(batch[0].settlement_id);
        const record = await db().create({ id: `hive-sol-batch:${first}`, reference: `hive-sol-payout-b${first}`,
          ids: batch.map(r => String(r.settlement_id)), micros: total.toString(), createdAt: Date.now() });
        for (const id of record.ids) {
          await db().create({ id: `hive-settle:${id}`, batch: record.id });
          batched.set(id, record.id);
        }
      }
      for (const batchId of new Set(batched.values())) {
        const record = await db().get(batchId);
        if (!record) continue;
        const pendingIds = new Set(payouts.map(r => String(r.settlement_id)));
        if (!record.ids.some(id => pendingIds.has(id))) continue;   // every row already confirmed
        try { await sendBatch({ ...(await db().get(batchId + ':deposit') ?? record), id: batchId, ids: record.ids, micros: record.micros, reference: record.reference }, outcome); }
        catch (error) { log(`[hive] payout ${record.reference}: ${error.message}`); outcome.push([record.reference, 'error']); }
      }
    } catch (error) {
      log(`[hive] payouts: ${error.message}`);
      outcome.push(['payouts', 'error']);
    }
    return outcome;
  }

  return { advancePayment, sweep, signatureState };
}
