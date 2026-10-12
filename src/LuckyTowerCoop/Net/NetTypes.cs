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
        /// <summary>Steam via Spacewar (AppID 480), separate CoopNet.exe process.</summary>
        Steam,
        /// <summary>Direct UDP (LAN / Tailscale / two copies on one PC).</summary>
        Direct,
    }

    /// <summary>Protocol message types (first byte of the packet).</summary>
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
        /// <summary>Client -> host: requests a full room resync.</summary>
        ResyncRequest = 21,
        /// <summary>Host -> client: room checksum (name, entity count and hash).</summary>
        RoomCheck = 22,
        /// <summary>Host -> client: a character started a line (who, text key).</summary>
        Talk = 23,
        /// <summary>Host -> client: an item was attached to a character (hand, back) or detached.</summary>
        Attach = 24,
        /// <summary>Host -> client: which character parts are visible (renderers: enabled/object active).</summary>
        Look = 25,
        /// <summary>Host -> client: the "lights out" trap turned the darkness on/off.</summary>
        LightsOut = 26,
        /// <summary>Host -> client: a choice window (dialog, shop, genie) opened/closed for the client's character.</summary>
        Window = 27,
        /// <summary>Client -> host: request to restart the run / return to the village (the host decides).</summary>
        RestartRequest = 28,
        /// <summary>Client -> host: give the partner N coins from your character (only the host changes them).</summary>
        GiveCoins = 29,

        /// <summary>Host -> client: the tower (powder keg) exploded - play the explosion cutscene.</summary>
        TowerExplosion = 31,

        Input = 30,
    }

    /// <summary>Transport: a single "host <-> client" connection.</summary>
    public interface INetTransport : IDisposable
    {
        bool IsConnected { get; }

        int PingMs { get; }

        string Status { get; }

        /// <summary>Connection to the remote side established.</summary>
        event Action Connected;

        /// <summary>Connection lost (reason).</summary>
        event Action<string> Disconnected;

        /// <summary>Data: buffer, offset, length.</summary>
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
        /// <summary>Player characters - more often than the rest of the world.</summary>
        public const float SeatSnapshotInterval = 1f / 60f;
        public const float InputInterval = 1f / 60f;
        public const int MaxPacket = 960; // LiteNetLib Unreliable: max 1023 bytes per packet

        /// <summary>Next periodic send deadline: from the previous deadline (steady pace), after a long pause - from "now".</summary>
        public static float NextTick(float due, float now, float interval)
        {
            due += interval;
            return due < now - interval ? now + interval : due;
        }
    }
}
