using System.Net.WebSockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using PocketBridge.Core;
using PocketBridge.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<RelayOptions>()
    .Bind(builder.Configuration.GetSection("Relay"))
    .Validate(o => o.MaxRooms is >= 1 and <= 100_000, "MaxRooms must be between 1 and 100000.")
    .Validate(o => o.WaitingMinutes is >= 1 and <= 60, "WaitingMinutes must be between 1 and 60.")
    .Validate(o => o.ActiveHours is >= 1 and <= 24, "ActiveHours must be between 1 and 24.")
    .ValidateOnStart();
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoomRegistry>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
    options.AddPolicy("create", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});

var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
    KeepAliveTimeout = TimeSpan.FromSeconds(20)
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = 1 }));
app.MapPost("/api/rooms", (HttpContext context, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return registry.Create() is { } room
        ? Results.Json(room, statusCode: StatusCodes.Status201Created)
        : Results.Problem("The relay is at capacity. Try again later.", statusCode: 503);
}).RequireRateLimiting("create");

app.MapPost("/api/shortcut/rooms", (HttpContext context, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return registry.CreateShortcut() is { } room
        ? Results.Json(room, statusCode: StatusCodes.Status201Created)
        : Results.Problem("The relay is at capacity. Try again later.", statusCode: 503);
}).RequireRateLimiting("create");

app.Map("/ws/shortcut/{roomId}/receiver", async (HttpContext context, string roomId, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    string? token = Bearer(context.Request);
    if (token is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var reservation = registry.ReserveShortcutReceiver(roomId, token, out int status);
    if (reservation is null)
    {
        context.Response.StatusCode = status;
        return;
    }
    using (reservation)
    {
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, reservation.Room.Stopped);
            if (!reservation.Attach(socket)) return;
            try { await reservation.Room.RunReceiverAsync(socket, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (WebSocketException) { }
        }
        finally { registry.Remove(reservation.Room); }
    }
});

app.MapPost("/api/shortcut/{roomId}/upload", async (HttpContext context, string roomId, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    string? token = Bearer(context.Request);
    ShortcutRoom? room = token is null ? null : registry.AuthenticateShortcutUpload(roomId, token);
    if (room is null) return Results.NotFound();
    var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = Wire.MaxFileSize;
    try
    {
        ShortcutUploadMetadata metadata = ShortcutUploadMetadata.Parse(context.Request);
        TransferAck receipt = await room.UploadAsync(context.Request.Body, metadata, context.RequestAborted);
        return Results.Ok(new { ok = true, fileName = receipt.FileName ?? metadata.Name, bytes = metadata.OriginalSize });
    }
    catch (BadHttpRequestException error)
    {
        return Results.Json(new { ok = false, error = error.Message }, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (ShortcutUploadException error)
    {
        if (error.StatusCode is >= 422) registry.Remove(room);
        return Results.Json(new { ok = false, error = error.Message }, statusCode: error.StatusCode);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        registry.Remove(room);
        return Results.Empty;
    }
});

// Map (rather than MapGet) also permits HTTP/2 WebSocket CONNECT requests.
app.Map("/ws/{roomId}/{role}", async (HttpContext context, string roomId, string role, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var header = context.Request.Headers.Authorization.ToString();
    const string bearer = "Bearer ";
    if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var reservation = registry.Reserve(roomId, role, header[bearer.Length..], out var status);
    if (reservation is null)
    {
        context.Response.StatusCode = status;
        return;
    }

    using (reservation)
    {
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, reservation.Room.Stopped);
            if (!reservation.Attach(socket)) return;
            try
            {
                // The request stays alive until its socket has finished forwarding.
                await reservation.Room.ForwardAsync(reservation.Role, socket, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (WebSocketException) { /* Disconnects are normal, and do not expose connection details. */ }
        }
        finally
        {
            registry.Remove(reservation.Room);
        }
    }
});

app.Run();

static string? Bearer(HttpRequest request)
{
    string header = request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length
        ? header[prefix.Length..]
        : null;
}
