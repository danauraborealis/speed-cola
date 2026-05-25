using System;
using System.IO;
using System.Reflection;
using EFT.HealthSystem;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // mirror of DeathPerceptionBuffIconPatch / JuggernogBuffIconPatch. swaps
    // the variation type used by EffectsPanel so our custom sprite is picked,
    // and registers revive_icon.png under the marker type once the panel
    // first opens. the underlying buff is a no-op StaminaRate-Value-0
    // (see ServerModFiles/db/CustomBuffs/QuickRevive.json) - the actual
    // auto-revive is driven C#-side by QuickReviveKillInterceptPatch which
    // checks QuickReviveBuffState.IsBuffActive() + QuickReviveState.

    public sealed class QuickReviveBuffIconMarker
    {
    }

    public class QuickReviveBuffIconPatch : ModulePatch
    {
        private const string BuffName = QuickReviveBuffState.BuffName;
        private const string DisplayName = "Quick Revive";

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass3018), nameof(GClass3018.GetDisplayableVariations));
        }

        [PatchPostfix]
        private static void Postfix(GInterface377 stimulator, ref GClass3056[] __result)
        {
            try
            {
                if (__result == null) return;
                string foundName = ReadStimulatorName(stimulator);
                if (!string.Equals(foundName, BuffName, StringComparison.OrdinalIgnoreCase))
                    return;

                int swapped = 0;
                int relabeled = 0;
                foreach (var variation in __result)
                {
                    if (variation.Type == typeof(IPlayerBuff))
                    {
                        variation.Type = typeof(QuickReviveBuffIconMarker);
                        swapped++;
                    }
                    if (variation.Buffs != null)
                    {
                        for (int i = 0; i < variation.Buffs.Count; i++)
                        {
                            var b = variation.Buffs[i];
                            if (b != null)
                            {
                                b.Text = DisplayName;
                                relabeled++;
                            }
                        }
                    }
                }
                Plugin.LogSource?.LogInfo($"[QuickRevive] icon swap: name='{foundName}', variations swapped={swapped}, buffs relabeled={relabeled}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[QuickRevive] icon variation swap failed: {ex.Message}");
            }
        }

        private static string ReadStimulatorName(GInterface377 stimulator)
        {
            if (stimulator == null) return null;
            var nameProperty = stimulator.GetType().GetProperty(
                "Name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return nameProperty?.GetValue(stimulator) as string;
        }
    }

    // registers Assets/revive_icon.png under QuickReviveBuffIconMarker
    // on first EffectsPanel.Show. idempotent registration with a fallback
    // to the generic IPlayerBuff sprite if the PNG fails to decode.
    public class QuickReviveEffectIconRegistrationPatch : ModulePatch
    {
        private static Sprite _icon;
        private static bool _registeredCustom;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EffectsPanel), nameof(EffectsPanel.Show));

        [PatchPostfix]
        private static void Postfix() => EnsureRegistered();

        // public so Plugin.Update's pre-registration poll can call it before
        // the player ever opens the inventory health screen.
        public static void EnsureRegistered()
        {
            try
            {
                var icons = EFTHardSettings.Instance?.StaticIcons?.EffectIcons?.EffectIcons;
                if (icons == null) return;
                if (_registeredCustom) return;

                Sprite sprite = LoadSprite();
                if (sprite != null)
                {
                    icons[typeof(QuickReviveBuffIconMarker)] = sprite;
                    _registeredCustom = true;
                    Plugin.LogSource?.LogInfo("[QuickRevive] icon registered: custom sprite");
                    return;
                }
                if (icons.TryGetValue(typeof(IPlayerBuff), out var fallback))
                {
                    icons[typeof(QuickReviveBuffIconMarker)] = fallback;
                    _registeredCustom = true; // latch even on fallback so the per-frame poll doesn't spam.
                    Plugin.LogSource?.LogWarning("[QuickRevive] icon registered: FALLBACK to IPlayerBuff sprite (custom PNG failed to load)");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[QuickRevive] icon registration failed: {ex.Message}");
            }
        }

        private static Sprite LoadSprite()
        {
            if (_icon != null) return _icon;
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(pluginDir))
                {
                    Plugin.LogSource?.LogWarning("[QuickRevive] plugin directory could not be resolved.");
                    return null;
                }

                string iconPath = Path.Combine(pluginDir, "Assets", "revive_icon.png");
                if (!File.Exists(iconPath))
                {
                    Plugin.LogSource?.LogWarning($"[QuickRevive] icon not found at: {iconPath}");
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(iconPath);
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                {
                    Plugin.LogSource?.LogWarning($"[QuickRevive] file at {iconPath} is not a PNG (header: {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}). Resave as PNG.");
                    return null;
                }

                var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
#pragma warning disable CS0618
                bool loaded = texture.LoadImage(bytes);
#pragma warning restore CS0618
                if (!loaded)
                {
                    Plugin.LogSource?.LogWarning($"[QuickRevive] Texture2D.LoadImage returned false. size={bytes.Length}");
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "revive_icon";
                Plugin.LogSource?.LogInfo($"[QuickRevive] texture decoded: {texture.width}x{texture.height}");

                _icon = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                _icon.name = "revive_icon";
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[QuickRevive] icon load threw: {ex.GetType().Name}: {ex.Message}");
            }
            return _icon;
        }
    }
}
