using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // CoD Zombies-style scoring. every shot the player lands on an infected
    // zombie awards TarCoins to the player's secured container:
    //
    //   non-lethal hit: 10
    //   lethal limb:    50    (arms / legs)
    //   lethal torso:   60    (chest / stomach minus neck colliders)
    //   lethal neck:    70    (NeckFront / NeckBack colliders)
    //   lethal head:   100
    //   lethal explosive: 50
    //   lethal melee:  130
    //
    // patches ActiveHealthController.ApplyDamage. captures IsAlive in the
    // prefix and compares against the postfix to detect "this damage was
    // the killing blow", so we don't double-count later body-part rot or
    // re-fires of the same shot's bleed ticks.
    //
    // gated on Plugin.ZombiesMode so normal raids don't get TarCoin showers.
    internal sealed class TarCoinScorePatch : ModulePatch
    {
        public const string TarCoinTpl = "6a0b45f6682ea02b4ca45907";

        // base point table from CoD Zombies with a flat +50% bonus on top:
        // non-lethal 10 -> 15, limb 50 -> 75, explosive 50 -> 75,
        // torso 60 -> 90, neck 70 -> 105, head 100 -> 150, melee 130 -> 195.
        private const int PointsNonLethal     = 15;
        private const int PointsLethalLimb    = 75;
        private const int PointsLethalExplo   = 75;
        private const int PointsLethalTorso   = 90;
        private const int PointsLethalNeck    = 105;
        private const int PointsLethalHead    = 150;
        private const int PointsLethalMelee   = 195;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));
        }

        [PatchPrefix]
        private static void Prefix(ActiveHealthController __instance, out bool __state)
        {
            // capture pre-damage alive state. we can't trust IsAlive in the
            // postfix alone because someone could already be dead and this
            // damage is from a delayed bleed - we'd then mis-score it as a
            // kill. wasAlive && !isAliveNow is the strict "killing blow".
            __state = false;
            try { __state = __instance != null && __instance.IsAlive; } catch { }
        }

        [PatchPostfix]
        private static void Postfix(ActiveHealthController __instance, EBodyPart bodyPart, float damage, DamageInfoStruct damageInfo, bool __state)
        {
            try
            {
                if (!Plugin.ZombiesMode) return;
                if (__instance == null) return;

                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return;

                IPlayerOwner aggressorOwner = damageInfo.Player;
                IPlayer aggressor = aggressorOwner?.iPlayer;
                if (aggressor == null) return;
                if (aggressor.ProfileId != main.ProfileId) return; // only main player's hits

                Player target = __instance.Player;
                if (target == null) return;
                WildSpawnType role = target.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!role.IsInfected()) return;

                bool wasAlive = __state;
                bool isAliveNow = __instance.IsAlive;
                bool lethal = wasAlive && !isAliveNow;

                int points = CalculatePoints(damageInfo, bodyPart, lethal);
                if (points <= 0) return;

                // shotgun bonus: MP-43 pellets distribute damage across multiple
                // ballistic colliders, so each hit on the same zombie tends to
                // score as "non-lethal" 15-point chips with only the very last
                // pellet getting the lethal bonus. quadrupling earned points
                // when the weapon is the MP-43 papers over the shortchange
                // without needing to rework the per-pellet damage accounting.
                Item weapon = damageInfo.Weapon;
                if (weapon != null && weapon.TemplateId == Mp43WallbuyActionPatch.Mp43ItemTpl)
                    points *= 4;

                TarCoinPouch.Award(main, points);

                // power-up drop: only on lethal hits, rolled in the spawner.
                // we pass the zombie's head bone position as the candidate
                // spawn point so the pickup floats roughly where the zombie's
                // head was at death (rather than at the feet / center).
                if (lethal)
                {
                    Vector3 headPos = ResolveHeadPosition(target);
                    MaxAmmoSpawner.TryRoll(headPos);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[TarCoinScore] postfix threw: {ex.Message}");
            }
        }

        // best-effort lookup of the bot's head bone for the power-up spawn
        // anchor. falls back to body position + a constant upward offset if
        // the head bone can't be resolved (different bot rigs use different
        // bone names; we don't want a NRE here to kill the kill-handler).
        private static Vector3 ResolveHeadPosition(Player target)
        {
            try
            {
                if (target == null) return Vector3.zero;
                // PlayerBones.Head is the canonical head-bone reference on
                // EFT.Player; falls back to a 1.7m vertical offset if null.
                Transform head = target.PlayerBones?.Head?.Original;
                if (head != null) return head.position;
                return target.Position + Vector3.up * 1.7f;
            }
            catch
            {
                return target != null ? target.Position + Vector3.up * 1.7f : Vector3.zero;
            }
        }

        private static int CalculatePoints(DamageInfoStruct dmg, EBodyPart bodyPart, bool lethal)
        {
            if (!lethal) return PointsNonLethal;

            EDamageType type = dmg.DamageType;

            // melee overrides body part for points purposes (CoD-style: pap'd
            // melee is the highest reward regardless of where the swing lands).
            if ((type & EDamageType.Melee) != 0) return PointsLethalMelee;

            // explosive (frag, mine, generic explosion) is its own bucket.
            if ((type & (EDamageType.Explosion | EDamageType.GrenadeFragment | EDamageType.Landmine)) != 0)
                return PointsLethalExplo;

            // throat / nape colliders. EFT's body-part enum collapses both
            // into Head, so we look at the more specific BodyPartColliderType.
            EBodyPartColliderType collider = dmg.BodyPartColliderType;
            if (collider == EBodyPartColliderType.NeckFront || collider == EBodyPartColliderType.NeckBack)
                return PointsLethalNeck;

            // headshot (any head collider that wasn't neck).
            if (bodyPart == EBodyPart.Head) return PointsLethalHead;

            // torso bucket.
            if (bodyPart == EBodyPart.Chest || bodyPart == EBodyPart.Stomach) return PointsLethalTorso;

            // arms / legs.
            if (bodyPart == EBodyPart.LeftArm || bodyPart == EBodyPart.RightArm ||
                bodyPart == EBodyPart.LeftLeg || bodyPart == EBodyPart.RightLeg)
                return PointsLethalLimb;

            // unknown body part on a lethal hit - default to limb tier so the
            // player isn't shorted points entirely.
            return PointsLethalLimb;
        }
    }

    // read-only / mutate-stack helpers for TarCoins in the secured container.
    // Balance sums every TarCoin stack in the container; TrySpend deducts a
    // price atomically across stacks. spend uses direct StackObjectsCount
    // mutation rather than a transaction - it's a single-player change in
    // the player's own secured container, and the visible side-effect is
    // just the number on the stack going down, which the UI re-reads when
    // the player opens the container.
    internal static class TarCoinWallet
    {
        public static int Balance(Player player)
        {
            if (player == null) return 0;
            try
            {
                CompoundItem secured = GetSecured(player);
                if (secured == null) return 0;
                int total = 0;
                foreach (Item item in secured.GetAllItems())
                {
                    if (item == null) continue;
                    if (item.TemplateId != TarCoinScorePatch.TarCoinTpl) continue;
                    total += item.StackObjectsCount;
                    if (total >= int.MaxValue / 2) break; // overflow guard
                }
                return total;
            }
            catch { return 0; }
        }

        public static bool TrySpend(Player player, int amount)
        {
            if (amount <= 0) return true;
            if (player == null) return false;
            try
            {
                if (Balance(player) < amount) return false;
                CompoundItem secured = GetSecured(player);
                if (secured == null) return false;

                int remaining = amount;
                foreach (Item item in secured.GetAllItems())
                {
                    if (remaining <= 0) break;
                    if (item == null) continue;
                    if (item.TemplateId != TarCoinScorePatch.TarCoinTpl) continue;
                    int take = Math.Min(remaining, item.StackObjectsCount);
                    item.StackObjectsCount -= take;
                    remaining -= take;
                }
                return remaining == 0;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[TarCoin] TrySpend threw: {ex.Message}");
                return false;
            }
        }

        private static CompoundItem GetSecured(Player player)
        {
            InventoryController inv = player.InventoryController as InventoryController;
            return inv?.Inventory.Equipment
                .GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem as CompoundItem;
        }
    }

    // helper: drops TarCoins into the player's secured container via a real
    // network transaction so the change is live in the inventory UI mid-raid
    // (auto-merges into any existing TarCoin stack, since QuickFindAppropriatePlace
    // picks an existing partial stack of the same tpl before allocating fresh
    // grid space).
    //
    // fire-and-forget from the damage postfix - we don't want to await on the
    // hot path of every bullet. transactions race on the same inventory
    // controller but EFT serializes them internally and the worst case is a
    // failed transaction (which we log).
    internal static class TarCoinPouch
    {
        public static void Award(Player player, int amount)
        {
            if (player == null || amount <= 0) return;
            _ = AwardAsync(player, amount);
        }

        private static async Task AwardAsync(Player player, int amount)
        {
            try
            {
                InventoryController inv = player.InventoryController as InventoryController;
                if (inv == null) return;

                CompoundItem secured = inv.Inventory.Equipment
                    .GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem as CompoundItem;
                if (secured == null) return;

                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null) return;

                // mint a fresh TarCoin item with our award amount as the
                // stack size. QuickFindAppropriatePlace will merge it into
                // the existing pile if there is one, else seat it in a free
                // cell.
                Item coin = factory.CreateItem(((IIdGenerator)inv).NextId, TarCoinScorePatch.TarCoinTpl, null);
                if (coin == null) return;
                coin.StackObjectsCount = amount;

                // give the coin a valid CurrentAddress before handing it to
                // the transaction system. we use the same fake-stash trick
                // the wallbuy patches use - a temporary 1x1 stash owned by
                // a throwaway TraderControllerClass. without an address,
                // InteractionsHandlerClass.Merge refuses the item.
                StashItemClass fakeStash = factory.CreateFakeStash(null);
                StashGridClass fakeGrid = new StashGridClass(
                    "tarcoin fake stash", 15, 15, false,
                    Array.Empty<ItemFilter>(), fakeStash);
                fakeStash.Grids[0] = fakeGrid;
                fakeStash.CurrentAddress = inv.CreateItemAddress();
                new TraderControllerClass(fakeStash, inv.ID, "tarcoin fake stash", false, EOwnerType.Profile);

                var seat = fakeGrid.AddAnywhere(coin, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[TarCoin] could not seat coin in fake stash: {seat.Error}");
                    return;
                }

                // route into the secured container. PickUp order is correct -
                // we're picking the item up off the fake stash and dropping
                // it onto an existing/empty cell in the secured container.
                IEnumerable<CompoundItem> targets = new[] { secured };
                var placement = InteractionsHandlerClass.QuickFindAppropriatePlace(
                    coin, inv, targets,
                    InteractionsHandlerClass.EMoveItemOrder.PickUp,
                    simulate: true);
                if (placement.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[TarCoin] no spot in secured container: {placement.Error}");
                    return;
                }

                IResult txResult = await inv.TryRunNetworkTransaction(placement, null);
                if (txResult.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[TarCoin] transaction failed: {txResult.Error}");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[TarCoin] AwardAsync threw: {ex.Message}");
            }
        }
    }
}
