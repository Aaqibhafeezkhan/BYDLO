using System.Text;

namespace Bydlo.Core.Themes;

/// <summary>File storage for installed custom themes under <c>&lt;data&gt;/themes</c>.</summary>
public static class CustomThemeStore
{
    public static string FileName(string id) => id + ".json";

    public static string ThemesDirectory(string dataDirectory) => Path.Combine(dataDirectory, "themes");

    /// <summary>All valid installed themes. Broken files are skipped so they cannot break startup.</summary>
    public static IReadOnlyList<CustomThemeDocument> ListAll(string themesDirectory)
    {
        if (!Directory.Exists(themesDirectory))
        {
            return [];
        }
        var result = new List<CustomThemeDocument>();
        foreach (var file in Directory.EnumerateFiles(themesDirectory, "*.json"))
        {
            try
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                if (Encoding.UTF8.GetByteCount(content) > CustomTheme.MaxFileBytes)
                {
                    continue;
                }
                result.Add(CustomTheme.Parse(content));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CustomThemeException)
            {
            }
        }
        return result.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Exists(string themesDirectory, string id) =>
        File.Exists(Path.Combine(themesDirectory, FileName(id)));

    public static CustomThemeDocument? TryLoad(string themesDirectory, string id)
    {
        var path = Path.Combine(themesDirectory, FileName(id));
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var content = File.ReadAllText(path, Encoding.UTF8);
            if (Encoding.UTF8.GetByteCount(content) > CustomTheme.MaxFileBytes)
            {
                return null;
            }
            var doc = CustomTheme.Parse(content);
            return doc.Id == id ? doc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CustomThemeException)
        {
            return null;
        }
    }

    /// <summary>Validates and saves the exact file content the user provided.</summary>
    public static CustomThemeDocument Save(string themesDirectory, string content)
    {
        var doc = CustomTheme.Parse(content);
        Directory.CreateDirectory(themesDirectory);
        var temporary = Path.Combine(themesDirectory, FileName(doc.Id) + ".tmp");
        File.WriteAllText(temporary, content, Encoding.UTF8);
        File.Move(temporary, Path.Combine(themesDirectory, FileName(doc.Id)), overwrite: true);
        return doc;
    }

    public static bool Delete(string themesDirectory, string id)
    {
        var path = Path.Combine(themesDirectory, FileName(id));
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
