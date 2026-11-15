using SpacetimeDB;

// Hive Link droid jobs (14 Nov 2026). A player hires a Hive worker droid at a Hive Link kiosk to grow a chosen crop to a
// target quantity on their own plots at the farm next to that kiosk, and pays in USDC through the NEAR Intents route.
//
// Durable order: the HiveJob row is the order. It survives reconnects (clients read MyHiveJobs), and every state change
// happens here, never on a client.
//
// Verified delivery: the droid's work runs on the scheduler (RunHiveJob). It plants the player's held seeds on free plots
// of the kiosk's farm, keeps them watered and harvests them when the server's own growth clock says they are mature, so
// a delivery is a harvest this module performed (HiveDelivery, one per plant). The harvest goes to the player's
// inventory as it would from their own hands, without farming XP (a hired droid's work is not the player's).
//
// Escrow, on the P2P market's hard-currency pattern (Marketplace.cs): the price is held by the Hive escrow account
// off-chain, this module books what it owes, and the settlement worker (an operator-registered identity,
// HiveSettlementAuthority) moves the money and confirms with a reference.
//   1. CreateHiveJob: the player picks site, crop and quantity; the seeds move out of the bag into the job
//      (AwaitingFunding). Price = the crop's labour rate x quantity, at most 10 USDC.
//   2. The player pays with one Solana USDC transfer into the studio treasury, memo'd with the job's ClientJobId
//      (15 Nov 2026; before that through NEAR Intents 1Click). Once it is confirmed on Solana the worker calls
//      ConfirmHiveJobFunding ("sol:<signature>", amount and payer wallet checked): Active. Unfunded jobs expire after
//      20 minutes (seeds back); funding that arrives later is refunded in full rather than lost.
//   3. Every delivery queues a payout settlement for its share (rate x quantity). The worker groups a job's payouts
//      into one deposit into the Hive's NEAR Intents agent when the job closes (HiveJobClosure) or the group reaches
//      its minimum.
//   4. Completed: everything paid out. Cancelled or past the deadline: the droid stops, crops it planted become the
//      player's ordinary crops, unplanted seeds go back to the bag, and the undelivered share is queued as a refund.
//   5. The worker reads pending HiveSettlement rows, pays or refunds, and confirms each with ConfirmHiveSettlement.
public static partial class Module
{
    public const byte HiveJobAwaitingFunding = 0;
    public const byte HiveJobActive = 1;
    public const byte HiveJobCompleted = 2;
    public const byte HiveJobCancelled = 3;
    public const byte HiveJobExpired = 4;
    public const byte HiveJobFailed = 5;

    public const byte HiveSettlementPayout = 1;  // escrow -> the droid's agent, for one verified delivery
    public const byte HiveSettlementRefund = 2;  // escrow -> the player, for the undelivered share

    public const byte HiveSettlementPending = 0;
    public const byte HiveSettlementSettled = 1;

    public const string HiveSiteFarm = "farm";
    public const string HiveSiteDesert = "desert-village-edge";

    private const int HiveTickSeconds = 10;
    private const int HiveFundingWindowSeconds = 20 * 60;
    private const uint HiveMaxTarget = 200;
    private const int HiveMaxIdLength = 96;
    private const int HiveMaxReferenceLength = 160;

    /// <summary>Labour pricing, set by an operator. Micros are USDC atomic units (6 decimals).</summary>
    [Table(Accessor = "HiveLaborConfig", Public = true)]
    public partial struct HiveLaborConfig
    {
        [PrimaryKey] public uint Id;                 // always 0
        public ulong BaseMicros;                     // every crop: the droid's visit
        public ulong MicrosPerGold;                  // what one Gold of crop profit is worth in USDC
        public uint ProfitShareBps;                  // the droid's share of the crop's profit
        public ulong MicrosPerGrowMinute;            // tending time
        public ulong MinMicros;                      // per crop
        public ulong MaxMicros;
        public ulong MaxOrderMicros;                 // per order (NEAR route: 10 USDC per quote)
        public uint PlotsPerDroid;                   // plots one droid tends at once
        public bool Enabled;
    }

    /// <summary>The price per delivered crop, derived from its value (SeedHiveLabor) or set by an operator.</summary>
    [Table(Accessor = "HiveCropRate", Public = true)]
    public partial struct HiveCropRate
    {
        [PrimaryKey] public uint PlantIndex;
        public ulong MicrosPerCrop;
        public long ProfitGold;                      // harvest sell value minus seed price, per plant
        public uint GrowSeconds;
        public bool Enabled;
    }

