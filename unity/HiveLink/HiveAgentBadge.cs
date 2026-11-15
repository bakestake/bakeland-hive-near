using UnityEngine;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// The badge over an agent NPC (a Hive worker droid, or any other bot that takes gigs): a small holo plate with a
    /// droid-head glyph, so players can tell bots from people at a glance.
    ///   AVAILABLE (phosphor rim): frames 0-1, the glyph blinks now and then.
    ///   ON A GIG (amber rim, a cog turning on the corner, three work pips running): frames 2-5 at 6 fps.
    /// 16 x 16 art at 16 PPU, pivot bottom centre; it bobs a whole art pixel at a time (2 s period) and draws on the
    /// Front layer so canopies never hide it. Art: Assets/Environment/HiveLink/HiveAgentBadge.png (paint_hive_droid.py).
    /// Add one to any agent with <see cref="Attach"/>.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public sealed class HiveAgentBadge : MonoBehaviour
    {
        public const string PrefabPath = "World/HiveLink/HiveAgentBadge";
        public Sprite[] frames;
        [SerializeField] private bool onGig;
        [Tooltip("Local height of the badge's foot over the agent's foot.")] public float height = 1.55f;

        private SpriteRenderer sr;
        private float blinkAt;

        public bool OnGig { get => onGig; set => onGig = value; }

        /// <summary>Adds the badge to an agent (loaded from Resources/World/HiveLink/HiveAgentBadge).</summary>
        public static HiveAgentBadge Attach(Transform agent, float height, bool gig = false)
        {
            var existing = agent.GetComponentInChildren<HiveAgentBadge>(true);
            if (existing != null) { existing.height = height; existing.onGig = gig; return existing; }
            var prefab = Resources.Load<HiveAgentBadge>(PrefabPath);
            if (prefab == null) { Log.LogWarning("[HiveLink] badge prefab missing at Resources/" + PrefabPath); return null; }
            var badge = Instantiate(prefab, agent);
            badge.name = "Agent Badge";
            badge.height = height; badge.onGig = gig;
            return badge;
        }

        private void Awake()
        {
            sr = GetComponent<SpriteRenderer>();
            blinkAt = Time.time + Random.Range(1.5f, 4f);
        }

        private void LateUpdate()
        {
            if (frames == null || frames.Length < 6) return;
            float t = Time.time;
            int frame;
            if (onGig) frame = 2 + Mathf.FloorToInt(t * 6f) % 4;
            else
            {
                if (t > blinkAt + .15f) blinkAt = t + Random.Range(2.5f, 5f);
                frame = t >= blinkAt && t < blinkAt + .15f ? 1 : 0;
            }
            if (sr.sprite != frames[frame]) sr.sprite = frames[frame];
            int bob = (Mathf.FloorToInt(t * 2f) % 2 == 0) ? 0 : 1;   // whole art pixels
            transform.localPosition = new Vector3(0f, height + bob / 16f, 0f);
        }
    }
}
