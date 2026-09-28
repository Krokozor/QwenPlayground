using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

/// <summary>
/// Управление инструментами (ffmpeg и др.): скачивание, экстракция, проверка версий.
///
/// Контракт раскладки: «каталог архива срезается» (<see cref="ToolConfig.StripTopLevelDir"/>).
/// Релизные zip всегда заворачивают содержимое в каталог верхнего уровня
/// (ffmpeg-master-latest-win64-gpl/, poppler-26.09.0/), поэтому BinPath в конфиге
/// версионно-независим: external/ffmpeg/bin/ffmpeg.exe, external/poppler/Library/bin/pdftotext.exe.
/// Без срезания BinPath пришлось бы знать версию заранее и он ломался бы на каждом релизе.
/// </summary>
public static class ToolManager
{
    /// <summary>Клиент загрузки: без общего таймаута (архив ffmpeg — ~200 МБ).</summary>
    private static readonly HttpClient Download = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>Клиент GitHub API. User-Agent обязателен: без него api.github.com отвечает 403.</summary>
    private static readonly HttpClient Api = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>Общий предел на всю установку (скачивание + экстракция), чтобы не висеть вечно.</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(2);

    /// <summary>Предел наversion-зонд: бинарь может зависнуть (например, ждать ввода).</summary>
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(10);

