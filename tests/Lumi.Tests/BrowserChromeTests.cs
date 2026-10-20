using System;
using System.IO;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

public sealed class BrowserChromeTests
{
    [Theory]
    [InlineData(1000, 1, 200)]
    [InlineData(300, 2, 148)]
    [InlineData(288, 5, 54)]
    [InlineData(300, 8, 54)]
    [InlineData(0, 3, 200)]
    public void TabsShareTheStripBetweenTheirMinimumAndMaximumWidth(double available, int count, double expected)
        => Assert.Equal(expected, BrowserView.CalculateTabWidth(available, count));

    [Theory]
    [InlineData(148, false, false)]
    [InlineData(80, false, false)]
    [InlineData(69, false, true)]
    [InlineData(94, true, false)]
    [InlineData(80, true, true)]
    public void NarrowTabsDropTheirTitleBeforeItBecomesAnEllipsis(double width, bool isActive, bool compact)
        => Assert.Equal(compact, BrowserView.IsCompactTab(width, isActive));

    [Theory]
    [InlineData("github.com", "github")]
    [InlineData("en.wikipedia.org", "wikipedia")]
    [InlineData("docs.avaloniaui.net", "avaloniaui")]
    [InlineData("www.bbc.co.uk", "bbc")]
    [InlineData("www.gov.uk", "gov")]
    [InlineData("docs.bmw.de", "bmw")]
    [InlineData("amazon.com.au", "amazon")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void BadgesUseTheNameASiteIsKnownBy(string host, string expected)
        => Assert.Equal(expected, BrowserView.GetSiteName(host));

    [Theory]
    [InlineData("https://www.github.com/adirh3/Lumi?tab=readme#top", "Secure", "github.com", "/adirh3/Lumi?tab=readme#top", "github")]
    [InlineData("https://example.com/", "Secure", "example.com", "", "example")]
    [InlineData("http://localhost:5173/a%20b", "Insecure", "localhost:5173", "/a b", "localhost")]
    [InlineData("about:blank", "Blank", "", "about:blank", "")]
    [InlineData("", "Blank", "", "about:blank", "")]
    [InlineData("data:text/html,hi", "Other", "", "data:text/html,hi", "")]
    public void LocationEmphasizesTheSite(string url, string kind, string host, string rest, string site)
    {
        var location = BrowserView.DescribeLocation(url);

        Assert.Equal(kind, location.Kind.ToString());
        Assert.Equal(host, location.Host);
        Assert.Equal(rest, location.Rest);
        Assert.Equal(site, location.Site);
    }

    [Fact]
    public void LocalFilesShowTheirPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "page.html");

        var location = BrowserView.DescribeLocation(new Uri(path).AbsoluteUri);

        Assert.Equal("File", location.Kind.ToString());
        Assert.Equal(path, location.Rest);
    }
}
