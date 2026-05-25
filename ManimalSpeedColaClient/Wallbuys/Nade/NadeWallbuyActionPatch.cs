using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // grenade-dispenser wallbuy action. flat 200 TC, no refill/half-price
    // mode, always dispenses 1 VOG-25 (the same grenade ZombiesLoadoutPatch
    // seeds into the player's belt at raid start).
    //
    // placement: rig -> pockets -> belt -> backpack via
    // ZombiesLoadoutPatch.TryPlaceItemAcrossEquipment. matches the existing
    // wallbuy cascade behavior. if every container is full we refund the
    // TarCoins so the player isn't punished for a buy that produced no item.
    internal sealed class NadeWallbuyActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 200;

        // Chattabka VOG-25 - same grenade ZombiesLoadoutPatch.ChattabkaGrenadeTpl
        // uses for the starter belt seed. duplicated here so the wallbuy
        // doesn't have a hard reference to the loadout patch's constant
        // (cosmetic; either would compile).
        public const string GrenadeTpl = "5e340dcdcb6d5863cc5e5efb";

        // bundles the dispense touches: just the grenade itself.
        public static readonly string[] RequiredBundleTpls =
        {
            GrenadeTpl,
        };

        protected override MethodBase GetTargetMethod()
        {
            return typeof(GetActionsClass)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetAvailableActions" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(GamePlayerOwner));
        }

        [PatchPrefix]
        private static bool Prefix(GamePlayerOwner owner, object interactive, ref ActionsReturnClass __result)
        {
            NadeWallbuy wallbuy = interactive as NadeWallbuy;
            if (wallbuy == null) return true;

            try { __result = BuildActions(owner, wallbuy); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[NadeWallbuy] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, NadeWallbuy wallbuy)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            int balance = TarCoinWallet.Balance(owner.Player);
            bool canAfford = balance >= TarCoinPrice;
            string label = canAfford
                ? $"BUY GRENADE ({TarCoinPrice} TC)"
                : $"BUY GRENADE ({TarCoinPrice} TC) - need {TarCoinPrice - balance} more";

            result.Actions.Add(new ActionsTypesClass
            {
                Name = label,
                Disabled = !canAfford,
                Action = canAfford ? (Action)(() => OnBuy(wallbuy, owner.Player)) : null,
            });
            return result;
        }

        private static void OnBuy(NadeWallbuy wallbuy, Player player)
        {
            try
            {
                // we don't charge the player until the grenade has actually
                // been seated in a container. order: balance gate -> create
                // -> placement attempt -> charge. if any step short of the
                // final charge fails, nothing is taken.
                int balance = TarCoinWallet.Balance(player);
                if (balance < TarCoinPrice)
                {
                    Plugin.LogSource?.LogInfo("[NadeWallbuy] not enough TarCoins; buy aborted.");
                    return;
                }

                GameWorld gw = Singleton<GameWorld>.Instance;
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                InventoryController inv = gw?.MainPlayer?.InventoryController;
                if (factory == null || inv == null)
                {
                    Plugin.LogSource?.LogWarning("[NadeWallbuy] ItemFactory or InventoryController missing; buy aborted.");
                    return;
                }

                Item grenade = factory.CreateItem(((IIdGenerator)inv).NextId, GrenadeTpl, null);
                if (grenade == null)
                {
                    Plugin.LogSource?.LogWarning($"[NadeWallbuy] CreateItem({GrenadeTpl}) returned null; buy aborted.");
                    return;
                }
                grenade.SpawnedInSession = true;

                bool placed = ZombiesLoadoutPatch.TryPlaceItemAcrossEquipment(inv.Inventory.Equipment, grenade);
                if (!placed)
                {
                    Plugin.LogSource?.LogWarning("[NadeWallbuy] no container had room for the grenade; buy aborted (no charge).");
                    return;
                }

                // placement landed; NOW charge. balance was re-checked
                // above so this should always succeed, but TrySpend has
                // its own concurrency guard.
                if (!TarCoinWallet.TrySpend(player, TarCoinPrice))
                {
                    Plugin.LogSource?.LogWarning("[NadeWallbuy] TrySpend failed after placement (race?); item left in inventory.");
                    return;
                }

                wallbuy.PlayBuySound();
                Plugin.LogSource?.LogInfo("[NadeWallbuy] dispensed 1x VOG-25.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[NadeWallbuy] buy failed: {ex.Message}");
            }
        }
    }
}
