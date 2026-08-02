using System.Net;
using System.Net.Http;

namespace AccessibilityModManager.Tests.Helpers;

/// <summary>
/// Answers every request with whatever <c>respond</c> returns for its absolute URL. Enough for the
/// clients under test, which fetch a document and sometimes its <c>.sig</c> beside it.
/// </summary>
internal sealed class RouteHandler(Func<string, string> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request.RequestUri!.AbsoluteUri))
        });
}

/// <summary>
/// The same, at the byte level. <see cref="RouteHandler"/> encodes a string, which cannot express
/// the cases where the exact bytes are the point — an invalid UTF-8 sequence, a byte-order mark, a
/// body larger than the reader's ceiling.
/// </summary>
internal sealed class ByteRouteHandler(Func<string, byte[]> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(respond(request.RequestUri!.AbsoluteUri))
        });
}
