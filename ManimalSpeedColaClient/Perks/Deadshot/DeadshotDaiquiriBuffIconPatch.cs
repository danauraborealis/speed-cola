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
    // marker type keyed in EFTHardSettings.StaticIcons.EffectIcons.EffectIcons
    // so the in-raid Effects panel renders the Deadshot Daiquiri sprite. mirrors
    // SpeedColaBuffIconMarker structure.
    public sealed class DeadshotDaiquiriBuffIconMarker
    {
    }

    // postfix on the variation builder that the Effects panel uses for a given
    // stimulator. when the stimulator's Name matches "deadshotdaiquiri", swap
    // the variations Type to our marker so EffectsPanel's icon lookup hits
    // the custom-icon registration in DeadshotDaiquiriEffectIconRegistrationPatch.
    public class DeadshotDaiquiriBuffIconPatch : ModulePatch
    {
        private const string BuffName = DeadshotDaiquiriBuffState.BuffName;
        private const string DisplayName = "Deadshot Daiquiri";

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
                {
                    LogSeenName(foundName);
                    return;
                }

                int swapped = 0;
                int relabeled = 0;
                foreach (var variation in __result)
                {
                    if (variation.Type == typeof(IPlayerBuff))
                    {
                        variation.Type = typeof(DeadshotDaiquiriBuffIconMarker);
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
                Plugin.LogSource?.LogInfo($"[Deadshot] icon swap: name='{foundName}', variations swapped={swapped}, buffs relabeled={relabeled}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Deadshot] icon variation swap failed: {ex.Message}");
            }
        }

        private static readonly System.Collections.Generic.HashSet<string> _seenNames = new System.Collections.Generic.HashSet<string>();
        private static void LogSeenName(string name)
        {
            if (name == null) return;
            lock (_seenNames)
            {
                if (_seenNames.Add(name))
                    Plugin.LogSource?.LogInfo($"[Deadshot] saw stimulator name='{name}' (not ours)");
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

    // registers the deadshot icon sprite under our marker type. fires on
    // EffectsPanel.Show postfix (idempotent - registers once and bails).
    public class DeadshotDaiquiriEffectIconRegistrationPatch : ModulePatch
    {
        // dedicated Deadshot icon (yellow skull + crosshair). lives in the
        // Assets folder alongside the other perk icons.
        private const string IconFileName = "dead_icon.png";

        private static Sprite _icon;
        private static bool _registeredCustom;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EffectsPanel), nameof(EffectsPanel.Show));

        [PatchPostfix]
        private static void Postfix() => EnsureRegistered();

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
                    icons[typeof(DeadshotDaiquiriBuffIconMarker)] = sprite;
                    _registeredCustom = true;
                    Plugin.LogSource?.LogInfo("[Deadshot] icon registered: custom sprite (placeholder = speedcola)");
                    return;
                }
                if (icons.TryGetValue(typeof(IPlayerBuff), out var fallback))
                {
                    icons[typeof(DeadshotDaiquiriBuffIconMarker)] = fallback;
                    _registeredCustom = true;
                    Plugin.LogSource?.LogWarning("[Deadshot] icon registered: FALLBACK to IPlayerBuff sprite");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Deadshot] icon registration failed: {ex.Message}");
            }
        }

        private static Sprite LoadSprite()
        {
            if (_icon != null) return _icon;
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(pluginDir)) return null;

                string iconPath = Path.Combine(pluginDir, "Assets", IconFileName);
                if (!File.Exists(iconPath))
                {
                    Plugin.LogSource?.LogWarning($"[Deadshot] icon not found at: {iconPath}");
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(iconPath);
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                {
                    Plugin.LogSource?.LogWarning($"[Deadshot] file at {iconPath} is not a PNG.");
                    return null;
                }

                var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
#pragma warning disable CS0618
                bool loaded = texture.LoadImage(bytes);
#pragma warning restore CS0618
                if (!loaded)
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "deadshot_icon";

                _icon = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                _icon.name = "deadshot_icon";
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Deadshot] icon load threw: {ex.Message}");
            }
            return _icon;
        }
    }
}
