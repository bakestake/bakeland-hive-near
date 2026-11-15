// Player session verification, as the routes expect it. Bakeland's own implementation lives in its private API; this
// public version checks an RS256 JWT against the issuer's JWKS (`<issuer>/.well-known/jwks.json`) with node:crypto.
// The session must carry the player's verified Solana wallet in a `wallet` claim.
import { createPublicKey, verify } from 'node:crypto';
import { PublicKey } from '@solana/web3.js';

let cached;

export async function authenticate(header, { issuer, audience, getKeys } = {}) {
  if (!issuer || !audience) throw new Error('Configure the session issuer and audience');
  if (!header?.startsWith('Bearer ') || header.length > 16000) throw new Error('Bearer token required');
  const parts = header.slice(7).split('.');
  if (parts.length !== 3) throw new Error('Invalid token');
  const protectedHeader = JSON.parse(Buffer.from(parts[0], 'base64url'));
  if (protectedHeader.alg !== 'RS256' || !protectedHeader.kid) throw new Error('Unsupported signing key');
  getKeys ??= async () => {
    if (!cached || cached.issuer !== issuer || cached.until < Date.now()) {
      const response = await fetch(`${issuer}/.well-known/jwks.json`, { signal: AbortSignal.timeout(5000) });
      if (!response.ok) throw new Error('Signing keys unavailable');
      cached = { issuer, until: Date.now() + 300_000, keys: (await response.json()).keys };
    }
    return cached.keys;
  };
  const jwk = (await getKeys()).find(k => k.kid === protectedHeader.kid);
  if (!jwk) throw new Error('Unknown signing key');
  const ok = verify('RSA-SHA256', Buffer.from(parts[0] + '.' + parts[1]), createPublicKey({ key: jwk, format: 'jwk' }),
    Buffer.from(parts[2], 'base64url'));
  if (!ok) throw new Error('Invalid signature');
  const claims = JSON.parse(Buffer.from(parts[1], 'base64url'));
  const now = Math.floor(Date.now() / 1000);
  const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (claims.iss !== issuer || !audiences.includes(audience) || !(claims.exp > now) || (claims.nbf && claims.nbf > now + 60)) {
    throw new Error('Invalid session');
  }
  return claims;
}

export function walletIdentity(claims) {
  if (typeof claims?.wallet !== 'string' || !claims.wallet) throw new Error('JWT wallet claim is required');
  return new PublicKey(claims.wallet).toBase58();
}
