using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Answers ARM from canned JSON so the deployment path can be exercised without a subscription.
/// Azure.Core.TestFramework would give us this for free, but it is not on the package feed.
/// </summary>
internal sealed class StubArmTransport : HttpPipelineTransport
{
    private readonly Func<RequestMethod, string, (int Status, string Body)> _respond;

    public StubArmTransport(Func<RequestMethod, string, (int Status, string Body)> respond) =>
        _respond = respond;

    public List<string> Requests { get; } = [];

    public override Request CreateRequest() => new StubRequest();

    public override void Process(HttpMessage message) =>
        ProcessAsync(message).AsTask().GetAwaiter().GetResult();

    public override async ValueTask ProcessAsync(HttpMessage message)
    {
        await Task.Yield();

        var uri = message.Request.Uri.ToString();
        Requests.Add($"{message.Request.Method} {uri}");

        var (status, body) = _respond(message.Request.Method, uri);

        var response = new StubResponse(status, message.Request.ClientRequestId);
        response.SetContent(body);
        response.AddHeader(new HttpHeader("Content-Type", "application/json"));
        message.Response = response;
    }

    private sealed class StubRequest : Request
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public override string ClientRequestId { get; set; } = Guid.NewGuid().ToString();

        protected override void AddHeader(string name, string value) => _headers[name] = value;
        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);
        protected override bool RemoveHeader(string name) => _headers.Remove(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() =>
            _headers.Select(h => new HttpHeader(h.Key, h.Value));

        protected override bool TryGetHeader(string name, out string value)
        {
            var found = _headers.TryGetValue(name, out var header);
            value = header!;
            return found;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            if (_headers.TryGetValue(name, out var single))
            {
                values = [single];
                return true;
            }

            values = null!;
            return false;
        }

        public override void Dispose() => Content?.Dispose();
    }

    private sealed class StubResponse : Response
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public StubResponse(int status, string clientRequestId)
        {
            Status = status;
            ClientRequestId = clientRequestId;
        }

        public override int Status { get; }
        public override string ReasonPhrase => Status < 400 ? "OK" : "Error";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; }

        public void SetContent(string content) =>
            ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        public void AddHeader(HttpHeader header) => _headers[header.Name] = header.Value;

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() =>
            _headers.Select(h => new HttpHeader(h.Key, h.Value));

        protected override bool TryGetHeader(string name, out string value)
        {
            var found = _headers.TryGetValue(name, out var header);
            value = header!;
            return found;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            if (_headers.TryGetValue(name, out var single))
            {
                values = [single];
                return true;
            }

            values = null!;
            return false;
        }

        // Deliberately does not close ContentStream. Azure.Core inspects the buffered body after
        // disposing the response when it decides whether a long-running operation is complete,
        // and there is nothing to release for a MemoryStream anyway.
        public override void Dispose()
        {
        }
    }
}

internal sealed class StubCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("stub-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
