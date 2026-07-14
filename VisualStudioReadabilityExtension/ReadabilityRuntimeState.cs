using System;

namespace VisualStudioReadabilityExtension
{
    internal static class ReadabilityRuntimeState
    {
        private static bool? _enabledOverride;
        private static bool? _activeScopeOverride;

        public static bool LastPersistedEnabled = true;
        public static bool LastPersistedActiveScope = true;

        public static event EventHandler Changed;

        public static bool? EnabledOverride => _enabledOverride;
        public static bool? ActiveScopeOverride => _activeScopeOverride;

        public static bool EffectiveEnabled => _enabledOverride ?? LastPersistedEnabled;
        public static bool EffectiveActiveScope => _activeScopeOverride ?? LastPersistedActiveScope;

        public static void ToggleEnabled()
        {
            _enabledOverride = !EffectiveEnabled;
            ReadabilityColorizerSettings.Log($"RuntimeState: enabled -> {_enabledOverride}");
            OnChanged();
        }

        public static void ToggleActiveScope()
        {
            _activeScopeOverride = !EffectiveActiveScope;
            ReadabilityColorizerSettings.Log($"RuntimeState: showActiveScope -> {_activeScopeOverride}");
            OnChanged();
        }

        private static void OnChanged()
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
