using System.Diagnostics;
using System.IO;
using System.Linq;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

/// <summary>
/// Обмен версиями приложения и запуск. Два входа:
/// - headless self-rebuild (из приложения): <see cref="RunSwapped"/> — ждёт старый процесс,
///   потом меняет версию; без окна;
/// - GUI-кнопки лаунчера: <see cref="StartCurrent"/> и <see cref="RebuildAndStartAsync"/>.
///
/// Pointer-режим: run/&lt;buildId&gt;/ — неизменяемый каталог версии, swap = запись current.txt.
/// Почему так: на Windows каталог нельзя удалить/переименовать, пока процесс держит его
/// (в т.ч. через CWD). Pointer-режим не подвержен этому классу сбоев.
/// </summary>
public static class SwapService
{
    // Лаунчер манипулирует каталогом версий run/. Его собственный каталог (launcher/) — только
    // для бинарей; корень версий всегда SelfBuildPaths.RunRoot (workspace/run). Резолвим так,
    // чтобы и из dev-билда (bin/Debug) корень был правильным.
    private static readonly string Root = Directory.Exists(SelfBuildPaths.RunRoot)
        ? SelfBuildPaths.RunRoot
        : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private const string ExeName = "QwenPlayground.App.exe";

    private static void Log(string message) =>
        File.AppendAllText(Path.Combine(Root, "launcher.log"), $"[{DateTime.Now:O}] {message}\n");

    /// <summary>Headless self-rebuild: дождаться старый процесс, обменять версии (pointer или legacy).</summary>
    public static int RunSwapped(int pid, string? buildId)
    {
        Environment.CurrentDirectory = Root;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(60_000))
            {
                // Висящий shutdown (сервис в Shutdown без таймаута) не должен блокировать
                // обмен вечно: старая версия уже не нужна — убиваем и деплоим новую.
                // Без этого сценария приложение «умирает посреди ничего»: закрылось,
                // а новая версия так и не стартовала, и никто об этом не знает.
                Log($"old process {pid} did not exit within 60s, killing");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch
        {
        }
        Thread.Sleep(1000);
        Log($"old process {pid} exited, deploying in {(buildId is null ? "legacy" : "pointer")} mode");
        return buildId is not null ? PointerMode(buildId) : LegacyMode();
    }

    public static string? CurrentVersionId()
    {
        var pointerFile = Path.Combine(Root, "current.txt");
        if (File.Exists(pointerFile))
        {
            var id = File.ReadAllText(pointerFile).Trim();
            if (id.Length > 0)
            {
                return id;
            }
        }
        return Directory.Exists(Path.Combine(Root, "current")) ? "current" : null;
    }

    /// <summary>Запустить активную версию приложения из корректной папки. Возвращает PID.</summary>
    public static int StartCurrent()
    {
        var versionId = CurrentVersionId();
        if (versionId is null)
        {
            throw new InvalidOperationException("нет активной версии (current.txt / current/)");
        }
        var workspaceRoot = GetWorkspaceRoot();
        var app = Process.Start(new ProcessStartInfo(Path.Combine(Root, versionId, ExeName))
        {
            WorkingDirectory = workspaceRoot,
            UseShellExecute = false,
            Environment = { ["QWENPLAYGROUND_ROOT"] = workspaceRoot }
        });
        WriteAppPid(app?.Id);
        Log($"started current version {versionId} (pid {app?.Id}, root={workspaceRoot})");
        return app?.Id ?? -1;
    }

    /// <summary>
    /// Корень проекта: из launcher.json или вычисленный (родитель run/ с .slnx).
    /// </summary>
    private static string GetWorkspaceRoot()
    {
        var config = LauncherConfig.Load();
        var root = config.EffectiveWorkspaceRoot;
        // Верификация: там должно быть .slnx
        if (File.Exists(Path.Combine(root, SelfBuildPaths.SolutionFileName)))
        {
            return root;
        }
        // Fallback: старый метод
        return WorkspaceRoot(Root);
    }

    /// <summary>Пересобрать из исходников (build + тест-гейт) и перезапустить в новую версию.</summary>
    public static async Task<(int ExitCode, string Message)> RebuildAndStartAsync(CancellationToken cancellationToken)
    {
        KillRunningApp();
        Log("GUI rebuild requested");
        var result = await SelfBuildService.BuildNextAsync(cancellationToken);
        if (result.ExitCode != 0)
        {
            return (result.ExitCode, $"сборка не удалась (exit {result.ExitCode}):\n{result.OutputTail}");
        }
        var swapCode = PointerMode(result.Id);
        return swapCode == 0
            ? (0, $"пересобрано и запущено: {result.Id}")
            : (swapCode, "обмен версиями не удался — подробности в launcher.log");
    }

