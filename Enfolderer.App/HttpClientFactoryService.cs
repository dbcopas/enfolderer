using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;

namespace Enfolderer.App;

public interface IHttpClientFactoryService
{
    HttpClient Client { get; }
}

public class HttpClientFactoryService : IHttpClientFactoryService
{
    private readonly TelemetryService? _telemetry; // optional
    public HttpClient Client { get; }

    public HttpClientFactoryService(TelemetryService? telemetry)
    {
        _telemetry = telemetry;
        Client = CreateClient();
    }

    private HttpClient CreateClient()
    {
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var c = new HttpClient(new HttpLoggingHandler(sockets, _telemetry));
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer/0.1");
        c.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/yourrepo)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    private class HttpLoggingHandler : DelegatingHandler
    {
        private readonly TelemetryService? _tel;
        public HttpLoggingHandler(HttpMessageHandler inner, TelemetryService? tel) : base(inner) { _tel = tel; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var sw = Stopwatch.StartNew();
            _tel?.Start(url);
            try
            {
                var resp = await base.SendAsync(request, cancellationToken);
                sw.Stop();
                _tel?.Done(url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
                return resp;
            }
            catch
            {
                sw.Stop();
                _tel?.Done(url, -1, sw.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
