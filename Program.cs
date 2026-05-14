using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.ServiceProcess;
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
    static int frameRate;
    static long nextRecordingTick;
    static FtpVideoSync ftpVideoSync;
    static readonly object logSync = new object();
    static string logFilePath;
    static long logMaxSizeBytes;
    static int logMaxRetainedFiles;
    static CancellationTokenSource serverCts;
    static Task listenerLoopTask;
    static readonly object lifecycleSync = new object();
    static readonly object notificationSync = new object();
    static readonly Dictionary<string, PendingVideoNotification> pendingNotifications = new Dictionary<string, PendingVideoNotification>(StringComparer.OrdinalIgnoreCase);
    static bool hostStarted;
    static DateTime recordingStartedUtc;
    const string VideoEvidenceSavePath = "/request-register/videoEvidence/save";

    public static void Main(string[] args)
    {
        if (ShouldRunAsService(args))
        {
            ServiceBase.Run(new WebcamRecorderService());
            return;
        }

        RunConsoleAsync().GetAwaiter().GetResult();
    }

    static bool ShouldRunAsService(string[] args)
    {
        if (args != null)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return !Environment.UserInteractive;
    }

    static async Task RunConsoleAsync()
    {
        var done = new TaskCompletionSource<bool>();
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            StopHost();
            done.TrySetResult(true);
        };

        StartHost();
        Log("Press Ctrl+C to stop.");
        await done.Task;
    }

    internal static void StartHost()
    {
        lock (lifecycleSync)
        {
            if (hostStarted) return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(baseDir, "appsettings.json");
            config = LoadConfig(configPath);
            InitializeLogging(config.FileLogging);
            ApplySecuritySettings();
            frameRate = config.Recording.FrameRate;
            var prefix = string.Format("http://{0}:{1}/", config.Server.Host, config.Server.Port);

            listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            serverCts = new CancellationTokenSource();
            listenerLoopTask = Task.Run(() => ListenLoopAsync(serverCts.Token));
            StartFtpSyncLoop();

            hostStarted = true;
            Log("Server running on {0}", prefix);
        }
    }

    internal static void StopHost()
    {
        lock (lifecycleSync)
        {
            if (!hostStarted) return;

            StopFtpSyncLoop();
            QueuePendingNotification(StopCapture());

            if (serverCts != null)
            {
                try { serverCts.Cancel(); } catch { }
            }

            if (listener != null)
            {
                try { listener.Stop(); } catch { }
                try { listener.Close(); } catch { }
                listener = null;
            }

            if (listenerLoopTask != null)
            {
                try { listenerLoopTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
                listenerLoopTask = null;
            }

            if (serverCts != null)
            {
                serverCts.Dispose();
                serverCts = null;
            }

            hostStarted = false;
        }
    }

    static async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
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

        // Minimal HTTP API: start/stop recording, stream the live preview, or download an MP4.
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

            if (!IsValidRecordingId(id))
            {
                await WriteJsonAsync(res, 400, Dict("error", "'id' contains invalid filename characters"));
                return;
            }

            var applicationId = GetApplicationId(id);
            if (string.IsNullOrWhiteSpace(applicationId))
            {
                await WriteJsonAsync(res, 400, Dict("error", "'id' must start with an application id"));
                return;
            }

            var recordingPath = BuildRecordingPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(recordingPath));

            if (isRecording)
            {
                await WriteJsonAsync(res, 500, Dict("error", "Recording already in progress"));
                return;
            }

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
            if (!isRecording)
            {
                await WriteJsonAsync(res, 500, Dict("error", "No recording is currently in progress"));
                return;
            }

            var stoppedRecording = StopCapture();
            QueuePendingNotification(stoppedRecording);
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
            if (!IsValidRecordingId(id))
            {
                await WriteJsonAsync(res, 400, Dict("error", "'id' contains invalid filename characters"));
                return;
            }

            var path = BuildRecordingPath(id);
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

    static bool IsValidRecordingId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (id.IndexOf(invalid) >= 0) return false;
        }

        return true;
    }

    static string BuildRecordingPath(string id)
    {
        // The application id is the first segment before "_"; each application gets its own folder.
        var applicationId = GetApplicationId(id);
        return Path.Combine(config.Recording.OutputDirectory, applicationId, id + ".mp4");
    }

    internal static string GetApplicationId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;

        var underscoreIndex = id.IndexOf('_');
        if (underscoreIndex < 0) return id;
        return id.Substring(0, underscoreIndex);
    }

    static void StartCapture(string id, string recordingPath)
    {
        if (isRecording)
        {
            throw new InvalidOperationException("Recording already in progress");
        }

        StopCapture();
        nextRecordingTick = 0;

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
        ApplyConfiguredResolution(videoDevice);
        videoDevice.NewFrame += delegate(object sender, NewFrameEventArgs eventArgs)
        {
            var frame = (Bitmap)eventArgs.Frame.Clone();
            lock (sync)
            {
                // The writer is opened lazily once the camera delivers the first frame dimensions.
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
                    if (videoWriter != null && ShouldWriteCurrentFrame())
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
        recordingStartedUtc = DateTime.UtcNow;
        videoDevice.Start();
    }

    static void ApplyConfiguredResolution(VideoCaptureDevice device)
    {
        if (config.Camera.Width <= 0 || config.Camera.Height <= 0) return;

        // DirectShow only accepts advertised capability objects, so find the exact configured size.
        foreach (var capability in device.VideoCapabilities)
        {
            if (capability.FrameSize.Width == config.Camera.Width &&
                capability.FrameSize.Height == config.Camera.Height)
            {
                device.VideoResolution = capability;
                Log("Camera resolution set to {0}x{1}.", config.Camera.Width, config.Camera.Height);
                return;
            }
        }

        Log("Configured camera resolution {0}x{1} is unavailable. Using camera default.",
            config.Camera.Width,
            config.Camera.Height);
    }

    static PendingVideoNotification StopCapture()
    {
        var hadRecording = isRecording || !string.IsNullOrWhiteSpace(activeId) || !string.IsNullOrWhiteSpace(currentRecordingPath);
        var stoppedId = activeId;
        var stoppedPath = currentRecordingPath;
        var startedAtUtc = recordingStartedUtc;

        isRecording = false;
        activeId = string.Empty;
        currentRecordingPath = string.Empty;
        recordingStartedUtc = DateTime.MinValue;

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
        nextRecordingTick = 0;

        lock (sync)
        {
            if (latestFrame != null)
            {
                latestFrame.Dispose();
                latestFrame = null;
            }
        }

        if (!hadRecording || string.IsNullOrWhiteSpace(stoppedPath))
        {
            return null;
        }

        var durationSeconds = 0L;
        if (startedAtUtc != DateTime.MinValue)
        {
            var elapsed = DateTime.UtcNow - startedAtUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            durationSeconds = (long)Math.Ceiling(elapsed.TotalSeconds);
        }

        var fileSizeKb = 0L;
        if (File.Exists(stoppedPath))
        {
            var fileInfo = new FileInfo(stoppedPath);
            fileSizeKb = (long)Math.Ceiling(fileInfo.Length / 1024d);
        }

        var videoFormat = Path.GetExtension(stoppedPath) ?? string.Empty;
        if (videoFormat.StartsWith("."))
        {
            videoFormat = videoFormat.Substring(1);
        }

        return new PendingVideoNotification
        {
            ApplicationId = GetApplicationId(stoppedId),
            LocalPath = stoppedPath,
            FileName = Path.GetFileNameWithoutExtension(stoppedPath),
            VideoFormat = string.IsNullOrWhiteSpace(videoFormat) ? "mp4" : videoFormat,
            FileSizeKb = fileSizeKb,
            DurationSeconds = durationSeconds
        };
    }

    static void StartFtpSyncLoop()
    {
        // FTP sync owns its own background loop; Program only supplies config and the active-file guard.
        ftpVideoSync = new FtpVideoSync(config.Ftp, config.Recording.OutputDirectory, IsActiveRecordingFile, OnFileUploadedToFtp, Log);
        ftpVideoSync.Start();
    }

    static void StopFtpSyncLoop()
    {
        if (ftpVideoSync == null) return;
        ftpVideoSync.Stop();
        ftpVideoSync = null;
    }

    static bool IsActiveRecordingFile(string path)
    {
        if (!isRecording) return false;
        if (string.IsNullOrWhiteSpace(currentRecordingPath)) return false;
        return string.Equals(
            Path.GetFullPath(path).TrimEnd('\\'),
            Path.GetFullPath(currentRecordingPath).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    static void QueuePendingNotification(PendingVideoNotification notification)
    {
        if (notification == null || string.IsNullOrWhiteSpace(notification.LocalPath)) return;

        var normalizedPath = NormalizePath(notification.LocalPath);
        lock (notificationSync)
        {
            pendingNotifications[normalizedPath] = notification;
        }

        Log("Queued video-evidence notification for {0}", notification.FileName);
    }

    static void OnFileUploadedToFtp(string localPath, string remoteFileName)
    {
        if (string.IsNullOrWhiteSpace(config.Notification.BaseUrl))
        {
            return;
        }

        var notification = DequeuePendingNotification(localPath) ?? CreateFallbackNotification(localPath);
        if (notification == null)
        {
            Log("Video-evidence notification skipped: no metadata found for {0}", localPath);
            return;
        }

        notification.VideoPath = BuildRemoteVideoDirectory(remoteFileName);
        SendVideoEvidenceNotification(notification);
    }

    static PendingVideoNotification DequeuePendingNotification(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return null;

        var normalizedPath = NormalizePath(localPath);
        lock (notificationSync)
        {
            PendingVideoNotification notification;
            if (pendingNotifications.TryGetValue(normalizedPath, out notification))
            {
                pendingNotifications.Remove(normalizedPath);
                return notification;
            }
        }

        return null;
    }

    static PendingVideoNotification CreateFallbackNotification(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath)) return null;

        var fileInfo = new FileInfo(localPath);
        var extension = fileInfo.Extension ?? string.Empty;
        if (extension.StartsWith("."))
        {
            extension = extension.Substring(1);
        }

        Log("No in-memory metadata found for {0}; using file metadata fallback for notification.", localPath);
        return new PendingVideoNotification
        {
            ApplicationId = GetApplicationId(Path.GetFileNameWithoutExtension(localPath)),
            LocalPath = localPath,
            FileName = Path.GetFileNameWithoutExtension(localPath),
            VideoFormat = string.IsNullOrWhiteSpace(extension) ? "mp4" : extension,
            FileSizeKb = (long)Math.Ceiling(fileInfo.Length / 1024d),
            DurationSeconds = 0
        };
    }

    static void SendVideoEvidenceNotification(PendingVideoNotification notification)
    {
        if (notification == null) return;
        if (string.IsNullOrWhiteSpace(config.Notification.BaseUrl))
        {
            Log("Video-evidence notification skipped: Notification.BaseUrl is empty.");
            return;
        }

        var url = BuildVideoEvidenceUrl();
        var payload = new Dictionary<string, object>
        {
            { "applicationId", notification.ApplicationId },
            {
                "videoList",
                new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "videoPath", notification.VideoPath },
                        { "videoFormat", notification.VideoFormat },
                        { "fileName", notification.FileName },
                        { "fileSize", notification.FileSizeKb },
                        { "duration", notification.DurationSeconds }
                    }
                }
            }
        };

        var serializer = new JavaScriptSerializer();
        var requestBody = serializer.Serialize(payload);
        var requestBytes = Encoding.UTF8.GetBytes(requestBody);

        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.ContentLength = requestBytes.Length;

            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(requestBytes, 0, requestBytes.Length);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                Log("Video-evidence notification sent for {0} to {1} (HTTP {2}).", notification.FileName, url, (int)response.StatusCode);
            }
        }
        catch (WebException ex)
        {
            var details = ex.Message;
            var response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                details = string.Format("HTTP {0} {1}", (int)response.StatusCode, response.StatusDescription);
                try
                {
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream))
                    {
                        var responseBody = reader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(responseBody))
                        {
                            details += ": " + responseBody;
                        }
                    }
                }
                catch
                {
                    // Ignore response-body read failures and keep the status detail.
                }
                finally
                {
                    response.Close();
                }
            }

            Log("Video-evidence notification failed for {0}: {1}", notification.FileName, details);
        }
        catch (Exception ex)
        {
            Log("Video-evidence notification failed for {0}: {1}", notification.FileName, ex.Message);
        }
    }

    static string BuildVideoEvidenceUrl()
    {
        return BuildVideoEvidenceUrl(config.Notification.BaseUrl);
    }

    internal static string BuildVideoEvidenceUrl(string baseUrl)
    {
        return (baseUrl ?? string.Empty).TrimEnd('/') + VideoEvidenceSavePath;
    }

    static string BuildRemoteVideoDirectory(string remoteFileName)
    {
        return BuildRemoteVideoDirectory(config.Ftp.RemoteDirectory, remoteFileName);
    }

    internal static string BuildRemoteVideoDirectory(string ftpRemoteDirectory, string remoteFileName)
    {
        var segments = new List<string>();
        var remoteRoot = (ftpRemoteDirectory ?? string.Empty).Replace('\\', '/').Trim('/');
        if (!string.IsNullOrWhiteSpace(remoteRoot))
        {
            segments.Add(remoteRoot);
        }

        var directoryName = Path.GetDirectoryName((remoteFileName ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        var remoteDirectory = (directoryName ?? string.Empty).Replace('\\', '/').Trim('/');
        if (!string.IsNullOrWhiteSpace(remoteDirectory))
        {
            segments.Add(remoteDirectory);
        }

        if (segments.Count == 0) return "/";
        return "/" + string.Join("/", segments.ToArray());
    }

    static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    static bool ShouldWriteCurrentFrame()
    {
        if (frameRate <= 0) return true;

        // Some webcams deliver frames faster than the target recording rate; throttle writes by time.
        var now = Stopwatch.GetTimestamp();
        var frameIntervalTicks = Math.Max(1L, Stopwatch.Frequency / Math.Max(1, frameRate));

        if (nextRecordingTick == 0 || now >= nextRecordingTick)
        {
            nextRecordingTick = now + frameIntervalTicks;
            return true;
        }

        return false;
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

    internal static AppConfig LoadConfig(string path)
    {
        var serializer = new JavaScriptSerializer();
        var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        var cfg = new AppConfig();
        cfg.Server.Host = GetString(root, "Server", "Host", "localhost");
        cfg.Server.Port = GetInt(root, "Server", "Port", 5000);
        cfg.Camera.DeviceName = GetString(root, "Camera", "DeviceName", "Integrated Webcam");
        cfg.Camera.Width = GetInt(root, "Camera", "Width", 0);
        cfg.Camera.Height = GetInt(root, "Camera", "Height", 0);
        cfg.Recording.OutputDirectory = GetString(root, "Recording", "OutputDirectory", @"C:\videos");
        cfg.Recording.FrameRate = GetInt(root, "Recording", "FrameRate", 15);
        cfg.FileLogging.FilePath = GetString(root, "FileLogging", "FilePath",
            GetString(root, "Logging", "FilePath", Path.Combine("logs", "server_run.log")));
        cfg.FileLogging.MaxSizeBytes = GetLong(root, "FileLogging", "MaxSizeBytes", 10 * 1024 * 1024);
        cfg.FileLogging.MaxRetainedFiles = GetInt(root, "FileLogging", "MaxRetainedFiles", 5);
        cfg.Ftp.Enabled = GetBool(root, "Ftp", "Enabled", false);
        cfg.Ftp.Host = GetString(root, "Ftp", "Host", string.Empty);
        cfg.Ftp.Port = GetInt(root, "Ftp", "Port", 21);
        cfg.Ftp.Username = GetString(root, "Ftp", "Username", "anonymous");
        cfg.Ftp.Password = GetString(root, "Ftp", "Password", string.Empty);
        cfg.Ftp.RemoteDirectory = GetString(root, "Ftp", "RemoteDirectory", string.Empty);
        cfg.Ftp.UseSsl = GetBool(root, "Ftp", "UseSsl", false);
        cfg.Ftp.CheckIntervalMinutes = GetInt(root, "Ftp", "CheckIntervalMinutes", 5);
        cfg.Ftp.TimeoutSeconds = GetInt(root, "Ftp", "TimeoutSeconds", 15);
        cfg.Notification.BaseUrl = GetString(root, "Notification", "BaseUrl", string.Empty);
        cfg.Security.IgnoreSslCertificateErrors = GetBool(root, "Security", "IgnoreSslCertificateErrors", false);
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

    static long GetLong(Dictionary<string, object> root, string section, string key, long defaultValue)
    {
        var sec = GetSection(root, section);
        if (sec != null && sec.ContainsKey(key) && sec[key] != null)
        {
            long n;
            if (long.TryParse(Convert.ToString(sec[key]), out n)) return n;
        }
        return defaultValue;
    }

    static bool GetBool(Dictionary<string, object> root, string section, string key, bool defaultValue)
    {
        var sec = GetSection(root, section);
        if (sec != null && sec.ContainsKey(key) && sec[key] != null)
        {
            bool b;
            if (bool.TryParse(Convert.ToString(sec[key]), out b)) return b;
        }
        return defaultValue;
    }

    static Dictionary<string, object> GetSection(Dictionary<string, object> root, string section)
    {
        if (root != null && root.ContainsKey(section) && root[section] is Dictionary<string, object>) return (Dictionary<string, object>)root[section];
        return null;
    }

    static void InitializeLogging(FileLoggingConfig loggingConfig)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var configuredPath = loggingConfig == null ? null : loggingConfig.FilePath;
        var path = string.IsNullOrWhiteSpace(configuredPath) ? Path.Combine("logs", "server_run.log") : configuredPath;
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(baseDir, path);
        }

        var logDir = Path.GetDirectoryName(path);
        Directory.CreateDirectory(logDir);
        logFilePath = path;
        logMaxSizeBytes = loggingConfig == null ? 10 * 1024 * 1024 : loggingConfig.MaxSizeBytes;
        logMaxRetainedFiles = loggingConfig == null ? 5 : loggingConfig.MaxRetainedFiles;
    }

    static void ApplySecuritySettings()
    {
        if (config.Security.IgnoreSslCertificateErrors)
        {
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            Log("WARNING: SSL/TLS certificate validation is disabled by configuration.");
            return;
        }

        ServicePointManager.ServerCertificateValidationCallback = null;
    }

    static void Log(string format, params object[] args)
    {
        var message = args == null || args.Length == 0 ? format : string.Format(format, args);
        var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1}", DateTime.Now, message);
        Console.WriteLine(line);

        if (string.IsNullOrWhiteSpace(logFilePath)) return;
        try
        {
            lock (logSync)
            {
                var fileLine = line + Environment.NewLine;
                RotateLogIfNeeded(Encoding.UTF8.GetByteCount(fileLine));
                File.AppendAllText(logFilePath, fileLine);
            }
        }
        catch
        {
            // Avoid crashing the server due to logging failures.
        }
    }

    static void RotateLogIfNeeded(long incomingBytes)
    {
        if (logMaxSizeBytes <= 0) return;
        if (string.IsNullOrWhiteSpace(logFilePath)) return;
        if (!File.Exists(logFilePath)) return;

        var currentSize = new FileInfo(logFilePath).Length;
        if (currentSize + incomingBytes <= logMaxSizeBytes) return;

        if (logMaxRetainedFiles <= 0)
        {
            File.Delete(logFilePath);
            return;
        }

        var oldestPath = GetRotatedLogPath(logMaxRetainedFiles);
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (var i = logMaxRetainedFiles - 1; i >= 1; i--)
        {
            var sourcePath = GetRotatedLogPath(i);
            if (!File.Exists(sourcePath)) continue;

            var targetPath = GetRotatedLogPath(i + 1);
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(sourcePath, targetPath);
        }

        var firstRotatedPath = GetRotatedLogPath(1);
        if (File.Exists(firstRotatedPath))
        {
            File.Delete(firstRotatedPath);
        }
        File.Move(logFilePath, firstRotatedPath);
    }

    static string GetRotatedLogPath(int index)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        var fileName = Path.GetFileNameWithoutExtension(logFilePath);
        var extension = Path.GetExtension(logFilePath);
        return Path.Combine(directory, string.Format("{0}.{1}{2}", fileName, index, extension));
    }
}

