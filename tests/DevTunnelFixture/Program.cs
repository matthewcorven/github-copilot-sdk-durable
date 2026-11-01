using System.Text.Json;

var record = Environment.GetEnvironmentVariable("DURABLE_TUNNEL_FIXTURE_RECORD")
    ?? throw new InvalidOperationException("This controlled test executable requires a record path.");
await File.WriteAllTextAsync(record, JsonSerializer.Serialize(args));
if (Environment.GetEnvironmentVariable("DURABLE_TUNNEL_FIXTURE_MODE") == "fail")
{
    Console.Error.WriteLine("controlled tunnel failure");
    return 7;
}

var output = Environment.GetEnvironmentVariable("DURABLE_TUNNEL_FIXTURE_MODE") == "stderr"
    ? Console.Error : Console.Out;
await output.WriteLineAsync("Connect via https://controlled-123.devtunnels.ms:443/");
await output.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
