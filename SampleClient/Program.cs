using System.Text;
using System.Text.Json;

// Usage:
//   dotnet run --project SampleClient/SampleClient.csproj start my-recording-id
//   dotnet run --project SampleClient/SampleClient.csproj stop

if (args.Length == 0)
{
    PrintUsage();
    return;
}

var command = args[0].ToLowerInvariant();
var baseUrl = "http://localhost:5000";

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

try
{
    switch (command)
    {
        case "start":
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.WriteLine("Error: missing id for start command.");
                PrintUsage();
                return;
            }

            var id = args[1];
            var startPayload = JsonSerializer.Serialize(new { id });
            using (var startContent = new StringContent(startPayload, Encoding.UTF8, "application/json"))
            using (var startResponse = await http.PostAsync("/start", startContent))
            {
                var body = await startResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"HTTP {(int)startResponse.StatusCode} {startResponse.ReasonPhrase}");
                Console.WriteLine(body);
            }
            break;

        case "stop":
            using (var stopResponse = await http.PostAsync("/stop", content: null))
            {
                var body = await stopResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"HTTP {(int)stopResponse.StatusCode} {stopResponse.ReasonPhrase}");
                Console.WriteLine(body);
            }
            break;

        default:
            Console.WriteLine($"Unknown command: {command}");
            PrintUsage();
            break;
    }
}
catch (HttpRequestException ex)
{
    Console.WriteLine("Request failed. Is the recorder server running on http://localhost:5000?");
    Console.WriteLine(ex.Message);
}

static void PrintUsage()
{
    Console.WriteLine("Sample REST Client for webcam_recorder");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  start <id>   Calls POST /start with JSON body { \"id\": \"<id>\" }");
    Console.WriteLine("  stop         Calls POST /stop");
}