    [Table(Accessor = "HiveJob")]
    public partial struct HiveJob
    {
        [PrimaryKey, AutoInc] public ulong JobId;
        [Unique] public string ClientJobId;          // also the NEAR Intents operation id
        [SpacetimeDB.Index.BTree] public Identity Owner;
        public uint PlayerId;
        public string Site;
        public string DroidId;
        public uint PlantIndex;
        public int SeedItemId;
        public int HarvestItemId;
        public uint Target;                          // harvest items ordered
        public uint Delivered;                       // harvest items delivered (verified harvests)
        public uint SeedsHeld;                       // seeds taken from the bag, not yet planted
        public uint Growing;                         // droid plants in the ground
        public ulong MicrosPerCrop;
        public ulong EscrowMicros;                   // MicrosPerCrop x Target: what the player pays
        public ulong PaidOutMicros;                  // booked to the droid's agent (one payout per delivery)
        public ulong RefundMicros;                   // booked back to the player
        public byte Status;
        public string FundingReference;
        public Timestamp CreatedAt;
        public Timestamp FundedAt;
        public Timestamp DeadlineAt;
        public Timestamp UpdatedAt;
        public string Note;                          // what the droid is doing, for the tracking view
    }

    /// <summary>A crop the droid planted for a job (links FarmPlant rows to the job until harvest).</summary>
    [Table(Accessor = "HiveJobPlant")]
    public partial struct HiveJobPlant
    {
        [PrimaryKey] public ulong PlantId;
        [SpacetimeDB.Index.BTree] public ulong JobId;
        [SpacetimeDB.Index.BTree] public Identity Owner;
        public uint TileIndex;
    }

    /// <summary>A verified delivery: a harvest the scheduler performed for a job.</summary>
    [Table(Accessor = "HiveDelivery")]
    public partial struct HiveDelivery
    {
        [PrimaryKey, AutoInc] public ulong DeliveryId;
        [Unique] public ulong PlantId;
        [SpacetimeDB.Index.BTree] public ulong JobId;
        [SpacetimeDB.Index.BTree] public Identity Owner;
        public uint TileIndex;
        public int ItemId;
        public uint Quantity;                        // counted toward the order
        public ulong PayoutMicros;
        public Timestamp DeliveredAt;
    }

    /// <summary>The escrow's to-do list for the settlement worker. No identities or wallets: the worker maps the
    /// job's FundingReference (the NEAR operation) to the payer it already knows.</summary>
    [Table(Accessor = "HiveSettlement", Public = true)]
    public partial struct HiveSettlement
    {
        [PrimaryKey, AutoInc] public ulong SettlementId;
        [SpacetimeDB.Index.BTree] public ulong JobId;
        public ulong DeliveryId;                     // payouts only
        public byte Kind;
        public string DroidId;
        public string FundingReference;
        public ulong Micros;
        public ulong EscrowMicros;                   // the job's escrow, so the worker can pay a proportional share
        public byte Status;
        public string Reference;
        public Timestamp CreatedAt;
        public Timestamp SettledAt;
    }

    /// <summary>A funded job that has ended (completed, cancelled or past its deadline): no more payouts will be queued
    /// for it. 15 Nov 2026: the settlement worker groups a job's payouts into one transfer and sends them when the job
    /// closes or the group reaches its minimum, and HiveJob is private, so the worker reads closures here.</summary>
    [Table(Accessor = "HiveJobClosure", Public = true)]
    public partial struct HiveJobClosure
    {
        [PrimaryKey] public ulong JobId;
        public byte Status;
        public Timestamp ClosedAt;
    }

    [Table(Accessor = "HiveJobTick", Scheduled = nameof(RunHiveJob), ScheduledAt = nameof(At))]
    public partial struct HiveJobTick
    {
        [PrimaryKey, AutoInc] public ulong TickId;
        [SpacetimeDB.Index.BTree] public ulong JobId;
        public ScheduleAt At;
    }

    [Table(Accessor = "HiveFundingExpiry", Scheduled = nameof(ExpireHiveFunding), ScheduledAt = nameof(At))]
    public partial struct HiveFundingExpiry
    {
        [PrimaryKey, AutoInc] public ulong ExpiryId;
        [SpacetimeDB.Index.BTree] public ulong JobId;
        public ScheduleAt At;
    }

    [View(Accessor = "MyHiveJobs", Public = true)]
    public static List<HiveJob> MyHiveJobs(ViewContext ctx) => ctx.Db.HiveJob.Owner.Filter(ctx.Sender).ToList();

    /// <summary>Which of the caller's farm plants a droid is tending (the client badges them and offers no harvest).</summary>
    [View(Accessor = "MyHiveJobPlants", Public = true)]
    public static List<HiveJobPlant> MyHiveJobPlants(ViewContext ctx) => ctx.Db.HiveJobPlant.Owner.Filter(ctx.Sender).ToList();

    [View(Accessor = "MyHiveDeliveries", Public = true)]
    public static List<HiveDelivery> MyHiveDeliveries(ViewContext ctx) => ctx.Db.HiveDelivery.Owner.Filter(ctx.Sender).ToList();

    // ------------------------------------------------------------------ pricing (operator)

