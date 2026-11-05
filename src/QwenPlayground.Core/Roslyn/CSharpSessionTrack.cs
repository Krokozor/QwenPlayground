using System.Xml.Linq;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Per-chat C#-трек (фаза 2, 2026-11-05): ЧАТ — источник информации о том, какой проект
/// трекается. Хранится в папке сессии (csharp.json, рядом с chat.json/shelves.json) и
/// переживает рестарт. Трек НЕЗАВИСИМ от рабочей папки (root): смена root'а не сбрасывает
/// трек; явный трек выигрывает у home-преднастройки (преднастройка — дефолт, когда трека нет).
/// Формат: { "projectFile": "V:\\...\\Game.sln", "projects": ["Game", "Game.Editor"] }
/// </summary>
public static class CSharpSessionTrack
{
    public const string FileName = "csharp.json";

    public sealed record Track(string ProjectFile, string[] Projects, string[] Recent);

    public static string FilePath(string sessionDir) => Path.Combine(sessionDir, FileName);

    /// <summary>
    /// Загрузка трека из папки сессии. null — трека нет (файла нет, пуст или повреждён —
    /// повреждённый файл не роняет приложение, трактуется как «нет трека»).
    /// </summary>
    public static Track? Load(string sessionDir)
    {
        try
        {
            var path = FilePath(sessionDir);
            if (!File.Exists(path)) return null;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var file = doc.RootElement.TryGetProperty("projectFile", out var f) ? f.GetString() : null;
            if (string.IsNullOrWhiteSpace(file)) return null;

            var projects = new List<string>();
            if (doc.RootElement.TryGetProperty("projects", out var p)
                && p.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in p.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) projects.Add(s);
                }
            }

            // История треков чата (recent): для быстрого переключения в UI-меню.
            var recent = new List<string>();
            if (doc.RootElement.TryGetProperty("recent", out var r)
                && r.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in r.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) recent.Add(s);
                }
            }

            return new Track(file, projects.ToArray(), recent.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Атомарное сохранение (temp + replace), как у остальных файлов сессии.</summary>
    public static void Save(string sessionDir, string projectFile, string[] projects, string[]? recent = null)
    {
        var path = FilePath(sessionDir);
        Directory.CreateDirectory(sessionDir);
        var payload = new
        {
            projectFile,
            projects = projects ?? Array.Empty<string>(),
            recent = recent ?? Array.Empty<string>()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    /// <summary>
    /// Оттрекать проект + добавить его в историю чата (recent, дедупликация, кап 10).
    /// Единая точка записи трека: csharp_track, UI-пикер, UI-меню.
    /// </summary>
    public static void TrackProject(string sessionDir, string projectFile, string[] projects)
    {
        var old = Load(sessionDir);
        Save(sessionDir, projectFile, projects, PushRecent(old?.Recent, projectFile));
    }

    /// <summary>Файл в начало истории (дедупликация, регистр не важен — пути Windows), максимум <paramref name="max"/> записей.</summary>
    public static string[] PushRecent(string[]? recent, string projectFile, int max = 10)
    {
        var list = new List<string>();
        foreach (var item in recent ?? Array.Empty<string>())
        {
            if (!list.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(item);
            }
        }
        // Убираем существующую запись (case-insensitive: List.Remove — чувствителен к регистру).
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], projectFile, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
                break;
            }
        }
        list.Insert(0, projectFile);
        return list.Take(max).ToArray();
    }

    /// <summary>Снять трек (сброс к дефолту: home-преднастройка для home-root'а).</summary>
    public static void Clear(string sessionDir)
    {
        var path = FilePath(sessionDir);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Список проектов из САМОГО файла солюшена (slnx/sln — текст, воркспейс не нужен —
    /// холодная загрузка ради счётчика запрещена). Счётчик в <state> и тул csharp_projects
    /// работают отсюда. csproj — один проект. Не распарсилось — пустой массив
    /// (трек при этом работает, счётчик 0).
    /// </summary>
    public static string[] ParseProjects(string projectFile)
    {
        try
        {
            return Path.GetExtension(projectFile).ToLowerInvariant() switch
            {
                ".slnx" => ParseSlnx(projectFile),
                ".sln" => ParseSln(projectFile),
                ".csproj" => new[] { Path.GetFileNameWithoutExtension(projectFile) },
                _ => Array.Empty<string>(),
            };
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // slnx — XML: <Project Path="..." Name="..."/> (могут быть вложенными в <Folder>).
    private static string[] ParseSlnx(string path)
    {
        var doc = XDocument.Load(path);
        var names = new List<string>();
        foreach (var el in doc.Descendants("Project"))
        {
            var name = (string)el.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                var p = (string)el.Attribute("Path");
                name = p is null ? null : Path.GetFileNameWithoutExtension(p);
            }
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names.ToArray();
    }

    // sln — текст: Project("{GUID}") = "Name", "path\to\csproj", "{GUID2}"
    private static string[] ParseSln(string path)
    {
        var names = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Project(\"", StringComparison.Ordinal)) continue;
            var first = line.IndexOf('"');
            var eq = line.IndexOf(") = \"", first);
            if (eq < 0) continue;
            var nameStart = eq + 5;
            var nameEnd = line.IndexOf('"', nameStart);
            if (nameEnd < 0) continue;
            var name = line[nameStart..nameEnd];
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names.ToArray();
    }
}
