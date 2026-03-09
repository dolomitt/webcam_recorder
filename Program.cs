using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

// Simple command-line HTTP server that exposes:
// POST /start { "id": "123" }
// POST /stop
//
// Settings are read from appsettings.json.

var config = await LoadConfigAsync("appsettings.json");

var prefix = $"http://{config.Server.Host}:{config.Server.Port}/";

var listener = new HttpListener();
listener.Prefixes.Add(prefix);

Process? ffmpegProcess = null;
var processLock = new object();

try
{
    listener.Start();
}
catch (HttpListenerException ex)
{
    Console.WriteLine($"Failed to start listener on {prefix}");
    Console.WriteLine(ex.Message);
    Console.WriteLine("Tip: ensure no other process is already using this URL/port.");
    return;
}

Console.WriteLine($"Server running on {prefix}");
Console.WriteLine("Press Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Shutting down...");

    listener.Stop();

    lock (processLock)
    {
        if (ffmpegProcess is { HasExited: false })
        {
            try
            {
                ffmpegProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort shutdown
            }
        }
    }

    Environment.Exit(0);
};

while (true)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (HttpListenerException)
    {
        // Listener stopped
        break;
    }
    catch (ObjectDisposedException)
    {
        break;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            await HandleRequestAsync(context);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, 500, new { error = ex.Message });
        }
    });
}

async Task HandleRequestAsync(HttpListenerContext context)
{
    var request = context.Request;
    var response = context.Response;

    if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/start")
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();

        string? id;
        try
        {
            using var doc = JsonDocument.Parse(body);
            id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        }
        catch
        {
            await WriteJsonAsync(response, 400, new { error = "Invalid JSON body" });
            return;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            await WriteJsonAsync(response, 400, new { error = "'id' is required" });
            return;
        }

        // Basic filename safety for id
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (id.Contains(invalid))
            {
                await WriteJsonAsync(response, 400, new { error = "'id' contains invalid filename characters" });
                return;
            }
        }

        var videosDir = config.Recording.OutputDirectory;
        Directory.CreateDirectory(videosDir);
        var extension = config.Recording.FileExtension.TrimStart('.');
        var path = Path.Combine(videosDir, $"{id}.{extension}");

        lock (processLock)
        {
            if (ffmpegProcess is { HasExited: false })
            {
                ffmpegProcess.Kill(entireProcessTree: true);
                ffmpegProcess.WaitForExit(2000);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = config.Ffmpeg.ExecutablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = false,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(config.Camera.InputFormat);

            if (!string.IsNullOrWhiteSpace(config.Camera.Resolution))
            {
                startInfo.ArgumentList.Add("-video_size");
                startInfo.ArgumentList.Add(config.Camera.Resolution);
            }

            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add($"video={config.Camera.DeviceName}");

            if (!string.IsNullOrWhiteSpace(config.Recording.OutputFormat))
            {
                startInfo.ArgumentList.Add("-f");
                startInfo.ArgumentList.Add(config.Recording.OutputFormat);
            }

            if (!string.IsNullOrWhiteSpace(config.Recording.VideoCodec))
            {
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add(config.Recording.VideoCodec);
            }

            if (!string.IsNullOrWhiteSpace(config.Recording.PixelFormat))
            {
                startInfo.ArgumentList.Add("-pix_fmt");
                startInfo.ArgumentList.Add(config.Recording.PixelFormat);
            }

            if (config.Recording.FastStart)
            {
                startInfo.ArgumentList.Add("-movflags");
                startInfo.ArgumentList.Add("+faststart");
            }

            startInfo.ArgumentList.Add(path);

            ffmpegProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start ffmpeg");
        }

        await WriteJsonAsync(response, 200, new { file = path });
        return;
    }

    if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/stop")
    {
        lock (processLock)
        {
            if (ffmpegProcess is { HasExited: false })
            {
                try
                {
                    ffmpegProcess.StandardInput.WriteLine("q");
                    ffmpegProcess.StandardInput.Flush();
                    if (!ffmpegProcess.WaitForExit(5000))
                    {
                        ffmpegProcess.Kill(entireProcessTree: true);
                        ffmpegProcess.WaitForExit(2000);
                    }
                }
                catch
                {
                    ffmpegProcess.Kill(entireProcessTree: true);
                    ffmpegProcess.WaitForExit(2000);
                }
            }
        }

        await WriteJsonAsync(response, 200, new { status = "stopped" });
        return;
    }

    await WriteJsonAsync(response, 404, new { error = "Not found" });
}

static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
{
    response.StatusCode = statusCode;
    response.ContentType = "application/json";

    var json = JsonSerializer.Serialize(payload);
    var buffer = Encoding.UTF8.GetBytes(json);
    response.ContentLength64 = buffer.Length;

    await response.OutputStream.WriteAsync(buffer);
    response.OutputStream.Close();
}

static async Task<AppConfig> LoadConfigAsync(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Missing config file: {path}");
    }

    var json = await File.ReadAllTextAsync(path);
    var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (config is null)
    {
        throw new InvalidOperationException("Invalid configuration. Could not deserialize appsettings.json");
    }

    if (string.IsNullOrWhiteSpace(config.Camera.DeviceName))
    {
        throw new InvalidOperationException("Configuration error: Camera.DeviceName is required.");
    }

    if (string.IsNullOrWhiteSpace(config.Recording.FileExtension))
    {
        throw new InvalidOperationException("Configuration error: Recording.FileExtension is required.");
    }

    if (config.Server.Port < 1 || config.Server.Port > 65535)
    {
        throw new InvalidOperationException("Configuration error: Server.Port must be between 1 and 65535.");
    }

    return config;
}

sealed class AppConfig
{
    public ServerConfig Server { get; set; } = new();
    public CameraConfig Camera { get; set; } = new();
    public RecordingConfig Recording { get; set; } = new();
    public FfmpegConfig Ffmpeg { get; set; } = new();
}

sealed class ServerConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5000;
}

sealed class CameraConfig
{
    public string InputFormat { get; set; } = "dshow";
    public string DeviceName { get; set; } = "HD Webcam";
    public string Resolution { get; set; } = "1280x720";
}

sealed class RecordingConfig
{
    public string OutputDirectory { get; set; } = @"C:\videos";
    public string FileExtension { get; set; } = "mp4";
    public string? OutputFormat { get; set; }
    public string VideoCodec { get; set; } = "libx264";
    public string PixelFormat { get; set; } = "yuv420p";
    public bool FastStart { get; set; } = true;
}

sealed class FfmpegConfig
{
    public string ExecutablePath { get; set; } = "ffmpeg";
}
