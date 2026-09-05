using System.Net.WebSockets;
using Tessera.Server;
using Tessera.Sync;
using Tessera.Sync.Wire;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IBoardRepository, InMemoryBoardRepository>();
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddTransient<BoardConnection>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    foreach (var converter in WireFormat.Options.Converters)
        options.SerializerOptions.Converters.Add(converter);
});

const string DevelopmentCors = "development";

builder.Services.AddCors(options => options.AddPolicy(DevelopmentCors, policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.UseCors(DevelopmentCors);

app.UseWebSockets(new WebSocketOptions
{
    // Idle NAT and load balancers drop silent connections, and a board can sit untouched for
    // minutes.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/boards/{boardId}", async (
    string boardId, RoomRegistry registry, CancellationToken cancellationToken) =>
{
    var room = await registry.OpenAsync(new BoardId(boardId), cancellationToken);

    return Results.Ok(new
    {
        board = boardId,
        shapes = room.Shapes.Select(shape => new
        {
            id = shape.Id.Value,
            properties = shape.Properties,
        }),
    });
});

app.MapGet("/api/boards/{boardId}/socket", async (
    HttpContext context,
    string boardId,
    BoardConnection connection,
    CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
        return Results.BadRequest("This endpoint expects a WebSocket upgrade.");

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await connection.RunAsync(socket, new BoardId(boardId), cancellationToken);

    return Results.Empty;
});

await app.RunAsync();
