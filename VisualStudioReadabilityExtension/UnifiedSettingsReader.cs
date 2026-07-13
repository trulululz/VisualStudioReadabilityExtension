using System;
using System.Reflection;

namespace VisualStudioReadabilityExtension
{
    internal static class UnifiedSettingsReader
    {
        private static object _reader;          // Microsoft.VisualStudio.Utilities.UnifiedSettings.ISettingsReader
        private static MethodInfo _getValueOrThrow; // object GetValueOrThrow(string moniker, Type targetType)

        public static bool TryInitialize(IServiceProvider serviceProvider)
        {
            if (_reader != null)
            {
                return true;
            }

            if (serviceProvider == null)
            {
                return false;
            }

            try
            {
                Type serviceType = ResolveType("Microsoft.Internal.VisualStudio.Shell.Interop.SVsUnifiedSettingsManager");
                object service = serviceType == null ? null : serviceProvider.GetService(serviceType);
                if (service == null)
                {
                    ReadabilityColorizerSettings.Log($"UnifiedSettingsReader: service unavailable (serviceType={serviceType?.FullName ?? "null"})");
                    return false;
                }

                MethodInfo getReader = service.GetType().GetMethod("GetReader", Type.EmptyTypes);
                if (getReader == null)
                {
                    ReadabilityColorizerSettings.Log("UnifiedSettin§gsReader: GetReader() not found on " + service.GetType().FullName);
                    return false;
                }

                object reader = getReader.Invoke(service, null);
                if (reader == null)
                {
                    return false;
                }

                // object GetValueOrThrow(string moniker, Type targetType)
                MethodInfo getValue = reader.GetType().GetMethod(
                    "GetValueOrThrow", new[] { typeof(string), typeof(Type) });
                if (getValue == null)
                {
                    ReadabilityColorizerSettings.Log("UnifiedSettingsReader: GetValueOrThrow(string,Type) not found on " + reader.GetType().FullName);
                    return false;
                }

                _reader = reader;
                _getValueOrThrow = getValue;
                ReadabilityColorizerSettings.Log("UnifiedSettingsReader: reader acquired");
                return true;
            }
            catch (Exception ex)
            {
                ReadabilityColorizerSettings.Log("UnifiedSettingsReader init failed: " + ex);
                return false;
            }
        }

        public static bool IsAvailable => _reader != null;

        public static bool GetBool(string moniker, bool fallback) => Get(moniker, fallback);

        public static int GetInt(string moniker, int fallback) => Get(moniker, fallback);

        public static string GetString(string moniker, string fallback) => Get(moniker, fallback);

        private static T Get<T>(string moniker, T fallback)
        {
            if (_reader == null || _getValueOrThrow == null)
            {
                return fallback;
            }

            try
            {
                object value = _getValueOrThrow.Invoke(_reader, new object[] { moniker, typeof(T) });
                ReadabilityColorizerSettings.Log($"UnifiedSettings {moniker} = {(value ?? "<null>")}");

                if (value is T typed)
                {
                    return typed;
                }

                if (value == null)
                {
                    return fallback;
                }

                return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                // No stored value / not registered / type mismatch — use the caller's default.
                Exception inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                ReadabilityColorizerSettings.Log($"UnifiedSettings {moniker}: {inner.GetType().Name} -> default '{fallback}'");
                return fallback;
            }
        }

        private static Type ResolveType(string fullName)
        {
            Type type = Type.GetType(fullName + ", Microsoft.Internal.VisualStudio.Interop", throwOnError: false);
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
                    // ignore assemblies that can't be inspected
                }
            }

            return null;
        }
    }
}
