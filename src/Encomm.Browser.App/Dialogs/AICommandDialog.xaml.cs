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
/// ENCOMM AI surface (Phase 3B — Workspace Intelligence).
///
/// This is a native ENCOMM result surface, deliberately NOT a chat
/// sidebar and NOT an embedded web page. It renders the structured
/// <see cref="AIResult"/> and lets the user jump to the exact source tab
/// behind any claim.
/// </summary>
public sealed partial class AICommandDialog : ContentDialog
{
    private readonly AIService _ai;
    private readonly MainViewModel _vm;

    public AICommandDialog()
    {
        InitializeComponent();
        _ai = App.Services.GetRequiredService<AIService>();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        Opened += (_, _) => Render();
        QuestionBox.KeyDown += OnQuestionKeyDown;
        QuestionBox.TextChanged += (_, _) => UpdateAskButton();
    }

    private void Render()
    {
        ProviderLabel.Text = _ai.IsConfigured
            ? "ENCOMM AI is ready. Page content leaves your machine only when you run an action, and every result lists its sources."
            : AIResult.NotConfiguredMessage.Replace('\n', ' ');
        Commands.ItemsSource = _vm.AiCommands;
        QuestionBox.Text = _vm.AiCommandText ?? string.Empty;
        if (AskScope.SelectedIndex < 0) AskScope.SelectedIndex = 0;
        UpdateAskButton();
        RenderResult(_vm.AiResult);
    }

    // ---- Actions -----------------------------------------------------

