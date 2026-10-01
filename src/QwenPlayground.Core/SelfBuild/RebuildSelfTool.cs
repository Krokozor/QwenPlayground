using System.Xml;
using Microsoft.CodeAnalysis;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.SelfBuild;

/// <summary>
/// rebuild_self — рискованная операция: приложение пересобирает СОБСТВЕННЫЙ рантайм.
///
/// МОДЕЛЬ БЕЗОПАСНОСТИ (почему операция допустима и что её охраняет):
///
/// Ничего изнутри приложения не гарантирует, что новая версия будет работать. Тест-гейт
/// покрывает тупые ошибки в лучшем случае; логический краш новой версии старая версия
/// увидеть не может — она уже мёртва к моменту, когда новый код пошёл бы в исполнение.
///
/// Единственная гарантия — ЛАУНЧЕР (внешний процесс, «следующий островок безопасности»):
/// приложение выходит → лаунчер переключает pointer → стартует новую версию → ждёт
/// handshake (30 с, ok.marker пишется в MainWindow Loaded) → либо OK (новая версия жива),
/// либо kill + rollback на старую. Прыжок либо на новый островок, либо назад на старый —
/// третьего исхода нет. Эту гарантию НЕЛЬЗЯ воспроизвести изнутри приложения: self-launch
/// без handshake/rollback (попытка 2026-10-02, отклонена владельцем) её скомпрометировал
/// бы — при неудачном старте pointer остался бы на битой версии, а возвращать некому.
///
/// Поэтому ПЕРЕД ребилдом проверяется страховка (CheckRestartSafety, в самом начале
/// ExecuteAsync): лаунчер запущен, инстанс ровно ОДИН (два лаунчера ломали deploy —
/// 2026-10-01 15:27, MSB3027) и watchdog в строю (фиксирует смерть процесса). Без
/// исправной страховки ребилд НЕ выполняется: приложение вышло бы, а запустить новую
/// версию и откатить было бы некому (инцидент 2026-10-02 01:07 — клиент лежал до
/// ручного старта). «Починка» этого отказом внутри приложения запрещена — только
/// запускать лаунчер и повторять rebuild_self.
/// </summary>
[Tool("rebuild_self",
    "Rebuild the QwenPlayground application itself from source and restart into the new version. " +
    "Use after modifying the application's own code. Runs a pre-check (XAML XML validation + Roslyn " +
    "C# diagnostics), then the full dotnet build (runs the XAML compiler) and the test gate. " +
    "On failure returns the errors; fix them and call again. " +
    "SAFETY: the restart is guaranteed by the launcher GUI (handshake 30s + rollback to the old " +
    "version). If the launcher is not running (or not exactly one instance, or the watchdog is " +
    "missing) the tool REFUSES to build — open the launcher and call again. " +
    "XAML: invalid XML (e.g. a raw '<' in an attribute) is reported directly with file:line:col. " +
    "XAML bindings / x:Name issues are caught by the full build with MC#### codes.")]
public sealed class RebuildSelfTool : AgentTool
{
    // Общий Roslyn-воркспейс — не создавать свой, см. RoslynService.Shared.
    private static readonly Roslyn.RoslynService Service = Roslyn.RoslynService.Shared;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        // ПРОВЕРКА СТРАХОВКИ — ПЕРВОЕ, до любой работы (см. safety model в шапке класса):
        // без исправного лаунчера ребилд оставляет приложение без возврата на старую версию.
        var safety = CheckRestartSafety();
        if (safety is not null)
        {
            return $"Error: {safety}";
        }

        // XAML-валидация: Roslyn не гоняет XAML-компилятор, поэтому невалидный XML в .xaml
        // (сырой '<' в атрибуте и т.п.) проявляется как ложный CS0103 'InitializeComponent'.
        // Проверяем XML напрямую — корневая причина с точной строкой/колонкой.
        var xamlErrors = CollectXamlXmlErrors();
        var roslynErrors = await CollectRoslynErrors(cancellationToken);
        var allErrors = xamlErrors.Concat(roslynErrors).ToList();
        if (allErrors.Count > 0)
        {
            return $"Error: {allErrors.Count} problem(s) found; fix them before rebuilding:\n" +
                   string.Join('\n', allErrors);
        }

