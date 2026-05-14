using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

public delegate void LogHandler(string format, params object[] args);

public class FtpVideoSync
{
    readonly FtpConfig config;
    readonly string outputDirectory;
    readonly Func<string, bool> isActiveRecordingFile;
    readonly Action<string, string> onFileUploaded;
    readonly LogHandler log;
    CancellationTokenSource cts;
    Task syncTask;

    public FtpVideoSync(FtpConfig config, string outputDirectory, Func<string, bool> isActiveRecordingFile, Action<string, string> onFileUploaded, LogHandler log)
    {
        this.config = config;
        this.outputDirectory = outputDirectory;
        this.isActiveRecordingFile = isActiveRecordingFile;
        this.onFileUploaded = onFileUploaded;
        this.log = log;
    }

    public void Start()
    {
        if (config == null || !config.Enabled)
        {
            Log("FTP sync disabled.");
            return;
        }
        if (string.IsNullOrWhiteSpace(config.Host))
        {
            Log("FTP sync disabled: Ftp.Host is empty.");
            return;
        }

        cts = new CancellationTokenSource();
        syncTask = Task.Run(() => RunAsync(cts.Token));
        Log("FTP sync enabled. Host={0}, Port={1}, RemoteDirectory={2}, UseSsl={3}, LocalDirectory={4}, CheckIntervalMinutes={5}.",
            config.Host,
            config.Port,
            string.IsNullOrWhiteSpace(config.RemoteDirectory) ? "/" : config.RemoteDirectory,
            config.UseSsl,
            outputDirectory,
            config.CheckIntervalMinutes);
    }

