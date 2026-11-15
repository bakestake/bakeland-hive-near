using System;
using System.Text;
using System.Threading.Tasks;
using Solana.Unity.Rpc.Models;
using Solana.Unity.SDK;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Scripting;

namespace Bakeland
{
    /// <summary>Wallet approval and recovery for Bakeland's Solana-funded NEAR Intents route.</summary>
    public sealed class NearIntentsClient
    {
        [Serializable, Preserve]
        public sealed class Quote
        {
            public string operationId, wallet, amountIn, amountOut, minAmountOut, destinationAsset;
            public string recipient, expiresAt, signature, status, sourceStatus;
        }

        [Serializable, Preserve]
        public sealed class Submission
        {
            public string signature, status;
        }

        [Serializable] private sealed class QuoteRequest { public string operationId, amountIn, recipient; public bool dry; }
        [Serializable, Preserve] private sealed class Preparation { public string preparationId, transaction; }
        [Serializable, Preserve] public sealed class NearLink { public bool linked; public string nearAccountId; }
        [Serializable, Preserve] public sealed class NearLinkSession { public string ticket, walletUrl; }
        [Serializable] private sealed class NearLinkRequest { public string ticket, nearAccountId; }
        [Serializable] private sealed class SignedDeposit { public string preparationId, signedTransaction; }

        private readonly string endpoint;
        private bool signing;

        public NearIntentsClient(string inventoryApiRoot)
        {
            if (!Uri.TryCreate(inventoryApiRoot, UriKind.Absolute, out var root) ||
                (root.Scheme != Uri.UriSchemeHttps && !(root.IsLoopback && root.Scheme == Uri.UriSchemeHttp)) ||
                !string.IsNullOrEmpty(root.UserInfo + root.Query + root.Fragment))
                throw new ArgumentException("Use an HTTPS inventory API root.", nameof(inventoryApiRoot));
            endpoint = root.AbsoluteUri.TrimEnd('/') + "/near-intents";
        }

        public static string NewOperationId() => Guid.NewGuid().ToString("N");

        private const string NearLinkPath = "/social-links/near";

        public Task<NearLink> GetNearLink() => Request<NearLink>("GET", NearLinkPath);

        public Task<NearLinkSession> StartNearLink() => Request<NearLinkSession>("POST", NearLinkPath + "/start");

        // Call from a user gesture on WebGL. Keep the session in memory until the player returns.
        public void OpenNearLink(NearLinkSession session)
        {
            if (session == null || !Uri.TryCreate(session.walletUrl, UriKind.Absolute, out var url) ||
                !Uri.TryCreate(endpoint + NearLinkPath + "/connect", UriKind.Absolute, out var expected) ||
                url.GetLeftPart(UriPartial.Authority) != expected.GetLeftPart(UriPartial.Authority) ||
                url.AbsolutePath != expected.AbsolutePath || url.Fragment != "#" + session.ticket ||
                !string.IsNullOrEmpty(url.UserInfo + url.Query))
                throw new InvalidOperationException("Unexpected NEAR wallet linking URL.");
            Application.OpenURL(url.AbsoluteUri);
        }

        public Task<NearLink> GetPendingNearLink(string ticket) =>
            Request<NearLink>("POST", NearLinkPath + "/pending", JsonUtility.ToJson(new NearLinkRequest { ticket = ticket }));

        public Task<NearLink> ConfirmNearLink(string ticket, string approvedAccountId) =>
            Request<NearLink>("POST", NearLinkPath + "/confirm",
                JsonUtility.ToJson(new NearLinkRequest { ticket = ticket, nearAccountId = approvedAccountId }));

        // The caller retains operationId for recovery. Reuse it when a request times out.
        public Task<Quote> RequestQuote(string operationId, ulong usdcAtomic, string recipient, bool dry = true) =>
            Request<Quote>("POST", "/quotes", JsonUtility.ToJson(new QuoteRequest {
                operationId = operationId, amountIn = usdcAtomic.ToString(System.Globalization.CultureInfo.InvariantCulture),
                recipient = recipient, dry = dry
            }));

        public Task<Quote> GetStatus(string operationId) => Request<Quote>("GET", QuotePath(operationId));

        public Task<Submission> Rebroadcast(string operationId) =>
            Request<Submission>("POST", QuotePath(operationId) + "/rebroadcast");

        public async Task<Submission> ApproveAndSubmit(Quote quote)
        {
            if (quote == null) throw new ArgumentNullException(nameof(quote));
            if (signing) throw new InvalidOperationException("A deposit approval is already in progress.");
            var wallet = Web3.Wallet;
            if (wallet?.Account?.PublicKey.ToString() != quote.wallet || wallet == null)
                throw new InvalidOperationException("Connect the Solana wallet used to request the quote.");

            signing = true;
            SpacetimeManager.BeginExternalWalletActionKeepAlive();
            try
            {
                string path = QuotePath(quote.operationId);
                var preparation = await Request<Preparation>("POST", path + "/prepare");
                if (wallet != Web3.Wallet || wallet.Account?.PublicKey.ToString() != quote.wallet)
                    throw new InvalidOperationException("The connected wallet changed.");
                var tx = Transaction.Deserialize(Convert.FromBase64String(preparation.transaction));
                if (tx.FeePayer?.ToString() != quote.wallet)
                    throw new InvalidOperationException("Unexpected deposit fee payer.");
                // The wire format includes a zero-filled signature slot. Give the wallet a clean transaction.
                tx.Signatures.Clear();
                var signed = await wallet.SignTransaction(tx);
                if (signed == null) throw new InvalidOperationException("The deposit was not approved.");
                if (wallet != Web3.Wallet || wallet.Account?.PublicKey.ToString() != quote.wallet)
                    throw new InvalidOperationException("The connected wallet changed.");
                return await Request<Submission>("POST", path + "/submit", JsonUtility.ToJson(new SignedDeposit {
                    preparationId = preparation.preparationId,
                    signedTransaction = Convert.ToBase64String(signed.Serialize())
                }));
            }
            finally
            {
                signing = false;
                SpacetimeManager.EndExternalWalletActionKeepAlive();
            }
        }

