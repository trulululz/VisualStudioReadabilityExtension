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

        // Horizontal gap (px) between the glyphs and the active-scope outline.
        private const double ActiveScopePadding = 2.0;

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly IServiceProvider _serviceProvider;
        private ISettingsManager _settings;
        private ISettingsSubset _subset;
        private bool _subscribedToSubset;

        private Brush[] _brushes;
        private bool _enabled;
        private int _depthLevels; // how many depths to colour; 0 = all
        private bool _showActiveScope; // outline the bracket pair containing the caret
        private Brush _activeScopeBrush; // stroke for the active-scope outline
        private double _activeScopeThickness;
        private string _settingsSignature; // fingerprint of the last-loaded settings, to skip no-op redraws
        private readonly Brush _originalBackground; // the view's background before we overrode it

        internal ReadabilityColorizer(IWpfTextView view, IServiceProvider serviceProvider)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _layer = view.GetAdornmentLayer(LayerName);
            _serviceProvider = serviceProvider;
            _originalBackground = _view.Background; // capture so we can restore it if disabled

            EnsureSettingsManager(); // may be null this early; retried on every reload

            ReloadSettings();

            _view.LayoutChanged += OnLayoutChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.GotAggregateFocus += OnGotAggregateFocus;
            _view.Closed += OnClosed;
            ReadabilityRuntimeState.Changed += OnRuntimeStateChanged;
        }

        // A toolbar toggle flipped an override — reload (picks up the override) and redraw.
        private void OnRuntimeStateChanged(object sender, EventArgs e)
        {
            ReloadAndRedrawIfChanged();
        }

        private void EnsureSettingsManager()
        {
            if (_settings == null)
            {
                _settings = ReadabilityColorizerSettings.GetManager(_serviceProvider);
            }

            if (_settings != null && !_subscribedToSubset)
            {
                try
                {
                    _subset = _settings.GetSubset(ReadabilityColorizerSettings.SubsetPattern);
                    _subset.SettingChangedAsync += OnSettingChangedAsync;
                    _subscribedToSubset = true;
                    ReadabilityColorizerSettings.Log("EnsureSettingsManager: subscribed to SettingChangedAsync");
                }
                catch (Exception ex)
                {
                    // A live-update subscription failure is non-fatal; focus-based reload still applies changes.
                    ReadabilityColorizerSettings.Log("EnsureSettingsManager: subscribe failed: " + ex.Message);
                }
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.GotAggregateFocus -= OnGotAggregateFocus;
            _view.Closed -= OnClosed;
            ReadabilityRuntimeState.Changed -= OnRuntimeStateChanged;
            if (_subset != null)
            {
                _subset.SettingChangedAsync -= OnSettingChangedAsync;
                _subset = null;
            }
        }

        private void OnGotAggregateFocus(object sender, EventArgs e)
        {
            ReloadAndRedrawIfChanged();
        }

        private async Task OnSettingChangedAsync(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ReloadSettings();
                RedrawBlocks();
            }
            catch
            {
                // A failure here must not tear down the subscription; the focus-based reload
                // (OnGotAggregateFocus) is the reliable fallback that still applies the change.
            }
        }

        /// <summary>
        /// Re-reads settings and redraws only when a value actually changed, so the frequent
        /// focus events don't cause needless work.
        /// </summary>
        private void ReloadAndRedrawIfChanged()
        {
            if (ReloadSettings())
            {
                RedrawBlocks();
            }
        }

        /// <summary>Re-reads every setting into the live fields. Returns true when at least one
        /// value changed since the previous load (so callers can skip a redraw when nothing moved).</summary>
        private bool ReloadSettings()
        {
            EnsureSettingsManager(); // pick up the manager if it wasn't available at construction

            ReadabilityColorizerSettings.Model model;
            try
            {
                model = ReadabilityColorizerSettings.Load(_serviceProvider);
            }
            catch
            {
                model = new ReadabilityColorizerSettings.Model(); // fall back to defaults
            }

            // Publish the persisted values so the toolbar can show a checked state and seed its
            // first toggle, then let any active toolbar override win for this session.
            ReadabilityRuntimeState.LastPersistedEnabled = model.Enabled;
            ReadabilityRuntimeState.LastPersistedActiveScope = model.ShowActiveScope;
            bool effectiveEnabled = ReadabilityRuntimeState.EffectiveEnabled;
            bool effectiveActiveScope = ReadabilityRuntimeState.EffectiveActiveScope;

            string signature = Signature(model, effectiveEnabled, effectiveActiveScope);
            bool changed = signature != _settingsSignature;
            _settingsSignature = signature;

            _enabled = effectiveEnabled;
            _depthLevels = Math.Max(0, model.DepthLevels);
            _brushes = BuildBrushes(model);

            _showActiveScope = effectiveActiveScope;
            _activeScopeThickness = model.ActiveScopeThickness;
            _activeScopeBrush = BuildActiveScopeBrush(model);

            // Only touch the view's Background when something changed: reassigning it every focus
            // would allocate a brush and could trigger a relayout on every click into the editor.
            if (changed)
            {
                ApplyBackground(model);
            }

            return changed;
        }

        /// <summary>A cheap fingerprint of every setting, used to detect whether a reload changed anything.</summary>
        private static string Signature(ReadabilityColorizerSettings.Model model, bool effectiveEnabled, bool effectiveActiveScope)
        {
            return string.Join("|",
                effectiveEnabled,
                model.BackgroundColor,
                model.OpacityPercent,
                model.DepthLevels,
                effectiveActiveScope,
                model.ActiveScopeColor,
                model.ActiveScopeThickness,
                string.Join(",", model.DepthColors));
        }

        private static Brush BuildActiveScopeBrush(ReadabilityColorizerSettings.Model model)
        {
            int argb = model.ActiveScopeColor;
            var color = Color.FromArgb(
                0xFF, // the outline is fully opaque so it reads against the depth fills
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
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

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            // Moving the caret doesn't relayout the view, so redraw here to keep the
            // active-scope outline following the caret. Cheap no-op when the feature is off.
            if (_enabled && _showActiveScope)
            {
                RedrawBlocks();
            }
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

            // Depth fills track curly-brace blocks; the active-scope outline tracks parentheses.
            var braceBlocks = new List<Block>(FindBlocks(text, '{', '}'));
            Block? activeBlock = _showActiveScope
                ? FindActiveBlock(new List<Block>(FindBlocks(text, '(', ')')))
                : null;

            foreach (Block block in braceBlocks)
            {
                int visibleDepth = block.Depth;

                // Optionally colour only the first N depths (0 = all).
                if (_depthLevels > 0 && visibleDepth >= _depthLevels)
                {
                    continue;
                }

                if (!TryGetBlockLayout(block, lines, snapshot, viewportLeft, viewportRight, columnWidth,
                        out SnapshotSpan span, out double left, out Rect bounds, out double width))
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

            // Draw the active-scope outline last so it sits on top of every depth fill.
            // Independent of the depth-level filter above: the caret's scope is always outlined.
            if (activeBlock.HasValue)
            {
                DrawActiveScope(activeBlock.Value, lines, snapshot);
            }
        }

        private Block? FindActiveBlock(List<Block> blocks)
        {
            int caret = _view.Caret.Position.BufferPosition.Position;

            Block? best = null;
            foreach (Block block in blocks)
            {
                if (caret >= block.Open && caret <= block.Close)
                {
                    if (best == null || block.Depth > best.Value.Depth)
                    {
                        best = block;
                    }
                }
            }

            return best;
        }

        private void DrawActiveScope(Block block, IWpfTextViewLineCollection lines, ITextSnapshot snapshot)
        {
            // Outline the parenthesised region from '(' through ')'. 
            int length = (block.Close - block.Open) + 1;
            var span = new SnapshotSpan(snapshot, block.Open, length);

            Geometry combined = null;
            foreach (ITextViewLine line in lines.GetTextViewLinesIntersectingSpan(span))
            {
                double left;
                if (block.Open >= line.Start.Position && block.Open < line.End.Position)
                {
                    // The line holding the opening '(' starts at the paren itself, so text before
                    // the call (e.g. "else if ") is not enclosed.
                    try
                    {
                        left = line.GetCharacterBounds(new SnapshotPoint(snapshot, block.Open)).Left;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        left = GetLineContentLeft(line, snapshot, out _);
                    }
                }
                else
                {
                    left = GetLineContentLeft(line, snapshot, out bool hasContent);
                    if (!hasContent)
                    {
                        continue; // blank line — nothing to wrap
                    }
                }

                // Right edge: clip to the closing ')' on the last line, otherwise the line's content end.
                double right = line.TextRight;
                if (block.Close >= line.Start.Position && block.Close < line.End.Position)
                {
                    try
                    {
                        right = line.GetCharacterBounds(new SnapshotPoint(snapshot, block.Close)).Right;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // keep TextRight
                    }
                }

                if (right <= left)
                {
                    continue;
                }

                // A little horizontal breathing room so the stroke sits just outside glyphs
                var box = new Rect(left, line.Top, right - left, line.Height);
                box.Inflate(ActiveScopePadding, 0);

                Geometry rect = new RectangleGeometry(box);
                combined = combined == null
                    ? rect
                    : Geometry.Combine(combined, rect, GeometryCombineMode.Union, null);
            }

            if (combined == null)
            {
                return;
            }

            combined.Freeze();

            var outline = new Path
            {
                Data = combined,
                Stroke = _activeScopeBrush,
                StrokeThickness = _activeScopeThickness,
                Fill = null,
                IsHitTestVisible = false,
            };

            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, outline, null);
        }

        private static double GetLineContentLeft(ITextViewLine line, ITextSnapshot snapshot, out bool hasContent)
        {
            int start = line.Start.Position;
            int end = line.End.Position; // excludes the line break
            for (int p = start; p < end; p++)
            {
                if (!char.IsWhiteSpace(snapshot[p]))
                {
                    hasContent = true;
                    try
                    {
                        return line.GetCharacterBounds(new SnapshotPoint(snapshot, p)).Left;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return line.TextLeft;
                    }
                }
            }

            hasContent = false;
            return line.TextLeft;
        }

        private static bool TryGetBlockLayout(Block block, IWpfTextViewLineCollection lines, ITextSnapshot snapshot,
            double viewportLeft, double viewportRight, double columnWidth,
            out SnapshotSpan span, out double left, out Rect bounds, out double width)
        {
            span = default;
            left = 0;
            bounds = default;
            width = 0;

            int length = (block.Close - block.Open) + 1;
            span = new SnapshotSpan(snapshot, block.Open, length);

            Geometry geometry;
            try
            {
                // Clips automatically to the currently formatted (visible) lines and
                // returns null when the block is entirely off-screen.
                geometry = lines.GetMarkerGeometry(span);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (geometry == null)
            {
                return false;
            }

            bounds = geometry.Bounds;

            // Start at the column of the opening '{' so it's obvious which brace owns each block.
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
            width = singleLine ? (bounds.Right - left) : (viewportRight - left);

            return width > 0 && bounds.Height > 0;
        }

        private static IEnumerable<Block> FindBlocks(string text, char openChar, char closeChar)
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

                if (c == openChar)
                {
                    open.Push(i);
                    i++;
                    continue;
                }

                if (c == closeChar)
                {
                    if (open.Count > 0)
                    {
                        int openIndex = open.Pop();
                        // Depth = number of still-open delimiters after popping (0 = outermost).
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
