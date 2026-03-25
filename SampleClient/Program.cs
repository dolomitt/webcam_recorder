using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading.Tasks;

// Usage:
//   dotnet run --project SampleClient/SampleClient.csproj start my-recording-id
//   dotnet run --project SampleClient/SampleClient.csproj stop

public class SampleClientProgram
{
    public static void Main(string[] args)
    {
        RunAsync(args).GetAwaiter().GetResult();
    }

    static async Task RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        var command = args[0].ToLowerInvariant();
        var baseUrl = "http://localhost:5001";

        using (var http = new HttpClient { BaseAddress = new Uri(baseUrl) })
        {
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
                        var serializer = new JavaScriptSerializer();
                        var startPayload = serializer.Serialize(new Dictionary<string, object> { { "id", id } });
                        using (var startContent = new StringContent(startPayload, Encoding.UTF8, "application/json"))
                        using (var startResponse = await http.PostAsync("/start", startContent))
                        {
                            var body = await startResponse.Content.ReadAsStringAsync();
                            Console.WriteLine("HTTP {0} {1}", (int)startResponse.StatusCode, startResponse.ReasonPhrase);
                            Console.WriteLine(body);

                            if (startResponse.IsSuccessStatusCode)
                            {
                                try
                                {
                                    var responseObj = serializer.Deserialize<Dictionary<string, object>>(body);
                                    if (responseObj != null)
                                    {
                                        object stream;
                                        if (responseObj.TryGetValue("stream", out stream))
                                        {
                                            Console.WriteLine();
                                            Console.WriteLine("Live preview: {0}{1}", baseUrl, Convert.ToString(stream));
                                        }

                                        object download;
                                        if (responseObj.TryGetValue("download", out download))
                                        {
                                            Console.WriteLine("Recording file: {0}{1}", baseUrl, Convert.ToString(download));
                                        }
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                        break;

                    case "stop":
                        using (var stopResponse = await http.PostAsync("/stop", content: null))
                        {
                            var body = await stopResponse.Content.ReadAsStringAsync();
                            Console.WriteLine("HTTP {0} {1}", (int)stopResponse.StatusCode, stopResponse.ReasonPhrase);
                            Console.WriteLine(body);
                        }
                        break;

                    default:
                        Console.WriteLine("Unknown command: {0}", command);
                        PrintUsage();
                        break;
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine("Request failed. Is the recorder server running on http://localhost:5001?");
                Console.WriteLine(ex.Message);
            }
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("Sample REST Client for webcam_recorder");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  start <id>   Calls POST /start and prints stream/file URLs");
        Console.WriteLine("  stop         Calls POST /stop");
    }
}