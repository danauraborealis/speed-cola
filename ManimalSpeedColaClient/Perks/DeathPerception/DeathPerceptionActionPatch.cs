using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // mirror of StaminupActionPatch / JuggernogActionPatch. detects
    // DeathPerceptionMachine on GetActionsClass.GetAvailableActions and injects
    // a "BUY (price)" action (or "Sold out" if already used this raid; or
    // "BUY (price) - need X more" if the player is broke). on buy: dispenses
    // the DP can and auto-uses if configured.
    internal sealed class DeathPerceptionActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 2000;

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
        private static bool Prefix(
            GamePlayerOwner owner,
            object interactive,
            ref ActionsReturnClass __result)
        {
            DeathPerceptionMachine machine = interactive as DeathPerceptionMachine;
            if (machine == null) return true;

            try
            {
                __result = BuildActions(owner, machine);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[DeathPerception] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, DeathPerceptionMachine machine)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            DeathPerceptionInstance instance = machine.GetComponent<DeathPerceptionInstance>();
            bool used = instance != null && instance.Used;
            int balance = TarCoinWallet.Balance(owner.Player);
            bool canAfford = balance >= TarCoinPrice;

            string label;
            bool disabled;
            if (used) { label = "Sold out"; disabled = true; }
            else if (!canAfford) { label = $"BUY ({TarCoinPrice} TC) - need {TarCoinPrice - balance} more"; disabled = true; }
            else { label = $"BUY ({TarCoinPrice} TC)"; disabled = false; }

            result.Actions.Add(new ActionsTypesClass
            {
                Name = label,
                Disabled = disabled,
                Action = disabled ? (Action)null : (() => OnBuy(machine, owner.Player)),
            });
            return result;
        }

        private static void OnBuy(DeathPerceptionMachine machine, Player player)
        {
            try
            {
                DeathPerceptionInstance instance = machine.GetComponent<DeathPerceptionInstance>();
                if (instance == null)
                {
                    Plugin.LogSource?.LogWarning("[DeathPerception] no DeathPerceptionInstance on machine; cannot buy.");
                    return;
                }
                if (instance.Used)
                {
                    Plugin.LogSource?.LogInfo("[DeathPerception] machine already used this raid; ignoring buy.");
                    return;
                }
                if (!TarCoinWallet.TrySpend(player, TarCoinPrice))
                {
                    Plugin.LogSource?.LogInfo("[DeathPerception] not enough TarCoins; buy aborted.");
                    return;
                }
                instance.PlayBuyJingle();
                _ = DispenseItemAsync(instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[DeathPerception] buy failed: {ex.Message}");
            }
        }

        // identical fake-stash + QuickFindAppropriatePlace + TryRunNetworkTransaction
        // flow as StaminupActionPatch.DispenseItemAsync.
        private static async Task DispenseItemAsync(DeathPerceptionInstance instance)
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null || gw.MainPlayer == null) return;

            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            if (factory == null)
            {
                Plugin.LogSource?.LogWarning("[DeathPerception] ItemFactoryClass singleton missing - cannot dispense.");
                return;
            }

            InventoryController inv = gw.MainPlayer.InventoryController;
            if (inv == null)
            {
                Plugin.LogSource?.LogWarning("[DeathPerception] no InventoryController; cannot dispense.");
                return;
            }

            Item item = factory.CreateItem(((IIdGenerator)inv).NextId, Plugin.DeathPerceptionItemTpl, null);
            if (item == null)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] factory.CreateItem({Plugin.DeathPerceptionItemTpl}) returned null - check that the item is registered server-side.");
                return;
            }

            item.SpawnedInSession = true;

            try
            {
                StashItemClass fakeStash = factory.CreateFakeStash(null);
                StashGridClass fakeGrid = new StashGridClass(
                    "deathperception fake stash",
                    15, 15,
                    false,
                    Array.Empty<ItemFilter>(),
                    fakeStash);
                fakeStash.Grids[0] = fakeGrid;
                fakeStash.CurrentAddress = inv.CreateItemAddress();

                new TraderControllerClass(fakeStash, inv.ID, "deathperception fake stash", false, EOwnerType.Profile);

                var seat = fakeGrid.AddAnywhere(item, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[DeathPerception] could not seat item in fake stash: {seat.Error}");
                    return;
                }

                IEnumerable<CompoundItem> targets = inv.Inventory.Equipment.GetCollections(InventoryEquipment.TraderServicesEligibleSlots);

                var placement = InteractionsHandlerClass.QuickFindAppropriatePlace(
                    item, inv, targets,
                    InteractionsHandlerClass.EMoveItemOrder.PickUp,
                    simulate: true);

                if (placement.Failed)
                {
                    Plugin.LogSource?.LogInfo($"[DeathPerception] no inventory space ({placement.Error}); skipping dispense.");
                    return;
                }

                IResult result = await inv.TryRunNetworkTransaction(placement, null);
                if (result.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[DeathPerception] inventory transaction failed: {result.Error}");
                    return;
                }

                Plugin.LogSource?.LogInfo("[DeathPerception] dispensed into inventory.");

                if (instance != null) instance.MarkUsed();

                if (Plugin.DeathPerceptionAutoUseAfterBuy != null && Plugin.DeathPerceptionAutoUseAfterBuy.Value)
                {
                    TryAutoUse(gw.MainPlayer, item);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[DeathPerception] inventory placement threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryAutoUse(Player player, Item item)
        {
            try
            {
                FoodDrinkItemClass foodDrink = item as FoodDrinkItemClass;
                if (foodDrink == null)
                {
                    Plugin.LogSource?.LogWarning("[DeathPerception] dispensed item is not FoodDrinkItemClass; cannot auto-use.");
                    return;
                }

                player.Proceed(
                    foodDrink,
                    1f,
                    new Callback<GInterface203>(OnAutoUseResult),
                    foodDrink.GetRandomAnimationVariant(),
                    scheduled: true);

                Plugin.LogSource?.LogInfo("[DeathPerception] auto-use initiated.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[DeathPerception] auto-use threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnAutoUseResult(Result<GInterface203> result)
        {
            if (result.Failed)
                Plugin.LogSource?.LogWarning($"[DeathPerception] auto-use failed: {result.Error}");
        }
    }
}
