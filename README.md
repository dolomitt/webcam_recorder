# webcam_recorder (C#)

Simple command-line HTTP webcam service in C# with live preview and MP4 recording.

## Features

- `POST /start` to start recording webcam video
- `POST /stop` to stop recording (returns an error if nothing is recording)
- `GET /stream/{id}` to view live MJPEG preview while recording
- `GET /file/{id}` to download/open saved MP4 recording
- Configurable file logging path via `FileLogging.FilePath`
- Optional FTP upload of completed MP4 files
- Optional post-upload callback to an external web server for video evidence registration
- JSON config for:
  - server host/port
  - camera device name
  - output directory
  - logging file path
  - FTP upload settings
  - callback base URL
- Sample client app included (`SampleClient`)
- MSTest unit tests included (`webcam_recorder.Tests`)

---

## Requirements

- Windows
- .NET SDK (current solution targets `net452`)
- .NET Framework 4.5.2 targeting pack/runtime on the machine
- A webcam available on Windows

If `dotnet` is not in PATH, use:

`C:\Progra~1\dotnet\dotnet.exe`

---

## Installation

### 1) Get the source code

Using Git:

```bat
git clone https://github.com/dolomitt/webcam_recorder.git
cd webcam_recorder
```

Or download the project as a ZIP and extract it, then open a terminal in the extracted `webcam_recorder` folder.

### 2) Restore dependencies

```bat
dotnet restore webcam_recorder.sln
```

### 3) Build the solution

```bat
dotnet build webcam_recorder.sln -c Debug
```

### 4) Configure the app

Edit `appsettings.json` and set:

- `Server.Host` and `Server.Port`
- `Camera.DeviceName` (your webcam device name)
- `Recording.OutputDirectory` (where MP4 files are saved)
- `FileLogging.FilePath` (where the application log file is written)
- `Ftp.*` if you want automatic FTP upload
- `Notification.BaseUrl` if you want a callback after FTP upload

### 5) Run the server

```bat
dotnet run --project webcam_recorder.csproj
```

Server should start on the configured URL (default: `http://localhost:5001/`).

### 6) (Optional) Validate with sample client

Start recording:

```bat
dotnet run --project SampleClient\SampleClient.csproj -- start test123
```

Stop recording:

```bat
dotnet run --project SampleClient\SampleClient.csproj -- stop
```

Then open `http://localhost:5001/file/test123` to verify the output file is available.

---

## Project Structure

- `Program.cs` - recorder server
- `webcam_recorder.csproj` - server project file
- `appsettings.json` - server configuration
- `SampleClient/Program.cs` - sample REST client
- `SampleClient/SampleClient.csproj` - sample client project file

---

## Configuration

Edit `appsettings.json`:

```json
{
  "Server": {
    "Host": "localhost",
    "Port": 5001
  },
  "Camera": {
    "DeviceName": "Integrated Webcam",
    "Width": 1280,
    "Height": 720
  },
  "Recording": {
    "OutputDirectory": "C:\\videos",
    "FrameRate": 15
  },
  "FileLogging": {
    "FilePath": "logs\\server_run.log"
  },
  "Notification": {
    "BaseUrl": "http://localhost:8080"
  },
  "Ftp": {
    "Enabled": false,
    "Host": "127.0.0.1",
    "Port": 21,
    "Username": "anonymous",
    "Password": "",
    "RemoteDirectory": "videos",
    "UseSsl": false,
    "CheckIntervalMinutes": 5,
    "TimeoutSeconds": 15
  }
}
```

### Notes

- `Camera.DeviceName` should match your DirectShow camera name. If not found, the first available camera is used.
- `Camera.Width` and `Camera.Height` are optional. If the exact resolution is unavailable, the camera default is used.
- If a port is busy, change `Server.Port`.
- Recordings are saved to `Recording.OutputDirectory` with `.mp4` extension.
- `FileLogging.FilePath` may be relative to the application folder or an absolute path.
- The application still accepts legacy `Logging.FilePath`, but `FileLogging.FilePath` is the preferred setting.
- `Notification.BaseUrl` is only used after a file has been uploaded to FTP successfully.
- The callback URL is built as:
  - `{Notification.BaseUrl}/request-register/videoEvidence/save`
- `Notification.BaseUrl` should therefore contain only the server/base URL, not the full API path.

### FTP upload and notification flow

When FTP upload is enabled:

1. A recording is created locally in `Recording.OutputDirectory`
2. `POST /stop` stops the recording and queues notification metadata
3. The background FTP sync uploads the file
4. After a successful FTP upload, the app sends a callback to:

`/request-register/videoEvidence/save`

Payload format:

```json
{
  "applicationId": "060065912",
  "videoList": [
    {
      "videoPath": "/videos/060065912",
      "videoFormat": "mp4",
      "fileName": "060065912_video001",
      "fileSize": 500,
      "duration": 800
    }
  ]
}
```

Notes:

