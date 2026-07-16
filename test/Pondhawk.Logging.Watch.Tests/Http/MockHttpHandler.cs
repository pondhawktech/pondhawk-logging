using System.Net;

namespace Pondhawk.Logging.Watch.Tests.Http;

/// <summary>
/// A test HttpMessageHandler that delegates to a configurable function.
/// </summary>
public class MockHttpHandler : HttpMessageHandler
{

    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public List<HttpRequestMessage> Requests { get; } = [];

    public MockHttpHandler()
    {
        _handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    public void SetHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public void RespondWith(HttpStatusCode status)
    {
        _handler = (_, _) => Task.FromResult(new HttpResponseMessage(status));
    }

    public void RespondWith(HttpStatusCode status, HttpContent content)
    {
        // Read the content bytes upfront so we can produce fresh content per request
        var bytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var mediaType = content.Headers.ContentType?.MediaType ?? "application/json";
        _handler = (_, _) =>
        {
            var fresh = new ByteArrayContent(bytes);
            fresh.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(status) { Content = fresh });
        };
    }

    public void ThrowOnSend(Exception ex)
    {
        _handler = (_, _) => throw ex;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _handler(request, cancellationToken);
    }

}
