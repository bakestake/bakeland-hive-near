using System;
using System.Collections.Generic;
using System.Linq;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// Client side of the server's Hive droid jobs (bakeland-spacetime HiveJobs.cs): the MyHiveJobs / MyHiveJobPlants
    /// views, the public HiveCropRate table and the CreateHiveJob / CancelHiveJob reducers.
    ///
    /// The job row is the durable order: it lives on the server, so the tracking view rebuilds from it after a reconnect
    /// or a reload. Rides on its own subscription (like FarmDeviceNetwork), so a server without HiveJobs.cs only turns
    /// the panel's job features off. When a job's delivered count rises or the job ends, the bag reloads (deliveries
    /// and returned seeds land in the server inventory).
    /// </summary>
    public static class HiveJobNetwork
    {
        public const byte AwaitingFunding = 0, Active = 1, Completed = 2, Cancelled = 3, Expired = 4, Failed = 5;
        public const string SiteFarm = "farm", SiteDesert = "desert-village-edge";
        /// <summary>Desert crops (server DesertFarm.cs): they grow only at the desert kiosk's farm, and only they do.</summary>
        private static readonly uint[] DesertCrops = { 60, 54, 74, 76 };

        public static bool IsReady { get; private set; }
        public static event Action Changed;
        /// <summary>The server's droid planted a crop for a job (a new MyHiveJobPlants row), live only (not the rows the
        /// subscription starts with). The kiosk's droid walks there and plants it on screen (HiveDroidAgent).</summary>
        public static event Action<HiveJobPlant> PlantStarted;
        /// <summary>A job's crop left the droid's care: harvested (the farm plant is gone) or handed to the player when the
        /// job ended (the plant stays).</summary>
        public static event Action<HiveJobPlant> PlantEnded;

        private static DbConnection attached;
        // MyHiveJobs is a view (no primary key): an update arrives as delete + insert. Keep the newest row per job.
        private static readonly Dictionary<ulong, HiveJob> jobs = new Dictionary<ulong, HiveJob>();
        private static readonly Dictionary<string, Pending> pendingCreate = new Dictionary<string, Pending>();
        private static readonly Dictionary<ulong, Action<string>> pendingCancel = new Dictionary<ulong, Action<string>>();
        private static readonly Dictionary<ulong, (uint delivered, bool open)> seen = new Dictionary<ulong, (uint delivered, bool open)>();

        private sealed class Pending
        {
            public Action<HiveJob> OnCreated;
            public Action<string> OnFailed;
            public bool Committed;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SpacetimeManager.OnConnected -= Attach;
            SpacetimeManager.OnConnected += Attach;
            SpacetimeManager.OnDisconnected -= Detach;
            SpacetimeManager.OnDisconnected += Detach;
            if (SpacetimeManager.IsConnected()) Attach();
        }

        // ------------------------------------------------------------------ queries

#if UNITY_EDITOR
        /// <summary>Editor probes only: sample rows for the JOBS page while the server module isn't published.</summary>
        public static bool HasPreview => preview.Count > 0;
        private static readonly List<HiveJob> preview = new List<HiveJob>();
        public static void SetPreviewJobs(IEnumerable<HiveJob> rows) { preview.Clear(); preview.AddRange(rows); Changed?.Invoke(); }
#else
        public static bool HasPreview => false;
#endif

        /// <summary>The caller's jobs: open ones first, then the newest.</summary>
        public static List<HiveJob> Jobs() => (HasPreview && !IsReady ? PreviewRows() : jobs.Values)
            .OrderBy(j => IsOpen(j) ? 0 : 1)
            .ThenByDescending(j => j.CreatedAt.MicrosecondsSinceUnixEpoch)
            .ToList();

        private static IEnumerable<HiveJob> PreviewRows()
        {
#if UNITY_EDITOR
            return preview;
#else
            return jobs.Values;
#endif
        }

        public static bool IsOpen(HiveJob job) => job.Status == AwaitingFunding || job.Status == Active;

        public static HiveJob OpenJobAt(string site) => Jobs().FirstOrDefault(j => j.Site == site && IsOpen(j));

        public static bool HasActiveJob(string site) => jobs.Values.Any(j => j.Site == site && j.Status == Active);

        public static HiveJob JobById(ulong jobId) => jobs.TryGetValue(jobId, out HiveJob job) ? job : null;

        /// <summary>Farm tiles a droid is tending for the caller's jobs at a site.</summary>
        public static List<HiveJobPlant> TendedAt(string site)
        {
            var list = new List<HiveJobPlant>();
            if (!IsReady || attached == null) return list;
            foreach (HiveJobPlant p in attached.Db.MyHiveJobPlants.Iter())
                if (JobById(p.JobId)?.Site == site) list.Add(p);
            return list;
        }

        public static HiveCropRate RateFor(uint plantIndex)
        {
            if (!IsReady || attached == null) return null;
            HiveCropRate rate = attached.Db.HiveCropRate.PlantIndex.Find(plantIndex);
            return rate != null && rate.Enabled ? rate : null;
        }

        /// <summary>True for a farm plant a hired droid is tending (it harvests it; the player can't).</summary>
        public static bool IsTended(ulong plantId)
        {
            if (!IsReady || attached == null) return false;
            foreach (HiveJobPlant p in attached.Db.MyHiveJobPlants.Iter()) if (p.PlantId == plantId) return true;
            return false;
        }

        /// <summary>Crops a kiosk's droid can grow: the active crop definitions that grow on that kiosk's farm and that
        /// the Hive has priced. Sorted by the player's farming level unlock, then name.</summary>
        public static List<FarmCropVersion> CropsFor(string site)
        {
            var list = new List<FarmCropVersion>();
            DbConnection conn = SpacetimeManager.Conn;
            if (conn == null || !IsReady) return list;
            bool desert = site == SiteDesert;
            foreach (FarmCrop crop in conn.Db.FarmCrop.Iter())
            {
                if (!crop.Enabled || (Array.IndexOf(DesertCrops, crop.PlantIndex) >= 0) != desert) continue;
                if (RateFor(crop.PlantIndex) == null) continue;
                FarmCropVersion definition = conn.Db.FarmCropVersion.Key.Find($"{crop.PlantIndex}:{crop.ActiveVersion}");
                if (definition != null) list.Add(definition);
            }
            return list.OrderBy(d => d.MinimumFarmingLevel).ThenBy(d => d.Name).ToList();
        }

        public static uint FarmingLevel()
        {
            DbConnection conn = SpacetimeManager.Conn;
            if (conn == null) return 1;
            foreach (PlayerXp xp in conn.Db.PlayerXp.Identity.Filter(SpacetimeManager.LocalIdentity))
                if (string.Equals(xp.Track, "farming", StringComparison.OrdinalIgnoreCase)) return xp.Level;
            return 1;
        }

        /// <summary>How many of an item the server bag holds.</summary>
        public static uint BagCount(int itemId)
        {
            DbConnection conn = SpacetimeManager.Conn;
            var me = conn?.Db.Player.Identity.Find(SpacetimeManager.LocalIdentity);
            var inventory = me != null ? conn.Db.PlayerInventory.PlayerId.Find(me.PlayerId) : null;
            if (inventory == null) return 0;
            uint n = 0;
            foreach (var pair in inventory.Items) if (pair.ItemId == itemId) n += pair.Count;
            return n;
        }

        public static string StatusName(HiveJob job) => job.Status switch
        {
            AwaitingFunding => "AWAITING PAYMENT",
            Active => "ACTIVE",
            Completed => "DELIVERED",
            Cancelled => "CANCELLED",
            Expired => "ENDED",
            Failed => "PAYMENT FAILED",
            _ => "UNKNOWN",
        };

        // ------------------------------------------------------------------ actions

        /// <summary>Places the order on the server: the seeds leave the bag and the job waits for its payment.
        /// <paramref name="onCreated"/> gets the server row (its EscrowMicros is the amount to pay).</summary>
        public static void Create(string clientJobId, string site, string droidId, uint plantIndex, uint target,
            Action<HiveJob> onCreated, Action<string> onFailed)
        {
            if (!IsReady || attached == null) { onFailed?.Invoke("The Hive is offline."); return; }
            pendingCreate[clientJobId] = new Pending { OnCreated = onCreated, OnFailed = onFailed };
            try { attached.Reducers.CreateHiveJob(clientJobId, site, droidId, plantIndex, target); }
            catch (Exception e) { pendingCreate.Remove(clientJobId); onFailed?.Invoke(e.Message); }
        }

        public static void Cancel(ulong jobId, Action<string> onFailed)
        {
            if (!IsReady || attached == null) { onFailed?.Invoke("The Hive is offline."); return; }
            pendingCancel[jobId] = onFailed;
            try { attached.Reducers.CancelHiveJob(jobId); }
            catch (Exception e) { pendingCancel.Remove(jobId); onFailed?.Invoke(e.Message); }
        }

        // ------------------------------------------------------------------ connection

        private static void Attach()
        {
            DbConnection conn = SpacetimeManager.Conn;
            if (conn == null || conn == attached) return;
            Detach();
            attached = conn;
            conn.Db.MyHiveJobs.OnInsert += HandleInsert;
            conn.Db.MyHiveJobs.OnDelete += HandleDelete;
            conn.Db.MyHiveJobPlants.OnInsert += HandlePlantInsert;
            conn.Db.MyHiveJobPlants.OnDelete += HandlePlantDelete;
            conn.Reducers.OnCreateHiveJob += HandleCreated;
            conn.Reducers.OnCancelHiveJob += HandleCancelled;
            try
            {
                conn.SubscriptionBuilder()
                    .OnApplied(_ => { IsReady = true; foreach (HiveJob j in conn.Db.MyHiveJobs.Iter()) jobs[j.JobId] = j; Changed?.Invoke(); })
                    .OnError((_, exception) => Log.LogWarning(
                        "HiveJobNetwork: Hive jobs unavailable (publish HiveJobs.cs): " + exception.Message))
                    .AddQuery(q => q.From.MyHiveJobs())
                    .AddQuery(q => q.From.MyHiveJobPlants())
                    .AddQuery(q => q.From.HiveCropRate())
                    .Subscribe();
            }
            catch (Exception exception)
            {
                Log.LogWarning("HiveJobNetwork: could not subscribe to Hive jobs: " + exception.Message);
            }
        }

        private static void Detach()
        {
            if (attached != null)
            {
                attached.Db.MyHiveJobs.OnInsert -= HandleInsert;
                attached.Db.MyHiveJobs.OnDelete -= HandleDelete;
                attached.Db.MyHiveJobPlants.OnInsert -= HandlePlantInsert;
                attached.Db.MyHiveJobPlants.OnDelete -= HandlePlantDelete;
                attached.Reducers.OnCreateHiveJob -= HandleCreated;
                attached.Reducers.OnCancelHiveJob -= HandleCancelled;
            }
            attached = null;
            jobs.Clear();
            seen.Clear();
            foreach (Pending p in pendingCreate.Values.ToList()) p.OnFailed?.Invoke("Disconnected from the Hive.");
            pendingCreate.Clear();
            pendingCancel.Clear();
            bool wasReady = IsReady;
            IsReady = false;
            if (wasReady) Changed?.Invoke();
        }

        private static void HandleInsert(EventContext context, HiveJob row)
        {
            // Deliveries and returned seeds change the server bag: reload it when a job's count or openness changes.
            bool reload = seen.TryGetValue(row.JobId, out var last) && (row.Delivered != last.delivered || last.open != IsOpen(row));
            seen[row.JobId] = (row.Delivered, IsOpen(row));
            jobs[row.JobId] = row;
            if (reload) ReloadInventory();
            if (pendingCreate.TryGetValue(row.ClientJobId, out Pending pending))
            {
                pendingCreate.Remove(row.ClientJobId);
                ReloadInventory();
                pending.OnCreated?.Invoke(row);
            }
            Changed?.Invoke();
        }

        private static void HandleDelete(EventContext context, HiveJob row)
        {
            // A view update arrives as delete + insert of the same job: keep the row until a newer one replaces it.
            if (jobs.TryGetValue(row.JobId, out HiveJob known) && known.UpdatedAt.MicrosecondsSinceUnixEpoch > row.UpdatedAt.MicrosecondsSinceUnixEpoch) return;
            if (attached != null && attached.Db.MyHiveJobs.Iter().Any(j => j.JobId == row.JobId)) return;
            jobs.Remove(row.JobId);
            Changed?.Invoke();
        }

        private static void HandlePlantInsert(EventContext context, HiveJobPlant row)
        {
            if (IsReady) PlantStarted?.Invoke(row);
            Changed?.Invoke();
        }

        private static void HandlePlantDelete(EventContext context, HiveJobPlant row)
        {
            if (IsReady) PlantEnded?.Invoke(row);
            Changed?.Invoke();
        }

        private static void HandleCreated(ReducerEventContext context, string clientJobId, string site, string droidId,
            uint plantIndex, uint target)
        {
            if (!pendingCreate.TryGetValue(clientJobId, out Pending pending)) return;
            if (context.Event.Status is Status.Committed)
            {
                // The row arrives in the same transaction; it may already be here.
                HiveJob row = jobs.Values.FirstOrDefault(j => j.ClientJobId == clientJobId);
                if (row == null) { pending.Committed = true; return; }
                pendingCreate.Remove(clientJobId);
                ReloadInventory();
                pending.OnCreated?.Invoke(row);
                return;
            }
            pendingCreate.Remove(clientJobId);
            pending.OnFailed?.Invoke(Reason(context.Event.Status, "The Hive couldn't take the job."));
        }

        private static void HandleCancelled(ReducerEventContext context, ulong jobId)
        {
            if (!pendingCancel.TryGetValue(jobId, out Action<string> onFailed)) return;
            pendingCancel.Remove(jobId);
            if (context.Event.Status is Status.Committed) ReloadInventory();
            else onFailed?.Invoke(Reason(context.Event.Status, "The job couldn't be cancelled."));
        }

        private static string Reason(Status status, string fallback) =>
            status is Status.Failed(var reason) && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : fallback;

        private static void ReloadInventory()
        {
            if (LocalGameManager.HasInstance && LocalGameManager.Instance.inventoryContainer != null)
                _ = LocalGameManager.Instance.inventoryContainer.LoadInventory();
        }
    }
}