public class AppConfig
{
    public ServerConfig Server = new ServerConfig();
    public CameraConfig Camera = new CameraConfig();
    public RecordingConfig Recording = new RecordingConfig();
    public FileLoggingConfig FileLogging = new FileLoggingConfig();
    public FtpConfig Ftp = new FtpConfig();
    public NotificationConfig Notification = new NotificationConfig();
    public SecurityConfig Security = new SecurityConfig();
}

public class ServerConfig { public string Host = "localhost"; public int Port = 5000; }
public class CameraConfig
{
    public string DeviceName = "Integrated Webcam";
    public int Width = 0;
    public int Height = 0;
}
public class RecordingConfig
{
    public string OutputDirectory = @"C:\videos";
    public int FrameRate = 15;
}
public class FileLoggingConfig
{
    public string FilePath = Path.Combine("logs", "server_run.log");
    public long MaxSizeBytes = 10 * 1024 * 1024;
    public int MaxRetainedFiles = 5;
}
public class SecurityConfig { public bool IgnoreSslCertificateErrors = false; }
public class FtpConfig
{
    public bool Enabled = false;
    public string Host = string.Empty;
    public int Port = 21;
    public string Username = "anonymous";
    public string Password = string.Empty;
    public string RemoteDirectory = string.Empty;
    public bool UseSsl = false;
    public int CheckIntervalMinutes = 5;
    public int TimeoutSeconds = 15;
}
public class NotificationConfig { public string BaseUrl = string.Empty; }

public class PendingVideoNotification
{
    public string ApplicationId = string.Empty;
    public string LocalPath = string.Empty;
    public string VideoPath = string.Empty;
    public string VideoFormat = "mp4";
    public string FileName = string.Empty;
    public long FileSizeKb;
    public long DurationSeconds;
}

public class WebcamRecorderService : ServiceBase
{
    public WebcamRecorderService()
    {
        ServiceName = "webcam_recorder";
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        Program.StartHost();
    }

    protected override void OnStop()
    {
        Program.StopHost();
    }

    protected override void OnShutdown()
    {
        Program.StopHost();
        base.OnShutdown();
    }
}
