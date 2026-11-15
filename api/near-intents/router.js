import express from 'express';
import { createHash, createPublicKey, timingSafeEqual, verify as verifySignature } from 'node:crypto';
import { Connection, Keypair, Message, PublicKey, Transaction, TransactionInstruction, VersionedTransaction } from '@solana/web3.js';
import { createAssociatedTokenAccountIdempotentInstruction, createTransferCheckedInstruction, getAssociatedTokenAddressSync } from '@solana/spl-token';
import { DynamoDBClient } from '@aws-sdk/client-dynamodb';
import { DeleteCommand, DynamoDBDocumentClient, GetCommand, PutCommand, ScanCommand, TransactWriteCommand } from '@aws-sdk/lib-dynamodb';
import { mountNearWalletLink } from './wallet-link.js';
import { createHiveSettlement, spacetimeHttp } from './hive-settlement.js';
import { mountHiveAgent } from './hive-agent.js';
import { createHiveSolana, makeHivePaymentTransaction } from './hive-sol.js';
import bs58 from 'bs58';
import { authenticate, walletIdentity } from './auth.js';

const USDC = new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v');
const MEMO = new PublicKey('MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr');
const SOLANA_USDC_ASSET = 'nep141:sol-5ce3bf3a31af18be40ba30f721101b4341690186.omft.near';
const operationId = /^[a-zA-Z0-9_-]{16,96}$/;
const amountFormat = /^[1-9][0-9]{0,19}$/;
const outputFormat = /^[1-9][0-9]{0,39}$/;
const statusPath = '/v0/status?';
const mainnetGenesis = '5eykt4UsFv8P8NJdTREpY1vzqKqZKvdpKuc147dw2N9d';

function fail(status, message) {
  throw Object.assign(new Error(message), { status });
}

function storage(table) {
  const db = DynamoDBDocumentClient.from(new DynamoDBClient({}));
  return {
    async link(challenge, proof, profile, reverse) {
      await db.send(new TransactWriteCommand({ TransactItems: [
        { Delete: { TableName: table, Key: { id: challenge.id }, ConditionExpression: 'expiresAt > :now', ExpressionAttributeValues: { ':now': Date.now() } } },
        { Delete: { TableName: table, Key: { id: proof.id }, ConditionExpression: 'accountId = :account', ExpressionAttributeValues: { ':account': profile.nearAccountId } } },
        ...[profile, reverse].map(Item => ({ Put: { TableName: table, Item, ConditionExpression: 'attribute_not_exists(id)' } }))
      ] }));
    },
    async get(id) {
      return (await db.send(new GetCommand({ TableName: table, Key: { id }, ConsistentRead: true }))).Item;
    },
    async remove(id) {
      await db.send(new DeleteCommand({ TableName: table, Key: { id } }));
    },
    /** Every item whose id starts with `prefix` (small work queues such as Hive payments in flight). */
    async scan(prefix) {
      const items = [];
      let ExclusiveStartKey;
      do {
        const page = await db.send(new ScanCommand({ TableName: table, FilterExpression: 'begins_with(id, :p)',
          ExpressionAttributeValues: { ':p': prefix }, ExclusiveStartKey }));
        items.push(...(page.Items ?? []));
        ExclusiveStartKey = page.LastEvaluatedKey;
      } while (ExclusiveStartKey && items.length < 500);
      return items;
    },
    async create(item) {
      try {
        await db.send(new PutCommand({ TableName: table, Item: item, ConditionExpression: 'attribute_not_exists(id)' }));
        return item;
      } catch (error) {
        if (error.name !== 'ConditionalCheckFailedException') throw error;
        return this.get(item.id);
      }
    }
  };
}

async function upstream(base, path, headers, body) {
  const response = await fetch(base + path, {
    method: body ? 'POST' : 'GET',
    headers: { ...headers, 'Content-Type': 'application/json' },
    body: body && JSON.stringify(body),
    redirect: 'error',
    signal: AbortSignal.timeout(8000)
  });
  if (!response.ok) fail(502, 'NEAR Intents request failed');
  return response.json();
}

