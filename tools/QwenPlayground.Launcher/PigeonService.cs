using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace QwenPlayground.Launcher;

/// <summary>
/// Управление жизненным циклом pigeon-сервера (docs/MESSANGER.md) из лаунчера.
///
/// Сервер — резидентный процесс, который НЕ ребилдится вместе с харнессами и
/// переживает их пересборки; значит его lifecycle — забота лаунчера, а не приложения:
///   Собрать → external/pigeon/pigeon.exe, Запустить/Остановить, статус-зонд.
///
/// Данные: data/pigeon (конфиг с owner-токеном, jsonl-ленты, копилка файлов, лог).
/// Статус определяется не по своему PID, а probe'ом порта — так виден и сервер,
/// запущенный вручную (лаунчер не претендует на эксклюзивность процесса).
/// </summary>
public sealed class PigeonService
{
    private static Process? _process;

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

    /// <summary>Собрать сервер (вместе с Pigeon.Client) в external/pigeon/.</summary>
    public static async Task<(bool success, string message)> BuildServerAsync(string workspaceRoot)
    {
        Stop(); // работающий exe блокирует файл пересборки
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

        var process = Process.Start(psi);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        // ExitCode читаем ДО Dispose: после Dispose процесс «отвязывается»
        // и любое обращение бросает InvalidOperationException (краш 2026-10-18).
        var exitCode = process.ExitCode;
        process.Dispose();

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
            return true;
        }

        var exe = ServerExePath(workspaceRoot);
        if (!File.Exists(exe))
        {
            return false;
        }

        _process = Process.Start(new ProcessStartInfo(exe, $"\"{DataDir(workspaceRoot)}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(exe) ?? workspaceRoot,
        });

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (await IsRunningAsync(port))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Остановить: убивает и «свой» процесс, и любой pigeon-процесс (запущенный
    /// вручную тоже) — в UI всегда виден один честный статус.
    /// </summary>
    public static void Stop()
    {
        foreach (var process in Process.GetProcessesByName("pigeon"))
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // уже умер или не дали — не критично
            }
        }
        _process = null;
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
}
