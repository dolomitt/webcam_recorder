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
    static int frameRate = 30;
    static CancellationTokenSource ftpSyncCts;
    static Task ftpSyncTask;
    static readonly object logSync = new object();
    static string logFilePath;
    static CancellationTokenSource serverCts;
    static Task listenerLoopTask;
    static readonly object lifecycleSync = new object();
    static bool hostStarted;

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

            InitializeLogging();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(baseDir, "appsettings.json");
            config = LoadConfig(configPath);
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
            StopCapture();

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

    static void StartFtpSyncLoop()
    {
        if (config.Ftp == null || !config.Ftp.Enabled)
        {
            Log("FTP sync disabled.");
            return;
        }
        if (string.IsNullOrWhiteSpace(config.Ftp.Host))
        {
            Log("FTP sync disabled: Ftp.Host is empty.");
            return;
        }

        ftpSyncCts = new CancellationTokenSource();
        ftpSyncTask = Task.Run(() => RunFtpSyncLoopAsync(ftpSyncCts.Token));
        Log("FTP sync enabled. Checking every {0} minute(s).", config.Ftp.CheckIntervalMinutes);
    }

    static void StopFtpSyncLoop()
    {
        if (ftpSyncCts == null) return;

        try
        {
            ftpSyncCts.Cancel();
            if (ftpSyncTask != null)
            {
                ftpSyncTask.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        finally
        {
            ftpSyncCts.Dispose();
            ftpSyncCts = null;
            ftpSyncTask = null;
        }
    }

    static async Task RunFtpSyncLoopAsync(CancellationToken token)
    {
        var intervalMinutes = config.Ftp.CheckIntervalMinutes < 1 ? 5 : config.Ftp.CheckIntervalMinutes;
        var delay = TimeSpan.FromMinutes(intervalMinutes);

        while (!token.IsCancellationRequested)
        {
            try
            {
                SyncVideosToFtp();
            }
            catch (Exception ex)
            {
                Log("FTP sync cycle failed: {0}", ex.Message);
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    static void SyncVideosToFtp()
    {
        if (!Directory.Exists(config.Recording.OutputDirectory))
        {
            Log("FTP sync: local output directory not found: {0}", config.Recording.OutputDirectory);
            return;
        }

        HashSet<string> remoteFiles;
        try
        {
            remoteFiles = ListRemoteFiles();
        }
        catch (Exception ex)
        {
            Log("FTP sync: server unavailable ({0})", ex.Message);
            return;
        }

        var localFiles = Directory.GetFiles(config.Recording.OutputDirectory, "*.mp4");
        var uploadedCount = 0;
        foreach (var localFile in localFiles)
        {
            if (IsActiveRecordingFile(localFile)) continue;

            var fileName = Path.GetFileName(localFile);
            if (remoteFiles.Contains(fileName))
            {
                try
                {
                    long remoteSize;
                    var exists = TryGetRemoteFileSize(fileName, out remoteSize);
                    var localSize = new FileInfo(localFile).Length;
                    if (exists && remoteSize == localSize)
                    {
                        continue;
                    }

                    Log("FTP sync: remote size mismatch for {0} (local={1}, remote={2}), re-uploading.", fileName, localSize, remoteSize);
                }
                catch (Exception ex)
                {
                    Log("FTP sync: size check failed for {0} ({1}), re-uploading.", fileName, ex.Message);
                }
            }

            try
            {
                UploadFileToFtp(localFile, fileName);
                uploadedCount++;
                Log("FTP sync: uploaded {0}", fileName);
            }
            catch (Exception ex)
            {
                Log("FTP sync: failed uploading {0} ({1})", fileName, ex.Message);
            }
        }

        Log("FTP sync cycle complete. Uploaded {0} file(s).", uploadedCount);
    }

    static bool TryGetRemoteFileSize(string remoteFileName, out long size)
    {
        size = -1;
        try
        {
            var request = CreateFtpRequest(remoteFileName, WebRequestMethods.Ftp.GetFileSize);
            using (var response = (FtpWebResponse)request.GetResponse())
            {
                size = response.ContentLength;
                if (size < 0)
                {
                    var status = response.StatusDescription ?? string.Empty;
                    var parts = status.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        long parsed;
                        if (long.TryParse(parts[1], out parsed))
                        {
                            size = parsed;
                        }
                    }
                }
                return true;
            }
        }
        catch (WebException ex)
        {
            var ftpResponse = ex.Response as FtpWebResponse;
            if (ftpResponse != null)
            {
                try
                {
                    if (ftpResponse.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable ||
                        ftpResponse.StatusCode == FtpStatusCode.ActionNotTakenFilenameNotAllowed)
                    {
                        return false;
                    }
                }
                finally
                {
                    ftpResponse.Close();
                }
            }

            throw;
        }
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

    static HashSet<string> ListRemoteFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var request = CreateFtpRequest(string.Empty, WebRequestMethods.Ftp.ListDirectory);
        using (var response = (FtpWebResponse)request.GetResponse())
        using (var stream = response.GetResponseStream())
        using (var reader = new StreamReader(stream))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length > 0)
                {
                    files.Add(line);
                }
            }
        }
        return files;
    }

    static void UploadFileToFtp(string localPath, string remoteFileName)
    {
        var request = CreateFtpRequest(remoteFileName, WebRequestMethods.Ftp.UploadFile);
        var bytes = File.ReadAllBytes(localPath);
        request.ContentLength = bytes.Length;
        using (var requestStream = request.GetRequestStream())
        {
            requestStream.Write(bytes, 0, bytes.Length);
        }
        using (var response = (FtpWebResponse)request.GetResponse())
        {
            if ((int)response.StatusCode >= 400)
            {
                throw new InvalidOperationException(response.StatusDescription);
            }
        }
    }

    static FtpWebRequest CreateFtpRequest(string remoteFileName, string method)
    {
        var uri = BuildFtpUri(remoteFileName);
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = method;
        request.Credentials = new NetworkCredential(config.Ftp.Username, config.Ftp.Password);
        request.EnableSsl = config.Ftp.UseSsl;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Timeout = config.Ftp.TimeoutSeconds * 1000;
        request.ReadWriteTimeout = config.Ftp.TimeoutSeconds * 1000;
        return request;
    }

    static string BuildFtpUri(string remoteFileName)
    {
        var basePath = (config.Ftp.RemoteDirectory ?? string.Empty).Trim();
        basePath = basePath.Trim('/');
        var cleanFileName = (remoteFileName ?? string.Empty).Trim().Trim('/');

        var uri = string.Format("ftp://{0}:{1}", config.Ftp.Host, config.Ftp.Port);
        if (!string.IsNullOrEmpty(basePath))
        {
            uri += "/" + basePath;
        }
        if (!string.IsNullOrEmpty(cleanFileName))
        {
            uri += "/" + cleanFileName;
        }
        return uri;
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
        cfg.Ftp.Enabled = GetBool(root, "Ftp", "Enabled", false);
        cfg.Ftp.Host = GetString(root, "Ftp", "Host", string.Empty);
        cfg.Ftp.Port = GetInt(root, "Ftp", "Port", 21);
        cfg.Ftp.Username = GetString(root, "Ftp", "Username", "anonymous");
        cfg.Ftp.Password = GetString(root, "Ftp", "Password", string.Empty);
        cfg.Ftp.RemoteDirectory = GetString(root, "Ftp", "RemoteDirectory", string.Empty);
        cfg.Ftp.UseSsl = GetBool(root, "Ftp", "UseSsl", false);
        cfg.Ftp.CheckIntervalMinutes = GetInt(root, "Ftp", "CheckIntervalMinutes", 5);
        cfg.Ftp.TimeoutSeconds = GetInt(root, "Ftp", "TimeoutSeconds", 15);
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

    static void InitializeLogging()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);
        logFilePath = Path.Combine(logDir, "server_run.log");
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
                File.AppendAllText(logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Avoid crashing the server due to logging failures.
        }
    }
}

public class AppConfig
{
    public ServerConfig Server = new ServerConfig();
    public CameraConfig Camera = new CameraConfig();
    public RecordingConfig Recording = new RecordingConfig();
    public FtpConfig Ftp = new FtpConfig();
}

public class ServerConfig { public string Host = "localhost"; public int Port = 5000; }
public class CameraConfig { public string DeviceName = "Integrated Webcam"; }
public class RecordingConfig { public string OutputDirectory = @"C:\videos"; }
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