    private async void OnRunCommand(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string commandId) return;
        await RunAsync(commandId, null);
    }

    private async void OnAsk(object sender, RoutedEventArgs e)
    {
        var commandId = AskScope.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : "ask-page";
        var question = QuestionBox.Text?.Trim() ?? string.Empty;
        if (question.Length == 0) return;
        _vm.AiCommandText = question;
        await RunAsync(commandId, question);
    }

    private void OnQuestionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        OnAsk(sender, new RoutedEventArgs());
    }

    private async Task RunAsync(string commandId, string? question)
    {
        SetBusy(true);
        try
        {
            var result = await _ai.RunAsync(new AICommandRequest(
                commandId, _vm.ActiveTab, _vm.Tabs.ToList(), question));
            _vm.AiResult = result;
            RenderResult(result);
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
        AskButton.IsEnabled = !busy;
        if (busy)
        {
            ResultTitle.Text = "Working…";
            ResultSummary.Text = "Reading the selected sources. This can take a moment.";
        }
    }

    // ---- Rendering ---------------------------------------------------

    private void RenderResult(AIResult? result)
    {
        if (result is null)
        {
            ResultTitle.Text = "Pick an action, or ask a question.";
            ResultSummary.Text = "ENCOMM AI reads only the bounded sources it lists, and shows you exactly which tab each claim came from.";
            Items.ItemsSource = null;
            Uncertainties.ItemsSource = null;
            UncertaintyPanel.Visibility = Visibility.Collapsed;
            Sources.ItemsSource = null;
            SourcePanel.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = Visibility.Collapsed;
            Footnote.Text = string.Empty;
            return;
        }

        ResultTitle.Text = result.Title;
        ResultSummary.Text = result.Summary;

        Items.ItemsSource = BuildItems(result);

        Uncertainties.ItemsSource = result.Uncertainties;
        UncertaintyPanel.Visibility = result.Uncertainties.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        Sources.ItemsSource = BuildSources(result);
        SourcePanel.Visibility = result.HasSources ? Visibility.Visible : Visibility.Collapsed;
        SourceHeading.Text = result.Sources.Count == 1
            ? "1 source used"
            : $"{result.Sources.Count} sources used";

        CopyButton.Visibility = result.IsUsable ? Visibility.Visible : Visibility.Collapsed;
        Footnote.Text = BuildFootnote(result);
    }

    private string BuildFootnote(AIResult result)
    {
        var notes = new List<string>();
        if (result.TruncatedContext)
            notes.Add("Some sources were trimmed to fit the context budget.");
        if (result.Sources.Any(s => s.Kind == AIContextKind.TabMetadata))
            notes.Add("Tabs marked cold were used as metadata only — ENCOMM did not wake them.");
        // Technical detail belongs in Developer Mode only.
        if (_vm.ShowDeveloperSurfaces && !string.IsNullOrWhiteSpace(result.Diagnostic))
            notes.Add(result.Diagnostic!);
        return string.Join("  ", notes);
    }

    private static IReadOnlyList<AiItemView> BuildItems(AIResult result)
    {
        var views = new List<AiItemView>(result.Items.Count);
        foreach (var item in result.Items)
        {
            var facts = string.Join(" · ",
                item.Facts.Select(f => $"{f.Key}: {f.Value}"));
            views.Add(new AiItemView(
                item.Label,
                item.Detail ?? string.Empty,
                facts,
                item.Detail is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed,
                facts.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                BuildChips(result, item)));
        }
        return views;
    }

    /// <summary>
    /// Resolve an item's cited labels back to real tabs. Citation labels
    /// are resolved by label first (what the model actually returned) and
    /// by tab id second, so traceability survives either shape.
    /// </summary>
    private static IReadOnlyList<AiSourceChip> BuildChips(AIResult result, AIResultItem item)
    {
        var chips = new List<AiSourceChip>();
        foreach (var label in item.SourceLabels)
        {
            if (result.Sources.FirstOrDefault(s => string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase)) is { } byLabel)
                AddChip(chips, byLabel);
        }
        foreach (var id in item.SourceTabIds)
        {
            if (result.Sources.FirstOrDefault(s => s.TabId == id) is { } byId)
                AddChip(chips, byId);
        }
        return chips;
    }

    private static void AddChip(List<AiSourceChip> chips, ContextSource source)
    {
        if (chips.Any(c => c.TabId == source.TabId)) return;
        chips.Add(new AiSourceChip(source.Label, source.Title, source.Url, source.TabId));
    }

    private static IReadOnlyList<AiSourceChip> BuildSources(AIResult result) =>
        result.Sources.Select(s => new AiSourceChip(s.Label, s.Title, s.Url, s.TabId)).ToList();

    // ---- Source traceability / clipboard -----------------------------

    private async void OnActivateSource(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Guid tabId) return;
        // Close first so the source tab is presented in the real window
        // rather than behind a modal surface.
        try { Hide(); } catch { }
        try { await _vm.ActivateAiSourceAsync(tabId); } catch { }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _vm.AiResult?.ToPlainText();
            if (string.IsNullOrWhiteSpace(text)) return;
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Footnote.Text = "Copied. Sources are included, so the result stays traceable.";
        }
        catch
        {
            Footnote.Text = "Couldn't copy to the clipboard.";
        }
    }

    private void UpdateAskButton()
    {
        var hasQuestion = !string.IsNullOrWhiteSpace(QuestionBox.Text);
        AskButton.IsEnabled = hasQuestion && !Busy.IsActive;
    }

    /// <summary>
    /// Bridge from an answer to a workspace artifact: carry the question the
    /// user just asked into the Canvas surface as the intent, so a good
    /// answer can become a reusable generated workspace.
    /// </summary>
    private async void OnBuildCanvas(object sender, RoutedEventArgs e)
    {
        var intent = !string.IsNullOrWhiteSpace(_vm.AiCommandText) ? _vm.AiCommandText : QuestionBox.Text;
        try { Hide(); } catch { }
        try
        {
            var dlg = new CanvasDialog { InitialIntent = intent?.Trim() };
            if (XamlRoot is not null) dlg.XamlRoot = XamlRoot;
            App.ApplyDialogTheme(dlg);
            await dlg.ShowAsync();
        }
        catch { }
    }
}