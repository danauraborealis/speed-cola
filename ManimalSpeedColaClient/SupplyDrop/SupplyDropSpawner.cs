using System;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // mid-raid supply-drop spawner. mirrors what GClass2656.ParseAirdropContainerData
    // does on the network path - pulls an AirdropSynchronizableObject from the
    // pool, configures it as a static landed crate (no parachute / no falling
    // animation), creates an Airdrop supply crate item, populates its grid
    // with our loot table, then binds the LootableContainer to that item via
    // LootItem.CreateLootContainer.
    //
    // the resulting GameObject is the same prefab vanilla EFT uses when a
    // network-driven airdrop lands - which means the player can interact with
    // it through the standard "Open" prompt and the loot grid UI works
    // out-of-the-box.
    public static class SupplyDropSpawner
    {
        // base item tpl - the loot-bearing container itself. one of the
        // vanilla airdrop crate tpls; "Airdrop supply crate" is the generic
        // mixed-loot variant.
        public const string AirdropCrateTpl = "61a89e5445a2672acf66c877";

        // tag MonoBehaviour we drop on the spawned GameObject so an action
        // patch can identify "this LootableContainer is our supply drop"
        // and gate the open action behind a TarCoin spend.
        //
        // Unlocked starts false - SupplyDropUnlockPatch replaces the vanilla
        // "Open" action with "UNLOCK (X TC)" until the player pays. once
        // Unlocked flips true, the patch bails and vanilla's open prompt
        // runs normally + a postfix injects the "RE-ROLL" option.
        //
        // re-roll mechanic:
        //   - costs a flat SupplyDropUnlockPatch.RerollPrice (1000 TC)
        //   - re-rolls the crate's random loot (preserving the TarCoin stack
        //     so the player can't gamble for more coins)
        //   - re-locks the crate at OriginalUnlockCost (no compounding
        //     discount - every unlock is the full price)
        // CrateItem is stashed here so the re-roll action can find the loot
        // grid without walking the LootableContainer state.
        public sealed class SupplyDropTag : MonoBehaviour
        {
            public int OriginalUnlockCost = 1500;
            public int CurrentUnlockCost  = 1500;
            public bool Unlocked;
            public int WaveCountAtSpawn;
            public EFT.InventoryLogic.Item CrateItem;
        }

        // last-spawned drop, kept so we can despawn it when a new intermission
        // fires. cleared at raid teardown via ClearForNewRaid.
        private static AirdropSynchronizableObject _lastDrop;

        // accessor for DeathPerceptionEffectController and any other system
        // that needs to highlight / read the active crate. returns null when
        // no drop is active.
        public static GameObject GetCurrentDropGameObject()
        {
            return _lastDrop != null ? _lastDrop.gameObject : null;
        }

        // reset on raid start so a stale reference from a previous raid
        // doesn't get touched. ZombiesWaveController calls this in HookSpawner.
        public static void ClearForNewRaid()
        {
            _lastDrop = null;
            BossWeaponRegistry.ClearForNewRaid();
        }

        // public despawn entry point: returns the current drop to the pool
        // and nulls the reference. called by ZombiesWaveController right
        // before the next wave starts so the crate doesn't linger into
        // gameplay. safe to call when no drop exists (no-op).
        public static void DespawnCurrent()
        {
            if (_lastDrop == null) return;
            try
            {
                GameWorld gw = Singleton<GameWorld>.Instance;
                if (gw?.SynchronizableObjectLogicProcessor != null)
                {
                    gw.SynchronizableObjectLogicProcessor.ReturnToPool(_lastDrop);
                    Plugin.LogSource?.LogInfo("[SupplyDrop] crate despawned at intermission end.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[SupplyDrop] DespawnCurrent threw: {ex.Message}");
            }
            finally
            {
                _lastDrop = null;
            }
        }

        public static async Task<GameObject> SpawnAt(Vector3 position, int waveCount, int tarCoinCost = 1500)
        {
            try
            {
                Plugin.LogSource?.LogInfo($"[SupplyDrop] SpawnAt begin pos={position} wave={waveCount}");
                GameWorld gw = Singleton<GameWorld>.Instance;
                if (gw?.SynchronizableObjectLogicProcessor == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] no GameWorld/processor available; skipping spawn.");
                    return null;
                }

                // despawn the previous drop (if it's still around) so the
                // player only ever has one supply-drop hunt active. routes
                // through SyncObjectProcessorClass.ReturnToPool which handles
                // both the active-list removal and asset-pool recycling.
                if (_lastDrop != null)
                {
                    try
                    {
                        gw.SynchronizableObjectLogicProcessor.ReturnToPool(_lastDrop);
                        Plugin.LogSource?.LogInfo("[SupplyDrop] previous drop returned to pool.");
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"[SupplyDrop] ReturnToPool threw on previous drop: {ex.Message}");
                    }
                    finally
                    {
                        _lastDrop = null;
                    }
                }

                // ensure the airdrop bundle is loaded in the pool before we
                // ask the pool for an instance. SynchronizableObjectPath[type]
                // is already a ResourceKey - we pass it directly.
                if (!Singleton<PoolManagerClass>.Instantiated)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] PoolManagerClass not instantiated; aborting.");
                    return null;
                }
                var pathMap = ResourceKeyManagerAbstractClass.SynchronizableObjectPath;
                if (pathMap == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] ResourceKeyManagerAbstractClass.SynchronizableObjectPath is null; aborting.");
                    return null;
                }
                ResourceKey airdropKey = pathMap[SynchronizableObjectType.AirDrop];
                if (airdropKey == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] no ResourceKey for SynchronizableObjectType.AirDrop; aborting.");
                    return null;
                }
                Plugin.LogSource?.LogInfo($"[SupplyDrop] loading airdrop pool (key={airdropKey})...");
                await Singleton<PoolManagerClass>.Instance.LoadBundlesAndCreatePools(
                    PoolManagerClass.PoolsCategory.Raid,
                    PoolManagerClass.AssemblyType.Local,
                    new[] { airdropKey },
                    JobPriorityClass.Immediate,
                    null,
                    default(CancellationToken));
                Plugin.LogSource?.LogInfo("[SupplyDrop] pool loaded; taking instance from pool...");

                // pre-retain UsePrefab bundles for the consumables this crate
                // will contain (milk, MRE). without this, the food/drink
                // animation NREs when the player tries to eat the item -
                // EFT's PoolManager.CreateItemUsablePrefabAsync expects the
                // bundle to already be loaded. (same fix we applied to perk
                // drinks via PerkMachineBundleLoader.DrinkDependencyBundles.)
                await SupplyDropLootTable.WarmupUsePrefabBundlesAsync();

                AirdropSynchronizableObject airdrop =
                    gw.SynchronizableObjectLogicProcessor.TakeFromPool(SynchronizableObjectType.AirDrop) as AirdropSynchronizableObject;
                if (airdrop == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] TakeFromPool returned null/wrong type; airdrop bundle missing?");
                    return null;
                }

                airdrop.AirdropType = EAirdropType.Supply;
                airdrop.transform.position = position;
                airdrop.transform.rotation = Quaternion.identity;

                // hide the falling-animation parts. our supply drop just
                // appears on the ground; no parachute, no smoke, no flare.
                if (airdrop.Parachute != null) airdrop.Parachute.SetActive(false);
                if (airdrop.AirdropFlare != null) airdrop.AirdropFlare.SetActive(false);
                if (airdrop.AirdropDust != null) airdrop.AirdropDust.SetActive(false);

                // apply the default landed view so the right colliders + meshes
                // are enabled on the LootableContainer. (NewYear is the only
                // other option in the enum.)
                try { airdrop.ApplyAirdropViewType(AirdropSynchronizableObject.EAirdropViewType.Default); }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] ApplyAirdropViewType threw: {ex.Message}");
                }

                LootableContainer lc = airdrop.gameObject.GetComponentInChildren<LootableContainer>(includeInactive: true);
                if (lc == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] airdrop prefab has no LootableContainer child; aborting.");
                    return null;
                }
                lc.enabled = true;
                lc.Id = new MongoID(false).ToString();

                // GameWorld.RegisterWorldInteractionObject forwards to
                // World_0.RegisterWorldInteractionObject, where World_0 is
                // a lazy GetComponent<World>() on the GameWorld GameObject.
                // in some game modes (zombies / hideout-walk / practice raid
                // variants) that World component isn't attached, so the
                // property returns null and the forwarding call NREs.
                //
                // skip the register call when World_0 is null. the LC's own
                // collider + WorldInteractiveObject script still drive the
                // "Open" prompt; the world-interaction dictionary is used
                // for cross-system Id lookups (doors, switches) which a
                // one-off airdrop crate doesn't participate in.
                if (gw.World_0 != null)
                {
                    gw.RegisterWorldInteractionObject(lc);
                }
                else
                {
                    Plugin.LogSource?.LogInfo("[SupplyDrop] GameWorld.World_0 is null; skipping RegisterWorldInteractionObject. LC interaction still works via its own collider.");
                }

                // mint the crate item. its grid is what the player sees when
                // they open the container.
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] ItemFactoryClass missing; aborting.");
                    return null;
                }
                Player main = gw.MainPlayer;
                InventoryController inv = main?.InventoryController;
                string newId = inv != null ? ((IIdGenerator)inv).NextId : new MongoID(false).ToString();
                Item crate = factory.CreateItem(newId, AirdropCrateTpl, null);
                if (crate == null)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] CreateItem({AirdropCrateTpl}) returned null.");
                    return null;
                }

                // populate the crate's grid with loot rolled from
                // SupplyDropLootTable. add new items to the table's static
                // Entries list - no spawner changes needed. waveCount feeds
                // the table's rarity scaling: rarer items become more likely
                // as the wave count grows.
                if (inv != null) SupplyDropLootTable.PopulateLoot(crate, inv, waveCount);
                else Plugin.LogSource?.LogWarning("[SupplyDrop] no InventoryController; skipping loot population.");

                LootItem.CreateLootContainer(
                    lc,
                    crate,
                    crate.ShortName?.Localized(null) ?? "Supply Drop",
                    gw,
                    null);

                // mark static so the synchronizable-object processor doesn't
                // try to network-sync position updates for this drop.
                gw.SynchronizableObjectLogicProcessor.InitStaticObject(airdrop);

                // tag for the TC-gate action patch. carries the unlock cost,
                // unlocked state, current wave count (for re-roll loot
                // generation), and a reference to the crate item so re-roll
                // can re-populate the loot grid in place.
                SupplyDropTag tag = airdrop.gameObject.AddComponent<SupplyDropTag>();
                tag.OriginalUnlockCost = tarCoinCost;
                tag.CurrentUnlockCost  = tarCoinCost;
                tag.WaveCountAtSpawn   = waveCount;
                tag.CrateItem          = crate;

                // through-wall visibility on the crate is now driven by the
                // Death Perception perk effect controller (it outlines the
                // crate when the buff is active). the old GL.LINES beacon
                // was removed - the player has to actually be running DP
                // to see the crate at range.

                _lastDrop = airdrop;
                Plugin.LogSource?.LogInfo($"[SupplyDrop] spawned at {position} (cost={tarCoinCost} TC).");
                return airdrop.gameObject;
            }
            catch (Exception ex)
            {
                // log the full exception (type + message + stack trace) so we
                // can see exactly which line threw. ex.ToString() includes all
                // three plus any inner-exception chain.
                Plugin.LogSource?.LogError($"[SupplyDrop] SpawnAt threw:\n{ex}");
                return null;
            }
        }
    }
}
