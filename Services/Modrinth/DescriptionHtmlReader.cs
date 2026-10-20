using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AngleSharp.Dom;

namespace NexLauncher.Services.Modrinth;

/// <summary>Text semantics only. All attributes other than validated href/src and alt are ignored.
/// Active content and its subtree disappear; images become user-initiated links.</summary>
internal sealed class DescriptionHtmlReader(CancellationToken token)
{
    private readonly List<DescriptionBlock> _blocks = [];
    private readonly List<DescriptionSpan> _pending = [];
    private DescriptionBlockKind _kind = DescriptionBlockKind.Paragraph;
    private int _level;
    private int _nodes;
    private int _spans;

    public ProjectDescription Read(IElement body)
    {
        Visit(body, 0);
        Flush();
        return new(_blocks);
    }

    private void Visit(INode node, int depth, bool strong = false, bool emphasis = false, bool code = false, Uri? link = null)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 64 || ++_nodes > 16384) throw TooComplex();
        if (node is IText text)
        {
            if (string.IsNullOrWhiteSpace(text.Data) && _pending.Count == 0) return;
            Add(text.Data, code ? DescriptionSpanKind.Code : link is not null ? DescriptionSpanKind.Link :
                strong ? DescriptionSpanKind.Bold : emphasis ? DescriptionSpanKind.Italic : DescriptionSpanKind.Text, link, strong, emphasis);
            return;
        }
        if (node is not IElement element) return; // comments, processing instructions, doctypes
        var name = element.LocalName;
        switch (name)
        {
            case "script": case "style": case "iframe": case "object": case "embed": case "svg": case "math":
            case "template": case "form": case "input": case "button": case "select": case "textarea":
            case "video": case "audio": case "canvas": case "noscript": case "head": return;
            case "img":
                if (SafeProjectLink.TryCreate(element.GetAttribute("src"), out var image))
                {
                    var alt = element.GetAttribute("alt");
                    var label = string.IsNullOrWhiteSpace(alt) ? "Открыть изображение" : alt.Trim();
                    Add(link is null ? "Изображение: " + label : label, DescriptionSpanKind.Link, link ?? image);
                }
                return;
            case "br": Add("\n"); return;
            case "hr": Flush(); AddBlock(new(DescriptionBlockKind.Rule, [])); return;
            case "pre":
                Flush(); AddBlock(new(DescriptionBlockKind.Code, [new(element.TextContent.TrimEnd('\n', '\r'), DescriptionSpanKind.Code)])); return;
        }

        // Unknown wrappers are unwrapped as text, never instantiated as UI controls.
        var block = name is "p" or "div" or "center" or "section" or "article" or "header" or "footer" or
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "ul" or "ol" or "blockquote" or
            "table" or "thead" or "tbody" or "tr" or "details" or "summary";
        // Loose Markdown lists wrap the item text in <p>; keep its marker with the text.
        if (name == "p" && _kind == DescriptionBlockKind.ListItem) block = false;
        var previousKind = _kind;
        var previousLevel = _level;
        if (block)
        {
            Flush();
            if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
            { _kind = DescriptionBlockKind.Heading; _level = name[1] - '0'; }
            else if (name == "li")
            {
                _kind = DescriptionBlockKind.ListItem;
                _level = element.Ancestors<IElement>().Count(x => x.LocalName is "ul" or "ol");
                var number = element.ParentElement?.Children.TakeWhile(x => x != element).Count(x => x.LocalName == "li") + 1;
                Add(element.ParentElement?.LocalName == "ol" ? number + ". " : "• ");
            }
            else if (name == "blockquote") _kind = DescriptionBlockKind.Quote;
        }
        if (name == "a" && SafeProjectLink.TryCreate(element.GetAttribute("href"), out var target)) link = target;
        foreach (var child in element.ChildNodes)
            Visit(child, depth + 1, strong || name is "strong" or "b" or "th", emphasis || name is "em" or "i", code || name == "code", link);
        if (name is "td" or "th") Add("  |  "); // readable text table; no HTML table layout
        if (block) { Flush(); _kind = previousKind; _level = previousLevel; }
    }

    private void Add(string text, DescriptionSpanKind kind = DescriptionSpanKind.Text, Uri? link = null, bool strong = false, bool emphasis = false)
    {
        if (text.Length == 0) return;
        if (++_spans > 4096) throw TooComplex();
        if (kind == DescriptionSpanKind.Link && _pending.LastOrDefault() is { Kind: DescriptionSpanKind.Link } last && last.Link == link)
        {
            _pending[^1] = last with { Text = last.Text + text, Strong = last.Strong || strong, Emphasis = last.Emphasis || emphasis };
            return;
        }
        _pending.Add(new(text, kind, link, strong, emphasis));
    }
    private void Flush()
    {
        if (_pending.Count > 0 && _pending.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            AddBlock(new(_kind, _pending.ToArray(), _level));
        _pending.Clear();
    }
    private void AddBlock(DescriptionBlock block)
    {
        if (_blocks.Count >= 512) throw TooComplex();
        _blocks.Add(block);
    }
    private static InvalidDataException TooComplex() => new("Слишком сложное форматирование описания. Полная версия доступна на странице проекта.");
}
