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
    // sibling of SpeedColaActionPatch but for Juggernog. detects JuggernogMachine
    // on GetActionsClass.GetAvailableActions and injects a "Buy" (or "Sold out"
    // if already used this raid) action. on click: dispense the Juggernog item
    // into the player's inventory and auto-use if configured.
    internal sealed class JuggernogActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 2500;

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
            JuggernogMachine machine = interactive as JuggernogMachine;
            if (machine == null) return true;

            try
            {
                __result = BuildActions(owner, machine);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Juggernog] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, JuggernogMachine machine)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            JuggernogInstance instance = machine.GetComponent<JuggernogInstance>();
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

        private static void OnBuy(JuggernogMachine machine, Player player)
        {
            try
            {
                JuggernogInstance instance = machine.GetComponent<JuggernogInstance>();
                if (instance == null)
                {
                    Plugin.LogSource?.LogWarning("[Juggernog] no JuggernogInstance on machine; cannot buy.");
                    return;
                }
                if (instance.Used)
                {
                    Plugin.LogSource?.LogInfo("[Juggernog] machine already used this raid; ignoring buy.");
                    return;
                }
                if (!TarCoinWallet.TrySpend(player, TarCoinPrice))
                {
                    Plugin.LogSource?.LogInfo("[Juggernog] not enough TarCoins; buy aborted.");
                    return;
                }
                instance.PlayBuyJingle();
                _ = DispenseItemAsync(instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Juggernog] buy failed: {ex.Message}");
            }
        }

        // identical fake-stash + QuickFindAppropriatePlace + TryRunNetworkTransaction
        // flow as SpeedColaActionPatch.DispenseItemAsync. one item, lands in any
        // valid container, auto-uses if configured.
        private static async Task DispenseItemAsync(JuggernogInstance instance)
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null || gw.MainPlayer == null) return;

            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            if (factory == null)
            {
                Plugin.LogSource?.LogWarning("[Juggernog] ItemFactoryClass singleton missing - cannot dispense.");
                return;
            }

            InventoryController inv = gw.MainPlayer.InventoryController;
            if (inv == null)
            {
                Plugin.LogSource?.LogWarning("[Juggernog] no InventoryController; cannot dispense.");
                return;
            }

            Item item = factory.CreateItem(((IIdGenerator)inv).NextId, Plugin.JuggernogItemTpl, null);
            if (item == null)
            {
                Plugin.LogSource?.LogWarning($"[Juggernog] factory.CreateItem({Plugin.JuggernogItemTpl}) returned null - check that the item is registered server-side.");
                return;
            }

            item.SpawnedInSession = true;

            try
            {
                StashItemClass fakeStash = factory.CreateFakeStash(null);
                StashGridClass fakeGrid = new StashGridClass(
                    "juggernog fake stash",
                    15, 15,
                    false,
                    Array.Empty<ItemFilter>(),
                    fakeStash);
                fakeStash.Grids[0] = fakeGrid;
                fakeStash.CurrentAddress = inv.CreateItemAddress();

                new TraderControllerClass(fakeStash, inv.ID, "juggernog fake stash", false, EOwnerType.Profile);

                var seat = fakeGrid.AddAnywhere(item, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[Juggernog] could not seat item in fake stash: {seat.Error}");
                    return;
                }

                IEnumerable<CompoundItem> targets = inv.Inventory.Equipment.GetCollections(InventoryEquipment.TraderServicesEligibleSlots);

                var placement = InteractionsHandlerClass.QuickFindAppropriatePlace(
                    item, inv, targets,
                    InteractionsHandlerClass.EMoveItemOrder.PickUp,
                    simulate: true);

                if (placement.Failed)
                {
                    Plugin.LogSource?.LogInfo($"[Juggernog] no inventory space ({placement.Error}); skipping dispense.");
                    return;
                }

                IResult result = await inv.TryRunNetworkTransaction(placement, null);
                if (result.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[Juggernog] inventory transaction failed: {result.Error}");
                    return;
                }

                Plugin.LogSource?.LogInfo("[Juggernog] dispensed into inventory.");

                if (instance != null) instance.MarkUsed();

                if (Plugin.JuggernogAutoUseAfterBuy != null && Plugin.JuggernogAutoUseAfterBuy.Value)
                {
                    TryAutoUse(gw.MainPlayer, item);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Juggernog] inventory placement threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryAutoUse(Player player, Item item)
        {
            try
            {
                FoodDrinkItemClass foodDrink = item as FoodDrinkItemClass;
                if (foodDrink == null)
                {
                    Plugin.LogSource?.LogWarning("[Juggernog] dispensed item is not FoodDrinkItemClass; cannot auto-use.");
                    return;
                }

                player.Proceed(
                    foodDrink,
                    1f,
                    new Callback<GInterface203>(OnAutoUseResult),
                    foodDrink.GetRandomAnimationVariant(),
                    scheduled: true);

                Plugin.LogSource?.LogInfo("[Juggernog] auto-use initiated.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Juggernog] auto-use threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnAutoUseResult(Result<GInterface203> result)
        {
            if (result.Failed)
                Plugin.LogSource?.LogWarning($"[Juggernog] auto-use failed: {result.Error}");
        }
    }
}