        // ------------------------------------------------------------------ Hive Link: direct Solana USDC (15 Nov 2026)

        /// <summary>Where a Hive job's payment stands: UNPAID, PENDING, FUNDED, FAILED, EXPIRED (nothing moved; pay
        /// again) or REFUNDING (the job could not take it; it goes back to the wallet).</summary>
        [Serializable, Preserve]
        public sealed class HivePayment { public string operationId, signature, status; }

        [Serializable] private sealed class HivePayRequest { public string operationId, amountIn; }
        [Serializable, Preserve] private sealed class HivePreparation { public string preparationId, transaction, treasury, amountIn; }
        [Serializable] private sealed class HivePaySubmit { public string operationId, preparationId, signedTransaction; }

        private const string TokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
        private const string MemoProgram = "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr";

        /// <summary>
        /// Pays a placed Hive job with one USDC transfer from the connected wallet to the studio treasury (memo
        /// "hive:&lt;job id&gt;"); the wallet pays only the network fee. The API builds the transaction, this checks it
        /// is exactly that transfer before the wallet signs, and the API funds the job once Solana confirms it.
        /// </summary>
        public async Task<HivePayment> PayHiveJob(string operationId, ulong micros, string expectedTreasury)
        {
            if (string.IsNullOrEmpty(operationId)) throw new ArgumentNullException(nameof(operationId));
            if (signing) throw new InvalidOperationException("A wallet approval is already in progress.");
            var wallet = Web3.Wallet;
            string owner = wallet?.Account?.PublicKey?.ToString();
            if (string.IsNullOrEmpty(owner)) throw new InvalidOperationException("Connect your Solana wallet to pay.");

            signing = true;
            SpacetimeManager.BeginExternalWalletActionKeepAlive();
            try
            {
                string amount = micros.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var preparation = await Request<HivePreparation>("POST", "/hive/pay/prepare",
                    JsonUtility.ToJson(new HivePayRequest { operationId = operationId, amountIn = amount }));
                if (!string.IsNullOrEmpty(expectedTreasury) && preparation.treasury != expectedTreasury)
                    throw new InvalidOperationException("Unexpected payment destination.");
                if (preparation.amountIn != amount) throw new InvalidOperationException("Unexpected payment amount.");
                var tx = Transaction.Deserialize(Convert.FromBase64String(preparation.transaction));
                if (tx.FeePayer?.ToString() != owner)
                    throw new InvalidOperationException("Sign in with the wallet you play with to pay.");
                if (tx.Instructions == null || tx.Instructions.Count != 2 ||
                    new Solana.Unity.Wallet.PublicKey(tx.Instructions[0].ProgramId).ToString() != TokenProgram ||
                    new Solana.Unity.Wallet.PublicKey(tx.Instructions[1].ProgramId).ToString() != MemoProgram)
                    throw new InvalidOperationException("Unexpected payment transaction.");
                tx.Signatures.Clear();
                var signed = await wallet.SignTransaction(tx);
                if (signed == null) throw new InvalidOperationException("The payment was not approved.");
                if (wallet != Web3.Wallet || wallet.Account?.PublicKey.ToString() != owner)
                    throw new InvalidOperationException("The connected wallet changed.");
                return await Request<HivePayment>("POST", "/hive/pay/submit", JsonUtility.ToJson(new HivePaySubmit {
                    operationId = operationId, preparationId = preparation.preparationId,
                    signedTransaction = Convert.ToBase64String(signed.Serialize())
                }));
            }
            finally
            {
                signing = false;
                SpacetimeManager.EndExternalWalletActionKeepAlive();
            }
        }

        public Task<HivePayment> GetHivePayment(string operationId) =>
            Request<HivePayment>("GET", "/hive/pay/" + Uri.EscapeDataString(operationId ?? throw new ArgumentNullException(nameof(operationId))));

        [Serializable, Preserve] private sealed class ErrorBody { public string error; }

        private static string QuotePath(string operationId) =>
            "/quotes/" + Uri.EscapeDataString(operationId ?? throw new ArgumentNullException(nameof(operationId)));

        private async Task<T> Request<T>(string method, string path, string body = null) where T : class
        {
            using var request = new UnityWebRequest(endpoint + path, method) {
                downloadHandler = new DownloadHandlerBuffer(), timeout = 25, redirectLimit = 0
            };
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            GatewayLoginProof.Authorize(request);
            if (string.IsNullOrEmpty(request.GetRequestHeader("Authorization")))
                throw new InvalidOperationException("Sign in with a Bakeland wallet to continue.");
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                // The API's own message (for example "Check your USDC and SOL balance") when it sent one.
                string reason = null;
                try { reason = JsonUtility.FromJson<ErrorBody>(request.downloadHandler?.text ?? "")?.error; } catch { }
                throw new InvalidOperationException(!string.IsNullOrEmpty(reason) && reason.Length <= 120
                    ? reason : $"NEAR Intents request failed (HTTP {request.responseCode}).");
            }
            return JsonUtility.FromJson<T>(request.downloadHandler.text)
                ?? throw new InvalidOperationException("NEAR Intents returned an empty response.");
        }
    }
}
