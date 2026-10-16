using System.Security.Claims;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Storage;
using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Imaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class SecurityChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nJob ownership and fail-closed MCP authentication");
        const string tenant = "11111111-1111-1111-1111-111111111111";
        const string userA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string userB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        static ClaimsPrincipal User(string tenantId, string objectId, bool mapped = false) => new(new ClaimsIdentity(
            [new(mapped ? "http://schemas.microsoft.com/identity/claims/tenantid" : "tid", tenantId),
             new(mapped ? "http://schemas.microsoft.com/identity/claims/objectidentifier" : "oid", objectId)], "test"));
        var owner = User(tenant, userA);
        check(JobOwnerFilter.ReadOwner(User(tenant, userA, true)) == JobOwnerFilter.ReadOwner(owner),
            "mapped and unmapped Entra claims identify the same owner");
        var store = new InMemoryJobStore();
        await store.CreateAsync(new ScanJobDocument { Id = "owned", JobId = "owned", OwnerTenantId = tenant, OwnerObjectId = userA });
        await store.CreateAsync(new ScanJobDocument { Id = "legacy", JobId = "legacy" });
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Enfolderer.Ai.Infrastructure.Storage.IJobStore>(store).BuildServiceProvider();
        foreach (var route in new[] { "/jobs/owned", "/jobs/owned/content", "/jobs/owned/submit", "/jobs/owned/annotated-image" })
            foreach (var caller in new[] { owner, User(tenant, userB), User(userB, userA), new ClaimsPrincipal() })
            {
                var http = new DefaultHttpContext { RequestServices = services, User = caller };
                http.Request.Path = route;
                http.Request.RouteValues["jobId"] = "owned";
                var reached = false;
                var result = await new JobOwnerFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ =>
                {
                    reached = true;
                    return ValueTask.FromResult<object?>(Results.Ok());
                });
                var allowed = ReferenceEquals(caller, owner);
                check(reached == allowed && (allowed || result is IStatusCodeHttpResult { StatusCode: 401 or 404 }),
                    $"{route}: {(allowed ? "owner admitted" : "other tenant/user or anonymous caller denied before handler")}");
            }
        foreach (var id in new[] { "legacy", "absent" })
        {
            var http = new DefaultHttpContext { RequestServices = services, User = owner };
            http.Request.RouteValues["jobId"] = id;
            var result = await new JobOwnerFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(http),
                _ => throw new InvalidOperationException("Ownerless or absent jobs must not reach a handler."));
            check(result is IStatusCodeHttpResult { StatusCode: 404 }, $"{id} jobs do not disclose results or claim a new owner");
        }
        foreach (var arguments in new[]
        {
            new[] { "--McpServer:TenantId=", "--McpServer:Audience=", "--McpServer:AllowedCallerObjectIds=" },
            new[] { $"--McpServer:TenantId={tenant}", "--McpServer:Audience=api://test", "--McpServer:AllowedCallerObjectIds=" }
        })
        {
            try
            {
                await using var app = McpServerHost.Create(arguments, "security-check", (_, _) => { });
                check(false, "MCP must reject missing authentication/allow-list configuration");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("refuses to start"))
            {
                check(true, "MCP refuses to start without authentication and a caller allow-list");
            }
        }
        foreach (ushort orientation in new ushort[] { 1, 6, 8 })
        {
            using var source = new Image<Rgba32>(16, 12, Color.DarkBlue);
            for (var y = 0; y < source.Height; y++)
                for (var x = 0; x < source.Width; x++)
                    source[x, y] = new Rgba32((byte)(x * 13), (byte)(y * 17), (byte)((x + y) * 7));
            source.Metadata.ExifProfile = new ExifProfile();
            source.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
            source.Metadata.ExifProfile.SetValue(ExifTag.Artist, "Private test identity");
            source.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
            source.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitude,
                new[] { new Rational(1), new Rational(2), new Rational(3) });
            using var encoded = new MemoryStream();
            source.SaveAsJpeg(encoded);
            var original = encoded.ToArray();
            var sanitized = JpegMetadataSanitizer.StripPrivateMetadata(original);
            using var before = Image.Load<Rgba32>(original);
            using var after = Image.Load<Rgba32>(sanitized);
            var originalPixels = new Rgba32[before.Width * before.Height];
            var sanitizedPixels = new Rgba32[after.Width * after.Height];
            before.CopyPixelDataTo(originalPixels);
            after.CopyPixelDataTo(sanitizedPixels);
            check(originalPixels.SequenceEqual(sanitizedPixels)
                && (after.Metadata.ExifProfile?.GetValue(ExifTag.Orientation)?.Value ?? 1) == orientation,
                $"JPEG metadata sanitation retains every decoded pixel and orientation {orientation}");
            check(after.Metadata.ExifProfile?.Values.All(v => v.Tag == ExifTag.Orientation) != false,
                "sanitized EXIF contains no GPS coordinates or personal identifiers");
            check(JpegMetadataSanitizer.StripPrivateMetadata(sanitized).SequenceEqual(sanitized),
                "JPEG metadata sanitation is idempotent");
        }
    }
}
