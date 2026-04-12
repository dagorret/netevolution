using System.Net;
using System.Text.RegularExpressions;

namespace Nevolution.App.Desktop.ViewModels;

internal static partial class HtmlBodyConverter
{
    public static string ToDisplayText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var normalized = ScriptRegex().Replace(html, string.Empty);
        normalized = StyleRegex().Replace(normalized, string.Empty);
        normalized = CommentRegex().Replace(normalized, string.Empty);
        normalized = LineBreakRegex().Replace(normalized, "\n");
        normalized = ParagraphRegex().Replace(normalized, "\n\n");
        normalized = ListItemRegex().Replace(normalized, "\n- ");
        normalized = LinkRegex().Replace(normalized, "$2 ($1)");
        normalized = TagRegex().Replace(normalized, string.Empty);
        normalized = WebUtility.HtmlDecode(normalized);
        normalized = normalized.Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized = normalized.Replace('\r', '\n');
        normalized = BlankLineRegex().Replace(normalized, "\n\n");

        return normalized.Trim();
    }

    [GeneratedRegex("(?is)<script\\b[^>]*>.*?</script>")]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("(?is)<style\\b[^>]*>.*?</style>")]
    private static partial Regex StyleRegex();

    [GeneratedRegex("(?is)<!--.*?-->")]
    private static partial Regex CommentRegex();

    [GeneratedRegex("(?is)<br\\s*/?>|</div>|</tr>|</table>|</h[1-6]>")]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex("(?is)</p>|</section>|</article>|</header>|</footer>|</ul>|</ol>|<hr\\s*/?>")]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex("(?is)<li\\b[^>]*>")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex("(?is)<a\\b[^>]*href\\s*=\\s*['\"]([^'\"]+)['\"][^>]*>(.*?)</a>")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("(?is)<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex BlankLineRegex();
}
