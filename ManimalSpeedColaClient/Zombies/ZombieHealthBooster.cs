using System;
using System.Collections;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // adds bonus max HP to a bot, evenly distributed across its body parts.
    // body-part state lives in a protected Dictionary_0 field on the base
    // health controller (GClass3009<T>) - so this reflects in once and caches
    // the FieldInfo. HealthValue exposes method_0(cur, min, max) as a public
    // reset so we don't have to mutate the underlying ValueStruct ourselves.
    //
    // bonus distribution policy: bonus / 7 to each of the 7 body parts
    // (Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg). simple
    // and predictable - head still gets a fraction (rough fairness).
    public static class ZombieHealthBooster
    {
        // Dictionary_0 is a PROPERTY on GClass3009<T> (getter-only); the
        // backing field is Dictionary_0_1. our reflection used to look for
        // a field named "Dictionary_0" which doesn't exist - explains why
        // every call was logging "could not access body-part Dictionary_0"
        // and bailing. now we use PropertyInfo for the public property.
        private static PropertyInfo _dictionaryProperty;

        // linear progression: +100 HP per wave starting from wave 2.
        //   wave 1 -> 0
        //   wave 2 -> 100
        //   wave 3 -> 200
        //   wave 4 -> 300
        //   wave N -> (N - 1) * 100
        public static float BonusForWave(int wave)
        {
            if (wave <= 1) return 0f;
            return (wave - 1) * 100f;
        }

        public static void Apply(BotOwner bot, float bonusTotalHp)
        {
            if (bot == null) return;
            Apply(bot.GetPlayer, bonusTotalHp, bot.Profile?.Nickname);
        }

        // overload that takes a Player directly. modifies both the runtime
        // HealthController.Dictionary_0 (so damage processing uses the new max)
        // AND the persistent Profile.Health.BodyParts data (so the inventory
        // HP display reflects the boost - those two stores are independent;
        // modifying just one wasn't enough for Juggernog).
        public static void Apply(Player player, float bonusTotalHp, string label = "player")
        {
            if (player == null || bonusTotalHp <= 0f) return;
            try
            {
                if (player.HealthController == null) return;

                IDictionary dict = GetDictionary(player.HealthController);
                if (dict == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombieHealth] could not access body-part Dictionary_0 via reflection; skipping boost.");
                    return;
                }

                // count parts so we divide evenly. profiles typically populate
                // all 7 parts but compute from actual dict to be safe.
                int partCount = 0;
                foreach (object _ in dict.Values) partCount++;
                if (partCount <= 0) return;
                // ceil so each body part's max ends up as a whole number on
                // the player's HP UI (no ugly 21.43/21.43 decimals). costs a
                // few extra HP versus the configured total - e.g. 150/7=21.43
                // ceils to 22 per part = 154 total, 4 over budget but clean.
                float perPart = Mathf.Ceil(bonusTotalHp / partCount);

                // 1. runtime HealthController.Dictionary_0 - what the damage
                //    pipeline reads. each entry's Health is a HealthValue;
                //    method_0(cur, min, max) is the public full reset.
                foreach (DictionaryEntry kv in dict)
                {
                    object stateObj = kv.Value;
                    if (stateObj == null) continue;
                    FieldInfo healthField = AccessTools.Field(stateObj.GetType(), "Health");
                    HealthValue hv = healthField?.GetValue(stateObj) as HealthValue;
                    if (hv == null) continue;
                    float newMax = hv.Maximum + perPart;
                    hv.method_0(newMax, 0f, newMax);
                }

                // 2. Profile.Health.BodyParts[part].Health.Maximum/.Current.
                //    this is the persistent profile data; the UI (inventory
                //    HP numbers + the in-raid body silhouette) reads off of
                //    it for the player. without this, the gameplay HP works
                //    but the player just sees the un-boosted numbers.
                var profileHealth = player.Profile?.Health;
                if (profileHealth?.BodyParts != null)
                {
                    foreach (var entry in profileHealth.BodyParts)
                    {
                        var partHealth = entry.Value?.Health;
                        if (partHealth == null) continue;
                        partHealth.Maximum += perPart;
                        partHealth.Current = partHealth.Maximum;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombieHealth] Apply threw for {label}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // mirror of Apply that SUBTRACTS the per-part HP. used by the Quick
        // Revive perk-wipe path so a triggered auto-revive strips Juggernog's
        // +150 max HP just like it strips the buff. clamps Maximum to >=1 and
        // Current to the new Maximum so we never end up with current > max.
        public static void Reverse(Player player, float bonusTotalHp, string label = "player")
        {
            if (player == null || bonusTotalHp <= 0f) return;
            try
            {
                if (player.HealthController == null) return;

                IDictionary dict = GetDictionary(player.HealthController);
                if (dict == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombieHealth] could not access body-part Dictionary_0 via reflection; skipping reverse.");
                    return;
                }

                int partCount = 0;
                foreach (object _ in dict.Values) partCount++;
                if (partCount <= 0) return;
                // must match the Apply policy (ceil) so reversal cancels out cleanly.
                float perPart = Mathf.Ceil(bonusTotalHp / partCount);

                // 1. runtime Dictionary_0 - new max + clamp current to it.
                foreach (DictionaryEntry kv in dict)
                {
                    object stateObj = kv.Value;
                    if (stateObj == null) continue;
                    FieldInfo healthField = AccessTools.Field(stateObj.GetType(), "Health");
                    HealthValue hv = healthField?.GetValue(stateObj) as HealthValue;
                    if (hv == null) continue;
                    float newMax = Mathf.Max(1f, hv.Maximum - perPart);
                    float newCur = Mathf.Min(hv.Current, newMax);
                    hv.method_0(newCur, 0f, newMax);
                }

                // 2. Profile.Health.BodyParts mirror.
                var profileHealth = player.Profile?.Health;
                if (profileHealth?.BodyParts != null)
                {
                    foreach (var entry in profileHealth.BodyParts)
                    {
                        var partHealth = entry.Value?.Health;
                        if (partHealth == null) continue;
                        partHealth.Maximum = Mathf.Max(1f, partHealth.Maximum - perPart);
                        if (partHealth.Current > partHealth.Maximum) partHealth.Current = partHealth.Maximum;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombieHealth] Reverse threw for {label}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static IDictionary GetDictionary(object healthController)
        {
            if (_dictionaryProperty == null)
            {
                // GClass3009<T>.Dictionary_0 (property, NOT a field) - walk up
                // the type chain so we find it on the generic base, not the
                // derived ActiveHealthController.
                Type t = healthController.GetType();
                while (t != null && _dictionaryProperty == null)
                {
                    _dictionaryProperty = t.GetProperty("Dictionary_0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    t = t.BaseType;
                }
            }
            return _dictionaryProperty?.GetValue(healthController) as IDictionary;
        }
    }
}
