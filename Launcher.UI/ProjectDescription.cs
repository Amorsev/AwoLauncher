using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HtmlAgilityPack;
using Markdig;

namespace Launcher.UI;

public sealed class ProjectDescription : FlowDocumentScrollViewer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "form", "textarea", "button",
        "meta", "link", "template", "svg", "math", "video", "audio"
    };

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string),
        typeof(ProjectDescription), new PropertyMetadata(null, (sender, args) =>
            ((ProjectDescription)sender).Render(args.NewValue as string ?? "")));

    public string? Markdown { get => (string?)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    private void Render(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(12, 8, 12, 8), FontSize = 14,
            FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Normal
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        var html = new HtmlDocument();
        html.LoadHtml(Markdig.Markdown.ToHtml(markdown, Pipeline));
        AddBlocks(html.DocumentNode.ChildNodes, document.Blocks, 0);
        Document = document;
    }

    private static void AddBlocks(IEnumerable<HtmlNode> nodes, BlockCollection target, int depth)
    {
        if (depth >= 48) return;
        Paragraph? pending = null;
        foreach (var node in nodes)
        {
            if (Ignore(node)) continue;
            if (node.Name is "div" or "article" or "aside" or "section" or "center" or "figure" or "html" or "body" or "details" or "dl")
            {
                pending = null;
                AddBlocks(node.ChildNodes, target, depth + 1);
            }
            else if (CreateBlock(node, depth) is { } block)
            {
                target.Add(block);
                pending = null;
            }
            else
            {
                if (pending is null && node.NodeType == HtmlNodeType.Text && string.IsNullOrWhiteSpace(node.InnerText)) continue;
                if (pending is null)
                {
                    pending = NewParagraph();
                    target.Add(pending);
                }
                AddInline(node, pending.Inlines, depth + 1);
            }
        }
    }

    private static Block? CreateBlock(HtmlNode node, int depth)
    {
        var tag = node.Name;
        switch (tag)
        {
            case "p": case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
            case "summary": case "figcaption": case "dt": case "dd":
                var paragraph = NewParagraph();
                foreach (var child in node.ChildNodes) AddInline(child, paragraph.Inlines, depth + 1);
                if (tag.Length == 2 && tag[0] == 'h')
                {
                    paragraph.FontSize = Math.Max(15, 25 - (tag[1] - '0') * 2);
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Margin = new Thickness(0, 10, 0, 6);
                }
                if (tag is "summary" or "dt") paragraph.FontWeight = FontWeights.SemiBold;
                return paragraph;
            case "pre":
                var code = new Paragraph(new Run(WebUtility.HtmlDecode(node.InnerText).TrimEnd('\r', '\n')))
                { FontFamily = new FontFamily("Consolas"), Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 10) };
                code.SetResourceReference(TextElement.BackgroundProperty, "Soft");
                return code;
            case "ul": case "ol":
                var list = new List { MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
                if (tag == "ol" && int.TryParse(node.GetAttributeValue("start", ""), out var start) && start > 0) list.StartIndex = start;
                foreach (var item in node.Elements("li"))
                {
                    var listItem = new ListItem();
                    AddBlocks(item.ChildNodes, listItem.Blocks, depth + 1);
                    list.ListItems.Add(listItem);
                }
                return list;
            case "blockquote":
                var quote = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 1) };
                quote.SetResourceReference(Block.BorderBrushProperty, "Accent");
                quote.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                AddBlocks(node.ChildNodes, quote.Blocks, depth + 1);
                return quote;
            case "table":
                return CreateTable(node, depth);
            case "hr":
                var rule = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 2, 0, 8) };
                rule.SetResourceReference(Block.BorderBrushProperty, "Line");
                return rule;
            default:
                return null;
        }
    }

    private static Table CreateTable(HtmlNode node, int depth)
    {
        var table = new Table { CellSpacing = 1, Margin = new Thickness(0, 4, 0, 10) };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (var row in node.ChildNodes.SelectMany(child => child.Name is "thead" or "tbody" or "tfoot"
                     ? child.Elements("tr") : child.Name == "tr" ? [child] : Enumerable.Empty<HtmlNode>()))
        {
            var tableRow = new TableRow();
            foreach (var cell in row.ChildNodes.Where(item => item.Name is "th" or "td").Take(32))
            {
                var column = new TableCell { Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1) };
                column.SetResourceReference(TableCell.BorderBrushProperty, "Line");
                if (cell.Name == "th")
                {
                    column.FontWeight = FontWeights.SemiBold;
                    column.SetResourceReference(TextElement.BackgroundProperty, "Soft");
                }
                AddBlocks(cell.ChildNodes, column.Blocks, depth + 1);
                tableRow.Cells.Add(column);
            }
            if (tableRow.Cells.Count > 0) group.Rows.Add(tableRow);
        }
        return table;
    }

    private static void AddInline(HtmlNode node, InlineCollection target, int depth, bool inLink = false)
    {
        if (depth >= 48 || Ignore(node)) return;
        if (node.NodeType == HtmlNodeType.Text)
        {
            target.Add(new Run(Regex.Replace(WebUtility.HtmlDecode(node.InnerText), @"[ \t\r\n\f]+", " ")));
            return;
        }

        var tag = node.Name;
        if (tag == "br") { target.Add(new LineBreak()); return; }
        if (tag == "img")
        {
            var source = WebUtility.HtmlDecode(node.GetAttributeValue("src", ""));
            var alternative = WebUtility.HtmlDecode(node.GetAttributeValue("alt", ""));
            if (SafeHttp(source) is { Host: "cdn.modrinth.com", Scheme: "https" } uri)
            {
                var image = new Image { Source = new BitmapImage(uri), MaxWidth = 640, MaxHeight = 300, Stretch = Stretch.Uniform };
                System.Windows.Automation.AutomationProperties.SetName(image, alternative);
                target.Add(new InlineUIContainer(image));
            }
            else if (!string.IsNullOrWhiteSpace(alternative)) target.Add(new Run(alternative));
            return;
        }
        if (tag == "input")
        {
            if (node.GetAttributeValue("type", "") == "checkbox")
                target.Add(new Run(node.Attributes["checked"] is null ? "☐ " : "☑ "));
            return;
        }

        Span? span = tag switch
        {
            "strong" or "b" => new Bold(),
            "em" or "i" => new Italic(),
            "u" => new Underline(),
            "s" or "del" or "strike" => new Span { TextDecorations = TextDecorations.Strikethrough },
            "code" or "kbd" => new Span { FontFamily = new FontFamily("Consolas") },
            "sup" => new Span { BaselineAlignment = BaselineAlignment.Superscript },
            "sub" => new Span { BaselineAlignment = BaselineAlignment.Subscript },
            "a" when !inLink && SafeHttp(WebUtility.HtmlDecode(node.GetAttributeValue("href", ""))) is { } uri => Link(uri),
            _ => null
        };
        foreach (var child in node.ChildNodes)
            AddInline(child, span?.Inlines ?? target, depth + 1, inLink || span is Hyperlink);
        if (span is not null) target.Add(span);
    }

    private static Hyperlink Link(Uri uri)
    {
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        link.RequestNavigate += (_, args) =>
        {
            args.Handled = true;
            try { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Trace.TraceWarning(error.Message);
            }
        };
        return link;
    }

    private static Uri? SafeHttp(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri : null;
    private static bool Ignore(HtmlNode node) => node.NodeType == HtmlNodeType.Comment || Ignored.Contains(node.Name);
    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 0, 0, 8) };
}
