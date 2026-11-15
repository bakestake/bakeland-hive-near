using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gamegaard.FarmSystem;
using UnityEngine;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// A Hive worker droid standing by a Hive Link terminal (HX-07 TILLER, sheet DroidTiller in the B44 droid rig).
    /// The kiosk's worker and the local, visual-only farm preview.
    /// </summary>
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
        }
        private void OnDisable()
        {
            HiveLinkPanel.Contracted -= OnContracted;
            HiveJobNetwork.Changed -= OnJobsChanged;
            serverGig = false;
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
            SetGig(active);
        }

        private void OnContracted(string site, string droid)
        {
            if (site != siteId || droid != droidId || onGig) return;
            onGig = true;
            if (badge != null) badge.OnGig = true;
            farmRoutine = StartCoroutine(PreviewFarm());
        }

        /// <summary>Puts the droid on (or off) a gig: amber badge and the work loop.</summary>
        public void SetGig(bool gig)
        {
            StopFarm();
            onGig = gig;
            if (badge != null) badge.OnGig = gig;
            mode = gig ? Mode.Work : Mode.Return;
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
                    if (farmRoutine == null && t > modeUntil)
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