    /// <summary>Закрыть работающий экземпляр приложения (по имени и app.pid), чтобы swap был безопасным.</summary>
    private static void KillRunningApp()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            if (process.Id == Environment.ProcessId)
            {
                continue;
            }
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                Log($"killed running app pid {process.Id}");
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        var pidFile = Path.Combine(Root, "app.pid");
        if (File.Exists(pidFile))
        {
            File.Delete(pidFile);
        }
    }

    private static void WriteAppPid(int? pid)
    {
        if (pid is not null && pid.Value > 0)
        {
            File.WriteAllText(Path.Combine(Root, "app.pid"), pid.Value.ToString());
        }
    }

    // ---------- pointer-режим: версии — неизменяемые каталоги, swap = запись указателя ----------

    private static int PointerMode(string buildId)
    {
        var versionDir = Path.Combine(Root, buildId);
        if (!File.Exists(Path.Combine(versionDir, ExeName)))
        {
            Log($"abort: {ExeName} not found in {buildId}/");
            return 1;
        }

        var pointerFile = Path.Combine(Root, "current.txt");
        var oldId = File.Exists(pointerFile) ? File.ReadAllText(pointerFile).Trim() : null;
        if (string.IsNullOrEmpty(oldId) && Directory.Exists(Path.Combine(Root, "current")))
        {
            oldId = "current"; // миграция с legacy-layout: откат возможен в старый каталог
        }

        File.WriteAllText(pointerFile, buildId);
        Log($"pointer -> {buildId} (previous: {oldId ?? "none"})");

        try
        {
            var marker = Path.Combine(versionDir, "ok.marker");
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            var wsRoot = GetWorkspaceRoot();
            var app = Process.Start(new ProcessStartInfo(Path.Combine(versionDir, ExeName))
            {
                WorkingDirectory = wsRoot,
                UseShellExecute = false,
                Environment = { ["QWENPLAYGROUND_ROOT"] = wsRoot }
            });

            if (WaitHandshake(app, marker))
            {
                WriteAppPid(app?.Id);
                Log($"handshake OK, new version is running (root={wsRoot})");
                BuildJournal.UpdateLast(Root, "success", null);
                GarbageCollectVersions(buildId);
                return 0;
            }

            var reason = app is { HasExited: true }
                ? $"process exited with code {app.ExitCode} before handshake"
                : "handshake timeout (30s)";
            Log($"startup failed: {reason}; rolling back");
            KillQuietly(app);
            if (!string.IsNullOrEmpty(oldId) && File.Exists(Path.Combine(Root, oldId, ExeName)))
            {
                File.WriteAllText(pointerFile, oldId);
                var rollback = Process.Start(new ProcessStartInfo(Path.Combine(Root, oldId, ExeName))
                {
                    WorkingDirectory = wsRoot,
                    UseShellExecute = false,
                    Environment = { ["QWENPLAYGROUND_ROOT"] = wsRoot }
                });
                WriteAppPid(rollback?.Id);
                Log($"rolled back to {oldId}");
            }
            BuildJournal.UpdateLast(Root, "failed", reason);
            return 1;
        }
        catch (Exception exception)
        {
            // Указатель уже переключён: сбой на этом участке (права на exe, корень
            // воркспейса, маркер…) оставил бы его на версии, которая так и не стартовала.
            // Откатываем — иначе StartCurrent запускал бы «призрака».
            Log($"FATAL: unexpected error after pointer switch: {exception.Message}; rolling back pointer");
            if (!string.IsNullOrEmpty(oldId) && File.Exists(Path.Combine(Root, oldId, ExeName)))
            {
                File.WriteAllText(pointerFile, oldId);
                Log($"pointer rolled back to {oldId}");
            }
            BuildJournal.UpdateLast(Root, "failed", exception.Message);
            return 1;
        }
    }

    // ---------- legacy-режим: next/ → current/ без удаления каталога ----------

    private static int LegacyMode()
    {
        var current = Path.Combine(Root, "current");
        var next = Path.Combine(Root, "next");
        var backup = Path.Combine(Root, "backup");

        if (!File.Exists(Path.Combine(next, ExeName)))
        {
            Log($"abort: {ExeName} not found in next/");
            return 1;
        }

        try
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            CopyTree(next, backup);
        }
        catch (Exception exception)
        {
            Log($"backup creation failed: {exception.Message}");
            return 1;
        }

        Exception? copyError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                OverwriteCopy(next, current);
                copyError = null;
                break;
            }
            catch (Exception exception)
            {
                copyError = exception;
                Log($"overwrite attempt {attempt + 1} failed: {exception.Message}");
                Thread.Sleep(2000);
            }
        }

        if (copyError is not null)
        {
            Log($"overwrite failed permanently: {copyError.Message}; restoring backup");
            try
            {
                OverwriteCopy(backup, current);
                var restored = Process.Start(new ProcessStartInfo(Path.Combine(current, ExeName))
                {
                    WorkingDirectory = WorkspaceRoot(Root),
                    UseShellExecute = false
                });
                WriteAppPid(restored?.Id);
                BuildJournal.UpdateLast(Root, "failed", $"swap failed: {copyError.Message}");
                Log("backup restored and started");
            }
            catch (Exception exception)
            {
                Log($"FATAL: backup restore failed: {exception.Message}");
            }
            return 1;
        }

        // Устаревшие файлы из старых версий не трогаем: .NET грузит то, что в deps.json.
        Log("overwrite done, starting new version");
        var marker = Path.Combine(current, "ok.marker");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }

        var app = Process.Start(new ProcessStartInfo(Path.Combine(current, ExeName))
        {
            WorkingDirectory = WorkspaceRoot(Root),
            UseShellExecute = false
        });

        if (WaitHandshake(app, marker))
        {
            WriteAppPid(app?.Id);
            Log("handshake OK, new version is running");
            BuildJournal.UpdateLast(Root, "success", null);
            return 0;
        }

        var reason = app is { HasExited: true }
            ? $"process exited with code {app.ExitCode} before handshake"
            : "handshake timeout (30s)";
        Log($"startup failed: {reason}; rolling back");
        KillQuietly(app);
        var rolledBack = Process.Start(new ProcessStartInfo(Path.Combine(current, ExeName))
        {
            WorkingDirectory = WorkspaceRoot(Root),
            UseShellExecute = false
        });
        WriteAppPid(rolledBack?.Id);
        BuildJournal.UpdateLast(Root, "failed", reason);
        Log("rollback done, previous version started");
        return 1;
    }

    // ---------- общие помощники ----------

    private static bool WaitHandshake(Process? app, string marker)
    {
        var deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            if (File.Exists(marker))
            {
                return true;
            }
            if (app is { HasExited: true })
            {
                return false;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static void KillQuietly(Process? app)
    {
        try
        {
            if (app is { HasExited: false })
            {
                app.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        Thread.Sleep(1000);
    }

    /// <summary>Корень воркспейса: родитель run/, если там есть решение; иначе сам родитель.</summary>
    private static string WorkspaceRoot(string runRoot)
    {
        var parent = Path.GetDirectoryName(runRoot);
        if (parent is not null && File.Exists(Path.Combine(parent, SelfBuildPaths.SolutionFileName)))
        {
            return parent;
        }
        return parent ?? runRoot;
    }

    /// <summary>Полная копия дерева (для backup).</summary>
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>Перезапись файлов поверх destination без удаления каталога (иммунитет к CWD-локам).</summary>
    private static void OverwriteCopy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            OverwriteCopy(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>GC версий: оставляем текущую и две последних по имени (id = yyyyMMdd-HHmmss).</summary>
    private static void GarbageCollectVersions(string currentId)
    {
        const int keep = 3;
        try
        {
            var versions = Directory.EnumerateDirectories(Root)
                .Select(d => Path.GetFileName(d))
                .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{8}-\d{6}$"))
                .OrderByDescending(name => name)
                .ToList();
            // Оставляем текущую версию и (keep-1) последних: после отката на старую
            // версию именно она должна пережить GC.
            var toKeep = new HashSet<string> { currentId };
            foreach (var name in versions.Take(keep - 1))
            {
                toKeep.Add(name);
            }
            foreach (var name in versions.Where(n => !toKeep.Contains(n)))
            {
                Directory.Delete(Path.Combine(Root, name), recursive: true);
                Log($"GC: removed old version {name}");
            }
        }
        catch (Exception exception)
        {
            Log($"GC skipped: {exception.Message}");
        }
    }
}
