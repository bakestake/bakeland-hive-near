// The Hive agent (15 Nov 2026): one NEAR Intents Agent API agent, owned by the studio's EVM wallet, that receives the
// Hive droids' earnings. Creating it needs one owner signature, so this module serves a one-time signing page:
//   POST /hive/agent/session   (service key)  -> a single-use page URL, valid 30 minutes
//   GET  /hive/agent/sign#<t>                 -> the page: connect an EVM wallet (Rabby, MetaMask), confirm the owner key
//                                                with a message signature (moves nothing), read the agent's terms (the
//                                                API's preview, ending with NEAR's risk notice), accept, sign typed data
//   POST /hive/agent/sign/generate {t, address, chainId, signature}  -> POST /v1/generate-intent (agent_create)
//   POST /hive/agent/sign/submit   {t, accepted, signed_data}        -> POST /v1/submit-intent, then GET /v1/status
//   GET  /hive/agent           (service key)  -> the studio's Hive agents (GET /v1/agents?external_user_id=...)
// EVM, not NEAR: a NEAR owner must sign agent_create as a NEP-366 delegate action, which Meteor through NEAR Connect
// did not offer (it also shared no public key). The partner API key stays on the server.
import { createHash, randomBytes } from 'node:crypto';
import { SigningKey, computeAddress, hashMessage } from 'ethers';

const SESSION_MS = 30 * 60 * 1000;
const EXTERNAL_ID = 'bakeland-hive';
const OWNER_CHAIN_ID = 1;   // Ethereum mainnet: the chain id in the owner record and the typed-data domain

/** The agent's rules at creation: it can receive deposits and spend nothing until the owner signs a policy update. */
export const HIVE_AGENT_POLICY = Object.freeze({
  frozen: false,
  actions: [],
  confidential: true,
  owner_approval: true,
  assets: 'any',
  limits: {},
  max_actions_per_hour: null,
  destinations: { mode: 'only', list: [] },
  budget: { daily_usd: null, weekly_usd: null, monthly_usd: null },
  timelock_ms: 0
});

/** The message the owner signs to prove which key will own the agent (bound to this signing link). */
export const ownerKeyMessage = t =>
  `Bakeland Hive: this wallet will own the Bakeland Hive agent.\nThis signature moves nothing.\nLink: ${t.slice(0, 16)}`;

/** The uncompressed secp256k1 public key (0x + 128 hex) behind an EIP-191 signature, checked against the address. */
export function recoverOwnerKey(message, signature, address) {
  const uncompressed = SigningKey.recoverPublicKey(hashMessage(message), signature);   // 0x04 + 128 hex
  if (computeAddress(uncompressed).toLowerCase() !== String(address).toLowerCase()) throw new Error('Signature does not match the wallet');
  return '0x' + uncompressed.slice(4).toLowerCase();
}

