namespace QwenPlayground.Core.Serialization;

/// <summary>
/// Атомарная запись файлов состояния (settings.json, chat.json, layers.json, memories/*.json,
/// journal.json): сначала во временный файл в том же каталоге, затем замена через
/// File.Replace — атомарна на NTFS. Прямой WriteAllText при сбое/выключении посреди записи
/// оставляет битый JSON; для сессий и памяти агента это потеря состояния без бэкапа.
///
/// Имя temp уникально на запись: писатели одного файла не делят один .tmp (иначе «A записал
/// tmp → B перезаписал tmp → A сделал Replace» атомарно публикует полузаписанный файл B).
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            File.WriteAllText(temp, contents);
            // Замена может споткнуться о мгновенную блокировку (антивирус/индексатор успел
            // открыть свежий файл) — «Не удается удалить заменяемый файл». Ретрай с бэкоффом;
            // последняя попытка — прямая запись: потеря атомарности лучше потери файла.
            var delays = new[] { 50, 150, 400, 900 };
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        if (attempt < delays.Length)
                        {
                            File.Replace(temp, path, destinationBackupFileName: null);
                            return;
                        }
                        // Последний резерв: обычная запись поверх (temp уже содержит данные).
                        File.Copy(temp, path, overwrite: true);
                        File.Delete(temp);
                        return;
                    }
                    File.Move(temp, path);
                    return;
                }
                catch (IOException) when (attempt < delays.Length)
                {
                    Thread.Sleep(delays[attempt]);
                }
            }
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // не мешаем пробросу исходной ошибки уборкой временного файла
            }
            throw;
        }
    }
}
