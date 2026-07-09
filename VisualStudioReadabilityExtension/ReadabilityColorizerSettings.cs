using System;
using Microsoft.VisualStudio.Settings;

namespace VisualStudioReadabilityExtension
{
    internal static class ReadabilityColorizerSettings
    {
        internal const int DepthColorCount = 7;
        internal const string DefaultBackgroundHex = "#000000"; // pure black

        private const string Prefix = "visualStudioReadabilityExtension.";
        internal const string SubsetPattern = Prefix + "*";

        internal const string KeyEnabled = Prefix + "enabled";
        internal const string KeyBackground = Prefix + "backgroundColor";
        internal const string KeyOpacity = Prefix + "opacityPercent";
        internal const string KeyDepthLevels = Prefix + "depthLevels";

        internal static string KeyDepthColor(int index) => Prefix + "depthColor" + (index + 1);

        public sealed class Model
        {
            public bool Enabled = true;
            public int BackgroundColor = ParseHex(DefaultBackgroundHex, unchecked((int)0xFF000000));
            public int OpacityPercent = 15;
            public int DepthLevels = 0; // 0 = all depths
            public int[] DepthColors = DefaultDepthColors();
        }

        public static string[] DefaultDepthHex() => new[]
        {
            "#90A4AE", // 1 - cool grey
            "#40C4FF", // 2 - blue
            "#69F0AE", // 3 - green
            "#FFD740", // 4 - amber
            "#FF6E40", // 5 - orange
            "#FF4081", // 6 - pink
            "#E040FB", // 7 - purple
        };

        public static int[] DefaultDepthColors()
        {
            var hex = DefaultDepthHex();
            var colors = new int[DepthColorCount];
            for (int i = 0; i < DepthColorCount; i++)
            {
                colors[i] = ParseHex(hex[i], unchecked((int)0xFFFF9100));
            }
            return colors;
        }

        /// <summary>Gets the unified settings manager, or null if the service is unavailable.</summary>
        public static ISettingsManager GetManager(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
            {
                return null;
            }

            Type serviceType = ResolveSettingsServiceType();
            return serviceType == null ? null : serviceProvider.GetService(serviceType) as ISettingsManager;
        }

        private static Type ResolveSettingsServiceType()
        {
            const string fullName = "Microsoft.Internal.VisualStudio.Shell.Interop.SVsSettingsPersistenceManager";

            Type type = Type.GetType(fullName + ", Microsoft.VisualStudio.Interop", throwOnError: false)
                        ?? Type.GetType(fullName + ", Microsoft.VisualStudio.Shell.15.0", throwOnError: false);
            if (type != null)
            {
                return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // Ignore assemblies that can't be inspected.
                }
            }

            return null;
        }

        public static Model Load(ISettingsManager manager)
        {
            var model = new Model();
            if (manager == null)
            {
                return model;
            }

            model.Enabled = Get(manager, KeyEnabled, true);
            model.BackgroundColor = ParseHex(Get(manager, KeyBackground, DefaultBackgroundHex), unchecked((int)0xFF000000));
            model.OpacityPercent = Clamp(Get(manager, KeyOpacity, 15), 1, 100);
            model.DepthLevels = Math.Max(0, Get(manager, KeyDepthLevels, 0));

            var def = DefaultDepthHex();
            var colors = new int[DepthColorCount];
            for (int i = 0; i < DepthColorCount; i++)
            {
                string hex = Get(manager, KeyDepthColor(i), def[i]);
                colors[i] = ParseHex(hex, ParseHex(def[i], unchecked((int)0xFFFF9100)));
            }
            model.DepthColors = colors;

            return model;
        }

        private static T Get<T>(ISettingsManager manager, string name, T fallback)
        {
            try
            {
                return manager.GetValueOrDefault(name, fallback);
            }
            catch
            {
                return fallback; // value stored with an incompatible type, etc.
            }
        }

        public static int ParseHex(string hex, int fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return fallback;
            }

            string s = hex.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
            {
                s = s.Substring(1);
            }

            if (s.Length != 6)
            {
                return fallback;
            }

            if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
            {
                return unchecked((int)0xFF000000) | (rgb & 0x00FFFFFF);
            }

            return fallback;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
