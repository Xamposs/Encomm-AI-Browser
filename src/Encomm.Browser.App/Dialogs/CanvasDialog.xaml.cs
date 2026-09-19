using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Encomm.Browser.AI;
using Encomm.Browser.App.Services;
using Encomm.Browser.App.ViewModels;

namespace Encomm.Browser.App.Dialogs;

/// <summary>
/// ENCOMM Canvas — a browser-generated workspace.
///
/// This is the Phase 3C product surface: the user states an intent, the
/// browser assembles a bounded source set, and the result is rendered as a
/// task-specific NATIVE surface (a comparison table or an evidence list)
/// where every claim carries clickable citations back to the real tab.
///
/// It is deliberately not a chat transcript: the canvas is a workspace
/// artifact, persisted per workspace, that can be reopened, regenerated or
/// deleted. Native XAML only — zero WebViews.
/// </summary>
public sealed partial class CanvasDialog : ContentDialog
{
    private readonly CanvasService _canvases;
    private readonly MainViewModel _vm;
    private AICanvas? _canvas;

    /// <summary>Optional intent supplied by a caller (e.g. the AI surface).</summary>
    public string? InitialIntent { get; set; }

    public CanvasDialog()
    {
        InitializeComponent();
        _canvases = App.Services.GetRequiredService<CanvasService>();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        Opened += (_, _) => Render();
        IntentBox.KeyDown += OnIntentKeyDown;
    }

    private Guid? WorkspaceId => _vm.ActiveWorkspace?.Id;

    private void Render()
    {
        Suggestions.ItemsSource = CanvasService.SuggestedIntents;
        StatusText.Text = _canvases.IsConfigured
            ? "Builds a workspace from this workspace's pages. Only the sources listed below are ever used."
            : AIResult.NotConfiguredMessage.Replace('\n', ' ');

        _canvas = WorkspaceId is { } workspaceId ? _canvases.LoadLatest(workspaceId) : null;
        IntentBox.Text = _canvas?.Intent ?? InitialIntent ?? string.Empty;
        RenderCanvas(_canvas);
    }

