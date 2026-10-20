using System;
using System.Collections.Generic;
using System.Linq;
using NexLauncher.Models;

namespace NexLauncher.Services.Diagnostics;

/// <summary>Independent deterministic signatures; a stack frame alone never identifies a guilty mod.</summary>
public static class CrashRules
{
    private sealed record Rule(string Code, string[] Signatures, string Title, string Description, string Advice,
        DiagnosticConfidence Confidence = DiagnosticConfidence.Likely);
    private static readonly Rule[] Rules =
    [
        new("missing-dependency", ["Missing mandatory dependencies", "Missing or unsupported mandatory dependencies", "requires version", "depends on", "Mod resolution encountered an incompatible mod set"],
            "Зависимости или версии модов", "Загрузчик сообщает о невыполненном требовании. Точные mod ID и ограничения указаны в доказательствах.", "Проверь требование и совместимые версии в Модах. Не устанавливай Fabric API без конкретного требования."),
        new("fabric-loading", ["Incompatible mods found!", "net.fabricmc.loader.impl.FormattedException"], "Ошибка Fabric", "Fabric не смог подготовить набор модов.", "Проверь более конкретные ошибки ниже и metadata модов."),
        new("forge-loading", ["net.minecraftforge.fml.ModLoadingException", "net.minecraftforge.fml.LoadingFailedException"], "Ошибка загрузки Forge", "Forge сообщает об ошибке загрузки мода.", "Проверь версии Minecraft, Forge и зависимости указанного мода."),
        new("neoforge-loading", ["net.neoforged.fml.ModLoadingException", "net.neoforged.fml.LoadingFailedException"], "Ошибка загрузки NeoForge", "NeoForge сообщает об ошибке загрузки мода.", "Используй версии модов именно для NeoForge и текущего Minecraft."),
        new("duplicate-mod", ["DuplicateModsFoundException", "Duplicate mods found", "Found duplicate mods"], "Дубликаты модов", "Загрузчик обнаружил повторяющиеся моды.", "Открой папку mods, сравни mod ID и сохрани backup перед ручным удалением."),
        new("java-mismatch", ["UnsupportedClassVersionError", "has been compiled by a more recent version of the Java Runtime"], "Несовместимая Java", "Байт-код требует другую версию Java; это может быть игра или конкретный мод.", "Сравни class-file version в логе с требованиями игры и мода. Попробуй автоматическую Java."),
        new("mixin", ["MixinApplyError", "MixinTransformerError", "InvalidMixinException", "InjectionError"], "Ошибка Mixin", "Не удалось применить изменение кода. Возможны несовместимые версии или конфликт модов.", "Проверь первую Mixin-ошибку, target class и версии вовлечённых модов.", DiagnosticConfidence.Possible),
        new("memory", ["java.lang.OutOfMemoryError", "Could not reserve enough space for"], "Недостаточно памяти", "Java сообщает, что выделить память не удалось.", "Проверь свободную RAM и лимит сборки. Увеличение лимита не исправляет любую утечку.", DiagnosticConfidence.Confirmed),
        new("jar-corrupt", ["java.util.zip.ZipException", "Invalid or corrupt jarfile"], "Повреждённый архив", "Java не смогла прочитать один из архивов.", "Найди имя файла в логе и проверь hash. Не удаляй неизвестные файлы автоматически."),
        new("graphics", ["GLFW error 65542", "GLFW error 65543", "Failed to create window", "No OpenGL context found", "Pixel format not accelerated"], "Инициализация графики", "Клиент не смог создать графический контекст.", "Проверь официальный драйвер GPU, поддержку OpenGL и запуск вне неподдерживаемой удалённой среды."),
        new("class-missing", ["java.lang.ClassNotFoundException", "java.lang.NoClassDefFoundError"], "Класс недоступен", "В цепочке ошибки есть отсутствующий класс. По одной строке нельзя определить первопричину.", "Проверь контекст ошибки и зависимости мода.", DiagnosticConfidence.Possible),
        new("mod-init", ["Could not execute entrypoint stage", "Exception during mod initialization"], "Ошибка инициализации мода", "Мод не смог завершить инициализацию.", "Проверь вложенную причину и совместимость версий.")
    ];
    public static List<DiagnosticIssue> Analyze(string text, bool abnormal)
    {
        if (!abnormal) return [];
        var lines = LauncherLog.Sanitize(text).Split('\n');
        var result = new List<DiagnosticIssue>();
        foreach (var rule in Rules)
        {
            var evidence = lines.Where(line => rule.Signatures.Any(s => line.Contains(s, StringComparison.OrdinalIgnoreCase)) &&
                (rule.Code != "missing-dependency" || line.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("incompatible mod set", StringComparison.OrdinalIgnoreCase) || line.Contains("but", StringComparison.OrdinalIgnoreCase)))
                .Take(3).Select(x => x.Trim()).ToArray();
            if (evidence.Length > 0)
                result.Add(new(rule.Code, DiagnosticSeverity.Error, rule.Confidence, rule.Title, rule.Description,
                    string.Join("\n", evidence.Select(x => x.Length > 1000 ? x[..1000] : x)), rule.Advice, DiagnosticFix.OpenMods));
        }
        if (result.Count == 0) result.Add(new("unknown-crash", DiagnosticSeverity.Warning, DiagnosticConfidence.Unknown,
            "Причина не определена", "Minecraft завершился ненормально, но достоверной сигнатуры в доступных данных нет.", "Ненормальное завершение процесса; log может быть неполным.", "Открой исходный лог и crash report. Не меняй моды наугад."));
        return result;
    }
}
