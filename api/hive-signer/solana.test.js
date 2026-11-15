import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { Keypair, PublicKey, Transaction, Message, ComputeBudgetProgram } from '@solana/web3.js';
import { getAssociatedTokenAddressSync } from '@solana/spl-token';
import { base58Encode } from './near-tx.js';
import { isOnCurve, associatedTokenAddress, compileMessage, signMessage, usdcTransferInstructions, importSolanaKey,
  USDC_MINT, TOKEN_PROGRAM, ATA_PROGRAM, MEMO_PROGRAM } from './solana-tx.js';
import { createSolanaEngine, checkSolTransfer } from './solana-engine.js';

function sqlStore() {
  const db = new DatabaseSync(':memory:');
  return { exec(query, ...args) {
    const statement = db.prepare(query);
    const rows = /^\s*select/i.test(query) ? statement.all(...args) : (statement.run(...args), []);
    return { toArray: () => rows.map(row => ({ ...row })) };
  } };
}

const treasury = Keypair.generate();
const TREASURY = treasury.publicKey.toBase58();
const SECRET = base58Encode(treasury.secretKey);
const env = { TREASURY_SOLANA_ADDRESS: TREASURY, TREASURY_SOLANA_SECRET_KEY: SECRET, MAX_USDC_TRANSFER_MICROS: '25000000',
  DAILY_MAX_USDC_MICROS: '30000000', SOLANA_PRIORITY_MICROLAMPORTS: '50000' };

test('curve membership and associated token addresses match web3.js and spl-token', async () => {
  for (let i = 0; i < 300; i++) {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    assert.equal(isOnCurve(bytes), PublicKey.isOnCurve(bytes), Buffer.from(bytes).toString('hex'));
  }
  for (let i = 0; i < 20; i++) {
    const owner = Keypair.generate().publicKey;
    assert.equal(await associatedTokenAddress(owner.toBase58()),
      getAssociatedTokenAddressSync(new PublicKey(USDC_MINT), owner, true).toBase58());
  }
});

test('the signed USDC transfer decodes in web3.js with the expected instructions and a valid signature', async () => {
  const to = Keypair.generate().publicKey.toBase58();
  const { instructions, source, destination } = await usdcTransferInstructions({ from: TREASURY, to, micros: 5_250_000n,
    memo: 'hive-sol-payout-7', priorityMicroLamports: 50000 });
  const blockhash = Keypair.generate().publicKey.toBase58();
  const { message } = compileMessage({ payer: TREASURY, recentBlockhash: blockhash, instructions });
  const key = await importSolanaKey(SECRET, TREASURY);
  const { wire, signature } = await signMessage(message, key);

  const tx = Transaction.from(Buffer.from(wire, 'base64'));
  assert.ok(tx.verifySignatures(), 'signature verifies');
  assert.equal(tx.signature && base58Encode(tx.signature), signature);
  assert.equal(tx.feePayer.toBase58(), TREASURY);
  assert.equal(tx.recentBlockhash, blockhash);
  const decoded = Message.from(message);
  assert.equal(decoded.header.numRequiredSignatures, 1);
  const programs = tx.instructions.map(ix => ix.programId.toBase58());
  assert.deepEqual(programs, [ComputeBudgetProgram.programId.toBase58(), ComputeBudgetProgram.programId.toBase58(), ATA_PROGRAM, TOKEN_PROGRAM, MEMO_PROGRAM]);
  const [, , create, transfer, memo] = tx.instructions;
  assert.deepEqual([...create.data], [1], 'CreateIdempotent');
  assert.deepEqual(create.keys.map(k => [k.pubkey.toBase58(), k.isSigner, k.isWritable]), [
    [TREASURY, true, true], [destination, false, true], [to, false, false], [USDC_MINT, false, false],
    ['11111111111111111111111111111111', false, false], [TOKEN_PROGRAM, false, false]]);
  assert.equal(transfer.data[0], 12, 'TransferChecked');
  assert.equal(Buffer.from(transfer.data).readBigUInt64LE(1), 5_250_000n);
  assert.equal(transfer.data[9], 6);
  assert.deepEqual(transfer.keys.map(k => [k.pubkey.toBase58(), k.isSigner, k.isWritable]), [
    // Flags are per account once compiled: the authority is also the (writable) fee payer.
    [source, false, true], [USDC_MINT, false, false], [destination, false, true], [TREASURY, true, true]]);
  assert.equal(Buffer.from(memo.data).toString(), 'hive-sol-payout-7');
});

