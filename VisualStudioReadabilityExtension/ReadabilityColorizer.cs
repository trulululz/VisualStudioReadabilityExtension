using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace VisualStudioReadabilityExtension
{
    internal sealed class ReadabilityColorizer
    {
        internal const string LayerName = "VisualStudioReadabilityExtension";

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly ISettingsManager _settings;
        private ISettingsSubset _subset;

        private Brush[] _brushes;
        private bool _enabled;
        private int _depthLevels; // how many depths to colour; 0 = all
        private readonly Brush _originalBackground; // the view's background before we overrode it

        internal ReadabilityColorizer(IWpfTextView view, IServiceProvider serviceProvider)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _layer = view.GetAdornmentLayer(LayerName);
            _settings = ReadabilityColorizerSettings.GetManager(serviceProvider);
            _originalBackground = _view.Background; // capture so we can restore it if disabled

            ReloadSettings();

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;

            if (_settings != null)
            {
                // Live-update when the user changes settings in the Settings window.
                _subset = _settings.GetSubset(ReadabilityColorizerSettings.SubsetPattern);
                _subset.SettingChangedAsync += OnSettingChangedAsync;
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
            if (_subset != null)
            {
                _subset.SettingChangedAsync -= OnSettingChangedAsync;
                _subset = null;
            }
        }

        private async Task OnSettingChangedAsync(object sender, PropertyChangedEventArgs e)
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            ReloadSettings();
            RedrawBlocks();
        }

        private void ReloadSettings()
        {
            ReadabilityColorizerSettings.Model model;
            try
            {
                model = ReadabilityColorizerSettings.Load(_settings);
            }
            catch
            {
                model = new ReadabilityColorizerSettings.Model(); // fall back to defaults
            }

            _enabled = model.Enabled;
            _depthLevels = Math.Max(0, model.DepthLevels);
            _brushes = BuildBrushes(model);

            ApplyBackground(model);
        }

        private void ApplyBackground(ReadabilityColorizerSettings.Model model)
        {
            if (!_enabled)
            {
                _view.Background = _originalBackground; // restore the theme's background
                return;
            }

            int argb = model.BackgroundColor;
            var color = Color.FromArgb(
                0xFF, // the code-view background is always opaque
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _view.Background = brush;
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            RedrawBlocks();
        }

        private void RedrawBlocks()
        {
            _layer.RemoveAllAdornments();

            if (!_enabled)
            {
                return;
            }

            IWpfTextViewLineCollection lines = _view.TextViewLines;
            if (lines == null || _view.InLayout)
            {
                return;
            }

            ITextSnapshot snapshot = _view.TextSnapshot;
            string text = snapshot.GetText();

            double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? 7.0;
            double viewportRight = _view.ViewportRight;
            double viewportLeft = _view.ViewportLeft;

            foreach (Block block in FindBlocks(text))
            {
                int visibleDepth = block.Depth;

                // Optionally colour only the first N depths (0 = all).
                if (_depthLevels > 0 && visibleDepth >= _depthLevels)
                {
                    continue;
                }

                int length = (block.Close - block.Open) + 1;
                var span = new SnapshotSpan(snapshot, block.Open, length);

                Geometry geometry;
                try
                {
                    // Clips automatically to the currently formatted (visible) lines and
                    // returns null when the block is entirely off-screen.
                    geometry = lines.GetMarkerGeometry(span);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (geometry == null)
                {
                    continue;
                }

                Rect bounds = geometry.Bounds;

                // Start the tint at the column of the opening '{' so it's obvious which brace owns each block
                double left;
                var openPoint = new SnapshotPoint(snapshot, block.Open);
                ITextViewLine openLine = lines.GetTextViewLineContainingBufferPosition(openPoint);
                if (openLine != null && openPoint >= openLine.Start && openPoint < openLine.EndIncludingLineBreak)
                {
                    try
                    {
                        left = openLine.GetCharacterBounds(openPoint).Left;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        left = viewportLeft + (block.Depth * columnWidth);
                    }
                }
                else
                {
                    left = viewportLeft + (block.Depth * columnWidth);
                }

                // Single-line block only across its own {}
                bool singleLine = snapshot.GetLineNumberFromPosition(block.Open)
                                  == snapshot.GetLineNumberFromPosition(block.Close);
                double width = singleLine ? (bounds.Right - left) : (viewportRight - left);
                if (width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                var rectangle = new Rectangle
                {
                    Width = width,
                    Height = bounds.Height,
                    Fill = _brushes[visibleDepth % _brushes.Length],
                    IsHitTestVisible = false,
                };

                Canvas.SetLeft(rectangle, left);
                Canvas.SetTop(rectangle, bounds.Top);

                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, rectangle, null);
            }
        }

        private static IEnumerable<Block> FindBlocks(string text)
        {
            var blocks = new List<Block>();
            var open = new Stack<int>();

            int i = 0;
            int n = text.Length;

            while (i < n)
            {
                char c = text[i];

                // Line comment: // ... to end of line
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    i += 2;
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }

                // Block comment: /* ... */
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i += 2;
                    continue;
                }

                // Verbatim / interpolated-verbatim string: @"..." or $@"..." ("" escapes a quote)
                if (c == '@' && i + 1 < n && text[i + 1] == '"')
                {
                    i += 2;
                    while (i < n)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < n && text[i + 1] == '"') { i += 2; continue; } // escaped quote
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // Regular string: "..." with \ escapes, terminated by an unescaped quote or newline
                if (c == '"')
                {
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '"' || text[i] == '\n') { i++; break; }
                        i++;
                    }
                    continue;
                }

                // Char literal: '...'
                if (c == '\'')
                {
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '\'' || text[i] == '\n') { i++; break; }
                        i++;
                    }
                    continue;
                }

                if (c == '{')
                {
                    open.Push(i);
                    i++;
                    continue;
                }

                if (c == '}')
                {
                    if (open.Count > 0)
                    {
                        int openIndex = open.Pop();
                        // Depth = number of still-open braces after popping (0 = outermost).
                        blocks.Add(new Block(openIndex, i, open.Count));
                    }
                    i++;
                    continue;
                }

                i++;
            }

            return blocks;
        }

        private static Brush[] BuildBrushes(ReadabilityColorizerSettings.Model model)
        {
            byte alpha = (byte)Math.Round(ReadabilityColorizerSettings.Clamp(model.OpacityPercent, 1, 100) * 255.0 / 100.0);

            var brushes = new Brush[model.DepthColors.Length];
            for (int i = 0; i < model.DepthColors.Length; i++)
            {
                int argb = model.DepthColors[i];
                var color = Color.FromArgb(
                    alpha,
                    (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));

                var brush = new SolidColorBrush(color);
                brush.Freeze(); // frozen brushes are cheaper and safe to share
                brushes[i] = brush;
            }

            return brushes;
        }

        private readonly struct Block
        {
            public Block(int open, int close, int depth)
            {
                Open = open;
                Close = close;
                Depth = depth;
            }

            public int Open { get; }
            public int Close { get; }
            public int Depth { get; }
        }
    }
}
