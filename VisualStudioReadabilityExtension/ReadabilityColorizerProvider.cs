using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VisualStudioReadabilityExtension
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ReadabilityColorizerProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(ReadabilityColorizer.LayerName)]
        [Order(After = PredefinedAdornmentLayers.Outlining, Before = PredefinedAdornmentLayers.Selection)]
#pragma warning disable CS0649 // assigned by MEF
        internal AdornmentLayerDefinition editorAdornmentLayer;
#pragma warning restore CS0649

        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            // The instance wires itself to the view's events and lives as long as the view.
            new ReadabilityColorizer(textView, ServiceProvider);
        }
    }
}
