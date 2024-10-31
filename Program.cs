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
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>єПрапор</title>
            <style>
                body { font-family: Arial, sans-serif; margin: 20px; }
                #imageDisplay { max-width: 100%; height: auto; margin-top: 20px; }
                #status { color: #666; }
            </style>
        </head>
        <body>
            <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAANYAAAArCAYAAAAE73bpAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAAEnQAABJ0Ad5mH3gAABBTSURBVHhe7Z0HsBRFE8cPI2ZExQBGFFQEA4qIERSzJAMqKGZBETMYS1SCAYyYAxm0DGXAwkBSS0xlAANJEHMEBREjzFe/Zvo5b5i7e7t3e/f42H9V8+5uZ2dnZ/rf09PTu2RMDrz22mvmkUceMddee6059dRTTatWrcz+++8vcuCBB0YSztlvv/3MkUceabp27Wpuuukm89hjj5lZs2bZqxUPb7/9thk6dKi5/vrrzRlnnGEOPfRQs/POO5sNNtjAZDKZSFKrVi05lzqo64YbbjDDhg0z7733nr1aihTLYzliffvtt+b88883++67rxkxYkQiig9+//138/7774vyQ7j777/fHomHX375xVxxxRVm4403DhIkCalbt660/7fffrOtSJFiGSoR68MPPzTt2rUzb7zxhv2lNPjjjz+ExMcdd5z9JRq++uorU69evaDyl0K222478/3339vWpEjhEOvzzz8Xd6ec1hcXrn379vZb1TB//nxTv379oMKXUrbffnszb94826oUKzsqiMUa6IsvvrDfyofRo0ebyy+/3H7LjwMOOCCo6OUQ1qApUgAh1qBBg8zw4cPlh+qAE044wXz55Zf2W3Y8/fTTQQUvp7zwwgu2dSlWZgixjjrqKPlSKG6//XbzySef2G/xQR3XXHON/ZYdLVu2DCp3OaVYfZlixUZm2rRppk+fPvZrYdhwww3NmWeeab8Vhs6dO9tPYbC2Cil2dRAinilWbmTuvfde8+qrr9qvuTFx4kRz991322+VQWQOpdp9993tL5WxaNEi06lTJ/stPy644ALz66+/2m/L45lnnllOoauLjB8/3rYyxcqKzGmnnWYWLlxov+bGyy+/LIpz9tln21/+w9y5c+XYLrvsYn/5D+wxNWrUyKy66qr2l/wg/J5LQa+++upKylyd5NZbb7WtTLGyIhNl70iJhfju488//yy/77PPPvaXZfjnn3/kN45FIdbrr79uhgwZYr8tD1xFbUscWWONNczBBx9sevfuLSSeMGGCmTRpkhk5cqRskMfJ0lDp0aOHbWVlYMAmT55snn322ciCkSGcv3TpUltbiuqM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" alt="Red dot" />
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