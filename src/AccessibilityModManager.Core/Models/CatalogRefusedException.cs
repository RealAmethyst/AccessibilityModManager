namespace AccessibilityModManager.Core.Models;

/// <summary>
/// A developer's catalog was reached and refused, with a reason fit to be read aloud.
///
/// <para><b>Why a type rather than a message.</b> The Mods tab speaks the failure, and speaking
/// <c>ex.Message</c> for whatever came back means a screen reader reading out CLR type names, JSON
/// paths, line numbers and byte offsets when an unsigned plugin serves a wrong JSON type. The
/// underlying exception still goes to the log, where that detail is useful; what reaches the user is
/// <see cref="Reason"/>.</para>
///
/// <para>It is a sentence, capitalised and terminated, so a caller can put it straight after
/// "Amethyst's mods couldn't be loaded." without stitching grammar together.</para>
/// </summary>
/// <remarks>
/// Derived from <see cref="InvalidOperationException"/> rather than <see cref="Exception"/> because
/// that is already the family every refusal in this codebase belongs to, and callers that catch it
/// are catching exactly this. Narrowing the base would have quietly changed which of them still
/// caught a refused catalog.
/// </remarks>
public sealed class CatalogRefusedException : InvalidOperationException
{
    public CatalogRefusedException(string pluginId, string reason, Exception? inner = null)
        : base(reason, inner)
    {
        PluginId = pluginId;
        Reason = reason;
    }

    public string PluginId { get; }

    /// <summary>What to say. Never empty — an unexplained refusal is indistinguishable from a bug.</summary>
    public string Reason { get; }

    /// <summary>
    /// The reason to speak for any failure, typed or not. Anything unrecognised becomes one plain
    /// sentence rather than framework text, because there is no exception whose message was written
    /// for someone listening to it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why <see cref="InvalidOperationException"/> is trusted here.</b> It is this
    /// codebase's channel for a written refusal: the size bound, the replay guard and the
    /// signing-key checks all raise one carrying a sentence composed for a listener. Narrowing this
    /// to <see cref="CatalogRefusedException"/> alone was tried and reverted — it replaced "this
    /// release is older than the version already accepted" and "the catalog is larger than N bytes"
    /// with a blank "the catalog couldn't be read", which is worse: those are security refusals the
    /// user is entitled to hear.</para>
    ///
    /// <para><b>The residual risk, stated plainly.</b> A framework-generated
    /// <see cref="InvalidOperationException"/> — LINQ's "Sequence contains no matching element", say
    /// — would also be spoken verbatim. Closing that means giving the written refusals a type of
    /// their own so intent is declared at the throw site rather than inferred here. Until then this
    /// is a deliberate trade, not an oversight.</para>
    /// </remarks>
    public static string SpeakableReason(Exception ex) => ex switch
    {
        CatalogRefusedException refused => refused.Reason,
        InvalidOperationException { Message.Length: > 0 } known => known.Message,
        _ => "The catalog couldn't be read."
    };
}