    [Reducer]
    public static void ConfigureHiveLabor(ReducerContext ctx, ulong baseMicros, ulong microsPerGold, uint profitShareBps,
        ulong microsPerGrowMinute, ulong minMicros, ulong maxMicros, ulong maxOrderMicros, uint plotsPerDroid, bool enabled)
    {
        RequireAgentOperator(ctx);
        if (profitShareBps > 10_000 || minMicros == 0 || minMicros > maxMicros || maxOrderMicros < maxMicros ||
            plotsPerDroid == 0 || plotsPerDroid > 32) throw new Exception("Invalid Hive labour configuration.");
        var row = new HiveLaborConfig { Id = 0, BaseMicros = baseMicros, MicrosPerGold = microsPerGold,
            ProfitShareBps = profitShareBps, MicrosPerGrowMinute = microsPerGrowMinute, MinMicros = minMicros,
            MaxMicros = maxMicros, MaxOrderMicros = maxOrderMicros, PlotsPerDroid = plotsPerDroid, Enabled = enabled };
        if (ctx.Db.HiveLaborConfig.Id.Find(0) == null) ctx.Db.HiveLaborConfig.Insert(row);
        else ctx.Db.HiveLaborConfig.Id.Update(row);
        PriceHiveCrops(ctx, row);
    }

    /// <summary>Default labour pricing (once per database) and a price for every enabled crop. Re-run after crop
    /// changes: it re-prices crops from the current config and keeps operator overrides' enabled flags.</summary>
    [Reducer]
    public static void SeedHiveLabor(ReducerContext ctx)
    {
        RequireAgentOperator(ctx);
        var config = ctx.Db.HiveLaborConfig.Id.Find(0) ?? ctx.Db.HiveLaborConfig.Insert(DefaultHiveLabor);
        PriceHiveCrops(ctx, config);
    }

    /// <summary>Operator override for one crop (after SeedHiveLabor, which would re-derive it).</summary>
    [Reducer]
    public static void SetHiveCropRate(ReducerContext ctx, uint plantIndex, ulong microsPerCrop, bool enabled)
    {
        RequireAgentOperator(ctx);
        if (microsPerCrop == 0) throw new Exception("A crop rate must be above zero.");
        var old = ctx.Db.HiveCropRate.PlantIndex.Find(plantIndex) ?? throw new Exception("Price the crops first (SeedHiveLabor).");
        ctx.Db.HiveCropRate.PlantIndex.Update(old with { MicrosPerCrop = microsPerCrop, Enabled = enabled });
    }

    // 1 Gold of profit = 0.01 USDC, the droid takes half of it, plus 0.02 USDC a crop and 0.01 USDC a minute of growing.
    internal static readonly HiveLaborConfig DefaultHiveLabor = new()
    {
        Id = 0, BaseMicros = 20_000, MicrosPerGold = 10_000, ProfitShareBps = 5_000, MicrosPerGrowMinute = 10_000,
        MinMicros = 10_000, MaxMicros = 2_000_000, MaxOrderMicros = 10_000_000, PlotsPerDroid = 8, Enabled = true,
    };

    private static void PriceHiveCrops(ReducerContext ctx, HiveLaborConfig config)
    {
        foreach (var crop in ctx.Db.FarmCrop.Iter())
        {
            if (ctx.Db.FarmCropVersion.Key.Find(CropVersionKey(crop.PlantIndex, crop.ActiveVersion)) is not FarmCropVersion definition)
                continue;
            var price = HivePricing.Price(config, definition.HarvestItemId, definition.HarvestQuantity,
                definition.SeedItemId, definition.StageStartsSeconds[^1]);
            var old = ctx.Db.HiveCropRate.PlantIndex.Find(crop.PlantIndex);
            var row = new HiveCropRate { PlantIndex = crop.PlantIndex, MicrosPerCrop = price.Micros,
                ProfitGold = price.ProfitGold, GrowSeconds = definition.StageStartsSeconds[^1],
                Enabled = old?.Enabled ?? true };
            if (old == null) ctx.Db.HiveCropRate.Insert(row);
            else ctx.Db.HiveCropRate.PlantIndex.Update(row);
        }
    }

    // ------------------------------------------------------------------ the order (player)

