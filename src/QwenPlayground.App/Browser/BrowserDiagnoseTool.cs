using System.IO;
using Microsoft.Web.WebView2.Core;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Browser;

[Tool("browser_diagnose", "Diagnose the built-in browser (WebView2) status. " +
                          "Checks runtime installation, environment creation, control state, and user data folder. " +
                          "Use when browser tools are failing or to check browser health.")]
public sealed class BrowserDiagnoseTool : AgentTool
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Browser Diagnostics ===");
        sb.AppendLine();

        // 1. Runtime check (registry)
        sb.AppendLine("[1] WebView2 Runtime:");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (key is not null)
            {
                var version = key.GetValue("pv") as string ?? "unknown";
                sb.AppendLine($"  Installed: {version}");
            }
            else
            {
                using var key2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
                if (key2 is not null)
                {
                    var version = key2.GetValue("pv") as string ?? "unknown";
                    sb.AppendLine($"  Installed: {version}");
                }
                else
                {
                    sb.AppendLine("  NOT FOUND in registry!");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Error reading registry: {ex.Message}");
        }
        sb.AppendLine();

        // 2. Control state
        sb.AppendLine("[2] WebView2 Control:");
        if (!BrowserService.IsAttached)
        {
            sb.AppendLine("  NOT ATTACHED (ChatView not loaded?)");
        }
        else
        {
            sb.AppendLine($"  Attached: yes");
            sb.AppendLine($"  CoreWebView2 available: {BrowserService.HasCore}");
        }
        sb.AppendLine();

        // 3. Try creating environment explicitly
        sb.AppendLine("[3] Environment Creation Test:");
        try
        {
            var userDataFolder = Path.Combine(Path.GetTempPath(), "QwenPlayground_WebView2");
            sb.AppendLine($"  User data folder: {userDataFolder}");
            sb.AppendLine($"  Folder writable: {TestWrite(userDataFolder)}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);
            sw.Stop();
            sb.AppendLine($"  Environment created: OK ({sw.ElapsedMilliseconds}ms)");
            sb.AppendLine($"  Environment version: {env.BrowserVersionString}");

            // 4. Try creating a controller (needs an HWND — skip if no control)
            sb.AppendLine();
            sb.AppendLine("[4] Control Init State:");
            if (BrowserService.IsAttached)
            {
                var diag = await BrowserService.GetDiagnosticsAsync();
                sb.AppendLine(diag);
            }
            else
            {
                sb.AppendLine("  Skipped (no control attached)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                sb.AppendLine($"  Inner: {ex.InnerException.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("=== End Diagnostics ===");
        return sb.ToString();
    }

    private static bool TestWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var testFile = Path.Combine(folder, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
