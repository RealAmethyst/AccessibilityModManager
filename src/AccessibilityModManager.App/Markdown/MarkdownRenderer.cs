using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdParagraphBlock = Markdig.Syntax.ParagraphBlock;
using MdHeadingBlock = Markdig.Syntax.HeadingBlock;
using MdListBlock = Markdig.Syntax.ListBlock;
using MdListItemBlock = Markdig.Syntax.ListItemBlock;
using MdQuoteBlock = Markdig.Syntax.QuoteBlock;
using MdFencedCodeBlock = Markdig.Syntax.FencedCodeBlock;
using MdCodeBlock = Markdig.Syntax.CodeBlock;
using MdThematicBreakBlock = Markdig.Syntax.ThematicBreakBlock;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WpfAutomation = System.Windows.Automation.Peers;

namespace AccessibilityModManager.App.Markdown;

/// <summary>
/// Renders a markdown string as a WPF <see cref="FlowDocument"/>. Used by the changelog viewer
/// so authors can write proper notes (headings, bold, links, lists, code) and have them
/// rendered cleanly in the manager. FlowDocument is screen-reader accessible — NVDA reads
/// the content with structure, including heading levels.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FlowDocument Render(string markdown)
    {
        var doc = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var flow = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(0)
        };

        foreach (var block in doc)
            RenderBlock(block, flow.Blocks);

        return flow;
    }

    private static void RenderBlock(MdBlock block, BlockCollection blocks)
    {
        switch (block)
        {
            case MdHeadingBlock h:
                var heading = new Paragraph
                {
                    FontWeight = FontWeights.Bold,
                    FontSize = HeadingFontSize(h.Level),
                    Margin = new Thickness(0, 12, 0, 4)
                };
                System.Windows.Automation.AutomationProperties.SetHeadingLevel(heading,
                    h.Level switch
                    {
                        1 => System.Windows.Automation.AutomationHeadingLevel.Level1,
                        2 => System.Windows.Automation.AutomationHeadingLevel.Level2,
                        3 => System.Windows.Automation.AutomationHeadingLevel.Level3,
                        4 => System.Windows.Automation.AutomationHeadingLevel.Level4,
                        5 => System.Windows.Automation.AutomationHeadingLevel.Level5,
                        _ => System.Windows.Automation.AutomationHeadingLevel.Level6
                    });
                if (h.Inline != null) RenderInlines(h.Inline, heading.Inlines);
                blocks.Add(heading);
                break;

            case MdParagraphBlock pb:
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                if (pb.Inline != null) RenderInlines(pb.Inline, para.Inlines);
                blocks.Add(para);
                break;

            case MdListBlock lb:
                var list = new List
                {
                    MarkerStyle = lb.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                foreach (var item in lb)
                {
                    if (item is MdListItemBlock lib)
                    {
                        var li = new ListItem();
                        foreach (var sub in lib)
                            RenderBlock(sub, li.Blocks);
                        list.ListItems.Add(li);
                    }
                }
                blocks.Add(list);
                break;

            case MdFencedCodeBlock fcb:
                var codePara = new Paragraph
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                codePara.Inlines.Add(new Run(string.Join("\n", fcb.Lines.Lines.Select(l => l.ToString()))));
                blocks.Add(codePara);
                break;

            case MdCodeBlock cb:
                var indentCodePara = new Paragraph
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                indentCodePara.Inlines.Add(new Run(string.Join("\n", cb.Lines.Lines.Select(l => l.ToString()))));
                blocks.Add(indentCodePara);
                break;

            case MdQuoteBlock qb:
                var quoteSection = new Section
                {
                    Margin = new Thickness(12, 0, 0, 8),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0)
                };
                foreach (var sub in qb)
                    RenderBlock(sub, quoteSection.Blocks);
                blocks.Add(quoteSection);
                break;

            case MdThematicBreakBlock _:
                blocks.Add(new Paragraph(new Run("─────────")) { TextAlignment = TextAlignment.Center });
                break;

            case MdTable table:
                // Tables in changelogs are rare; fallback: render rows as paragraphs.
                foreach (var row in table)
                {
                    if (row is MdTableRow tr)
                    {
                        var rowPara = new Paragraph();
                        var first = true;
                        foreach (var cell in tr)
                        {
                            if (!first) rowPara.Inlines.Add(new Run("  |  "));
                            if (cell is MdTableCell tc)
                            {
                                foreach (var inner in tc)
                                {
                                    if (inner is MdParagraphBlock cellPara && cellPara.Inline != null)
                                        RenderInlines(cellPara.Inline, rowPara.Inlines);
                                }
                            }
                            first = false;
                        }
                        blocks.Add(rowPara);
                    }
                }
                break;
        }
    }

    private static double HeadingFontSize(int level) => level switch
    {
        1 => 22,
        2 => 18,
        3 => 16,
        4 => 14,
        _ => 14
    };

    private static void RenderInlines(ContainerInline inline, InlineCollection inlines)
    {
        foreach (var i in inline)
        {
            switch (i)
            {
                case LiteralInline lit:
                    inlines.Add(new Run(lit.Content.ToString()));
                    break;

                case EmphasisInline emp:
                    var span = new Span();
                    if (emp.DelimiterCount == 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    RenderInlines(emp, span.Inlines);
                    inlines.Add(span);
                    break;

                case LinkInline link when link.Url != null:
                    if (link.IsImage)
                    {
                        // Don't try to load remote images in a changelog; fall back to alt text.
                        var altText = ReadInlineText(link);
                        inlines.Add(new Run($"[image: {altText}]"));
                    }
                    else
                    {
                        Uri? uri = null;
                        try { uri = new Uri(link.Url, UriKind.Absolute); } catch { }
                        if (uri != null)
                        {
                            var hyperlink = new Hyperlink { NavigateUri = uri };
                            hyperlink.RequestNavigate += (_, e) =>
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo
                                    {
                                        FileName = e.Uri.AbsoluteUri,
                                        UseShellExecute = true
                                    });
                                }
                                catch { }
                                e.Handled = true;
                            };
                            RenderInlines(link, hyperlink.Inlines);
                            inlines.Add(hyperlink);
                        }
                        else
                        {
                            // Bad URL — render as plain text.
                            RenderInlines(link, inlines);
                        }
                    }
                    break;

                case CodeInline code:
                    inlines.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = new SolidColorBrush(Color.FromRgb(245, 245, 245))
                    });
                    break;

                case LineBreakInline lb:
                    inlines.Add(lb.IsHard ? new LineBreak() : new Run(" "));
                    break;

                case AutolinkInline auto:
                    if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var autoUri))
                    {
                        var h = new Hyperlink(new Run(auto.Url)) { NavigateUri = autoUri };
                        h.RequestNavigate += (_, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = e.Uri.AbsoluteUri,
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                            e.Handled = true;
                        };
                        inlines.Add(h);
                    }
                    else
                    {
                        inlines.Add(new Run(auto.Url));
                    }
                    break;

                case ContainerInline container:
                    // Generic containers (e.g. unknown extensions) — render their children.
                    RenderInlines(container, inlines);
                    break;
            }
        }
    }

    private static string ReadInlineText(ContainerInline inline)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in inline)
        {
            switch (i)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case ContainerInline c: sb.Append(ReadInlineText(c)); break;
            }
        }
        return sb.ToString();
    }
}
