#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bakeland.HiveLink;
using Bakeland.UI.Merged;
using Cinemachine;
using Gamegaard.FarmSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Bakeland.Cutscenes
{
    /// <summary>
    /// Capture rig for the NEAR integration demo (Editor only, recorded by NearDemoRecorder at 1560x720, the MX-77
    /// screen of the house film frame, so the footage drops into the frame 1:1).
    ///
    /// 1. The farm Hive Link kiosk: the player walks up and opens HIVE LINK; a pixel cursor browses the ROSTER, hires
    ///    TILLER, orders 9 carrots on CONTRACT, pays (the TRANSMITTING checklist), HIRED, then POWER.
    /// 2. Three Hive droids (the kiosk's TILLER and two more) walk to the player's plots, till, plant and water; the
    ///    crops grow (time-lapse) and the droids harvest them, each harvest popping into the bag.
    /// 3. Back at the kiosk: JOBS shows the order delivered.
    ///
    /// Everything is local and visual: the panel runs its PREVIEW (nothing is signed or sent), the crops are local
    /// sprites over the soils, preview JOBS rows stand in for the server's, and all of it is restored afterwards.
    /// Beat marks (s since Roll) go to the recorder log for the edit.
    /// </summary>
    public sealed class NearDemoCutscene : MonoBehaviour
    {
        public bool IsReady { get; private set; }
        public bool IsFinished { get; private set; }
        public string Report => report.ToString();
        public string Marks { get; private set; } = "";
        private readonly System.Text.StringBuilder report = new System.Text.StringBuilder();
        private bool rolling;
        private float rollAt;
        public void Roll() { rolling = true; rollAt = Time.time; }
        private void Mark(string beat) => Marks += $"{beat}={Time.time - rollAt:0.00} ";

        public static NearDemoCutscene Active { get; private set; }

        private const int FxOrder = 30000;
        private const float ArtFps = 8f;
        private const int CropPlant = 31, CropHarvest = 32;  // carrot
        private const int Plots = 9;
        // ortho sizes that keep whole screen pixels per art pixel on the 720-tall capture (16 PPU): 3x, 4x, 5x
        private const float Zoom3 = 7.5f, Zoom4 = 5.625f, Zoom5 = 4.5f;

        // B44 slots of the droid rig
        private const int IdleDown = 0, WalkDown = 4, IdleRight = 8, WalkUp = 12, IdleLeft = 16, WalkRight = 20, IdleUp = 24, WalkLeft = 28;

        private HiveLinkTerminal kiosk;
        private HiveDroidAgent siteDroid;
        private Vector2 kioskFoot, playerStart, playerFront;
        private Camera cam;
        private CinemachineBrain brain;
        private bool brainWasEnabled;
        private float camSizeBefore;
        private Vector3 camPosBefore;
        private Vector2 camPos, camGoal;
        private float camSize, camSizeGoal, camEase = 1.2f;
        private Canvas overlay;
        private RectTransform cursor;
        private Image cursorImage;
        private Sprite cursorUp, cursorDown;
        private Transform stage;

        private readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        private readonly List<GameObject> hiddenObjects = new List<GameObject>();
        private readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private readonly List<Behaviour> pausedBehaviours = new List<Behaviour>();
        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<Bot> bots = new List<Bot>();
        private readonly List<Crop> crops = new List<Crop>();
        private bool hidHud, restored;
        private float oldMusic, oldAmbience;
        private string oldSiteId;
        private PlantPhase[] phases;
        private Sprite harvestIcon;
        private uint delivered;
        private float oldRenderScale = -1f;
        private bool oldHiveReady, oldClickToMove;
        private System.Reflection.PropertyInfo hiveReady;

        /// <summary>The capture renders at full resolution (the game's URP asset runs at 0.8).</summary>
        private void FullRes(bool on)
        {
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                if (on) { oldRenderScale = urp.renderScale; urp.renderScale = 1f; }
                else if (oldRenderScale > 0f) urp.renderScale = oldRenderScale;
            }
        }

        /// <summary>The demo runs the panel's PREVIEW path (no seeds needed, preview JOBS rows), so the Hive reads as not
        /// connected for its length; restored afterwards.</summary>
        private void HiveOffline(bool on)
        {
            hiveReady ??= typeof(HiveJobNetwork).GetProperty("IsReady", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (hiveReady == null || !hiveReady.CanWrite && hiveReady.GetSetMethod(true) == null) return;
            var set = hiveReady.GetSetMethod(true);
            if (on) { oldHiveReady = HiveJobNetwork.IsReady; set.Invoke(null, new object[] { false }); }
            else if (oldHiveReady) set.Invoke(null, new object[] { true });
        }

        private sealed class Bot
        {
            public Transform root;
            public SpriteRenderer body;
            public Sprite[] sheet, actions;
            public HiveAgentBadge badge;
            public int facing = IdleDown;
            public int action = -1;          // >= 0 plays an action row (0 till, 1 water, 2 harvest, 3 gig)
            public float actionAt;
            public bool walking;
            public int walkSlot;
            public List<Crop> plots = new List<Crop>();
        }

        private sealed class Crop
        {
            public Soil soil;
            public Vector2 at;
            public SpriteRenderer r;
            public int phase = -1;
        }

        public static NearDemoCutscene Begin(out string error)
        {
            error = null;
            if (Active != null) { error = "already running"; return null; }
            var host = new GameObject("NEAR Demo");
            var c = host.AddComponent<NearDemoCutscene>();
            Active = c;
            try { c.Setup(); }
            catch (Exception e) { Debug.LogException(e); error = e.Message; Destroy(host); return null; }
            if (!c.IsReady) { error = c.Report; Destroy(host); return null; }
            c.StartCoroutine(c.Play());
            return c;
        }

        // ================================================================== setup

        private void Setup()
        {
            stage = new GameObject("Stage").transform;
            stage.SetParent(transform, false);
            kiosk = FindObjectsOfType<HiveLinkTerminal>(true).FirstOrDefault(k => k.SiteId == HiveJobNetwork.SiteFarm);
            siteDroid = FindObjectsOfType<HiveDroidAgent>(true).FirstOrDefault(d => d.siteId == HiveJobNetwork.SiteFarm);
            var player = PlayerController.Instance;
            if (kiosk == null || siteDroid == null || player == null) { report.Append($"missing kiosk={kiosk != null} droid={siteDroid != null} player={player != null}"); return; }
            kioskFoot = kiosk.transform.position;
            playerFront = kioskFoot + new Vector2(-.15f, -1.05f);
            playerStart = kioskFoot + new Vector2(-1.35f, -1.15f);

            var db = ItemDatabaseManager.Instance;
            phases = db != null ? db.Find(CropPlant)?.PlantData?.Phases : null;
            harvestIcon = db != null ? db.Find(CropHarvest)?.Icon : null;
            if (phases == null || phases.Length == 0) { report.Append("no carrot phases"); return; }

            HideChrome();
            oldClickToMove = SettingsManager.ClickToMove;
            SettingsManager.SetClickToMove(true);
            FullRes(true);
            HiveOffline(true);
            var weather = Bakeland.Weather.WeatherSystem.Instance;
            if (weather != null) { weather.PreviewWeather(Bakeland.Weather.WeatherKind.Clear); weather.SetWeather(Bakeland.Weather.WeatherKind.Clear, true); }
            oldMusic = SettingsManager.MusicVolume; oldAmbience = SettingsManager.AmbienceVolume;
            SoundManager.Instance?.SetVolume(0f, AudioType.MUSIC);
            StarterCrate.Suppress = true;
            HiveLinkPanel.CloseIfOpen();
            HiveJobNetwork.SetPreviewJobs(Array.Empty<SpacetimeDB.Types.HiveJob>());

            // the player waits south-west of the kiosk
            if (player.TryGetComponent(out Rigidbody2D rb)) { rb.velocity = Vector2.zero; rb.position = playerStart; }
            player.transform.position = new Vector3(playerStart.x, playerStart.y, player.transform.position.z);
            Physics2D.SyncTransforms();
            PlayerController.canMove = false;
            PlayerController.canInteract = false;

            BuildCrew();
            ChoosePlots();
            BuildOverlay();
            TakeCamera();
            camPos = camGoal = kioskFoot + new Vector2(-2.5f, -1.8f);
            camSize = camSizeGoal = Zoom3;
            ApplyCamera();
            report.Append($"NEAR_DEMO_READY kiosk={kioskFoot} droid={siteDroid.transform.position} plots={crops.Count} " +
                          $"hive={HiveJobNetwork.IsReady} live={HiveLinkPanel.Live} phases={phases.Length} ");
            IsReady = crops.Count > 0;
            if (!IsReady) report.Append("no empty plots found");
        }

        private void HideChrome()
        {
            if (!GameUIManager.IsCinematic) { GameUIManager.SetCinematicMode(true); hidHud = true; }
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || !canvas.enabled || canvas.renderMode == RenderMode.WorldSpace) continue;
                if (canvas.GetComponentInParent<HiveLinkPanel>() != null) continue;
                canvas.enabled = false;
                hiddenCanvases.Add(canvas);
            }
            foreach (var pair in SpacetimeManager.Players)
            {
                SpacetimePlayerCharacter other = pair.Value;
                if (other == null || other.IsLocalPlayer || !other.gameObject.activeSelf) continue;
                other.gameObject.SetActive(false);
                hiddenObjects.Add(other.gameObject);
            }
            if (WorldPointerInteraction.Instance != null) Pause(WorldPointerInteraction.Instance);
            foreach (SoilHighlightManager h in FindObjectsByType<SoilHighlightManager>(FindObjectsSortMode.None)) Pause(h);
            foreach (PlantStatusIndicator indicator in FindObjectsByType<PlantStatusIndicator>(FindObjectsSortMode.None))
            {
                indicator.gameObject.SetActive(false);
                hiddenObjects.Add(indicator.gameObject);
            }
        }

        private void Pause(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            b.enabled = false;
            pausedBehaviours.Add(b);
        }

        /// <summary>The kiosk's TILLER plus two more of the same make, all driven here (their own agent logic is paused).</summary>
        private void BuildCrew()
        {
            oldSiteId = siteDroid.siteId;
            bots.Add(MakeBot(siteDroid, siteDroid.transform));
            for (int i = 0; i < 2; i++)
            {
                var copy = Instantiate(siteDroid.gameObject, stage);
                copy.name = "Hive Droid (demo " + (i + 1) + ")";
                copy.transform.position = siteDroid.transform.position + new Vector3(1.1f * (i + 1), -.25f * (i + 1), 0f);
                spawned.Add(copy);
                bots.Add(MakeBot(copy.GetComponent<HiveDroidAgent>(), copy.transform));
            }
        }

        private Bot MakeBot(HiveDroidAgent agent, Transform root)
        {
            var bot = new Bot { root = root, body = agent.body, sheet = agent.sheet, actions = agent.actions, badge = agent.badge };
            Pause(agent);
            foreach (var body in root.GetComponentsInChildren<Rigidbody2D>()) body.simulated = false;
            if (bot.badge != null) bot.badge.OnGig = false;
            return bot;
        }

        /// <summary>Nine empty soils of the player's farm, nearest the kiosk, three per droid; any real crop on them is hidden.</summary>
        private void ChoosePlots()
        {
            var soils = FindObjectsOfType<Soil>()
                .Where(s => s.gameObject.scene == kiosk.gameObject.scene && s.FarmTileIndex >= 0 && s.FarmTileIndex < 1000)
                .ToList();
            report.Append($"soils={soils.Count} ");
            if (soils.Count == 0) return;
            // the plot block: soils within reach, preferring a compact block (nearest to the kiosk first)
            var pick = soils.OrderBy(s => ((Vector2)s.transform.position - kioskFoot).sqrMagnitude).Take(Plots).ToList();
            float minX = pick.Min(s => s.transform.position.x), maxX = pick.Max(s => s.transform.position.x);
            float minY = pick.Min(s => s.transform.position.y), maxY = pick.Max(s => s.transform.position.y);
            report.Append($"block=({minX:0.0},{minY:0.0})..({maxX:0.0},{maxY:0.0}) ");
            foreach (var s in pick.OrderBy(s => s.transform.position.x).ThenByDescending(s => s.transform.position.y))
            {
                if (s.Plant != null && s.Plant.gameObject.activeSelf) { s.Plant.gameObject.SetActive(false); hiddenObjects.Add(s.Plant.gameObject); }
                foreach (Renderer r in s.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.transform == s.transform || !r.enabled) continue;
                    r.enabled = false; hiddenRenderers.Add(r);
                }
                var go = new GameObject("Demo crop");
                go.transform.SetParent(stage, false);
                go.transform.position = s.transform.position;
                var sr = go.AddComponent<SpriteRenderer>();
                sr.enabled = false;
                crops.Add(new Crop { soil = s, at = s.transform.position, r = sr });
            }
            // three columns, one per droid (west to east)
            for (int i = 0; i < crops.Count; i++) bots[Mathf.Min(i * bots.Count / crops.Count, bots.Count - 1)].plots.Add(crops[i]);
        }

        // ================================================================== overlay + cursor

        private void BuildOverlay()
        {
            var root = new GameObject("NEAR Demo Overlay", typeof(RectTransform), typeof(Canvas));
            root.transform.SetParent(transform, false);
            overlay = root.GetComponent<Canvas>();
            overlay.renderMode = RenderMode.ScreenSpaceOverlay;
            overlay.overrideSorting = true;
            overlay.sortingOrder = 32010;
            cursorUp = CursorSprite(false); cursorDown = CursorSprite(true);
            var c = new GameObject("Cursor", typeof(RectTransform), typeof(Image));
            c.transform.SetParent(root.transform, false);
            cursor = (RectTransform)c.transform;
            cursor.anchorMin = cursor.anchorMax = Vector2.zero;
            cursor.pivot = new Vector2(0f, 1f);
            cursorImage = c.GetComponent<Image>();
            cursorImage.sprite = cursorUp; cursorImage.raycastTarget = false;
            cursor.sizeDelta = new Vector2(10 * 2, 13 * 2);   // the panel draws at 2x on the 720p capture
            c.SetActive(false);
        }

        /// <summary>A 10x13 house-style arrow: console ink outline, light fill (darker while pressed).</summary>
        private static Sprite CursorSprite(bool down)
        {
            string[] rows =
            {
                "#.........",
                "##........",
                "#o#.......",
                "#oo#......",
                "#ooo#.....",
                "#oooo#....",
                "#ooooo#...",
                "#oooooo#..",
                "#ooooooo#.",
                "#oooo#####",
                "#oo#o#....",
                "##.#o#....",
                "....##....",
            };
            var tex = new Texture2D(10, 13, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            Color ink = new Color32(0x2b, 0x27, 0x38, 255), fill = down ? new Color32(0x7f, 0xd3, 0xa0, 255) : new Color32(0xf2, 0xf0, 0xf6, 255);
            for (int y = 0; y < 13; y++)
                for (int x = 0; x < 10; x++)
                {
                    char ch = rows[12 - y][x];
                    tex.SetPixel(x, y, ch == '#' ? ink : ch == 'o' ? fill : Color.clear);
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 10, 13), new Vector2(0f, 1f), 1f);
        }

        private Vector2 cursorAt;
        private void PlaceCursor(Vector2 screen)
        {
            cursorAt = screen;
            cursor.anchoredPosition = new Vector2(Mathf.Round(screen.x / 2f) * 2f, Mathf.Round(screen.y / 2f) * 2f);
        }

        private Transform PanelChild(string name)
        {
            var panel = FindObjectOfType<HiveLinkPanel>(true);
            if (panel == null) return null;
            return panel.GetComponentsInChildren<Transform>(false).FirstOrDefault(t => t.name == name && t.gameObject.activeInHierarchy);
        }

        private static Vector2 ScreenCenter(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null) return t.position;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            return (corners[0] + corners[2]) * .5f;
        }

        /// <summary>Glides the cursor to a control (eased), presses it and invokes it like a click.</summary>
        private IEnumerator Click(string name, float glide = .55f, float after = .35f)
        {
            Transform target = PanelChild(name);
            if (target == null) { report.Append($"[no control {name}] "); yield break; }
            Vector2 from = cursorAt, to = ScreenCenter(target) + new Vector2(2f, -4f);
            float t0 = Time.time;
            while (Time.time < t0 + glide)
            {
                float u = Mathf.SmoothStep(0f, 1f, (Time.time - t0) / glide);
                Vector2 p = Vector2.Lerp(from, to, u) + new Vector2(0f, Mathf.Sin(u * Mathf.PI) * 18f);
                PlaceCursor(p);
                yield return null;
            }
            PlaceCursor(to);
            cursorImage.sprite = cursorDown;
            yield return Wait(.10f);
            target.GetComponent<Button>()?.onClick.Invoke();
            Mark("click_" + name);
            yield return Wait(.08f);
            cursorImage.sprite = cursorUp;
            yield return Wait(after);
        }

        // ================================================================== camera

        private void TakeCamera()
        {
            cam = Camera.main;
            if (cam == null) return;
            brain = cam.GetComponent<CinemachineBrain>();
            if (brain != null) { brainWasEnabled = brain.enabled; brain.enabled = false; }
            camSizeBefore = cam.orthographicSize;
            camPosBefore = cam.transform.position;
        }

        private void Frame(Vector2 at, float size, float ease = 1.2f) { camGoal = at; camSizeGoal = size; camEase = ease; }

        private void ApplyCamera()
        {
            if (cam == null) return;
            cam.transform.position = new Vector3(camPos.x, camPos.y, cam.transform.position.z);
            cam.orthographicSize = camSize;
        }

        private void LateUpdate()
        {
            if (!IsReady || restored) return;
            // critically damped glide towards the goal: no stops between beats
            float k = 1f - Mathf.Exp(-Time.deltaTime * 3.2f / Mathf.Max(.2f, camEase));
            camPos = Vector2.Lerp(camPos, camGoal, k);
            camSize = Mathf.Lerp(camSize, camSizeGoal, k);
            if (Mathf.Abs(camSize - camSizeGoal) < .01f) camSize = camSizeGoal;
            ApplyCamera();
            AnimateBots();
            KeepChromeHidden();
            SoftenPanelText();
        }

        /// <summary>HUD canvases that switch themselves back on (chat, hotbar...) stay off; the Hive Link panel draws at 2x.</summary>
        private void KeepChromeHidden()
        {
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || !canvas.enabled || canvas.renderMode == RenderMode.WorldSpace || canvas == overlay) continue;
                if (canvas.GetComponentInParent<HiveLinkPanel>() != null) { if (canvas.scaleFactor != 2f) canvas.scaleFactor = 2f; continue; }
                canvas.enabled = false;
                if (!hiddenCanvases.Contains(canvas)) hiddenCanvases.Add(canvas);
            }
        }

        /// <summary>The panel's preview-only notes would read as errors in a demo; they become the live wording.</summary>
        private string lastPay = "0.35 USDC";
        private void SoftenPanelText()
        {
            if (!HiveLinkPanel.IsOpen) return;
            var panel = FindObjectOfType<HiveLinkPanel>(true);
            if (panel == null) return;
            foreach (var t in panel.GetComponentsInChildren<TMP_Text>(false))
            {
                string s = t.text;
                if (string.IsNullOrEmpty(s)) continue;
                if (s.StartsWith("PAY ")) { lastPay = s.Substring(4); continue; }
                string n = s;
                if (s == "PREVIEW") n = "ONLINE";
                else if (s.Contains("PREVIEW: NOTHING IS CHARGED") || s.Contains("THE HIVE IS OFFLINE")) n = "MAX 10 USDC PER ORDER";
                else if (s.Contains("PREVIEW\nNOTHING\nCHARGED")) n = "USUALLY\nUNDER A\nMINUTE";
                else if (s.StartsWith("PAID:")) n = s.Replace("PREVIEW", lastPay);
                else if (s.Contains("TEST: TO YOUR NEAR")) n = s.Replace("TEST: TO YOUR NEAR", "TO THE HIVE");
                if (n != s) t.text = n;
            }
        }

        // ================================================================== droids

        private void AnimateBots()
        {
            int tick = Mathf.FloorToInt(Time.time * ArtFps);
            foreach (var b in bots)
            {
                if (b.body == null || b.sheet == null || b.sheet.Length < 44) continue;
                Sprite s;
                if (b.walking) s = b.sheet[b.walkSlot + tick % 4];
                else if (b.action >= 0 && b.actions != null && b.actions.Length >= 16)
                    s = b.actions[b.action * 4 + Mathf.FloorToInt((Time.time - b.actionAt) * ArtFps) % 4];
                else s = b.sheet[b.facing + (tick / 2) % 4];
                if (b.body.sprite != s) b.body.sprite = s;
            }
        }

        private IEnumerator Walk(Bot b, Vector2 target, float speed = 2.2f)
        {
            b.action = -1;
            List<Vector2> route = null; bool planned = false;
            var router = PlayerController.Instance != null ? PlayerController.Instance.GetComponent<PlayerTapToMove>() : null;
            if (router != null && router.TryPlanRoute(b.root.position, target, r => { route = r; planned = true; }, b.root))
            {
                float deadline = Time.unscaledTime + 3f;
                while (!planned && Time.unscaledTime < deadline) yield return null;
            }
            if (route == null || route.Count == 0) route = new List<Vector2> { target };
            b.walking = true;
            foreach (var point in route)
            {
                while (Vector2.Distance(b.root.position, point) > .02f)
                {
                    Vector2 cur = b.root.position, d = point - cur;
                    Vector2 next = Vector2.MoveTowards(cur, point, speed * Time.deltaTime);
                    b.root.position = new Vector3(next.x, next.y, b.root.position.z);
                    b.walkSlot = Mathf.Abs(d.x) >= Mathf.Abs(d.y) ? (d.x > 0 ? WalkRight : WalkLeft) : (d.y > 0 ? WalkUp : WalkDown);
                    b.facing = b.walkSlot == WalkRight ? IdleRight : b.walkSlot == WalkLeft ? IdleLeft : b.walkSlot == WalkUp ? IdleUp : IdleDown;
                    yield return null;
                }
            }
            b.walking = false;
            b.facing = IdleDown;
        }

        private IEnumerator Act(Bot b, int action, float seconds)
        {
            b.action = action; b.actionAt = Time.time;
            yield return Wait(seconds);
            b.action = -1;
        }

        private void ShowPhase(Crop c, int phase)
        {
            phase = Mathf.Clamp(phase, 0, phases.Length - 1);
            c.phase = phase;
            var p = phases[phase];
            Sprite s = null;
            if (p.GetState("Health", out PlantState st) && st.sprite != null) s = st.sprite;
            else if (p.States != null && p.States.Length > 0) s = p.States[0].sprite != null ? p.States[0].sprite : p.States[0].frames?.FirstOrDefault();
            c.r.sprite = s;
            c.r.enabled = s != null;
            StartCoroutine(Pop(c.r.transform));
        }

        private static IEnumerator Pop(Transform t)
        {
            float t0 = Time.time;
            while (Time.time < t0 + .25f)
            {
                float u = (Time.time - t0) / .25f;
                float s = 1f + Mathf.Sin(u * Mathf.PI) * .18f;
                t.localScale = new Vector3(s, 2f - s, 1f);
                yield return null;
            }
            t.localScale = Vector3.one;
        }

        /// <summary>The harvested crop's icon jumps out and rises into the bag (fades), like a pickup.</summary>
        private IEnumerator HarvestPop(Vector2 at)
        {
            if (harvestIcon == null) yield break;
            var go = new GameObject("Harvest pop");
            go.transform.SetParent(stage, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = harvestIcon; sr.sortingOrder = FxOrder;
            float t0 = Time.time, dur = .9f;
            while (Time.time < t0 + dur)
            {
                float u = (Time.time - t0) / dur;
                float y = .35f + Mathf.Sin(Mathf.Min(1f, u * 1.6f) * Mathf.PI * .5f) * .9f + u * .3f;
                go.transform.position = at + new Vector2(0f, y);
                sr.color = new Color(1f, 1f, 1f, u < .65f ? 1f : 1f - (u - .65f) / .35f);
                yield return null;
            }
            Destroy(go);
        }

        private void SetJob(uint target, uint done, uint growing, byte status, string note)
        {
            var row = new SpacetimeDB.Types.HiveJob
            {
                JobId = 41, ClientJobId = "near-demo", Site = HiveJobNetwork.SiteFarm, DroidId = "HX-07", PlantIndex = CropPlant,
                HarvestItemId = CropHarvest, Target = target, Delivered = done, Growing = growing, Status = status,
                MicrosPerCrop = 39_000, EscrowMicros = 39_000UL * target, PaidOutMicros = 39_000UL * done, RefundMicros = 0UL, Note = note,
            };
            HiveJobNetwork.SetPreviewJobs(new[] { row });
        }

        // ================================================================== the film

        private IEnumerator Play()
        {
            while (!rolling) yield return null;
            var player = PlayerController.Instance;
            uint target = (uint)crops.Count;

            // ---- 1. arrive at the kiosk
            Mark("open");
            // a slow push in from the wide shot of the farm road onto the kiosk, the player and the droids
            Frame(kioskFoot + new Vector2(.3f, .5f), Zoom4, 2.6f);
            yield return Wait(4.2f);

            // ---- 2. HIVE LINK
            Mark("panel");
            kiosk.Open();
            Frame(kioskFoot + new Vector2(0f, .8f), Zoom4, 1.0f);
            cursor.gameObject.SetActive(true);
            PlaceCursor(new Vector2(Screen.width * .5f + 220f, Screen.height * .5f - 160f));
            yield return Wait(1.6f);
            Mark("roster");
            yield return Click("Next", .6f, .9f);       // MULE
            yield return Click("Next", .35f, .9f);      // SPRIG
            yield return Click("Prev", .35f, .7f);
            yield return Click("Prev", .35f, 1.1f);     // back to TILLER
            Mark("hire");
            yield return Click("Hire", .6f, 1.0f);
            Mark("contract");
            for (int guard = 0; guard < 20; guard++)
            {
                string d = HiveLinkPanel.Describe();
                int qi = d.IndexOf("qty="); uint q = 0;
                if (qi >= 0) uint.TryParse(new string(d.Substring(qi + 4).TakeWhile(char.IsDigit).ToArray()), out q);
                if (q == target) break;
                yield return Click(q > target ? "Less" : "More", guard == 0 ? .6f : .14f, .25f);
            }
            yield return Wait(1.4f);
            Mark("pay");
            yield return Click("Send", .7f, .2f);
            Mark("transmit");
            yield return Wait(6.8f);                    // five steps, 1.2 s each, then HIRED
            Mark("hired");
            SetJob(target, 0, 0, HiveJobNetwork.Active, "");
            foreach (var b in bots) if (b.badge != null) b.badge.OnGig = true;
            yield return Wait(2.6f);
            Mark("power");
            yield return Click("Power", .7f, 1.0f);
            cursor.gameObject.SetActive(false);

            // ---- 3. the Hive at work
            Mark("dispatch");
            Vector2 block = crops.Aggregate(Vector2.zero, (a, c) => a + c.at) / crops.Count;
            Frame(Vector2.Lerp(kioskFoot, block, .5f) + Vector2.up * .6f, Zoom3, 2.2f);
            var jobs = new List<Coroutine>();
            for (int i = 0; i < bots.Count; i++) jobs.Add(StartCoroutine(PlantRow(bots[i], i * .35f)));
            yield return Wait(1.6f);
            Frame(block + new Vector2(0f, .5f), Zoom4, 2.4f);
            foreach (var j in jobs) yield return j;
            Mark("planted");
            SetJob(target, 0, target, HiveJobNetwork.Active, "");

            // time-lapse: every crop steps through its growth phases
            Mark("grow");
            Frame(block + new Vector2(0f, .7f), Zoom5, 3.0f);
            foreach (var b in bots) { b.action = 3; b.actionAt = Time.time; }
            for (int p = 1; p < phases.Length; p++)
            {
                foreach (var c in crops) ShowPhase(c, p);
                yield return Wait(p == phases.Length - 1 ? 1.2f : .95f);
            }
            foreach (var b in bots) b.action = -1;

            // harvest: each pick pays out and lands in the bag
            Mark("harvest");
            Frame(block + new Vector2(0f, .5f), Zoom4, 2.0f);
            jobs.Clear();
            for (int i = 0; i < bots.Count; i++) jobs.Add(StartCoroutine(HarvestRow(bots[i], i * .3f, target)));
            foreach (var j in jobs) yield return j;
            Mark("delivered");
            SetJob(target, target, 0, HiveJobNetwork.Completed, "");
            foreach (var b in bots) if (b.badge != null) b.badge.OnGig = false;

            // ---- 4. back to the kiosk: JOBS
            jobs.Clear();
            for (int i = 0; i < bots.Count; i++)
                jobs.Add(StartCoroutine(Walk(bots[i], kioskFoot + new Vector2(1.7f + i * 1.1f, -.4f - i * .25f))));
            Frame(kioskFoot + new Vector2(.6f, .6f), Zoom4, 1.8f);
            foreach (var j in jobs) yield return j;
            Mark("jobs");
            kiosk.Open();
            cursor.gameObject.SetActive(true);
            PlaceCursor(new Vector2(Screen.width * .5f + 120f, Screen.height * .5f - 140f));
            yield return Wait(.9f);
            yield return Click("NavJOBS", .7f, 3.2f);
            Mark("end");
            yield return Wait(.5f);
            IsFinished = true;
        }

        private IEnumerator PlantRow(Bot b, float delay)
        {
            yield return Wait(delay);
            foreach (var c in b.plots)
            {
                yield return Walk(b, c.at + Vector2.down * .55f);
                yield return Act(b, 0, .75f);   // till
                ShowPhase(c, 0);
                yield return Act(b, 1, .75f);   // water
            }
        }

        private IEnumerator HarvestRow(Bot b, float delay, uint target)
        {
            yield return Wait(delay);
            foreach (var c in b.plots.AsEnumerable().Reverse())
            {
                yield return Walk(b, c.at + Vector2.down * .55f);
                yield return Act(b, 2, .6f);
                c.r.enabled = false;
                StartCoroutine(HarvestPop(c.at));
                delivered++;
                SetJob(target, delivered, (uint)crops.Count(x => x.r.enabled), HiveJobNetwork.Active, "");
                yield return Wait(.15f);
            }
        }

        private static IEnumerator Wait(float s)
        {
            float until = Time.time + s;
            while (Time.time < until) yield return null;
        }

        // ================================================================== restore

        public void Stop() { Restore(); Destroy(gameObject); }

        private void OnDestroy()
        {
            Restore();
            if (Active == this) Active = null;
        }

        private void Restore()
        {
            if (restored) return;
            restored = true;
            IsFinished = true;
            HiveLinkPanel.CloseIfOpen();
            HiveJobNetwork.SetPreviewJobs(Array.Empty<SpacetimeDB.Types.HiveJob>());
            HiveOffline(false);
            FullRes(false);
            SettingsManager.SetClickToMove(oldClickToMove);
            foreach (var b in bots) if (b.badge != null) b.badge.OnGig = false;
            if (siteDroid != null)
            {
                siteDroid.siteId = oldSiteId ?? siteDroid.siteId;
                foreach (var body in siteDroid.GetComponentsInChildren<Rigidbody2D>()) body.simulated = true;
            }
            foreach (var go in spawned) if (go != null) Destroy(go);
            foreach (var c in hiddenCanvases) if (c != null) c.enabled = true;
            foreach (var go in hiddenObjects) if (go != null) go.SetActive(true);
            foreach (var r in hiddenRenderers) if (r != null) r.enabled = true;
            foreach (var b in pausedBehaviours) if (b != null) b.enabled = true;
            if (hidHud) GameUIManager.SetCinematicMode(false);
            if (cam != null)
            {
                cam.orthographicSize = camSizeBefore;
                cam.transform.position = camPosBefore;
                if (brain != null) brain.enabled = brainWasEnabled;
            }
            SoundManager.Instance?.SetVolume(oldMusic, AudioType.MUSIC);
            StarterCrate.Suppress = false;
            Bakeland.Weather.WeatherSystem.Instance?.EndLocalPreview();
            PlayerController.canMove = true;
            PlayerController.canInteract = true;
        }
    }
}
#endif
