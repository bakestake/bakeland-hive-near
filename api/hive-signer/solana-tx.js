// Minimal Solana transaction building for the Hive escrow signer (15 Nov 2026): the legacy message layout, the
// associated token account address, and the three instructions a USDC transfer from the treasury needs
// (CreateIdempotent, TransferChecked, Memo). Ed25519 signing goes through WebCrypto like near-tx.js; no SDK, so the
// code that touches the treasury key stays small enough to read. signer.test.js checks it against @solana/web3.js.
import { base58Decode, base58Encode } from './near-tx.js';

export const TOKEN_PROGRAM = 'TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA';
export const ATA_PROGRAM = 'ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL';
export const SYSTEM_PROGRAM = '11111111111111111111111111111111';
export const MEMO_PROGRAM = 'MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr';
export const COMPUTE_BUDGET_PROGRAM = 'ComputeBudget111111111111111111111111111111';
export const USDC_MINT = 'EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v';
export const USDC_DECIMALS = 6;

const b64url = bytes => btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const fromBase64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));

/** A 32-byte Solana address from its base58 text. Throws on anything else. */
export function solanaAddress(text) {
  if (typeof text !== 'string' || text.length < 32 || text.length > 44) throw new Error('Invalid Solana address');
  const bytes = base58Decode(text);
  if (bytes.length !== 32) throw new Error('Invalid Solana address');
  return bytes;
}

// --- Ed25519 curve membership (a program-derived address must be off the curve) ---------------------------------
const P = (1n << 255n) - 19n;
const mod = a => ((a % P) + P) % P;
function pow(base, exp) {
  let result = 1n, b = mod(base);
  for (let e = exp; e > 0n; e >>= 1n) { if (e & 1n) result = result * b % P; b = b * b % P; }
  return result;
}
const D = mod(-121665n * pow(121666n, P - 2n));
const SQRT_M1 = pow(2n, (P - 1n) / 4n);

/** Whether 32 bytes decompress to an Ed25519 point (curve25519-dalek CompressedEdwardsY::decompress().is_some()). */
export function isOnCurve(bytes) {
  let y = 0n;
  for (let i = 31; i >= 0; i--) y = (y << 8n) | BigInt(i === 31 ? bytes[i] & 0x7f : bytes[i]);
  y = mod(y);
  const y2 = y * y % P;
  const u = mod(y2 - 1n), v = mod(D * y2 + 1n);
  const v3 = v * v % P * v % P, v7 = v3 * v3 % P * v % P;
  let x = u * v3 % P * pow(u * v7 % P, (P - 5n) / 8n) % P;
  const vx2 = v * x % P * x % P;
  if (vx2 === u) return true;
  if (vx2 === mod(-u)) { x = x * SQRT_M1 % P; return true; }
  return false;
}

async function sha256(...parts) {
  const all = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.length; }
  return new Uint8Array(await crypto.subtle.digest('SHA-256', all));
}

/** PublicKey.findProgramAddressSync: the first bump from 255 down whose hash is off the curve. */
export async function findProgramAddress(seeds, programId) {
  const program = solanaAddress(programId);
  const marker = new TextEncoder().encode('ProgramDerivedAddress');
  for (let bump = 255; bump >= 0; bump--) {
    const hash = await sha256(...seeds, Uint8Array.of(bump), program, marker);
    if (!isOnCurve(hash)) return { address: base58Encode(hash), bump };
  }
  throw new Error('No program address found');
}

/** The associated token account of `owner` for `mint` (classic token program). */
export async function associatedTokenAddress(owner, mint = USDC_MINT) {
  return (await findProgramAddress([solanaAddress(owner), solanaAddress(TOKEN_PROGRAM), solanaAddress(mint)], ATA_PROGRAM)).address;
}

// --- Keys --------------------------------------------------------------------------------------------------------
/**
 * Parses the treasury's 64-byte secret (seed + public key) from a JSON byte array, hex, base58 (wallet export) or
 * base64 (what bakeland-api's decodeSecretKey also accepts), and refuses it unless its public key is `expected`.
 */
export async function importSolanaKey(secret, expected) {
  const text = String(secret ?? '').trim();
  const candidates = [];
  if (text.startsWith('[')) { try { candidates.push(Uint8Array.from(JSON.parse(text))); } catch {} }
  else if (/^[0-9a-fA-F]{128}$/.test(text)) candidates.push(Uint8Array.from(text.match(/../g), h => parseInt(h, 16)));
  else {
    try { candidates.push(base58Decode(text)); } catch {}
    try { candidates.push(fromBase64(text)); } catch {}
  }
  const raw = candidates.find(c => c.length === 64);
  if (!raw) throw new Error('The treasury key must be a 64-byte Solana secret key');
  const seed = raw.slice(0, 32), publicKey = raw.slice(32);
  const address = base58Encode(publicKey);
  if (!expected || address !== expected) throw new Error('The treasury key is not the key of TREASURY_SOLANA_ADDRESS');
  let privateKey;
  try {
    privateKey = await crypto.subtle.importKey('jwk',
      { kty: 'OKP', crv: 'Ed25519', d: b64url(seed), x: b64url(publicKey), ext: false }, { name: 'Ed25519' }, false, ['sign']);
  } catch { throw new Error('The treasury key does not match its public key'); }
  const check = new TextEncoder().encode('bakeland-hive-signer solana key check');
  const verifyKey = await crypto.subtle.importKey('raw', publicKey, { name: 'Ed25519' }, false, ['verify']);
  if (!await crypto.subtle.verify('Ed25519', verifyKey, await crypto.subtle.sign('Ed25519', privateKey, check), check)) {
    throw new Error('The treasury key does not match its public key');
  }
  return { privateKey, publicKey, address };
}

