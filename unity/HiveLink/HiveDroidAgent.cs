using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gamegaard.FarmSystem;
using SpacetimeDB.Types;
using UnityEngine;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// A Hive worker droid standing by a Hive Link terminal (HX-07 TILLER, sheet DroidTiller in the B44 droid rig).
    /// The kiosk's worker and the local, visual-only farm preview.
    ///
    /// Paid jobs (15 Nov 2026): the server's scheduler does the real farming (HiveJobs.cs RunHiveJob), and this droid
    /// acts it out on the farm like a player would. Each crop the server plants (a new MyHiveJobPlants row) queues a
    /// visit: the droid walks to that plot, tills and plants it, and only then does the crop show (it is hidden until
    /// the droid arrives). Each crop the server harvests leaves a ripe stand-in on its plot until the droid walks over
    /// and harvests it. Between those it makes watering rounds over the plots it tends, and it walks back to the
    /// kiosk once the job is over and its queue is done.
    /// </summary>
    [DefaultExecutionOrder(10000)]   // its LateUpdate hides not-yet-planted crops after the farm's own renderers update
    public sealed class HiveDroidAgent : MonoBehaviour
    {
        // B44 slots
        private const int IdleDown = 0, WalkDown = 4, IdleRight = 8, WalkUp = 12, IdleLeft = 16, WalkRight = 20, IdleUp = 24, WalkLeft = 28;
        private const float Fps = 8f, WalkSpeed = 1.2f, NoticeRadius = 2.6f;

        [Header("Set by HiveLinkAuthoring")]
        public Sprite[] sheet;     // 44
        public Sprite[] actions;   // 16: till 0-3, water 4-7, harvest 8-11, gig loop 12-15
        public SpriteRenderer body;
        public HiveAgentBadge badge;

        public string siteId = "";
        public string droidId = "HX-07";

        private enum Mode { Idle, Glance, Patrol, Return, Work, FarmRoute }
        private Mode mode = Mode.Idle;
        private Vector3 home;
        private Vector2 patrolTarget;
        private int facing = IdleDown;
        private float modeUntil, workStarted;
        private int workAction = 3;
        private bool onGig;
        private Transform player;
        private float lookForPlayer;
        private Coroutine farmRoutine;
        private SpriteRenderer previewCrop;
        private bool arrived;

        public bool OnGig => onGig;
        public string PreviewStage { get; private set; }

        public static bool CanPreview(string site, string droid) =>
            FindObjectsOfType<HiveDroidAgent>().Any(a => a.siteId == site && a.droidId == droid && !a.OnGig);

        public static string PreviewCropName(string site) => site == "desert-village-edge" ? "DUNE SAGE" : "CARROT";

        private void OnEnable()
        {
            HiveLinkPanel.Contracted += OnContracted;
            HiveJobNetwork.Changed += OnJobsChanged;
            HiveJobNetwork.PlantStarted += OnPlantStarted;
            HiveJobNetwork.PlantEnded += OnPlantEnded;
        }
        private void OnDisable()
        {
            HiveLinkPanel.Contracted -= OnContracted;
            HiveJobNetwork.Changed -= OnJobsChanged;
            HiveJobNetwork.PlantStarted -= OnPlantStarted;
            HiveJobNetwork.PlantEnded -= OnPlantEnded;
            serverGig = false;
            StopLive();
            StopFarm();
            onGig = false;
            if (badge != null) badge.OnGig = false;
            mode = Mode.Idle;
        }

        private void Start()
        {
            home = transform.position;
            modeUntil = Time.time + Random.Range(2f, 4f);
            if (badge != null) badge.OnGig = false;
            OnJobsChanged();
        }

        // The player's paid job at this kiosk's farm (server HiveJobs.cs): while it is active the droid is on a gig (amber
        // badge, work loop); its crops grow on the player's own plots, drawn by the farm like any other crop.
        private bool serverGig;
        private void OnJobsChanged()
        {
            bool active = HiveJobNetwork.IsReady && HiveJobNetwork.HasActiveJob(siteId);
            if (active == serverGig) return;
            serverGig = active;
            // Starting: off to the farm. Ending: the live loop finishes its queue (the last harvests), then walks home.
            if (active) SetGig(true);
        }

        // ------------------------------------------------------------------ paid jobs, acted out on the farm

        private enum TaskKind { Plant, Harvest }
        private struct FarmTask { public TaskKind kind; public uint tile; }
        private readonly Queue<FarmTask> tasks = new Queue<FarmTask>();
        // Crops the server planted that the droid hasn't planted on screen yet: hidden until it does (or a time limit).
        private readonly Dictionary<uint, float> hiddenUntil = new Dictionary<uint, float>();
        private readonly Dictionary<uint, HashSet<Renderer>> hiddenRenderers = new Dictionary<uint, HashSet<Renderer>>();
        // Crops the server harvested that the droid hasn't harvested on screen yet: a ripe stand-in sprite.
        private readonly Dictionary<uint, SpriteRenderer> ghosts = new Dictionary<uint, SpriteRenderer>();
        private readonly Dictionary<uint, Soil> soils = new Dictionary<uint, Soil>();
        private Coroutine liveRoutine;
        private int waterCursor;
        private const float HideLimit = 60f;

        private bool IsMySite(HiveJobPlant row) => row != null && HiveJobNetwork.JobById(row.JobId)?.Site == siteId;

        private void OnPlantStarted(HiveJobPlant row)
        {
            if (!IsMySite(row)) return;
            hiddenUntil[row.TileIndex] = Time.time + HideLimit;
            tasks.Enqueue(new FarmTask { kind = TaskKind.Plant, tile = row.TileIndex });
            EnsureLive();
        }

        private void OnPlantEnded(HiveJobPlant row)
        {
            if (!IsMySite(row) || !isActiveAndEnabled) return;
            StartCoroutine(AfterPlantEnded(row));
        }

        private IEnumerator AfterPlantEnded(HiveJobPlant row)
        {
            // The farm removes its plant in the same server update; give it a couple of frames.
            yield return null; yield return null;
            Reveal(row.TileIndex);
            Soil soil = SoilAt(row.TileIndex);
            if (soil == null || soil.HasPlant) yield break;   // handed to the player when the job ended: nothing to harvest
            var job = HiveJobNetwork.JobById(row.JobId);
            Sprite ripe = job != null ? RipeSprite(job.SeedItemId) : null;
            if (ripe != null && !ghosts.ContainsKey(row.TileIndex))
            {
                var visual = new GameObject("Hive ripe crop");
                visual.transform.SetParent(soil.transform, false);
                var sr = visual.AddComponent<SpriteRenderer>();
                if (body != null) { sr.sharedMaterial = body.sharedMaterial; sr.sortingLayerID = body.sortingLayerID; }
                sr.sprite = ripe;
                ghosts[row.TileIndex] = sr;
            }
            tasks.Enqueue(new FarmTask { kind = TaskKind.Harvest, tile = row.TileIndex });
            EnsureLive();
        }

        private static Sprite RipeSprite(int seedItemId)
        {
            var phases = ItemDatabaseManager.Instance != null ? ItemDatabaseManager.Instance.Find(seedItemId)?.PlantData?.Phases : null;
            if (phases == null || phases.Length == 0) return null;
            PlantPhase phase = phases.LastOrDefault(p => p != null && p.CanHarvest && p.States != null && p.States.Length > 0)
                ?? phases[phases.Length - 1];
            if (phase?.States == null || phase.States.Length == 0) return null;
            return phase.States[0].sprite != null ? phase.States[0].sprite : phase.States[0].frames?.FirstOrDefault();
        }

        private Soil SoilAt(uint tile)
        {
            if (soils.TryGetValue(tile, out Soil s) && s != null) return s;
            soils.Clear();
            foreach (Soil candidate in FindObjectsOfType<Soil>())
                if (candidate.gameObject.scene == gameObject.scene && candidate.TryGetFarmTileIndex(out uint index)) soils[index] = candidate;
            return soils.TryGetValue(tile, out s) ? s : null;
        }

        private void EnsureLive()
        {
            if (liveRoutine != null || !isActiveAndEnabled) return;
            StopFarm();   // a paid job replaces the visual-only preview
            onGig = true;
            if (badge != null) badge.OnGig = true;
            liveRoutine = StartCoroutine(LiveWork());
        }

        private void StopLive()
        {
            if (liveRoutine != null) StopCoroutine(liveRoutine);
            liveRoutine = null;
            tasks.Clear();
            foreach (uint tile in hiddenUntil.Keys.ToList()) Reveal(tile);
            foreach (var g in ghosts.Values) if (g != null) Destroy(g.gameObject);
            ghosts.Clear();
        }

        private IEnumerator LiveWork()
        {
            PreviewStage = "HEADING TO THE FARM";
            while (true)
            {
                if (tasks.Count > 0)
                {
                    FarmTask task = tasks.Dequeue();
                    if (task.kind == TaskKind.Plant)
                    {
                        yield return Visit(task.tile, "PLANTING", 0, 1.4f);
                        Reveal(task.tile);
                        yield return FarmAction("WATERING", 1, 1f);
                    }
                    else
                    {
                        yield return Visit(task.tile, "HARVESTING", 2, 1.4f);
                        if (ghosts.TryGetValue(task.tile, out SpriteRenderer ghost) && ghost != null) Destroy(ghost.gameObject);
                        ghosts.Remove(task.tile);
                    }
                    continue;
                }
                if (!serverGig) break;
                var tended = HiveJobNetwork.TendedAt(siteId).Where(p => !hiddenUntil.ContainsKey(p.TileIndex)).ToList();
                if (tended.Count > 0)
                {
                    // A watering round, one plot at a time, like a player with a can.
                    var plot = tended[waterCursor++ % tended.Count];
                    yield return Visit(plot.TileIndex, "WATERING", 1, 1.6f);
                    yield return FarmAction("TENDING", 3, Random.Range(.8f, 1.6f));
                }
                else yield return FarmAction("WAITING FOR THE NEXT ROUND", 3, 1.5f);
            }
            PreviewStage = "RETURNING TO THE KIOSK";
            yield return WalkRoute(home);
            if (!arrived) transform.position = home;   // no route back: don't leave it stranded on the farm
            mode = Mode.Idle; facing = IdleDown; modeUntil = Time.time + 3f;
            onGig = false;
            if (badge != null) badge.OnGig = false;
            PreviewStage = null;
            liveRoutine = null;
        }

        /// <summary>Walks to just below a plot (where a player stands to work it) and plays one farming action.</summary>
        private IEnumerator Visit(uint tile, string stage, int action, float seconds)
        {
            Soil soil = SoilAt(tile);
            if (soil == null) yield break;
            PreviewStage = stage;
            yield return WalkRoute((Vector2)soil.transform.position + Vector2.down * .55f);
            if (!arrived)
            {
                // No route (a fence, the player in the way): step straight there rather than act from afar.
                Vector3 p = (Vector2)soil.transform.position + Vector2.down * .55f;
                transform.position = new Vector3(p.x, p.y, home.z);
            }
            yield return FarmAction(stage, action, seconds);
        }

        private void Reveal(uint tile)
        {
            hiddenUntil.Remove(tile);
            if (hiddenRenderers.TryGetValue(tile, out var set)) foreach (var r in set) if (r != null) r.enabled = true;
            hiddenRenderers.Remove(tile);
        }

        private void LateUpdate()
        {
            if (hiddenUntil.Count == 0) return;
            foreach (uint tile in hiddenUntil.Keys.ToList())
            {
                if (Time.time > hiddenUntil[tile]) { Reveal(tile); continue; }
                Soil soil = SoilAt(tile);
                if (soil == null || soil.Plant == null) continue;
                if (!hiddenRenderers.TryGetValue(tile, out var set)) hiddenRenderers[tile] = set = new HashSet<Renderer>();
                foreach (Renderer r in soil.Plant.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) { set.Add(r); r.enabled = false; }
            }
        }

        private void OnContracted(string site, string droid)
        {
            // A paid job drives the droid from the server's own planting and harvests (OnJobsChanged, OnPlantStarted);
            // only the PREVIEW build plays the visual-only demo round.
            if (HiveLinkPanel.Live) return;
            if (site != siteId || droid != droidId || onGig) return;
            onGig = true;
            if (badge != null) badge.OnGig = true;
            farmRoutine = StartCoroutine(PreviewFarm());
        }

        /// <summary>Puts the droid on a gig (off to the farm to work the job's plots) or, off, sends it straight home.</summary>
        public void SetGig(bool gig)
        {
            if (gig) { EnsureLive(); return; }
            StopLive();
            StopFarm();
            onGig = false;
            if (badge != null) badge.OnGig = false;
            mode = Mode.Return;
            workStarted = Time.time; workAction = 3; modeUntil = Time.time + 3f;
        }

        private void StopFarm()
        {
            if (farmRoutine != null) StopCoroutine(farmRoutine);
            farmRoutine = null;
            if (previewCrop != null) Destroy(previewCrop.gameObject);
            previewCrop = null;
            PreviewStage = null;
        }

        private IEnumerator PreviewFarm()
        {
            yield return null; // Let the kiosk spawn and the panel dispatch finish.
            bool desert = siteId == "desert-village-edge";
            int seedId = desert ? 76 : 31;
            var database = ItemDatabaseManager.Instance;
            var phases = database != null ? database.Find(seedId)?.PlantData?.Phases : null;
            var plots = FindObjectsOfType<Soil>()
                .Where(s => s.gameObject.scene == gameObject.scene && s.FarmTileIndex >= 0 &&
                            (s.FarmTileIndex >= 1000 && s.FarmTileIndex < 2000) == desert && !s.HasPlant)
                .OrderBy(s => ((Vector2)(s.transform.position - transform.position)).sqrMagnitude)
                .Take(12).ToArray();
            Soil plot = null;
            PreviewStage = "HEADING TO THE FARM";
            foreach (var candidate in plots)
            {
                if (candidate == null || candidate.HasPlant) continue;
                yield return WalkRoute((Vector2)candidate.transform.position + Vector2.down * .55f);
                if (!arrived || candidate == null || candidate.HasPlant) continue;
                plot = candidate;
                break;
            }

            if (plot != null && phases != null && phases.Length > 0)
            {
                yield return FarmAction("PREPARING AND PLANTING", 0, 1.5f);
                if (plot == null || plot.HasPlant)
                {
                    yield return FinishPreview();
                    yield break;
                }
                var visual = new GameObject("Hive preview crop");
                visual.transform.SetParent(plot.transform, false);
                previewCrop = visual.AddComponent<SpriteRenderer>();
                if (body != null) previewCrop.sharedMaterial = body.sharedMaterial;
                ShowCrop(phases[0]);
                yield return FarmAction("WATERING", 1, 1.5f);
                for (int i = 1; i < phases.Length; i++)
                {
                    if (plot == null || plot.HasPlant) break;
                    ShowCrop(phases[i]);
                    yield return FarmAction("GROWING", 3, 1.5f);
                    if (!phases[i].CanHarvest) continue;
                    if (plot != null && !plot.HasPlant) yield return FarmAction("HARVESTING", 2, 1.5f);
                    break;
                }
            }
            else Debug.LogWarning("[HiveLink] No reachable empty preview bed or crop definition at " + siteId);

            yield return FinishPreview();
        }

        private IEnumerator FinishPreview()
        {
            if (previewCrop != null) Destroy(previewCrop.gameObject);
            previewCrop = null;
            PreviewStage = "RETURNING TO THE KIOSK";
            yield return WalkRoute(home);
            if (!arrived) Debug.LogWarning("[HiveLink] Tiller could not route back to " + siteId);
            mode = Mode.Idle;
            modeUntil = Time.time + 3f;
            if (badge != null) badge.OnGig = false;
            onGig = false;
            PreviewStage = null;
            farmRoutine = null;
        }

        private void ShowCrop(PlantPhase phase)
        {
            if (previewCrop == null || phase.States == null || phase.States.Length == 0) return;
            var state = phase.States[0];
            previewCrop.sprite = state.sprite != null ? state.sprite : state.frames?.FirstOrDefault();
        }

        private IEnumerator FarmAction(string stage, int action, float seconds)
        {
            PreviewStage = stage;
            mode = Mode.Work;
            workAction = action;
            workStarted = Time.time;
            modeUntil = Time.time + seconds;
            yield return new WaitForSeconds(seconds);
        }

        private IEnumerator WalkRoute(Vector2 target)
        {
            mode = Mode.FarmRoute;
            arrived = Vector2.Distance(transform.position, target) <= .1f;
            if (arrived) yield break;
            var playerController = PlayerController.Instance;
            var router = playerController != null ? playerController.GetComponent<PlayerTapToMove>() : null;
            bool planned = false;
            List<Vector2> route = null;
            if (router == null || !router.TryPlanRoute(transform.position, target, result =>
                { route = result; planned = true; }, transform)) yield break;
            float deadline = Time.unscaledTime + 5f;
            while (!planned && Time.unscaledTime < deadline) yield return null;
            if (route == null || route.Count == 0) yield break;
            foreach (var point in route)
            {
                while (Vector2.Distance(transform.position, point) > .025f)
                {
                    Vector2 current = transform.position;
                    Vector2 delta = point - current;
                    Vector2 next = Vector2.MoveTowards(current, point, 2.4f * Time.deltaTime);
                    transform.position = new Vector3(next.x, next.y, home.z);
                    int walk = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? (delta.x > 0 ? WalkRight : WalkLeft) : (delta.y > 0 ? WalkUp : WalkDown);
                    if (body != null && sheet != null && sheet.Length >= 44) Show(sheet[walk + Mathf.FloorToInt(Time.time * Fps) % 4]);
                    yield return null;
                }
            }
            arrived = Vector2.Distance(transform.position, target) <= .65f;
        }

        private void Update()
        {
            if (sheet == null || sheet.Length < 44 || body == null) return;
            float t = Time.time;
            int tick = Mathf.FloorToInt(t * Fps);

            if (Time.time > lookForPlayer)
            {
                lookForPlayer = Time.time + 1f;
                if (player == null) { var p = GameObject.FindGameObjectWithTag("Player"); player = p != null ? p.transform : null; }
            }
            bool near = player != null && ((Vector2)(player.position - transform.position)).sqrMagnitude < NoticeRadius * NoticeRadius;

            switch (mode)
            {
                case Mode.FarmRoute:
                    return;
                case Mode.Work:
                    // gig loop most of the time, a farming move now and then (a preview of what it will do on the plot)
                    if (farmRoutine == null && liveRoutine == null && t > modeUntil)
                    {
                        workAction = workAction == 3 ? Random.Range(0, 3) : 3;
                        modeUntil = t + (workAction == 3 ? Random.Range(2.5f, 4.5f) : 4f / Fps * 2f);
                        workStarted = t;
                    }
                    int f = Mathf.FloorToInt((t - workStarted) * Fps) % 4;
                    Show(actions != null && actions.Length >= 16 ? actions[workAction * 4 + f] : sheet[IdleDown + f]);
                    return;

                case Mode.Patrol:
                case Mode.Return:
                {
                    Vector2 target = mode == Mode.Patrol ? patrolTarget : (Vector2)home;
                    Vector2 pos = transform.position;
                    Vector2 d = target - pos;
                    if (d.magnitude < .04f)
                    {
                        transform.position = new Vector3(target.x, target.y, home.z);
                        if (mode == Mode.Patrol) { mode = Mode.Return; modeUntil = t + .6f; }
                        else { mode = onGig ? Mode.Work : Mode.Idle; facing = IdleDown; modeUntil = t + Random.Range(3f, 6f); }
                        break;
                    }
                    if (mode == Mode.Return && t < modeUntil && d.magnitude > .04f && mode != Mode.Patrol) { Show(sheet[facing + tick % 4]); return; }
                    Vector2 step = Vector2.MoveTowards(pos, target, WalkSpeed * Time.deltaTime);
                    transform.position = new Vector3(step.x, step.y, home.z);
                    int walk = Mathf.Abs(d.x) >= Mathf.Abs(d.y) ? (d.x > 0 ? WalkRight : WalkLeft) : (d.y > 0 ? WalkUp : WalkDown);
                    facing = walk == WalkRight ? IdleRight : walk == WalkLeft ? IdleLeft : walk == WalkUp ? IdleUp : IdleDown;
                    Show(sheet[walk + tick % 4]);
                    return;
                }

                case Mode.Glance:
                    if (t > modeUntil) { mode = Mode.Idle; facing = IdleDown; modeUntil = t + Random.Range(3f, 6f); }
                    break;

                default:
                    if (near)
                    {
                        Vector2 d = player.position - transform.position;
                        facing = Mathf.Abs(d.x) > Mathf.Abs(d.y) * 1.2f ? (d.x > 0 ? IdleRight : IdleLeft) : (d.y > 0 ? IdleUp : IdleDown);
                        modeUntil = Mathf.Max(modeUntil, t + 2f);
                    }
                    else if (t > modeUntil)
                    {
                        float roll = Random.value;
                        if (roll < .45f) { mode = Mode.Glance; facing = Random.value < .5f ? IdleLeft : IdleRight; modeUntil = t + Random.Range(1.2f, 2.2f); }
                        else if (roll < .8f)
                        {
                            mode = Mode.Patrol;
                            patrolTarget = (Vector2)home + new Vector2(Random.value < .5f ? -1f : 1f, -.5f);
                        }
                        else { facing = IdleDown; modeUntil = t + Random.Range(3f, 6f); }
                    }
                    break;
            }
            Show(sheet[facing + tick % 4]);
        }

        private void Show(Sprite s) { if (body.sprite != s) body.sprite = s; }
    }
}
