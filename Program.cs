// Program.cs
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string latestImageData = string.Empty;
List<WebSocket> connectedClients = new();

app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        connectedClients.Add(webSocket);

        if (!string.IsNullOrEmpty(latestImageData))
        {
            await webSocket.SendAsync(
                Encoding.UTF8.GetBytes(latestImageData),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }

        try
        {
            var buffer = new byte[1024 * 4];
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        finally
        {
            connectedClients.Remove(webSocket);
        }
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

app.MapPost("/api/image", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    latestImageData = await reader.ReadToEndAsync();

    var deadSockets = new List<WebSocket>();
    foreach (var socket in connectedClients)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(latestImageData),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            else
            {
                deadSockets.Add(socket);
            }
        }
        catch
        {
            deadSockets.Add(socket);
        }
    }

    foreach (var socket in deadSockets)
    {
        connectedClients.Remove(socket);
    }

    return Results.Ok();
});

app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html>
    <head>
        <title>Live Image Viewer</title>
        <style>
            body { font-family: Arial, sans-serif; margin: 20px; }
            #imageDisplay { max-width: 100%; height: auto; margin-top: 20px; }
            #status { color: #666; }
        </style>
    </head>
    <body>
        <h1>Live Image Viewer</h1>
        <div id="status">Connecting...</div>
        <img id="imageDisplay" alt="Live Feed" />
        
        <script>
            let ws;
            const status = document.getElementById('status');
            
            function connect() {
                ws = new WebSocket(`${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/ws`);
                
                ws.onopen = () => {
                    status.textContent = 'Connected';
                    status.style.color = 'green';
                };
                
                ws.onclose = () => {
                    status.textContent = 'Disconnected - Reconnecting...';
                    status.style.color = 'red';
                    setTimeout(connect, 2000);
                };
                
                ws.onmessage = (event) => {
                    document.getElementById('imageDisplay').src = event.data;
                };
                
                ws.onerror = (error) => {
                    console.error('WebSocket error:', error);
                    ws.close();
                };
            }
            
            connect();
        </script>
    </body>
    </html>
    """, "text/html"));

app.Run();