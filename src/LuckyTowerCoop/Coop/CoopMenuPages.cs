using System;
using BepInEx.Configuration;
using LuckyTowerCoop.Net;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Вкладки «Настройки» и «Игроки» меню F7. Значения сразу пишутся в конфиг (BepInEx сохраняет файл сам).
    /// Правила (сложность, воскрешение, подтягивание отставшего) действуют у хоста / в локальной игре — у клиента только для чтения.
    /// </summary>
    internal static class CoopMenuPages
    {
        private static GUIStyle s_header;

        public static void DrawSettings()
        {
            bool client = NetSession.IsClient;
            bool enabled = GUI.enabled;

            Header("Сложность для двоих");
            if (client)
            {
                Note("Сложность и правила задаёт хост — здесь они не действуют.");
            }
            GUI.enabled = enabled && !client;
            Set(CoopConfig.EnemyHealthMultiplier, Slider("Здоровье врагов", CoopConfig.EnemyHealthMultiplier.Value, 0.25f, 5f, 0.25f, v => $"×{v:0.##}"));
            Set(CoopConfig.ExtraEnemies, Slider("Дополнительные монстры", CoopConfig.ExtraEnemies.Value, 0f, 3f, 0.25f, v => v <= 0f ? "нет" : $"+{v * 100f:0}%"));
            Note("Применяется к комнатам, в которые вы ещё не заходили.");

            Header("Воскрешение напарника");
            Set(CoopConfig.KillsToRevive, Mathf.RoundToInt(Slider("Убийств для воскрешения", CoopConfig.KillsToRevive.Value, 1f, 20f, 1f, v => $"{v:0}")));
            Set(CoopConfig.ReviveOnNewFloor, GUILayout.Toggle(CoopConfig.ReviveOnNewFloor.Value, " Воскрешать при переходе на новый этаж"));

            Header("Камера");
            Set(CoopConfig.LeashSeconds, Slider("Подтянуть отставшего через", CoopConfig.LeashSeconds.Value, 0f, 10f, 0.5f, v => v <= 0f ? "никогда" : $"{v:0.#} с"));
            GUI.enabled = enabled;
            Set(CoopConfig.ShowNames, GUILayout.Toggle(CoopConfig.ShowNames.Value, " Подписи над персонажами"));

            if (NetSession.Role == NetRole.None)
            {
                Header("Второй игрок на этом ПК");
                bool kb = CoopConfig.P2Device.Value == P2DeviceMode.Keyboard;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Управление:", GUILayout.Width(110));
                if (GUILayout.Toggle(!kb, " геймпад", GUILayout.Width(110)) && kb)
                {
                    CoopConfig.P2Device.Value = P2DeviceMode.Gamepad;
                    CoopInput.MarkDirty();
                }
                if (GUILayout.Toggle(kb, " клавиатура (оба с клавиатуры)") && !kb)
                {
                    CoopConfig.P2Device.Value = P2DeviceMode.Keyboard;
                    CoopInput.MarkDirty();
                }
                GUILayout.EndHorizontal();
                if (kb)
                {
                    Note("Второй: стрелки, K прыжок, J атака, L подобрать, U бросить, I использовать, O сменить руки, P выкинуть, 7/8/9 ответы. Первый — схема «клавиатура + мышь».");
                }
                Set(CoopConfig.AutoSpawnP2, GUILayout.Toggle(CoopConfig.AutoSpawnP2.Value, " Второй игрок появляется сам"));
                Set(CoopConfig.SpawnInVillage, GUILayout.Toggle(CoopConfig.SpawnInVillage.Value, " …и в деревне"));
            }

            Header("Сеть");
            Set(CoopConfig.NetPauseOnDisconnect, GUILayout.Toggle(CoopConfig.NetPauseOnDisconnect.Value, " Пауза, пока отключившийся напарник не вернётся"));
            Set(CoopConfig.NetPredict, GUILayout.Toggle(CoopConfig.NetPredict.Value, " Предсказание движения у клиента (идти сразу по нажатию)"));
            Set(CoopConfig.NetReconnectSeconds, Slider("Продолжить без него через", CoopConfig.NetReconnectSeconds.Value, 0f, 600f, 30f, v => v <= 0f ? "решаю сам" : $"{v / 60f:0.#} мин"));

            GUILayout.Space(8);
            Note($"Меню — {CoopConfig.NetMenuKey.Value}. Всё сохраняется сразу в BepInEx\\config\\{Plugin.Guid}.cfg");
        }

        public static void DrawPlayers()
        {
            bool client = NetSession.IsClient;
            Header("Персонажи");
            DrawPlayerLine(CoopPlayers.P1, NetSession.Role == NetRole.None ? "Игрок 1" : "Вы");
            DrawPlayerLine(CoopPlayers.P2, NetSession.Role == NetRole.None ? "Игрок 2" : Partner());

            if (client)
            {
                GUILayout.Space(6);
                Note("Действия с персонажами доступны хосту. Если что-то разъехалось — «Пересинхронизировать» на вкладке «Онлайн».");
                return;
            }

            Header("Действия");
            if (CoopLife.SomeoneDead)
            {
                GUILayout.Label($"Напарник погиб: {CoopLife.Kills}/{CoopConfig.KillsToRevive.Value} убийств" + (CoopConfig.ReviveOnNewFloor.Value ? " или новый этаж" : ""));
                if (GUILayout.Button("Воскресить сейчас", GUILayout.Height(28)))
                {
                    CoopLife.ReviveNow();
                }
            }
            GUI.enabled = CoopPlayers.P1 != null && CoopPlayers.P2 != null;
            if (GUILayout.Button("Подтянуть напарника ко мне (если застрял)", GUILayout.Height(28)))
            {
                CoopPlayers.TeleportP2ToP1("menu");
            }
            GUI.enabled = true;

            if (NetSession.Role == NetRole.None)
            {
                var p1 = CoopPlayers.P1;
                if (CoopPlayers.P2 == null && !CoopLife.SomeoneDead)
                {
                    GUI.enabled = p1 != null && !p1.Stats.Dead;
                    if (GUILayout.Button("Добавить второго игрока", GUILayout.Height(28)))
                    {
                        CoopPlayers.SpawnP2(p1.transform.position + Vector3.right * 0.8f);
                    }
                    GUI.enabled = true;
                }
                else if (CoopPlayers.P2 != null && GUILayout.Button("Убрать второго игрока", GUILayout.Height(28)))
                {
                    CoopPlayers.Despawn();
                }
            }
        }

        private static string Partner()
        {
            string name = NetSession.Instance != null ? NetSession.Instance.PeerName : null;
            return string.IsNullOrEmpty(name) ? "Напарник" : name;
        }

        private static void DrawPlayerLine(GamePlayer p, string who)
        {
            if (p == null)
            {
                GUILayout.Label($"{who}: —");
                return;
            }
            var s = p.Stats;
            string hands = (s.HasWeapon ? s.WeaponItem.ItemName : "пусто") + (s.ItemInSecondHand != null ? " + " + s.ItemInSecondHand.ItemName : "");
            GUILayout.Label($"{who}: {(s.Dead ? "погиб" : "жив")} · в руках: {hands} · золото: {s.CurrentGoldCount()}");
        }

        // ---------------- элементы ----------------

        private static void Header(string text)
        {
            if (s_header == null)
            {
                s_header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            }
            GUILayout.Space(6);
            GUILayout.Label(text, s_header);
        }

        private static void Note(string text)
        {
            var c = GUI.contentColor;
            GUI.contentColor = new Color(0.8f, 0.8f, 0.8f);
            GUILayout.Label(text);
            GUI.contentColor = c;
        }

        private static float Slider(string label, float value, float min, float max, float step, Func<float, string> format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(220));
            bool changedBefore = GUI.changed;
            GUI.changed = false;
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(200));
            bool moved = GUI.changed;
            GUI.changed |= changedBefore;
            // значение из конфига вне диапазона ползунка не трогаем, пока ползунок не сдвинули
            v = moved ? Mathf.Clamp(Mathf.Round(v / step) * step, min, max) : value;
            GUILayout.Label(format(v), GUILayout.Width(90));
            GUILayout.EndHorizontal();
            return v;
        }

        private static void Set(ConfigEntry<float> e, float v)
        {
            if (!Mathf.Approximately(e.Value, v))
            {
                e.Value = v;
            }
        }

        private static void Set(ConfigEntry<int> e, int v)
        {
            if (e.Value != v)
            {
                e.Value = v;
            }
        }

        private static void Set(ConfigEntry<bool> e, bool v)
        {
            if (e.Value != v)
            {
                e.Value = v;
            }
        }
    }
}
