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
    // marker type keyed in EFTHardSettings.StaticIcons.EffectIcons.EffectIcons so
    // the in-raid Effects panel renders our custom sprite instead of the generic
    // buff icon. mirrors RakukajaBuffIconMarker from MitsuruMod.
    public sealed class SpeedColaBuffIconMarker
    {
    }

    // postfix on the variation builder that the Effects panel uses for a given
    // stimulator. when the stimulator's Name matches "speedcola", swap the
    // variations Type to our marker so EffectsPanel's icon lookup hits the
    // custom-icon registration in SpeedColaEffectIconRegistrationPatch.
    public class SpeedColaBuffIconPatch : ModulePatch
    {
        private const string BuffName = WeaponSpeedBuffState.BuffName;
        private const string DisplayName = "Speed Cola";

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
                    // log non-matches once per name so we can see what the
                    // stimulator actually calls itself if "speedcola" lookup
                    // ever stops working.
                    LogSeenName(foundName);
                    return;
                }

                int swapped = 0;
                int relabeled = 0;
                foreach (var variation in __result)
                {
                    if (variation.Type == typeof(IPlayerBuff))
                    {
                        variation.Type = typeof(SpeedColaBuffIconMarker);
                        swapped++;
                    }
                    // rewrite each contained buff's display text so the
                    // mouseover tooltip says "Speed Cola" instead of the
                    // localized BuffType name ("Stamina rate" with our
                    // marker BuffType).
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
                Plugin.LogSource?.LogInfo($"[SpeedCola] icon swap: name='{foundName}', variations swapped={swapped}, buffs relabeled={relabeled}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] icon variation swap failed: {ex.Message}");
            }
        }

        private static readonly System.Collections.Generic.HashSet<string> _seenNames = new System.Collections.Generic.HashSet<string>();
        private static void LogSeenName(string name)
        {
            if (name == null) return;
            lock (_seenNames)
            {
                if (_seenNames.Add(name))
                    Plugin.LogSource?.LogInfo($"[SpeedCola] saw stimulator name='{name}' (not ours)");
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

    // registers the speedcola_icon.png sprite under our marker type so the
    // Effects panel's icon dictionary returns it. fires on EffectsPanel.Show
    // postfix (idempotent - registers once and bails on subsequent calls).
    public class SpeedColaEffectIconRegistrationPatch : ModulePatch
    {
        private static Sprite _icon;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EffectsPanel), nameof(EffectsPanel.Show));

        [PatchPostfix]
        private static void Postfix() => EnsureRegistered();

        private static bool _registeredCustom;
        // public so Plugin.Update's pre-registration poll can call it before
        // the player ever opens the inventory health screen - otherwise the
        // HUD buff strip renders blank icons until EffectsPanel.Show fires.
        public static void EnsureRegistered()
        {
            try
            {
                var icons = EFTHardSettings.Instance?.StaticIcons?.EffectIcons?.EffectIcons;
                if (icons == null) return;
                // already registered our custom sprite - nothing to do. (the
                // fallback path doesnt set _registeredCustom so we keep
                // retrying the PNG load on subsequent EffectsPanel.Show calls.)
                if (_registeredCustom) return;

                Sprite sprite = LoadSprite();
                if (sprite != null)
                {
                    icons[typeof(SpeedColaBuffIconMarker)] = sprite;
                    _registeredCustom = true;
                    Plugin.LogSource?.LogInfo("[SpeedCola] icon registered: custom sprite");
                    return;
                }
                // fallback: reuse the generic player-buff icon so something shows
                // even if our PNG failed to load. latch _registeredCustom so the
                // per-frame Plugin.Update poll doesn't keep retrying and spamming
                // the log - if the PNG was missing at first attempt it's almost
                // certainly missing for the whole session (deploy issue).
                if (icons.TryGetValue(typeof(IPlayerBuff), out var fallback))
                {
                    icons[typeof(SpeedColaBuffIconMarker)] = fallback;
                    _registeredCustom = true;
                    Plugin.LogSource?.LogWarning("[SpeedCola] icon registered: FALLBACK to IPlayerBuff sprite (custom PNG failed to load)");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] icon registration failed: {ex.Message}");
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
                    Plugin.LogSource?.LogWarning("[SpeedCola] plugin directory could not be resolved.");
                    return null;
                }

                string iconPath = Path.Combine(pluginDir, "Assets", "speedcola_icon.png");
                if (!File.Exists(iconPath))
                {
                    Plugin.LogSource?.LogWarning($"[SpeedCola] icon not found at: {iconPath}");
                    return null;
                }

                long size = new FileInfo(iconPath).Length;
                byte[] bytes = File.ReadAllBytes(iconPath);
                Plugin.LogSource?.LogInfo($"[SpeedCola] icon file read: path='{iconPath}', size={size}, bytesRead={bytes.Length}");

                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                {
                    Plugin.LogSource?.LogWarning($"[SpeedCola] file at {iconPath} is not a PNG (header: {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}). Resave as PNG.");
                    return null;
                }

                // Texture2D.LoadImage is the legacy instance method - some EFT
                // builds resolve it where the static ImageConversion.LoadImage
                // doesnt. start with a 1x1 texture; LoadImage replaces both the
                // pixel data AND the dimensions.
                var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
#pragma warning disable CS0618
                bool loaded = texture.LoadImage(bytes);
#pragma warning restore CS0618
                if (!loaded)
                {
                    Plugin.LogSource?.LogWarning($"[SpeedCola] Texture2D.LoadImage returned false. size={bytes.Length}");
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "speedcola_icon";
                Plugin.LogSource?.LogInfo($"[SpeedCola] texture decoded: {texture.width}x{texture.height}");

                _icon = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                _icon.name = "speedcola_icon";
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] icon load threw: {ex.GetType().Name}: {ex.Message}");
            }
            return _icon;
        }
    }
}