    static ToolManager()
    {
        // GitHub API отклоняет запросы без User-Agent (403 «Request forbidden by administrative
        // rules») — это .NET-специфика: HttpClient сам заголовок не добавляет, в отличие от
        // браузера. Один заголовок на оба клиента: загрузка с github.com тоже его требует.
        Download.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Api.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private const string UserAgent = "QwenPlayground-Launcher";

    /// <summary>Сайдкар с digest/тегом/датой релиза, по которому «Проверить обновления» узнаёт сборку.</summary>
    private const string AssetInfoFileName = ".asset-info";

    private static string Log(string message)
    {
        var logPath = Path.Combine(SelfBuildPaths.RunRoot, "launcher.log");
        var line = $"[{DateTime.Now:O}] [tool] {message}";
        File.AppendAllText(logPath, line + "\n");
        return line;
    }

    /// <summary>Проверить, установлен ли инструмент.</summary>
    public static bool IsInstalled(ToolConfig tool, string? workspaceRoot = null) => tool.IsInstalled(workspaceRoot);

    /// <summary>
    /// Версия установленного инструмента (запуск с <see cref="ToolConfig.VersionArgs"/>).
    /// null — бинаря нет, он не ответил или ответил пустотой. Ошибка не «съедается»
    /// молча: зонд читает ОБА потока, потому что poppler-утилиты печатают версию в stderr.
    /// </summary>
    public static async Task<string?> GetInstalledVersionAsync(ToolConfig tool, string? workspaceRoot = null)
    {
        var binPath = tool.BinPathFor(workspaceRoot);
        if (!File.Exists(binPath)) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = binPath,
                Arguments = string.IsNullOrWhiteSpace(tool.VersionArgs) ? "-version" : tool.VersionArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        try
        {
            process.Start();
            // Оба потока читаем до конца иначе процесс встанет на переполненном пайпе:
            // буфер анонимного пайпа ~4 КБ, а читается только первая строка stdout.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(VersionProbeTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Зонд не должен висеть: убиваем и считаем версию неизвестной.
                try { process.Kill(entireProcessTree: true); } catch { /* процесс уже мёртв */ }
            }
            return FirstMeaningfulLine(await ReadQuietlyAsync(stdout))
                ?? FirstMeaningfulLine(await ReadQuietlyAsync(stderr));
        }
        catch (Exception ex)
        {
            Log($"version probe failed for {tool.BinPath}: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> ReadQuietlyAsync(Task<string> read)
    {
        try
        {
            return await read;
        }
        catch
        {
            // Поток читается «до конца» — на killed-процессе бывает IOException/ObjectDisposed.
            return string.Empty;
        }
    }

    /// <summary>Первая непустая строка без ведущих пробелов; null — поток пуст/мусорный.</summary>
    private static string? FirstMeaningfulLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ', '\t');
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        return null;
    }

    /// <summary>
    /// Скачать и установить инструмент. Возвращает (success, message).
    ///
    /// Распаковка идёт во временный каталог и подменяет рабочую копию только после того,
    /// как бинарник найден: оборванная загрузка или битый архив оставляют прежнюю
    /// установку целой (раньше каталог удалялся до экстракции, и неудача роняла инструмент).
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallAsync(
        ToolConfig tool, IProgress<string>? progress = null, string? workspaceRoot = null)
    {
        // 1. Определяем, что именно качаем. Шаблон с {tag}/{version} требует свежего
        //    релиза — и заодно даёт digest того ассета, который мы сейчас скачаем
        //    (сайдкар пишем отсюда, без повторного запроса: релиз мог бы смениться между
        //    скачиванием и сохранением, и тогда «актуальная» была бы не та, что стоит).
        var (release, error) = await TryGetLatestReleaseAsync(tool);
        if (release is null && UsesReleaseTag(tool.DownloadUrl))
        {
            return (false, $"не удалось определить тег релиза: {error}");
        }
        var url = release?.DownloadUrl ?? tool.DownloadUrl;

        var root = workspaceRoot ?? SelfBuildPaths.WorkspaceRoot;
        var extractDir = Path.Combine(root, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        if (!TryBinRelativeToExtractDir(tool, out var binRel))
        {
            return (false, $"BinPath ({tool.BinPath}) должен лежать внутри ExtractTo ({tool.ExtractTo})");
        }
        var safeName = tool.ExtractTo.Replace('/', '_').Replace('\\', '_');
        var tempZip = Path.Combine(Path.GetTempPath(), $"qpw_{safeName}_{DateTime.Now:HHmmss}.zip");
        var stagingDir = extractDir + ".staging-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            using var cts = new CancellationTokenSource(InstallTimeout);

            progress?.Report("Скачивание...");
            Log($"downloading {url}");

            using (var response = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                }

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;
                var buffer = new byte[81920];
                int read;

                using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var fileStream = File.Create(tempZip);
                while ((read = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    downloadedBytes += read;
                    if (totalBytes > 0)
                    {
                        var percent = (int)(downloadedBytes * 100 / totalBytes);
                        progress?.Report($"Скачивание... {percent}% ({downloadedBytes / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)");
                    }
                }
            }

            progress?.Report("Экстракция...");
            Log($"extracting to {stagingDir}");
            Directory.CreateDirectory(stagingDir);
            ExtractToolZip(tool, tempZip, stagingDir);

            // Бинарник ищем в распакованном, а не в боевом каталоге: пока он не найден,
            // рабочая копия не тронута.
            var stagedBin = Path.Combine(stagingDir, binRel);
            if (!File.Exists(stagedBin))
            {
                return (false, $"бинарник не найден после экстракции: {Path.Combine(extractDir, binRel)} " +
                              $"(проверь BinPath и раскладку архива; в архиве: {Describe(stagingDir)})");
            }

            // README инструмента переживает переустановку: он лежит в git и это единственный
            // источник секции «внешние инструменты» в системном промпте, а в архивах его нет.
            var docsBackup = TryReadDocs(tool, extractDir);

            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
            Directory.Move(stagingDir, extractDir);
            if (docsBackup is not null)
            {
                WriteDocs(tool, extractDir, docsBackup);
            }

            if (release is not null)
            {
                SaveAssetInfo(tool, release, root);
            }

            Log($"installed {tool.ExtractTo} successfully");
            return (true, "установлено успешно");
        }
        catch (OperationCanceledException)
        {
            Log($"install timed out after {InstallTimeout.TotalHours:F0}h: {url}");
            return (false, $"превышен лимит времени ({InstallTimeout.TotalHours:F0} ч) — оборвано на середине");
        }
        catch (Exception ex)
        {
            Log($"install failed: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { /* временный файл — не жалко */ }
            }
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* недособранный мусор */ }
            }
        }
    }

