using System.Net;
using System.Text.RegularExpressions;

namespace CRS.Common.Text;

/// <summary>将来源提供的 HTML 片段转成供纯文本界面显示的文字。</summary>
public static class HtmlText
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex HiddenContent = new(
        @"<!--.*?-->|<script\b[^>]*>.*?(?:</script\s*>|$)|<style\b[^>]*>.*?(?:</style\s*>|$)",
        Options | RegexOptions.Singleline);
    private static readonly Regex Tags = new(
        """</?(?<tag>[a-z][a-z0-9:-]*)(?:\s+(?:[^>"']|"[^"]*"|'[^']*')*)?\s*/?>""", Options);
    private static readonly Regex Whitespace = new(@"\s+", Options);

    public static string ToPlainText(string? html, bool preserveLineBreaks = false)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var visible = HiddenContent.Replace(html, " ");
        var text = WebUtility.HtmlDecode(Tags.Replace(visible, tag =>
        {
            if (!preserveLineBreaks) return " ";
            return tag.Groups["tag"].Value.ToLowerInvariant() switch
            {
                "p" or "br" or "div" or "li" or "blockquote" or "pre" or "tr" or
                    "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "\n",
                "td" or "th" => " ",
                _ => ""
            };
        }));
        if (!preserveLineBreaks) return Whitespace.Replace(text, " ").Trim();
        return string.Join('\n', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Whitespace.Replace(line, " ").Trim()).Where(line => line.Length > 0));
    }
}