    [Reducer]
    public static void CreateHiveJob(ReducerContext ctx, string clientJobId, string site, string droidId,
        uint plantIndex, uint target)
    {
        var player = RequireFarmPlayer(ctx);
        RequireHiveId(clientJobId, nameof(clientJobId));
        RequireHiveId(droidId, nameof(droidId));
        if (ctx.Db.HiveJob.ClientJobId.Find(clientJobId) != null) throw new Exception("This job already exists.");
        var range = HiveSiteTiles(site);
        var config = ctx.Db.HiveLaborConfig.Id.Find(0);
        if (config == null || !config.Value.Enabled) throw new Exception("The Hive is not taking jobs right now.");
        foreach (var other in ctx.Db.HiveJob.Owner.Filter(ctx.Sender))
            if (other.Site == site && (other.Status == HiveJobAwaitingFunding || other.Status == HiveJobActive))
                throw new Exception("A droid is already working for you at this farm.");

        if (!HiveSiteGrows(site, plantIndex)) throw new Exception(IsDesertCrop(plantIndex)
            ? "Desert crops only grow on the desert farm." : "That crop doesn't grow at this farm.");
        var crop = ctx.Db.FarmCrop.PlantIndex.Find(plantIndex);
        if (crop == null || !crop.Value.Enabled) throw new Exception("This crop is not available for planting.");
        var rate = ctx.Db.HiveCropRate.PlantIndex.Find(plantIndex);
        if (rate == null || !rate.Value.Enabled) throw new Exception("The Hive doesn't grow this crop yet.");
        var definition = ctx.Db.FarmCropVersion.Key.Find(CropVersionKey(plantIndex, crop.Value.ActiveVersion))
            ?? throw new Exception("Missing crop definition.");
        var curve = ctx.Db.XpTrack.Id.Find("farming") ?? throw new Exception("Farming progression is not configured.");
        ulong xp = ctx.Db.PlayerXp.Key.Find(ctx.Sender.ToString() + ":farming")?.Xp ?? 0;
        if (CalculateXpLevel(xp, curve.Thresholds, curve.LevelCap) < definition.MinimumFarmingLevel)
            throw new Exception($"Farming level {definition.MinimumFarmingLevel} required.");

        ulong perCrop = rate.Value.MicrosPerCrop;
        if (target == 0 || target > HiveMaxTarget) throw new Exception($"Order between 1 and {HiveMaxTarget} crops.");
        ulong escrow = checked(perCrop * target);
        if (escrow > config.Value.MaxOrderMicros)
            throw new Exception($"That order is over {config.Value.MaxOrderMicros / 1_000_000.0:0.##} USDC. Order fewer crops.");
        uint seeds = HiveSeedsFor(target, definition.HarvestQuantity);
        TakeFarmItems(ctx, player.player_id, definition.SeedItemId, seeds);   // "Not enough seeds." when short

        var job = ctx.Db.HiveJob.Insert(new HiveJob
        {
            JobId = 0, ClientJobId = clientJobId, Owner = ctx.Sender, PlayerId = player.player_id, Site = site,
            DroidId = droidId, PlantIndex = plantIndex, SeedItemId = definition.SeedItemId,
            HarvestItemId = definition.HarvestItemId, Target = target, Delivered = 0, SeedsHeld = seeds, Growing = 0,
            MicrosPerCrop = perCrop, EscrowMicros = escrow, PaidOutMicros = 0, RefundMicros = 0,
            Status = HiveJobAwaitingFunding, FundingReference = "", CreatedAt = ctx.Timestamp,
            FundedAt = ctx.Timestamp, DeadlineAt = ctx.Timestamp + TimeSpan.FromSeconds(HiveFundingWindowSeconds),
            UpdatedAt = ctx.Timestamp, Note = "AWAITING PAYMENT",
        });
        ctx.Db.HiveFundingExpiry.Insert(new HiveFundingExpiry { ExpiryId = 0, JobId = job.JobId,
            At = new ScheduleAt.Time(job.DeadlineAt) });
    }

    /// <summary>The owner stops a job. Unfunded: nothing was paid, seeds go back. Active: the droid stops, its crops in
    /// the ground become the player's, seeds go back and the undelivered share is refunded.</summary>
    [Reducer]
    public static void CancelHiveJob(ReducerContext ctx, ulong jobId)
    {
        RequireFarmPlayer(ctx);
        var job = ctx.Db.HiveJob.JobId.Find(jobId) ?? throw new Exception("Job not found.");
        if (job.Owner != ctx.Sender) throw new Exception("This job is not yours.");
        if (job.Status == HiveJobAwaitingFunding) { CloseUnfundedHiveJob(ctx, job, HiveJobCancelled, "CANCELLED"); return; }
        if (job.Status != HiveJobActive) throw new Exception("This job has already ended.");
        EndHiveJob(ctx, job, HiveJobCancelled, "CANCELLED: UNDELIVERED SHARE REFUNDED");
    }

    // ------------------------------------------------------------------ escrow (settlement worker)

    /// <summary>The worker saw the NEAR operation <paramref name="fundingReference"/> settle into the Hive escrow.
    /// Idempotent for the same reference. Funding for a job that already closed unfunded is refunded in full.</summary>
    [Reducer]
    public static void ConfirmHiveJobFunding(ReducerContext ctx, string clientJobId, string fundingReference,
        ulong amountMicros, string payerWallet)
    {
        RequireHiveSettlementAuthority(ctx);
        RequireHiveReference(fundingReference);
        var job = ctx.Db.HiveJob.ClientJobId.Find(clientJobId) ?? throw new Exception("Job not found.");
        if (job.FundingReference == fundingReference) return;                   // a retried confirmation
        if (!string.IsNullOrEmpty(job.FundingReference)) throw new Exception("This job was funded by another operation.");
        string wallet = HivePlayerWallet(ctx, job);
        if (string.IsNullOrEmpty(wallet) || wallet != payerWallet) throw new Exception("The payer is not this job's player.");
        job.FundingReference = fundingReference;
        if (job.Status != HiveJobAwaitingFunding)
        {
            // Paid after it closed (expired, cancelled or failed while the payment was in flight): give it all back.
            job.RefundMicros = checked(job.RefundMicros + amountMicros);
            job.Note = "PAID AFTER CLOSING: FULL REFUND";
            job.UpdatedAt = ctx.Timestamp;
            ctx.Db.HiveJob.JobId.Update(job);
            QueueHiveSettlement(ctx, job, HiveSettlementRefund, 0, amountMicros);
            return;
        }
        if (amountMicros < job.EscrowMicros) throw new Exception("The payment is short of the order's price.");
        foreach (var expiry in ctx.Db.HiveFundingExpiry.JobId.Filter(job.JobId).ToList())
            ctx.Db.HiveFundingExpiry.ExpiryId.Delete(expiry.ExpiryId);
        var config = ctx.Db.HiveLaborConfig.Id.Find(0) ?? DefaultHiveLabor;
        var rate = ctx.Db.HiveCropRate.PlantIndex.Find(job.PlantIndex);
        uint grow = rate?.GrowSeconds ?? 600;
        uint rounds = (job.SeedsHeld + config.PlotsPerDroid - 1) / config.PlotsPerDroid;
        job.Status = HiveJobActive;
        job.FundedAt = ctx.Timestamp;
        job.DeadlineAt = ctx.Timestamp + TimeSpan.FromSeconds(HiveJobDeadlineSeconds(rounds, grow));
        job.Note = "DROID DISPATCHED";
        if (amountMicros > job.EscrowMicros)   // overpaid (slippage in the player's favour): return the difference
        {
            job.RefundMicros = checked(job.RefundMicros + (amountMicros - job.EscrowMicros));
            QueueHiveSettlement(ctx, job, HiveSettlementRefund, 0, amountMicros - job.EscrowMicros);
        }
        job.UpdatedAt = ctx.Timestamp;
        ctx.Db.HiveJob.JobId.Update(job);
        ctx.Db.HiveJobTick.Insert(new HiveJobTick { TickId = 0, JobId = job.JobId, At = new ScheduleAt.Time(ctx.Timestamp) });
    }

