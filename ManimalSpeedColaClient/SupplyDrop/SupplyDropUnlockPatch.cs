using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // prefix on GetActionsClass.GetAvailableActions for LootableContainer
    // interactions on a supply-drop crate. when the crate's airdrop root
    // GameObject carries our SupplyDropSpawner.SupplyDropTag component AND
    // the tag isn't Unlocked yet, replace the available actions with a
    // single "UNLOCK (X TC)" entry. once unlocked, the prefix bails and
    // vanilla's "Open" prompt runs as normal.
    //
    // gates loot access behind a TC spend - same UX as a wallbuy purchase,
    // applied to a container instead of an item dispenser. unlock price is
    // stored on the tag (SupplyDropTag.OriginalUnlockCost / CurrentUnlockCost,
    // default 1500). re-rolls are a flat RerollPrice (1000 TC) regardless
    // of how many times the player has already re-rolled this crate.
    //
    // hook chain (matches the wallbuy patches):
    //   GetActionsClass.GetAvailableActions(owner, interactive)
    //     -> if LootableContainer: smethod_16(owner, lc) builds the vanilla
    //        "Open" action set
    //   our prefix intercepts BEFORE smethod_16 runs and either returns the
    //   unlock action (locked path) or returns true to let vanilla run
    //   (unlocked path).
    internal sealed class SupplyDropUnlockPatch : ModulePatch
    {
        // flat re-roll cost. independent of OriginalUnlockCost so the two
        // prices can be tuned separately (e.g. cheap unlocks + expensive
        // re-rolls, or vice versa).
        public const int RerollPrice = 1000;

        protected override MethodBase GetTargetMethod()
        {
            // same target-selector the wallbuy patches use - filters by name,
            // arg count, and the GamePlayerOwner first-param so we don't
            // accidentally bind to the hideout/AreaData overload at line 911
            // of GetActionsClass.
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
            LootableContainer lc = interactive as LootableContainer;
            if (lc == null) return true; // not a container - let vanilla run

            // walk up to the airdrop root looking for our tag. supply-drop
            // crates are LootableContainer children of an AirdropSynchronizableObject;
            // SupplyDropSpawner attaches the tag to the airdrop root.
            SupplyDropSpawner.SupplyDropTag tag = lc.GetComponentInParent<SupplyDropSpawner.SupplyDropTag>();
            if (tag == null) return true; // not a supply drop, just a regular container
            if (tag.Unlocked) return true; // already paid; vanilla "Open" runs + postfix appends re-roll

            try { __result = BuildUnlockAction(owner, lc, tag); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SupplyDrop] unlock action build threw: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false; // skip vanilla - only our unlock action is offered while locked
        }

        // postfix: once the crate is unlocked, append a "RE-ROLL (X TC)"
        // action to whatever vanilla built (typically just "Open"). re-roll
        // re-randomizes loot, preserves the TarCoin stack, re-locks the
        // crate at a reduced unlock cost.
        [PatchPostfix]
        private static void Postfix(GamePlayerOwner owner, object interactive, ref ActionsReturnClass __result)
        {
            try
            {
                LootableContainer lc = interactive as LootableContainer;
                if (lc == null) return;
                SupplyDropSpawner.SupplyDropTag tag = lc.GetComponentInParent<SupplyDropSpawner.SupplyDropTag>();
                if (tag == null) return;
                if (!tag.Unlocked) return; // locked state is handled by prefix
                if (owner?.Player == null) return;
                if (__result == null) return;
                if (__result.Actions == null) __result.Actions = new List<ActionsTypesClass>();

                // re-roll price is flat RerollPrice (1000 TC) regardless
                // of how many times the player has already re-rolled. unlock
                // cost stays at OriginalUnlockCost on re-lock, so there's no
                // discount-on-repeat - every cycle is fully priced.
                int rerollPrice = RerollPrice;
                int balance = TarCoinWallet.Balance(owner.Player);
                bool canAfford = balance >= rerollPrice;
                string label = canAfford
                    ? $"RE-ROLL ({rerollPrice} TC)"
                    : $"RE-ROLL ({rerollPrice} TC) - need {rerollPrice - balance} more";

                __result.Actions.Add(new ActionsTypesClass
                {
                    Name = label,
                    Disabled = !canAfford,
                    Action = canAfford ? (Action)(() => OnReroll(owner, lc, tag, rerollPrice)) : null,
                });
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SupplyDrop] re-roll action append threw: {ex.Message}");
            }
        }

        private static ActionsReturnClass BuildUnlockAction(GamePlayerOwner owner, LootableContainer lc, SupplyDropSpawner.SupplyDropTag tag)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            int price = tag.CurrentUnlockCost;
            int balance = TarCoinWallet.Balance(owner.Player);
            bool canAfford = balance >= price;

            string label = canAfford
                ? $"UNLOCK ({price} TC)"
                : $"UNLOCK ({price} TC) - need {price - balance} more";

            result.Actions.Add(new ActionsTypesClass
            {
                Name = label,
                Disabled = !canAfford,
                Action = canAfford ? (Action)(() => OnUnlock(owner, lc, tag, price)) : null,
            });
            return result;
        }

        // refresh the player's action prompt. without this, the UI keeps
        // showing the action set that was current when the player first
        // looked at the crate - so e.g. "UNLOCK" stays visible after the
        // player clicks it until they look away + back. calling
        // InteractionsChangedHandler re-queries GetAvailableActions and
        // pushes the new result into AvailableInteractionState, which the
        // action panel binds to reactively (see GamePlayerOwner.cs line
        // 633-665 in the decompile).
        private static void RefreshPrompt(GamePlayerOwner owner)
        {
            try { owner?.InteractionsChangedHandler(); }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"[SupplyDrop] RefreshPrompt threw: {ex.Message}"); }
        }

        private static void OnUnlock(GamePlayerOwner owner, LootableContainer lc, SupplyDropSpawner.SupplyDropTag tag, int price)
        {
            try
            {
                if (owner?.Player == null) return;
                if (tag.Unlocked) return; // race-guard: re-click between frames
                if (!TarCoinWallet.TrySpend(owner.Player, price))
                {
                    Plugin.LogSource?.LogInfo("[SupplyDrop] unlock failed - not enough TarCoins.");
                    return;
                }
                tag.Unlocked = true;
                PlayWallbuyBuyClipAt(lc != null ? lc.transform.position : tag.transform.position);
                Plugin.LogSource?.LogInfo($"[SupplyDrop] crate unlocked for {price} TC. next interaction shows vanilla Open + RE-ROLL.");
                RefreshPrompt(owner);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SupplyDrop] OnUnlock threw: {ex.Message}");
            }
        }

        private static void OnReroll(GamePlayerOwner owner, LootableContainer lc, SupplyDropSpawner.SupplyDropTag tag, int price)
        {
            try
            {
                if (owner?.Player == null) return;
                if (!tag.Unlocked)
                {
                    Plugin.LogSource?.LogInfo("[SupplyDrop] re-roll ignored - crate already locked.");
                    return;
                }
                if (!TarCoinWallet.TrySpend(owner.Player, price))
                {
                    Plugin.LogSource?.LogInfo("[SupplyDrop] re-roll failed - not enough TarCoins.");
                    return;
                }

                // re-randomize the crate's loot in place. TarCoin stack is
                // preserved by SupplyDropLootTable.RerollLoot - the player
                // can't cheese this for extra coins.
                InventoryController inv = owner.Player.InventoryController;
                if (inv != null && tag.CrateItem != null)
                {
                    SupplyDropLootTable.RerollLoot(tag.CrateItem, inv, tag.WaveCountAtSpawn);
                }
                else
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] re-roll: missing inv/crateItem; loot grid unchanged.");
                }

                // re-lock with the SAME unlock cost as last time. no
                // discount-on-repeat - the player pays the full unlock fee
                // again to access the re-rolled loot. re-roll cost is the
                // flat RerollPrice; unlock cost stays at OriginalUnlockCost
                // throughout the crate's lifetime.
                tag.CurrentUnlockCost = tag.OriginalUnlockCost;
                tag.Unlocked = false;
                PlayWallbuyBuyClipAt(lc != null ? lc.transform.position : tag.transform.position);
                Plugin.LogSource?.LogInfo($"[SupplyDrop] crate re-rolled for {price} TC. now locked again at {tag.CurrentUnlockCost} TC.");
                RefreshPrompt(owner);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SupplyDrop] OnReroll threw: {ex.Message}");
            }
        }

        // borrows the wallbuy "ka-ching" AudioClip from any spawned wallbuy
        // and plays it as a one-shot at the crate's position via
        // AudioSource.PlayClipAtPoint (Unity spawns a temporary AudioSource
        // GameObject for us, plays the clip, then destroys itself when the
        // clip ends - no lifetime management needed).
        //
        // we look at Mp43Wallbuy specifically because that's the component
        // we added BuySoundClip to. all wallbuys ship with a "buysound"
        // AudioSource child in their bundle, but only one needs to be
        // resolvable for the cue to work.
        //
        // if no wallbuy is present yet (e.g. F8 test spawn before zombies
        // mode armed wallbuys), silently no-op.
        private static void PlayWallbuyBuyClipAt(Vector3 position)
        {
            try
            {
                Mp43Wallbuy wallbuy = UnityEngine.Object.FindObjectOfType<Mp43Wallbuy>();
                AudioClip clip = wallbuy?.BuySoundClip;
                if (clip == null)
                {
                    Plugin.LogSource?.LogInfo("[SupplyDrop] no wallbuy BuySoundClip available; unlock SFX skipped.");
                    return;
                }
                AudioSource.PlayClipAtPoint(clip, position, 1f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[SupplyDrop] PlayWallbuyBuyClipAt threw: {ex.Message}");
            }
        }
    }
}
