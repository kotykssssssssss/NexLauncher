using System.Windows.Input;
using Avalonia;
using Avalonia.Data;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NexLauncher.Services.Modrinth;

namespace NexLauncher.Views;

/// <summary>Creates native controls from typed text only. Never loads markup or remote content.</summary>
public sealed class ProjectDescriptionView : StackPanel
{
    public static readonly StyledProperty<ProjectDescription?> DocumentProperty = AvaloniaProperty.Register<ProjectDescriptionView, ProjectDescription?>(nameof(Document));
    public static readonly StyledProperty<ICommand?> OpenLinkCommandProperty = AvaloniaProperty.Register<ProjectDescriptionView, ICommand?>(nameof(OpenLinkCommand));
    public ProjectDescription? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public ICommand? OpenLinkCommand { get => GetValue(OpenLinkCommandProperty); set => SetValue(OpenLinkCommandProperty, value); }

    public ProjectDescriptionView() => Spacing = 12;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty || change.Property == OpenLinkCommandProperty) Render();
    }
    private void Render()
    {
        Children.Clear();
        if (Document is null) return;
        foreach (var block in Document.Blocks)
        {
            if (block.Kind == DescriptionBlockKind.Rule)
            {
                Children.Add(new Border { Classes = { "description-divider" } });
                continue;
            }
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineSpacing = 4 };
            if (block.Kind == DescriptionBlockKind.Heading)
            {
                text.FontSize = block.Level <= 2 ? 22 : 17; text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, 12, 0, 2);
            }
            if (block.Kind == DescriptionBlockKind.ListItem) text.Margin = new Thickness(12 * System.Math.Clamp(block.Level, 1, 5), 0, 0, 0);
            if (block.Kind == DescriptionBlockKind.Quote) text.Classes.Add("muted");
            foreach (var span in block.Spans)
            {
                if (span.Kind == DescriptionSpanKind.Link && span.Link is not null && SafeProjectLink.TryCreate(span.Link.AbsoluteUri, out _))
                {
                    var button = new Button { Content = new TextBlock { Text = span.Text + " ↗", TextWrapping = TextWrapping.Wrap,
                            FontWeight = span.Strong ? FontWeight.Bold : FontWeight.Normal,
                            FontStyle = span.Emphasis ? FontStyle.Italic : FontStyle.Normal },
                        Command = OpenLinkCommand, CommandParameter = span.Link.AbsoluteUri, Padding = new Thickness(2, 0), MinHeight = 0 };
                    button.Bind(MaxWidthProperty, new Binding("Bounds.Width") { Source = this });
                    button.Classes.Add("quiet"); ToolTip.SetTip(button, span.Link.AbsoluteUri);
                    text.Inlines!.Add(new InlineUIContainer { Child = button, BaselineAlignment = BaselineAlignment.Center });
                    continue;
                }
                var run = new Run(span.Text);
                if (span.Strong || span.Kind == DescriptionSpanKind.Bold) run.FontWeight = FontWeight.Bold;
                if (span.Emphasis || span.Kind == DescriptionSpanKind.Italic) run.FontStyle = FontStyle.Italic;
                if (span.Kind == DescriptionSpanKind.Code) run.FontFamily = new FontFamily("Consolas");
                text.Inlines!.Add(run);
            }
            if (block.Kind is DescriptionBlockKind.Code or DescriptionBlockKind.Quote)
                Children.Add(new Border { Classes = { block.Kind == DescriptionBlockKind.Code ? "description-code" : "description-quote" }, Child = text });
            else Children.Add(text);
        }
    }
}
