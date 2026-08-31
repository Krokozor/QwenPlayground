using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Переключает вкладку собственного окна, чтобы агент мог посмотреть экраны, отличные от чата.
/// В связке со screenshot: switch_tab → screenshot → вижу → оцениваю.
/// </summary>
[Tool("switch_tab",
    "Switch the tab of your own app window so you can inspect a different screen. Known tabs: " +
    "'Чат' (chat), 'Превью промпта' (prompt preview), 'Настройки' (settings), 'Память' (memory), " +
    "'Суммаризация' (summarization), 'Диагностика' (diagnostics). Accepts the tab header text " +
    "(case-insensitive) or a 1-based index. Pair with the screenshot tool to see the result.")]
public sealed class SwitchTabTool : AgentTool
{
    [ToolParameter("Tab to switch to: header text (e.g. 'Настройки') or 1-based index (e.g. '3').", Required = true)]
    public string Tab { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var app = Application.Current;
        if (app is null)
        {
            return Task.FromResult("Error: no WPF application is available.");
        }
        var result = app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is not { } window || FindTabControl(window) is not { } tabs)
            {
                return "Error: the main window or its TabControl was not found.";
            }
            var wanted = Tab.Trim();
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                if (tabs.Items[i] is not TabItem item)
                {
                    continue;
                }
                var header = item.Header?.ToString() ?? string.Empty;
                var matches = string.Equals(header, wanted, StringComparison.OrdinalIgnoreCase) ||
                              (int.TryParse(wanted, out var index) && index - 1 == i);
                if (matches)
                {
                    tabs.SelectedIndex = i;
                    return $"Switched to tab '{header}' (index {i + 1}). Take a screenshot to see it.";
                }
            }
            var available = string.Join(", ", tabs.Items.OfType<TabItem>().Select(t => $"'{t.Header}'"));
            return $"Error: tab '{wanted}' not found. Available tabs: {available}.";
        });
        return Task.FromResult(result);
    }

    /// <summary>Ищет первый TabControl в визуальном дереве окна.</summary>
    private static TabControl? FindTabControl(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TabControl tabControl)
            {
                return tabControl;
            }
            if (child is DependencyObject dependencyObject)
            {
                var found = FindTabControl(dependencyObject);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
    }
}