    public void Stop()
    {
        if (cts == null) return;

        try
        {
            Log("FTP sync stopping.");
            cts.Cancel();
            if (syncTask != null)
            {
                syncTask.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        finally
        {
            cts.Dispose();
            cts = null;
            syncTask = null;
            Log("FTP sync stopped.");
        }
    }

    async Task RunAsync(CancellationToken token)
    {
        var intervalMinutes = config.CheckIntervalMinutes < 1 ? 5 : config.CheckIntervalMinutes;
        var delay = TimeSpan.FromMinutes(intervalMinutes);

        while (!token.IsCancellationRequested)
        {
            try
            {
                SyncVideos();
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

    void SyncVideos()
    {
        Log("FTP sync cycle starting. LocalDirectory={0}", outputDirectory);

        if (!Directory.Exists(outputDirectory))
        {
            Log("FTP sync: local output directory not found: {0}", outputDirectory);
            return;
        }

        try
        {
            var remoteFiles = ListRemoteFiles();
            Log("FTP sync: server reachable. Remote root contains {0} item(s).", remoteFiles.Count);
        }
        catch (Exception ex)
        {
            Log("FTP sync: server unavailable ({0})", ex.Message);
            return;
        }

        var localFiles = Directory.GetFiles(outputDirectory, "*.mp4", SearchOption.AllDirectories);
        Log("FTP sync: found {0} local mp4 file(s) to inspect.", localFiles.Length);
        var uploadedCount = 0;
        var skippedActiveCount = 0;
        var alreadyRemoteCount = 0;
        var failedCount = 0;
        foreach (var localFile in localFiles)
        {
            var remoteFileName = GetRecordingRelativePath(localFile);
            var localSize = new FileInfo(localFile).Length;
            Log("FTP sync: inspecting {0} ({1} bytes).", remoteFileName, localSize);

            if (isActiveRecordingFile != null && isActiveRecordingFile(localFile))
            {
                skippedActiveCount++;
                Log("FTP sync: skipping active recording {0}.", remoteFileName);
                continue;
            }

            try
            {
                long remoteSize;
                var exists = TryGetRemoteFileSize(remoteFileName, out remoteSize);
                if (exists && remoteSize == localSize)
                {
                    Log("FTP sync: confirmed remote copy for {0}; deleting local file.", remoteFileName);
                    NotifyFileUploaded(localFile, remoteFileName);
                    DeleteLocalFile(localFile, remoteFileName);
                    alreadyRemoteCount++;
                    continue;
                }

                if (exists)
                {
                    Log("FTP sync: remote size mismatch for {0} (local={1}, remote={2}), re-uploading.", remoteFileName, localSize, remoteSize);
                }
                else
                {
                    Log("FTP sync: remote file not found for {0}; uploading.", remoteFileName);
                }
            }
            catch (Exception ex)
            {
                Log("FTP sync: size check failed for {0} ({1}), re-uploading.", remoteFileName, ex.Message);
            }

            try
            {
                UploadFile(localFile, remoteFileName);
                uploadedCount++;
                Log("FTP sync: uploaded {0}", remoteFileName);
                NotifyFileUploaded(localFile, remoteFileName);
                DeleteLocalFile(localFile, remoteFileName);
            }
            catch (Exception ex)
            {
                failedCount++;
                Log("FTP sync: failed uploading {0} ({1})", remoteFileName, ex.Message);
            }
        }

        Log("FTP sync cycle complete. Uploaded={0}, AlreadyRemote={1}, SkippedActive={2}, Failed={3}, Inspected={4}.",
            uploadedCount,
            alreadyRemoteCount,
            skippedActiveCount,
            failedCount,
            localFiles.Length);
    }

    bool TryGetRemoteFileSize(string remoteFileName, out long size)
    {
        size = -1;
        try
        {
            Log("FTP sync: checking remote size for {0}.", remoteFileName);
            var request = CreateRequest(remoteFileName, WebRequestMethods.Ftp.GetFileSize);
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
                Log("FTP sync: remote size for {0} is {1} bytes.", remoteFileName, size);
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
                        Log("FTP sync: remote file unavailable for {0} ({1}).", remoteFileName, ftpResponse.StatusCode);
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

    string GetRecordingRelativePath(string localPath)
    {
        var root = Path.GetFullPath(outputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(localPath);

        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(root.Length).Replace('\\', '/');
        }

        return Path.GetFileName(localPath);
    }

    HashSet<string> ListRemoteFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Log("FTP sync: listing remote root.");
        var request = CreateRequest(string.Empty, WebRequestMethods.Ftp.ListDirectory);
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

    void UploadFile(string localPath, string remoteFileName)
    {
        EnsureRemoteDirectories(remoteFileName);
        var request = CreateRequest(remoteFileName, WebRequestMethods.Ftp.UploadFile);
        var bytes = File.ReadAllBytes(localPath);
        Log("FTP sync: uploading {0} ({1} bytes).", remoteFileName, bytes.Length);
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

            Log("FTP sync: upload response for {0}: {1} {2}", remoteFileName, response.StatusCode, response.StatusDescription);
        }
    }

    void EnsureRemoteDirectories(string remoteFileName)
    {
        var normalized = (remoteFileName ?? string.Empty).Replace('\\', '/').Trim('/');
        var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return;

        var currentPath = string.Empty;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : currentPath + "/" + parts[i];
            try
            {
                Log("FTP sync: ensuring remote directory {0}.", currentPath);
                var request = CreateRequest(currentPath, WebRequestMethods.Ftp.MakeDirectory);
                using (var response = (FtpWebResponse)request.GetResponse())
                {
                    Log("FTP sync: remote directory {0} mkdir response: {1} {2}", currentPath, response.StatusCode, response.StatusDescription);
                }
            }
            catch (Exception ex)
            {
                Log("FTP sync: remote directory {0} not created or already exists ({1}).", currentPath, ex.Message);
            }
        }
    }

    FtpWebRequest CreateRequest(string remoteFileName, string method)
    {
        var uri = BuildUri(remoteFileName);
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = method;
        request.Credentials = new NetworkCredential(config.Username, config.Password);
        request.EnableSsl = config.UseSsl;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Timeout = config.TimeoutSeconds * 1000;
        request.ReadWriteTimeout = config.TimeoutSeconds * 1000;
        return request;
    }

    string BuildUri(string remoteFileName)
    {
        var basePath = (config.RemoteDirectory ?? string.Empty).Trim();
        basePath = basePath.Trim('/');
        var cleanFileName = (remoteFileName ?? string.Empty).Trim().Trim('/');

        var uri = string.Format("ftp://{0}:{1}", config.Host, config.Port);
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

    void Log(string format, params object[] args)
    {
        if (log != null)
        {
            log(format, args);
        }
    }

    void NotifyFileUploaded(string localPath, string remoteFileName)
    {
        if (onFileUploaded == null) return;

        try
        {
            onFileUploaded(localPath, remoteFileName);
        }
        catch (Exception ex)
        {
            Log("FTP sync: upload callback failed for {0} ({1})", remoteFileName, ex.Message);
        }
    }

    void DeleteLocalFile(string localPath, string remoteFileName)
    {
        try
        {
            if (!File.Exists(localPath)) return;

            File.Delete(localPath);
            Log("FTP sync: deleted local file {0} after remote confirmation.", remoteFileName);
        }
        catch (Exception ex)
        {
            Log("FTP sync: failed deleting local file {0} ({1})", localPath, ex.Message);
        }
    }
}
