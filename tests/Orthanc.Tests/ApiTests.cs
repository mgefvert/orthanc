using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Core.Services;

namespace Orthanc.Tests;

[TestClass]
public sealed class ApiTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly SqliteConnection Connection = new("Data Source=:memory:");

        public Factory() => Connection.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Reports:Enabled"] = "false"
                }));

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<OrthancContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<OrthancContext>>();
                services.AddDbContext<OrthancContext>(o => o.UseSqlite(Connection));
                services.AddSingleton<IStartupFilter, AddressFilter>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                Connection.Dispose();
        }
    }

    private sealed class AddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proceed) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
                return proceed();
            });
            next(app);
        };
    }

    [TestMethod]
    public async Task HttpAuthenticationTenantIsolationAndRevocation()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new("https://localhost")
        });

        var first = Credentials.Generate();
        var second = Credentials.Generate();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrthancContext>();
            await db.Database.EnsureCreatedAsync();
            db.Customers.AddRange(new DbCustomer { Id = 1 }, new DbCustomer { Id = 2 });
            db.ApiKeys.AddRange(new DbApiKey { CustomerId = 1, KeyHash = Credentials.Hash(first) },
                new DbApiKey { CustomerId = 2, KeyHash = Credentials.Hash(second) });
            await db.SaveChangesAsync();
        }

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var request = new StartRequest("HOST", "backup", DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/jobruns/start", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first);
        var started = await client.PostAsJsonAsync("/api/v1/jobruns/start", request);
        Assert.AreEqual(HttpStatusCode.Created, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<StartResponse>())!.Id;
        var completion = new CompleteRequest(DateTimeOffset.UtcNow, 1, 0, [new("stderr", "fatal: failed")]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/jobruns/{id}/complete", completion)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first);
        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/jobruns/{id}/complete", completion)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/jobruns/{id}/complete", completion)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/jobruns/{id}/complete", completion with { ExitCode = 4 })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/jobruns/start",
            new StringContent("{invalid", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrthancContext>();
            var key = await db.ApiKeys.SingleAsync(x => x.CustomerId == 1);
            key.RevokedZ = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/jobruns/start", request)).StatusCode);
    }

    [TestMethod]
    public async Task HttpRequiresTlsAndDoesNotTrustUnconfiguredForwarding()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("http://localhost") });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/jobruns/start", new StartRequest("host", "job", DateTimeOffset.UtcNow))).StatusCode);
    }

    [TestMethod]
    public async Task RepeatedInvalidCredentialsTriggerSharedIpBanMiddleware()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrthancContext>().Database.EnsureCreatedAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('a', 64));
        var request = new StartRequest("host", "job", DateTimeOffset.UtcNow);
        for (var i = 0; i < 51; i++)
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/jobruns/start", request)).StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/jobruns/start", request)).StatusCode);
    }

    private sealed record StartResponse(Guid Id);
}