    /// <summary>The payment failed or was refunded by the route: the job closes and the seeds go back.</summary>
    [Reducer]
    public static void RejectHiveJobFunding(ReducerContext ctx, string clientJobId, string failureReference)
    {
        RequireHiveSettlementAuthority(ctx);
        RequireHiveReference(failureReference);
        var job = ctx.Db.HiveJob.ClientJobId.Find(clientJobId) ?? throw new Exception("Job not found.");
        if (job.Status != HiveJobAwaitingFunding) return;
        CloseUnfundedHiveJob(ctx, job, HiveJobFailed, "PAYMENT FAILED");
    }

    /// <summary>The worker paid (payout) or refunded (refund) one settlement row.</summary>
    [Reducer]
    public static void ConfirmHiveSettlement(ReducerContext ctx, ulong settlementId, string reference)
    {
        RequireHiveSettlementAuthority(ctx);
        RequireHiveReference(reference);
        var row = ctx.Db.HiveSettlement.SettlementId.Find(settlementId) ?? throw new Exception("Settlement not found.");
        if (row.Status == HiveSettlementSettled)
        {
            if (row.Reference == reference) return;
            throw new Exception("This settlement was confirmed with another reference.");
        }
        ctx.Db.HiveSettlement.SettlementId.Update(row with { Status = HiveSettlementSettled, Reference = reference,
            SettledAt = ctx.Timestamp });
    }

    // ------------------------------------------------------------------ the droid (scheduler)

    [Reducer]
    public static void RunHiveJob(ReducerContext ctx, HiveJobTick tick)
    {
        if (ctx.Sender != ctx.Identity) throw new Exception("Hive droids are run by the scheduler only.");
        if (ctx.Db.HiveJob.JobId.Find(tick.JobId) is not HiveJob job || job.Status != HiveJobActive) return;
        var config = ctx.Db.HiveLaborConfig.Id.Find(0) ?? DefaultHiveLabor;

        // 1. Tend: harvest what is ripe (a verified delivery), water the rest.
        uint growing = 0;
        foreach (var tended in ctx.Db.HiveJobPlant.JobId.Filter(job.JobId).ToList())
        {
            if (ctx.Db.FarmPlant.PlantId.Find(tended.PlantId) is not FarmPlant plant || plant.Owner != job.Owner)
            {
                ctx.Db.HiveJobPlant.PlantId.Delete(tended.PlantId);   // gone (should not happen: harvest is guarded)
                continue;
            }
            var definition = GetFarmDefinition(ctx, plant);
            plant = SettleFarmGrowth(ctx, plant, definition);
            if (IsFarmMature(plant, definition))
            {
                DeliverHiveCrop(ctx, ref job, plant, definition);
                ctx.Db.FarmPlant.PlantId.Delete(plant.PlantId);
                ctx.Db.HiveJobPlant.PlantId.Delete(tended.PlantId);
                continue;
            }
            var until = ctx.Timestamp + TimeSpan.FromSeconds(Math.Max(definition.WaterDurationSeconds, (uint)HiveTickSeconds * 3));
            if (plant.WateredUntil.MicrosecondsSinceUnixEpoch < until.MicrosecondsSinceUnixEpoch) plant.WateredUntil = until;
            ctx.Db.FarmPlant.PlantId.Update(plant);   // settled growth + water; Revision untouched like device watering
            growing++;
        }
        job.Growing = growing;

        // 2. Done, or out of time.
        if (job.Delivered >= job.Target) { EndHiveJob(ctx, job, HiveJobCompleted, "DELIVERED"); return; }
        if (ctx.Timestamp.MicrosecondsSinceUnixEpoch >= job.DeadlineAt.MicrosecondsSinceUnixEpoch)
        {
            EndHiveJob(ctx, job, HiveJobExpired, "TIME UP: UNDELIVERED SHARE REFUNDED");
            return;
        }

        // 3. Plant held seeds on free plots of this kiosk's farm.
        var active = ctx.Db.FarmCrop.PlantIndex.Find(job.PlantIndex);
        var plantDefinition = active == null ? null
            : ctx.Db.FarmCropVersion.Key.Find(CropVersionKey(job.PlantIndex, active.Value.ActiveVersion));
        bool waitingForPlot = false;
        if (plantDefinition is FarmCropVersion seedDefinition && active!.Value.Enabled)
        {
            uint perPlant = Math.Max(1u, seedDefinition.HarvestQuantity);
            var range = HiveSiteTiles(job.Site);
            for (uint tile = range.First; tile < range.End && job.SeedsHeld > 0 && growing < config.PlotsPerDroid; tile++)
            {
                if (job.Delivered + growing * perPlant >= job.Target) break;
                if (!HivePlotFree(ctx, job.Owner, tile, job.PlantIndex)) continue;
                PlantForHiveJob(ctx, job, tile, seedDefinition);
                job.SeedsHeld--;
                growing++;
            }
            waitingForPlot = growing == 0 && job.SeedsHeld > 0;
        }
        job.Growing = growing;
        job.Note = waitingForPlot ? "WAITING FOR A FREE PLOT" : growing > 0 ? $"TENDING {growing} PLOT{(growing == 1 ? "" : "S")}" : "WORKING";
        job.UpdatedAt = ctx.Timestamp;
        ctx.Db.HiveJob.JobId.Update(job);
        ctx.Db.HiveJobTick.Insert(new HiveJobTick { TickId = 0, JobId = job.JobId,
            At = new ScheduleAt.Time(ctx.Timestamp + TimeSpan.FromSeconds(HiveTickSeconds)) });
    }