- `applicationId` is derived from the recording ID prefix before `_`
- `videoPath` is the final FTP directory path
- `fileName` is sent without extension
- `fileSize` is sent in KB
- `duration` is sent in seconds
- If the callback fails, the application logs the failure and continues

---

## Build

```bat
dotnet build webcam_recorder.sln -nologo
```

### Packaging options (important)

- **ZIP + native Windows service (recommended for your target machine flow):** uses `scripts\build-zip.ps1` and **does not require ISCC/Inno Setup**.
- **EXE installer (optional):** uses `scripts\build-installer.ps1` and **does require ISCC**.

## Build Installer (EXE, optional)

This repository includes an Inno Setup installer definition at `installer\webcam_recorder.iss` and a helper script:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Version 1.0.0
```

Output installer location:

`installer\dist\webcam_recorder-setup-<version>.exe`

Notes:

- Requires Inno Setup 6 (`ISCC.exe`) installed on the build machine.
- The installer includes the built `net452` app files and `appsettings.json`.
- During install, a .NET Framework 4.5.2+ check is enforced.

---

## Build Portable ZIP (for target machine)

If you want a copyable deployment package (instead of the EXE installer), build a ZIP that contains:

- main app binaries
- `SampleClient` binaries (ready-to-run)
- default `appsettings.json`
- service helper scripts

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-zip.ps1 -Version 1.0.0
```

Output ZIP location:

`installer\dist\webcam_recorder-ntservice-<version>.zip`

Inside the ZIP, sample client is located under:

`SampleClient\SampleClient.exe`

---

## Install as Windows NT Service (native)

This build supports running directly as a native Windows service (no NSSM required).

### 1) Prepare target folder

1. Copy ZIP to target machine.
2. Extract to a folder, for example:

`C:\Apps\webcam_recorder`

### 2) Configure appsettings

Edit `appsettings.json` in the extracted folder.

Set at least:

- `Server.Host` and `Server.Port`
- `Camera.DeviceName`
- `Recording.OutputDirectory`
- `FileLogging.FilePath`
- `Ftp.*` and `Notification.BaseUrl` if you want post-upload notification support

### 3) Install service

Run an **elevated PowerShell** and execute:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-ntservice.ps1 `
  -InstallDir "C:\Apps\webcam_recorder" `
  -ServiceName "webcam_recorder" `
  -DisplayName "Webcam Recorder" `
  -Description "HTTP webcam recorder service"
```

The script creates an auto-start service using:

`"C:\Apps\webcam_recorder\webcam_recorder.exe" --service`

### 4) Start / verify

```powershell
sc.exe start webcam_recorder
sc.exe query webcam_recorder
```

### 5) Remove service (if needed)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-ntservice.ps1 `
  -ServiceName "webcam_recorder"
```

---

## Run Server

```bat
dotnet run --project webcam_recorder.csproj
```

Server starts on the configured URL, e.g.:

`http://localhost:5001/`

---

## API

### Start recording

**POST** `/start`

Body:

```json
{ "id": "my-recording" }
```

Success response (example):

```json
{
  "file": "C:\\videos\\my-recording.mp4",
  "stream": "/stream/my-recording",
  "download": "/file/my-recording"
}
```

### Stop recording

**POST** `/stop`

Success response:

```json
{ "status": "stopped" }
```

If no recording is currently active, the server returns HTTP 500, for example:

```json
{ "error": "No recording is currently in progress" }
```

### Stream live preview

**GET** `/stream/{id}`

Example:

`http://localhost:5001/stream/my-recording`

Returns a live MJPEG stream while that recording ID is active.

### Download recording

**GET** `/file/{id}`

Example:

`http://localhost:5001/file/my-recording`

Returns the saved MP4 file.

---

## Sample Client

Start:

```bat
dotnet run --project SampleClient\SampleClient.csproj -- start test123
```

Stop:

```bat
dotnet run --project SampleClient\SampleClient.csproj -- stop
```

---

## Tests

Run the unit tests with:

```bat
dotnet test webcam_recorder.sln
```

Current unit tests cover:

- config loading for `FileLogging.FilePath`
- fallback support for legacy `Logging.FilePath`
- video evidence callback URL generation
- FTP remote video directory generation
- application ID extraction from recording IDs

---

## Troubleshooting

- **Port conflict**
  - Error: `Failed to listen on prefix... conflicts with an existing registration`
  - Fix: change `Server.Port` in `appsettings.json`.

- **No webcam found**
  - Ensure a camera is connected and recognized by Windows.

- **`/stop` returns an error**
  - Error: `No recording is currently in progress`
  - Fix: call `POST /start` first, then call `POST /stop` only while a recording is active.

- **Callback not being sent**
  - Confirm `Ftp.Enabled = true`
  - Confirm FTP upload succeeds
  - Confirm `Notification.BaseUrl` is set
  - Check the log file configured by `FileLogging.FilePath`