export function mountHiveAgent(router, { env, db, route, requireServiceKey, fail, agentApiBase, fetchImpl = fetch }) {
  async function agentApi(method, path, body, idempotencyKey) {
    // Trimmed: a secret piped in from a shell can carry a trailing newline, which the API refuses as invalid_api_key.
    const apiKey = String(env.NEAR_INTENTS_AGENT_API_KEY || '').trim();
    if (!apiKey) fail(503, 'The Agent API key is not configured');
    const response = await fetchImpl(agentApiBase() + path, {
      method, redirect: 'error', signal: AbortSignal.timeout(40_000),
      headers: { 'X-API-Key': apiKey, 'Content-Type': 'application/json',
        ...(idempotencyKey && { 'Idempotency-Key': idempotencyKey }) },
      body: body && JSON.stringify(body)
    });
    const json = await response.json().catch(() => null);
    if (!response.ok) {
      const e = json?.errors?.[0];
      fail(response.status >= 500 ? 502 : 400, `Agent API ${e?.code || response.status}: ${String(e?.detail || e?.title || '').slice(0, 200)}`);
    }
    return json;
  }

  const sessionKey = t => 'hive-agent-session:' + t;
  async function session(t) {
    if (typeof t !== 'string' || !/^[a-f0-9]{64}$/.test(t)) fail(400, 'Invalid link');
    const s = await db().get(sessionKey(t));
    if (!s || s.expiresAt < Date.now()) fail(410, 'This signing link has expired. Ask for a new one.');
    return s;
  }

  router.post('/hive/agent/session', route(async req => {
    requireServiceKey(req);
    const t = randomBytes(32).toString('hex');
    const expiresAt = Date.now() + SESSION_MS;
    await db().create({ id: sessionKey(t), expiresAt, ttl: Math.ceil(expiresAt / 1000) });
    const base = new URL(req.originalUrl || req.url, `https://${req.get('host')}`);
    base.pathname = base.pathname.replace(/\/session$/, '/sign'); base.search = '';
    return { url: base.href + '#' + t, expiresAt: new Date(expiresAt).toISOString() };
  }));

  router.get('/hive/agent', route(async req => {
    requireServiceKey(req);
    const list = await agentApi('GET', '/v1/agents?' + new URLSearchParams({ external_user_id: EXTERNAL_ID }));
    return { agents: (list?.data || []).map(a => ({ agent_id: a.agent_id ?? a.id, name: a.name, status: a.status,
      owner: a.owner, near_account_id: a.wallet?.near_account_id })) };
  }));

  router.post('/hive/agent/sign/generate', route(async req => {
    const { t, address, signature } = req.body ?? {};
    const s = await session(t);
    if (typeof address !== 'string' || !/^0x[0-9a-fA-F]{40}$/.test(address) ||
      typeof signature !== 'string' || !/^0x[0-9a-fA-F]{130}$/.test(signature)) fail(400, 'Invalid wallet confirmation');
    let publicKey;
    try { publicKey = recoverOwnerKey(ownerKeyMessage(t), signature, address); }
    catch { fail(400, 'The confirmation signature does not belong to this wallet'); }
    const owner = { type: 'evm', address: address.toLowerCase(), chain_id: OWNER_CHAIN_ID, public_key: publicKey };
    const generated = await agentApi('POST', '/v1/generate-intent', {
      type: 'agent_create', name: 'Bakeland Hive', external_user_id: EXTERNAL_ID, owner, policy: HIVE_AGENT_POLICY
    }, 'hive-agent-create-' + t.slice(0, 40));
    await db().create({ id: sessionKey(t) + ':intent', correlationId: generated.correlation_id, address: owner.address,
      standard: generated.intent?.standard, ttl: s.ttl });
    return { correlation_id: generated.correlation_id, signer: generated.signer, intent: generated.intent,
      preview: generated.preview, expires_at: generated.expires_at };
  }));

  router.post('/hive/agent/sign/submit', route(async req => {
    const { t, accepted, signed_data: signedData } = req.body ?? {};
    await session(t);
    const intent = await db().get(sessionKey(t) + ':intent');
    if (!intent) fail(409, 'Generate the request first');
    if (accepted !== true) fail(400, 'The owner terms must be accepted before signing');
    if (!signedData || typeof signedData !== 'object' || signedData.standard !== intent.standard) fail(400, 'Invalid signature');
    // Evidence of acceptance, kept as the Agent API's owner terms require.
    await db().create({ id: sessionKey(t) + ':accepted', at: new Date().toISOString(), owner: intent.address,
      correlationId: intent.correlationId, signedSha256: createHash('sha256').update(JSON.stringify(signedData)).digest('hex'),
      userAgent: String(req.get('user-agent') || '').slice(0, 200) });
    await agentApi('POST', '/v1/submit-intent', { type: 'agent_create', correlation_id: intent.correlationId, signed_data: signedData });
    const status = await agentApi('GET', '/v1/status?' + new URLSearchParams({ correlation_id: intent.correlationId, wait_ms: '25000' }));
    return { status: status?.status, agent_id: status?.agent_id ?? status?.details?.agent_id ?? null,
      failure_code: status?.failure_code ?? null };
  }));

  router.get('/hive/agent/sign', (req, res) => {
    const cspNonce = randomBytes(18).toString('base64');
    res.set({ 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer',
      'Content-Security-Policy': "default-src 'none'; script-src 'nonce-" + cspNonce + "'; connect-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'" });
    res.type('html').send(`<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Create the Bakeland Hive agent</title>
<style>body{font:15px/1.5 system-ui;max-width:720px;margin:32px auto;padding:0 16px;color:#1d1d1f}pre{white-space:pre-wrap;background:#f4f4f6;padding:12px;border-radius:8px;max-height:360px;overflow:auto}button{font:inherit;padding:8px 16px;margin-top:8px}#status{font-weight:600}</style>
<h1>Create the Bakeland Hive agent</h1>
<p>This creates one NEAR Intents agent owned by your EVM wallet. It receives the Hive droids' earnings and can spend nothing until you sign a rule change. You are its owner for good: the owner cannot be changed or recovered.</p>
<p id="status" role="status">Connect the EVM wallet (Rabby or MetaMask) that will own the agent.</p>
<button id="connect" disabled>Connect wallet</button>
<div id="review" hidden><h2>Terms and preview from the Agent API</h2><pre id="preview"></pre>
<label><input type="checkbox" id="accept"> I have read the Owner Terms, OutLayer's terms and the risk notice above, and I accept them as the agent's owner.</label><br>
<button id="sign" disabled>Sign with my wallet</button></div>
<script nonce="${cspNonce}">
const t = location.hash.slice(1);
history.replaceState(null, '', location.pathname);
const $ = id => document.getElementById(id);
const say = text => { $('status').textContent = text; };
const post = async (path, body) => {
  const r = await fetch(location.pathname + '/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ t, ...body }) });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || 'Request failed (' + r.status + ')');
  return j;
};
const hex = s => '0x' + Array.from(new TextEncoder().encode(s), b => b.toString(16).padStart(2, '0')).join('');
const message = ${JSON.stringify('Bakeland Hive: this wallet will own the Bakeland Hive agent.\nThis signature moves nothing.\nLink: ')} + t.slice(0, 16);
let generated, address;
$('connect').onclick = async () => {
  try {
    if (!window.ethereum) throw new Error('No EVM wallet found in this browser. Open this page where Rabby or MetaMask is installed.');
    [address] = await window.ethereum.request({ method: 'eth_requestAccounts' });
    await window.ethereum.request({ method: 'wallet_switchEthereumChain', params: [{ chainId: '0x1' }] }).catch(() => {});
    say('Confirm in your wallet which address will own the agent (this moves nothing)...');
    const signature = await window.ethereum.request({ method: 'personal_sign', params: [hex(message), address] });
    say('Preparing the agent for ' + address + '...');
    generated = await post('generate', { address, signature });
    $('preview').textContent = (generated.preview?.summary || '') + '\\n\\n' + JSON.stringify(generated.preview, null, 2);
    $('review').hidden = false;
    $('connect').disabled = true;
    say('Read the terms, then accept and sign with ' + address + '.');
  } catch (e) { say(e.message || String(e)); }
};
$('accept').onchange = () => { $('sign').disabled = !$('accept').checked; };
$('sign').onclick = async () => {
  $('sign').disabled = true;
  try {
    const { standard, payload } = generated.intent;
    if (standard !== 'eip712') throw new Error('Unexpected signing standard ' + standard);
    const signature = await window.ethereum.request({ method: 'eth_signTypedData_v4', params: [address, JSON.stringify(payload)] });
    say('Submitting and waiting for the agent...');
    const done = await post('submit', { accepted: true, signed_data: { standard, payload, signature } });
    say(done.status === 'SUCCESS' ? 'The Hive agent is created' + (done.agent_id ? ': ' + done.agent_id : '') + '. You can close this page.'
      : 'Submitted. Status: ' + done.status + (done.failure_code ? ' (' + done.failure_code + ')' : '') + '. Tell your developer.');
  } catch (e) { say(e.message || String(e)); $('sign').disabled = false; }
};
$('connect').disabled = false;
</script>`);
  });
}
