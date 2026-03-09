# webcam_recorder (C#)

Simple command-line HTTP recorder service in C# that wraps `ffmpeg`.

## Features

- `POST /start` to start recording webcam video
- `POST /stop` to stop recording
- JSON config for:
  - camera device name
  - server host/port
  - capture resolution
  - output format/extension
  - ffmpeg executable path
- Sample client app included (`SampleClient`)

---

## Requirements

- Windows
- .NET SDK (tested with .NET 10)
- ffmpeg installed (or unpacked) locally

If `dotnet` is not in PATH, use:

`C:\Progra~1\dotnet\dotnet.exe`

---

## Project Structure

- `Program.cs` - recorder server
- `webcam_recorder.csproj` - server project file
- `appsettings.json` - server configuration
- `SampleClient/Program.cs` - sample REST client

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
    "InputFormat": "dshow",
    "DeviceName": "Integrated Webcam",
    "Resolution": "1280x720"
  },
  "Recording": {
    "OutputDirectory": "C:\\videos",
    "FileExtension": "mp4",
    "OutputFormat": "mp4"
  },
  "Ffmpeg": {
    "ExecutablePath": "C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe"
  }
}
```

### Notes

- `DeviceName` must match your DirectShow camera name.
- If a port is busy, change `Server.Port`.
- Recordings are saved to `Recording.OutputDirectory`.

---

## Build

```bat
C:\Progra~1\dotnet\dotnet.exe build webcam_recorder.csproj -nologo
C:\Progra~1\dotnet\dotnet.exe build SampleClient\SampleClient.csproj -nologo
```

---

## Run Server

```bat
C:\Progra~1\dotnet\dotnet.exe run --project webcam_recorder.csproj
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

Response:

```json
{ "file": "C:\\videos\\my-recording.mp4" }
```

### Stop recording

**POST** `/stop`

Response:

```json
{ "status": "stopped" }
```

---

## Sample Client

Start:

```bat
C:\Progra~1\dotnet\dotnet.exe run --project SampleClient\SampleClient.csproj -- start test123
```

Stop:

```bat
C:\Progra~1\dotnet\dotnet.exe run --project SampleClient\SampleClient.csproj -- stop
```

---

## Troubleshooting

- **Port conflict**
  - Error: `Failed to listen on prefix... conflicts with an existing registration`
  - Fix: change `Server.Port` in `appsettings.json`.

- **ffmpeg not found**
  - Set full path in `Ffmpeg.ExecutablePath`.

- **Unplayable mp4 (`moov atom not found`)**
  - This happens if ffmpeg is terminated abruptly.
  - Use `/stop` to stop gracefully so container metadata is finalized.
