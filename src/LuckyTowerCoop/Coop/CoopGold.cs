using LuckyTower;
using LuckyTowerCoop.Net;
using LuckyTower.Items;
using LuckyTowerCoop.Patches;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Character coins: give, take, hand over to the partner. Coins live in a belt pouch (GoldSack, up to 20),
    /// in a pouch/coin held in hand. All of it runs on the side that simulates the character (host or local play).
    /// </summary>
    internal static class CoopGold
    {
        public const int SackMax = 20;

        public static int Count(GamePlayer p) => p != null && p.Stats != null ? p.Stats.CurrentGoldCount() : 0;

        /// <summary>How many more coins fit on the belt (no pouch - an empty one for 20 will be created).</summary>
        public static int FreeSpace(GamePlayer p)
        {
            if (p == null || p.Stats == null)
            {
                return 0;
            }
            var sack = p.Stats.CurrentGoldSack;
            return sack == null ? SackMax : System.Math.Max(0, sack.MaxGold - sack.CurrentGold);
        }

        /// <summary>Put coins on the belt (a pouch is created if needed, as when picking up). Returns how many were put.</summary>
        public static int Add(GamePlayer p, int amount)
        {
            if (p == null || p.Stats == null || amount <= 0)
            {
                return 0;
            }
            amount = System.Math.Min(amount, FreeSpace(p));
            if (amount <= 0)
            {
                return 0;
            }
            var st = TargetSwap.SwapTo(p);
            try
            {
                var sack = p.Stats.CurrentGoldSack;
                if (sack == null)
                {
                    sack = NewEmptySack();
                    if (sack == null)
                    {
                        return 0;
                    }
                    p.Actions.EquipGoldSack(sack, fromHand: false, swapAfterEquip: false);
                    sack = p.Stats.CurrentGoldSack;
                    if (sack == null)
                    {
                        return 0;
                    }
                }
                sack.AddCoin(amount, playSound: false);
                p.Components.Animator.ChangeGoldSackSprite(sack.CurrentGold);
                return amount;
            }
            finally
            {
                TargetSwap.Restore(st);
            }
        }

        /// <summary>Deduct coins (pouch in hand, coin in hand, belt - in the game's order). Returns how many were deducted.</summary>
        public static int Remove(GamePlayer p, int amount)
        {
            if (p == null || p.Stats == null || amount <= 0)
            {
                return 0;
            }
            amount = System.Math.Min(amount, Count(p));
            if (amount <= 0)
            {
                return 0;
            }
            var st = TargetSwap.SwapTo(p);
            try
            {
                p.Utilities.RemoveCoins(amount);
            }
            finally
            {
                TargetSwap.Restore(st);
            }
            return amount;
        }

        /// <summary>Set exactly this many coins (debug): the rest is deducted / added to the belt.</summary>
        public static int Set(GamePlayer p, int total)
        {
            int cur = Count(p);
            if (total > cur)
            {
                Add(p, total - cur);
            }
            else if (total < cur)
            {
                Remove(p, cur - total);
            }
            return Count(p);
        }

        /// <summary>Give the partner up to amount coins (as many as available and as many as fit). Returns the amount given.</summary>
        public static int Transfer(GamePlayer from, GamePlayer to, int amount)
        {
            if (from == null || to == null || from == to || from.Stats == null || to.Stats == null || from.Stats.Dead || to.Stats.Dead)
            {
                return 0;
            }
            int n = System.Math.Min(System.Math.Min(amount, Count(from)), FreeSpace(to));
            if (n <= 0)
            {
                return 0;
            }
            int removed = Remove(from, n);
            int added = Add(to, removed);
            if (added < removed)
            {
                Add(from, removed - added); // did not fit - give back
            }
            return added;
        }


        /// <summary>Seat of the first/second character in the menu: for the client their own character (P1) is seat B.</summary>
        public static Seat SeatOf(bool first) => NetSession.IsClient ? (first ? Seat.B : Seat.A) : (first ? CoopPlayers.MainSeat : CoopPlayers.SecondSeat);

        /// <summary>Coins of a seat according to the host's latest word (on the client the characters are puppets without pouches).</summary>
        public static readonly int[] SeatTotals = new int[2];

        /// <summary>Coins of the seat's character: on the client - as sent by the host, otherwise counted from pouches.</summary>
        public static int TotalOf(GamePlayer p, Seat seat) => NetSession.IsClient ? SeatTotals[(int)seat] : Count(p);

        /// <summary>
        /// Give the partner coins from "my" character. Host / local play - immediately; client - a request to the host (only the host changes anything).
        /// Returns the amount given (always 0 for the client: the answer arrives as coin counts in the next PlayerState).
        /// </summary>
        public static int GiveToPartner(GamePlayer mine, int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }
            if (NetSession.IsClient)
            {
                NetSession.Instance?.Client?.RequestGiveCoins(amount);
                return 0;
            }
            var other = mine == CoopPlayers.P1 ? CoopPlayers.P2 : CoopPlayers.P1;
            int n = Transfer(mine, other, amount);
            Plugin.Log.LogInfo($"[Coop] coins: {mine?.name} -> {other?.name}: asked {amount}, moved {n}");
            return n;
        }

        private static GoldSack NewEmptySack()
        {
            var gm = GameManager.InstanceNoWarnings;
            var prefab = gm != null ? gm.ItemLibrary.GetItem("GoldsackEmpty") : null;
            if (prefab == null)
            {
                return null;
            }
            var item = UnityEngine.Object.Instantiate(prefab);
            item.FirstTimeGrounded = true;
            var sack = item.GetComponentInChildren<GoldSack>();
            sack.Initialize();
            sack.SetCoins(0);
            var special = item.GetComponentInChildren<GoldSackSpecialItem>();
            if (special != null)
            {
                special.InitializeSpecialItem();
            }
            item.StartInit();
            return sack;
        }
    }
}
