using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ImageViewer;

public record ImageUploadRequest(string Photo);

public class Program
{
    private static string latestImageData = string.Empty;
    private static List<WebSocket> connectedClients = new();

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        // WebSocket endpoint
        app.UseWebSockets();
        app.Map("/ws", HandleWebSocketConnection);

        // Endpoint to receive images as JSON
        app.MapPost("/api/image", HandleImageUpload);

        // Serve the static HTML page
        app.MapGet("/", () => Results.Content(GetHtmlContent(), "text/html"));

        await app.RunAsync();
    }

    private static async Task HandleWebSocketConnection(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            connectedClients.Add(webSocket);

            // Send the latest image immediately after connection
            if (!string.IsNullOrEmpty(latestImageData))
            {
                await webSocket.SendAsync(
                    Encoding.UTF8.GetBytes(latestImageData),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }

            // Keep the connection alive and handle disconnection
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
    }

    private static async Task<IResult> HandleImageUpload(HttpContext context)
    {
        try
        {
            // Read and parse the JSON request
            var request = await JsonSerializer.DeserializeAsync<ImageUploadRequest>(
                context.Request.Body,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (request == null || string.IsNullOrEmpty(request.Photo))
            {
                return Results.BadRequest("Missing or invalid 'photo' field");
            }

            // Ensure the image data is properly formatted
            latestImageData = request.Photo.StartsWith("data:image")
                ? request.Photo
                : $"data:image/jpeg;base64,{request.Photo}";

            // Broadcast to all connected WebSocket clients
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

            // Clean up dead connections
            foreach (var socket in deadSockets)
            {
                connectedClients.Remove(socket);
            }

            return Results.Ok(new { message = "Image received and broadcast successfully" });
        }
        catch (JsonException)
        {
            return Results.BadRequest("Invalid JSON format");
        }
        catch (Exception)
        {
            return Results.StatusCode(500); // Internal Server Error
        }
    }

    private static string GetHtmlContent() => """
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
    """;
}