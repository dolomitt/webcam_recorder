using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Accord.Video.FFMPEG;
using AForge.Video;
using AForge.Video.DirectShow;

public class Program
{
    static AppConfig config;
    static HttpListener listener;
    static readonly object sync = new object();
    static VideoCaptureDevice videoDevice;
    static Bitmap latestFrame;
    static string activeId = string.Empty;
    static string currentRecordingPath = string.Empty;
    static volatile bool isRecording;
    static VideoFileWriter videoWriter;
    static int frameWidth;
    static int frameHeight;
    static int frameRate = 30;

    public static void Main(string[] args)
    {
        RunAsync().GetAwaiter().GetResult();
    }

    static async Task RunAsync()
    {
        config = LoadConfig("appsettings.json");
        var prefix = string.Format("http://{0}:{1}/", config.Server.Host, config.Server.Port);

        listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            StopCapture();
            try { listener.Stop(); } catch { }
        };

        Console.WriteLine("Server running on {0}", prefix);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context));
        }
    }

    static async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/start")
        {
            var body = new StreamReader(req.InputStream, req.ContentEncoding).ReadToEnd();
            Dictionary<string, object> payload;
            try
            {
                payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
            }
            catch
            {
                await WriteJsonAsync(res, 400, Dict("error", "Invalid JSON body"));
                return;
            }

            var id = payload.ContainsKey("id") ? Convert.ToString(payload["id"]) : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                await WriteJsonAsync(res, 400, Dict("error", "'id' is required"));
                return;
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                if (id.IndexOf(invalid) >= 0)
                {
                    await WriteJsonAsync(res, 400, Dict("error", "'id' contains invalid filename characters"));
                    return;
                }
            }

            Directory.CreateDirectory(config.Recording.OutputDirectory);
            var recordingPath = Path.Combine(config.Recording.OutputDirectory, id + ".mp4");

            try
            {
                StartCapture(id, recordingPath);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(res, 500, Dict("error", ex.Message));
                return;
            }

            await WriteJsonAsync(res, 200, Dict("file", recordingPath, "stream", "/stream/" + id, "download", "/file/" + id));
            return;
        }

        if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/stop")
        {
            StopCapture();
            await WriteJsonAsync(res, 200, Dict("status", "stopped"));
            return;
        }

        if (req.HttpMethod == "GET" && req.Url.AbsolutePath.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase))
        {
            var id = req.Url.AbsolutePath.Substring("/stream/".Length);
            if (!string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(res, 404, Dict("error", "Live preview not running"));
                return;
            }

            res.StatusCode = 200;
            res.ContentType = "multipart/x-mixed-replace; boundary=frame";
            res.SendChunked = true;

            while (res.OutputStream.CanWrite && string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                byte[] jpegBytes = null;
                lock (sync)
                {
                    if (latestFrame != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            latestFrame.Save(ms, ImageFormat.Jpeg);
                            jpegBytes = ms.ToArray();
                        }
                    }
                }

                if (jpegBytes != null)
                {
                    var header = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpegBytes.Length + "\r\n\r\n");
                    await res.OutputStream.WriteAsync(header, 0, header.Length);
                    await res.OutputStream.WriteAsync(jpegBytes, 0, jpegBytes.Length);
                    var crlf = Encoding.ASCII.GetBytes("\r\n");
                    await res.OutputStream.WriteAsync(crlf, 0, crlf.Length);
                    await res.OutputStream.FlushAsync();
                }

                await Task.Delay(100);
            }

            return;
        }

        if (req.HttpMethod == "GET" && req.Url.AbsolutePath.StartsWith("/file/", StringComparison.OrdinalIgnoreCase))
        {
            var id = req.Url.AbsolutePath.Substring("/file/".Length);
            var path = Path.Combine(config.Recording.OutputDirectory, id + ".mp4");
            if (!File.Exists(path))
            {
                await WriteJsonAsync(res, 404, Dict("error", "Recording not found"));
                return;
            }

            res.StatusCode = 200;
            res.ContentType = "video/mp4";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                res.ContentLength64 = fs.Length;
                await fs.CopyToAsync(res.OutputStream);
            }
            res.OutputStream.Close();
            return;
        }

        await WriteJsonAsync(res, 404, Dict("error", "Not found"));
    }

    static void StartCapture(string id, string recordingPath)
    {
        StopCapture();

        var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No webcam found.");
        }

        FilterInfo selected = null;
        foreach (FilterInfo device in devices)
        {
            if (string.Equals(device.Name, config.Camera.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                selected = device;
                break;
            }
        }
        if (selected == null) selected = devices[0];

        videoDevice = new VideoCaptureDevice(selected.MonikerString);
        videoDevice.NewFrame += delegate(object sender, NewFrameEventArgs eventArgs)
        {
            var frame = (Bitmap)eventArgs.Frame.Clone();
            lock (sync)
            {
                if (frameWidth == 0 || frameHeight == 0)
                {
                    frameWidth = frame.Width;
                    frameHeight = frame.Height;
                }

                if (latestFrame != null) latestFrame.Dispose();
                latestFrame = (Bitmap)frame.Clone();
                if (isRecording)
                {
                    EnsureWriter();
                    if (videoWriter != null)
                    {
                        videoWriter.WriteVideoFrame(latestFrame);
                    }
                }
            }
            frame.Dispose();
        };

        activeId = id;
        currentRecordingPath = recordingPath;
        isRecording = true;
        videoDevice.Start();
    }

    static void StopCapture()
    {
        isRecording = false;
        activeId = string.Empty;
        currentRecordingPath = string.Empty;

        if (videoDevice != null)
        {
            try
            {
                if (videoDevice.IsRunning)
                {
                    videoDevice.SignalToStop();
                    videoDevice.WaitForStop();
                }
            }
            catch { }

            videoDevice = null;
        }

        if (videoWriter != null)
        {
            try { videoWriter.Close(); } catch { }
            videoWriter.Dispose();
            videoWriter = null;
        }

        frameWidth = 0;
        frameHeight = 0;

        lock (sync)
        {
            if (latestFrame != null)
            {
                latestFrame.Dispose();
                latestFrame = null;
            }
        }
    }

    static void EnsureWriter()
    {
        try
        {
            if (videoWriter == null && !string.IsNullOrWhiteSpace(currentRecordingPath) && frameWidth > 0 && frameHeight > 0)
            {
                videoWriter = new VideoFileWriter();
                videoWriter.Open(currentRecordingPath, frameWidth, frameHeight, frameRate, VideoCodec.MPEG4);
            }
        }
        catch { }
    }

    static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var json = new JavaScriptSerializer().Serialize(payload);
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    static object Dict(string k1, object v1) { return new Dictionary<string, object> { { k1, v1 } }; }
    static object Dict(string k1, object v1, string k2, object v2, string k3, object v3) { return new Dictionary<string, object> { { k1, v1 }, { k2, v2 }, { k3, v3 } }; }

    static AppConfig LoadConfig(string path)
    {
        var serializer = new JavaScriptSerializer();
        var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        var cfg = new AppConfig();
        cfg.Server.Host = GetString(root, "Server", "Host", "localhost");
        cfg.Server.Port = GetInt(root, "Server", "Port", 5000);
        cfg.Camera.DeviceName = GetString(root, "Camera", "DeviceName", "Integrated Webcam");
        cfg.Recording.OutputDirectory = GetString(root, "Recording", "OutputDirectory", @"C:\videos");
        return cfg;
    }

    static string GetString(Dictionary<string, object> root, string section, string key, string defaultValue)
    {
        var sec = GetSection(root, section);
        if (sec != null && sec.ContainsKey(key) && sec[key] != null) return Convert.ToString(sec[key]);
        return defaultValue;
    }

    static int GetInt(Dictionary<string, object> root, string section, string key, int defaultValue)
    {
        var sec = GetSection(root, section);
        if (sec != null && sec.ContainsKey(key) && sec[key] != null)
        {
            int n;
            if (int.TryParse(Convert.ToString(sec[key]), out n)) return n;
        }
        return defaultValue;
    }

    static Dictionary<string, object> GetSection(Dictionary<string, object> root, string section)
    {
        if (root != null && root.ContainsKey(section) && root[section] is Dictionary<string, object>) return (Dictionary<string, object>)root[section];
        return null;
    }
}

public class AppConfig
{
    public ServerConfig Server = new ServerConfig();
    public CameraConfig Camera = new CameraConfig();
    public RecordingConfig Recording = new RecordingConfig();
}

public class ServerConfig { public string Host = "localhost"; public int Port = 5000; }
public class CameraConfig { public string DeviceName = "Integrated Webcam"; }
public class RecordingConfig { public string OutputDirectory = @"C:\videos"; }