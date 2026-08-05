using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The settings transaction, exercised against the REAL <see cref="ConfigService"/> on disk.
///
/// <para>The view-model tests use a fake that hands back one shared object, so they cannot see
/// serialization, file locking, or two independent snapshots — which is precisely where a
/// read-modify-write race lives. These use two separate services over one directory, the way two
/// copies of the manager would.</para>
/// </summary>
public sealed class ConfigTransactionTests : IDisposable
{
    private readonly string _dir;

    public ConfigTransactionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "config-tx-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private ConfigService NewService() => new(TestLogger.Create(), _dir);

    [Fact]
    public async Task An_update_re_reads_so_it_cannot_write_from_a_stale_snapshot()
    {
        // The race this exists for: one part of the app loads settings, something else changes them,
        // and the first one saves what it loaded — erasing the change. Adding a source is the worst
        // case, because it spans a network fetch AND however long someone spends reading a warning.
        var a = NewService();
        var b = NewService();

        // A takes its snapshot BEFORE B writes anything.
        var stale = await a.LoadAsync();

        await b.UpdateAsync(c => c.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu")));

        // A now makes its own unrelated change through the transaction.
        stale.DefaultChannel = "beta";
        await a.UpdateAsync(c => c.DefaultChannel = "beta");

        var final = await NewService().LoadAsync();
        Assert.Single(final.UserPluginSources);
        Assert.Equal("beta", final.DefaultChannel);
    }

    [Fact]
    public async Task Concurrent_updates_all_survive()
    {
        // Ten writers, each adding its own source. If the transaction were a plain load-modify-save,
        // the later writes would clobber the earlier ones and the count would come out short.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var service = NewService();
            await service.UpdateAsync(c => c.UserPluginSources.Add(TestUserSource.Accepted($"src{i}")));
        }));

        var final = await NewService().LoadAsync();
        Assert.Equal(10, final.UserPluginSources.Count);
    }

    [Fact]
    public async Task A_removal_is_not_resurrected_by_another_writer()
    {
        // A source the user believes removed coming back on the next start is the outcome that
        // would most damage trust in the feature.
        var setup = NewService();
        await setup.UpdateAsync(c =>
        {
            c.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu"));
            c.UserPluginSources.Add(TestUserSource.Accepted("someone", "Someone"));
        });

        await Task.WhenAll(
            NewService().UpdateAsync(c => c.UserPluginSources.RemoveAll(s => s.PluginId == "buu420")),
            NewService().UpdateAsync(c => c.DefaultChannel = "beta"));

        var final = await NewService().LoadAsync();
        Assert.DoesNotContain(final.UserPluginSources, s => s.PluginId == "buu420");
        Assert.Contains(final.UserPluginSources, s => s.PluginId == "someone");
        Assert.Equal("beta", final.DefaultChannel);
    }

    [Fact]
    public async Task What_an_update_writes_can_be_read_back_by_the_source_loader()
    {
        // Serialization actually round-trips the new fields. A source saved but not readable would
        // be added, announced, and then silently ignored on the next start.
        await NewService().UpdateAsync(c =>
            c.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu")));

        var reloaded = await NewService().LoadAsync();
        var accepted = UserPluginSourceValidation.Accept(reloaded.UserPluginSources);

        Assert.Single(accepted.Accepted);
        Assert.Empty(accepted.Rejected);
        Assert.Equal("Buu", accepted.Accepted[0].DisplayName);
    }

    [Fact]
    public async Task An_old_config_without_the_sources_field_still_loads()
    {
        // The upgrade case for everyone who already has this app.
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.json"),
            """{"defaultChannel":"stable","knownGameOverrides":{}}""");

        var config = await NewService().LoadAsync();

        Assert.Empty(config.UserPluginSources);
        Assert.Null(NewService().LastLoadProblem);
    }
}
