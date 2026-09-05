using System.Net.WebSockets;
using System.Security.Cryptography;
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

app.MapGet("/p/{roomId}", (HttpContext context, string roomId, string? token, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (token is null || registry.AuthenticateShortcutUpload(roomId, token) is null) return Results.NotFound();
    string action = $"/p/{Uri.EscapeDataString(roomId)}/upload?token={Uri.EscapeDataString(token)}";
    return Results.Content($$"""<!doctype html><html lang="ko"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PocketBridge</title><style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#f5f7fb;color:#192336;margin:0;padding:28px}main{max-width:460px;margin:auto;background:white;border-radius:20px;padding:28px;box-shadow:0 12px 36px #1b27401a}h1{margin:0 0 9px}p{color:#61708a;line-height:1.6}input,button{width:100%;box-sizing:border-box;padding:14px;border-radius:10px;font-size:16px;margin-top:12px}input{border:1px solid #d7ddea}button{border:0;background:#6857e8;color:white;font-weight:700}small{display:block;margin-top:16px;color:#8490a5}</style><main><h1>PocketBridge</h1><p>이 iPhone에서 보낼 사진, 동영상 또는 파일을 하나 선택하세요. 파일은 Windows PC에 저장됩니다.</p><form method="post" enctype="multipart/form-data" action="{{action}}"><input name="file" type="file" required><button type="submit">Windows로 보내기</button></form><small>이 링크는 QR을 만든 뒤 5시간 동안만 유효합니다.</small></main></html>""", "text/html; charset=utf-8");
});

app.MapPost("/p/{roomId}/upload", async (HttpContext context, string roomId, string? token, RoomRegistry registry) =>
{
    context.Response.Headers.CacheControl = "no-store";
    ShortcutRoom? room = token is null ? null : registry.AuthenticateShortcutUpload(roomId, token);
    if (room is null) return Results.NotFound();
    var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = Wire.MaxFileSize;
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    if (form.Files.Count != 1) return Results.BadRequest("파일을 하나만 선택해 전송하세요.");
    IFormFile file = form.Files[0];
    if (file.Length > Wire.MaxFileSize) return Results.BadRequest("파일이 너무 큽니다.");
    await using Stream body = file.OpenReadStream();
    string sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(body, context.RequestAborted));
    if (!body.CanSeek) return Results.Problem("파일을 다시 읽을 수 없습니다.", statusCode: 500);
    body.Position = 0;
    var metadata = new ShortcutUploadMetadata(file.FileName, file.Length, file.Length, "none", sha256);
    try
    {
        TransferAck receipt = await room.UploadAsync(body, metadata, context.RequestAborted);
        return Results.Content($"<meta name=\"viewport\" content=\"width=device-width\"><h2>전송 완료</h2><p>{System.Net.WebUtility.HtmlEncode(receipt.FileName ?? file.FileName)} 파일을 Windows에 저장했습니다.</p>", "text/html; charset=utf-8");
    }
    catch (ShortcutUploadException error)
    {
        if (error.StatusCode >= 422) registry.Remove(room);
        return Results.Problem(error.Message, statusCode: error.StatusCode);
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
