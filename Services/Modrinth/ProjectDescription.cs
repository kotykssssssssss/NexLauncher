using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NexLauncher.Services.Modrinth;

public enum DescriptionBlockKind { Paragraph, Heading, ListItem, Quote, Code }
public enum DescriptionSpanKind { Text, Bold, Italic, Code, Link }
public sealed record DescriptionSpan(string Text, DescriptionSpanKind Kind = DescriptionSpanKind.Text, Uri? Link = null);
public sealed record DescriptionBlock(DescriptionBlockKind Kind, IReadOnlyList<DescriptionSpan> Spans, int Level = 0);
public sealed record ProjectDescription(IReadOnlyList<DescriptionBlock> Blocks)
{
    public static ProjectDescription Empty { get; } = new(Array.Empty<DescriptionBlock>());
}

/// <summary>A bounded text-to-render-model parser. It never interprets HTML, XAML, CSS or executable content.</summary>
public static class ProjectDescriptionParser
{
    public const int MaxCharacters = 262144;
    private static readonly Regex ListPrefix = new(@"^(?:[-+*]|[0-9]{1,6}[.)])\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static ProjectDescription Parse(string source, CancellationToken token = default)
    {
        if (source.Length > MaxCharacters) throw new InvalidDataException("Описание превышает 256 тысяч символов. Полная версия доступна на странице проекта.");
        var blocks = new List<DescriptionBlock>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        string? fence = null;
        var spans = 0;
        void Add(DescriptionBlockKind kind, string text, int level = 0)
        {
            var parsed = kind == DescriptionBlockKind.Code ? new[] { new DescriptionSpan(text, DescriptionSpanKind.Code) } : ParseInline(text);
            spans += parsed.Count;
            if (blocks.Count >= 512 || spans > 4096) throw new InvalidDataException("Слишком сложное форматирование описания. Полная версия доступна на странице проекта.");
            blocks.Add(new(kind, parsed, level));
        }
        void Flush()
        {
            if (paragraph.Length == 0) return;
            Add(DescriptionBlockKind.Paragraph, paragraph.ToString()); paragraph.Clear();
        }
        foreach (var original in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            var line = original.TrimEnd();
            if (fence is not null)
            {
                if (line.Trim() == fence) { Add(DescriptionBlockKind.Code, code.ToString().TrimEnd('\n')); code.Clear(); fence = null; }
                else code.AppendLine(original);
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            { Flush(); fence = line[..3]; continue; }
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
            var heading = line.TakeWhile(x => x == '#').Count();
            if (heading is >= 1 and <= 6 && line.Length > heading && line[heading] == ' ')
            { Flush(); Add(DescriptionBlockKind.Heading, line[(heading + 1)..], heading); continue; }
            if (line.StartsWith("> ", StringComparison.Ordinal))
            { Flush(); Add(DescriptionBlockKind.Quote, line[2..]); continue; }
            var list = ListPrefix.Match(line.TrimStart());
            if (list.Success)
            { Flush(); Add(DescriptionBlockKind.ListItem, "• " + line.TrimStart()[list.Length..]); continue; }
            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(line);
        }
        Flush();
        if (fence is not null) Add(DescriptionBlockKind.Code, code.ToString().TrimEnd('\n'));
        return new(blocks);
    }

    // Deliberately non-recursive: emphasis, code and links are not nested. Unsupported syntax stays text.
    private static IReadOnlyList<DescriptionSpan> ParseInline(string text)
    {
        var spans = new List<DescriptionSpan>(); var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { spans.Add(new(plain.ToString())); plain.Clear(); } }
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\\' && i + 1 < text.Length && "\\`*_[]!".Contains(text[i + 1]))
            { plain.Append(text[i + 1]); i += 2; continue; }
            var isImage = text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[';
            var start = i + (isImage ? 1 : 0);
            if (text[start] == '[')
            {
                var endLabel = text.IndexOf("](", start, Math.Min(1026, text.Length - start), StringComparison.Ordinal);
                if (endLabel > start && endLabel - start <= 1024)
                {
                    var endUrl = text.IndexOf(')', endLabel + 2, Math.Min(4096, text.Length - endLabel - 2));
                    if (endUrl > endLabel && endUrl - endLabel <= 4096)
                    {
                        var label = text[(start + 1)..endLabel];
                        var url = text[(endLabel + 2)..endUrl];
                        if (!url.Contains('(') && SafeProjectLink.TryCreate(url, out var link))
                        {
                            Flush(); spans.Add(new(isImage ? "Изображение: " + label : label, DescriptionSpanKind.Link, link));
                            i = endUrl + 1; continue;
                        }
                        // Invalid links remain ordinary text; a shell protocol can never reach the renderer.
                    }
                }
            }
            var marker = text.AsSpan(i).StartsWith("**", StringComparison.Ordinal) ? "**" : text[i] is '*' or '`' ? text[i].ToString() : null;
            if (marker is not null)
            {
                var end = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                if (end > i + marker.Length)
                {
                    Flush(); spans.Add(new(text[(i + marker.Length)..end], marker == "**" ? DescriptionSpanKind.Bold : marker == "`" ? DescriptionSpanKind.Code : DescriptionSpanKind.Italic));
                    i = end + marker.Length; continue;
                }
            }
            plain.Append(text[i++]);
        }
        Flush(); return spans;
    }
}

/// <summary>Only explicit web links are handed to the OS. No shell/file/data/JavaScript protocols.</summary>
public static class SafeProjectLink
{
    public static bool TryCreate(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(x => char.IsControl(x) || char.IsWhiteSpace(x) || x == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http") ||
            parsed.UserInfo.Length != 0 || parsed.Host.Length == 0 || parsed.IsLoopback) return false;
        uri = parsed; return true;
    }
}