        // Rebuild-окно: BuildNextAsync внутри вызывает PreDeployTools (StopWatchdog =
        // BeginRebuild), но окно закрываем МЫ — в finally, в любом исходе: иначе
        // EnsureAlive (heartbeat) не восстановит стража после упавшей сборки.
        BuildResult result;
        try
        {
            result = await SelfBuildService.BuildNextAsync(cancellationToken);
        }
        finally
        {
            QwenPlayground.Core.Crash.WatchdogLauncher.EndRebuild();
        }
        if (result.ExitCode != 0)
        {
            return $"Error: build failed (exit code {result.ExitCode}). Fix the errors and call rebuild_self again.\n{result.OutputTail}";
        }

        // Git status: где мы, что запушено. Пуш — только если включён в настройках.
        var gitInfo = GetGitStatus();
        var pushInfo = MaybePush();

        SelfBuildService.RequestRestart(result.Id);
        // Приложение само выходит (App-хук): если продолжить ход, UI-поток останется занят —
        // graceful-kill watchdog'а не дождётся закрытия, и новый процесс запустится рядом со
        // старым (два процесса, общие сессии — инцидент 2026-09-23).
        QwenPlayground.Core.Main.Main.Instance?.RestartRequested?.Invoke();
        // Ворнинги (если были) — в конце отчёта: не блокируют, но должны быть видны.
        var warningsInfo = result.Warnings is null ? string.Empty : $"\n{result.Warnings}";
        return $"Build {result.Id} succeeded. The application will now restart into the new version.\n" +
               $"Git: {gitInfo}" + (pushInfo is null ? string.Empty : $"\n{pushInfo}") + warningsInfo;
    }

    /// <summary>
    /// Проверка страховки перед ребилдом (safety model в шапке класса). null — страховка
    /// в строю; иначе — причина отказа (приложение продолжает жить, ничего не сломано).
    /// </summary>
    private static string? CheckRestartSafety()
    {
        var launchers = System.Diagnostics.Process.GetProcessesByName("QwenPlayground.Launcher");
        try
        {
            if (launchers.Length == 0)
            {
                return "launcher GUI is not running. The launcher is the guarantee of a safe restart " +
                       "(handshake 30s + rollback to the old version); without it the app would exit and " +
                       "nobody would start the new version (incident 2026-10-02 01:07). Open the launcher " +
                       "and call rebuild_self again.";
            }
            if (launchers.Length > 1)
            {
                return $"there are {launchers.Length} launcher instances — exactly one is required " +
                       "(two simultaneous launchers broke the watchdog deploy on 2026-10-01 15:27, MSB3027). " +
                       "Close the extra ones and call rebuild_self again.";
            }
        }
        finally
        {
            foreach (var launcher in launchers)
            {
                launcher.Dispose();
            }
        }

        // Watchdog — стража процесса (фиксирует нативную смерть в crash-лог). В dev-сборке
        // exe может отсутствовать (TryStart — no-op) — тогда не блокируем.
        var watchdogExe = Path.Combine(SelfBuildPaths.LauncherDir, WatchdogLauncher.WatchdogExeName);
        if (File.Exists(watchdogExe) && !WatchdogLauncher.IsWatchdogRunning())
        {
            return "the watchdog is not running (the guardian that records process death). This is " +
                   "abnormal — EnsureAlive (20s heartbeat) should have started it. Restart the app and " +
                   "call rebuild_self again.";
        }
        return null;
    }

    /// <summary>
    /// Краткий git-статус для логирования в результат rebuild: где HEAD и сколько коммитов
    /// не запушено (только КОНСТАТАЦИЯ — пуш не делает; пуш — MaybePush по настройке).
    /// </summary>
    private static string GetGitStatus()
    {
        var root = SelfBuildPaths.WorkspaceRoot;
        var (_, head) = RunGit(root, "log -1 --oneline");
        if (string.IsNullOrEmpty(head))
        {
            return "not a git repo";
        }
        var (_, upstream) = RunGit(root, "rev-parse --verify -q @{u}");
        if (string.IsNullOrEmpty(upstream))
        {
            return $"{head} (no upstream)";
        }
        var (_, unpushed) = RunGit(root, "rev-list --count @{u}..HEAD");
        return unpushed == "0"
            ? $"{head} (up-to-date with origin)"
            : $"{head} ({unpushed} unpushed commit(s))";
    }

    /// <summary>
    /// Пуш после успешного билда, только если включён в настройках (PushOnRebuild, по умолчанию
    /// выкл): git push уже закоммиченных коммитов. Инструмент НЕ коммитит — коммиты делает
    /// владелец/агент явно. null — пуш выключен.
    /// Направление: PushRepo (URL/remote, например форк) — если задан; иначе текущий upstream.
    /// </summary>
    private static string? MaybePush()
    {
        var settings = Settings.AppSettings.Get();
        if (!settings.PushOnRebuild)
        {
            return null;
        }
        var root = SelfBuildPaths.WorkspaceRoot;

        string pushArgs;
        if (!string.IsNullOrWhiteSpace(settings.PushRepo))
        {
            // Явное направление (форк): HEAD текущей ветки → main цели.
            pushArgs = $"push {settings.PushRepo.Trim()} HEAD:main";
        }
        else
        {
            var (_, upstream) = RunGit(root, "rev-parse --verify -q @{u}");
            if (string.IsNullOrEmpty(upstream))
            {
                return "push: no upstream — skipped (укажите «куда пушить» в настройках или установите upstream)";
            }
            pushArgs = "push";
        }

        var (code, output) = RunGit(root, pushArgs, timeoutMs: 60000);
        return code == 0
            ? $"push: ok ({(output.Length > 0 ? output : "nothing to push")})"
            : $"push: FAILED (exit {code}) — {output}";
    }

    private static (int ExitCode, string Output) RunGit(string workingDir, string args, int timeoutMs = 10000)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return (-1, "");
            // git пишет результат в stderr (push: "To https://…") — читаем оба потока.
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(timeoutMs);
            var output = (stdout + (stderr.Length > 0 ? (stdout.Length > 0 ? " " : "") + stderr : "")).Trim();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }

    /// <summary>
    /// Прямая валидация .xaml как XML: ловит невалидный XML (сырой '&lt;'/'&gt;' в атрибуте,
    /// незакрытый тег) с точной строкой/колонкой ДО того, как Roslyn покажет ложный каскад
    /// 'InitializeComponent не существует'. Только синтаксис XML — привязки/x:Name судит
    /// реальный dotnet build (MC####).
    /// </summary>
    private static List<string> CollectXamlXmlErrors()
    {
        var errors = new List<string>();
        var srcDir = Path.Combine(SelfBuildPaths.WorkspaceRoot, "src");
        if (!Directory.Exists(srcDir))
        {
            return errors;
        }
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.xaml", SearchOption.AllDirectories))
        {
            try
            {
                using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                while (reader.Read())
                {
                }
            }
            catch (XmlException ex)
            {
                var rel = Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, file);
                var line = ex.LineNumber > 0 ? ex.LineNumber.ToString() : "?";
                var col = ex.LinePosition > 0 ? ex.LinePosition.ToString() : "?";
                errors.Add($"XAML {rel}:{line}:{col}: {ex.Message}");
            }
            catch
            {
                // файл не прочитался — пропускаем (не ломаем гейт)
            }
            if (errors.Count >= 50)
            {
                break;
            }
        }
        return errors;
    }

    private static async Task<List<string>> CollectRoslynErrors(CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var errors = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }
                var position = diagnostic.Location.GetLineSpan();
                var rawPath = position.Path;
                // Roslyn не гоняет XAML-компилятор: CS0103/CS0117 в code-behind (.xaml.cs)
                // на сгенерированные XAML-члены (InitializeComponent, x:Name) — ложные. Их
                // ловит реальный dotnet build с точным MC####-сообщением (он идёт следом).
                if (rawPath is not null &&
                    rawPath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) &&
                    (diagnostic.Id == "CS0103" || diagnostic.Id == "CS0117"))
                {
                    continue;
                }
                var path = rawPath is not null
                    ? Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, rawPath)
                    : "?";
                errors.Add($"{diagnostic.Id} {path}:{position.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                if (errors.Count >= 50)
                {
                    return errors;
                }
            }
        }
        return errors;
    }
}
