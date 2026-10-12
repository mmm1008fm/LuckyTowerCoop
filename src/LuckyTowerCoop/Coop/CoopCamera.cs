using HarmonyLib;
using LuckyTower;
using UnityEngine;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Камера на двоих (один экран): основная виртуальная камера следит за точкой между игроками
    /// (игра везде ставит Follow = фокус P1 — подменяем, если Follow указывает на него).
    /// Если P2 долго за кадром — телепортируем его к P1. Онлайн: камера на своём персонаже (погиб — на напарнике).
    /// </summary>
    public class CoopCamera : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<CameraManager> s_instance =
            AccessTools.StaticFieldRefAccess<CameraManager>(AccessTools.Field(typeof(CameraManager), "m_instance"));

        /// <summary>Дальше этого расстояния камера держит только P1 (игроки в разных частях уровня).</summary>
        private const float MaxGroupDistance = 18f;

        private Transform m_target;
        private float m_offscreenSince = -1f;

        public static CameraManager CameraManagerSilent => s_instance();

        public static Transform Target { get; private set; }

        private void LateUpdate()
        {
            var cam = CameraManagerSilent;
            var p1 = CoopPlayers.P1;
            if (cam == null || p1 == null || cam.VirtualCamera == null)
            {
                return;
            }
            var p1Focus = p1.Components != null ? p1.Components.CameraFocusPoint : null; // у тела фокуса может уже не быть
            EnsureTarget();

            var p2 = CoopPlayers.P2;
            Vector3 desired;
            if (Net.NetSession.Role != Net.NetRole.None)
            {
                // онлайн: у каждого свой экран — камера на своём персонаже; погиб или ждёт в двери — смотрим за напарником
                var own = Net.NetSession.IsClient ? p1 : CoopPlayers.CharOfSeat(Seat.A);
                var partner = Net.NetSession.IsClient ? p2 : CoopPlayers.CharOfSeat(Seat.B);
                var follow = Usable(own) ? own : Usable(partner) ? partner : null;
                var f = follow != null ? follow.Components.CameraFocusPoint : p1Focus;
                if (f == null)
                {
                    return;
                }
                desired = f.position;
            }
            else
            {
                if (p1Focus == null)
                {
                    return;
                }
                desired = p1Focus.position;
                bool group = CoopPlayers.IsP2ActiveInWorld && p2.Components.CameraFocusPoint != null &&
                             Vector2.Distance(p1Focus.position, p2.Components.CameraFocusPoint.position) < MaxGroupDistance;
                if (group && CoopTransitions.IsWaiting(p1))
                {
                    desired = p2.Components.CameraFocusPoint.position; // P1 ждёт в двери — показываем P2
                }
                else if (group)
                {
                    desired = (p1Focus.position + p2.Components.CameraFocusPoint.position) * 0.5f;
                }
            }
            m_target.position = desired;

            var vcam = cam.VirtualCamera;
            if ((p1Focus != null && vcam.Follow == p1Focus) || (p2 != null && p2.Components != null && vcam.Follow == p2.Components.CameraFocusPoint))
            {
                vcam.Follow = m_target;
            }
            else if (Net.NetSession.Role != Net.NetRole.None && vcam.Follow != m_target
                     && (vcam.Follow == null || vcam.Follow.GetComponentInParent<LuckyTower.CharacterSystem.Player>() != null))
            {
                vcam.Follow = m_target; // онлайн: после смерти игра могла прицепить камеру к телу — ведём свою цель
            }

            UpdateLeash(cam, p1);
        }

        private void UpdateLeash(CameraManager cam, LuckyTower.CharacterSystem.Player p1)
        {
            // в онлайне у каждого свой экран — подтягивать отставшего незачем
            float leash = Net.NetSession.Role != Net.NetRole.None ? 0f : CoopConfig.LeashSeconds.Value;
            var p2 = CoopPlayers.P2;
            var gm = GameManager.InstanceNoWarnings;
            if (leash <= 0f || !CoopPlayers.IsP2ActiveInWorld || cam.MainCamera == null || gm == null || gm.InEnterDoor || p1.Stats.Dead)
            {
                m_offscreenSince = -1f;
                return;
            }

            var vp = cam.MainCamera.WorldToViewportPoint(p2.transform.position + Vector3.up);
            bool offscreen = vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.1f || vp.y > 1.1f;
            if (!offscreen)
            {
                m_offscreenSince = -1f;
                return;
            }
            if (m_offscreenSince < 0f)
            {
                m_offscreenSince = Time.time;
            }
            else if (Time.time - m_offscreenSince >= leash)
            {
                m_offscreenSince = -1f;
                CoopPlayers.TeleportP2ToP1("offscreen");
            }
        }

        private static bool Usable(LuckyTower.CharacterSystem.Player p) =>
            p != null && p.isActiveAndEnabled && !p.Stats.Dead && !CoopVisibility.IsHidden(p) && p.Components.CameraFocusPoint != null;

        private void EnsureTarget()
        {
            if (m_target != null)
            {
                return;
            }
            var go = new GameObject("CoopCameraTarget");
            DontDestroyOnLoad(go);
            m_target = go.transform;
            Target = m_target;
        }
    }
}
