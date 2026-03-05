using System.Security;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Tests.Security;

public class UrlValidatorTests
{
    [Fact]
    public void RequireHttps_AcceptsHttpsUrl()
    {
        var uri = new Uri("https://example.com/file.zip");
        UrlValidator.RequireHttps(uri, "test");
    }

    [Fact]
    public void RequireHttps_RejectsHttpUrl()
    {
        var uri = new Uri("http://example.com/file.zip");
        Assert.Throws<SecurityException>(() => UrlValidator.RequireHttps(uri, "test"));
    }

    [Fact]
    public void RequireHttps_RejectsFtpUrl()
    {
        var uri = new Uri("ftp://example.com/file.zip");
        Assert.Throws<SecurityException>(() => UrlValidator.RequireHttps(uri, "test"));
    }

    [Fact]
    public void RequireHttps_String_RejectsInvalidUrl()
    {
        Assert.Throws<ArgumentException>(() => UrlValidator.RequireHttps("not-a-url", "test"));
    }

    [Fact]
    public void RequireHttps_String_AcceptsHttpsUrl()
    {
        UrlValidator.RequireHttps("https://example.com/file.zip", "test");
    }
}
