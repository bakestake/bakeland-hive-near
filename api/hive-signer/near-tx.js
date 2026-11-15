// Minimal NEAR transaction building for the Hive escrow signer (14 Nov 2026): base58, the Borsh layout of a
// Transaction with FunctionCall actions, and Ed25519 signing through WebCrypto (Workers and Node). Only what an
// ft_transfer on wrap.near needs; no SDK, so the code that touches the escrow key stays small enough to read.

const ALPHABET = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';

export function base58Decode(text) {
  let n = 0n;
  for (const c of String(text)) {
    const i = ALPHABET.indexOf(c);
    if (i < 0) throw new Error('Invalid base58');
    n = n * 58n + BigInt(i);
  }
  const bytes = [];
  while (n > 0n) { bytes.unshift(Number(n & 0xffn)); n >>= 8n; }
  for (const c of String(text)) { if (c !== '1') break; bytes.unshift(0); }
  return Uint8Array.from(bytes);
}

export function base58Encode(bytes) {
  let n = 0n;
  for (const b of bytes) n = (n << 8n) + BigInt(b);
  let out = '';
  while (n > 0n) { out = ALPHABET[Number(n % 58n)] + out; n /= 58n; }
  for (const b of bytes) { if (b !== 0) break; out = '1' + out; }
  return out;
}

const b64url = bytes => btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
export const toBase64 = bytes => btoa(Array.from(bytes, b => String.fromCharCode(b)).join(''));

class BorshWriter {
  #parts = [];
  u8(v) { this.#parts.push(Uint8Array.of(v)); }
  u32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, v, true); this.#parts.push(b); }
  u64(v) { const b = new Uint8Array(8); new DataView(b.buffer).setBigUint64(0, BigInt(v), true); this.#parts.push(b); }
  u128(v) {
    let x = BigInt(v);
    if (x < 0n) throw new Error('u128 must not be negative');
    const b = new Uint8Array(16);
    for (let i = 0; i < 16; i++) { b[i] = Number(x & 0xffn); x >>= 8n; }
    if (x !== 0n) throw new Error('u128 overflow');
    this.#parts.push(b);
  }
  fixed(bytes) { this.#parts.push(Uint8Array.from(bytes)); }
  bytes(bytes) { this.u32(bytes.length); this.fixed(bytes); }
  string(text) { this.bytes(new TextEncoder().encode(text)); }
  result() {
    const out = new Uint8Array(this.#parts.reduce((n, p) => n + p.length, 0));
    let at = 0;
    for (const p of this.#parts) { out.set(p, at); at += p.length; }
    return out;
  }
}

/** Parses a NEAR secret key ("ed25519:<base58 of seed + public key>") into a WebCrypto signing key. */
export async function importNearKey(secret) {
  const [type, data] = String(secret || '').split(':');
  if (type !== 'ed25519' || !data) throw new Error('The escrow key must be an ed25519:... secret key');
  const raw = base58Decode(data);
  if (raw.length !== 64) throw new Error('The escrow key has the wrong length');
  const seed = raw.slice(0, 32), publicKey = raw.slice(32);
  let privateKey;
  try {
    privateKey = await crypto.subtle.importKey('jwk',
      { kty: 'OKP', crv: 'Ed25519', d: b64url(seed), x: b64url(publicKey), ext: false }, { name: 'Ed25519' }, false, ['sign']);
  } catch {
    // Some WebCrypto implementations reject a seed whose public half does not match at import.
    throw new Error('The escrow key does not match its public key');
  }
  // A key whose public half does not match its seed would sign for a different account key: refuse it.
  const check = new TextEncoder().encode('bakeland-hive-signer key check');
  const verifyKey = await crypto.subtle.importKey('raw', publicKey, { name: 'Ed25519' }, false, ['verify']);
  if (!await crypto.subtle.verify('Ed25519', verifyKey, await crypto.subtle.sign('Ed25519', privateKey, check), check)) {
    throw new Error('The escrow key does not match its public key');
  }
  return { privateKey, publicKey, publicKeyText: 'ed25519:' + base58Encode(publicKey) };
}

export function functionCall(methodName, args, gas, deposit) {
  return { methodName, args: new TextEncoder().encode(JSON.stringify(args)), gas: BigInt(gas), deposit: BigInt(deposit) };
}

/** Borsh: signer_id, public_key (ed25519), nonce, receiver_id, block_hash, actions (FunctionCall = variant 2). */
export function encodeTransaction({ signerId, publicKey, nonce, receiverId, blockHash, actions }) {
  if (publicKey.length !== 32) throw new Error('Public key must be 32 bytes');
  if (blockHash.length !== 32) throw new Error('Block hash must be 32 bytes');
  const w = new BorshWriter();
  w.string(signerId);
  w.u8(0); w.fixed(publicKey);
  w.u64(nonce);
  w.string(receiverId);
  w.fixed(blockHash);
  w.u32(actions.length);
  for (const action of actions) {
    w.u8(2);
    w.string(action.methodName);
    w.bytes(action.args);
    w.u64(action.gas);
    w.u128(action.deposit);
  }
  return w.result();
}

/** Signs sha256(transaction). Returns the SignedTransaction bytes and the transaction hash (base58). */
export async function signTransaction(transaction, key) {
  const encoded = encodeTransaction(transaction);
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encoded));
  const signature = new Uint8Array(await crypto.subtle.sign('Ed25519', key.privateKey, digest));
  const w = new BorshWriter();
  w.fixed(encoded);
  w.u8(0); w.fixed(signature);
  return { signed: w.result(), hash: base58Encode(digest) };
}
