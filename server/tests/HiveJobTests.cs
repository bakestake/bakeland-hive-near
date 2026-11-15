using SpacetimeDB;

// Hive Link droid jobs (HiveJobs.cs): pricing by crop value, per-site crops and plots, seeds held by the job, escrow
// funding by the settlement authority only (payer wallet, amount, idempotency, late funding refunded), the droid on
// the scheduler (plants free plots of the kiosk's farm, keeps them watered, harvests at maturity as a verified
// delivery with one payout each), the player blocked from harvesting droid crops, completion, cancellation, unfunded
// expiry and the deadline (crops handed over, seeds back, the undelivered share refunded), and settlement confirmation.
public static class HiveJobTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool success, string message) { if (!success) throw new Exception(message); checks++; }
        void Reject(Action action, string contains)
        {
            try { action(); }
            catch (Exception e) when (e.Message.Contains(contains)) { checks++; return; }
            catch (Exception e) { throw new Exception($"Expected rejection '{contains}', got '{e.Message}'"); }
            throw new Exception("Expected rejection: " + contains);
        }

        var db = new TestDatabase();
        var op = new Identity("operator");
        var alice = new Identity("alice");
        var bob = new Identity("bob");
        var worker = new Identity("settlement");
        var scheduler = new Identity("module");
        db.GuideOperator.Insert(new Module.GuideOperator { id = 0, identity = op });
        // Only an operator can register the settlement worker; nobody else is one by default.
        Reject(() => Tx(() => Module.SetHiveSettlementAuthority(C(worker, 0), worker, "inventory-api", true)), "operator");
        Tx(() => Module.SetHiveSettlementAuthority(C(op, 0), worker, "inventory-api", true));
        ReducerContext C(Identity user, long seconds) => new(db, user, new Timestamp(seconds * 1_000_000));
        void Tx(Action action) => db.Transaction(action);
        db.Player.Insert(new Module.Player { identity = alice, player_id = 1, wallet_address = "AliceWallet" });
        db.Player.Insert(new Module.Player { identity = bob, player_id = 2, wallet_address = "BobWallet" });
        foreach (var id in new[] { alice, bob })
            // The admission wallet is the verified sign-in wallet the payer must match (HivePlayerWallet).
            db.GameAdmission.Insert(new Module.GameAdmission { Identity = id, Wallet = id == alice ? "AliceWallet" : "BobWallet", ExpiresAt = new Timestamp(long.MaxValue) });
        db.PlayerInventory.Insert(new Module.PlayerInventory { player_id = 1, gold = 0, items = new() {
            new() { itemId = 31, count = 40 },   // carrot seeds
            new() { itemId = 76, count = 10 },    // dune sage seeds
            new() { itemId = 18, count = 12 } } }); // mugwort seeds
        db.PlayerInventory.Insert(new Module.PlayerInventory { player_id = 2, gold = 0, items = new() { new() { itemId = 31, count = 5 } } });
        Tx(() => Module.SeedXpConfiguration(C(op, 0)));
        Tx(() => Module.SeedUnityFarmConfiguration(C(op, 0)));
        Tx(() => Module.SeedDesertFarmConfiguration(C(op, 0)));
        uint Count(uint player, int item) => db.PlayerInventory.player_id.Find(player)!.Value.items.Where(i => i.itemId == item).Sum(i => (int)i.count) is int n ? (uint)n : 0;
        Module.HiveJob Job(string id) => db.HiveJob.ClientJobId.Find(id)!.Value;
        void Tick(string id, long seconds)
        {
            var job = Job(id);
            var tick = db.HiveJobTick.JobId.Filter(job.JobId).OrderBy(t => t.TickId).First();
            db.HiveJobTick.TickId.Delete(tick.TickId);   // SpacetimeDB drops a fired one-shot schedule row
            Tx(() => Module.RunHiveJob(C(scheduler, seconds), tick));
        }

        // ---- pricing
        Reject(() => Tx(() => Module.SeedHiveLabor(C(alice, 0))), "operator");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job-early", "farm", "HX-07", 31, 1)), "not taking jobs");
        Tx(() => Module.SeedHiveLabor(C(op, 0)));
        var carrotRate = db.HiveCropRate.PlantIndex.Find(31)!.Value;
        var sageRate = db.HiveCropRate.PlantIndex.Find(76)!.Value;
        Check(db.HiveCropRate.Iter().Count() == db.FarmCrop.Iter().Count(), "Every crop gets a labour rate.");
        Check(db.HiveCropRate.Iter().All(r => r.MicrosPerCrop >= 10_000 && r.MicrosPerCrop % 1000 == 0),
            "Rates respect the floor and round to 0.001 USDC.");
        var pumpkin = db.HiveCropRate.PlantIndex.Find(28)!.Value;
        Check(pumpkin.MicrosPerCrop > carrotRate.MicrosPerCrop, "A more valuable, slower crop costs more labour.");
        foreach (var r in db.HiveCropRate.Iter().OrderBy(r => r.PlantIndex))
            Console.WriteLine($"  hive rate plant {r.PlantIndex,3}: {r.MicrosPerCrop / 1_000_000.0:0.000} USDC/crop (profit {r.ProfitGold} G, grow {r.GrowSeconds} s)");

        // ---- creating an order
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job-x", "moon", "HX-07", 31, 1)), "Unknown Hive Link site");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job-x", "farm", "HX-07", 76, 1)), "desert farm");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job-x", "desert-village-edge", "HX-07", 31, 1)), "doesn't grow");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job x", "farm", "HX-07", 31, 1)), "Invalid clientJobId");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job-x", "farm", "HX-07", 31, 0)), "between 1 and");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 0), "job-x", "farm", "HX-07", 31, 41)), "Not enough seeds");
        ulong cap = Module.DefaultHiveLabor.MaxOrderMicros / carrotRate.MicrosPerCrop + 1;
        if (cap <= 200) Reject(() => Tx(() => Module.CreateHiveJob(C(bob, 0), "job-x", "farm", "HX-07", 31, (uint)cap)), "Order fewer");
        Check(Count(1, 31) == 40, "A rejected order keeps the seeds.");

        // Alice already grows something on tile 0: the droid must leave it alone.
        Tx(() => Module.PlantSeed(C(alice, 0), 0, 31, 0));
        Tx(() => Module.CreateHiveJob(C(alice, 10), "job-carrot", "farm", "HX-07", 31, 5));
        var carrot = Job("job-carrot");
        Check(carrot.Status == Module.HiveJobAwaitingFunding && carrot.SeedsHeld == 5 && Count(1, 31) == 34,
            "An order holds its seeds out of the bag until it ends.");
        Check(carrot.EscrowMicros == carrotRate.MicrosPerCrop * 5, "Price = rate x quantity.");
        Check(db.HiveFundingExpiry.JobId.Filter(carrot.JobId).Count() == 1, "An unfunded order has a funding window.");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 10), "job-carrot2", "farm", "HX-07", 31, 1)), "already working for you");
        Reject(() => Tx(() => Module.CreateHiveJob(C(alice, 10), "job-carrot", "desert-village-edge", "HX-07", 76, 1)), "already exists");

        // ---- funding (settlement authority)
        Reject(() => Tx(() => Module.ConfirmHiveJobFunding(C(alice, 20), "job-carrot", "near-op-1", carrot.EscrowMicros, "AliceWallet")), "settlement authority");
        Reject(() => Tx(() => Module.ConfirmHiveJobFunding(C(worker, 20), "job-carrot", "near-op-1", carrot.EscrowMicros, "BobWallet")), "payer");
        Reject(() => Tx(() => Module.ConfirmHiveJobFunding(C(worker, 20), "job-carrot", "near-op-1", carrot.EscrowMicros - 1, "AliceWallet")), "short");
        Tx(() => Module.ConfirmHiveJobFunding(C(worker, 20), "job-carrot", "near-op-1", carrot.EscrowMicros, "AliceWallet"));
        Tx(() => Module.ConfirmHiveJobFunding(C(worker, 21), "job-carrot", "near-op-1", carrot.EscrowMicros, "AliceWallet"));
        Reject(() => Tx(() => Module.ConfirmHiveJobFunding(C(worker, 22), "job-carrot", "near-op-2", carrot.EscrowMicros, "AliceWallet")), "another operation");
        carrot = Job("job-carrot");
        Check(carrot.Status == Module.HiveJobActive && db.HiveFundingExpiry.JobId.Filter(carrot.JobId).Count() == 0
            && db.HiveJobTick.JobId.Filter(carrot.JobId).Count() == 1, "Funding activates the job once and starts the droid.");

        // ---- the droid
        Reject(() => Tx(() => Module.RunHiveJob(C(alice, 30), db.HiveJobTick.JobId.Filter(carrot.JobId).First())), "scheduler only");
        Tick("job-carrot", 30);
        carrot = Job("job-carrot");
        var tended = db.HiveJobPlant.JobId.Filter(carrot.JobId).ToList();
        Check(tended.Count == 5 && carrot.SeedsHeld == 0 && carrot.Growing == 5, "The droid plants every held seed it has plots for.");
        Check(tended.All(t => t.TileIndex >= 1 && t.TileIndex < 88), "The droid works the farm's free plots and leaves the player's crop alone.");
        Check(Module.MyHiveJobPlants(new ViewContext(db, alice)).Count == 5 && Module.MyHiveJobPlants(new ViewContext(db, bob)).Count == 0,
            "The owner sees which crops the droid tends.");
        Check(db.FarmPlant.Iter().Count(p => p.Owner == alice) == 6, "Droid crops are the player's own farm plants.");
        Reject(() => Tx(() => Module.HarvestPlant(C(alice, 2000), tended[0].PlantId)), "Hive droid");
        Check(db.HiveJobTick.JobId.Filter(carrot.JobId).Count() == 1, "The droid books its next visit.");
        long grow = db.HiveCropRate.PlantIndex.Find(31)!.Value.GrowSeconds;
        for (long t = 40; t < 30 + grow; t += 10) Tick("job-carrot", t);
        Check(db.HiveDelivery.Iter().Count() == 0, "Nothing is delivered before the crop is mature.");
        Tick("job-carrot", 30 + grow + 10);
        carrot = Job("job-carrot");
        Check(carrot.Status == Module.HiveJobCompleted && carrot.Delivered == 5, "The order completes at its target.");
        Check(Count(1, 32) == 5, "Delivered crops land in the player's bag.");
        Check(db.HiveDelivery.JobId.Filter(carrot.JobId).Count() == 5, "One verified delivery per harvested plant.");
        var payouts = db.HiveSettlement.JobId.Filter(carrot.JobId).ToList();
        Check(payouts.Count == 5 && payouts.All(s => s.Kind == Module.HiveSettlementPayout && s.Micros == carrotRate.MicrosPerCrop
            && s.FundingReference == "near-op-1"), "Each delivery queues one payout of its share.");
        Check(carrot.PaidOutMicros == carrot.EscrowMicros && carrot.RefundMicros == 0, "A completed order pays out the whole escrow.");
        Check(db.HiveJobTick.JobId.Filter(carrot.JobId).Count() == 0 && db.HiveJobPlant.JobId.Filter(carrot.JobId).Count() == 0,
            "A finished job stops the droid.");
        Check(db.HiveJobClosure.JobId.Find(carrot.JobId)?.Status == Module.HiveJobCompleted,
            "A completed job is listed as closed, so its grouped payouts can be sent.");
        Check(db.FarmHarvestReceipt.Iter().Count() == 0 && db.XpReceipt.Iter().All(x => !x.Key.Contains("harvest")),
            "A droid harvest awards no farming XP.");

        // ---- settlement confirmation
        var first = payouts[0];
        Reject(() => Tx(() => Module.ConfirmHiveSettlement(C(alice, 100), first.SettlementId, "tx-1")), "settlement authority");
        Tx(() => Module.ConfirmHiveSettlement(C(worker, 100), first.SettlementId, "tx-1"));
        Tx(() => Module.ConfirmHiveSettlement(C(worker, 101), first.SettlementId, "tx-1"));
        Reject(() => Tx(() => Module.ConfirmHiveSettlement(C(worker, 102), first.SettlementId, "tx-2")), "another reference");
        Check(db.HiveSettlement.SettlementId.Find(first.SettlementId)!.Value.Status == Module.HiveSettlementSettled, "Settled once.");

        // ---- desert: cancel mid-growth
        long d0 = 2000;
        Tx(() => Module.CreateHiveJob(C(alice, d0), "job-sage", "desert-village-edge", "HX-07", 76, 4));
        Tx(() => Module.ConfirmHiveJobFunding(C(worker, d0 + 5), "job-sage", "near-op-3", Job("job-sage").EscrowMicros, "AliceWallet"));
        Tick("job-sage", d0 + 10);
        var sage = Job("job-sage");
        var sagePlants = db.HiveJobPlant.JobId.Filter(sage.JobId).ToList();
        Check(sagePlants.Count == 4 && sagePlants.All(p => p.TileIndex >= 1000 && p.TileIndex < 1032), "The desert kiosk's droid works the desert farm.");
        Reject(() => Tx(() => Module.CancelHiveJob(C(bob, d0 + 20), sage.JobId)), "not yours");
        Tx(() => Module.CancelHiveJob(C(alice, d0 + 20), sage.JobId));
        sage = Job("job-sage");
        Check(sage.Status == Module.HiveJobCancelled && sage.RefundMicros == sage.EscrowMicros && db.HiveJobPlant.JobId.Filter(sage.JobId).Count() == 0,
            "Cancelling refunds the undelivered share and hands the crops over.");
        Check(db.FarmPlant.PlantId.Find(sagePlants[0].PlantId) != null, "Handed-over crops keep growing as the player's own.");
        Check(db.HiveSettlement.JobId.Filter(sage.JobId).Single().Kind == Module.HiveSettlementRefund, "One refund for the rest.");
        Check(db.HiveJobTick.JobId.Filter(sage.JobId).Count() == 0, "A cancelled job stops the droid.");
        Check(db.HiveJobClosure.JobId.Find(sage.JobId)?.Status == Module.HiveJobCancelled, "A cancelled job is listed as closed.");

        // ---- unfunded expiry, then a late payment
        long e0 = 5000;
        Tx(() => Module.CreateHiveJob(C(alice, e0), "job-late", "farm", "HX-07", 31, 3));
        Check(Count(1, 31) == 31, "Seeds held.");
        var expiry = db.HiveFundingExpiry.JobId.Filter(Job("job-late").JobId).Single();
        Reject(() => Tx(() => Module.ExpireHiveFunding(C(alice, e0 + 1200), expiry)), "scheduler only");
        db.HiveFundingExpiry.ExpiryId.Delete(expiry.ExpiryId);
        Tx(() => Module.ExpireHiveFunding(C(scheduler, e0 + 1200), expiry));
        Check(Job("job-late").Status == Module.HiveJobExpired && Count(1, 31) == 34, "An unfunded order expires and returns its seeds.");
        Tx(() => Module.ConfirmHiveJobFunding(C(worker, e0 + 1300), "job-late", "near-op-4", Job("job-late").EscrowMicros, "AliceWallet"));
        var late = Job("job-late");
        Check(late.Status == Module.HiveJobExpired && late.RefundMicros == late.EscrowMicros
            && db.HiveSettlement.JobId.Filter(late.JobId).Single().Kind == Module.HiveSettlementRefund,
            "Funding that arrives after the order closed is refunded in full, never lost.");

        // ---- payment failure
        Tx(() => Module.CreateHiveJob(C(bob, e0), "job-bob", "farm", "HX-07", 31, 2));
        Tx(() => Module.RejectHiveJobFunding(C(worker, e0 + 30), "job-bob", "1click-refunded"));
        Check(Job("job-bob").Status == Module.HiveJobFailed && Count(2, 31) == 5, "A failed payment closes the order and returns the seeds.");

        // ---- deadline: partly delivered, the rest refunded (Mugwort, 96 s: the second round can't ripen in time)
        long f0 = 10_000;
        Tx(() => Module.CreateHiveJob(C(alice, f0), "job-slow", "farm", "HX-07", 18, 12));
        Tx(() => Module.ConfirmHiveJobFunding(C(worker, f0 + 1), "job-slow", "near-op-5", Job("job-slow").EscrowMicros + 4_000, "AliceWallet"));
        var slow = Job("job-slow");
        Check(slow.RefundMicros == 4_000 && db.HiveSettlement.JobId.Filter(slow.JobId).Single().Micros == 4_000,
            "An overpayment is refunded straight away.");
        Tick("job-slow", f0 + 2);
        slow = Job("job-slow");
        Check(slow.Growing == 8 && slow.SeedsHeld == 4, "A droid tends at most its plot allowance at once.");
        long mugwort = db.HiveCropRate.PlantIndex.Find(18)!.Value.GrowSeconds;
        for (long t = f0 + 12; t <= f0 + 2 + mugwort + 10; t += 10) Tick("job-slow", t);
        slow = Job("job-slow");
        Check(slow.Delivered == 8 && slow.Growing == 4 && slow.Status == Module.HiveJobActive, "The first round is delivered and the second planted.");
        Tick("job-slow", (long)(slow.DeadlineAt.MicrosecondsSinceUnixEpoch / 1_000_000) + 1);
        slow = Job("job-slow");
        Check(slow.Status == Module.HiveJobExpired && slow.Delivered == 8, "Past the deadline the job ends with what it delivered.");
        Check(slow.PaidOutMicros == slow.MicrosPerCrop * 8 && slow.RefundMicros == 4_000 + slow.MicrosPerCrop * 4,
            "Only delivered crops are paid out; the rest is refunded.");
        Check(db.HiveSettlement.JobId.Filter(slow.JobId).Sum(s => (long)s.Micros) == (long)(slow.EscrowMicros + 4_000),
            "Payouts and refunds add up to exactly what was paid.");
        Check(db.FarmPlant.Iter().Count(p => p.Owner == alice && p.PlantIndex == 18) == 4, "The unripe crops stay on the farm as the player's.");

        // ---- the views
        var mine = Module.MyHiveJobs(new ViewContext(db, alice));
        Check(mine.Count == 4 && mine.All(j => j.Owner == alice), "MyHiveJobs shows only the caller's jobs.");
        Check(Module.MyHiveDeliveries(new ViewContext(db, bob)).Count == 0, "MyHiveDeliveries is private to the owner.");
        Check(Module.MyHiveJobPlants(new ViewContext(db, alice)).Count == 0, "No droid crops left once every job ended.");

        Console.WriteLine($"Passed {checks} Hive job checks: pricing, sites, held seeds, escrow funding, scheduler-only droid, verified deliveries, payouts, refunds and settlement.");
    }
}
