using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AngleSharp.Html.Parser;
using Markdig;

namespace NexLauncher.Services.Modrinth;

public enum DescriptionBlockKind { Paragraph, Heading, ListItem, Quote, Code, Rule }
public enum DescriptionSpanKind { Text, Bold, Italic, Code, Link }
public sealed record DescriptionSpan(string Text, DescriptionSpanKind Kind = DescriptionSpanKind.Text, Uri? Link = null,
    bool Strong = false, bool Emphasis = false);
public sealed record DescriptionBlock(DescriptionBlockKind Kind, IReadOnlyList<DescriptionSpan> Spans, int Level = 0);
public sealed record ProjectDescription(IReadOnlyList<DescriptionBlock> Blocks)
{
    public static ProjectDescription Empty { get; } = new(Array.Empty<DescriptionBlock>());
}

/// <summary>Markdown and HTML are parsed as data, then reduced to a bounded native render model.
/// No generated markup is passed to Avalonia, a browser, a network loader or a script engine.</summary>
public static class ProjectDescriptionParser
{
    public const int MaxCharacters = 262144;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 64 }
        .UseAutoLinks().UsePipeTables().Build();

    public static ProjectDescription Parse(string source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length > MaxCharacters)
            throw new InvalidDataException("Описание превышает 256 тысяч символов. Полная версия доступна на странице проекта.");
        string html;
        try { html = Markdown.ToHtml(source, Pipeline); }
        catch (ArgumentException ex)
        {
            // Markdig's depth guard must produce the same nonfatal notice as our own bounds.
            throw new InvalidDataException("Слишком сложное форматирование описания. Полная версия доступна на странице проекта.", ex);
        }
        token.ThrowIfCancellationRequested();
        // A standalone parser, without a browsing context, loaders, CSS or JavaScript services.
        using var document = new HtmlParser().ParseDocument(html);
        token.ThrowIfCancellationRequested();
        return new DescriptionHtmlReader(token).Read(document.Body!);
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