export function makeDepositTransaction(wallet, quote, blockhash) {
  const owner = new PublicKey(wallet);
  const deposit = new PublicKey(quote.depositAddress);
  const source = getAssociatedTokenAddressSync(USDC, owner);
  const target = getAssociatedTokenAddressSync(USDC, deposit, true);
  const tx = new Transaction({ feePayer: owner, blockhash, lastValidBlockHeight: quote.lastValidBlockHeight });
  tx.add(createAssociatedTokenAccountIdempotentInstruction(owner, target, deposit, USDC));
  tx.add(createTransferCheckedInstruction(source, USDC, target, owner, BigInt(quote.amountIn), 6));
  if (quote.depositMemo) tx.add(new TransactionInstruction({
    programId: MEMO, keys: [{ pubkey: owner, isSigner: true, isWritable: false }],
    data: Buffer.from(quote.depositMemo)
  }));
  return tx;
}

export function validateSignedDeposit(encoded, expectedMessage) {
  if (typeof encoded !== 'string' || encoded.length > 2000) fail(400, 'Invalid signed transaction');
  try {
    const signed = VersionedTransaction.deserialize(Buffer.from(encoded, 'base64'));
    if (signed.version !== 'legacy' || signed.signatures.length !== 1) fail(400, 'Expected one legacy signature');
    const expected = Message.from(Buffer.from(expectedMessage, 'base64'));
    const actual = signed.message;
    // Solana.Unity can reorder accounts with equal privileges. Compare instructions in execution
    // order and compare the complete account/privilege set, then verify the actual wire message.
    if (JSON.stringify(messageIntent(actual)) !== JSON.stringify(messageIntent(expected)))
      fail(400, 'Deposit instructions changed');
    const key = createPublicKey({
      key: Buffer.concat([Buffer.from('302a300506032b6570032100', 'hex'), actual.accountKeys[0].toBuffer()]),
      format: 'der', type: 'spki'
    });
    if (verifySignature(null, actual.serialize(), key, signed.signatures[0])) return bs58.encode(signed.signatures[0]);
  } catch { /* Invalid transaction encoding or signature. */ }
  fail(400, 'Signed transaction does not match the prepared deposit');
}

function messageIntent(message) {
  const address = i => message.accountKeys[i].toBase58();
  return {
    payer: address(0), blockhash: message.recentBlockhash,
    accounts: message.accountKeys.map((_, i) =>
      [address(i), message.isAccountSigner(i), message.isAccountWritable(i)]).sort(),
    instructions: message.instructions.map(ix => [address(ix.programIdIndex), ix.accounts.map(address), ix.data])
  };
}

