using System;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using GorillaNetworking;
using NoxsCustomComputer.Classes.Models;

namespace MonkeFur.Nox
{
    public static class NoxBridge
    {
        internal static ConfigFile Config;
        internal static Func<bool> IsOn;
        internal static Action Toggle;

        public static void Init(ConfigFile config, Func<bool> isOn, Action toggle)
        {
            Config = config;
            IsOn = isOn;
            Toggle = toggle;
        }
    }

    public sealed class FurScreen : NoxScreen
    {
        private sealed class Setting
        {
            public string Label;
            public string Section;
            public string Key;
            public float Step;
            public string Format;
        }

        private static readonly Setting[] Settings =
        {
            new Setting { Label = "LENGTH", Section = "Look", Key = "FurLength", Step = 0.005f, Format = "0.000" },
            new Setting { Label = "WIDTH", Section = "Look", Key = "StrandWidth", Step = 0.001f, Format = "0.000" },
            new Setting { Label = "STRANDS", Section = "Shape", Key = "StrandCount", Step = 2000f, Format = "0" },
            new Setting { Label = "GUIDES", Section = "Shape", Key = "GuideCount", Step = 50f, Format = "0" },
            new Setting { Label = "STIFFNESS", Section = "Physics", Key = "Stiffness", Step = 0.05f, Format = "0.00" },
            new Setting { Label = "BOUNCE", Section = "Physics", Key = "Damping", Step = 0.02f, Format = "0.00" },
            new Setting { Label = "SWING", Section = "Physics", Key = "Inertia", Step = 0.05f, Format = "0.00" },
            new Setting { Label = "TRAIL", Section = "Physics", Key = "WorldDrag", Step = 0.05f, Format = "0.00" },
            new Setting { Label = "GRAVITY", Section = "Physics", Key = "GravityScale", Step = 0.05f, Format = "0.00" },
        };

        private int selected;

        public FurScreen() : base("FUR")
        {
        }

        public override void OnOpen()
        {
            base.OnOpen();
            Draw();
        }

        public override void OnKey(GorillaKeyboardBindings key)
        {
            if (key >= GorillaKeyboardBindings.one && key <= GorillaKeyboardBindings.nine)
            {
                int index = (int)key - (int)GorillaKeyboardBindings.one;
                if (index < Settings.Length) selected = index;
            }
            else if (key == GorillaKeyboardBindings.option1)
            {
                NoxBridge.Toggle?.Invoke();
            }
            else if (key == GorillaKeyboardBindings.option2)
            {
                Nudge(-1);
            }
            else if (key == GorillaKeyboardBindings.option3)
            {
                Nudge(1);
            }
            else if (key == GorillaKeyboardBindings.enter)
            {
                ConfigEntryBase entry = Find(Settings[selected]);
                if (entry != null) entry.BoxedValue = entry.DefaultValue;
            }
            Draw();
        }

        private static ConfigEntryBase Find(Setting s)
        {
            ConfigFile cfg = NoxBridge.Config;
            if (cfg == null) return null;
            var def = new ConfigDefinition(s.Section, s.Key);
            return cfg.ContainsKey(def) ? cfg[def] : null;
        }

        private void Nudge(int direction)
        {
            Setting s = Settings[selected];
            ConfigEntryBase entry = Find(s);
            if (entry == null) return;

            if (entry.BoxedValue is int i)
                entry.BoxedValue = i + (int)s.Step * direction;
            else if (entry.BoxedValue is float f)
                entry.BoxedValue = (float)Math.Round(f + s.Step * direction, 4);
        }

        private void Draw()
        {
            var sb = new StringBuilder();
            bool on = NoxBridge.IsOn?.Invoke() ?? false;
            sb.Append("FUR: ").Append(on ? "ON" : "OFF").Append("   OPTION 1 TO TOGGLE\n\n");

            for (int i = 0; i < Settings.Length; i++)
            {
                Setting s = Settings[i];
                ConfigEntryBase entry = Find(s);
                string value = entry == null ? "-" : Format(entry.BoxedValue, s.Format);
                sb.Append(i == selected ? "> " : "  ")
                  .Append(i + 1).Append(' ')
                  .Append(s.Label.PadRight(10))
                  .Append(value).Append('\n');
            }

            sb.Append("\nNUMBER: PICK  OPT2/3: -/+  ENTER: RESET");
            SetContent(sb.ToString());
        }

        private static string Format(object value, string format)
        {
            switch (value)
            {
                case float f: return f.ToString(format, CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                default: return value?.ToString() ?? "-";
            }
        }
    }
}
