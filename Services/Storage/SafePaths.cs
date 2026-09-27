using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;

namespace NexLauncher.Services.Storage;

public static class SafePaths
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new InvalidDataException("Недопустимый относительный путь.");
        foreach (var part in relative.Split('/'))
        {
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') ||
                new[] { "CON", "CONIN$", "CONOUT$", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Недопустимое имя файла в metadata: " + relative);
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы сборки.");
        NoLinks(destination);
        return destination;
    }

    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Ссылки и junction в папке сборки не поддерживаются: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string LocalRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\") || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Выбери локальную папку с полным путём.");
        var result = Path.GetFullPath(path);
        NoLinks(result);
        return result;
    }

    // CmlLib and official installers write many paths internally. Check an existing tree before
    // handing it to either library, not only before starting the installer after Vanilla repair.
    public static Task CheckTreeAsync(string root, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); NoLinks(root);
        if (!Directory.Exists(root)) return;
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Ссылки и junction в папке сборки не поддерживаются: " + entry);
                if (Directory.Exists(entry)) directories.Push(entry);
            }
    }, token);

    public static string GamePath(string dataDirectory, GameInstance instance)
    {
        if (!Guid.TryParse(instance.Id, out var id) || id == Guid.Empty)
            throw new ArgumentException("Некорректный идентификатор сборки.");
        var relative = id.ToString("N") + "/game";
        if (string.IsNullOrEmpty(instance.GameDirectory)) return Resolve(Path.Combine(dataDirectory, "instances"), relative);
        var path = LocalRoot(instance.GameDirectory);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? throw new InvalidDataException("Некорректная папка сборки.");
        if (!string.Equals(path, Resolve(root, relative), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Папка сборки должна иметь структуру <root>/<id>/game.");
        return path;
    }

    public static Task<string> CheckWritableRootAsync(string path, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var root = LocalRoot(path);
        var drive = Path.GetPathRoot(root)!;
        if (!Directory.Exists(drive)) throw new IOException("Диск папки сборок недоступен.");
        Directory.CreateDirectory(root);
        var probe = Resolve(root, ".nex-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return root;
    }, token);

    // Only call for a newly created, owned staging directory. Never follow a junction during cleanup.
    public static void DeleteStaging(string allowedRoot, string ownedPath)
    {
        var relative = Path.GetRelativePath(allowedRoot, ownedPath).Replace('\\', '/');
        var verified = Resolve(allowedRoot, relative);
        if (!Path.GetFileName(verified).StartsWith(".nex-stage-", StringComparison.Ordinal))
            throw new InvalidDataException("Удалять можно только собственную staging-папку.");
        if (!Directory.Exists(verified)) return;
        DeleteDirectory(verified);
    }

    private static void DeleteDirectory(string directory)
    {
        NoLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            NoLinks(entry);
            if (Directory.Exists(entry)) DeleteDirectory(entry); else File.Delete(entry);
        }
        Directory.Delete(directory);
    }
}
