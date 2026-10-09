using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record PatreonSessionStatus(
    bool IsSignedIn,
    string? MemberName,
    string? CampaignId);

public sealed record PatreonTierInfo(string TierId, string DisplayName);

public sealed record PatreonAttachmentInfo(
    string SelectionId,
    string FileName,
    string? DownloadUrl)
{
    public long? SizeBytes { get; init; }
    public IReadOnlyList<string> RequiredTierIds { get; init; } = [];
}

public sealed record PatreonPostInspection(
    string PostId,
    IReadOnlyList<PatreonAttachmentInfo> Attachments);

public interface IPatreonAuthorSession
{
    bool IsSignedIn { get; }
    PatreonAccount? CurrentAccount { get; }
    PatreonOwnCampaign? OwnCampaign { get; }
    Task LoadAsync();
    Task SignInAsync(CancellationToken ct);
    Task SignOutAsync(CancellationToken ct);
    Task<PatreonOwnCampaign?> RefreshOwnCampaignAsync(CancellationToken ct);
    Task<(IReadOnlyList<PatreonPostAttachment> Attachments, string? DebugFilePath)>
        ValidatePostUrlAsync(string postUrl, CancellationToken ct);
}

public sealed class PatreonAuthorSession(PatreonAuthorService service) : IPatreonAuthorSession
{
    public bool IsSignedIn => service.IsSignedIn;
    public PatreonAccount? CurrentAccount => service.CurrentAccount;
    public PatreonOwnCampaign? OwnCampaign => service.OwnCampaign;
    public Task LoadAsync() => service.LoadAsync();
    public Task SignInAsync(CancellationToken ct) => service.SignInAsync(ct);
    public Task SignOutAsync(CancellationToken ct) => service.SignOutAsync(ct);
    public Task<PatreonOwnCampaign?> RefreshOwnCampaignAsync(CancellationToken ct) =>
        service.RefreshOwnCampaignAsync(ct);
    public Task<(IReadOnlyList<PatreonPostAttachment> Attachments, string? DebugFilePath)>
        ValidatePostUrlAsync(string postUrl, CancellationToken ct) =>
        service.ValidatePostUrlAsync(postUrl, ct);
}

public interface IPatreonWorkflow
{
    Task<WorkflowResult<PatreonSessionStatus>> GetStatusAsync(CancellationToken ct);
    Task<WorkflowResult<PatreonSessionStatus>> SignInAsync(CancellationToken ct);
    Task<WorkflowResult<bool>> SignOutAsync(CancellationToken ct);
    Task<WorkflowResult<IReadOnlyList<PatreonTierInfo>>> GetTiersAsync(CancellationToken ct);
    Task<WorkflowResult<PatreonPostInspection>> InspectPostAsync(string postUrl, CancellationToken ct);
}

public sealed class PatreonWorkflow(IPatreonAuthorSession session) : IPatreonWorkflow
{
    public async Task<WorkflowResult<PatreonSessionStatus>> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await session.LoadAsync();
            return Success(
                "patreonStatus",
                Status(),
                session.IsSignedIn
                    ? "Signed in to Patreon."
                    : "Not signed in to Patreon.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<PatreonSessionStatus>(
                WorkflowErrorKind.Authentication,
                "patreonStatusFailed",
                ex.Message);
        }
    }

    public async Task<WorkflowResult<PatreonSessionStatus>> SignInAsync(CancellationToken ct)
    {
        try
        {
            await session.SignInAsync(ct);
            if (!session.IsSignedIn)
            {
                return Failure<PatreonSessionStatus>(
                    WorkflowErrorKind.Authentication,
                    "patreonSignInFailed",
                    "Patreon sign-in returned without an account.");
            }

            return Success("patreonSignedIn", Status(), "Signed in to Patreon.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<PatreonSessionStatus>(
                WorkflowErrorKind.Authentication,
                "patreonSignInFailed",
                ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> SignOutAsync(CancellationToken ct)
    {
        try
        {
            await session.LoadAsync();
            await session.SignOutAsync(ct);
            return Success("patreonSignedOut", true, "Signed out of Patreon and removed the saved author session.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<bool>(WorkflowErrorKind.Authentication, "patreonSignOutFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<IReadOnlyList<PatreonTierInfo>>> GetTiersAsync(
        CancellationToken ct)
    {
        try
        {
            await session.LoadAsync();
            if (!session.IsSignedIn)
            {
                return Failure<IReadOnlyList<PatreonTierInfo>>(
                    WorkflowErrorKind.Authentication,
                    "patreonSignInRequired",
                    "Sign in to Patreon before loading your campaign tiers.");
            }

            var campaign = await session.RefreshOwnCampaignAsync(ct);
            if (campaign is null)
            {
                return Failure<IReadOnlyList<PatreonTierInfo>>(
                    WorkflowErrorKind.Validation,
                    "patreonCampaignMissing",
                    "The signed-in Patreon account has no creator campaign available.");
            }

            IReadOnlyList<PatreonTierInfo> tiers = campaign.Tiers
                .Select(tier => new PatreonTierInfo(tier.Id, tier.DisplayLabel))
                .ToArray();
            return Success(
                "patreonTiersListed",
                tiers,
                $"Found {tiers.Count} tier(s) for {campaign.DisplayName} ({campaign.CampaignId}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<IReadOnlyList<PatreonTierInfo>>(
                WorkflowErrorKind.Authentication,
                "patreonTierRefreshFailed",
                ex.Message);
        }
    }

    public Task<WorkflowResult<PatreonPostInspection>> InspectPostAsync(string postUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var postId = PatreonAuthorService.ExtractPostId(postUrl);
        return Task.FromResult(postId is null
            ? Failure<PatreonPostInspection>(WorkflowErrorKind.Validation, "patreonPostUrlInvalid",
                "Use an HTTPS Patreon post URL ending with a numeric post id.")
            : Success("patreonPostUrlValidated", new PatreonPostInspection(postId, []),
                "Post URL syntax validated only. Patreon does not expose attachment downloads in its documented v2 API. Upload the wrapped ZIP in your browser and record its exact filename."));
    }

    private PatreonSessionStatus Status()
    {
        var account = session.CurrentAccount;
        return new PatreonSessionStatus(
            session.IsSignedIn,
            account?.FullName ?? account?.Email,
            session.OwnCampaign?.CampaignId);
    }

    private static WorkflowResult<T> Success<T>(string status, T value, string message) =>
        new(status, value, new[] { message });

    private static WorkflowResult<T> Failure<T>(
        WorkflowErrorKind kind,
        string status,
        string message) =>
        new(status, default, new[] { message }, kind);
}
