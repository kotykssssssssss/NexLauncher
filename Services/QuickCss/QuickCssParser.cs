using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace NexLauncher.Services.QuickCss;

/// <summary>
/// A deliberately small CSS-like syntax. Parsing is independent of the allowed selector/property
/// registry and never evaluates URLs, accesses files, or instantiates Avalonia objects.
/// </summary>
public static class QuickCssParser
{
    public const int MaximumInputBytes = 128 * 1024;
    public const int MaximumRules = 256;
    public const int MaximumDeclarations = 2048;
    public const int MaximumValueLength = 4096;
    private const int MaximumNesting = 32;
    private const int MaximumVariableDepth = 16;

    public static QuickCssDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumInputBytes || Encoding.UTF8.GetByteCount(source) > MaximumInputBytes)
            return new QuickCssDocument([], [new QuickCssDiagnostic(1, "Файл Quick CSS превышает 128 КиБ.")]);
        return new Reader(source).Read();
    }

    private sealed class Reader
    {
        private readonly string _source;
        private readonly int[] _lines;
        private readonly List<QuickCssDiagnostic> _diagnostics = new();
        private readonly List<QuickCssRule> _rules = new();
        private int _position;
        private int _declarationCount;

        public Reader(string source)
        {
            _lines = new int[source.Length + 1];
            var line = 1;
            for (var index = 0; index < source.Length; index++)
            {
                _lines[index] = line;
                if (source[index] == '\n') line++;
            }
            _lines[source.Length] = line;
            _source = RemoveComments(source);
        }

        public QuickCssDocument Read()
        {
            var ruleCount = 0;
            while (_position < _source.Length)
            {
                SkipWhitespace();
                if (_position >= _source.Length) break;
                if (++ruleCount > MaximumRules)
                {
                    Report(_position, "Слишком много правил: максимум 256.");
                    break;
                }
                var start = _position;
                if (_source[_position] == '@')
                {
                    Report(start, "Директивы @ (включая @import и @media) не поддерживаются.");
                    SkipAtRule();
                    continue;
                }
                while (_position < _source.Length && _source[_position] is not ('{' or '}' or ';')) _position++;
                if (_position >= _source.Length)
                {
                    Report(start, "После селектора ожидается блок { ... }.");
                    break;
                }
                if (_source[_position] != '{')
                {
                    Report(start, "Некорректное правило: ожидается селектор и блок { ... }.");
                    _position++;
                    continue;
                }
                var selectorText = _source[start.._position].Trim();
                var selectors = ParseSelectors(selectorText, start);
                var bodyStart = ++_position;
                var bodyEnd = ReadBlock(out var nested, out var closed);
                if (nested) Report(start, "Вложенные правила не поддерживаются; весь блок пропущен.");
                if (!closed) Report(start, "Не закрыта фигурная скобка правила; блок пропущен.");
                if (nested || !closed || selectors.Count == 0) continue;
                _rules.Add(new QuickCssRule(selectors, ReadDeclarations(bodyStart, bodyEnd), _lines[start]));
                if (_declarationCount > MaximumDeclarations) break;
            }

            // Variables intentionally have global scope. The last :root declaration wins.
            var variables = new Dictionary<string, QuickCssDeclaration>(StringComparer.Ordinal);
            foreach (var rule in _rules)
            foreach (var declaration in rule.Declarations)
            {
                if (!declaration.Property.StartsWith("--", StringComparison.Ordinal)) continue;
                if (rule.Selectors.Count == 1 && rule.Selectors[0] == ":root")
                    variables[declaration.Property] = declaration;
                else
                    _diagnostics.Add(new QuickCssDiagnostic(declaration.Line, "Переменные можно объявлять только в отдельном правиле :root."));
            }
            var resolver = new VariableResolver(variables);
            var result = new List<QuickCssRule>();
            foreach (var rule in _rules)
            {
                var declarations = new List<QuickCssDeclaration>();
                foreach (var declaration in rule.Declarations)
                {
                    if (declaration.Property.StartsWith("--", StringComparison.Ordinal)) continue;
                    if (resolver.TryResolve(declaration.Value, out var value, out var error))
                        declarations.Add(declaration with { Value = value });
                    else
                        _diagnostics.Add(new QuickCssDiagnostic(declaration.Line, error));
                }
                if (declarations.Count > 0) result.Add(rule with { Declarations = declarations });
            }
            NexLauncher.Models.HomeLayout? layout = null;
            if (variables.TryGetValue("--home-layout", out var layoutDeclaration))
            {
                if (resolver.TryResolve(layoutDeclaration.Value, out var value, out var error))
                {
                    if (QuickCssHomeLayout.TryParse(value, out var parsed)) layout = parsed;
                    else _diagnostics.Add(new(layoutDeclaration.Line, "--home-layout принимает только default, tiles или list."));
                }
                else _diagnostics.Add(new(layoutDeclaration.Line, error));
            }
            var appearance = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var option in QuickCssAppearance.Options)
            {
                if (!variables.TryGetValue(option.Token, out var declaration)) continue;
                if (!resolver.TryResolve(declaration.Value, out var value, out var error))
                    _diagnostics.Add(new(declaration.Line, error));
                else if (QuickCssAppearance.TryNormalize(option, value, out var normalized)) appearance.Add(option.Token, normalized);
                else _diagnostics.Add(new(declaration.Line, option.Token + ": " + (option.IsColor ? "ожидается непрозрачный цвет." : option.Hint)));
            }
            var rootDeclarations = _rules.Where(x => x.Selectors.Count == 1 && x.Selectors[0] == ":root").SelectMany(x => x.Declarations).ToArray();
            return new QuickCssDocument(QuickCssAppearance.Rules(appearance).Concat(result).ToArray(), _diagnostics, layout,
                rootDeclarations.Where(x => x.Property == "--home-layout").ToArray(), appearance, rootDeclarations);
        }

        private string RemoveComments(string source)
        {
            var characters = source.ToCharArray();
            char quote = '\0';
            for (var index = 0; index < characters.Length; index++)
            {
                var character = source[index];
                if (quote != '\0')
                {
                    if (character == '\\') index++;
                    else if (character == quote || character is '\r' or '\n') quote = '\0';
                    continue;
                }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character != '/' || index + 1 >= source.Length || source[index + 1] != '*') continue;
                var start = index;
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0) { Report(start, "Не закрыт комментарий /* ... */."); end = source.Length; }
                else end += 2;
                for (; index < end; index++)
                    if (characters[index] is not ('\r' or '\n')) characters[index] = ' ';
                index--;
            }
            return new string(characters);
        }

        private List<string> ParseSelectors(string text, int offset)
        {
            var selectors = new List<string>();
            foreach (var candidate in text.Split(','))
            {
                var selector = candidate.Trim();
                if (IsSelector(selector)) selectors.Add(selector);
                else Report(offset, "Неподдерживаемый селектор. Используйте имя, #id, .class и :hover/:selected/:disabled.");
            }
            return selectors;
        }

        private List<QuickCssDeclaration> ReadDeclarations(int start, int end)
        {
            var declarations = new List<QuickCssDeclaration>();
            var segmentStart = start;
            char quote = '\0';
            var parentheses = 0;
            for (var index = start; index <= end; index++)
            {
                var character = index == end ? ';' : _source[index];
                if (quote != '\0' && index != end)
                {
                    if (character == '\\') { index++; continue; }
                    if (character == quote || character is '\r' or '\n') quote = '\0';
                    continue;
                }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == '(') parentheses++;
                if (character == ')') parentheses = Math.Max(0, parentheses - 1);
                var missingSeparator = character == '\n' && parentheses == 0 && StartsDeclaration(index + 1, end) && _source[segmentStart..index].Contains(':');
                if (missingSeparator) character = ';';
                if (character != ';') continue;
                parentheses = 0;
                var offset = segmentStart;
                while (offset < index && char.IsWhiteSpace(_source[offset])) offset++;
                var text = _source[offset..index].Trim();
                segmentStart = index + 1;
                if (text.Length == 0) continue;
                if (++_declarationCount > MaximumDeclarations)
                {
                    Report(offset, "Слишком много свойств: максимум 2048.");
                    break;
                }
                var colon = text.IndexOf(':');
                if (colon <= 0)
                {
                    Report(offset, "Ожидается свойство: значение;.");
                    continue;
                }
                var property = text[..colon].Trim();
                if (missingSeparator) Report(offset, $"Пропущена «;» после «{property}». Добавь её перед следующим свойством.");
                var custom = property.StartsWith("--", StringComparison.Ordinal);
                if (!(custom ? IsIdentifier(property.AsSpan(2)) : IsIdentifier(property.AsSpan())))
                {
                    Report(offset, "Некорректное имя свойства или переменной.");
                    continue;
                }
                var value = text[(colon + 1)..].Trim();
                if (value.Length == 0 || value.Length > MaximumValueLength || !IsBalancedValue(value))
                {
                    Report(offset, "Пустое, слишком длинное или некорректное значение свойства.");
                    continue;
                }
                if (ContainsImportant(value))
                {
                    Report(offset, "!important не поддерживается. Поздние правила переопределяют ранние.");
                    continue;
                }
                var finish = index;
                while (finish > offset && char.IsWhiteSpace(_source[finish - 1])) finish--;
                declarations.Add(new QuickCssDeclaration(custom ? property : property.ToLowerInvariant(), value, _lines[offset], offset, finish - offset));
            }
            return declarations;
        }

        private bool StartsDeclaration(int start, int end)
        {
            while (start < end && _source[start] is ' ' or '\t' or '\r') start++;
            var name = start;
            while (start < end && (char.IsAsciiLetterOrDigit(_source[start]) || _source[start] is '-' or '_')) start++;
            var finish = start;
            while (start < end && _source[start] is ' ' or '\t') start++;
            return finish > name && start < end && _source[start] == ':';
        }

        private int ReadBlock(out bool nested, out bool closed)
        {
            var depth = 1;
            nested = false;
            closed = false;
            char quote = '\0';
            for (; _position < _source.Length; _position++)
            {
                var character = _source[_position];
                if (quote != '\0')
                {
                    if (character == '\\') _position++;
                    else if (character == quote || character is '\r' or '\n') quote = '\0';
                    continue;
                }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == '{') { depth++; nested = true; }
                if (character != '}') continue;
                depth--;
                if (depth != 0) continue;
                closed = true;
                return _position++;
            }
            return _source.Length;
        }

        private void SkipAtRule()
        {
            char quote = '\0';
            while (_position < _source.Length)
            {
                var character = _source[_position++];
                if (quote != '\0')
                {
                    if (character == '\\') _position++;
                    else if (character == quote || character is '\r' or '\n') quote = '\0';
                    continue;
                }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == ';') return;
                if (character == '{') { ReadBlock(out _, out _); return; }
            }
        }

        private void SkipWhitespace() { while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++; }
        private void Report(int offset, string message) => _diagnostics.Add(new QuickCssDiagnostic(_lines[Math.Min(offset, _lines.Length - 1)], message));
    }

    private static bool IsSelector(string selector)
    {
        if (selector == ":root") return true;
        var colon = selector.IndexOf(':');
        if (colon >= 0)
        {
            if (selector[colon..] is not (":hover" or ":selected" or ":disabled")) return false;
            selector = selector[..colon];
        }
        if (selector.Length == 0) return false;
        var position = 0;
        if (selector[0] is not ('.' or '#'))
        {
            var start = position;
            while (position < selector.Length && selector[position] is not ('.' or '#')) position++;
            if (!IsIdentifier(selector.AsSpan(start, position - start))) return false;
        }
        while (position < selector.Length)
        {
            var start = ++position;
            while (position < selector.Length && selector[position] is not ('.' or '#')) position++;
            if (!IsIdentifier(selector.AsSpan(start, position - start))) return false;
        }
        return true;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !IsLetter(value[0]) && value[0] != '_') return false;
        foreach (var character in value)
            if (!IsLetter(character) && !(character is >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }
    private static bool IsLetter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsBalancedValue(string value)
    {
        var depth = 0;
        char quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == '\\') { index++; if (index >= value.Length) return false; }
                else if (character == quote) quote = '\0';
                else if (character is '\r' or '\n') return false;
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '(' && ++depth > MaximumNesting) return false;
            if (character == ')' && --depth < 0) return false;
            if (character is '{' or '}' or '\0') return false;
        }
        return depth == 0 && quote == '\0';
    }

    private static bool ContainsImportant(string value)
    {
        char quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == '\\') index++;
                else if (character == quote) quote = '\0';
            }
            else if (character is '\'' or '"') quote = character;
            else if (character == '!') return true;
        }
        return false;
    }

    private sealed class VariableResolver(Dictionary<string, QuickCssDeclaration> variables)
    {
        private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _cyclic = new(StringComparer.Ordinal);
        private int _remainingExpansions = MaximumDeclarations * 32;
        public bool TryResolve(string value, out string result, out string error) =>
            Resolve(value, new List<string>(), 0, out result, out error);

        private bool Resolve(string value, List<string> stack, int depth, out string result, out string error)
        {
            result = "";
            error = "Переменная не определена, образует цикл или превышает допустимую глубину (16).";
            if (depth > MaximumVariableDepth || --_remainingExpansions < 0) return false;
            var output = new StringBuilder();
            char quote = '\0';
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (quote != '\0')
                {
                    output.Append(character);
                    if (character == '\\' && index + 1 < value.Length) output.Append(value[++index]);
                    else if (character == quote) quote = '\0';
                }
                else if (character is '\'' or '"') { quote = character; output.Append(character); }
                else if (value.AsSpan(index).StartsWith("var(", StringComparison.Ordinal)
                         && (index == 0 || !IsLetter(value[index - 1]) && value[index - 1] is not ('_' or '-')))
                {
                    var end = FindFunctionEnd(value, index + 4, out var comma);
                    if (end < 0) return false;
                    var name = value[(index + 4)..(comma < 0 ? end : comma)].Trim();
                    if (!name.StartsWith("--", StringComparison.Ordinal) || !IsIdentifier(name.AsSpan(2))) return false;
                    if (!ResolveVariable(name, stack, depth + 1, out var replacement))
                    {
                        if (comma < 0 || !Resolve(value[(comma + 1)..end].Trim(), stack, depth + 1, out replacement, out error)) return false;
                    }
                    output.Append(replacement);
                    index = end;
                }
                else output.Append(character);
                if (output.Length > MaximumValueLength)
                {
                    error = "Значение после подстановки переменных превышает 4096 символов.";
                    return false;
                }
            }
            result = output.ToString();
            return true;
        }

        private bool ResolveVariable(string name, List<string> stack, int depth, out string value)
        {
            value = "";
            if (depth > MaximumVariableDepth || --_remainingExpansions < 0) return false;
            if (_cache.TryGetValue(name, out var cached)) { value = cached ?? ""; return cached is not null; }
            if (!variables.TryGetValue(name, out var definition)) return false;
            var cycleStart = stack.IndexOf(name);
            if (cycleStart >= 0)
            {
                // Every member of a detected cycle stays invalid, even if one uses a fallback.
                for (var index = cycleStart; index < stack.Count; index++) _cyclic.Add(stack[index]);
                _cyclic.Add(name);
                return false;
            }
            stack.Add(name);
            var success = Resolve(definition.Value, stack, depth, out value, out _);
            stack.Remove(name);
            if (_cyclic.Contains(name)) success = false;
            // Failures caused by a caller's depth must not invalidate a later shallow reference.
            if (success) _cache[name] = value;
            else if (_cyclic.Contains(name)) _cache[name] = null;
            return success;
        }

        private static int FindFunctionEnd(string value, int start, out int comma)
        {
            comma = -1;
            var depth = 1;
            char quote = '\0';
            for (var index = start; index < value.Length; index++)
            {
                var character = value[index];
                if (quote != '\0')
                {
                    if (character == '\\') index++;
                    else if (character == quote) quote = '\0';
                    continue;
                }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == '(') depth++;
                if (character == ')') { if (--depth == 0) return index; }
                if (character == ',' && depth == 1 && comma < 0) comma = index;
            }
            return -1;
        }
    }
}
