// Transfer rules for the Hive escrow signer (signer.js), kept free of Workers imports so Node tests can load them.

export const WNEAR_ASSET = 'nep141:wrap.near';
const ACCOUNT = /^(([a-z\d]+[-_])*[a-z\d]+\.)*([a-z\d]+[-_])*[a-z\d]+$/;

export class TransferRefused extends Error {}

/** Validates a transfer request against the signer's limits. Throws TransferRefused. */
export function checkTransfer(transfer, env) {
  const { asset, amount, to, reference } = transfer ?? {};
  if (asset !== WNEAR_ASSET) throw new TransferRefused(`Only ${WNEAR_ASSET} can be sent`);
  if (typeof to !== 'string' || to.length < 2 || to.length > 64 || !ACCOUNT.test(to)) throw new TransferRefused('Invalid NEAR account');
  if (to === env.ESCROW_ACCOUNT_ID) throw new TransferRefused('The escrow cannot pay itself');
  if (typeof amount !== 'string' || !/^[1-9]\d{0,38}$/.test(amount)) throw new TransferRefused('Amount must be a positive integer in yocto');
  if (typeof reference !== 'string' || !/^hive-(payout|refund)-\d{1,20}$/.test(reference)) throw new TransferRefused('Invalid reference');
  const max = BigInt(env.MAX_TRANSFER_YOCTO || '0');
  if (max <= 0n || BigInt(amount) > max) throw new TransferRefused('Amount is over the per-transfer limit');
  return { amount: BigInt(amount), to, reference, memo: typeof transfer.memo === 'string' ? transfer.memo.slice(0, 256) : '' };
}

/** Failure anywhere in the transaction or its receipts, or null when everything succeeded. */
export function executionFailure(result) {
  if (result?.status?.Failure) return JSON.stringify(result.status.Failure).slice(0, 300);
  for (const receipt of result?.receipts_outcome ?? []) {
    if (receipt.outcome?.status?.Failure) return JSON.stringify(receipt.outcome.status.Failure).slice(0, 300);
  }
  const status = result?.status ?? {};
  if ('SuccessValue' in status || 'SuccessReceiptId' in status) return null;
  return 'Unknown outcome';
}

