using LuckyTower;
using LuckyTower.Items;
using LuckyTowerCoop.Patches;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Монеты персонажей: выдать, забрать, передать напарнику. Монеты живут в кошеле на поясе (GoldSack, до 20),
    /// в кошеле/монете в руках. Всё выполняется на стороне, где персонаж симулируется (хост или локальная игра).
    /// </summary>
    internal static class CoopGold
    {
        public const int SackMax = 20;

        public static int Count(GamePlayer p) => p != null && p.Stats != null ? p.Stats.CurrentGoldCount() : 0;

        /// <summary>Сколько монет ещё поместится на пояс (нет кошеля — создадим пустой на 20).</summary>
        public static int FreeSpace(GamePlayer p)
        {
            if (p == null || p.Stats == null)
            {
                return 0;
            }
            var sack = p.Stats.CurrentGoldSack;
            return sack == null ? SackMax : System.Math.Max(0, sack.MaxGold - sack.CurrentGold);
        }

        /// <summary>Положить монеты на пояс (кошель создаётся при необходимости, как при подборе). Возвращает, сколько положено.</summary>
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

        /// <summary>Списать монеты (кошель в руках, монета в руках, пояс — в порядке игры). Возвращает, сколько списано.</summary>
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

        /// <summary>Установить ровно столько монет (отладка): остальные списываются / докладываются на пояс.</summary>
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

        /// <summary>Передать напарнику не больше amount монет (сколько есть и сколько у него влезет). Возвращает переданное.</summary>
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
                Add(from, removed - added); // не влезло — вернуть
            }
            return added;
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