test('the treasury key is refused unless it belongs to the configured address, in any common format', async () => {
  for (const format of [SECRET, JSON.stringify([...treasury.secretKey]), Buffer.from(treasury.secretKey).toString('hex'),
    Buffer.from(treasury.secretKey).toString('base64')]) {
    assert.equal((await importSolanaKey(format, TREASURY)).address, TREASURY);
  }
  await assert.rejects(importSolanaKey(SECRET, Keypair.generate().publicKey.toBase58()), /not the key of TREASURY_SOLANA_ADDRESS/);
  const mixed = Uint8Array.from([...treasury.secretKey.slice(0, 32), ...Keypair.generate().publicKey.toBytes()]);
  await assert.rejects(importSolanaKey(base58Encode(mixed), base58Encode(mixed.slice(32))), /does not match/);
});

test('refuses anything but a capped USDC transfer with a Hive Solana reference', () => {
  const to = Keypair.generate().publicKey.toBase58();
  const ok = { asset: 'solana:usdc', amount: '5000000', to, reference: 'hive-sol-payout-b12' };
  assert.equal(checkSolTransfer(ok, env).micros, 5_000_000n);
  assert.throws(() => checkSolTransfer({ ...ok, asset: 'nep141:wrap.near' }, env));
  assert.throws(() => checkSolTransfer({ ...ok, amount: '25000001' }, env), /per-transfer limit/);
  assert.throws(() => checkSolTransfer({ ...ok, amount: '-1' }, env));
  assert.throws(() => checkSolTransfer({ ...ok, to: TREASURY }, env), /itself/);
  assert.throws(() => checkSolTransfer({ ...ok, to: 'nope' }, env));
  assert.throws(() => checkSolTransfer({ ...ok, reference: 'hive-payout-1' }, env), /reference/);
});

/** A fake Solana RPC: balances, blockhash, block height, and scripted signature statuses. */
function fakeRpc({ usdc = '100000000', statuses = [], height = 100, sendError } = {}) {
  const calls = [];
  let blockhashes = 0;
  const state = { height, statuses: [...statuses] };
  const rpc = async (method, params) => {
    calls.push([method, params]);
    if (method === 'getTokenAccountBalance') return { value: { amount: usdc } };
    if (method === 'getBalance') return { value: 5_000_000 };
    if (method === 'getLatestBlockhash') { blockhashes++; return { value: { blockhash: Keypair.generate().publicKey.toBase58(), lastValidBlockHeight: state.height + 150 } }; }
    if (method === 'getBlockHeight') return state.height;
    if (method === 'sendTransaction') { if (sendError) throw sendError; return params[0]; }
    if (method === 'getSignatureStatuses') return { value: [state.statuses.length ? state.statuses.shift() : null] };
    throw new Error('unexpected ' + method);
  };
  return { rpc, calls, state, blockhashes: () => blockhashes };
}

const confirmed = { confirmationStatus: 'confirmed', err: null };
const engine = (rpc, extra = {}) => createSolanaEngine({ sql: sqlStore(), env: { ...env, ...extra }, rpc, sleep: async () => {} });

test('sends once per reference', async () => {
  const fake = fakeRpc({ statuses: [confirmed] });
  const e = engine(fake.rpc);
  const to = Keypair.generate().publicKey.toBase58();
  const first = await e.send({ asset: 'solana:usdc', amount: '6000000', to, reference: 'hive-sol-payout-b1', memo: 'hive' });
  assert.equal(first.ok, true);
  const again = await e.send({ asset: 'solana:usdc', amount: '6000000', to, reference: 'hive-sol-payout-b1' });
  assert.deepEqual([again.ok, again.repeated, again.signature], [true, true, first.signature]);
  assert.equal(fake.calls.filter(c => c[0] === 'sendTransaction').length, 1);
  const other = await e.send({ asset: 'solana:usdc', amount: '7000000', to, reference: 'hive-sol-payout-b1' });
  assert.equal(other.refused, true, 'a reference never moves a different amount');
});