    private void RenderCanvas(AICanvas? canvas)
    {
        TableHost.Children.Clear();
        TableHost.ColumnDefinitions.Clear();
        TableHost.RowDefinitions.Clear();
        TablePanel.Visibility = Visibility.Collapsed;
        RowsList.ItemsSource = null;
        Sources.ItemsSource = null;
        SourcePanel.Visibility = Visibility.Collapsed;
        Uncertainties.ItemsSource = null;
        UncertaintyPanel.Visibility = Visibility.Collapsed;

        if (canvas is null)
        {
            CanvasTitle.Text = string.Empty;
            CanvasSummary.Text = string.Empty;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "No canvas yet for this workspace. Describe what you are trying to accomplish, or start from one of these:";
            CopyButton.Visibility = Visibility.Collapsed;
            DeleteButton.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        CanvasTitle.Text = canvas.Title;
        CanvasSummary.Text = canvas.Summary;

        if (canvas.HasTable)
        {
            TablePanel.Visibility = Visibility.Visible;
            BuildTable(canvas);
        }
        else
        {
            RowsList.ItemsSource = BuildRowViews(canvas);
        }

        Uncertainties.ItemsSource = canvas.Uncertainties;
        UncertaintyPanel.Visibility = canvas.Uncertainties.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        Sources.ItemsSource = BuildChips(canvas.Sources);
        SourceHeading.Text = canvas.Sources.Count == 1
            ? "1 source used" : $"{canvas.Sources.Count} sources used";
        SourcePanel.Visibility = canvas.HasSources ? Visibility.Visible : Visibility.Collapsed;

        var usable = canvas.Status == AIResultStatus.Ok;
        CopyButton.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Visible;
        Footnote.Text = BuildFootnote(canvas);
    }

    private string BuildFootnote(AICanvas canvas)
    {
        var notes = new List<string>
        {
            canvas.Kind switch
            {
                CanvasKind.Comparison => "Generated comparison",
                CanvasKind.Evidence => "Generated evidence list",
                _ => "Generated summary"
            },
            $"saved {canvas.CreatedUtc.ToLocalTime():g}"
        };
        if (canvas.TruncatedContext) notes.Add("some sources were trimmed to fit the context budget");
        if (canvas.Sources.Any(s => s.Kind == AIContextKind.TabMetadata))
            notes.Add("cold tabs were used as metadata only — ENCOMM did not wake them");
        return string.Join(" · ", notes);
    }

    // ---- Table / rows ------------------------------------------------

    /// <summary>
    /// Render the comparison as a real table. Columns are data-driven, so
    /// the grid is assembled in code (XAML cannot declare dynamic columns),
    /// with one citation chip row per cell.
    /// </summary>
    private void BuildTable(AICanvas canvas)
    {
        var columns = canvas.Columns.ToList();
        // Rows may contain attributes the model did not declare as columns;
        // render them in an extra "More detail" column instead of dropping.
        var leftovers = canvas.Rows.SelectMany(r => r.Cells)
            .Where(c => c.ColumnKey.Length == 0 || !columns.Any(x =>
                string.Equals(x.Key, c.ColumnKey, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.ColumnKey).Where(k => k.Length > 0).Distinct().ToList();
        foreach (var key in leftovers) columns.Add(new CanvasColumn(key, DisplayLabel(key)));
        var hasUnkeyed = canvas.Rows.SelectMany(r => r.Cells).Any(c => c.ColumnKey.Length == 0);
        if (hasUnkeyed) columns.Add(new CanvasColumn(string.Empty, "Detail"));

        TableHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        foreach (var _ in columns)
            TableHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        TableHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var _ in canvas.Rows)
            TableHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddHeaderCell(0, 0, "Item");
        for (var c = 0; c < columns.Count; c++) AddHeaderCell(c + 1, 0, columns[c].Label);

        for (var r = 0; r < canvas.Rows.Count; r++)
        {
            var row = canvas.Rows[r];
            AddHeaderCell(r + 1, 0, row.Label);
            for (var c = 0; c < columns.Count; c++)
            {
                var key = columns[c].Key;
                var cell = row.Cells.FirstOrDefault(x =>
                    string.Equals(x.ColumnKey, key, StringComparison.OrdinalIgnoreCase));
                AddCell(r + 1, c + 1, cell, canvas);
            }
        }
    }

    private void AddHeaderCell(int row, int column, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = Resource<Style>("EncommChromeTitle")
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, column);
        TableHost.Children.Add(tb);
    }

    private void AddCell(int row, int column, CanvasCell? cell, AICanvas canvas)
    {
        var panel = new StackPanel { Spacing = 2 };
        if (cell is not null && !string.IsNullOrWhiteSpace(cell.Text))
        {
            panel.Children.Add(new TextBlock { Text = cell.Text, TextWrapping = TextWrapping.Wrap });
            var chips = panel.Children.Count > 0
                ? ChipsFor(cell.SourceLabels, cell.SourceTabIds, canvas.Sources)
                : Array.Empty<AiSourceChip>();
            if (chips.Count > 0) panel.Children.Add(BuildChipStrip(chips));
        }

        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        TableHost.Children.Add(panel);
    }

    private StackPanel BuildChipStrip(IReadOnlyList<AiSourceChip> chips)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var chip in chips)
        {
            var button = new Button
            {
                Content = chip.Label,
                Tag = chip.TabId,
                Padding = new Thickness(6, 1, 6, 1),
                FontSize = 10
            };
            button.Foreground = Resource<Microsoft.UI.Xaml.Media.Brush>("EncommAccentCyanBrush");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, chip.AccessibleName);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, chip.Title);
            button.Click += OnActivateSource;
            strip.Children.Add(button);
        }
        return strip;
    }

    private static T? Resource<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;

    // ---- Actions -----------------------------------------------------

    private async void OnBuild(object sender, RoutedEventArgs e) => await BuildAsync(IntentBox.Text);

    private void OnIntentKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = BuildAsync(IntentBox.Text);
    }

    private async void OnUseSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Content is not string intent) return;
        IntentBox.Text = intent;
        await BuildAsync(intent);
    }

    private async Task BuildAsync(string? intent)
    {
        if (WorkspaceId is not { } workspaceId)
        {
            EmptyText.Text = "No workspace is active, so there is nothing to build from yet.";
            return;
        }

        SetBusy(true);
        try
        {
            _canvas = await _canvases.GenerateAsync(
                workspaceId, intent ?? string.Empty, _vm.ActiveTab, _vm.Tabs.ToList());
            RenderCanvas(_canvas);
            if (_canvas.Status != AIResultStatus.Ok)
                Footnote.Text = _canvas.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BuildButton.IsEnabled = !busy;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _canvas?.ToPlainText();
            if (string.IsNullOrWhiteSpace(text)) return;
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Footnote.Text = "Copied. Citations and sources are included, so the canvas stays traceable.";
        }
        catch
        {
            Footnote.Text = "Couldn't copy to the clipboard.";
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        // No confirmation dialog: a canvas is a regenerable derived artifact,
        // and the pages it was built from are never touched.
        if (_canvas is null) return;
        _canvases.Delete(_canvas.Id);
        _canvas = null;
        RenderCanvas(null);
        Footnote.Text = "Canvas deleted. Sources and tabs are untouched.";
    }

    /// <summary>
    /// Open the tab behind a citation. Closes the surface first so the page
    /// is presented in the real window (and normal Ghost restore applies).
    /// </summary>
    private async void OnActivateSource(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Guid tabId) return;
        try { Hide(); } catch { }
        try { await _vm.ActivateAiSourceAsync(tabId); } catch { }
    }

    private static string DisplayLabel(string key)
    {
        if (key.Length == 0) return key;
        var spaced = System.Text.RegularExpressions.Regex.Replace(key, "(?<!^)([A-Z])", " $1")
            .Replace('_', ' ').Replace('-', ' ').Trim();
        return spaced.Length == 0 ? key : char.ToUpper(spaced[0]) + spaced[1..];
    }

    private static IReadOnlyList<CanvasRowView> BuildRowViews(AICanvas canvas)
    {
        var views = new List<CanvasRowView>(canvas.Rows.Count);
        foreach (var row in canvas.Rows)
        {
            var detail = row.FirstText ?? string.Empty;
            if (string.Equals(detail, row.Label, StringComparison.OrdinalIgnoreCase)) detail = string.Empty;
            views.Add(new CanvasRowView(
                row.Label,
                detail,
                detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                ChipsFor(row.SourceLabels, row.SourceTabIds, canvas.Sources)));
        }
        return views;
    }

    private static IReadOnlyList<AiSourceChip> BuildChips(IReadOnlyList<ContextSource> sources) =>
        sources.Select(s => new AiSourceChip(s.Label, s.Title, s.Url, s.TabId)).ToList();

    /// <summary>
    /// Resolve row/cell citations back to real tabs. Labels are resolved
    /// first (what the model returned) and tab ids second, so traceability
    /// survives either output shape; unknown sources are dropped.
    /// </summary>
    private static IReadOnlyList<AiSourceChip> ChipsFor(
        IReadOnlyList<string> labels, IReadOnlyList<Guid> tabIds, IReadOnlyList<ContextSource> sources)
    {
        var chips = new List<AiSourceChip>();
        void Add(ContextSource src)
        {
            if (chips.Any(c => c.TabId == src.TabId)) return;
            chips.Add(new AiSourceChip(src.Label, src.Title, src.Url, src.TabId));
        }

        foreach (var label in labels)
        {
            var src = sources.FirstOrDefault(s =>
                string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase));
            if (src is not null) Add(src);
        }
        foreach (var id in tabIds)
        {
            var src = sources.FirstOrDefault(s => s.TabId == id);
            if (src is not null) Add(src);
        }
        return chips;
    }
}