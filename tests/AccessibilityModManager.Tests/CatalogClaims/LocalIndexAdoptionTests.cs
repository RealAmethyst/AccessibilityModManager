using System.Text;
using AccessibilityModManager.AuthorTool.Services;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The one place reconciliation writes over the author's folder.
///
/// <para>Everything above this decides whether a replacement is allowed; this is where it happens,
/// and the property that matters is that it never happens to a folder somebody else has changed
/// since it was compared.</para>
/// </summary>
public sealed class LocalIndexAdoptionTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public LocalIndexAdoptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "adopt-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "index.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void An_unchanged_file_is_replaced()
    {
        var original = Bytes("{\"a\":1}");
        File.WriteAllBytes(_path, original);

        var result = LocalIndexAdoption.ReplaceIfUnchanged(_path, original, Bytes("{\"b\":2}"), out var error);

        Assert.Equal(AdoptionResult.Replaced, result);
        Assert.Null(error);
        Assert.Equal("{\"b\":2}", File.ReadAllText(_path));
    }

    [Fact]
    public void A_file_that_changed_since_it_was_compared_is_left_exactly_as_it_is()
    {
        File.WriteAllBytes(_path, Bytes("{\"a\":1}"));
        var somebodyElse = Bytes("{\"theirs\":true}");
        File.WriteAllBytes(_path, somebodyElse);

        var result = LocalIndexAdoption.ReplaceIfUnchanged(
            _path, Bytes("{\"a\":1}"), Bytes("{\"adopted\":true}"), out var error);

        // Whatever is there now was written by someone with a better claim to it than a background
        // fetch that started before they did.
        Assert.Equal(AdoptionResult.Superseded, result);
        Assert.Null(error);
        Assert.Equal(somebodyElse, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_file_that_is_a_byte_longer_is_not_treated_as_unchanged()
    {
        var original = Bytes("{\"a\":1}");
        File.WriteAllBytes(_path, Bytes("{\"a\":1} "));

        var result = LocalIndexAdoption.ReplaceIfUnchanged(_path, original, Bytes("{\"b\":2}"), out _);

        Assert.Equal(AdoptionResult.Superseded, result);
    }

    [Fact]
    public void A_file_that_is_gone_is_a_failure_and_not_a_replacement()
    {
        var result = LocalIndexAdoption.ReplaceIfUnchanged(
            _path, Bytes("{\"a\":1}"), Bytes("{\"b\":2}"), out var error);

        // Deliberately not "there is nothing there, so write it": adoption replaces a project's
        // index, and a project whose index has vanished is a problem to report rather than to fill in.
        Assert.Equal(AdoptionResult.Failed, result);
        Assert.NotNull(error);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void The_replacement_is_the_whole_file_and_not_an_overlay()
    {
        // A shorter document must not leave the tail of the longer one behind, which an in-place
        // rewrite without truncation would.
        var original = Bytes("{\"long\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}");
        File.WriteAllBytes(_path, original);

        LocalIndexAdoption.ReplaceIfUnchanged(_path, original, Bytes("{}"), out _);

        Assert.Equal("{}", File.ReadAllText(_path));
    }
}
