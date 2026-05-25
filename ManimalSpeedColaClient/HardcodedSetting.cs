using System;

namespace Manimal.SpeedCola
{
    // mimics the slice of BepInEx ConfigEntry<T> that consumer code actually
    // touches - .Value (read-only) and a never-firing SettingChanged event.
    //
    // used to retire a knob from F12 without rewriting every consumer:
    // - we stop calling Config.Bind(...) for that knob (so it never enters
    //   the .cfg file and disappears from ConfigurationManager)
    // - we seed a HardcodedSetting<T> with the value we want to bake in
    // - every consumer that read Plugin.X.Value or subscribed to
    //   Plugin.X.SettingChanged keeps working unchanged (Value reads the
    //   constant; SettingChanged just never fires, which is correct since
    //   the value can't change at runtime).
    //
    // DP is still a ConfigEntry<T> because that perk is the only one we
    // still want editable in F12. all other perks + wallbuys went through
    // this wrapper after their positions/colors were dialed in.
    public sealed class HardcodedSetting<T>
    {
        public T Value { get; }

        public HardcodedSetting(T value)
        {
            Value = value;
        }

        // matches BepInEx ConfigEntry's SettingChanged event shape so
        // consumer code like `setting.SettingChanged += handler` compiles.
        // never raised - hardcoded values don't change. CS0067 suppresses
        // the "event never used" warning.
#pragma warning disable CS0067
        public event EventHandler SettingChanged;
#pragma warning restore CS0067
    }
}