// --- Message -----------------------------------------------------------------------------------------------------
function compactU16(n) {
  const out = [];
  for (;;) {
    let byte = n & 0x7f; n >>= 7;
    if (n === 0) { out.push(byte); return out; }
    out.push(byte | 0x80);
  }
}

const u64le = value => { const b = new Uint8Array(8); new DataView(b.buffer).setBigUint64(0, BigInt(value), true); return b; };
const u32le = value => { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, value, true); return b; };

/**
 * Legacy message: header, account keys (writable signers, read-only signers, writable, read-only), recent blockhash,
 * instructions. The fee payer is the first key. `instructions`: [{ programId, keys: [{pubkey, isSigner, isWritable}], data }].
 */
export function compileMessage({ payer, recentBlockhash, instructions }) {
  const metas = new Map();
  const add = (pubkey, isSigner, isWritable) => {
    const m = metas.get(pubkey) ?? { pubkey, isSigner: false, isWritable: false };
    m.isSigner ||= isSigner; m.isWritable ||= isWritable;
    metas.set(pubkey, m);
  };
  add(payer, true, true);
  for (const ix of instructions) {
    for (const k of ix.keys) add(k.pubkey, k.isSigner, k.isWritable);
    add(ix.programId, false, false);
  }
  const rest = [...metas.values()].filter(m => m.pubkey !== payer);
  const rank = m => (m.isSigner ? 0 : 2) + (m.isWritable ? 0 : 1);
  // Stable sort by role, matching web3.js (which also orders by role and keeps insertion order within one role).
  const ordered = [metas.get(payer), ...rest.map((m, i) => [m, i]).sort((a, b) => rank(a[0]) - rank(b[0]) || a[1] - b[1]).map(([m]) => m)];
  const index = new Map(ordered.map((m, i) => [m.pubkey, i]));
  const header = [
    ordered.filter(m => m.isSigner).length,
    ordered.filter(m => m.isSigner && !m.isWritable).length,
    ordered.filter(m => !m.isSigner && !m.isWritable).length
  ];
  const bytes = [...header, ...compactU16(ordered.length)];
  for (const m of ordered) bytes.push(...solanaAddress(m.pubkey));
  bytes.push(...solanaAddress(recentBlockhash));
  bytes.push(...compactU16(instructions.length));
  for (const ix of instructions) {
    bytes.push(index.get(ix.programId), ...compactU16(ix.keys.length), ...ix.keys.map(k => index.get(k.pubkey)));
    bytes.push(...compactU16(ix.data.length), ...ix.data);
  }
  return { message: Uint8Array.from(bytes), signerCount: header[0] };
}

/** Signs a message with its only signer. Returns the wire transaction (base64) and its signature (base58, the tx id). */
export async function signMessage(message, key) {
  const signature = new Uint8Array(await crypto.subtle.sign('Ed25519', key.privateKey, message));
  const wire = new Uint8Array(1 + 64 + message.length);
  wire[0] = 1; wire.set(signature, 1); wire.set(message, 65);
  return { wire: btoa(Array.from(wire, b => String.fromCharCode(b)).join('')), signature: base58Encode(signature) };
}

/**
 * The instructions of one USDC transfer from `from` (the treasury, fee payer and authority) to `to`'s associated
 * token account, creating that account first when it does not exist (paid by the treasury), with an optional memo.
 */
export async function usdcTransferInstructions({ from, to, micros, memo, priorityMicroLamports = 0, computeUnits = 80_000 }) {
  const source = await associatedTokenAddress(from), destination = await associatedTokenAddress(to);
  const instructions = [];
  if (priorityMicroLamports > 0) {
    instructions.push({ programId: COMPUTE_BUDGET_PROGRAM, keys: [], data: [2, ...u32le(computeUnits)] });
    instructions.push({ programId: COMPUTE_BUDGET_PROGRAM, keys: [], data: [3, ...u64le(priorityMicroLamports)] });
  }
  instructions.push({ programId: ATA_PROGRAM, data: [1], keys: [
    { pubkey: from, isSigner: true, isWritable: true }, { pubkey: destination, isSigner: false, isWritable: true },
    { pubkey: to, isSigner: false, isWritable: false }, { pubkey: USDC_MINT, isSigner: false, isWritable: false },
    { pubkey: SYSTEM_PROGRAM, isSigner: false, isWritable: false }, { pubkey: TOKEN_PROGRAM, isSigner: false, isWritable: false }] });
  instructions.push({ programId: TOKEN_PROGRAM, data: [12, ...u64le(micros), USDC_DECIMALS], keys: [
    { pubkey: source, isSigner: false, isWritable: true }, { pubkey: USDC_MINT, isSigner: false, isWritable: false },
    { pubkey: destination, isSigner: false, isWritable: true }, { pubkey: from, isSigner: true, isWritable: false }] });
  if (memo) {
    instructions.push({ programId: MEMO_PROGRAM, data: [...new TextEncoder().encode(memo)],
      keys: [{ pubkey: from, isSigner: true, isWritable: false }] });
  }
  return { instructions, source, destination };
}
