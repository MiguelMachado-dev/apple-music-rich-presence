using System.Net;
using System.Text;

namespace AppleMusicPresence.Specs;

internal sealed class FakeAppleHttpHandler(Func<Uri, string> respond) : HttpMessageHandler
{
    internal int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request.RequestUri!), Encoding.UTF8, "application/json")
        });
    }
}
