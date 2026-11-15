import { createHash, createPublicKey, randomBytes, verify } from 'node:crypto';
import bs58 from 'bs58';

const accountPattern = /^(?=.{2,64}$)[a-z0-9]+(?:[-_.][a-z0-9]+)*$/;
const ticketPattern = /^[a-f0-9]{64}$/;
const hash = value => createHash('sha256').update(value).digest('hex');
const profileKey = wallet => 'near-profile:' + wallet;
export const getLinkedNearAccount = async (store, solanaId) =>
  (await store.get(profileKey(solanaId)))?.nearAccountId ?? null;
const ticketKey = ticket => 'near-ticket:' + hash(ticket);
const lifetime = 5 * 60_000;

function reject(status, message) {
  throw Object.assign(new Error(message), { status });
}

function borshString(text) {
  const data = Buffer.from(text);
  const size = Buffer.alloc(4);
  size.writeUInt32LE(data.length);
  return Buffer.concat([size, data]);
}

export function digestLinkProof({ message, nonce, recipient }) {
  const tag = Buffer.alloc(4);
  tag.writeUInt32LE(2147484061);
  return createHash('sha256').update(Buffer.concat([
    tag, borshString(message), Buffer.from(nonce, 'base64'), borshString(recipient),
    Buffer.from([0])
  ])).digest();
}

async function checkFullAccess(accountId, publicKey) {
  const response = await fetch('https://rpc.mainnet.near.org', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: AbortSignal.timeout(6000),
    body: JSON.stringify({ jsonrpc: '2.0', id: 'bakeland-near-link', method: 'query',
      params: { request_type: 'view_access_key', finality: 'final', account_id: accountId, public_key: publicKey } })
  });
  if (!response.ok) reject(503, 'NEAR key verification is unavailable');
  const result = await response.json();
  if (result.error && !['UNKNOWN_ACCESS_KEY', 'UNKNOWN_ACCOUNT'].includes(result.error.cause?.name))
    reject(503, 'NEAR key verification is unavailable');
  return result.result?.permission === 'FullAccess';
}

