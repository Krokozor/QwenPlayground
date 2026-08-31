using System.Text.Json;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Изолированный тип настроек для тестов Update: свой файл, не трогает реальный
/// settings.json. Путь — под tests/, файл создаётся/удаляется тестом.
/// </summary>
[SettingsFile("tests/settings-update-probe.json")]
internal sealed class ProbeSettings
{
    public int Counter { get; set; }
    public string Label { get; set; } = "x";
}

public sealed class SettingsStoreTests
{
    [Fact]
    public void Deserialize_ReasoningEffort_LowercaseString_MapsToEnum()
    {
        // Старый settings.json хранил строку "medium" в нижнем регистре.
        const string json = """{"ReasoningEffort":"medium"}""";

        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Equal(ReasoningEffort.Medium, settings!.ReasoningEffort);
    }

    [Fact]
    public void Deserialize_ReasoningEffort_Missing_DefaultsToXHigh()
    {
        const string json = """{}""";

        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Equal(ReasoningEffort.XHigh, settings!.ReasoningEffort);
    }

    [Fact]
    public void Update_MutatesLiveInstance_PersistsToDisk_AndFiresChanged()
    {
        var path = Path.Combine(SelfBuildPaths.WorkspaceRoot, "tests", "settings-update-probe.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var changed = 0;
        void Handler(ProbeSettings _) => changed++;
        SettingsStore<ProbeSettings>.Changed += Handler;
        try
        {
            SettingsStore<ProbeSettings>.Reload();
            var before = SettingsStore<ProbeSettings>.Get().Counter;

            SettingsStore<ProbeSettings>.Update(s => s.Counter = before + 1);

            // 1) Живой экземпляр (источник правды в процессе) изменён.
            Assert.Equal(before + 1, SettingsStore<ProbeSettings>.Get().Counter);
            // 2) Записано на диск.
            Assert.True(File.Exists(path));
            var fromDisk = JsonSerializer.Deserialize<ProbeSettings>(File.ReadAllText(path));
            Assert.Equal(before + 1, fromDisk!.Counter);
            // 3) Событие Changed поднято ровно один раз.
            Assert.Equal(1, changed);
        }
        finally
        {
            SettingsStore<ProbeSettings>.Changed -= Handler;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}