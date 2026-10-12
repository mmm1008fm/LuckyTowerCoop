using System.Linq;
using System.Text;
using LuckyTower;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;
using GamePlayer = LuckyTower.CharacterSystem.Player;
using InputSystemGamepad = UnityEngine.InputSystem.Gamepad;

namespace LuckyTowerCoop.Coop
{
    /// <summary>Диагностические дампы состояния (в лог и в ответ отладочного сервера). F8 — дамп в лог.</summary>
    public class CoopProbe : MonoBehaviour
    {
        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            {
                Plugin.Log.LogInfo(Dump("F8"));
            }
        }

        public static string Dump(string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Probe] ---- {reason} (scene: {SceneManager.GetActiveScene().name}, frame {Time.frameCount}) ----");

            if (ReInput.isReady)
            {
                sb.AppendLine($"Rewired players: {ReInput.players.playerCount} (+system)");
                foreach (var p in ReInput.players.GetPlayers(includeSystemPlayer: true))
                {
                    sb.Append($"  #{p.id} '{p.name}' playing={p.isPlaying} kb={p.controllers.hasKeyboard} mouse={p.controllers.hasMouse} joy=[");
                    sb.Append(string.Join(", ", p.controllers.Joysticks.Select(j => $"{j.name} id={j.id}")));
                    sb.AppendLine("]");
                }
                sb.AppendLine($"Rewired joysticks total: {ReInput.controllers.joystickCount}");
            }
            else
            {
                sb.AppendLine("Rewired not ready");
            }
            sb.AppendLine($"InputSystem gamepads: {InputSystemGamepad.all.Count}");

            var gm = GameManager.InstanceNoWarnings;
            if (gm != null)
            {
                sb.AppendLine($"GameManager: scene={gm.SceneName} initialized={CoopPlayers.IsInitialized(gm)} room={(gm.CurrentRoom != null ? gm.CurrentRoom.name : "-")} inEnterDoor={gm.InEnterDoor}");
            }
            sb.AppendLine($"LayerManager.CurrentPlayerLayer={LuckyTower.LevelLayers.LayerManager.CurrentPlayerLayer}");
            var cam = CameraManager.Instance;
            if (cam != null && cam.VirtualCamera != null)
            {
                var follow = cam.VirtualCamera.Follow;
                sb.AppendLine($"Camera: follow={(follow != null ? follow.root.name + "/" + follow.name : "null")} pos={cam.MainCamera.transform.position}");
            }

            for (int i = 0; i < GamePlayer.VonWansts.Length; i++)
            {
                var w = GamePlayer.VonWansts[i];
                if (w != null)
                {
                    sb.AppendLine($"VonWansts[{i}] = '{w.gameObject.name}' num={w.Stats.PlayerNumber} pos={w.transform.position} dead={w.Stats.Dead} " +
                                  $"rewired={(w.PlayerInput != null ? w.PlayerInput.id : -1)} inputAllowed={w.Stats.InputAllowed} gm.Player={(gm != null && gm.Player == w)}");
                }
            }
            sb.AppendLine($"Coop P2: {(CoopPlayers.P2 != null ? CoopPlayers.P2.name : "none")}");
            return sb.ToString();
        }

        public static string DumpMaps()
        {
            if (!ReInput.isReady)
            {
                return "Rewired not ready";
            }
            var sb = new StringBuilder();
            foreach (var p in ReInput.players.GetPlayers(includeSystemPlayer: true))
            {
                sb.AppendLine($"#{p.id} '{p.name}' hasKeyboard={p.controllers.hasKeyboard} joysticks={p.controllers.joystickCount}");
                foreach (var type in new[] { ControllerType.Keyboard, ControllerType.Joystick })
                {
                    {
                        foreach (var map in p.controllers.maps.GetAllMaps(type))
                        {
                            sb.AppendLine($"#{p.id} {type} controllerId={map.controllerId} map cat={map.categoryId} layout={map.layoutId} enabled={map.enabled}");
                            foreach (var aem in map.AllMaps)
                            {
                                var action = ReInput.mapping.GetAction(aem.actionId);
                                sb.AppendLine($"    {aem.actionId,3} {(action != null ? action.name : "?"),-16} <- {aem.elementIdentifierName} {(type == ControllerType.Keyboard ? aem.keyCode.ToString() : "")} {aem.axisContribution} {(aem.enabled ? "" : "(disabled)")}");
                            }
                        }
                    }
                }
            }
            return sb.ToString();
        }
    }
}
