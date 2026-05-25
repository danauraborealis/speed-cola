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
    // mirror of JuggernogBuffIconPatch but for Staminup. swaps the variation
    // type used by EffectsPanel so our custom sprite is picked, and registers
    // staminup_icon.png under the marker type once the panel first opens.

    public sealed class StaminupBuffIconMarker
    {
    }

    public class StaminupBuffIconPatch : ModulePatch
    {
        public const string BuffName = "staminup";
        private const string DisplayName = "Staminup";

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
                int collapsed = 0;
                foreach (var variation in __result)
                {
                    if (variation.Type == typeof(IPlayerBuff))
                    {
                        variation.Type = typeof(StaminupBuffIconMarker);
                        swapped++;
                    }
                    // collapse the variation's Buffs list to a single entry
                    // labelled "Staminup". staminup.json defines 6 stat buffs
                    // (one per skill/stat), so without this the mouseover
                    // tooltip would print "Staminup" 6 times. SpeedCola and
                    // Juggernog don't hit this because their server-side
                    // buff configs each ship as a single entry.
                    if (variation.Buffs != null && variation.Buffs.Count > 0)
                    {
                        var keep = variation.Buffs[0];
                        if (keep != null) keep.Text = DisplayName;
                        while (variation.Buffs.Count > 1)
                            variation.Buffs.RemoveAt(variation.Buffs.Count - 1);
                        collapsed++;
                    }
                }
                Plugin.LogSource?.LogInfo($"[Staminup] icon swap: name='{foundName}', variations swapped={swapped}, collapsed={collapsed}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Staminup] icon variation swap failed: {ex.Message}");
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

    // registers Assets/staminup_icon.png under StaminupBuffIconMarker on first
    // EffectsPanel.Show. mirrors JuggernogEffectIconRegistrationPatch -
    // idempotent registration with a fallback to the generic IPlayerBuff
    // sprite if the PNG fails to decode.
    public class StaminupEffectIconRegistrationPatch : ModulePatch
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
                    icons[typeof(StaminupBuffIconMarker)] = sprite;
                    _registeredCustom = true;
                    Plugin.LogSource?.LogInfo("[Staminup] icon registered: custom sprite");
                    return;
                }
                if (icons.TryGetValue(typeof(IPlayerBuff), out var fallback))
                {
                    icons[typeof(StaminupBuffIconMarker)] = fallback;
                    _registeredCustom = true; // latch even on fallback so the per-frame poll doesn't spam.
                    Plugin.LogSource?.LogWarning("[Staminup] icon registered: FALLBACK to IPlayerBuff sprite (custom PNG failed to load)");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Staminup] icon registration failed: {ex.Message}");
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
                    Plugin.LogSource?.LogWarning("[Staminup] plugin directory could not be resolved.");
                    return null;
                }

                string iconPath = Path.Combine(pluginDir, "Assets", "staminup_icon.png");
                if (!File.Exists(iconPath))
                {
                    Plugin.LogSource?.LogWarning($"[Staminup] icon not found at: {iconPath}");
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(iconPath);
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                {
                    Plugin.LogSource?.LogWarning($"[Staminup] file at {iconPath} is not a PNG (header: {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}). Resave as PNG.");
                    return null;
                }

                var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
#pragma warning disable CS0618
                bool loaded = texture.LoadImage(bytes);
#pragma warning restore CS0618
                if (!loaded)
                {
                    Plugin.LogSource?.LogWarning($"[Staminup] Texture2D.LoadImage returned false. size={bytes.Length}");
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "staminup_icon";
                Plugin.LogSource?.LogInfo($"[Staminup] texture decoded: {texture.width}x{texture.height}");

                _icon = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                _icon.name = "staminup_icon";
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Staminup] icon load threw: {ex.GetType().Name}: {ex.Message}");
            }
            return _icon;
        }
    }
}
