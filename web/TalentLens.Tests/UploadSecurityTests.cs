using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TalentLens.Infrastructure.Security;
using TalentLens.Web.Security;
using TalentLens.Web.Services;

namespace TalentLens.Tests;

public class MalwareScanTests
{
    /// <summary>A tiny fake clamd: reads one INSTREAM request and answers like ClamAV would.</summary>
    private static (int Port, Task<byte[]> Received) StartFakeClamd(Func<byte[], string> reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var net = client.GetStream();
            var command = new byte["zINSTREAM\0".Length];
            await net.ReadExactlyAsync(command);
            Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));

            var data = new MemoryStream();
            var header = new byte[4];
            while (true)
            {
                await net.ReadExactlyAsync(header);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0) break;
                var chunk = new byte[length];
                await net.ReadExactlyAsync(chunk);
                data.Write(chunk);
            }
            await net.WriteAsync(Encoding.ASCII.GetBytes(reply(data.ToArray()) + "\0"));
            listener.Stop();
            return data.ToArray();
        });
        return (port, received);
    }

    private static ClamAvMalwareScanner Scanner(int port) => new(
        Options.Create(new MalwareScanOptions { Provider = "ClamAV", Host = "127.0.0.1", Port = port, TimeoutSeconds = 5 }),
        NullLogger<ClamAvMalwareScanner>.Instance);

    [Fact]
    public async Task Clean_file_is_streamed_completely_and_stream_is_rewound()
    {
        var content = new byte[200_000]; // > one 64 KB chunk
        Random.Shared.NextBytes(content);
        var (port, received) = StartFakeClamd(_ => "stream: OK");
        var stream = new MemoryStream(content);

        var result = await Scanner(port).ScanAsync(stream);

        Assert.Equal(MalwareScanVerdict.Clean, result.Verdict);
        Assert.Equal(content, await received);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task Infected_file_reports_the_threat_name()
    {
        var (port, _) = StartFakeClamd(_ => "stream: Eicar-Test-Signature FOUND");

        var result = await Scanner(port).ScanAsync(new MemoryStream("X5O!P%@AP"u8.ToArray()));

        Assert.Equal(MalwareScanVerdict.Infected, result.Verdict);
        Assert.Equal("Eicar-Test-Signature", result.ThreatName);
    }

    [Fact]
    public async Task Unreachable_scanner_is_an_error_not_a_pass()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens there now

        var result = await Scanner(port).ScanAsync(new MemoryStream([1, 2, 3]));

        Assert.Equal(MalwareScanVerdict.Error, result.Verdict);
    }

    [Theory]
    [InlineData("stream: OK", MalwareScanVerdict.Clean)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND\0", MalwareScanVerdict.Infected)]
    [InlineData("INSTREAM size limit exceeded. ERROR", MalwareScanVerdict.Error)]
    [InlineData("", MalwareScanVerdict.Error)]
    public void Interprets_clamd_replies(string reply, MalwareScanVerdict expected) =>
        Assert.Equal(expected, ClamAvMalwareScanner.Interpret(reply).Verdict);
}

public class UploadScreenerTests
{
    private sealed class FixedScanner(MalwareScanResult result) : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken ct = default) => Task.FromResult(result);
    }

    private static async Task<UploadValidator.Result> Screen(MalwareScanVerdict verdict, bool failClosed = true,
        byte[]? bytes = null)
    {
        bytes ??= "%PDF-1.7 fake"u8.ToArray();
        var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "cv.pdf");
        var screener = new UploadScreener(
            new FixedScanner(new MalwareScanResult(verdict, "Eicar")),
            Options.Create(new MalwareScanOptions { FailClosed = failClosed }),
            NullLogger<UploadScreener>.Instance);
        return await screener.ScreenAsync(file, stream, maxMb: 10, CancellationToken.None);
    }

    [Fact]
    public async Task Clean_and_unscanned_files_pass()
    {
        Assert.True((await Screen(MalwareScanVerdict.Clean)).Ok);
        Assert.True((await Screen(MalwareScanVerdict.NotScanned)).Ok);
    }

    [Fact]
    public async Task Infected_files_are_blocked()
    {
        var result = await Screen(MalwareScanVerdict.Infected);
        Assert.False(result.Ok);
        Assert.Equal("UploadInfected", result.ErrorKey);
    }

    [Fact]
    public async Task Scanner_errors_block_only_when_fail_closed()
    {
        Assert.Equal("UploadScanFailed", (await Screen(MalwareScanVerdict.Error, failClosed: true)).ErrorKey);
        Assert.True((await Screen(MalwareScanVerdict.Error, failClosed: false)).Ok);
    }

    [Fact]
    public async Task Validation_runs_before_scanning()
    {
        var result = await Screen(MalwareScanVerdict.Clean, bytes: "not a pdf"u8.ToArray());
        Assert.Equal("ContentMismatch", result.ErrorKey);
    }
}

public class RateLimitingTests
{
    private static async Task<HttpClient> StartAppAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAppRateLimiting(builder.Configuration);

        var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapPost("/login", () => "ok").RequireRateLimiting(RateLimitPolicies.Auth);
        app.MapGet("/page", () => "ok");
        app.MapGet("/css/site.css", () => "body{}");
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Auth_policy_rejects_with_429_and_retry_after()
    {
        var client = await StartAppAsync(new() { ["RateLimiting:AuthPerMinute"] = "2" });

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", null)).StatusCode);
        var third = await client.PostAsync("/login", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Contains("wait", await third.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Global_limit_applies_to_pages_but_not_static_files()
    {
        var client = await StartAppAsync(new() { ["RateLimiting:GlobalPerMinute"] = "1" });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/page")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/page")).StatusCode);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/site.css")).StatusCode);
    }

    [Fact]
    public async Task Can_be_disabled()
    {
        var client = await StartAppAsync(new()
        {
            ["RateLimiting:Enabled"] = "false", ["RateLimiting:AuthPerMinute"] = "1",
        });
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", null)).StatusCode);
    }
}
