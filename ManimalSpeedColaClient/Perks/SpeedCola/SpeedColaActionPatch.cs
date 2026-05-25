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
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // prefix on GetActionsClass.GetAvailableActions(owner, interactive):
    // detect SpeedColaMachine and inject a "Buy" action. without this the
    // vanilla method hits the bottom of its long if-else chain and throws
    // "no interactions defined for SpeedColaMachine".
    //
    // resolves the target method by parameter shape — the second parameter is
    // an obfuscated GInterfaceXXX that shifts index between BSG builds.
    internal sealed class SpeedColaActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 3000;

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
            SpeedColaMachine machine = interactive as SpeedColaMachine;
            if (machine == null) return true; // not ours, let vanilla run

            try
            {
                __result = BuildActions(owner, machine);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, SpeedColaMachine machine)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            SpeedColaInstance instance = machine.GetComponent<SpeedColaInstance>();
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
                Action = disabled ? (System.Action)null : (() => OnBuy(machine, owner.Player)),
            });
            return result;
        }

        private static void OnBuy(SpeedColaMachine machine, Player player)
        {
            try
            {
                SpeedColaInstance instance = machine.GetComponent<SpeedColaInstance>();
                if (instance == null)
                {
                    Plugin.LogSource?.LogWarning("[SpeedCola] no SpeedColaInstance on machine; cannot buy.");
                    return;
                }
                if (instance.Used)
                {
                    Plugin.LogSource?.LogInfo("[SpeedCola] machine already used this raid; ignoring buy.");
                    return;
                }
                if (!TarCoinWallet.TrySpend(player, TarCoinPrice))
                {
                    Plugin.LogSource?.LogInfo("[SpeedCola] not enough TarCoins; buy aborted.");
                    return;
                }
                instance.PlayBuyJingle();

                // dispense flow uses async network transaction; fire-and-forget
                // so the action handler returns immediately. instance is passed
                // so DispenseItemAsync can mark Used after a SUCCESSFUL placement
                // - that way a failed buy (e.g. inventory full) doesnt eat the
                // player's one-shot.
                _ = DispenseItemAsync(instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] buy failed: {ex.Message}");
            }
        }

        // tries to place the item directly into pockets/backpack/rig/secure via
        // InteractionsHandlerClass.QuickFindAppropriatePlace + the inventory
        // controller's network transaction. this is the same flow trader
        // services (BTR deliveries etc) use - see InteractionsHandlerClass.cs
        // lines 2056-2080 in the decompiled 4.0 assembly.
        //
        // falls back to dropping at player feet if no space or anything else
        // fails. uses the InventoryController as the id generator so the new
        // item's id is unique within the player's namespace.
        private static async Task DispenseItemAsync(SpeedColaInstance instance)
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null || gw.MainPlayer == null) return;

            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            if (factory == null)
            {
                Plugin.LogSource?.LogWarning("[SpeedCola] ItemFactoryClass singleton missing - cannot dispense.");
                return;
            }

            InventoryController inv = gw.MainPlayer.InventoryController;
            if (inv == null)
            {
                Plugin.LogSource?.LogWarning("[SpeedCola] no InventoryController; cannot dispense.");
                return;
            }

            Item item = factory.CreateItem(((IIdGenerator)inv).NextId, Plugin.SpeedColaItemTpl, null);
            if (item == null)
            {
                Plugin.LogSource?.LogWarning($"[SpeedCola] factory.CreateItem({Plugin.SpeedColaItemTpl}) returned null - check that the item is registered server-side.");
                return;
            }

            // mark as found-in-raid so the bought can shows the green FIR mark
            // and the destination container fires the correct discovery events.
            item.SpawnedInSession = true;

            try
            {
                // freshly-created items have no CurrentAddress, and accessing
                // Item.Parent on a parentless item throws. QuickFindAppropriatePlace
                // and its downstream helpers (Move, DestinationCheck, etc) all
                // touch Parent at some point, so IgnoreItemParent alone is not
                // enough.
                //
                // canonical fix mirroring the in-raid quest reward / BTR delivery
                // flow (see QuestRewardDataClass.TryAppendClaimResults and
                // InteractionsHandlerClass trader-services code path): wrap the
                // item in a fake stash that gives it a parent address, then run
                // QuickFindAppropriatePlace to move it into real equipment.
                StashItemClass fakeStash = factory.CreateFakeStash(null);
                StashGridClass fakeGrid = new StashGridClass(
                    "speedcola fake stash",
                    15, 15,
                    false,
                    Array.Empty<ItemFilter>(),
                    fakeStash);
                fakeStash.Grids[0] = fakeGrid;
                fakeStash.CurrentAddress = inv.CreateItemAddress();

                // construct a throwaway TraderControllerClass over the fake stash
                // (matching QuestRewardDataClass.cs:113). the ctor wires the
                // stash as a "from" owner so the subsequent move dispatches
                // ownership-transfer events instead of leaving the destination
                // container marked as unsearched.
                new TraderControllerClass(fakeStash, inv.ID, "speedcola fake stash", false, EOwnerType.Profile);

                var seat = fakeGrid.AddAnywhere(item, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[SpeedCola] could not seat item in fake stash: {seat.Error}");
                    return;
                }

                IEnumerable<CompoundItem> targets = inv.Inventory.Equipment.GetCollections(InventoryEquipment.TraderServicesEligibleSlots);

                // simulate: true here means "find a spot, return an operation, do
                // NOT apply locally". TryRunNetworkTransaction below applies it
                // and raises the inventory events that update the UI. with
                // simulate=false we'd commit twice and the destination stayed
                // marked unsearched because the transaction collapsed to a
                // "same-place" no-op.
                var placement = InteractionsHandlerClass.QuickFindAppropriatePlace(
                    item, inv, targets,
                    InteractionsHandlerClass.EMoveItemOrder.PickUp,
                    simulate: true);

                if (placement.Failed)
                {
                    Plugin.LogSource?.LogInfo($"[SpeedCola] no inventory space ({placement.Error}); skipping dispense.");
                    return;
                }

                IResult result = await inv.TryRunNetworkTransaction(placement, null);
                if (result.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[SpeedCola] inventory transaction failed: {result.Error}");
                    return;
                }

                Plugin.LogSource?.LogInfo("[SpeedCola] dispensed into inventory.");

                // mark the machine as used only after the dispense actually
                // commits - failed transactions (e.g. no inventory space)
                // shouldn't burn the player's one-shot. action label flips
                // to "Sold out" + Disabled the next time GetAvailableActions
                // runs (next time the player looks at the machine).
                if (instance != null) instance.Used = true;

                if (Plugin.AutoUseAfterBuy != null && Plugin.AutoUseAfterBuy.Value)
                {
                    TryAutoUse(gw.MainPlayer, item);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] inventory placement threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // kicks off the food/drink Proceed flow on the player so the SpeedCola
        // animation begins immediately - same path the game uses when the
        // player manually selects "Use" on a food item (see Player.cs:9419).
        private static void TryAutoUse(Player player, Item item)
        {
            try
            {
                FoodDrinkItemClass foodDrink = item as FoodDrinkItemClass;
                if (foodDrink == null)
                {
                    Plugin.LogSource?.LogWarning("[SpeedCola] dispensed item is not FoodDrinkItemClass; cannot auto-use.");
                    return;
                }

                player.Proceed(
                    foodDrink,
                    1f,
                    new Callback<GInterface203>(OnAutoUseResult),
                    foodDrink.GetRandomAnimationVariant(),
                    scheduled: true);

                Plugin.LogSource?.LogInfo("[SpeedCola] auto-use initiated.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] auto-use threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnAutoUseResult(Result<GInterface203> result)
        {
            if (result.Failed)
                Plugin.LogSource?.LogWarning($"[SpeedCola] auto-use failed: {result.Error}");
        }
    }
}
