using UnityEngine;

namespace Bakeland.HiveLink
{
    /// <summary>
    /// A Hive Link terminal in the world (14 Nov 2026): the kiosk with the holo rate board above it. Placed at runtime by
    /// <see cref="HiveLinkSites"/>; the prefab (Resources/World/HiveLink/HiveLinkTerminal) is built by
    /// Bakeland/World/Hive Link/Build (HiveLinkAuthoring). Interacting opens the HIVE LINK screen (<see cref="HiveLinkPanel"/>).
    /// </summary>
    public sealed class HiveLinkTerminal : MonoBehaviour
    {
        [SerializeField, Tooltip("Which terminal this is (HiveLinkSites id).")]
        private string siteId;

        public string SiteId { get => siteId; set => siteId = value; }

        [Header("No-base variant (set by HiveLinkAuthoring)")]
        [SerializeField] private SpriteRenderer body;
        [SerializeField] private SpriteRenderer ground;
        [SerializeField] private Sprite noBaseBody, noBaseGround;

        /// <summary>Drops the plinth (the MainGame kiosk stands straight on the path): same canvas and pivot, so the
        /// machine doesn't move; only the base and its shadow change.</summary>
        public void RemoveBase()
        {
            if (body != null && noBaseBody != null) body.sprite = noBaseBody;
            if (ground != null && noBaseGround != null) ground.sprite = noBaseGround;
        }

        /// <summary>Wired to the terminal's Interact event.</summary>
        public void Open()
        {
            if (HiveLinkPanel.Open(siteId)) return;
            var notices = NotificationsManager.Instance;
            if (notices != null) notices.ShowOK("HIVE LINK\n\nThis terminal is offline. Try again soon.", () => { });
        }
    }
}
