using System;

namespace VisualStudioReadabilityExtension
{
    internal static class PackageCommands
    {
        // guidReadabilityCmdSet in the .vsct
        public static readonly Guid CommandSet = new Guid("d3a5b8e1-4c7f-4e29-9a6b-2f8c1d0e3b4a");

        // cmdidToggleReadability in the .vsct
        public const int ToggleReadabilityCommandId = 0x0100;

        // cmdidToggleActiveScope in the .vsct
        public const int ToggleActiveScopeCommandId = 0x0101;
    }
}
