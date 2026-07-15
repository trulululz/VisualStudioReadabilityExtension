using System;
using Microsoft.VisualStudio.Settings;

namespace VisualStudioReadabilityExtension
{
    internal static class ReadabilityColorizerSettings
    {
        internal const int DepthColorCount = 7;
        internal const string DefaultBackgroundHex = "#000000"; // pure black
        internal const string DefaultActiveScopeHex = "#FFFFFF"; // bright white outline

        private const string Prefix = "visualStudioReadabilityExtension.";
        internal const string SubsetPattern = Prefix + "*";

        internal const string KeyBackground = Prefix + "backgroundColor";
        internal const string KeyOpacity = Prefix + "opacityPercent";
        internal const string KeyDepthLevels = Prefix + "depthLevels";
        internal const string KeyActiveScopeColor = Prefix + "activeScopeColor";
        internal const string KeyActiveScopeThickness = Prefix + "activeScopeThickness";

        internal static string KeyDepthColor(int index) => Prefix + "depthColor" + (index + 1);

        public sealed class Model
        {
            public bool Enabled = true;
            public int BackgroundColor = ParseHex(DefaultBackgroundHex, unchecked((int)0xFF000000));
            public int OpacityPercent = 15;
            public int DepthLevels = 0; // 0 = all depths
            public int[] DepthColors = DefaultDepthColors();
            public bool ShowActiveScope = true;
            public int ActiveScopeColor = ParseHex(DefaultActiveScopeHex, unchecked((int)0xFFFFFFFF));
            public int ActiveScopeThickness = 1;
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

        /// <summary>Diagnostic log written to %TEMP%\VsReadabilityExtension.log.</summary>
        internal static readonly string LogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VsReadabilityExtension.log");

        internal static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never throw into the editor.
            }
        }

        public static ISettingsManager GetManager(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
            {
                Log("GetManager: serviceProvider is null");
                return null;
            }

            Type serviceType = ResolveSettingsServiceType();
            if (serviceType == null)
            {
                Log("GetManager: could not resolve SVsSettingsPersistenceManager type");
                return null;
            }

            object service = serviceProvider.GetService(serviceType);
            var manager = service as ISettingsManager;
            Log($"GetManager: type={serviceType.Assembly.GetName().Name}; service={(service == null ? "null" : service.GetType().FullName)}; isISettingsManager={manager != null}");
            return manager;
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

        public static Model Load(IServiceProvider serviceProvider)
        {
            var model = new Model();

            if (!UnifiedSettingsReader.TryInitialize(serviceProvider))
            {
                Log("Load: unified settings reader unavailable -> returning all defaults");
                return model;
            }

            model.BackgroundColor = ParseHex(UnifiedSettingsReader.GetString(KeyBackground, DefaultBackgroundHex), unchecked((int)0xFF000000));
            model.OpacityPercent = Clamp(UnifiedSettingsReader.GetInt(KeyOpacity, 15), 1, 100);
            model.DepthLevels = Math.Max(0, UnifiedSettingsReader.GetInt(KeyDepthLevels, 0));

            var def = DefaultDepthHex();
            var colors = new int[DepthColorCount];
            for (int i = 0; i < DepthColorCount; i++)
            {
                string hex = UnifiedSettingsReader.GetString(KeyDepthColor(i), def[i]);
                colors[i] = ParseHex(hex, ParseHex(def[i], unchecked((int)0xFFFF9100)));
            }
            model.DepthColors = colors;

            model.ActiveScopeColor = ParseHex(UnifiedSettingsReader.GetString(KeyActiveScopeColor, DefaultActiveScopeHex), unchecked((int)0xFFFFFFFF));
            model.ActiveScopeThickness = Clamp(UnifiedSettingsReader.GetInt(KeyActiveScopeThickness, 1), 1, 10);

            return model;
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
