using LuckyTowerCoop.Net;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>Labels above the characters: online - "You" and the partner's name, local - "1" and "2" (the characters look identical, easy to mix up).</summary>
    public class CoopLabels : MonoBehaviour
    {
        private GUIStyle m_style;
        private int m_styleHeight;

        private static readonly Color s_own = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color s_partner = new Color(0.55f, 0.85f, 1f, 0.9f);

        private void OnGUI()
        {
            if (!CoopConfig.ShowNames.Value || Event.current.type != EventType.Repaint)
            {
                return;
            }
            var p1 = CoopPlayers.P1;
            var p2 = CoopPlayers.P2;
            if (p1 == null || p2 == null)
            {
                return; // single character - nothing to label
            }
            var cam = CoopCamera.CameraManagerSilent;
            if (cam == null || cam.MainCamera == null)
            {
                return;
            }
            if (m_style == null || m_styleHeight != Screen.height)
            {
                m_styleHeight = Screen.height;
                m_style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height / 48f)), fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter };
            }
            bool online = NetSession.Role != NetRole.None;
            string partner = online && NetSession.Instance != null && !string.IsNullOrEmpty(NetSession.Instance.PeerName) ? NetSession.Instance.PeerName : "Partner";
            Draw(cam.MainCamera, p1, online ? "You" : "1", s_own);
            Draw(cam.MainCamera, p2, online ? partner : "2", s_partner);
        }

        private void Draw(Camera cam, GamePlayer p, string text, Color color)
        {
            bool hidden = CoopVisibility.IsHidden(p);
            if (p.Stats.Dead || !p.gameObject.activeInHierarchy || hidden || CoopTransitions.IsWaiting(p))
            {
                return;
            }
            var col = p.Components != null ? p.Components.Collider : null;
            var head = col != null && col.enabled ? new Vector3(col.bounds.center.x, col.bounds.max.y + 0.35f, 0f) : p.transform.position + Vector3.up * 2.4f;
            var sp = cam.WorldToScreenPoint(head);
            if (sp.z < 0f)
            {
                return;
            }
            var rect = new Rect(sp.x - 120f, Screen.height - sp.y - 40f, 240f, 40f);
            m_style.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, m_style);
            m_style.normal.textColor = color;
            GUI.Label(rect, text, m_style);
        }
    }
}