    /// <summary>
    /// Путь BinPath относительно каталога экстракции. Заодно проверяет, что BinPath
    /// вообще внутри ExtractTo: иначе бинарник и README оказались бы в разных папках.
    /// </summary>
    private static bool TryBinRelativeToExtractDir(ToolConfig tool, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(tool.BinPath) || string.IsNullOrWhiteSpace(tool.ExtractTo))
        {
            return false;
        }
        var extractRel = tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        var binRel = tool.BinPath.Replace('/', Path.DirectorySeparatorChar);
        var prefix = extractRel + Path.DirectorySeparatorChar;
        if (!binRel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || binRel.Length <= prefix.Length)
        {
            return false;
        }
        relative = binRel[prefix.Length..];
        return true;
    }

    /// <summary>Краткий перечень того, что реально распаковалось — для сообщения об ошибке.</summary>
    private static string Describe(string dir)
    {
        try
        {
            return string.Join(", ", Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Take(8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "не читается";
        }
    }

    /// <summary>
    /// Распаковать архив инструмента в <paramref name="extractDir"/>.
    /// Вынесено отдельно от установки: раскладку важно тестировать без сети.
    /// </summary>
    public static void ExtractToolZip(ToolConfig tool, string zipPath, string extractDir)
    {
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        if (tool.StripTopLevelDir)
        {
            StripSingleRootDir(extractDir);
        }
    }

    /// <summary>
    /// Поднять содержимое на уровень выше, если архив — «одна папка внутри».
    /// Релизные zip с обоих источников (BtbN/FFmpeg-Builds, oschwartz10612/poppler-windows)
    /// заворачивают всё в каталог с именем сборки; без срезания BinPath пришлось бы
    /// знать версию заранее.
    /// </summary>
    private static void StripSingleRootDir(string extractDir)
    {
        var entries = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }
        // Список детей снимаем заранее: перемещение мутирует каталог, который обходим.
        foreach (var child in Directory.GetFileSystemEntries(entries[0]).ToArray())
        {
            Directory.Move(child, Path.Combine(extractDir, Path.GetFileName(child)));
        }
        Directory.Delete(entries[0]);
    }

    /// <summary>
    /// Удалить инструмент (каталог). README инструмента переживает удаление: он в git
    /// и описывает, как пользоваться инструментом, а не как он установлен.
    /// </summary>
    public static bool Uninstall(ToolConfig tool, string? workspaceRoot = null)
    {
        var extractDir = Path.Combine(workspaceRoot ?? SelfBuildPaths.WorkspaceRoot,
            tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(extractDir))
        {
            return false;
        }
        var docs = TryReadDocs(tool, extractDir);
        Directory.Delete(extractDir, recursive: true);
        if (docs is not null)
        {
            WriteDocs(tool, extractDir, docs);
        }
        Log($"uninstalled {tool.ExtractTo}");
        return true;
    }

    /// <summary>README инструмента из каталога установки; null — нет (или не прочитался).</summary>
    private static string? TryReadDocs(ToolConfig tool, string extractDir)
    {
        var docs = Path.Combine(extractDir, tool.DocsFileName);
        try
        {
            return File.Exists(docs) ? File.ReadAllText(docs) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Заперт другим процессом — не повод отказывать в удалении бинарей.
            return null;
        }
    }

    private static void WriteDocs(ToolConfig tool, string extractDir, string content)
    {
        try
        {
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(extractDir, tool.DocsFileName), content);
        }
        catch (Exception ex)
        {
            // Потеря раздела промпта — не повод считать установку неудачной.
            Log($"docs write failed for {tool.ExtractTo}: {ex.Message}");
        }
    }

    // ── Проверка обновлений ─────────────────────────────────────────────────────────

    /// <summary>
    /// «Паспорт» скачанной сборки: digest ассета (sha256), тег и дата публикации релиза.
    /// Релиз-тег «latest» у авто-сборок обновляется на месте, поэтому именно digest
    /// ассета — честный идентификатор конкретной сборки.
    /// </summary>
    public sealed record AssetInfo(string Digest, string Tag, string PublishedAt);

    /// <summary>Сайдкар-файл с метаданными сборки в каталоге экстракции инструмента.</summary>
    private static string AssetInfoPath(ToolConfig tool, string? workspaceRoot = null) =>
        Path.Combine(workspaceRoot ?? SelfBuildPaths.WorkspaceRoot,
            tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar), AssetInfoFileName);

    /// <summary>Шаблон URL ссылается на «последний релиз» — тег надо узнать у API.</summary>
    private static bool UsesReleaseTag(string downloadUrl) =>
        downloadUrl.Contains("{tag}", StringComparison.Ordinal) ||
        downloadUrl.Contains("{version}", StringComparison.Ordinal);

    /// <summary>Тег без ведущей «v»: v26.09.0-0 → 26.09.0-0 (имя ассета у poppler без неё).</summary>
    internal static string VersionFromTag(string tag) =>
        tag.Length > 1 && tag[0] == 'v' && char.IsDigit(tag[1]) ? tag[1..] : tag;

    /// <summary>Подставить тег релиза в шаблон URL: {tag} → v26.09.0-0, {version} → 26.09.0-0.</summary>
    internal static string SubstituteReleaseTags(string urlTemplate, string tag) =>
        urlTemplate
            .Replace("{tag}", tag, StringComparison.Ordinal)
            .Replace("{version}", VersionFromTag(tag), StringComparison.Ordinal);

    /// <summary>
    /// Последний релиз репозитория из DownloadUrl + ассет, соответствующий шаблону.
    /// (Release, null) — всё хорошо; (null, причина) — с причиной, а не «что-то не так».
    /// </summary>
    public sealed record LatestRelease(
        string Tag, string Version, string PublishedAt, string Digest, string AssetName, string DownloadUrl);

    public static async Task<(LatestRelease? Release, string? Error)> TryGetLatestReleaseAsync(ToolConfig tool)
    {
        if (!TryParseGitHubAssetUrl(tool.DownloadUrl, out var owner, out var repo, out _, out _))
        {
            return (null, "источник не является релизом GitHub");
        }
        try
        {
            using var response = await Api.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            if (!response.IsSuccessStatusCode)
            {
                // 403/429 — лимит GitHub API (60 запросов/час на IP без токена): это самая
                // частая причина, и она не про «сеть», поэтому называем её прямо.
                var reason = (int)response.StatusCode is 403 or 429
                    ? "GitHub API: превышен лимит запросов (60/час без токена), попробуйте позже"
                    : $"GitHub API: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return (null, reason);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var publishedAt = root.GetProperty("published_at").GetString() ?? "";
            var url = SubstituteReleaseTags(tool.DownloadUrl, tag);
            var assetName = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != assetName)
                {
                    continue;
                }
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                return (new LatestRelease(tag, VersionFromTag(tag), publishedAt, digest, assetName, url), null);
            }

            return (null,
                $"в последнем релизе {tag} нет ассета «{assetName}» — имя ассета зависит от версии, проверьте шаблон DownloadUrl");
        }
        catch (Exception ex)
        {
            // Сеть/DNS/TLS/таймаут: единственный класс ошибок, который честно назвать своим именем.
            return (null, $"сеть недоступна ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Разобрать URL ассета релиза GitHub: /&lt;owner&gt;/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;asset&gt;.
    /// Шаблоны {tag}/{version} не мешают: owner/repo и имя ассета берутся из позиций,
    /// а имя ассета уточняется подстановкой тега.
    /// </summary>
    private static bool TryParseGitHubAssetUrl(
        string downloadUrl, out string owner, out string repo, out string tag, out string assetName)
    {
        owner = repo = tag = assetName = string.Empty;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[2] != "releases")
        {
            return false;
        }
        owner = parts[0];
        repo = parts[1];
        tag = parts[3];
        assetName = parts[^1];
        return true;
    }

    /// <summary>Сохранить метаданные сборки в сайдкар (best-effort).</summary>
    public static void SaveAssetInfo(ToolConfig tool, LatestRelease release, string? workspaceRoot = null)
    {
        try
        {
            var path = AssetInfoPath(tool, workspaceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{release.Digest}\n{release.Tag}\n{release.PublishedAt}\n");
        }
        catch (Exception ex)
        {
            // Метаданные — не критичны для установки: без них «Проверить обновления»
            // честно скажет «локально нет метаданных», установка при этом успешна.
            Log($"asset-info save failed for {tool.ExtractTo}: {ex.Message}");
        }
    }

    /// <summary>Прочитать метаданные локальной сборки. null — не установлены/нет сайдкора.</summary>
    public static AssetInfo? LoadAssetInfo(ToolConfig tool, string? workspaceRoot = null)
    {
        try
        {
            var lines = File.ReadAllLines(AssetInfoPath(tool, workspaceRoot));
            if (lines.Length >= 3)
            {
                return new AssetInfo(lines[0], lines[1], lines[2]);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Сайдкара нет — обычное состояние (инструмент поставлен до их введения).
        }
        catch (Exception ex)
        {
            Log($"asset-info read failed for {tool.ExtractTo}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Настоящая проверка обновлений: digest локальной сборки (сайдкар) против
    /// последнего релиза из GitHub API. Возвращает человекочитаемое сообщение.
    /// </summary>
    public static async Task<string> CheckUpdateAsync(ToolConfig tool, string? workspaceRoot = null)
    {
        if (!tool.IsInstalled(workspaceRoot))
        {
            return "не установлен";
        }
        var (release, error) = await TryGetLatestReleaseAsync(tool);
        if (release is null)
        {
            return $"проверка не удалась: {error}";
        }
        var local = LoadAssetInfo(tool, workspaceRoot);
        if (local is null)
        {
            return $"последняя сборка {release.Tag} от {release.PublishedAt}; локально нет метаданных версии " +
                   "(установлено до их введения) — нажмите «Скачать»";
        }
        return release.Digest == local.Digest
            ? $"актуальная версия: {release.Tag} от {release.PublishedAt}"
            : $"доступна новая сборка {release.Tag} от {release.PublishedAt} " +
              $"(у вас {local.Tag} от {local.PublishedAt}) — нажмите «Скачать»";
    }
}