export function createNearIntentsRouter({ env = process.env, store, rpc, oneClick, agentApi, verify = authenticate, hasFullAccess,
  spacetime, hiveEscrow } = {}) {
  const router = express.Router();
  const route = handler => async (req, res) => {
    res.set('Cache-Control', 'no-store');
    try {
      if (env.BAKELAND_NEAR_ENABLED !== 'true') fail(404, 'NEAR Intents is disabled');
      res.json(await handler(req));
    } catch (error) {
      res.status(error.status >= 400 && error.status <= 599 ? error.status : 502)
        .json({ error: error.status ? error.message : 'NEAR Intents operation unavailable' });
    }
  };
  const db = () => store ??= storage(env.BAKELAND_NEAR_TABLE);
  // The API's own Solana mainnet RPC secret unless a NEAR-specific one is set.
  const solana = () => rpc ??= new Connection(env.BAKELAND_NEAR_SOLANA_RPC || env.SOLANA_MAINNET_RPC_URL, 'confirmed');
  let genesis;
  async function requireMainnet() {
    genesis ??= solana().getGenesisHash().catch(error => { genesis = undefined; throw error; });
    if (await genesis !== mainnetGenesis) fail(503, 'Solana mainnet RPC required');
  }
  const click = (path, body) => oneClick ? oneClick(path, body) : upstream('https://1click.chaindefuser.com', path,
    env.NEAR_1CLICK_JWT ? { Authorization: 'Bearer ' + env.NEAR_1CLICK_JWT } : {}, body);
  // The Agent API deployment: keys only work on the host whose portal issued them (14 Nov 2026: the studio's key is from
  // partners.demo.agentsonintents.com, so staging uses api.demo.agentsonintents.com).
  const agentApiBase = () => (env.NEAR_INTENTS_AGENT_API_URL || 'https://api.agentsonintents.com').replace(/\/+$/, '');
  const agent = (path, body, key) => {
    if (agentApi) return agentApi(path, body, key);
    if (!env.NEAR_INTENTS_AGENT_API_KEY) fail(503, 'Agent deposits are unavailable');
    return upstream(agentApiBase(), path, {
      'X-API-Key': String(env.NEAR_INTENTS_AGENT_API_KEY).trim(), ...(key && { 'Idempotency-Key': key })
    }, body);
  };
  // Hive Link droid jobs: funding confirmation and the escrow's settlements (hive-settlement.js).
  const hive = createHiveSettlement({ env, db, click, agent, spacetime, escrow: hiveEscrow,
    solanaPayments: ({ stdb }) => createHiveSolana({ env, db, solana, agent, stdb, escrow: hiveEscrow }) });
  router.hive = hive;
  async function caller(req) {
    try {
      const claims = await verify(req.get('Authorization'), {
        issuer: env.BAKELAND_NEAR_AUTH_ISSUER || 'https://userdb.bakeland.xyz',
        audience: env.BAKELAND_NEAR_AUTH_AUDIENCE || 'bakeland-spacetime'
      });
      return { subject: claims.sub, wallet: walletIdentity(claims) };
    } catch { fail(401, 'A verified Bakeland wallet session is required'); }
  }
  function key(owner, id) {
    if (typeof id !== 'string' || !operationId.test(id)) fail(400, 'Invalid operation ID');
    return createHash('sha256').update(JSON.stringify([owner, id])).digest('hex');
  }
  function requireServiceKey(req) {
    const expected = Buffer.from(env.BAKELAND_NEAR_WORKER_KEY || '');
    const supplied = Buffer.from(req.get('x-api-key') || '');
    if (expected.length < 32 || supplied.length !== expected.length || !timingSafeEqual(expected, supplied))
      fail(401, 'Service authentication required');
  }
  function requireWorker(req) {
    requireServiceKey(req);
    const workers = JSON.parse(env.BAKELAND_NEAR_WORKERS || '{}');
    const worker = Object.hasOwn(workers, req.params.worker) && workers[req.params.worker];
    if (!worker?.agentId || !worker.accountId) fail(404, 'Worker wallet not configured');
    return worker;
  }
  async function load(req) {
    const owner = await caller(req);
    const id = key([owner.subject, owner.wallet], req.params.id);
    const record = await db().get(id);
    if (!record) fail(404, 'Quote not found');
    return record;
  }
  function boundedAmount(amount) {
    const cap = env.BAKELAND_NEAR_MAX_USDC_ATOMIC || '10000000';
    if (typeof amount !== 'string' || !amountFormat.test(amount) || !amountFormat.test(cap)
      || BigInt(amount) > BigInt(cap) || BigInt(amount) > 18446744073709551615n) fail(400, 'Amount is outside the configured limit');
  }
  function summary(record) {
    return {
      operationId: record.operationId, wallet: record.wallet, amountIn: record.amountIn,
      amountOut: record.amountOut, minAmountOut: record.minAmountOut,
      destinationAsset: record.destinationAsset, recipient: record.recipient,
      expiresAt: record.expiresAt
    };
  }

  router.post('/quotes', route(async req => {
    const owner = await caller(req);
    const { operationId: requestedId, amountIn, recipient, dry = true } = req.body ?? {};
    const id = key([owner.subject, owner.wallet], requestedId);
    boundedAmount(amountIn);
    if (typeof dry !== 'boolean' || typeof recipient !== 'string' ||
      !/^(?=.{2,64}$)[a-z0-9]+(?:[-_.][a-z0-9]+)*$/.test(recipient)) fail(400, 'Invalid NEAR recipient or quote mode');
    const existing = dry ? null : await db().get(id);
    if (existing) {
      if (existing.amountIn !== amountIn || existing.recipient !== recipient) fail(409, 'Operation ID already used');
      return summary(existing);
    }
    const destinationAsset = 'nep141:wrap.near';
    const deadline = new Date(Date.now() + 10 * 60_000).toISOString();
    const { quote } = await click('/v0/quote', {
      dry, swapType: 'EXACT_INPUT', slippageTolerance: 100, originAsset: SOLANA_USDC_ASSET, destinationAsset,
      amount: amountIn, depositType: 'ORIGIN_CHAIN', recipient, recipientType: 'DESTINATION_CHAIN',
      refundTo: owner.wallet, refundType: 'ORIGIN_CHAIN', deadline
    });
    if (quote?.amountIn !== amountIn || !outputFormat.test(quote?.amountOut ?? '') ||
      !outputFormat.test(quote?.minAmountOut ?? '') || BigInt(quote.minAmountOut) > BigInt(quote.amountOut))
      fail(502, 'Unexpected quote amounts');
    const record = {
      id, operationId: requestedId, wallet: owner.wallet, amountIn, recipient, destinationAsset,
      amountOut: quote.amountOut, minAmountOut: quote.minAmountOut, expiresAt: quote.deadline || deadline
    };
    if (dry) return summary(record);
    if (typeof quote.depositAddress !== 'string' || !Number.isFinite(Date.parse(record.expiresAt))
      || (quote.depositMemo != null && (typeof quote.depositMemo !== 'string' || quote.depositMemo.length > 128)))
      fail(502, 'Invalid deposit instructions');
    new PublicKey(quote.depositAddress);
    const saved = await db().create({ ...record, depositAddress: quote.depositAddress, depositMemo: quote.depositMemo ?? '' });
    if (saved.amountIn !== amountIn || saved.recipient !== recipient) fail(409, 'Operation ID already used');
    return summary(saved);
  }));

  router.post('/quotes/:id/prepare', route(async req => {
    const record = await load(req);
    if (Date.parse(record.expiresAt) <= Date.now()) fail(409, 'Quote expired');
    if (await db().get(record.id + ':signed')) fail(409, 'Deposit already signed. Check status.');
    await requireMainnet();
    const { blockhash, lastValidBlockHeight } = await solana().getLatestBlockhash();
    const tx = makeDepositTransaction(record.wallet, { ...record, lastValidBlockHeight }, blockhash);
    const message = tx.serializeMessage().toString('base64');
    const preparationId = createHash('sha256').update(message).digest('hex');
    await db().create({ id: record.id + ':' + preparationId, message });
    return { preparationId, transaction: tx.serialize({ requireAllSignatures: false }).toString('base64') };
  }));

  router.post('/quotes/:id/submit', route(async req => {
    const record = await load(req);
    const { preparationId, signedTransaction } = req.body ?? {};
    if (!/^[a-f0-9]{64}$/.test(preparationId ?? '')) fail(400, 'Invalid preparation ID');
    const prepared = await db().get(record.id + ':' + preparationId);
    if (!prepared) fail(404, 'Deposit preparation not found');
    const signature = validateSignedDeposit(signedTransaction, prepared.message);
    const signingKey = record.id + ':signed';
    let saved = await db().get(signingKey);
    if (!saved) {
      if (Date.parse(record.expiresAt) <= Date.now()) fail(409, 'Quote expired');
      saved = await db().create({ id: signingKey, signature, signedTransaction });
    }
    if (saved.signature !== signature) fail(409, 'A different deposit is already signed. Check status.');
    return broadcast(saved);
  }));

  async function broadcast(saved) {
    const { signature } = saved;
    await requireMainnet();
    try {
      await solana().sendRawTransaction(Buffer.from(saved.signedTransaction, 'base64'), { preflightCommitment: 'confirmed' });
      return { signature, status: 'SUBMITTED' };
    } catch {
      return { signature, status: 'BROADCAST_UNKNOWN' };
    }
  }

  router.post('/quotes/:id/rebroadcast', route(async req => {
    const record = await load(req);
    const saved = await db().get(record.id + ':signed');
    if (!saved) fail(404, 'No signed deposit recorded');
    if (Date.parse(record.expiresAt) <= Date.now()) fail(409, 'Quote expired. Check status.');
    return broadcast(saved);
  }));

  router.get('/quotes/:id', route(async req => {
    const record = await load(req);
    const signed = await db().get(record.id + ':signed');
    const query = new URLSearchParams({ depositAddress: record.depositAddress });
    if (record.depositMemo) query.set('memo', record.depositMemo);
    const result = await click(statusPath + query);
    let sourceStatus;
    if (signed) {
      sourceStatus = 'UNKNOWN';
      try {
        await requireMainnet();
        const { value: [receipt] } = await solana().getSignatureStatuses([signed.signature], { searchTransactionHistory: true });
        if (receipt) sourceStatus = receipt.err ? 'FAILED' : (receipt.confirmationStatus?.toUpperCase() ?? 'UNKNOWN');
      } catch { /* Provider settlement remains readable during a Solana RPC interruption. */ }
    }
    await hive.onQuoteStatus(record, result.status);   // a Hive job's payment settled into the escrow: fund the job
    return { ...summary(record), signature: signed?.signature, status: result.status, sourceStatus };
  }));

  // ---- Hive Link: pay a job with one Solana USDC transfer into the treasury (hive-sol.js, 15 Nov 2026) --------------
  // The player signs in their own wallet; nothing else is needed (no NEAR wallet, no deposit address, fee only).
  const hiveTreasury = () => env.BAKELAND_HIVE_TREASURY || fail(503, 'Hive payments are not configured');
  async function hivePayment(req, operation) {
    const owner = await caller(req);
    if (!/^hive-[a-zA-Z0-9_-]{8,90}$/.test(String(operation))) fail(400, 'Invalid job');
    return { owner, id: 'hive-pay:' + key([owner.subject, owner.wallet], operation) };
  }

  router.post('/hive/pay/prepare', route(async req => {
    const { operationId: operation, amountIn } = req.body ?? {};
    const { owner, id } = await hivePayment(req, operation);
    boundedAmount(amountIn);
    const treasury = hiveTreasury();
    if (owner.wallet === treasury) fail(400, 'Invalid payer');
    const signed = await db().get(id + ':signed');
    if (signed) {
      const state = await hive.solana.signatureState(signed.signature, signed.lastValidBlockHeight).catch(() => 'pending');
      // A payment that failed or expired moved nothing, so the player may pay again; anything else is in flight.
      if (state !== 'failed' && state !== 'expired') fail(409, 'This job is already paid or being paid. Check status.');
      await db().remove(id + ':signed');
    }
    await requireMainnet();
    const { blockhash, lastValidBlockHeight } = await solana().getLatestBlockhash('confirmed');
    const tx = makeHivePaymentTransaction({ wallet: owner.wallet, treasury, micros: amountIn, operationId: operation,
      blockhash, lastValidBlockHeight });
    const message = tx.serializeMessage().toString('base64');
    const preparationId = createHash('sha256').update(message).digest('hex');
    await db().create({ id: id + ':' + preparationId, message, amountIn, lastValidBlockHeight, ttl: Math.floor(Date.now() / 1000) + 3600 });
    return { preparationId, transaction: tx.serialize({ requireAllSignatures: false }).toString('base64'), treasury, amountIn };
  }));

  router.post('/hive/pay/submit', route(async req => {
    const { operationId: operation, preparationId, signedTransaction } = req.body ?? {};
    const { owner, id } = await hivePayment(req, operation);
    if (!/^[a-f0-9]{64}$/.test(preparationId ?? '')) fail(400, 'Invalid preparation ID');
    const prepared = await db().get(id + ':' + preparationId);
    if (!prepared) fail(404, 'Payment preparation not found');
    const signature = validateSignedDeposit(signedTransaction, prepared.message);
    let saved = await db().get(id + ':signed');
    if (!saved) {
      saved = await db().create({ id: id + ':signed', signature, signedTransaction, amountIn: prepared.amountIn,
        lastValidBlockHeight: prepared.lastValidBlockHeight });
      // The settlement sweep finishes the payment if this request ends before Solana confirms it.
      await db().create({ id: 'hive-pay-open:' + signature, operationId: operation, wallet: owner.wallet,
        amountIn: prepared.amountIn, signature, lastValidBlockHeight: prepared.lastValidBlockHeight });
    }
    if (saved.signature !== signature) fail(409, 'A different payment is already signed. Check status.');
    await requireMainnet();
    try {
      await solana().sendRawTransaction(Buffer.from(saved.signedTransaction, 'base64'), { preflightCommitment: 'confirmed', maxRetries: 3 });
    } catch (error) {
      // Refused in simulation (for example not enough USDC): nothing was sent, so the player can try again.
      if (/simulation failed/i.test(error.message) && !/already been processed/i.test(error.message)) {
        await db().remove(id + ':signed');
        await db().remove('hive-pay-open:' + signature);
        fail(422, 'The wallet could not make this payment. Check your USDC and SOL balance.');
      }
    }
    return { signature, status: await settlePayment(operation, saved.signature, 20) };
  }));

  /** Waits up to `seconds` for the payment to be confirmed and the job funded. */
  async function settlePayment(operation, signature, seconds) {
    const deadline = Date.now() + seconds * 1000;
    for (;;) {
      const done = await db().get(`hive-funding:${operation}:${signature}`);
      if (done) return done.status;
      const open = await db().get('hive-pay-open:' + signature);
      if (!open) return 'PENDING';
      let status;
      try { status = await hive.solana.advancePayment(open); } catch { status = 'PENDING'; }
      if (status !== 'PENDING' || Date.now() > deadline) return status;
      await new Promise(resolve => setTimeout(resolve, 2000));
    }
  }

  router.get('/hive/pay/:operationId', route(async req => {
    const operation = req.params.operationId;
    const { id } = await hivePayment(req, operation);
    const saved = await db().get(id + ':signed');
    if (!saved) return { operationId: operation, status: 'UNPAID' };
    const status = await settlePayment(operation, saved.signature, 0);
    return { operationId: operation, signature: saved.signature, status };
  }));

  // Operator (service key): re-runs Hive funding for a stored quote record (D1 key) whose confirmation never reached
  // the server, e.g. the player closed the panel before 1Click settled. Safe to repeat: funding is once per operation.
  router.post('/hive/funding/:recordId', route(async req => {
    requireServiceKey(req);
    if (!/^[a-f0-9]{64}$/.test(req.params.recordId)) fail(400, 'Invalid record');
    const record = await db().get(req.params.recordId);
    if (!record?.operationId) fail(404, 'Quote not found');
    const query = new URLSearchParams({ depositAddress: record.depositAddress });
    if (record.depositMemo) query.set('memo', record.depositMemo);
    const result = await click(statusPath + query);
    await hive.onQuoteStatus(record, result.status);
    const marker = await db().get(`hive-funding:${record.operationId}`);
    return { operationId: record.operationId, status: result.status, fundingRecorded: Boolean(marker) };
  }));

  // Operator (service key): a small test payout from the escrow into the Hive agent through the same path a droid payout
  // takes (agent deposit address, signer transfer, Agent API status). At most 0.05 NEAR; `n` keeps retries idempotent.
  router.post('/hive/agent/test-payout', route(async req => {
    requireServiceKey(req);
    const { amountYocto, n } = req.body ?? {};
    if (!env.BAKELAND_NEAR_HIVE_AGENT_ID) fail(503, 'No Hive agent configured');
    if (typeof hiveEscrow?.send !== 'function') fail(503, 'The escrow signer is not bound');
    if (!/^[1-9]\d{0,22}$/.test(String(amountYocto)) || BigInt(amountYocto) > 50_000_000_000_000_000_000_000n) fail(400, 'Amount must be at most 0.05 NEAR');
    if (!Number.isInteger(n) || n < 1 || n > 999) fail(400, 'n must be 1-999');
    const reference = `hive-payout-${9_000_000_000 + n}`;
    const deposit = await agent('/v1/agents/' + encodeURIComponent(env.BAKELAND_NEAR_HIVE_AGENT_ID) + '/deposit',
      { origin_asset: 'nep141:wrap.near', amount: String(amountYocto) }, reference);
    const to = deposit?.details?.deposit_address;
    if (!to) fail(502, 'The agent deposit has no address');
    const tx = await hiveEscrow.send({ asset: 'nep141:wrap.near', amount: String(amountYocto), to, memo: deposit.details?.memo ?? '', reference });
    // No wait_ms: upstream() times out at 8 s. A repeat with the same `n` returns the then-current status.
    const status = await agent('/v1/status?' + new URLSearchParams({ correlation_id: deposit.correlation_id }));
    return { reference, tx, correlation_id: deposit.correlation_id, status: status?.status };
  }));

  // Operator (service key): retry one Hive settlement whose escrow transfer failed on chain. Clears the signer's failed
  // record (only a failed one: never a sent or pending transfer) and the expired prepared transfer, so the next sweep
  // prepares a fresh quote or deposit and sends again. Refused while the settlement has a recorded send.
  router.post('/hive/settlements/:id/retry', route(async req => {
    requireServiceKey(req);
    if (!/^\d{1,20}$/.test(req.params.id)) fail(400, 'Invalid settlement');
    if (typeof hiveEscrow?.clearFailed !== 'function') fail(503, 'The escrow signer is not bound');
    const key = `hive-settle:${req.params.id}`;
    if (await db().get(key + ':sent')) fail(409, 'This settlement was already sent');
    const cleared = [];
    for (const kind of ['payout', 'refund']) {
      const result = await hiveEscrow.clearFailed(`hive-${kind}-${req.params.id}`);
      if (result?.cleared) cleared.push(result.reference);
      if (result?.status && result.status !== 'failed') fail(409, `Transfer ${result.reference} is ${result.status}, not failed`);
    }
    const prepared = await db().get(key);
    if (prepared) await db().remove(key);
    return { settlement: Number(req.params.id), clearedTransfers: cleared, preparedRemoved: Boolean(prepared) };
  }));

  // Trusted service only. This prepares an agent deposit; it cannot move funds or award game items.
  router.post('/workers/:worker/deposits/:id', route(async req => {
    const worker = requireWorker(req);
    const id = key(['worker', req.params.worker], req.params.id);
    const { originAsset, amount } = req.body ?? {};
    if (!Array.isArray(worker.originAssets) || !worker.originAssets.includes(originAsset)
      || typeof amount !== 'string' || !amountFormat.test(amount)) fail(400, 'Unsupported worker deposit');
    const result = await agent('/v1/agents/' + encodeURIComponent(worker.agentId) + '/deposit',
      { origin_asset: originAsset, amount }, id);
    return { accountId: worker.accountId, correlationId: result.correlation_id, status: result.status, details: result.details };
  }));

  // Operator readiness check (service key): tries each configured credential against the live service and reports the
  // status it got, without returning any secret. The 1Click probe is a dry quote, so nothing is created.
  router.get('/readiness', route(async req => {
    requireServiceKey(req);
    async function probe(url, headers, body) {
      try {
        const response = await fetch(url, { method: body ? 'POST' : 'GET', redirect: 'error', signal: AbortSignal.timeout(8000),
          headers: { ...headers, 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
        const text = await response.text();
        let json = null;
        try { json = JSON.parse(text); } catch { /* not JSON */ }
        const message = response.ok ? undefined : String(json?.message ?? json?.error?.message ?? json?.error ?? json?.code ?? text).slice(0, 200);
        return { ok: response.ok, status: response.status, ...(message && { message }), json };
      } catch (error) {
        return { ok: false, error: error.message };
      }
    }
    const result = { config: {
      enabled: env.BAKELAND_NEAR_ENABLED === 'true', hiveEscrow: env.BAKELAND_NEAR_HIVE_ESCROW || null,
      hivePayoutAccount: env.BAKELAND_NEAR_HIVE_PAYOUT_ACCOUNT || null,
      droidAccounts: Object.keys(JSON.parse(env.BAKELAND_NEAR_WORKERS || '{}')), spacetimeDb: env.BAKELAND_SPACETIME_DB || null,
      sweep: env.BAKELAND_HIVE_SETTLEMENT_SWEEP === 'true', escrowSignerBound: typeof hiveEscrow?.send === 'function'
    } };
    const quoteBody = { dry: true, swapType: 'EXACT_INPUT', slippageTolerance: 100, originAsset: SOLANA_USDC_ASSET,
      // An ordinary account as the recipient: 1Click answers 400 for a contract such as wrap.near.
      destinationAsset: 'nep141:wrap.near', amount: '1000000', depositType: 'ORIGIN_CHAIN',
      recipient: env.BAKELAND_NEAR_HIVE_ESCROW || 'near',
      // A throwaway wallet address: 1Click rejects a mint (or other non-wallet account) as the refund address.
      recipientType: 'DESTINATION_CHAIN', refundTo: Keypair.generate().publicKey.toBase58(), refundType: 'ORIGIN_CHAIN',
      deadline: new Date(Date.now() + 10 * 60_000).toISOString() };
    // Agent API (NEAR_INTENTS_AGENT_API_URL; mainnet): try the agent key and the partner Distribution Channel
    // key (NEAR_1CLICK_JWT), each as X-API-Key and as Bearer. Only statuses and the key's public JWT claims are returned.
    const claims = token => {
      try {
        const body = JSON.parse(Buffer.from(String(token).split('.')[1], 'base64url').toString('utf8'));
        const pick = {};
        for (const k of ['iss', 'aud', 'env', 'network', 'scope', 'scopes', 'type', 'key_type', 'exp']) if (body[k] !== undefined) pick[k] = body[k];
        return { claimNames: Object.keys(body), ...pick };
      } catch { return { claimNames: [] }; }
    };
    result.agentApi = {};
    for (const [name, value] of [['agentKey', env.NEAR_INTENTS_AGENT_API_KEY], ['distributionKey', env.NEAR_1CLICK_JWT]]) {
      if (!value) { result.agentApi[name] = 'not set'; continue; }
      const key = String(value).trim();
      const viaHeader = await probe(agentApiBase() + '/v1/whoami', { 'X-API-Key': key });
      const viaBearer = await probe(agentApiBase() + '/v1/whoami', { Authorization: 'Bearer ' + key });
      result.agentApi[name] = { xApiKey: viaHeader.status, bearer: viaBearer.status,
        whoami: viaHeader.ok ? viaHeader.json : viaBearer.ok ? viaBearer.json : undefined,
        error: viaHeader.ok || viaBearer.ok ? undefined : (viaHeader.message ?? '').slice(0, 160),
        trimmedWhitespace: key !== String(value), jwt: claims(key) };
    }
    // The same dry quote with the JWT and without it, so a bad JWT and a bad request can be told apart.
    const quote = await probe('https://1click.chaindefuser.com/v0/quote',
      env.NEAR_1CLICK_JWT ? { Authorization: 'Bearer ' + env.NEAR_1CLICK_JWT } : {}, quoteBody);
    const anonymous = await probe('https://1click.chaindefuser.com/v0/quote', {}, quoteBody);
    result.oneClick = { authenticated: Boolean(env.NEAR_1CLICK_JWT), ok: quote.ok, status: quote.status,
      message: quote.message ?? quote.error, amountOut: quote.json?.quote?.amountOutFormatted,
      withoutJwt: { ok: anonymous.ok, status: anonymous.status, amountOut: anonymous.json?.quote?.amountOutFormatted } };
    if (typeof hiveEscrow?.check === 'function') {
      try { result.escrowSigner = await hiveEscrow.check(); } catch (error) { result.escrowSigner = { ok: false, problem: error.message }; }
      if (typeof hiveEscrow.status === 'function') {
        try {
          const s = await hiveEscrow.status();
          result.escrowSigner.dayTotalYocto = s.dayTotalYocto;
          result.escrowSigner.recentTransfers = (s.recent || []).slice(0, 8).map(r => ({ reference: r.reference, status: r.status,
            amount: r.amount, error: r.error ? String(r.error).slice(0, 160) : undefined, tx: r.tx_hash }));
        } catch { /* status is informational */ }
      }
      if (result.escrowSigner?.escrow && result.escrowSigner.escrow !== env.BAKELAND_NEAR_HIVE_ESCROW) {
        result.escrowSigner.ok = false;
        result.escrowSigner.problem = 'The signer escrow account differs from BAKELAND_NEAR_HIVE_ESCROW';
      }
    }
    if (typeof hiveEscrow?.solCheck === 'function') {
      try { result.treasurySigner = await hiveEscrow.solCheck(); } catch (error) { result.treasurySigner = { ok: false, problem: error.message }; }
      if (result.treasurySigner?.treasury && result.treasurySigner.treasury !== env.BAKELAND_HIVE_TREASURY) {
        result.treasurySigner.ok = false;
        result.treasurySigner.problem = 'The signer treasury differs from BAKELAND_HIVE_TREASURY';
      }
      try {
        const s = await hiveEscrow.solStatus();
        result.treasurySigner.dayTotalMicros = s.dayTotalMicros;
        result.treasurySigner.recentTransfers = (s.recent || []).slice(0, 8).map(r => ({ reference: r.reference, status: r.status,
          micros: r.micros, error: r.error ? String(r.error).slice(0, 160) : undefined, signature: r.signature }));
      } catch { /* status is informational */ }
    }
    try {
      const rows = await spacetimeHttp(env).sql('SELECT * FROM hive_labor_config');
      result.spacetime = { ok: true, laborConfigRows: rows.length };
    } catch (error) { result.spacetime = { ok: false, message: error.message }; }
    return result;
  }));
  // Operator (service key): register the Hive escrow account with wrap.near through the signer. Idempotent.
  router.post('/escrow/register', route(async req => {
    requireServiceKey(req);
    if (typeof hiveEscrow?.registerEscrow !== 'function') fail(503, 'The escrow signer is not bound');
    return hiveEscrow.registerEscrow();
  }));
  router.get('/workers/:worker/deposits/:id', route(async req => {
    const worker = requireWorker(req);
    if (!/^op_[a-zA-Z0-9_-]{4,120}$/.test(req.params.id)) fail(404, 'Worker deposit not found');
    const result = await agent('/v1/status?' + new URLSearchParams({ correlation_id: req.params.id }));
    if (result.agent_id !== worker.agentId || result.type !== 'deposit') fail(404, 'Worker deposit not found');
    return { correlationId: result.correlation_id, status: result.status, details: result.details };
  }));
  mountNearWalletLink(router, { env, db, caller, route, hasFullAccess });
  mountHiveAgent(router, { env, db, route, requireServiceKey, fail, agentApiBase });
  return router;
}
