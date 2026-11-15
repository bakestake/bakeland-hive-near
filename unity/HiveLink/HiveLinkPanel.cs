using System;
using System.Collections.Generic;
using System.Linq;
using Bakeland.UI.Merged;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// HIVE LINK (14 Nov 2026): the Hive Link terminal's screen, where players contract Hive worker droids over the Array
    /// (the NEAR relay network in Bakeland's fiction). A 448 x 256 art-pixel modal at k = 1 on its own integer-scaled
    /// canvas, built at runtime from the art in Assets/UI/HiveLink/Art (made with the "Hive Link Terminal" design canvas):
    /// the console chrome is one image; boxes, tags, keys and chips are rounded 9-slices; texts are VCR Mini.
    ///
    /// Screens: ROSTER (droid blueprints with callouts, stats, pulse) -> CONTRACT (a crop this kiosk's farm grows and a
    /// target quantity, priced per delivered crop, max 10 USDC) -> PAYING (one Solana USDC payment) -> HIRED (receipt).
    /// JOBS tracks the player's durable orders from the server (HiveJobNetwork / bakeland-spacetime HiveJobs.cs): status
    /// and crops delivered of the target. HISTORY lists this session's hires.
    ///
    /// 15 Nov 2026: the player never needs a NEAR wallet or leaves the game. The only step is approving one USDC transfer
    /// in their Solana wallet, straight to the studio treasury (BAKELAND_HIVE_TREASURY); they pay only the network fee.
    /// The backend pays the droids' earnings into the Hive's NEAR Intents agent, grouped per job. The RELAY ID page (NEAR
    /// wallet linking through an external page) was removed.
    ///
    /// Each kiosk works the farm beside it (siteId): the MainGame farm kiosk offers MainGame crops, the desert kiosk
    /// desert crops. The droid plants the player's own seeds (they leave the bag when the order is placed).
    ///
    /// Payments: <see cref="NearIntentsClient"/> drives the panel when the client env sets BAKELAND_NEAR_ENABLED=true and
    /// BAKELAND_NEAR_API_URL (or INVENTORY_API_URL) points at an API with the near-intents routes. A live send first places
    /// the order on the server (CreateHiveJob), then PayHiveJob: the API prepares the transfer of the job's escrow amount
    /// (memo = the job's ClientJobId), the wallet signs, the API submits it and funds the job once Solana confirms it
    /// (usually seconds). GetHivePayment polls; RESEND asks again. The open payment is kept in PlayerPrefs so a reload
    /// resumes tracking. Without the env flags everything runs as a marked PREVIEW and no funds move.
    /// </summary>
    public sealed class HiveLinkPanel : MonoBehaviour
    {
        public const string ResourcePath = "HiveLink/HiveLinkPanel";
        /// <summary>True when the NEAR Intents route is configured for this build (see the class summary); else PREVIEW.</summary>
        public static bool Live
        {
            get
            {
                if (liveFlag == null)
                {
                    liveFlag = false;
                    try
                    {
                        // BAKELAND_NEAR_API_URL sends only Hive Link to another API (e.g. the staging Worker for a test
                        // build); everything else keeps INVENTORY_API_URL.
                        string root = null;
                        if (BackendEndpointGuard.IsFeatureEnabled("BAKELAND_NEAR_ENABLED") &&
                            (BackendEndpointGuard.TryGetEnabledBaseUrl("BAKELAND_NEAR_API_URL", "Hive Link", out root, true) ||
                             BackendEndpointGuard.TryGetEnabledBaseUrl("INVENTORY_API_URL", "Hive Link", out root, true)))
                        {
                            client = new NearIntentsClient(root);
                            hiveTreasury = GetEnvVars.Get("BAKELAND_HIVE_TREASURY", false);
                            liveFlag = true;
                        }
                    }
                    catch (Exception e) { Log.LogWarning("[HiveLink] NEAR route off, running PREVIEW: " + e.Message); }
                }
                return liveFlag.Value;
            }
        }
        private static bool? liveFlag;
        private static NearIntentsClient client;
        private static string hiveTreasury;   // the studio's Solana treasury that Hive payments go to
        private const string PendingKey = "HiveLink.PendingOp";
        public const decimal MaxUsdcPerSignal = 10m;
        public const decimal UsdcPerCredit = 2.35m;   // placeholder until a live quote
        private const float QuoteSeconds = 600f;

        [Header("Set by HiveLinkAuthoring")]
        public Sprite[] art;
        public TMP_FontAsset vcrFont, thaleahFont;

        // ------------------------------------------------------------------ palette (faded phosphor + console ink)
        private static readonly Color32 Ph = C(0x7fd3a0), PhHi = C(0xc4f2d4), PhMid = C(0x4f9e74), PhDim = C(0x3d7a5c), PhDeep = C(0x2d5c48);
        private static readonly Color32 Val = C(0xe58f8f), Amb = C(0xe3bf6b), Ink = C(0x2b2738), Engr = C(0x4a4160), Dark = C(0x10181a);
        private static readonly Color32 RedLed = C(0xd66a6a), LeverOn = C(0xa3364a);
        private static Color32 C(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        private static string Hex(Color32 c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        private sealed class Droid
        {
            public string id, name, cls, relay, status, note, art; public int rate; public bool farm; public int[] pips; public (float x, float y, string label)[] callouts;
        }
        private static readonly Droid[] Droids =
        {
            new Droid { id = "HX-07", name = "TILLER", cls = "FARMHAND", farm = true, rate = 5, relay = "hx07.hive", status = "AVAILABLE", note = "TENDS, WATERS, HARVESTS.", art = "tiller", pips = new[] { 4, 2, 1, 3 }, callouts = new[] { (7f, 5f, "VISOR"), (1.5f, 11f, "SCOOP ARM") } },
            new Droid { id = "HX-12", name = "MULE", cls = "HAULER", rate = 6, relay = "hx12.hive", status = "COMING SOON", note = "HAULS CRATES TO MARKET.", art = "hauler", pips = new[] { 1, 5, 2, 2 }, callouts = new[] { (7f, 9f, "CARGO DECK"), (3f, 13f, "STRUTS") } },
            new Droid { id = "HX-19", name = "SPRIG", cls = "SEEDER", farm = true, rate = 4, relay = "hx19.hive", status = "AVAILABLE", note = "PLANTS. GENTLE WITH SPORES.", art = "sprig", pips = new[] { 5, 1, 1, 5 }, callouts = new[] { (7f, 1f, "SENSOR"), (3f, 12f, "SEED PODS") } },
            new Droid { id = "HX-23", name = "WARDEN", cls = "SENTRY", rate = 8, relay = "hx23.hive", status = "COMING SOON", note = "GUARDS THE PLOT AT NIGHT.", art = "warden", pips = new[] { 1, 2, 5, 2 }, callouts = new[] { (7f, 4f, "VISOR"), (1.5f, 9f, "ARM GUARD") } },
        };

        /// <summary>A crop the CONTRACT picker offers: from the server (FarmCropVersion + HiveCropRate) or, without the
        /// Hive server, the preview list (rates as SeedHiveLabor prices them).</summary>
        private sealed class CropOption
        {
            public uint plant; public string name; public int seed, harvest; public uint level, grow; public ulong micros; public uint perPlant = 1;
        }
        private static readonly CropOption[] PreviewFarmCrops =
        {
            new CropOption { plant = 31, name = "Carrot", seed = 31, harvest = 32, level = 1, grow = 20, micros = 39_000 },
            new CropOption { plant = 27, name = "Wheat", seed = 27, harvest = 35, level = 1, grow = 32, micros = 41_000 },
            new CropOption { plant = 18, name = "Mugwort", seed = 18, harvest = 44, level = 1, grow = 96, micros = 56_000 },
            new CropOption { plant = 29, name = "Potato", seed = 29, harvest = 36, level = 2, grow = 135, micros = 73_000 },
            new CropOption { plant = 28, name = "Pumpkin", seed = 28, harvest = 34, level = 2, grow = 240, micros = 105_000 },
            new CropOption { plant = 30, name = "Cabbage", seed = 30, harvest = 33, level = 3, grow = 480, micros = 125_000 },
        };
        private static readonly CropOption[] PreviewDesertCrops =
        {
            new CropOption { plant = 76, name = "Dune Sage", seed = 76, harvest = 77, level = 1, grow = 90, micros = 35_000 },
            new CropOption { plant = 60, name = "Cactus Pear", seed = 60, harvest = 61, level = 1, grow = 200, micros = 54_000 },
            new CropOption { plant = 74, name = "Sun Gourd", seed = 74, harvest = 75, level = 3, grow = 300, micros = 70_000 },
            new CropOption { plant = 54, name = "Glassroot", seed = 54, harvest = 55, level = 5, grow = 720, micros = 175_000 },
        };
        private const uint PlotsPerDroid = 8, MaxCrops = 200;

        private enum Page { Roster, Contract, Transmit, Live, Log, Jobs }
        private static readonly string[] Steps = { "PRICE SET", "WALLET APPROVED", "PAYMENT SENT", "PAYMENT CONFIRMED", "DROID ON ITS WAY" };

        private static HiveLinkPanel instance;
        private Canvas canvas;
        private RectTransform frame;
        private readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        private readonly Dictionary<Page, RectTransform> pages = new Dictionary<Page, RectTransform>();
        private bool built, visible, releasePending;
        private int lastW, lastH; private bool lastMobile;

        // state
        private Page page;
        private int sel, cropSel;
        private uint qty = 10;
        private int jobsFirst;
        private float cancelArmedAt = -10f;
        private string jobNote;   // why a contract can't go ahead (offline, no seeds, a droid already working here...)
        private string siteId = "";
        private float quoteAt, transmitAt;
        private int stepsDone;
        private string lastTransmitError = "";
        private readonly List<string> log = new List<string>();

        // live (NEAR Intents) state
        private string opId, opKey, liveError, liveNote;
        private string paySignature;
        private bool working, quoting, sending, polling, failed;
        private int liveSteps;
        private float lastPoll;
        private TextMeshProUGUI contractStatus;

        // dynamic views
        private Image dome, leverKnob, holo, sendArrow, tint, band, walletBar;
        private TextMeshProUGUI readout, sendText;
        private TextMeshProUGUI[] leverLabels = new TextMeshProUGUI[4];
        private readonly Image[] navRed = new Image[4], navGreen = new Image[4];
        private TextMeshProUGUI rosterUnit, rosterData, rosterNote, contractTag, quoteTag, quoteText, shiftsNote, sendLabel;
        private TextMeshProUGUI transmitList, transmitPct, transmitNote, liveName, liveSub, liveReceipt, logText, rosterHire;
        private TextMeshProUGUI node1Val, node2Val, node3Id, node3Val;
        private Image rosterBp, contractBp, liveBp;
        private readonly Image[] tube = new Image[6], progress = new Image[10], pulse = new Image[31];
        private readonly Image[,] pips = new Image[4, 5];
        private readonly List<GameObject> callouts = new List<GameObject>();
        private RectTransform rosterPage;
        private Button sendKey, requoteKey;
        private Image cropIcon;
        private TextMeshProUGUI cropName, cropRate, qtyText, seedsText, farmText, jobsPage, jobsEmpty, cancelLabel;
        private GameObject jobsPageTag, jobsPrev, jobsNext, jobsCancel;
        private const int JobRows = 4, JobSegs = 24;
        private readonly Image[] jobCards = new Image[JobRows], jobIcons = new Image[JobRows];
        private readonly TextMeshProUGUI[] jobTitle = new TextMeshProUGUI[JobRows], jobState = new TextMeshProUGUI[JobRows], jobCount = new TextMeshProUGUI[JobRows];
        private readonly Image[,] jobBar = new Image[JobRows, JobSegs];
        private readonly TextMeshProUGUI[] navLabel = new TextMeshProUGUI[4];
        private Image powerKey, powerLed, shutTop, shutBottom, shutLine;
        private bool powering;
        private float powerAt;
        private int lastJobsTick;

        // ================================================================== open / close

        /// <summary>Opens the terminal screen (HiveLinkTerminal.Open).</summary>
        public static bool Open(string site = null)
        {
            if (instance == null)
            {
                var prefab = Resources.Load<HiveLinkPanel>(ResourcePath);
                if (prefab == null) { Log.LogWarning("[HiveLink] missing Resources/" + ResourcePath + " (Bakeland/World/Hive Link/Build)"); return false; }
                instance = Instantiate(prefab);
                instance.name = "HiveLink";
                DontDestroyOnLoad(instance.gameObject);
            }
            instance.siteId = site ?? "";
            instance.Show();
            return true;
        }

        public static bool IsOpen => instance != null && instance.visible;
        /// <summary>Raised when a contract settles (site id, droid id); the site's Hive droid goes on a gig.</summary>
        public static event Action<string, string> Contracted;
        public static void CloseIfOpen() { if (instance != null) instance.Hide(); }

#if UNITY_EDITOR
        /// <summary>Probes: opens on a page (roster, contract, transmit, live, log, jobs).</summary>
        public static void Preview(string pageName, int droid = 0)
        {
            Open(instance != null && instance.visible && !string.IsNullOrEmpty(instance.siteId) ? instance.siteId : "probe");
            if (instance == null || !Enum.TryParse(pageName, true, out Page p)) return;
            instance.sel = Mathf.Clamp(droid, 0, Droids.Length - 1);
            if (p == Page.Transmit) { instance.transmitAt = Time.unscaledTime - 3.7f; }
            instance.Go(p, false);
        }
        /// <summary>Probe: presses the POWER key.</summary>
        public static void PressPower() { if (instance != null) instance.PowerOff(); }
        public static string Describe() => instance == null ? "no instance" :
            $"visible={instance.visible} powering={instance.powering} page={instance.page} sel={instance.sel} crop={instance.Crop?.name} qty={instance.qty} usdc={instance.Usdc} steps={instance.stepsDone} scale={instance.canvas.scaleFactor} site={instance.siteId} hive={HiveJobNetwork.IsReady} jobs={HiveJobNetwork.Jobs().Count}";
        /// <summary>Probe: picks a crop step and a quantity on CONTRACT.</summary>
        public static void PreviewOrder(int cropStep, uint quantity)
        {
            if (instance == null) return;
            instance.StepCrop(cropStep); instance.qty = quantity; instance.ClampQty(); instance.Redraw();
        }
#endif

        private void Awake()
        {
            canvas = GetComponent<Canvas>();
            SceneManager.activeSceneChanged += OnSceneChanged;
            HiveJobNetwork.Changed += OnJobsChanged;
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            HiveJobNetwork.Changed -= OnJobsChanged;
            if (instance == this) instance = null;
            UIInputLock.Release(this);
        }

        private void OnSceneChanged(Scene a, Scene b) => Hide();

        private void OnJobsChanged() { if (visible && (page == Page.Jobs || page == Page.Contract)) Redraw(); }

        private void Show()
        {
            if (!built) Build();
            visible = true;
            releasePending = false;
            canvas.enabled = true;
            frame.parent.gameObject.SetActive(true);
            UIInputLock.Acquire(this);
            ApplyScale(true);
            if (page == Page.Transmit && stepsDone >= Steps.Length) page = Page.Live;
            if (Live && page != Page.Transmit && ResumePending()) { Go(Page.Transmit, false); MergedUIAudio.Play(UISoundKind.PanelOpen); return; }
            Go(page == Page.Transmit ? Page.Transmit : Page.Roster, false);
            MergedUIAudio.Play(UISoundKind.PanelOpen);
        }

        private void Hide()
        {
            if (!visible) return;
            visible = false;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            frame.parent.gameObject.SetActive(false);
            canvas.enabled = false;
            releasePending = true;               // next frame, so the same Escape can't also open the game menu
            MergedUIAudio.Play(UISoundKind.PanelClose);
        }

        /// <summary>
        /// Own integer scale. Browser: the Jupiter OS rule floor(min(w/640, h/320)) made ~30 % smaller (15 Nov 2026), rounded
        /// to the nearest whole art-pixel scale so the pixel art stays crisp (3 -> 2 at 1080p, 4 -> 3 at 1440p). Phones keep
        /// 264 px of height, already the smallest readable size there.
        /// </summary>
        private void ApplyScale(bool force)
        {
            int w = Screen.width, h = Screen.height;
            bool mobile = PixelCanvasScaler.DetectProfile(false) == UIProfile.Mobile;
            if (!force && w == lastW && h == lastH && mobile == lastMobile) return;
            lastW = w; lastH = h; lastMobile = mobile;
            int scale = mobile
                ? Mathf.Max(1, Mathf.Min(Mathf.FloorToInt(h / 264f), Mathf.FloorToInt(w / 460f)))
                : Mathf.Max(1, Mathf.RoundToInt(Mathf.FloorToInt(Mathf.Min(w / 640f, h / 320f)) * 0.7f));
            canvas.scaleFactor = scale;
            canvas.pixelPerfect = true;
        }

        // ================================================================== build

        private Sprite S(string name) => sprites.TryGetValue("hl_" + name, out Sprite s) ? s : null;

        private void Build()
        {
            built = true;
            foreach (Sprite s in art) if (s != null) sprites[s.name] = s;

            var root = NewRect(transform, "Root");
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = root.offsetMax = Vector2.zero;
            var dim = NewRect(root, "Dim");
            dim.anchorMin = Vector2.zero; dim.anchorMax = Vector2.one; dim.offsetMin = dim.offsetMax = Vector2.zero;
            var dimImg = dim.gameObject.AddComponent<Image>(); dimImg.color = new Color(0f, 0f, 0f, .6f);
            var dimBtn = dim.gameObject.AddComponent<Button>(); dimBtn.transition = Selectable.Transition.None; dimBtn.onClick.AddListener(() => { if (!Busy) Hide(); });

            frame = NewRect(root, "Frame");
            frame.anchorMin = frame.anchorMax = new Vector2(.5f, .5f); frame.pivot = new Vector2(.5f, .5f);
            frame.sizeDelta = new Vector2(448, 256); frame.anchoredPosition = Vector2.zero;
            Img(frame, "Chrome", S("chrome"), 0, 0, 448, 256).raycastTarget = true;   // swallows clicks on the console

            // ---- left column
            Text(frame, "RelayedBy", "POWERED BY", 7, 17, 60, 10, Engr);
            dome = Img(frame, "Beacon", S("dome_g"), 10, 58, 13, 9);
            Text(frame, "BeaconLabel", "BEACON", 27, 58, 44, 10, Ink);
            string[] lev = { "IDLE", "SCAN", "LINK", "SEND" };
            int[] levY = { 128, 138, 148, 157 };
            for (int i = 0; i < 4; i++) leverLabels[i] = Text(frame, "Lever" + lev[i], lev[i], 39, levY[i], 32, 10, Ink);
            leverKnob = Img(frame, "LeverKnob", S("knob_h"), 26, 141, 11, 5);
            Img(frame, "SwitchKnob", S("knob_v"), 10, 132, 5, 13);
            Img(frame, "Auto", S("toggle_on"), 9, 178, 11, 8);
            Text(frame, "AutoLabel", "AUTO", 24, 177, 40, 10, Ink);

            // POWER: the bottom-left plate (6..69 x 232..249) becomes the power key; it shuts the screen down and closes
            Fill(frame, "PowerPlate", 6, 232, 64, 18, C(0xb9b8c6));
            powerKey = Img(frame, "PowerKey", S("power"), 9, 234, 15, 15);
            Text(frame, "PowerLabel", "POWER", 28, 237, 34, 10, Ink);
            powerLed = Img(frame, "PowerLed", S("led_g"), 61, 239, 6, 5);
            var power = Img(frame, "Power", null, 6, 232, 64, 18);
            power.color = new Color(1, 1, 1, 0); power.raycastTarget = true;
            var pb = power.gameObject.AddComponent<Button>(); pb.transition = Selectable.Transition.None; pb.targetGraphic = power;
            pb.onClick.AddListener(PowerOff);
            var pt = power.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown }; down.callback.AddListener(_ => { if (!Busy) powerKey.sprite = S("power_dn"); });
            var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp }; up.callback.AddListener(_ => { if (!powering) powerKey.sprite = S("power"); });
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit }; exit.callback.AddListener(_ => { if (!powering) powerKey.sprite = S("power"); });
            pt.triggers.Add(down); pt.triggers.Add(up); pt.triggers.Add(exit);

            // ---- right column
            Text(frame, "Title", "HIVE LINK", 378, 2, 66, 10, Ink, TextAlignmentOptions.Top);
            readout = Text(frame, "Readout", "ONLINE", 380, 18, 62, 10, Ph, TextAlignmentOptions.Top);
            string[] nav = { "ROSTER", "CONTRACT", "JOBS", "HISTORY" };
            Page[] navPage = { Page.Roster, Page.Contract, Page.Jobs, Page.Log };
            for (int i = 0; i < 4; i++)
            {
                int y = 34 + i * 29; Page target = navPage[i];
                int index = i;
                Hit(frame, "Nav" + nav[i], 378, y, 66, 26, () => { if (!Busy && NavEnabled(index)) Go(target, true); else MergedUIAudio.Play(UISoundKind.Deny); });
                navLabel[i] = Text(frame, "NavLabel" + i, nav[i], 378, y + 1, 66, 10, Ink, TextAlignmentOptions.Top);
                navRed[i] = Img(frame, "NavRed" + i, S("led_r"), 395, y + 16, 6, 5);
                navGreen[i] = Img(frame, "NavGreen" + i, S("led_gd"), 409, y + 16, 6, 5);
            }
            holo = Img(frame, "Holo", S("holo_tiller"), 388, 160, 16, 16);
            walletBar = Img(frame, "Wallet", null, 383, 193, 35, 4); walletBar.color = C(0x5d8fd0);
            var sig = Img(frame, "Signal", null, 383, 207, 52, 4); sig.color = C(0xd99a52);
            sendText = Text(frame, "SendText", "SAFE", 383, 221, 35, 10, C(0x6b4a50), TextAlignmentOptions.Top);
            Hit(frame, "SendKey", 422, 221, 21, 29, () => { if (page == Page.Contract) Send(); else MergedUIAudio.Play(UISoundKind.Deny); });
            sendArrow = Img(frame, "SendArrow", S("arrow_off"), 429, 231, 7, 9);


            // ---- screen pages (content origin 87, 23; 275 x 209)
            var screen = NewRect(frame, "Screen"); Place(screen, 87, 23, 275, 209);
            BuildRoster(MakePage(screen, Page.Roster));
            BuildContract(MakePage(screen, Page.Contract));
            BuildTransmit(MakePage(screen, Page.Transmit));
            BuildLive(MakePage(screen, Page.Live));
            BuildLog(MakePage(screen, Page.Log));
            BuildJobs(MakePage(screen, Page.Jobs));

            // ---- CRT over the glass (81, 17; 287 x 221): breathing tint, rolling band, scanlines + rounded vignette
            tint = Img(frame, "Tint", null, 81, 17, 287, 221); tint.color = new Color(Ph.r / 255f, Ph.g / 255f, Ph.b / 255f, .01f);
            var bandMask = NewRect(frame, "BandMask"); Place(bandMask, 81, 17, 287, 221); bandMask.gameObject.AddComponent<RectMask2D>();
            band = Img(bandMask, "Band", S("band"), 0, 0, 287, 16);
            Img(frame, "Crt", S("crt"), 81, 17, 287, 221);
            // power-down: black shutters close over the glass to a phosphor line, then a dot (CRT off)
            shutTop = Fill(frame, "ShutTop", 81, 17, 287, 0, Dark); shutBottom = Fill(frame, "ShutBottom", 81, 238, 287, 0, Dark);
            shutLine = Fill(frame, "ShutLine", 81, 127, 287, 1, PhHi);
            foreach (Graphic g in new Graphic[] { shutTop, shutBottom, shutLine }) { g.raycastTarget = false; g.enabled = false; }
            foreach (Graphic g in new Graphic[] { tint, band }) g.raycastTarget = false;
            frame.Find("Crt").GetComponent<Image>().raycastTarget = false;
        }

        /// <summary>ROSTER, JOBS and HISTORY always lead somewhere; CONTRACT only for a droid that takes jobs.</summary>
        private bool NavEnabled(int i) => i != 1 || D.farm;

        private RectTransform MakePage(RectTransform screen, Page p)
        {
            var r = NewRect(screen, p.ToString()); Place(r, 0, 0, 275, 209);
            pages[p] = r;
            return r;
        }

        private void TitleTag(RectTransform p, string text, int w, out TextMeshProUGUI label)
        {
            w = Mathf.Max(w, 26 + text.Length * 6);   // the mark, the text and the frame
            Box(p, "TagL", "box_g", 0, 0, w, 19);
            Img(p, "Mark", S("mark"), 5, 3, 13, 13);
            label = Text(p, "TagText", text, 21, 5, w - 24, 10, Ph);
        }

        private TextMeshProUGUI RightTag(RectTransform p, string kind, string text, Color32 color)
        {
            int w = text.Length * 6 + 9;
            Box(p, "TagR", kind, 275 - w, 0, w, 19);
            return Text(p, "TagRText", text, 275 - w + 4, 5, w - 6, 10, color);
        }

        private void BuildRoster(RectTransform p)
        {
            rosterPage = p;
            TitleTag(p, "HIVE ROSTER", 92, out _);
            rosterUnit = RightTag(p, "box_g", "UNIT 1/4", Ph);
            rosterBp = Img(p, "Blueprint", S("bp_tiller"), 8, 23, 98, 98);
            var data = Box(p, "Data", "box_g", 129, 23, 146, 75);
            rosterData = Text(data, "Rows", "", 5, 5, 136, 66, Ph); rosterData.lineSpacing = 10;
            var stats = Box(p, "Stats", "box_g", 129, 102, 146, 53);
            string[] labels = { "TEND", "HAUL", "GUARD", "SPORES" };
            for (int i = 0; i < 4; i++)
            {
                Text(stats, "Stat" + i, labels[i], 5, 5 + i * 11, 42, 10, Ph);
                for (int k = 0; k < 5; k++) pips[i, k] = Img(stats, $"Pip{i}{k}", S("pip_off"), 50 + k * 10, 8 + i * 11, 7, 4);
            }
            var note = Box(p, "Note", "box_g", 0, 144, 111, 44);
            Text(note, "NoteTitle", "HIVE NOTE:", 5, 5, 100, 10, Ph);
            rosterNote = Text(note, "NoteText", "", 5, 16, 100, 22, Val); rosterNote.enableWordWrapping = true;
            Fill(p, "Wire1", 111, 165, 5, 1, PhMid);
            var tubeBox = Box(p, "Tube", "box_b", 116, 144, 14, 44);
            for (int i = 0; i < 6; i++) tube[i] = Img(tubeBox, "Seg" + i, S("seg_off"), 3, 36 - i * 6, 8, 5);
            Fill(p, "Wire2", 130, 173, 4, 1, PhMid);
            var pul = Box(p, "Pulse", "box_g", 134, 159, 141, 29);
            for (int i = 0; i < pulse.Length; i++) pulse[i] = Img(pul, "Bar" + i, null, 4 + i * 3, 4, 2, 2);
            foreach (Image b in pulse) b.color = Ph;
            Text(pul, "PulseLabel", "PULSE", 100, 3, 36, 10, PhDim, TextAlignmentOptions.TopRight);
            Key(p, "Prev", "box_g", "< PREV", 0, 193, 48, 16, Ph, () => Pick(-1));
            Key(p, "Next", "box_g", "NEXT >", 52, 193, 48, 16, Ph, () => Pick(1));
            Button hire = Key(p, "Hire", "btn_p", "CONTRACT TILLER", 275 - 104, 193, 104, 16, Dark, Hire);
            rosterHire = hire.GetComponentInChildren<TextMeshProUGUI>();
        }

        private void BuildContract(RectTransform p)
        {
            TitleTag(p, "HIRE HX-07", 104, out contractTag);
            quoteTag = RightTag(p, "box_a", "PRICE 10:00", Amb);

            // the job: a crop this kiosk's farm grows, and how many to deliver
            var job = Box(p, "Job", "box_g", 0, 23, 125, 98);
            Text(job, "CropLabel", "CROP:", 5, 5, 60, 10, Ph);
            Key(job, "CropPrev", "box_g", "<", 5, 17, 16, 16, Ph, () => StepCrop(-1));
            cropIcon = Img(job, "CropIcon", null, 24, 17, 16, 16); cropIcon.preserveAspect = true;
            cropName = Text(job, "CropName", "", 43, 20, 60, 10, Val);
            Key(job, "CropNext", "box_g", ">", 104, 17, 16, 16, Ph, () => StepCrop(1));
            cropRate = Text(job, "Rate", "", 5, 38, 115, 10, Ph);
            Text(job, "QtyLabel", "QUANTITY:", 5, 51, 70, 10, Ph);
            Key(job, "Less", "box_g", "-", 5, 63, 16, 16, Ph, () => StepQty(-1));
            qtyText = Text(job, "Qty", "10", 22, 66, 34, 10, Val, TextAlignmentOptions.Top);
            Key(job, "More", "box_g", "+", 57, 63, 16, 16, Ph, () => StepQty(1));
            Key(job, "Max", "box_g", "MAX", 77, 63, 43, 16, Ph, () => { qty = MaxQty; MergedUIAudio.Play(UISoundKind.Toggle); QtyChanged(); });
            seedsText = Text(job, "Seeds", "", 5, 83, 115, 10, PhDim);
            Fill(p, "Wire", 125, 70, 4, 1, PhMid);
            var q = Box(p, "Quote", "box_g", 129, 23, 146, 98);
            quoteText = Text(q, "Rows", "", 5, 5, 136, 88, Ph); quoteText.lineSpacing = 30;

            // the farm the droid works (the kiosk's own) and the order's terms
            var farm = Box(p, "Farm", "box_g", 0, 127, 275, 52);
            farmText = Text(farm, "Rows", "", 5, 5, 265, 30, Ph); farmText.lineSpacing = 30;
            shiftsNote = Text(farm, "Note", "", 5, 37, 265, 10, PhDim);
            contractStatus = Text(p, "Status", "", 0, 182, 275, 10, Val);
            requoteKey = Key(p, "Requote", "box_g", "REFRESH", 0, 193, 56, 16, Ph, () =>
            {
                MergedUIAudio.Play(UISoundKind.Toggle);
                if (Live) RefreshQuote(); else { quoteAt = Time.unscaledTime; Redraw(); }
            });
            Key(p, "JobsKey", "box_g", "JOBS", 60, 193, 40, 16, Ph, () => Go(Page.Jobs, true));
            sendKey = Key(p, "Send", "btn_p", "PAY 5.00 USDC", 275 - 152, 193, 152, 16, Dark, Send);
            sendLabel = sendKey.GetComponentInChildren<TextMeshProUGUI>();
        }

        private void BuildJobs(RectTransform p)
        {
            TitleTag(p, "JOBS", 50, out _);
            jobsPage = RightTag(p, "box_g", "1/9", Ph);
            jobsPageTag = p.Find("TagR").gameObject;
            for (int i = 0; i < JobRows; i++)
            {
                int y = 23 + i * 42;
                jobCards[i] = Box(p, "Job" + i, "box_g", 0, y, 275, 38).GetComponent<Image>();
                var c = (RectTransform)jobCards[i].transform;
                jobIcons[i] = Img(c, "Icon", null, 6, 11, 16, 16); jobIcons[i].preserveAspect = true;
                jobTitle[i] = Text(c, "Title", "", 28, 6, 140, 10, Ph);
                jobState[i] = Text(c, "State", "", 170, 6, 99, 10, Amb, TextAlignmentOptions.TopRight);
                for (int k = 0; k < JobSegs; k++) jobBar[i, k] = Fill(c, "Seg" + k, 28 + k * 8, 22, 7, 5, PhDeep);
                jobCount[i] = Text(c, "Count", "", 222, 20, 47, 10, Val, TextAlignmentOptions.TopRight);
            }
            jobsEmpty = Text(p, "Empty", "", 0, 95, 275, 10, PhDim, TextAlignmentOptions.Top);
            jobsPrev = Key(p, "Newer", "box_g", "<", 0, 193, 24, 16, Ph, () => PageJobs(-JobRows)).gameObject;
            jobsNext = Key(p, "Older", "box_g", ">", 28, 193, 24, 16, Ph, () => PageJobs(JobRows)).gameObject;
            var cancel = Key(p, "Cancel", "box_r", "CANCEL", 275 - 70, 193, 70, 16, Val, CancelJob);
            jobsCancel = cancel.gameObject;
            cancelLabel = cancel.GetComponentInChildren<TextMeshProUGUI>();
        }

        private void BuildTransmit(RectTransform p)
        {
            TitleTag(p, "PAYING", 86, out _);
            RightTag(p, "box_a", "PLEASE WAIT", Amb);
            node1Val = Node(p, 0, "YOU PAY", out _, "box_g");
            Fill(p, "Wire1", 77, 40, 22, 1, Ph);
            node2Val = Node(p, 99, "THE HIVE", out _, "box_g");
            Dashed(p, "Wire2", 176, 40, 22, Amb);
            node3Val = Node(p, 198, "HX-07", out node3Id, "box_d");
            var list = Box(p, "Steps", "box_g", 0, 90, 177, 72);
            transmitList = Text(list, "Rows", "", 5, 5, 167, 62, Ph); transmitList.lineSpacing = 20;
            Fill(p, "Wire3", 177, 123, 13, 1, PhMid);
            var tubeBox = Box(p, "Tube", "box_b", 190, 90, 14, 72);
            for (int i = 0; i < 10; i++) progress[i] = Img(tubeBox, "Seg" + i, S("seg_off"), 3, 63 - i * 6, 8, 5);
            transmitPct = Text(p, "Pct", "0%", 210, 90, 60, 10, Ph);
            transmitNote = Text(p, "Note", "", 210, 104, 65, 40, PhDim); transmitNote.lineSpacing = 30;
            Key(p, "Resend", "box_d", "RETRY", 0, 193, 60, 16, PhDim, Resend);
            Key(p, "Check", "box_g", "CHECK", 275 - 60, 193, 60, 16, Ph, () =>
            {
                MergedUIAudio.Play(UISoundKind.Click);
                if (stepsDone >= Steps.Length) Go(Page.Live, true);
                else if (Live && liveSteps >= 2 && !failed) Poll();
            });
        }

        private void BuildLive(RectTransform p)
        {
            TitleTag(p, "HIRED", 98, out _);
            Box(p, "Settled", "btn_p", 275 - 51, 0, 51, 19);
            Text(p, "SettledText", "PAID", 275 - 47, 5, 44, 10, Dark, TextAlignmentOptions.Top);
            liveBp = Img(p, "Blueprint", S("bp_tiller"), 8, 23, 98, 98);
            liveName = Text(p, "Name", "TILLER", 0, 127, 114, 16, Ph, TextAlignmentOptions.Top);
            liveName.font = thaleahFont; liveName.fontSize = 16;
            liveSub = Text(p, "Sub", "", 0, 147, 114, 22, PhDim, TextAlignmentOptions.Top); liveSub.lineSpacing = 20;
            Fill(p, "Wire", 106, 70, 23, 1, PhMid);
            var r = Box(p, "Receipt", "box_g", 129, 23, 146, 85);
            liveReceipt = Text(r, "Rows", "", 5, 5, 136, 75, Ph); liveReceipt.lineSpacing = 30;
            Key(p, "View", "box_g", "SEE JOBS", 0, 193, 80, 16, Ph, () => Go(Page.Jobs, true));
            Key(p, "Back", "btn_p", "DONE", 275 - 60, 193, 60, 16, Dark, () => Go(Page.Roster, true));
        }

        private void BuildLog(RectTransform p)
        {
            TitleTag(p, "HISTORY", 80, out _);
            var b = Box(p, "Table", "box_g", 0, 25, 275, 110);
            logText = Text(b, "Rows", "", 6, 6, 263, 98, Ph); logText.lineSpacing = 50;
        }

        private TextMeshProUGUI Node(RectTransform p, int x, string title, out TextMeshProUGUI head, string kind)
        {
            var n = Box(p, "Node" + x, kind, x, 25, 77, 31);
            head = Text(n, "Title", title, 5, 5, 67, 10, kind == "box_d" ? PhDim : Ph);
            return Text(n, "Value", "", 5, 16, 67, 10, kind == "box_d" ? PhDim : Val);
        }

        private void Dashed(RectTransform p, string name, int x, int y, int w, Color32 c)
        {
            for (int i = 0; i < w; i += 5) Fill(p, name + i, x + i, y, Mathf.Min(3, w - i), 1, c);
        }

        // ================================================================== flow

        /// <summary>Preview: the simulated send. Live: only while the wallet approval is open (tracking survives closing).</summary>
        private bool Busy => page == Page.Transmit && (Live ? sending : stepsDone < Steps.Length);
        /// <summary>Who receives the swap: the Hive escrow (bakeland.near); the player never needs a NEAR account.</summary>
        private string Recipient => Live ? hiveTreasury : "hive-treasury";
        private ulong Atomic => RateMicros * qty;
        private ulong payMicros;
        private Droid D => Droids[sel];
        /// <summary>The farm this kiosk works: the desert kiosk's desert farm, otherwise MainGame's farm (probes too).</summary>
        private string Site => siteId == HiveJobNetwork.SiteDesert ? HiveJobNetwork.SiteDesert : HiveJobNetwork.SiteFarm;
        private string FarmName => Site == HiveJobNetwork.SiteDesert ? "DESERT FARM" : "MAINGAME FARM";
        private CropOption Crop { get { var c = Crops(); return c.Count == 0 ? null : c[Mathf.Clamp(cropSel, 0, c.Count - 1)]; } }
        private ulong RateMicros => Crop?.micros ?? 0;
        private decimal Usdc => RateMicros * qty / 1_000_000m;
        private uint Seeds => Crop == null ? 0 : HiveJobNetwork.IsReady ? (uint)Mathf.CeilToInt(qty / (float)Mathf.Max(1, (int)Crop.perPlant)) : qty;
        private uint SeedsOwned => Crop == null ? 0 : HiveJobNetwork.BagCount(Crop.seed);
        private uint MaxQty
        {
            get
            {
                if (RateMicros == 0) return 1;
                ulong byPrice = (ulong)(MaxUsdcPerSignal * 1_000_000m) / RateMicros;
                uint max = (uint)Math.Min(MaxCrops, byPrice);
                if (HiveJobNetwork.IsReady) max = Math.Min(max, SeedsOwned * Math.Max(1u, Crop.perPlant));
                return Math.Max(1u, max);
            }
        }

        /// <summary>Server crops for this kiosk's farm (priced by the Hive), or the preview list without the Hive server.</summary>
        private List<CropOption> Crops()
        {
            if (!HiveJobNetwork.IsReady)
                return new List<CropOption>(Site == HiveJobNetwork.SiteDesert ? PreviewDesertCrops : PreviewFarmCrops);
            var list = new List<CropOption>();
            foreach (var d in HiveJobNetwork.CropsFor(Site))
            {
                var rate = HiveJobNetwork.RateFor(d.PlantIndex);
                list.Add(new CropOption { plant = d.PlantIndex, name = d.Name, seed = d.SeedItemId, harvest = d.HarvestItemId,
                    level = d.MinimumFarmingLevel, grow = d.StageStartsSeconds.Count > 0 ? d.StageStartsSeconds[d.StageStartsSeconds.Count - 1] : 0,
                    micros = rate.MicrosPerCrop, perPlant = Math.Max(1u, d.HarvestQuantity) });
            }
            return list;
        }

        private void StepCrop(int step)
        {
            int n = Crops().Count;
            if (n == 0) return;
            cropSel = (cropSel + step + n) % n;
            MergedUIAudio.Play(UISoundKind.Toggle);
            ClampQty();
            QtyChanged();
        }

        private void StepQty(int step)
        {
            long next = (long)qty + step * (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 10 : 1);
            qty = (uint)Mathf.Clamp((int)next, 1, (int)MaxQty);
            MergedUIAudio.Play(UISoundKind.Toggle);
            QtyChanged();
        }

        private void ClampQty() => qty = (uint)Mathf.Clamp((int)qty, 1, (int)MaxQty);

        private void QtyChanged()
        {
            Redraw();
            if (Live && page == Page.Contract) RefreshQuote();
        }

        private static string Usd(ulong micros) => (micros / 1_000_000m).ToString("0.000");

        private static string Duration(uint seconds)
        {
            if (seconds < 90) return "~1 MIN";
            if (seconds < 3600) return $"~{(seconds + 59) / 60} MIN";
            return $"~{seconds / 3600} H {(seconds % 3600 + 59) / 60} MIN";
        }

        private Sprite ItemIcon(int itemId)
        {
            var db = ItemDatabaseManager.Instance;
            return db != null ? db.Find(itemId)?.Icon : null;
        }

        private void Go(Page p, bool sound)
        {
            if (p != page && p != Page.Transmit && !working) liveError = null;
            bool entering = p == Page.Contract && page != Page.Contract;
            if (entering) quoteAt = Time.unscaledTime;
            page = p;
            foreach (var kv in pages) kv.Value.gameObject.SetActive(kv.Key == p);
            if (sound) MergedUIAudio.Play(UISoundKind.Tab);
            Redraw();
            if (entering && Live) RefreshQuote();
        }

        private void Pick(int step)
        {
            sel = (sel + step + Droids.Length) % Droids.Length;
            MergedUIAudio.Play(UISoundKind.Toggle);
            Redraw();
        }

        /// <summary>Only farmhands take crop jobs (the other droids' work comes later).</summary>
        private void Hire()
        {
            if (!D.farm) { MergedUIAudio.Play(UISoundKind.Deny); return; }
            MergedUIAudio.Play(UISoundKind.Click);
            Go(Page.Contract, false);
        }

        private void PageJobs(int step)
        {
            int n = SiteJobs().Count;
            int next = Mathf.Clamp(jobsFirst + step, 0, Mathf.Max(0, (n - 1) / JobRows * JobRows));
            MergedUIAudio.Play(next == jobsFirst ? UISoundKind.Deny : UISoundKind.Toggle);
            jobsFirst = next;
            Redraw();
        }

        /// <summary>Cancels the open job at this kiosk's farm: press twice within 3 s.</summary>
        private void CancelJob()
        {
            var open = HiveJobNetwork.OpenJobAt(Site);
            if (open == null) { MergedUIAudio.Play(UISoundKind.Deny); return; }
            if (Time.unscaledTime - cancelArmedAt > 3f) { cancelArmedAt = Time.unscaledTime; MergedUIAudio.Play(UISoundKind.Toggle); Redraw(); return; }
            cancelArmedAt = -10f;
            MergedUIAudio.Play(UISoundKind.Confirm);
            HiveJobNetwork.Cancel(open.JobId, reason => { jobNote = reason.ToUpperInvariant(); Redraw(); });
            Redraw();
        }

        private void Send()
        {
            if (page != Page.Contract) return;
            if (Crop == null || (HiveJobNetwork.IsReady && SeedsOwned < Seeds)) { MergedUIAudio.Play(UISoundKind.Deny); return; }
            if (Time.unscaledTime - quoteAt > QuoteSeconds) { MergedUIAudio.Play(UISoundKind.Deny); return; }
            if (Live) { SendLive(); return; }
            // Preview: the live steps are simulated on a timer and nothing is signed.
            MergedUIAudio.Play(UISoundKind.Confirm);
            transmitAt = Time.unscaledTime;
            stepsDone = 0;
            Go(Page.Transmit, false);
        }

        /// <summary>POWER: refused while a send is being approved; otherwise a short CRT power-down, then the panel closes.</summary>
        private void PowerOff()
        {
            if (powering) return;
            if (Busy) { MergedUIAudio.Play(UISoundKind.Deny); powerKey.sprite = S("power"); return; }
            powering = true; powerAt = Time.unscaledTime;
            powerKey.sprite = S("power_dn"); powerLed.sprite = S("led_rd");
            MergedUIAudio.Play(UISoundKind.Toggle);
        }

        /// <summary>12 fps steps: shutters close (0-3), the line shrinks to a dot (4-6), dark (7), then Hide.</summary>
        private void StepPower()
        {
            int step = Mathf.FloorToInt((Time.unscaledTime - powerAt) * 12f);
            shutTop.enabled = shutBottom.enabled = true;
            int half = 110;   // glass 221 tall: top shutter to row 127, bottom from 128
            int closed = Mathf.Min(half, (step + 1) * half / 4);
            Place(shutTop.rectTransform, 81, 17, 287, closed);
            Place(shutBottom.rectTransform, 81, 238 - closed, 287, closed);
            if (step >= 3 && step < 7)
            {
                shutLine.enabled = true;
                int w = step == 3 ? 287 : step == 4 ? 120 : step == 5 ? 24 : 3;
                Place(shutLine.rectTransform, 81 + (287 - w) / 2, 127, w, 1);
            }
            else shutLine.enabled = false;
            if (step < 8) return;
            powering = false;
            shutTop.enabled = shutBottom.enabled = shutLine.enabled = false;
            powerKey.sprite = S("power"); powerLed.sprite = S("led_g");
            Hide();
        }

        private void ShowNotice(string message)
        {
            var notices = NotificationsManager.Instance;
            if (notices != null) notices.ShowOK(message, () => { });
        }


        // ================================================================== live: NEAR Intents (NearIntentsClient)

        private static string Wrap(string text)
        {
            var sb = new System.Text.StringBuilder(); int line = 0;
            foreach (string w in text.Split(' '))
            {
                if (line > 0 && line + 1 + w.Length > 10) { sb.Append('\n'); line = 0; }
                else if (line > 0) { sb.Append(' '); line++; }
                sb.Append(w); line += w.Length;
            }
            return sb.ToString();
        }

        private static string Short(Exception e)
        {
            string m = (e?.Message ?? "ERROR").ToUpperInvariant().Replace('\n', ' ');
            return m.Length > 45 ? m.Substring(0, 44) + "." : m;
        }

        /// <summary>yoctoNEAR (24 decimals) to "0.00".</summary>
        private static string Near(string atomic)
        {
            if (string.IsNullOrEmpty(atomic) || !System.Numerics.BigInteger.TryParse(atomic, out var v)) return "?";
            var hundredths = v / System.Numerics.BigInteger.Pow(10, 22);
            return (hundredths / 100) + "." + ((int)(hundredths % 100)).ToString("00");
        }

        private static string ShortSig(string sig) => string.IsNullOrEmpty(sig) || sig.Length < 10 ? (sig ?? "-") : sig.Substring(0, 4).ToUpperInvariant() + ".." + sig.Substring(sig.Length - 4).ToUpperInvariant();

        /// <summary>The price is the job's own USDC amount (no swap quote since 15 Nov 2026): refreshing re-arms SEND.</summary>
        private void RefreshQuote()
        {
            if (!Live || sending) return;
            liveError = string.IsNullOrEmpty(hiveTreasury) ? "HIRING IS UNAVAILABLE RIGHT NOW" : null;
            quoteAt = Time.unscaledTime;
            Redraw();
        }

        private void SendLive()
        {
            if (sending || quoting) { MergedUIAudio.Play(UISoundKind.Deny); return; }
            if (string.IsNullOrEmpty(hiveTreasury)) { liveError = "HIRING IS UNAVAILABLE RIGHT NOW"; MergedUIAudio.Play(UISoundKind.Deny); Redraw(); return; }
            if (!HiveJobNetwork.IsReady) { liveError = "THE HIVE IS OFFLINE"; MergedUIAudio.Play(UISoundKind.Deny); Redraw(); return; }
            var open = HiveJobNetwork.OpenJobAt(Site);
            if (open != null && open.Status == HiveJobNetwork.Active) { liveError = "A DROID IS ALREADY WORKING HERE"; MergedUIAudio.Play(UISoundKind.Deny); Go(Page.Jobs, true); return; }
            if (open != null && open.PlantIndex == Crop.plant && open.Target == qty) { PayJob(open); return; }   // placed earlier, not paid yet
            if (open != null) { liveError = "CANCEL YOUR UNPAID JOB FIRST"; MergedUIAudio.Play(UISoundKind.Deny); Redraw(); return; }
            sending = true; liveError = null; Redraw();
            string clientJobId = "hive-" + NearIntentsClient.NewOperationId();
            HiveJobNetwork.Create(clientJobId, Site, D.id, Crop.plant, qty,
                job => { sending = false; if (this != null) PayJob(job); },
                reason => { sending = false; if (this == null) return; liveError = Short(new Exception(reason)); MergedUIAudio.Play(UISoundKind.Deny); Redraw(); });
        }

        /// <summary>Pays a placed order: one USDC transfer of the server's escrow amount from the player's wallet to the
        /// treasury, memo'd with the job's id. The API funds the job once Solana confirms it.</summary>
        private async void PayJob(SpacetimeDB.Types.HiveJob job)
        {
            if (sending) return;
            opId = job.ClientJobId; opKey = job.EscrowMicros + ">" + hiveTreasury; payMicros = job.EscrowMicros; paySignature = null;
            string id = opId;
            sending = true; failed = false; liveError = null; liveNote = "APPROVE IN\nYOUR WALLET"; liveSteps = 1; stepsDone = 0;
            transmitAt = Time.unscaledTime;
            MergedUIAudio.Play(UISoundKind.Confirm);
            Go(Page.Transmit, false);
            SavePending();
            try
            {
                var paid = await client.PayHiveJob(id, payMicros, hiveTreasury);
                if (this == null) return;
                if (id == opId) { liveSteps = 2; ApplyPayment(paid); }
            }
            catch (Exception e)
            {
                if (this == null) return;
                // Nothing was sent unless the wallet approved it; the order stays placed, so SEND pays it again.
                liveError = Short(e); failed = true; liveNote = null; ClearPending();
                MergedUIAudio.Play(UISoundKind.Deny);
            }
            sending = false;
            lastPoll = Time.unscaledTime;
        }

        /// <summary>Moves the tracker to what the API reports for the payment.</summary>
        private void ApplyPayment(NearIntentsClient.HivePayment p)
        {
            if (p == null) return;
            if (!string.IsNullOrEmpty(p.signature)) paySignature = p.signature;
            string st = (p.status ?? "").ToUpperInvariant();
            liveError = null;
            switch (st)
            {
                case "FUNDED":
                    liveSteps = Steps.Length; ClearPending();
                    log.Insert(0, Row(Clock(), D.id + " " + D.name, Usdc.ToString("0.00") + " USDC", "PAID"));
                    break;
                case "PENDING":
                    liveSteps = Mathf.Max(liveSteps, 3); liveNote = "USUALLY\nA FEW\nSECONDS";
                    break;
                case "REFUNDING":
                    failed = true; ClearPending(); liveError = "REFUNDED TO YOUR WALLET";
                    log.Insert(0, Row(Clock(), D.id + " " + D.name, Usdc.ToString("0.00") + " USDC", "REFUNDED"));
                    MergedUIAudio.Play(UISoundKind.Deny);
                    break;
                case "FAILED":
                case "EXPIRED":
                    // Nothing moved: the order is still placed, so SEND pays it again.
                    failed = true; ClearPending(); liveError = st == "EXPIRED" ? "PAYMENT DID NOT GO THROUGH. TRY AGAIN" : "PAYMENT FAILED";
                    MergedUIAudio.Play(UISoundKind.Deny);
                    break;
                case "UNPAID":
                    failed = true; ClearPending(); liveError = "NOT PAID YET";
                    break;
            }
        }

        private async void Poll()
        {
            if (polling || string.IsNullOrEmpty(opId)) return;
            polling = true; lastPoll = Time.unscaledTime;
            string id = opId;
            try
            {
                var s = await client.GetHivePayment(id);
                if (this == null) return;
                if (id == opId && s != null) ApplyPayment(s);
            }
            catch (Exception e)
            {
                if (this == null) return;
                liveNote = "STATUS\nUNREACHABLE"; Log.LogWarning("[HiveLink] status: " + e.Message);
            }
            polling = false;
        }

        /// <summary>RESEND: asks again where the payment stands (the API re-checks Solana and funds the job).</summary>
        private void Resend()
        {
            if (!Live || string.IsNullOrEmpty(opId) || liveSteps < 2 || liveSteps >= Steps.Length || failed || polling)
            {
                MergedUIAudio.Play(UISoundKind.Deny); return;
            }
            liveNote = "CHECKING\nTHE PAYMENT"; MergedUIAudio.Play(UISoundKind.Confirm);
            Poll();
        }

        private void SavePending()
        {
            try { PlayerPrefs.SetString(PendingKey, string.Join("|", opId, sel, Crop?.plant ?? 0, qty, Recipient, payMicros)); PlayerPrefs.Save(); } catch { }
        }

        private void ClearPending()
        {
            try { PlayerPrefs.DeleteKey(PendingKey); PlayerPrefs.Save(); } catch { }
        }

        /// <summary>After a reload: keep tracking the payment that was submitted.</summary>
        private bool ResumePending()
        {
            string saved = PlayerPrefs.GetString(PendingKey, "");
            string[] f = saved.Split('|');
            if (f.Length < 6 || !int.TryParse(f[1], out int d) || !uint.TryParse(f[2], out uint plant) || !uint.TryParse(f[3], out uint n)
                || !ulong.TryParse(f[5], out ulong micros)) return false;
            sel = Mathf.Clamp(d, 0, Droids.Length - 1); qty = Math.Max(1u, n);
            var crops = Crops(); int at = crops.FindIndex(c => c.plant == plant); if (at >= 0) cropSel = at;
            opId = f[0]; payMicros = micros; opKey = micros + ">" + f[4]; paySignature = null;
            liveSteps = 2; stepsDone = 0; failed = false; liveError = null; liveNote = "RESUMED\nTRACKING"; lastPoll = 0f;
            return true;
        }
        // ================================================================== state -> views

        private static string V(string text, Color32? c = null) => "<color=" + Hex(c ?? Val) + ">" + text + "</color>";
        private static string Clock() => DateTime.Now.ToString("HH:mm");
        private static string Row(string when, string what, string amount, string state) => when.PadRight(7) + what.PadRight(16) + amount.PadRight(11) + state;
        private static string ShortWallet()
        {
            string w = UserRegistration.username;
            return string.IsNullOrEmpty(w) ? "YOUR WALLET" : (w.Length > 10 ? w.Substring(0, 10) : w).ToUpperInvariant();
        }

        private void Redraw()
        {
            if (!built) return;
            Droid d = D;
            // right column
            bool online = Live && HiveJobNetwork.IsReady;
            readout.text = !Live ? "PREVIEW" : online ? "ONLINE" : "OFFLINE"; readout.color = online ? (Color)Ph : Live ? RedLed : (Color)Amb;
            dome.sprite = S(online ? "dome_g" : "dome_r");
            int nav = page == Page.Roster ? 0 : page == Page.Jobs ? 2 : page == Page.Log ? 3 : 1;
            for (int i = 0; i < 4; i++) { navRed[i].sprite = S(i == nav ? "led_rd" : "led_r"); navGreen[i].sprite = S(i == nav ? "led_g" : "led_gd"); }
            int lev = page == Page.Roster ? 1 : page == Page.Contract ? 2 : page == Page.Transmit ? 3 : 0;
            int[] ly = { 133, 143, 153, 162 };
            Place(leverKnob.rectTransform, 26, ly[lev] - 2, 11, 5);
            for (int i = 0; i < 4; i++) leverLabels[i].color = i == lev ? (Color)LeverOn : Ink;
            holo.sprite = S("holo_" + d.art);
            bool armed = page == Page.Contract && Time.unscaledTime - quoteAt <= QuoteSeconds;
            sendText.text = armed ? "SEND" : page == Page.Transmit && Busy ? "BUSY" : "SAFE";
            sendText.color = armed ? C(0xdcc066) : page == Page.Transmit ? Amb : C(0x6b4a50);
            sendArrow.sprite = S(armed ? "arrow_on" : "arrow_off");

            switch (page)
            {
                case Page.Roster:
                    rosterUnit.text = $"UNIT {sel + 1}/4";
                    rosterBp.sprite = S("bp_" + d.art);
                    rosterData.text = $"UNIT: {V(d.id + " " + d.name)}\nCLASS: {V(d.cls)}\nPAY: {V(d.farm ? "PER CROP" : "-")}\nWORKS: {V(d.farm ? FarmName : "-")}\nSTATUS: {V(d.status, d.farm ? Val : Amb)}";
                    rosterNote.text = d.note;
                    for (int i = 0; i < 4; i++) for (int k = 0; k < 5; k++) pips[i, k].sprite = S(k < d.pips[i] ? "pip_on" : "pip_off");
                    int rating = Mathf.RoundToInt((d.pips[0] + d.pips[1] + d.pips[2] + d.pips[3] - 4) / 16f * 6f);
                    for (int i = 0; i < 6; i++) tube[i].sprite = S(i < rating ? "seg_on" : "seg_off");
                    rosterHire.text = d.farm ? "HIRE " + d.name : "COMING SOON";
                    DrawCallouts(d);
                    break;
                case Page.Contract:
                {
                    contractTag.text = "HIRE " + d.name;
                    var crop = Crop;
                    if (crop != null) ClampQty();
                    cropIcon.sprite = crop != null ? ItemIcon(crop.harvest) : null; cropIcon.enabled = cropIcon.sprite != null;
                    string cname = crop != null ? crop.name.ToUpperInvariant() : "NO CROPS";
                    cropName.text = cname.Length > 10 ? cname.Substring(0, 9) + "." : cname;
                    uint level = HiveJobNetwork.FarmingLevel();
                    bool locked = crop != null && HiveJobNetwork.IsReady && level < crop.level;
                    cropRate.text = crop == null ? V("NO PRICES YET", Amb)
                        : locked ? V($"NEEDS FARMING LV{crop.level}", Amb) : $"{V(Usd(crop.micros))} USDC EACH";
                    qtyText.text = qty.ToString();
                    uint owned = SeedsOwned, need = Seeds;
                    bool shortSeeds = HiveJobNetwork.IsReady && owned < need;
                    seedsText.text = !HiveJobNetwork.IsReady ? "USES YOUR SEEDS" : $"SEEDS: {owned} OF {need}";
                    seedsText.color = shortSeeds ? (Color)Val : PhDim;
                    string pay = Usdc.ToString("0.00") + " USDC";
                    quoteText.text = $"ORDER: {V(qty + " " + (crop?.name ?? "-").ToUpperInvariant())}\nPRICE: {V(pay)}\n" +
                                     $"CROPS GO TO: {V("YOUR BAG")}\nUNFINISHED: {V("REFUNDED", Amb)}";
                    uint rounds = (need + PlotsPerDroid - 1) / PlotsPerDroid;
                    var open = HiveJobNetwork.OpenJobAt(Site);
                    farmText.text = $"FARM: {V(FarmName)}\nREADY IN: {V(crop == null ? "-" : Duration(rounds * crop.grow + 20))}";
                    shiftsNote.text = !Live ? "PREVIEW: NOTHING IS CHARGED"
                        : open != null ? (open.Status == HiveJobNetwork.Active ? "A DROID IS ALREADY WORKING HERE" : "YOU HAVE AN UNPAID ORDER: PAY TO START")
                        : !HiveJobNetwork.IsReady ? "THE HIVE IS OFFLINE" : "MAX 10 USDC PER ORDER";
                    shiftsNote.color = !Live || open != null || !HiveJobNetwork.IsReady ? (Color)Amb : PhDim;
                    contractStatus.text = liveError ?? (shortSeeds ? $"YOU NEED {need - owned} MORE SEEDS" : jobNote ?? "");
                    sendLabel.text = "PAY " + pay;
                    bool canSend = crop != null && !locked && !shortSeeds;
                    sendKey.GetComponent<Image>().sprite = S(canSend ? "btn_p" : "box_d");
                    sendLabel.color = canSend ? (Color)Dark : PhDim;
                    break;
                }
                case Page.Transmit:
                    node1Val.text = Usdc.ToString("0.00") + " USDC";
                    node2Val.text = stepsDone >= 4 ? "CONFIRMED" : "CONFIRMING";
                    node3Id.text = d.name;
                    break;
                case Page.Live:
                    liveBp.sprite = S("bp_" + d.art);
                    liveName.text = d.name;
                    liveSub.text = $"GROWS {qty} {(Crop?.name ?? "").ToUpperInvariant()}\nAT THE {FarmName}";
                    liveReceipt.text = $"PAID: {(Live ? V(Usdc.ToString("0.00") + " USDC") : V("PREVIEW", Amb))}\n" +
                                       $"ORDER: {V(qty + " " + (Crop?.name ?? "").ToUpperInvariant())}\nCROPS GO TO: {V("YOUR BAG")}\nTRACK IT: {V("JOBS", Amb)}";
                    break;
                case Page.Jobs:
                    DrawJobs();
                    break;
                case Page.Log:
                    var sb = new System.Text.StringBuilder("WHEN   WHAT            AMOUNT     STATE\n");
                    if (log.Count == 0) sb.Append("<color=" + Hex(PhDim) + ">NO HIRES YET.</color>");
                    for (int i = 0; i < Mathf.Min(6, log.Count); i++) sb.Append("<color=" + Hex(PhDim) + ">").Append(log[i]).Append("</color>\n");
                    logText.text = sb.ToString();
                    break;
            }
        }

        /// <summary>A crop's name for any farm (job cards list both kiosks' jobs).</summary>
        private static string CropName(uint plant)
        {
            var conn = SpacetimeManager.Conn;
            var crop = conn?.Db.FarmCrop.PlantIndex.Find(plant);
            var definition = crop != null ? conn.Db.FarmCropVersion.Key.Find($"{plant}:{crop.ActiveVersion}") : null;
            string name = definition?.Name;
            if (string.IsNullOrEmpty(name))
                foreach (var c in PreviewFarmCrops.Concat(PreviewDesertCrops)) if (c.plant == plant) name = c.name;
            return (name ?? "CROP " + plant).ToUpperInvariant();
        }

        /// <summary>This kiosk's jobs (its farm), open first.</summary>
        private List<SpacetimeDB.Types.HiveJob> SiteJobs() => HiveJobNetwork.Jobs().FindAll(j => j.Site == Site);

        private static string ShortStatus(SpacetimeDB.Types.HiveJob job) => job.Status switch
        {
            HiveJobNetwork.AwaitingFunding => "UNPAID",
            HiveJobNetwork.Active => "ACTIVE",
            HiveJobNetwork.Completed => "DONE",
            HiveJobNetwork.Cancelled => "CANCELLED",
            HiveJobNetwork.Expired => "ENDED",
            _ => "FAILED",
        };

        private void DrawJobs()
        {
            var all = SiteJobs();
            bool ready = HiveJobNetwork.IsReady || HiveJobNetwork.HasPreview;
            int pages = Mathf.Max(1, (all.Count + JobRows - 1) / JobRows);
            jobsFirst = Mathf.Clamp(jobsFirst, 0, (pages - 1) * JobRows);
            jobsPageTag.SetActive(pages > 1); jobsPage.gameObject.SetActive(pages > 1);
            jobsPage.text = $"{jobsFirst / JobRows + 1}/{pages}";
            jobsPrev.SetActive(pages > 1); jobsNext.SetActive(pages > 1);
            jobsEmpty.text = !ready ? "OFFLINE" : all.Count == 0 ? "NO JOBS" : "";
            for (int i = 0; i < JobRows; i++)
            {
                int at = jobsFirst + i;
                bool show = ready && at < all.Count;
                jobCards[i].gameObject.SetActive(show);
                if (!show) continue;
                var job = all[at];
                bool active = job.Status == HiveJobNetwork.Active, open = HiveJobNetwork.IsOpen(job);
                jobCards[i].sprite = S(active ? "box_a" : open ? "box_g" : "box_d");
                jobIcons[i].sprite = ItemIcon(job.HarvestItemId); jobIcons[i].enabled = jobIcons[i].sprite != null;
                jobTitle[i].text = $"{job.Target} {CropName(job.PlantIndex)}";
                jobTitle[i].color = open ? (Color)Ph : PhDim;
                jobState[i].text = ShortStatus(job);
                jobState[i].color = active || job.Status == HiveJobNetwork.AwaitingFunding ? (Color)Amb
                    : job.Status == HiveJobNetwork.Completed ? Ph : Val;
                int lit = job.Target == 0 ? 0 : Mathf.Clamp(Mathf.FloorToInt((float)JobSegs * job.Delivered / job.Target), 0, JobSegs);
                int grow = job.Target == 0 ? 0 : Mathf.CeilToInt((float)JobSegs * job.Growing / job.Target);
                for (int k = 0; k < JobSegs; k++)
                    jobBar[i, k].color = k < lit ? (Color)Ph : k < Mathf.Min(JobSegs, lit + grow) ? (Color)PhMid : PhDeep;
                jobCount[i].text = $"{job.Delivered}/{job.Target}";
                jobCount[i].color = open ? (Color)Val : PhDim;
            }
            var mine = HiveJobNetwork.OpenJobAt(Site);
            jobsCancel.SetActive(ready && mine != null);
            cancelLabel.text = Time.unscaledTime - cancelArmedAt <= 3f ? "CONFIRM" : "CANCEL";
        }

        private void Chip(Image img, TextMeshProUGUI label, bool on, bool off)
        {
            img.sprite = S(off ? "box_d" : on ? "btn_p" : "box_g");
            label.color = off ? (Color)PhDim : on ? (Color)Dark : Ph;
        }

        private void DrawCallouts(Droid d)
        {
            foreach (GameObject g in callouts) Destroy(g);
            callouts.Clear();
            // tags sit side by side under the blueprint (left to right by anchor); each leader drops from its target,
            // runs across at y 120 when the tag had to move, and drops into the tag's middle
            const int tagTop = 124, elbow = 120;
            var list = new List<(int x, int y, string label)>();
            foreach (var (ax, ay, label) in d.callouts)
                list.Add((8 + 1 + Mathf.RoundToInt((ax + .5f) * 6f) - 2, 23 + 1 + Mathf.RoundToInt((ay + .5f) * 6f) - 2, label));
            list.Sort((a, b) => a.x.CompareTo(b.x));
            int nextFree = 0;
            foreach (var (x, y, label) in list)
            {
                int w = label.Length * 6 + 9;
                int tx = Mathf.Clamp(x + 2 - w / 2, nextFree, 124 - w);
                nextFree = tx + w + 4;
                int cx = tx + w / 2;
                callouts.Add(Img(rosterPage, "Target", S("target"), x, y, 5, 5).gameObject);
                if (cx == x + 2) callouts.Add(Fill(rosterPage, "Lead", x + 2, y + 5, 1, tagTop - y - 5, RedLed).gameObject);
                else
                {
                    callouts.Add(Fill(rosterPage, "Lead", x + 2, y + 5, 1, elbow - y - 5, RedLed).gameObject);
                    callouts.Add(Fill(rosterPage, "LeadX", Mathf.Min(x + 2, cx), elbow, Mathf.Abs(cx - (x + 2)) + 1, 1, RedLed).gameObject);
                    callouts.Add(Fill(rosterPage, "LeadY", cx, elbow, 1, tagTop - elbow, RedLed).gameObject);
                }
                callouts.Add(Box(rosterPage, "Callout", "box_r", tx, tagTop, w, 16).gameObject);
                callouts.Add(Text(rosterPage, "CalloutText", label, tx + 5, tagTop + 3, w - 6, 10, Val).gameObject);
            }
        }

        // ================================================================== update

        private void Update()
        {
            if (releasePending) { releasePending = false; UIInputLock.Release(this); }
            if (!visible) return;
            ApplyScale(false);
            if (powering) { StepPower(); return; }
            float t = Time.unscaledTime;
            int tick = Mathf.FloorToInt(t * 12f);   // 12 fps art clock

            // CRT: breathing tint (4.5 s), soft band rolling down every 9 s
            tint.color = new Color(Ph.r / 255f, Ph.g / 255f, Ph.b / 255f, .004f + .018f * (.5f + .5f * Mathf.Sin(t * Mathf.PI * 2f / 4.5f)));
            int bandY = (tick % 108) * 2 - 16;
            band.enabled = bandY > -16 && bandY < 221;
            band.rectTransform.anchoredPosition = new Vector2(0, -bandY);

            if (page == Page.Roster)
                for (int i = 0; i < pulse.Length; i++)
                {
                    float v = Mathf.Abs(Mathf.Sin(i * .45f + sel + tick * .25f)) * .7f + Mathf.Abs(Mathf.Sin(i * 1.7f)) * .3f;
                    int h = 2 + Mathf.RoundToInt(v * 19f);
                    Place(pulse[i].rectTransform, 4 + i * 3, 23 - h, 2, h);
                }

            if (page == Page.Jobs && tick % 12 == 0 && tick != lastJobsTick) { lastJobsTick = tick; DrawJobs(); }

            if (page == Page.Contract)
            {
                float left = Mathf.Max(0f, QuoteSeconds - (t - quoteAt));
                quoteTag.text = left > 0 ? $"PRICE {(int)left / 60:00}:{(int)left % 60:00}" : "REFRESH";
                bool armed = left > 0;
                sendKey.interactable = armed;
                if (sendText.text != (armed ? "SEND" : "SAFE")) Redraw();
            }

            if (page == Page.Transmit)
            {
                if (Live && !sending && !polling && !failed && liveSteps >= 2 && liveSteps < Steps.Length && t - lastPoll > 4f) Poll();
                if (Live && liveSteps == Steps.Length - 1) liveSteps = Steps.Length;   // droid dispatch follows settlement
                int done = Live ? Mathf.Clamp(liveSteps, 0, Steps.Length) : Mathf.Clamp(Mathf.FloorToInt((t - transmitAt) / 1.2f), 0, Steps.Length);
                bool fresh = Live && (liveError ?? "") != lastTransmitError;
                if (done != stepsDone || transmitList.text.Length == 0 || fresh)
                {
                    stepsDone = done;
                    lastTransmitError = liveError ?? "";
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < Steps.Length; i++)
                    {
                        bool stuck = Live && liveError != null && i == done;
                        string tag = i < done ? (i == 2 ? "CONFIRMED" : "DONE") : stuck ? (failed ? "FAILED" : "STOPPED") : i == done ? "IN PROGRESS" : "WAITING";
                        Color32 c = i < done ? Val : stuck ? RedLed : i == done ? Amb : PhDim;
                        sb.Append(Steps[i]).Append(": ").Append(V(tag, c)).Append('\n');
                    }
                    transmitList.text = sb.ToString();
                    if (done >= Steps.Length)
                    {
                        log.Insert(0, Row(Clock(), D.id + " " + D.name, Usdc.ToString("0.00") + " USDC", Live ? "COMPLETE" : "PREVIEW"));
                        try { Contracted?.Invoke(siteId, D.id); } catch (Exception e) { Debug.LogException(e); }
                        jobNote = null;
                        MergedUIAudio.Play(UISoundKind.Confirm);
                        Go(Page.Live, false);
                        return;
                    }
                    Redraw();
                }
                float frac = Live ? (float)stepsDone / Steps.Length : Mathf.Clamp01((t - transmitAt) / (1.2f * Steps.Length));
                int lit = Mathf.RoundToInt(frac * 10f);
                for (int i = 0; i < 10; i++) progress[i].sprite = S(i < lit ? "seg_on" : "seg_off");
                transmitPct.text = Mathf.RoundToInt(frac * 100f) + "%";
                transmitNote.text = !Live ? "<color=" + Hex(Amb) + ">PREVIEW\nNOTHING\nCHARGED</color>"
                    : liveError != null ? "<color=" + Hex(Val) + ">" + Wrap(liveError) + "</color>" : liveNote ?? "USUALLY\nUNDER A\nMINUTE";
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (Busy) return;
                if (page != Page.Roster) Go(Page.Roster, true); else Hide();
            }
        }

        // ================================================================== builders (top-left art-pixel coordinates)

        private static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Place(RectTransform r, int x, int y, int w, int h)
        {
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f); r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h);
        }

        private static Image Img(Transform parent, string name, Sprite sprite, int x, int y, int w, int h)
        {
            var r = NewRect(parent, name); Place(r, x, y, w, h);
            var img = r.gameObject.AddComponent<Image>();
            img.sprite = sprite; img.raycastTarget = false;
            if (sprite == null) img.color = Color.white;
            return img;
        }

        private static Image Fill(Transform parent, string name, int x, int y, int w, int h, Color32 c)
        {
            var img = Img(parent, name, null, x, y, w, h); img.color = c; return img;
        }

        private RectTransform Box(Transform parent, string name, string kind, int x, int y, int w, int h)
        {
            var img = Img(parent, name, S(kind), x, y, w, h);
            img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f;
            return img.rectTransform;
        }

        private TextMeshProUGUI Text(Transform parent, string name, string text, int x, int y, int w, int h, Color32 color,
            TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
        {
            var r = NewRect(parent, name); Place(r, x, y, w, h);
            var t = r.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = vcrFont; t.fontSize = 10; t.color = color; t.alignment = align;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Overflow; t.richText = true;
            t.raycastTarget = false; t.margin = Vector4.zero;
            t.text = text;
            return t;
        }

        private Button Key(Transform parent, string name, string kind, string label, int x, int y, int w, int h, Color32 color, Action click)
        {
            var box = Box(parent, name, kind, x, y, w, h);
            var img = box.GetComponent<Image>(); img.raycastTarget = true;
            var b = box.gameObject.AddComponent<Button>(); b.transition = Selectable.Transition.None; b.targetGraphic = img;
            b.onClick.AddListener(() => click());
            Text(box, "Label", label, 0, 3, w, 10, color, TextAlignmentOptions.Top);
            return b;
        }

        private void Hit(Transform parent, string name, int x, int y, int w, int h, Action click)
        {
            var img = Img(parent, name, null, x, y, w, h);
            img.color = new Color(1, 1, 1, 0); img.raycastTarget = true;
            var b = img.gameObject.AddComponent<Button>(); b.transition = Selectable.Transition.None; b.targetGraphic = img;
            b.onClick.AddListener(() => { MergedUIAudio.Play(UISoundKind.Click); click(); });
        }
    }
}