    [Reducer]
    public static void ExpireHiveFunding(ReducerContext ctx, HiveFundingExpiry expiry)
    {
        if (ctx.Sender != ctx.Identity) throw new Exception("Funding windows close on the scheduler only.");
        if (ctx.Db.HiveJob.JobId.Find(expiry.JobId) is not HiveJob job || job.Status != HiveJobAwaitingFunding) return;
        CloseUnfundedHiveJob(ctx, job, HiveJobExpired, "PAYMENT NOT RECEIVED IN TIME");
    }

    // ------------------------------------------------------------------ guards used by Farming.cs

    /// <summary>HarvestPlant: a crop a droid is tending is harvested by the droid (that harvest is the delivery).</summary>
    private static void RequireNotHiveTended(ReducerContext ctx, ulong plantId)
    {
        if (ctx.Db.HiveJobPlant.PlantId.Find(plantId) != null)
            throw new Exception("A Hive droid is tending this crop. It harvests it for your order.");
    }

    // ------------------------------------------------------------------ helpers

    internal readonly record struct HiveTiles(uint First, uint End);

    /// <summary>Each kiosk works the farm beside it: MainGame's farm (tiles 0-87) or the desert farm (1000-1031).</summary>
    internal static HiveTiles HiveSiteTiles(string site) => site switch
    {
        HiveSiteFarm => new HiveTiles(0, 88),
        HiveSiteDesert => new HiveTiles(DesertFarmFirstTile, DesertFarmFirstTile + DesertFarmTileCount),
        _ => throw new Exception("Unknown Hive Link site."),
    };

    /// <summary>The crops a kiosk's farm grows: desert crops at the desert kiosk, every other crop at the farm.</summary>
    public static bool HiveSiteGrows(string site, uint plantIndex) => SoilAcceptsCrop(HiveSiteTiles(site).First, plantIndex);

    internal static uint HiveSeedsFor(uint target, uint harvestQuantity) =>
        (target + Math.Max(1u, harvestQuantity) - 1) / Math.Max(1u, harvestQuantity);

    /// <summary>Time for the rounds of planting the order needs, twice over, plus 10 minutes; at least an hour.</summary>
    internal static ulong HiveJobDeadlineSeconds(uint rounds, uint growSeconds) =>
        Math.Max(3600UL, (ulong)Math.Max(1u, rounds) * Math.Max(60u, growSeconds) * 2 + 600);

    private static bool HivePlotFree(ReducerContext ctx, Identity owner, uint tile, uint plantIndex)
    {
        if (ctx.Db.FarmPlotDefinition.TileIndex.Find(tile) is not FarmPlotDefinition definition || !definition.Enabled) return false;
        if (!SoilAcceptsCrop(tile, plantIndex)) return false;
        string key = FarmPlotKey(owner, tile);
        return ctx.Db.FarmPlant.PlotKey.Find(key) == null && ctx.Db.FarmDevice.PlotKey.Find(key) == null;
    }