test('an unconfirmed transfer is re-sent with the same bytes, and signed again only once its blockhash is finalized past', async () => {
  const fake = fakeRpc();
  const e = engine(fake.rpc);
  const to = Keypair.generate().publicKey.toBase58();
  const request = { asset: 'solana:usdc', amount: '6000000', to, reference: 'hive-sol-refund-9' };
  const first = await e.send(request);
  assert.equal(first.pending, true);
  const sentBytes = fake.calls.filter(c => c[0] === 'sendTransaction').map(c => c[1][0]);
  assert.equal(new Set(sentBytes).size, 1);
  assert.equal((await e.send({ ...request, reference: 'hive-sol-refund-10' })).pending, true, 'blocks new transfers meanwhile');

  // Still inside its validity window: the retry re-sends the same signed transaction.
  await e.send(request);
  assert.equal(new Set(fake.calls.filter(c => c[0] === 'sendTransaction').map(c => c[1][0])).size, 1);
  assert.equal(fake.blockhashes(), 1);

  // Finalized height past its last valid height and no record of it: it can never land, so it is signed afresh.
  fake.state.height = 1000;
  fake.state.statuses = [null, confirmed];
  const resent = await e.send(request);
  assert.equal(resent.ok, true);
  assert.notEqual(resent.signature, first.signature);
  assert.equal(fake.blockhashes(), 2);
});

test('an on-chain failure is recorded and not re-sent until an operator clears it', async () => {
  const fake = fakeRpc({ statuses: [{ confirmationStatus: 'confirmed', err: { InstructionError: [3, 'Custom'] } }] });
  const e = engine(fake.rpc);
  const to = Keypair.generate().publicKey.toBase58();
  const request = { asset: 'solana:usdc', amount: '6000000', to, reference: 'hive-sol-payout-3' };
  assert.match((await e.send(request)).error, /failed on chain/);
  assert.match((await e.send(request)).error, /failed on chain/);
  assert.equal(fake.calls.filter(c => c[0] === 'sendTransaction').length, 1);
  assert.equal(e.clearFailed('hive-sol-payout-3').cleared, true);
  fake.state.statuses = [confirmed];
  assert.equal((await e.send(request)).ok, true);
});

test('a simulation refusal records a failure without anything landing', async () => {
  const error = Object.assign(new Error('Transaction simulation failed: insufficient funds'), { rpcError: { code: -32002 } });
  const e = engine(fakeRpc({ sendError: error }).rpc);
  const result = await e.send({ asset: 'solana:usdc', amount: '6000000', to: Keypair.generate().publicKey.toBase58(), reference: 'hive-sol-payout-4' });
  assert.match(result.error, /refused by simulation/);
});

test('enforces the daily limit and the treasury balance', async () => {
  const fake = fakeRpc({ statuses: [confirmed, confirmed, confirmed] });
  const e = engine(fake.rpc);
  const to = Keypair.generate().publicKey.toBase58();
  assert.equal((await e.send({ asset: 'solana:usdc', amount: '20000000', to, reference: 'hive-sol-payout-5' })).ok, true);
  const over = await e.send({ asset: 'solana:usdc', amount: '20000000', to, reference: 'hive-sol-payout-6' });
  assert.match(over.error, /daily limit/);
  const poor = engine(fakeRpc({ usdc: '1000' }).rpc);
  assert.match((await poor.send({ asset: 'solana:usdc', amount: '6000000', to, reference: 'hive-sol-payout-7' })).error, /enough USDC/);
});

test('the self-check reports balances, never the key, and refuses a key for another wallet', async () => {
  const good = await engine(fakeRpc().rpc).check();
  assert.equal(good.ok, true);
  assert.equal(good.usdcMicros, '100000000');
  assert.ok(!JSON.stringify(good).includes(SECRET));
  const bad = await engine(fakeRpc().rpc, { TREASURY_SOLANA_ADDRESS: Keypair.generate().publicKey.toBase58() }).check();
  assert.equal(bad.ok, false);
});
