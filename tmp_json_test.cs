using System;
using System.IO;
using System.Text.Json;
public class CollectorOptions {
  public string ApiBaseUrl { get; set; } = string.Empty;
  public string SharedPath { get; set; } = string.Empty;
  public bool WatchMode { get; set; }
  public bool PromptForCredential { get; set; }
}
public static class TestLoader {
  public static void Run(string path) {
    var json = File.ReadAllText(path);
    var options = JsonSerializer.Deserialize<CollectorOptions>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, WriteIndented = true });
    Console.WriteLine($"ApiBaseUrl=[{options?.ApiBaseUrl}]");
    Console.WriteLine($"SharedPath=[{options?.SharedPath}]");
    Console.WriteLine($"WatchMode=[{options?.WatchMode}]");
    Console.WriteLine($"Prompt=[{options?.PromptForCredential}]");
  }
}