    private static void PlantForHiveJob(ReducerContext ctx, HiveJob job, uint tile, FarmCropVersion definition)
    {
        string key = FarmPlotKey(job.Owner, tile);
        var plant = ctx.Db.FarmPlant.Insert(new FarmPlant { PlantId = 0, PlotKey = key, Owner = job.Owner,
            TileIndex = tile, PlantIndex = job.PlantIndex, DefinitionVersion = definition.Version,
            PlantedAt = ctx.Timestamp, GrowthUpdatedAt = ctx.Timestamp,
            WateredUntil = ctx.Timestamp + TimeSpan.FromSeconds(Math.Max(definition.WaterDurationSeconds, (uint)HiveTickSeconds * 3)),
            GrowthMicros = 0, GrowthFraction = 0, GrowthRateBps = FarmGrowth.NormalRate,
            FertilizerIndex = null, FertilizerVersion = 0, Revision = 0 });
        // Bump the plot revision like PlantSeed, so a stale planting request from the player's client fails cleanly.
        var plot = ctx.Db.FarmPlot.Key.Find(key);
        var next = new FarmPlot { Key = key, Owner = job.Owner, TileIndex = tile, Revision = checked((plot?.Revision ?? 0) + 1) };
        if (plot == null) ctx.Db.FarmPlot.Insert(next); else ctx.Db.FarmPlot.Key.Update(next);
        ctx.Db.HiveJobPlant.Insert(new HiveJobPlant { PlantId = plant.PlantId, JobId = job.JobId, Owner = job.Owner, TileIndex = tile });
    }

    private static void DeliverHiveCrop(ReducerContext ctx, ref HiveJob job, FarmPlant plant, FarmCropVersion definition)
    {
        if (ctx.Db.HiveDelivery.PlantId.Find(plant.PlantId) != null) return;   // one delivery per plant
        AddFarmHarvestItem(ctx, job.PlayerId, definition.HarvestItemId, definition.HarvestQuantity);
        uint counted = Math.Min(definition.HarvestQuantity, job.Target - Math.Min(job.Target, job.Delivered));
        ulong payout = checked(job.MicrosPerCrop * counted);
        var delivery = ctx.Db.HiveDelivery.Insert(new HiveDelivery { DeliveryId = 0, PlantId = plant.PlantId,
            JobId = job.JobId, Owner = job.Owner, TileIndex = plant.TileIndex, ItemId = definition.HarvestItemId,
            Quantity = counted, PayoutMicros = payout, DeliveredAt = ctx.Timestamp });
        job.Delivered = checked(job.Delivered + counted);
        job.PaidOutMicros = checked(job.PaidOutMicros + payout);
        if (payout > 0) QueueHiveSettlement(ctx, job, HiveSettlementPayout, delivery.DeliveryId, payout);
    }

    /// <summary>Closes an active job: crops still growing become the player's own, seeds go back, the rest of the
    /// escrow is refunded.</summary>
    private static void EndHiveJob(ReducerContext ctx, HiveJob job, byte status, string note)
    {
        foreach (var tended in ctx.Db.HiveJobPlant.JobId.Filter(job.JobId).ToList())
            ctx.Db.HiveJobPlant.PlantId.Delete(tended.PlantId);
        foreach (var tick in ctx.Db.HiveJobTick.JobId.Filter(job.JobId).ToList())
            ctx.Db.HiveJobTick.TickId.Delete(tick.TickId);
        if (job.SeedsHeld > 0) AddFarmHarvestItem(ctx, job.PlayerId, job.SeedItemId, job.SeedsHeld);
        job.SeedsHeld = 0;
        job.Growing = 0;
        ulong left = job.EscrowMicros - Math.Min(job.EscrowMicros, job.PaidOutMicros);
        if (left > 0)
        {
            job.RefundMicros = checked(job.RefundMicros + left);
            QueueHiveSettlement(ctx, job, HiveSettlementRefund, 0, left);
        }
        job.Status = status;
        job.Note = note;
        job.UpdatedAt = ctx.Timestamp;
        ctx.Db.HiveJob.JobId.Update(job);
        if (ctx.Db.HiveJobClosure.JobId.Find(job.JobId) is null)
            ctx.Db.HiveJobClosure.Insert(new HiveJobClosure { JobId = job.JobId, Status = status, ClosedAt = ctx.Timestamp });
    }

    private static void CloseUnfundedHiveJob(ReducerContext ctx, HiveJob job, byte status, string note)
    {
        foreach (var expiry in ctx.Db.HiveFundingExpiry.JobId.Filter(job.JobId).ToList())
            ctx.Db.HiveFundingExpiry.ExpiryId.Delete(expiry.ExpiryId);
        if (job.SeedsHeld > 0) AddFarmHarvestItem(ctx, job.PlayerId, job.SeedItemId, job.SeedsHeld);
        job.SeedsHeld = 0;
        job.Status = status;
        job.Note = note;
        job.UpdatedAt = ctx.Timestamp;
        ctx.Db.HiveJob.JobId.Update(job);
    }

