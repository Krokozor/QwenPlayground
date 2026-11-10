using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Launcher;

/// <summary>Реестр и жизненный цикл известных pigeon-серверов.</summary>
public sealed class PigeonService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string? RegistryPathOverride { get; set; }

    private static string RegistryPath => RegistryPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QwenPlayground", "pigeon-instances.json");

    public static string ServerExePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "external", "pigeon", "pigeon.exe");

    public static string DataDir(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "data", "pigeon");

    public static bool IsBuilt(string workspaceRoot) => File.Exists(ServerExePath(workspaceRoot));

    /// <summary>Порт из data/pigeon.config.json (дефолт 8199 — как в ServerConfig).</summary>
    public static int GetPort(string workspaceRoot)
    {
        var path = Path.Combine(DataDir(workspaceRoot), "pigeon.config.json");
        if (File.Exists(path))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("port", out var port) && port.GetInt32() > 0)
                {
                    return port.GetInt32();
                }
            }
            catch
            {
                // нет конфига/не распарсился — дефолт
            }
        }
        return 8199;
    }

    /// <summary>Живые зарегистрированные экземпляры; stale-записи удаляются.</summary>
    public static IReadOnlyList<PigeonInstance> RefreshInstances(string? workspaceRoot = null)
    {
        lock (Gate)
        {
            var instances = LoadUnlocked();
            var changed = instances.RemoveAll(instance => !IsLive(instance)) > 0;
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                changed |= DiscoverWorkspaceUnlocked(workspaceRoot, instances);
            }
            if (changed)
            {
                SaveUnlocked(instances);
            }
            return instances
                .OrderBy(instance => instance.WorkspaceRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.Pid)
                .ToList();
        }
    }

    /// <summary>Собрать сервер (вместе с Pigeon.Client) в external/pigeon/.</summary>
    public static async Task<(bool success, string message)> BuildServerAsync(string workspaceRoot)
    {
        Stop(workspaceRoot); // работающий exe блокирует файл пересборки
        var project = Path.Combine(workspaceRoot, "tools", "pigeon", "Pigeon.Server", "Pigeon.Server.csproj");
        if (!File.Exists(project))
        {
            return (false, "не найден источник: tools/pigeon/Pigeon.Server");
        }

        var outDir = Path.Combine(workspaceRoot, "external", "pigeon");
        var psi = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release -o \"{outDir}\"")
        {
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (false, "не удалось запустить dotnet build");
        }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var exitCode = process.ExitCode;

        if (exitCode == 0)
        {
            return (true, "сервер собран: external/pigeon/pigeon.exe");
        }

        var tail = (stderr + stdout)
            .Split('\n')
            .Where(line => line.Trim().Length > 0)
            .Take(15);
        return (false, "сборка не прошла:\n" + string.Join("\n", tail));
    }

    /// <summary>Запустить сервер и дождаться, что порт отвечает (до 10 с).</summary>
    public static async Task<bool> StartAsync(string workspaceRoot)
    {
        var port = GetPort(workspaceRoot);
        if (await IsRunningAsync(port))
        {
            RefreshInstances(workspaceRoot);
            return true;
        }
        if (RefreshInstances(workspaceRoot).Any(instance => SamePath(instance.WorkspaceRoot, workspaceRoot)))
        {
            return false; // процесс жив, но ещё не отвечает — второй в тот же DataDir не запускаем
        }

        var exe = ServerExePath(workspaceRoot);
        if (!File.Exists(exe))
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo(exe, $"\"{DataDir(workspaceRoot)}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(exe) ?? workspaceRoot,
            UseShellExecute = false,
        });
        if (process is null)
        {
            return false;
        }

        lock (Gate)
        {
            var instances = LoadUnlocked();
            instances.RemoveAll(instance => instance.Pid == process.Id);
            instances.Add(NewInstance(workspaceRoot, process, launchedByLauncher: true));
            SaveUnlocked(instances);
        }

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (await IsRunningAsync(port))
            {
                return true;
            }
            if (process.HasExited)
            {
                break;
            }
        }
        return false;
    }

    /// <summary>Остановить экземпляры только указанной рабочей папки.</summary>
    public static void Stop(string workspaceRoot)
    {
        var instances = RefreshInstances(workspaceRoot)
            .Where(instance => SamePath(instance.WorkspaceRoot, workspaceRoot))
            .Select(instance => instance.Pid)
            .ToList();
        foreach (var pid in instances)
        {
            Stop(pid);
        }
    }

    /// <summary>Остановить процесс только после проверки PID, StartTime и пути к exe.</summary>
    public static bool Stop(int pid)
    {
        lock (Gate)
        {
            var instances = LoadUnlocked();
            var instance = instances.FirstOrDefault(candidate => candidate.Pid == pid);
            if (instance is null)
            {
                return false;
            }
            using var process = GetLiveProcess(instance);
            if (process is null)
            {
                instances.Remove(instance);
                SaveUnlocked(instances);
                return false;
            }
            try
            {
                process.Kill();
                process.WaitForExit(2000);
            }
            catch
            {
                return false;
            }
            instances.Remove(instance);
            SaveUnlocked(instances);
            return true;
        }
    }

    /// <summary>Зонд: GET /api/channels (публичный, без auth). 200 — сервер жив.</summary>
    public static async Task<bool> IsRunningAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync($"http://127.0.0.1:{port}/api/channels");
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private static bool DiscoverWorkspaceUnlocked(string workspaceRoot, List<PigeonInstance> instances)
    {
        var exe = ServerExePath(workspaceRoot);
        var changed = false;
        foreach (var process in Process.GetProcessesByName("pigeon"))
        {
            using (process)
            {
                if (process.HasExited || instances.Any(instance => instance.Pid == process.Id))
                {
                    continue;
                }
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }
                if (path is null || !SamePath(path, exe))
                {
                    continue;
                }
                instances.Add(NewInstance(workspaceRoot, process, launchedByLauncher: false));
                changed = true;
            }
        }
        return changed;
    }

    private static PigeonInstance NewInstance(string workspaceRoot, Process process, bool launchedByLauncher) => new()
    {
        WorkspaceRoot = Path.GetFullPath(workspaceRoot),
        DataDir = DataDir(workspaceRoot),
        ExecutablePath = ServerExePath(workspaceRoot),
        Port = GetPort(workspaceRoot),
        Pid = process.Id,
        ProcessStartTimeUtc = process.StartTime.ToUniversalTime(),
        LaunchedByLauncher = launchedByLauncher,
    };

    private static bool IsLive(PigeonInstance instance)
    {
        using var process = GetLiveProcess(instance);
        return process is not null;
    }

    private static Process? GetLiveProcess(PigeonInstance instance)
    {
        try
        {
            var process = Process.GetProcessById(instance.Pid);
            if (process.HasExited ||
                Math.Abs((process.StartTime.ToUniversalTime() - instance.ProcessStartTimeUtc).TotalSeconds) > 1 ||
                !SamePath(process.MainModule?.FileName, instance.ExecutablePath))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch
        {
            return null;
        }
    }

    private static List<PigeonInstance> LoadUnlocked()
    {
        try
        {
            return File.Exists(RegistryPath)
                ? JsonSerializer.Deserialize<List<PigeonInstance>>(File.ReadAllText(RegistryPath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveUnlocked(IEnumerable<PigeonInstance> instances)
    {
        AtomicFile.WriteAllText(RegistryPath, JsonSerializer.Serialize(instances, JsonOptions));
    }

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
