using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// Places the Hive Link terminals when their scene loads (no scene edit, like the Stash Points). The prefab is built
    /// for MainGame (Sprite-Unlit-Default, sorted around the player, behind order 0); in the desert each terminal switches
    /// to world depth and to the desert tiles' material. Positions are the terminal's foot (the plinth's front edge).
    /// </summary>
    public static class HiveLinkSites
    {
        public const string PrefabPath = "World/HiveLink/HiveLinkTerminal";
        public const string RootName = "Hive Link (runtime)";
        public const string DroidPath = "World/HiveLink/HiveDroid";
        private const string DesertScene = "SpriteFusionDesert";

        public readonly struct Site
        {
            public readonly string Id, Scene;
            public readonly Vector2 Foot, Droid;
            public readonly bool NoBase;
            /// <param name="dx">the Hive droid's foot relative to the terminal's (it stands beside the kiosk)</param>
            /// <param name="noBase">drop the plinth (HiveLinkTerminal.RemoveBase)</param>
            public Site(string id, string scene, float x, float y, float dx, float dy, bool noBase = false)
            { Id = id; Scene = scene; Foot = new Vector2(x, y); Droid = new Vector2(dx, dy); NoBase = noBase; }
        }

        public static readonly Site[] Sites =
        {
            new Site("farm", "MainGame", FarmX, FarmY, FarmDroidX, -.4f, true),
            new Site("desert-village-edge", DesertScene, DesertX, DesertY, 1.6f, -.4f),
        };
        public const float KioskScale = 0.7f;
        public const float FarmX = -113.84f, FarmY = 51.73f, FarmDroidX = 1.7f;   // beside the Farm bus stop (-106, 51); placed by hand 14 Nov, no plinth
        public const float DesertX = -10f, DesertY = 318f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) OnSceneLoaded(SceneManager.GetSceneAt(i), LoadSceneMode.Additive);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            bool any = false;
            foreach (Site s in Sites) any |= s.Scene == scene.name;
            if (!any) return;
            foreach (GameObject existing in scene.GetRootGameObjects())
                if (existing.name == RootName) return;
            var prefab = Resources.Load<GameObject>(PrefabPath);
            if (prefab == null) { Log.LogWarning("[HiveLink] prefab missing at Resources/" + PrefabPath + " (Bakeland/World/Hive Link/Build)"); return; }

            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            bool desert = scene.name == DesertScene;
            Material tiles = desert ? FindTileMaterial(scene) : null;
            var droidPrefab = Resources.Load<HiveDroidAgent>(DroidPath);
            if (droidPrefab == null) Log.LogWarning("[HiveLink] droid prefab missing at Resources/" + DroidPath + " (Bakeland/World/Hive Link/Build Droid)");
            foreach (Site s in Sites)
            {
                if (s.Scene != scene.name) continue;
                GameObject t = Object.Instantiate(prefab, new Vector3(s.Foot.x, s.Foot.y, 0f), Quaternion.identity, root.transform);
                // 70 % of the painted size to sit in proportion with the farm and village props. Scaled about the foot
                // (the root sits on it), so the kiosk keeps its spot; the interact prompt keeps its usual size.
                t.transform.localScale = new Vector3(KioskScale, KioskScale, 1f);
                foreach (Transform child in t.GetComponentsInChildren<Transform>(true))
                    if (child.name == "InteractIndicator") child.localScale = new Vector3(child.localScale.x / KioskScale, child.localScale.y / KioskScale, 1f);
                t.name = "Hive Link (" + s.Id + ")";
                if (t.TryGetComponent(out HiveLinkTerminal term)) { term.SiteId = s.Id; if (s.NoBase) term.RemoveBase(); }
                if (droidPrefab != null)
                {
                    HiveDroidAgent d = Object.Instantiate(droidPrefab, new Vector3(s.Foot.x + s.Droid.x, s.Foot.y + s.Droid.y, 0f), Quaternion.identity, root.transform);
                    d.name = "Hive Droid (" + s.Id + ")";
                    d.siteId = s.Id;
                    if (desert) { var dd = d.GetComponentInChildren<WorldPropDepthSort>(true); if (dd != null) dd.useWorldDepth = true; }
                }
                if (!desert) continue;
                WorldPropDepthSort depth = t.GetComponentInChildren<WorldPropDepthSort>(true); if (depth != null) depth.useWorldDepth = true;
                if (tiles == null) continue;
                foreach (SpriteRenderer r in t.GetComponentsInChildren<SpriteRenderer>(true))
                    if (!IsIndicator(r.transform)) r.sharedMaterial = tiles;
            }
        }

        private static bool IsIndicator(Transform t)
        {
            for (; t != null; t = t.parent) if (t.name == "InteractIndicator") return true;
            return false;
        }

        private static Material FindTileMaterial(Scene scene)
        {
            foreach (GameObject go in scene.GetRootGameObjects())
                foreach (TilemapRenderer r in go.GetComponentsInChildren<TilemapRenderer>(true))
                    if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("DesertTiles")) return r.sharedMaterial;
            return null;
        }
    }
}
