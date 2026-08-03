using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace PdPower.Mcp;

/// <summary>
/// GUI 프로세스 안에서 도는 MCP HTTP 서버(Streamable HTTP).
/// </summary>
/// <remarks>
/// COM 포트는 한 프로세스만 잡을 수 있으므로, 앱을 쓰면서 AI 로도 제어하려면
/// 서버가 포트를 쥔 GUI 프로세스 안에 살아야 한다. localhost 전용으로만 연다.
///
/// 클라이언트 등록:  claude mcp add --transport http pdpower http://localhost:5115
/// </remarks>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int DefaultPort = 5115;

    private readonly WebApplication _app;

    private McpServerHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Endpoint => $"http://localhost:{Port}";

    public static async Task<McpServerHost> StartAsync(
        IPdPowerGateway gateway, string version, int port = DefaultPort, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();          // GUI 앱이라 콘솔이 없다 — 상태는 앱 Log 화면이 담당
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));

        builder.Services.AddSingleton(gateway);
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "pdpower",
                Title = "WeAct PD Power Mini V1 (Buck)",
                Version = version,
            })
            // Stateless: 요청마다 완결 — 클라이언트가 재시작해도 세션 잔재가 없다
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<PdPowerMcpTools>();

        var app = builder.Build();
        app.MapMcp();

        await app.StartAsync(ct).ConfigureAwait(false);
        return new McpServerHost(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
