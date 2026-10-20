using System;
using System.IO;

namespace NexLauncher.Services;

public static class MinecraftRuntimePolicy
{
    public static string RequireExecutableJava(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Java для этой версии Minecraft и архитектуры не найдена. Укажи совместимый runtime в настройках сборки.");
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidOperationException("У выбранной Java нет права выполнения. Выбери исполняемый bin/java из установленного JDK или восстанови автоматический runtime.");
        return path;
    }
}
