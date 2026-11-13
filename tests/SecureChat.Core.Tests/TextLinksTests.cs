using SecureChat.Core.Text;
using Xunit;

namespace SecureChat.Core.Tests;

public sealed class TextLinksTests
{
    [Fact]
    public void SchemeUrls_TrimTrailingPunctuation()
    {
        var hrefs = TextLinks.WebHrefs("Смотри https://example.com/a?b=1, ok.");

        Assert.Single(hrefs);
        Assert.Equal("https://example.com/a?b=1", hrefs[0]);
    }

    [Fact]
    public void SchemeUrls_KeepBalancedParenthesis()
    {
        var hrefs = TextLinks.WebHrefs("Wiki: https://ru.wikipedia.org/wiki/Тест_(x)!");

        Assert.Single(hrefs);
        Assert.Equal("https://ru.wikipedia.org/wiki/Тест_(x)", hrefs[0]);
    }

    [Fact]
    public void Www_And_Bare_Domains_Are_Detected()
    {
        var hrefs = TextLinks.WebHrefs("Открой www.example.com и example.org/path?q=1");

        Assert.Equal(2, hrefs.Count);
        Assert.Equal("https://www.example.com", hrefs[0]);
        Assert.Equal("https://example.org/path?q=1", hrefs[1]);
    }

    [Fact]
    public void BareDomain_SupportsPort()
    {
        var hrefs = TextLinks.WebHrefs("сервер example.com:8080/status упущен");

        Assert.Single(hrefs);
        Assert.Equal("https://example.com:8080/status", hrefs[0]);
    }

    [Fact]
    public void Filenames_And_VersionNumbers_Are_Not_Links()
    {
        var hrefs = TextLinks.WebHrefs("файл index.html, движок v1.2.3, версия 5.6, e.g. note");

        Assert.Empty(hrefs);
    }

    [Fact]
    public void UnknownTld_Is_Not_Link()
    {
        // «co» — зона, но «company» целиком не TLD: ссылке здесь не быть.
        var hrefs = TextLinks.WebHrefs("это site.company, а не ссылка");

        Assert.Empty(hrefs);
    }

    [Fact]
    public void Email_Is_Mail_Link_Not_Web()
    {
        var hrefs = TextLinks.WebHrefs("пиши на test@example.com");
        Assert.Empty(hrefs);

        var segments = TextLinks.Segment("пиши на test@example.com");
        var link = Assert.Single(segments, s => s.Href is not null);
        Assert.Equal(LinkKind.Mail, link.Kind);
        Assert.Equal("mailto:test@example.com", link.Href);
    }

    [Fact]
    public void Phone_With_Plus_Is_Tel_Link()
    {
        var segments = TextLinks.Segment("звони +7 900 123-45-67");
        var link = Assert.Single(segments, s => s.Href is not null);

        Assert.Equal(LinkKind.Phone, link.Kind);
        Assert.Equal("tel:+79001234567", link.Href);
    }

    [Fact]
    public void Markdown_Hides_Target_Behind_Label()
    {
        var segments = TextLinks.Segment("Вот подробность: [тут](https://sc.example.com/docs) заходи");

        var link = Assert.Single(segments, s => s.Href is not null);
        Assert.True(link.IsHiddenTarget);
        Assert.Equal("тут", link.Display);
        Assert.Equal("https://sc.example.com/docs", link.Href);
    }

    [Fact]
    public void Markdown_Target_Without_Scheme_Gets_Https()
    {
        var segments = TextLinks.Segment("[сайт](www.example.com)");
        var link = Assert.Single(segments, s => s.Href is not null);

        Assert.Equal("https://www.example.com", link.Href);
    }

    [Fact]
    public void Mention_Inside_Url_Stays_Inside_Url()
    {
        var segments = TextLinks.Segment("заходи https://site.com/@user скорее");

        var link = Assert.Single(segments, s => s.Href is not null);
        Assert.Equal("https://site.com/@user", link.Href);
    }

    [Fact]
    public void Long_Url_Display_Is_Shortened()
    {
        var longUrl = "https://example.com/some/very/long/path?with=query&and=more#section";
        var segments = TextLinks.Segment(longUrl);
        var link = Assert.Single(segments, s => s.Href is not null);

        Assert.Equal(longUrl, link.Href);
        Assert.True(link.Display.Length <= TextLinks.MaxDisplayLength, "display is short");
        Assert.StartsWith("example.com/", link.Display);
        Assert.EndsWith("…", link.Display);
    }

    [Fact]
    public void WebHrefs_Deduplicates()
    {
        var hrefs = TextLinks.WebHrefs("example.com и https://example.com снова");

        Assert.Single(hrefs);
    }

    [Fact]
    public void PlainText_Produces_Single_Plain_Segment()
    {
        var segments = TextLinks.Segment("просто текст без ссылок");

        var only = Assert.Single(segments);
        Assert.Null(only.Href);
        Assert.Equal("просто текст без ссылок", only.Display);
    }

    [Fact]
    public void Segments_Cover_Whole_Text()
    {
        var text = "go to example.com now";
        var segments = TextLinks.Segment(text);

        Assert.Equal(text, string.Concat(segments.Select(s => s.Display)));
    }
}
