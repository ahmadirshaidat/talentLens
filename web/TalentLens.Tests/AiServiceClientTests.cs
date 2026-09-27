using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using TalentLens.Infrastructure.AiService;

namespace TalentLens.Tests;

public class AiServiceClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (AiServiceClient Client, StubHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ai/") };
        return (new AiServiceClient(http), handler);
    }

    [Fact]
    public async Task Search_sends_snake_case_and_parses_results()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """
            [{"candidate_id": "c1", "score": 0.91, "reason": "Python",
              "evidence": [{"section": "skills", "quote": "Python, SQL"}]}]
            """);

        var results = await client.SearchAsync("ws1", "python dev", topK: 5);

        Assert.Equal("http://ai/search", handler.Request!.RequestUri!.ToString());
        Assert.Contains("\"workspace_id\":\"ws1\"", handler.RequestBody);
        Assert.Contains("\"top_k\":5", handler.RequestBody);
        var hit = Assert.Single(results);
        Assert.Equal("c1", hit.CandidateId);
        Assert.Equal(0.91, hit.Score, 3);
        Assert.Equal("Python, SQL", Assert.Single(hit.Evidence).Quote);
    }

    [Fact]
    public async Task Ingest_parses_profile()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """
            {"full_name": "سارة خالد", "email": null, "phone": null, "location": "Amman",
             "years_of_experience": 4.5, "skills": ["React"], "languages": ["Arabic"],
             "job_titles": [], "education": [{"degree": "B.Sc.", "year": "2020"}]}
            """);

        var profile = await client.IngestAsync(new MemoryStream([1, 2, 3]), "cv.pdf", "c1", "ws1");

        Assert.Equal("سارة خالد", profile.FullName);
        Assert.Equal(4.5, profile.YearsOfExperience);
        Assert.Equal("2020", profile.Education[0]["year"]);
        Assert.Contains("name=candidate_id", handler.RequestBody);
    }

    [Theory]
    [InlineData(415, """{"detail": "Unsupported file type '.txt'"}""", "Unsupported file type '.txt'", true)]
    [InlineData(422, """{"detail": [{"msg": "String should match pattern"}]}""", "String should match pattern", true)]
    [InlineData(502, "Bad gateway", "Bad gateway", false)]
    public async Task Errors_surface_the_detail_message(int status, string body, string detail, bool permanent)
    {
        var (client, _) = Create((HttpStatusCode)status, body);

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => client.SearchAsync("ws1", "x"));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(detail, ex.Detail);
        Assert.Equal(permanent, ex.IsPermanent);
    }

    [Fact]
    public async Task Delete_escapes_ids_in_url()
    {
        var (client, handler) = Create(HttpStatusCode.NoContent, "");

        await client.DeleteCandidateAsync("c-1", "ws 1");

        Assert.Equal("/candidates/c-1?workspace_id=ws%201", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Delete, handler.Request.Method);
    }

    [Fact]
    public async Task Health_is_false_when_service_is_down()
    {
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://ai/") };
        Assert.False(await new AiServiceClient(http).IsHealthyAsync());
    }

    [Theory]
    [InlineData("secret-key", true)]
    [InlineData(null, false)]
    public async Task Handler_adds_service_key_and_request_id(string? key, bool expectKey)
    {
        var stub = new StubHandler(HttpStatusCode.OK, "[]");
        var handler = new AiServiceHandler(Options.Create(new AiServiceOptions { ApiKey = key })) { InnerHandler = stub };
        var client = new AiServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://ai/") });

        await client.SearchAsync("ws1", "x");

        var headers = stub.Request!.Headers;
        Assert.Equal(expectKey, headers.Contains(AiServiceHandler.ApiKeyHeader));
        if (expectKey) Assert.Equal(key, headers.GetValues(AiServiceHandler.ApiKeyHeader).Single());
        Assert.Matches("^[0-9a-f]{32}$", headers.GetValues(AiServiceHandler.RequestIdHeader).Single());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
