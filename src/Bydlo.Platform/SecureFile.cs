namespace Bydlo.Platform;

/// <summary>Writes files that contain credentials with owner-only permissions where the OS supports it.</summary>
public static class SecureFile
{
    public static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using var fs = new FileStream(path, options);
        await using var writer = new StreamWriter(fs);
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
