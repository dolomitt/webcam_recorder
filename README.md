# webcam_recorder (C#)

Simple command-line HTTP webcam service in C# with live preview and MP4 recording.

## Features

- `POST /start` to start recording webcam video
- `POST /stop` to stop recording
- `GET /stream/{id}` to view live MJPEG preview while recording
- `GET /file/{id}` to download/open saved MP4 recording
- JSON config for:
  - server host/port
  - camera device name
  - output directory
- Sample client app included (`SampleClient`)

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
    "DeviceName": "Integrated Webcam"
  },
  "Recording": {
    "OutputDirectory": "C:\\videos"
  }
}
```

### Notes

- `Camera.DeviceName` should match your DirectShow camera name. If not found, the first available camera is used.
- If a port is busy, change `Server.Port`.
- Recordings are saved to `Recording.OutputDirectory` with `.mp4` extension.

---

## Build

```bat
dotnet build webcam_recorder.sln -nologo
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

Response:

```json
{ "status": "stopped" }
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

## Troubleshooting

- **Port conflict**
  - Error: `Failed to listen on prefix... conflicts with an existing registration`
  - Fix: change `Server.Port` in `appsettings.json`.

- **No webcam found**
  - Ensure a camera is connected and recognized by Windows.
