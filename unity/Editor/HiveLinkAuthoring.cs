using System;
using System.IO;
using System.Linq;
using System.Text;
using Bakeland.HiveLink;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Bakeland.EditorTools
{
    /// <summary>
    /// Builds the Hive Link terminal (world prefab) and the HIVE LINK screen (UI prefab). Menu: Bakeland/World/Hive Link/Build.
    /// Art: Assets/Environment/HiveLink (painted by Source~/paint_hivelink.py) and Assets/UI/HiveLink/Art (from the
    /// "Hive Link Terminal" design canvas). Builds in a preview scene (open scenes untouched); re-runnable. Log: Logs/hive-link.txt.
    ///
    ///   HiveLinkTerminal   root at the foot (plinth front edge, 248 x 186 canvas pivot (142.5, 176)), solid box under the kiosk
    ///     Kiosk            SortingGroup Default/0 + WorldPropDepthSort (behind order 0; world depth in the desert)
    ///       Ground Shadow  baked cast shadow, order -1
    ///       Body           kiosk, pole and plinth, order 0
    ///       Holo           kiosk screen: the selected droid, 16 frames at 8 fps, order 1
    ///     Board            the holo rate board, on the Front layer (orders 200/201) so tree canopies never cover it
    ///       Board Frame / Board Holo (NEAR hologlyph, rate rows, droid grid; 16 frames at 8 fps, in step with Holo)
    ///     Reach            trigger + Interact (outline root = Body) -> HiveLinkTerminal.Open, with the InteractIndicator
    ///
    /// Hive droid (14 Nov): Assets/Sprites/Characters/Droids/DroidTiller(.png, _Actions.png) in the B44 droid rig (sliced by
    /// DroidSheetImporter.RunNew, which also writes its SpriteLibrary), painted by Assets/Environment/HiveLink/Source~/paint_hive_droid.py.
    ///   HiveDroid          kinematic body, foot box, HiveDroidAgent
    ///     Visual           SortingGroup Default/0 + WorldPropDepthSort: Shadow (-1), Body (0, Sprite-Lit-Default like characters)
    ///     Agent Badge      HiveAgentBadge (Resources/World/HiveLink/HiveAgentBadge), Front layer order 210, unlit
    /// </summary>
    public static class HiveLinkAuthoring
    {
        private const string WorldArt = "Assets/Environment/HiveLink";
        private const string UiArt = "Assets/UI/HiveLink/Art";
        private const string TerminalPrefab = "Assets/Resources/World/HiveLink/HiveLinkTerminal.prefab";
        private const string PanelPrefab = "Assets/Resources/HiveLink/HiveLinkPanel.prefab";
        private const string IndicatorPath = "Assets/Prefabs/UI/InteractIndicator.prefab";
        private const string UnlitMaterial = "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Sprite-Unlit-Default.mat";
        private const string VcrFont = "Assets/UI/JupiterOS/Fonts/VCRMini.asset";
        private const string ThaleahFont = "Assets/Externals/Fonts/THALEAHFAT.asset";
        private const int W = 248, H = 186, Frames = 16;
        private const string DroidFolder = "Assets/Sprites/Characters/Droids";
        private const string DroidPrefab = "Assets/Resources/World/HiveLink/HiveDroid.prefab";
        private const string BadgePrefab = "Assets/Resources/World/HiveLink/HiveAgentBadge.prefab";
        private const string LitMaterialGuid = "9dfc825aed78fcd4ba02077103263b40";
        private static readonly Vector2 Pivot = new Vector2(142.5f / W, (H - 176f) / H);
        private static readonly string[] Sliced = { "hl_box_g", "hl_box_r", "hl_box_a", "hl_box_d", "hl_box_b", "hl_btn_p", "hl_chip_on" };

        [MenuItem("Bakeland/World/Hive Link/Build")]
        public static void Build()
        {
            var log = new StringBuilder();
            try { ImportArt(log); BuildTerminal(log); BuildPanel(log); BuildDroid(log); }
            catch (Exception e) { log.AppendLine("HIVE_LINK_FAILED " + e); Debug.LogException(e); }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/hive-link.txt", log.ToString());
            Debug.Log("[HiveLink] " + log);
        }

        // ------------------------------------------------------------------ import settings

        private static void ImportArt(StringBuilder log)
        {
            ImportWorld(WorldArt + "/HiveLinkTerminal.png", null);
            ImportWorld(WorldArt + "/HiveLinkTerminal_Ground.png", null);
            ImportWorld(WorldArt + "/HiveLinkTerminal_NoBase.png", null);
            ImportWorld(WorldArt + "/HiveLinkTerminal_NoBaseGround.png", null);
            var slices = new SpriteMetaData[Frames];
            for (int f = 0; f < Frames; f++)
                slices[f] = new SpriteMetaData
                {
                    name = "HiveLinkTerminal_Holo_" + f,
                    rect = new Rect((f % 4) * W, (3 - f / 4) * H, W, H),
                    alignment = (int)SpriteAlignment.Custom, pivot = Pivot,
                };
            ImportWorld(WorldArt + "/HiveLinkTerminal_Holo.png", slices);
            ImportWorld(WorldArt + "/HiveLinkTerminal_Board.png", null);
            var boardSlices = new SpriteMetaData[Frames];
            for (int f = 0; f < Frames; f++) { boardSlices[f] = slices[f]; boardSlices[f].name = "HiveLinkTerminal_BoardHolo_" + f; }
            ImportWorld(WorldArt + "/HiveLinkTerminal_BoardHolo.png", boardSlices);
            int ui = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { UiArt }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                if (ti == null) continue;
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.spritePixelsPerUnit = 100;
                ti.filterMode = FilterMode.Point;
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.alphaIsTransparency = true;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.spriteBorder = Sliced.Contains(Path.GetFileNameWithoutExtension(path)) ? new Vector4(3, 3, 3, 3) : Vector4.zero;
                var s = new TextureImporterSettings(); ti.ReadTextureSettings(s);
                s.spriteMeshType = SpriteMeshType.FullRect; s.spriteAlignment = (int)SpriteAlignment.Center;
                ti.SetTextureSettings(s);
                ti.SaveAndReimport();
                ui++;
            }
            log.AppendLine("ui art imported: " + ui);
        }

        private static void ImportWorld(string path, SpriteMetaData[] slices) => ImportWorld(path, slices, Pivot);

        private static void ImportWorld(string path, SpriteMetaData[] slices, Vector2 pivot)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            if (ti == null) throw new Exception("no texture at " + path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = slices != null ? SpriteImportMode.Multiple : SpriteImportMode.Single;
            ti.spritePixelsPerUnit = 16;
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.alphaIsTransparency = true;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = 2048;
            var s = new TextureImporterSettings(); ti.ReadTextureSettings(s);
            s.spriteMeshType = SpriteMeshType.FullRect;
            if (slices == null) { s.spriteAlignment = (int)SpriteAlignment.Custom; s.spritePivot = pivot; }
            ti.SetTextureSettings(s);
            if (slices != null) ti.spritesheet = slices;
            ti.SaveAndReimport();
        }

        // ------------------------------------------------------------------ world prefab

        private static void BuildTerminal(StringBuilder log)
        {
            Sprite body = AssetDatabase.LoadAssetAtPath<Sprite>(WorldArt + "/HiveLinkTerminal.png");
            Sprite ground = AssetDatabase.LoadAssetAtPath<Sprite>(WorldArt + "/HiveLinkTerminal_Ground.png");
            Sprite[] holo = Strip(WorldArt + "/HiveLinkTerminal_Holo.png");
            Sprite board = AssetDatabase.LoadAssetAtPath<Sprite>(WorldArt + "/HiveLinkTerminal_Board.png");
            Sprite[] boardHolo = Strip(WorldArt + "/HiveLinkTerminal_BoardHolo.png");
            if (body == null || ground == null || board == null || holo.Length != Frames || boardHolo.Length != Frames)
                throw new Exception($"terminal sprites missing: body {body != null}, ground {ground != null}, board {board != null}, holo {holo.Length}, board holo {boardHolo.Length}");
            Material unlit = AssetDatabase.LoadAssetAtPath<Material>(UnlitMaterial);
            Directory.CreateDirectory(Path.GetDirectoryName(TerminalPrefab));

            Scene preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("HiveLinkTerminal");
            SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                var solid = root.AddComponent<BoxCollider2D>(); solid.size = new Vector2(2.2f, .5f); solid.offset = new Vector2(0f, .4f);
                var term = root.AddComponent<HiveLinkTerminal>();

                // kiosk, pole and plinth: Y-sorted like every prop
                var kiosk = new GameObject("Kiosk"); kiosk.transform.SetParent(root.transform, false);
                var group = kiosk.AddComponent<SortingGroup>(); group.sortingLayerName = "Default"; group.sortingOrder = 0;
                var depth = kiosk.AddComponent<WorldPropDepthSort>(); depth.useWorldDepth = false; depth.behindOrder = 0;
                var groundR = Renderer(kiosk.transform, "Ground Shadow", ground, unlit, -1);
                var bodyR = Renderer(kiosk.transform, "Body", body, unlit, 0);
                var termSo = new SerializedObject(term);
                termSo.FindProperty("body").objectReferenceValue = bodyR;
                termSo.FindProperty("ground").objectReferenceValue = groundR;
                termSo.FindProperty("noBaseBody").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>(WorldArt + "/HiveLinkTerminal_NoBase.png");
                termSo.FindProperty("noBaseGround").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>(WorldArt + "/HiveLinkTerminal_NoBaseGround.png");
                termSo.ApplyModifiedPropertiesWithoutUndo();
                var holoR = Renderer(kiosk.transform, "Holo", holo[0], unlit, 1);
                holoR.gameObject.AddComponent<PixelSpriteAnimation>().Configure(holo, 8f, true, false);

                // the holo board floats high over the kiosk: drawn on Front so tree canopies (Front tiles) never cover it
                var boardGo = new GameObject("Board"); boardGo.transform.SetParent(root.transform, false);
                var boardR = Renderer(boardGo.transform, "Board Frame", board, unlit, 200); boardR.sortingLayerName = "Front";
                var boardHoloR = Renderer(boardGo.transform, "Board Holo", boardHolo[0], unlit, 201); boardHoloR.sortingLayerName = "Front";
                boardHoloR.gameObject.AddComponent<PixelSpriteAnimation>().Configure(boardHolo, 8f, true, false);

                var reach = new GameObject("Reach");
                reach.transform.SetParent(root.transform, false);
                var trigger = reach.AddComponent<BoxCollider2D>(); trigger.isTrigger = true;
                trigger.size = new Vector2(3.2f, 2.2f); trigger.offset = new Vector2(0f, .2f);
                var interact = reach.AddComponent<Interact>();
                var so = new SerializedObject(interact);
                so.FindProperty("questCheck").boolValue = false;
                so.FindProperty("outlineRoot").objectReferenceValue = bodyR.transform;
                so.ApplyModifiedPropertiesWithoutUndo();
                var field = typeof(Interact).GetField("interactEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var evt = (UnityEvent)field.GetValue(interact);
                if (evt == null) { evt = new UnityEvent(); field.SetValue(interact, evt); }
                for (int i = evt.GetPersistentEventCount() - 1; i >= 0; i--) UnityEventTools.RemovePersistentListener(evt, i);
                UnityEventTools.AddPersistentListener(evt, term.Open);

                var indicatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(IndicatorPath);
                if (indicatorPrefab != null)
                {
                    var indicator = (GameObject)PrefabUtility.InstantiatePrefab(indicatorPrefab, preview);
                    indicator.name = "InteractIndicator";
                    indicator.transform.SetParent(reach.transform, false);
                    indicator.transform.localPosition = new Vector3(0f, 5.3f, 0f);   // over the kiosk cap, under the board
                    if (indicator.GetComponent<WorldIndicatorBob>() == null) indicator.AddComponent<WorldIndicatorBob>();
                    foreach (SpriteRenderer s in indicator.GetComponentsInChildren<SpriteRenderer>(true)) { s.sortingLayerName = "Default"; s.sortingOrder = 15; }
                }
                else log.AppendLine("no interact indicator at " + IndicatorPath);

                PrefabUtility.SaveAsPrefabAsset(root, TerminalPrefab, out bool ok);
                log.AppendLine((ok ? "TERMINAL_BUILT " : "TERMINAL_FAILED ") + TerminalPrefab + $" holo {holo.Length} frames, pivot {body.pivot}");
            }
            finally
            {
                Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(preview);
            }
            AssetDatabase.SaveAssets();
        }

        private static Sprite[] Strip(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>()
            .OrderBy(s => int.Parse(s.name.Substring(s.name.LastIndexOf('_') + 1))).ToArray();

        private static SpriteRenderer Renderer(Transform parent, string name, Sprite sprite, Material material, int order)
        {
            var go = new GameObject(name, typeof(SpriteRenderer));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<SpriteRenderer>();
            r.sprite = sprite;
            if (material != null) r.sharedMaterial = material;
            r.sortingLayerName = "Default";
            r.sortingOrder = order;
            return r;
        }

        // ------------------------------------------------------------------ Hive droid + agent badge

        [MenuItem("Bakeland/World/Hive Link/Build Droid")]
        public static void BuildDroidMenu()
        {
            var log = new StringBuilder();
            try { BuildDroid(log); }
            catch (Exception e) { log.AppendLine("HIVE_DROID_FAILED " + e); Debug.LogException(e); }
            Directory.CreateDirectory("Logs");
            File.AppendAllText("Logs/hive-link.txt", log.ToString());
            Debug.Log("[HiveLink] " + log);
        }

        private static void BuildDroid(StringBuilder log)
        {
            Bakeland.CharacterCreation.DroidSheetImporter.RunNew();   // slices DroidTiller(+_Actions) once, writes its SpriteLibrary
            Sprite[] sheet = AssetDatabase.LoadAllAssetsAtPath(DroidFolder + "/DroidTiller.png").OfType<Sprite>()
                .OrderBy(x => int.Parse(x.name.Substring(x.name.LastIndexOf('_') + 1))).ToArray();
            Sprite[] actions = AssetDatabase.LoadAllAssetsAtPath(DroidFolder + "/DroidTiller_Actions.png").OfType<Sprite>()
                .OrderBy(x => int.Parse(x.name.Substring(x.name.LastIndexOf('_') + 1))).ToArray();
            ImportWorld(WorldArt + "/HiveDroid_Shadow.png", null, new Vector2(.5f, .5f));
            var badgeSlices = new SpriteMetaData[6];
            for (int f = 0; f < 6; f++)
                badgeSlices[f] = new SpriteMetaData { name = "HiveAgentBadge_" + f, rect = new Rect(f * 16, 0, 16, 16), alignment = (int)SpriteAlignment.BottomCenter, pivot = new Vector2(.5f, 0f) };
            ImportWorld(WorldArt + "/HiveAgentBadge.png", badgeSlices);
            Sprite shadow = AssetDatabase.LoadAssetAtPath<Sprite>(WorldArt + "/HiveDroid_Shadow.png");
            Sprite[] badgeFrames = Strip(WorldArt + "/HiveAgentBadge.png");
            if (sheet.Length != 44 || actions.Length != 16 || shadow == null || badgeFrames.Length != 6)
                throw new Exception($"droid sprites missing: sheet {sheet.Length}, actions {actions.Length}, shadow {shadow != null}, badge {badgeFrames.Length}");
            Material unlit = AssetDatabase.LoadAssetAtPath<Material>(UnlitMaterial);
            Material lit = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(LitMaterialGuid));

            // badge prefab (also used by HiveAgentBadge.Attach for any other agent)
            var b = new GameObject("HiveAgentBadge", typeof(SpriteRenderer));
            try
            {
                var br = b.GetComponent<SpriteRenderer>();
                br.sprite = badgeFrames[0]; br.sharedMaterial = unlit; br.sortingLayerName = "Front"; br.sortingOrder = 210;
                var badge = b.AddComponent<HiveAgentBadge>(); badge.frames = badgeFrames;
                PrefabUtility.SaveAsPrefabAsset(b, BadgePrefab, out bool okB);
                log.AppendLine((okB ? "BADGE_BUILT " : "BADGE_FAILED ") + BadgePrefab);
            }
            finally { Object.DestroyImmediate(b); }
            var badgePrefab = AssetDatabase.LoadAssetAtPath<HiveAgentBadge>(BadgePrefab);

            Scene preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("HiveDroid");
            SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                var rb = root.AddComponent<Rigidbody2D>(); rb.bodyType = RigidbodyType2D.Kinematic; rb.simulated = true;
                var foot = root.AddComponent<BoxCollider2D>(); foot.size = new Vector2(.9f, .35f); foot.offset = new Vector2(0f, .18f);
                var agent = root.AddComponent<HiveDroidAgent>();
                var visual = new GameObject("Visual"); visual.transform.SetParent(root.transform, false);
                var group = visual.AddComponent<SortingGroup>(); group.sortingLayerName = "Default"; group.sortingOrder = 0;
                var depth = visual.AddComponent<WorldPropDepthSort>(); depth.useWorldDepth = false; depth.behindOrder = 0;
                Renderer(visual.transform, "Shadow", shadow, lit, -1);
                var bodyR = Renderer(visual.transform, "Body", sheet[0], lit, 0);
                var badgeGo = (HiveAgentBadge)PrefabUtility.InstantiatePrefab(badgePrefab, preview);
                badgeGo.name = "Agent Badge";
                badgeGo.transform.SetParent(root.transform, false);
                badgeGo.transform.localPosition = new Vector3(0f, 1.55f, 0f);
                agent.sheet = sheet; agent.actions = actions; agent.body = bodyR; agent.badge = badgeGo;
                PrefabUtility.SaveAsPrefabAsset(root, DroidPrefab, out bool ok);
                log.AppendLine((ok ? "DROID_BUILT " : "DROID_FAILED ") + DroidPrefab + $" sheet {sheet.Length}, actions {actions.Length}, lit {(lit != null)}");
            }
            finally
            {
                Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(preview);
            }
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ UI prefab

        private static void BuildPanel(StringBuilder log)
        {
            Sprite[] art = AssetDatabase.FindAssets("t:Sprite", new[] { UiArt })
                .Select(g => AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GUIDToAssetPath(g))).Where(s => s != null).ToArray();
            var vcr = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(VcrFont);
            var thaleah = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(ThaleahFont);
            Directory.CreateDirectory(Path.GetDirectoryName(PanelPrefab));
            var go = new GameObject("HiveLinkPanel");
            try
            {
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 60; canvas.pixelPerfect = true; canvas.enabled = false;
                go.AddComponent<GraphicRaycaster>();
                var panel = go.AddComponent<HiveLinkPanel>();
                panel.art = art; panel.vcrFont = vcr; panel.thaleahFont = thaleah;
                PrefabUtility.SaveAsPrefabAsset(go, PanelPrefab, out bool ok);
                log.AppendLine((ok ? "PANEL_BUILT " : "PANEL_FAILED ") + PanelPrefab + $" art {art.Length}, vcr {(vcr != null)}, thaleah {(thaleah != null)}");
            }
            finally { Object.DestroyImmediate(go); }
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ probes (cmd.txt: static Bakeland.EditorTools.HiveLinkAuthoring X)

        private static void Out(string s)
        {
            Directory.CreateDirectory("Temp/MergedUI");
            File.AppendAllText("Temp/MergedUI/hivelink.txt", DateTime.Now.ToString("HH:mm:ss") + " " + s + "\n");
        }
        public static void Status()
        {
            var t = Object.FindObjectsOfType<HiveLinkTerminal>(true);
            Out($"playing={Application.isPlaying} terminals={t.Length} " + string.Join(", ", t.Select(x => x.SiteId + "@" + x.transform.position)) + " | " + HiveLinkPanel.Describe());
        }
        public static void Roster() { HiveLinkPanel.Preview("roster"); Status(); }
        public static void Contract() { HiveLinkPanel.Preview("contract", 0); Status(); }
        public static void Transmit() { HiveLinkPanel.Preview("transmit", 0); Status(); }
        public static void LivePage() { HiveLinkPanel.Preview("live", 0); Status(); }
        public static void Close() { HiveLinkPanel.CloseIfOpen(); Status(); }
        public static void Power() { HiveLinkPanel.PressPower(); Status(); }
        public static void Jobs() { HiveLinkPanel.Preview("jobs"); Status(); }
        /// <summary>JOBS with three sample orders (active, unpaid, delivered) while HiveJobs.cs isn't published.</summary>
        public static void JobsSample()
        {
            SpacetimeDB.Types.HiveJob Row(ulong id, string site, uint plant, int harvest, uint target, uint delivered, uint growing, byte status,
                ulong rate, string note) => new SpacetimeDB.Types.HiveJob
            {
                JobId = id, ClientJobId = "hive-sample-" + id, Site = site, DroidId = "HX-07", PlantIndex = plant, HarvestItemId = harvest,
                Target = target, Delivered = delivered, Growing = growing, Status = status, MicrosPerCrop = rate, EscrowMicros = rate * target,
                PaidOutMicros = rate * delivered, RefundMicros = 0UL, Note = note,
            };
            HiveJobNetwork.SetPreviewJobs(new[]
            {
                Row(3, "farm", 31, 32, 12, 7, 5, 1, 39_000, "TENDING 5 PLOTS"),
                Row(2, "desert-village-edge", 76, 77, 10, 0, 0, 0, 35_000, "AWAITING PAYMENT"),
                Row(1, "farm", 28, 34, 8, 8, 0, 2, 105_000, "DELIVERED"),
                Row(4, "desert-village-edge", 60, 61, 20, 12, 6, 1, 54_000, "TENDING 6 PLOTS"),
                Row(5, "desert-village-edge", 54, 55, 6, 6, 0, 2, 175_000, "DELIVERED"),
                Row(6, "desert-village-edge", 74, 75, 12, 3, 0, 3, 70_000, "CANCELLED"),
            });
            HiveLinkPanel.Preview("jobs");
            Status();
        }
        /// <summary>CONTRACT with the next crop and 25 of it (the picker and the quantity steppers).</summary>
        public static void NextCrop() { HiveLinkPanel.PreviewOrder(1, 25); Status(); }
        public static void Interact()
        {
            var t = Object.FindObjectOfType<HiveLinkTerminal>();
            if (t == null) { Out("no terminal"); return; }
            t.Open(); Status();
        }
        /// <summary>Play mode: travel straight to the desert village (no confirmation dialog).</summary>
        public static void TravelDesert() { WorldSceneTransitionService.Load("SpriteFusionDesert", WorldSceneContext.SkiffSpawnPrefix + "Village", .2f); Out("travelling to the desert"); }
        public static void TravelFarm() { WorldSceneTransitionService.Load("MainGame", WorldSceneContext.TagSpawnPrefix + "Farm", .2f); Out("travelling to the farm"); }

        /// <summary>Play mode: every bus stop in the loaded scenes (station, position, exit).</summary>
        public static void Buses()
        {
            foreach (var b in Object.FindObjectsOfType<Bus>(true))
            {
                var col = b.GetComponent<Collider2D>();
                Out($"bus {b.Station} '{b.DisplayName}' at {b.transform.position} active={b.isActiveAndEnabled} bounds={(col != null ? col.bounds.ToString() : "-")}");
            }
            foreach (var d in Object.FindObjectsOfType<HiveDroidAgent>(true)) Out($"droid {d.droidId} site={d.siteId} at {d.transform.position} gig={d.OnGig}");
        }
        /// <summary>Play mode: puts every Hive droid on a gig (badge check).</summary>
        public static void GigOn() { foreach (var d in Object.FindObjectsOfType<HiveDroidAgent>()) d.SetGig(true); Buses(); }
        public static void GigOff() { foreach (var d in Object.FindObjectsOfType<HiveDroidAgent>()) d.SetGig(false); Buses(); }
        /// <summary>Play mode: camera to the point written in Temp/MergedUI/hivelink-at.txt ("x y [size]").</summary>
        public static void FrameAt()
        {
            string[] f = File.ReadAllText("Temp/MergedUI/hivelink-at.txt").Trim().Split(' ');
            Camera cam = Camera.main;
            foreach (Behaviour b in cam.GetComponents<Behaviour>()) if (b != cam && b.GetType().Name.Contains("Cinemachine")) b.enabled = false;
            cam.transform.position = new Vector3(float.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), cam.transform.position.z);
            if (f.Length > 2) cam.orthographicSize = float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
            Out("camera " + cam.transform.position + " ortho " + cam.orthographicSize);
        }
        /// <summary>Play mode: moves the player to the camera's centre minus 3 units in y (to stand by a terminal).</summary>
        public static void PlayerToCamera()
        {
            var p = GameObject.FindGameObjectWithTag("Player"); Camera cam = Camera.main;
            if (p == null || cam == null) { Out("no player"); return; }
            p.transform.position = new Vector3(cam.transform.position.x, cam.transform.position.y - 3f, p.transform.position.z);
            Out("player " + p.transform.position);
        }

        /// <summary>Frames the main camera on the first terminal (play mode).</summary>
        public static void Frame()
        {
            var t = Object.FindObjectOfType<HiveLinkTerminal>();
            Camera cam = Camera.main;
            if (t == null || cam == null) { Out("no terminal or camera"); return; }
            foreach (Behaviour b in cam.GetComponents<Behaviour>()) if (b != cam && b.GetType().Name.Contains("Cinemachine")) b.enabled = false;
            cam.transform.position = new Vector3(t.transform.position.x, t.transform.position.y + 4f, cam.transform.position.z);
            Out("framed " + t.SiteId + " cam " + cam.transform.position + " ortho " + cam.orthographicSize);
        }
    }
}
