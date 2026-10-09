using System.Net.Http.Json;
using System.Text.Json;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class PluginDirectoryClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RequirePluginAvailableAsync(string pluginId,
        AccessibilityModManager.Core.Interfaces.IConfigService config, CancellationToken ct,
        string? indexUrl = null)
    {
        var settings = await config.LoadAsync();
        indexUrl ??= settings.UserPluginSources.FirstOrDefault(source => source.PluginId == pluginId)?.IndexUrl
            ?? settings.KnownPluginAddresses.GetValueOrDefault(pluginId);
        await RequireAvailableAsync(indexUrl ?? "", ct);
    }

    public async Task<IReadOnlyList<PluginDirectoryEntry>> ListAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync(PluginDirectoryProtocol.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var snapshot = await ReadAsync<PluginDirectorySnapshot>(response, deadline.Token);
        if (snapshot.Version != 1 || snapshot.Plugins is null || snapshot.Plugins.Count > 1000)
            throw new InvalidDataException("The plugin directory format is not supported.");
        foreach (var plugin in snapshot.Plugins)
        {
            if (plugin is null || plugin.Games is null || plugin.Games.Count > 500 ||
                plugin.GitHubAccountId <= 0 || string.IsNullOrWhiteSpace(plugin.PluginId) ||
                !Uri.TryCreate(plugin.IndexUrl, UriKind.Absolute, out var url) ||
                url.Scheme != "https" || url.Host != "raw.githubusercontent.com" ||
                url.UserInfo.Length != 0 || !url.IsDefaultPort)
                throw new InvalidDataException("The plugin directory contains an invalid source.");
        }
        return snapshot.Plugins;
    }

    public async Task SubmitAsync(PluginDirectorySubmission submission, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.PostAsJsonAsync(PluginDirectoryProtocol.Url + "/register", submission, deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    public async Task RequireAvailableAsync(string indexUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(indexUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new InvalidOperationException("The plugin's source address is unavailable. Refresh the catalog before installing.");
        // The owner's signed catalog is separate from third-party discovery and remains usable offline.
        if (uri.Host == "accessibilitymods.com" && uri.AbsolutePath.StartsWith("/registry/plugins/", StringComparison.Ordinal)) return;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(PluginDirectoryProtocol.Url + "/availability?indexUrl=" +
                Uri.EscapeDataString(indexUrl), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (!(await ReadAsync<PluginAvailability>(response, deadline.Token)).Available)
                throw new InvalidOperationException("This author's downloads are unavailable. Existing installed files have not been changed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Could not check this author's download availability. Try again when the service is reachable. Existing installed files have not been changed.", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Directory response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Directory response is too large.");
            output.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), Json) ?? throw new InvalidDataException("Empty directory response.");
    }
}