export function mountNearWalletLink(router, { env, db, caller, route, hasFullAccess = checkFullAccess }) {
  async function getTicket(ticket) {
    if (typeof ticket !== 'string' || !ticketPattern.test(ticket)) reject(404, 'Link request not found');
    const challenge = await db().get(ticketKey(ticket));
    if (!challenge || challenge.expiresAt <= Date.now()) reject(404, 'Link request expired');
    return challenge;
  }

  router.get('/social-links/near', route(async req => {
    const { wallet } = await caller(req);
    const nearAccountId = await getLinkedNearAccount(db(), wallet);
    return { linked: Boolean(nearAccountId), nearAccountId };
  }));

  router.post('/social-links/near/start', route(async req => {
    const { wallet, subject } = await caller(req);
    if (await db().get(profileKey(wallet))) reject(409, 'A NEAR wallet is already linked');
    const page = new URL(env.BAKELAND_NEAR_LINK_PAGE || 'about:blank');
    if (page.protocol !== 'https:' || page.username || page.password || page.search || page.hash ||
      !page.pathname.endsWith('/near-intents/social-links/near/connect')) reject(503, 'Wallet linking is unavailable');
    const ticket = randomBytes(32).toString('hex');
    page.hash = ticket;
    const challenge = {
      id: ticketKey(ticket), wallet, subject, nonce: randomBytes(32).toString('base64'), recipient: page.hostname,
      expiresAt: Date.now() + lifetime, ttl: Math.ceil((Date.now() + lifetime) / 1000),
      message: 'Link your NEAR wallet to Bakeland Solana account ' + wallet + '. This approval does not move funds.'
    };
    await db().create(challenge);
    return { ticket, walletUrl: page.href };
  }));

  router.get('/social-links/near/connect', (req, res) => {
    if (env.BAKELAND_NEAR_ENABLED !== 'true') return res.sendStatus(404);
    const cspNonce = randomBytes(18).toString('base64');
    res.set({ 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer',
      'Content-Security-Policy': "default-src 'none'; script-src 'nonce-" + cspNonce + "' https://cdn.jsdelivr.net; connect-src 'self' https:; img-src https: data:; style-src 'unsafe-inline'; frame-src https: blob:; frame-ancestors 'none'; base-uri 'none'" });
    res.type('html').send(`<!doctype html><meta charset="utf-8"><title>Bakeland wallet linking</title>
<p id="status" role="status">Link a NEAR wallet to use Bakeland features that require it.</p>
<button id="link" disabled>Connect NEAR wallet</button>
<script type="importmap" nonce="${cspNonce}">{"imports":{"near-connect":"https://cdn.jsdelivr.net/npm/@hot-labs/near-connect@0.12.0/+esm"},"integrity":{"https://cdn.jsdelivr.net/npm/@hot-labs/near-connect@0.12.0/+esm":"sha384-f3y9zSYOtg1HHwq/4/hbu8xZ//yn5j2mt8zREsCiLSbSnDHqEPwHEesM83PKy8Lj"}}</script>
<script type="module" nonce="${cspNonce}">
import { NearConnector } from 'near-connect';
const ticket = location.hash.slice(1);
history.replaceState(null, '', location.pathname);
const button = document.getElementById('link');
const status = document.getElementById('status');
const request = async (path, body) => {
  const response = await fetch(location.pathname + '/' + path, {method:'POST',
    headers:{'Content-Type':'application/json'},body:JSON.stringify({ticket,...body})});
  if (!response.ok) throw new Error('Wallet link request failed');
  return response.json();
};
try {
  const challenge = await request('challenge');
  const connector = new NearConnector({network:'mainnet', features:{signInAndSignMessage:true},
    cspNonce:document.querySelector('script[type="module"]').nonce});
  connector.on('wallet:signInAndSignMessage', async ({accounts}) => {
    try {
      const {accountId, signedMessage} = accounts[0];
      await request('proof', {accountId, publicKey:signedMessage.publicKey, signature:signedMessage.signature});
      status.textContent = 'Wallet approved. Return to Bakeland to confirm the link.';
      button.disabled = true;
    } catch { status.textContent = 'Approval could not be verified. Start a new link in Bakeland.'; }
  });
  button.disabled = false;
  button.onclick = async () => {
    try {
      await connector.connect({signMessageParams:{message:challenge.message, recipient:challenge.recipient,
        nonce:Uint8Array.from(atob(challenge.nonce), c => c.charCodeAt(0))}});
    } catch { status.textContent = 'Wallet connection did not complete. You can try again.'; }
  };
} catch { status.textContent = 'This link has expired. Return to Bakeland to start again.'; }
</script>`);
  });

  router.post('/social-links/near/connect/challenge', route(async req => {
    const { message, recipient, nonce } = await getTicket(req.body?.ticket);
    return { message, recipient, nonce };
  }));

  router.post('/social-links/near/connect/proof', route(async req => {
    const { ticket, accountId, publicKey, signature } = req.body ?? {};
    const challenge = await getTicket(ticket);
    if (typeof accountId !== 'string' || !accountPattern.test(accountId) ||
      typeof publicKey !== 'string' || !/^ed25519:[1-9A-HJ-NP-Za-km-z]{32,44}$/.test(publicKey) ||
      typeof signature !== 'string' || !/^[A-Za-z0-9+/]{86}==$/.test(signature))
      reject(400, 'Invalid wallet approval');
    const key = Buffer.from(bs58.decode(publicKey.slice(8)));
    if (key.length !== 32) reject(400, 'Invalid NEAR public key');
    const verifier = createPublicKey({
      key: Buffer.concat([Buffer.from('302a300506032b6570032100', 'hex'), key]), type: 'spki', format: 'der'
    });
    if (!verify(null, digestLinkProof(challenge), verifier, Buffer.from(signature, 'base64')) ||
      !await hasFullAccess(accountId, publicKey)) reject(403, 'Wallet approval could not be verified');
    const result = await db().create({ id: challenge.id + ':proof', accountId, publicKey, expiresAt: challenge.expiresAt, ttl: challenge.ttl });
    if (result.accountId !== accountId) reject(409, 'A different wallet was already approved');
    return { verified: true };
  }));

  router.post('/social-links/near/pending', route(async req => {
    const { wallet, subject } = await caller(req);
    const challenge = await getTicket(req.body?.ticket);
    if (challenge.wallet !== wallet || challenge.subject !== subject) reject(404, 'Link request not found');
    const proof = await db().get(challenge.id + ':proof');
    return { nearAccountId: proof?.accountId };
  }));

  router.post('/social-links/near/confirm', route(async req => {
    const { wallet, subject } = await caller(req);
    const { ticket, nearAccountId } = req.body ?? {};
    const existing = await db().get(profileKey(wallet));
    if (existing?.nearAccountId === nearAccountId && existing) return { linked: true, nearAccountId };
    const challenge = await getTicket(ticket);
    if (challenge.wallet !== wallet || challenge.subject !== subject) reject(404, 'Link request not found');
    const proof = await db().get(challenge.id + ':proof');
    if (!proof || proof.accountId !== nearAccountId) reject(409, 'Confirm the approved wallet account');
    if (!await hasFullAccess(nearAccountId, proof.publicKey)) reject(403, 'The approved NEAR key is no longer active');
    try {
      await db().link(challenge, proof, { id: profileKey(wallet), nearAccountId, linkedAt: Date.now() },
        { id: 'near-account:' + nearAccountId, wallet });
    } catch (error) {
      if (error.name !== 'TransactionCanceledException') throw error;
      if ((await db().get(profileKey(wallet)))?.nearAccountId !== nearAccountId)
        reject(409, 'A wallet link already exists or the request expired');
    }
    return { linked: true, nearAccountId };
  }));
}
