using System.IO;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace NexLauncher.Views;

internal static class QuickCssHighlighting
{
    public static IHighlightingDefinition Create()
    {
        using var reader = XmlReader.Create(new StringReader("""
            <SyntaxDefinition name="NexLauncher Quick CSS" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955"/>
              <Color name="Selector" foreground="#D7BA7D"/>
              <Color name="Property" foreground="#9CDCFE"/>
              <Color name="Variable" foreground="#C586C0"/>
              <Color name="Value" foreground="#CE9178"/>
              <Color name="Number" foreground="#B5CEA8"/>
              <RuleSet>
                <Span color="Comment" multiline="true"><Begin>/\*</Begin><End>\*/</End></Span>
                <Span color="Value"><Begin>"</Begin><End>"</End></Span>
                <Span color="Value"><Begin>'</Begin><End>'</End></Span>
                <Rule color="Variable">--[a-zA-Z_][a-zA-Z0-9_-]*</Rule>
                <Rule color="Number">\#[0-9a-fA-F]{3,8}\b|\b[0-9]+(?:\.[0-9]+)?(?:px)?\b</Rule>
                <Rule color="Selector">[#.][a-zA-Z_][a-zA-Z0-9_-]*|:[a-zA-Z_][a-zA-Z0-9_-]*</Rule>
                <Rule color="Property">\b[a-zA-Z][a-zA-Z0-9-]*(?=\s*:)</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