    private static void QueueHiveSettlement(ReducerContext ctx, HiveJob job, byte kind, ulong deliveryId, ulong micros)
    {
        ctx.Db.HiveSettlement.Insert(new HiveSettlement { SettlementId = 0, JobId = job.JobId, DeliveryId = deliveryId,
            Kind = kind, DroidId = job.DroidId, FundingReference = job.FundingReference, Micros = micros,
            EscrowMicros = job.EscrowMicros, Status = HiveSettlementPending, Reference = "",
            CreatedAt = ctx.Timestamp, SettledAt = ctx.Timestamp });
    }

    /// <summary>Removes <paramref name="count"/> of an item from the bag in one step (the job holds them).</summary>
    private static void TakeFarmItems(ReducerContext ctx, uint playerId, int itemId, uint count)
    {
        var inventory = ctx.Db.PlayerInventory.player_id.Find(playerId) ?? throw new Exception("Not enough seeds.");
        var items = new List<ItemCountPair>(inventory.items);
        int index = UniqueFarmItemIndex(items, itemId);
        if (index < 0 || items[index].count < count) throw new Exception("Not enough seeds.");
        var item = items[index];
        item.count -= count;
        if (item.count == 0) items.RemoveAt(index); else items[index] = item;
        ctx.Db.PlayerInventory.player_id.Update(inventory with { items = items });
    }

    /// <summary>
    /// The Solana wallet that must have paid for a job: the owner's verified sign-in wallet (GameAdmission, written from
    /// the session JWT), else the player row's wallet_address. 14 Nov 2026: on bakeland-test the player row's
    /// wallet_address was empty, so a real payment from the player's own wallet was refused as "not this job's player".
    /// </summary>
    private static string HivePlayerWallet(ReducerContext ctx, HiveJob job)
    {
        string admitted = ctx.Db.GameAdmission.Identity.Find(job.Owner)?.Wallet;
        if (!string.IsNullOrEmpty(admitted)) return admitted;
        return (ctx.Db.Player.player_id.Find(job.PlayerId) ?? ctx.Db.LoggedOutPlayer.player_id.Find(job.PlayerId))?.wallet_address ?? "";
    }

    private static void RequireHiveId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > HiveMaxIdLength ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')))
            throw new Exception($"Invalid {name}.");
    }

    private static void RequireHiveReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > HiveMaxReferenceLength)
            throw new Exception("A valid settlement reference is required.");
    }

    /// <summary>
    /// Identities allowed to confirm Hive funding and settlements: the inventory API's settlement worker, registered by
    /// an operator. A dedicated allowlist, so no player session can ever confirm its own funding.
    /// </summary>
    [Table(Accessor = "HiveSettlementAuthority")]
    public partial struct HiveSettlementAuthority
    {
        [PrimaryKey] public Identity Identity;
        public string Label;
        public Timestamp AddedAt;
    }

    /// <summary>Operator: allows (or removes) an identity as the Hive settlement authority.</summary>
    [Reducer]
    public static void SetHiveSettlementAuthority(ReducerContext ctx, Identity identity, string label, bool allowed)
    {
        RequireAgentOperator(ctx);
        ctx.Db.HiveSettlementAuthority.Identity.Delete(identity);
        if (!allowed) return;
        if (string.IsNullOrWhiteSpace(label) || label.Length > HiveMaxIdLength) throw new Exception("A short label is required.");
        ctx.Db.HiveSettlementAuthority.Insert(new HiveSettlementAuthority { Identity = identity, Label = label, AddedAt = ctx.Timestamp });
    }

    private static void RequireHiveSettlementAuthority(ReducerContext ctx)
    {
        if (ctx.Db.HiveSettlementAuthority.Identity.Find(ctx.Sender) is null)
            throw new Exception("Only the Hive settlement authority can confirm Hive payments.");
    }
}

/// <summary>Pure pricing, shared by the server and its tests.</summary>
internal static class HivePricing
{
    internal readonly record struct Result(ulong Micros, long ProfitGold);

    /// <summary>Per crop delivered: base + the droid's share of the crop's profit (harvest sell value minus the seed's
    /// shop price, in Gold, converted to USDC) + tending time; clamped and rounded up to 0.001 USDC.</summary>
    internal static Result Price(Module.HiveLaborConfig config, int harvestItemId, uint harvestQuantity, int seedItemId,
        uint growSeconds)
    {
        long sell = EconomyCatalog.Items.TryGetValue(harvestItemId, out var harvest) ? harvest.Sell : 0;
        long seed = EconomyCatalog.Items.TryGetValue(seedItemId, out var seedItem) ? seedItem.Buy : 0;
        long profit = sell * Math.Max(1u, harvestQuantity) - seed;
        ulong profitPerCrop = profit <= 0 ? 0 : (ulong)profit / Math.Max(1u, harvestQuantity);
        UInt128 share = (UInt128)profitPerCrop * config.MicrosPerGold * config.ProfitShareBps / 10_000;
        UInt128 time = (UInt128)growSeconds * config.MicrosPerGrowMinute / 60;
        UInt128 total = config.BaseMicros + share + time;
        ulong micros = total > config.MaxMicros ? config.MaxMicros : (ulong)total;
        micros = Math.Max(config.MinMicros, micros);
        micros = (micros + 999) / 1000 * 1000;
        return new Result(Math.Min(micros, config.MaxMicros), profit);
    }
}
