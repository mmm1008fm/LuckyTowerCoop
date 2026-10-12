using System;

namespace LuckyTowerCoop.Net
{
    public enum NetRole
    {
        None,
        Host,
        Client,
    }

    public enum Delivery
    {
        Reliable,
        Unreliable,
    }

    public enum NetTransportKind
    {
        /// <summary>Steam через Spacewar (AppID 480), отдельный процесс CoopNet.exe.</summary>
        Steam,
        /// <summary>Прямой UDP (LAN / Tailscale / две копии на одном ПК).</summary>
        Direct,
    }

    /// <summary>Типы сообщений протокола (первый байт пакета).</summary>
    public enum Msg : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        Heartbeat = 4,
        Bye = 5,

        SceneLoad = 10,
        SceneReady = 11,
        SegmentSeed = 12,
        RoomEnter = 13,
        EntityRegister = 14,
        EntityDespawn = 15,
        Snapshot = 16,
        EntityDeath = 17,
        PlayerState = 18,
        UiState = 19,
        GameOver = 20,
        /// <summary>Клиент → хост: прошу полную пересинхронизацию комнаты.</summary>
        ResyncRequest = 21,
        /// <summary>Хост → клиент: контрольная сумма комнаты (имя, число и хэш сущностей).</summary>
        RoomCheck = 22,
        /// <summary>Хост → клиент: персонаж начал реплику (кто, ключ текста).</summary>
        Talk = 23,
        /// <summary>Хост → клиент: предмет прикреплён к персонажу (рука, спина) или откреплён.</summary>
        Attach = 24,
        /// <summary>Хост → клиент: какие части персонажа видны (рендеры: включён/активен объект).</summary>
        Look = 25,
        /// <summary>Хост → клиент: ловушка «свет погас» включила/выключила темноту.</summary>
        LightsOut = 26,
        /// <summary>Хост → клиент: окно выбора (диалог, магазин, джинн) у персонажа клиента открылось/закрылось.</summary>
        Window = 27,
        /// <summary>Клиент → хост: просьба начать забег заново / вернуться в деревню (хост решает).</summary>
        RestartRequest = 28,

        Input = 30,
    }

    /// <summary>Транспорт: одно соединение «хост ↔ клиент».</summary>
    public interface INetTransport : IDisposable
    {
        bool IsConnected { get; }

        int PingMs { get; }

        string Status { get; }

        /// <summary>Подключение к удалённой стороне установлено.</summary>
        event Action Connected;

        /// <summary>Соединение потеряно (причина).</summary>
        event Action<string> Disconnected;

        /// <summary>Данные: буфер, смещение, длина.</summary>
        event Action<byte[], int, int> Received;

        void Poll();

        void Send(byte[] data, int length, Delivery delivery);

        void Disconnect();
    }

    internal static class NetConst
    {
        public const int ProtocolVersion = 1;
        public const string ConnectKey = "LTCOOP-1";
        public const float HeartbeatInterval = 0.5f;
        public const float TimeoutSeconds = 6f;
        public const float SnapshotInterval = 0.05f;
        /// <summary>Персонажи игроков — чаще остального мира.</summary>
        public const float SeatSnapshotInterval = 1f / 60f;
        public const float InputInterval = 1f / 60f;
        public const int MaxPacket = 960; // LiteNetLib Unreliable: максимум 1023 байта на пакет

        /// <summary>Следующий срок периодической отправки: от прошлого срока (ровный темп), после долгой паузы — от «сейчас».</summary>
        public static float NextTick(float due, float now, float interval)
        {
            due += interval;
            return due < now - interval ? now + interval : due;
        }
    }
}
