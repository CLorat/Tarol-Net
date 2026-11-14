static class J2 { public static void Run() {
  foreach (var json in new[] { "{\"Status\":\"Reopened\"}", "{\"Status\":\"Closed\"}", "{\"Status\":\"closed\"}", "{\"Status\":\"Settled\"}" })
    try { Console.WriteLine(json + " -> " + System.Text.Json.JsonSerializer.Deserialize(json, Ctx.Default.Holder)); }
    catch (Exception e) { Console.WriteLine(json + " -> " + e.GetType().Name); }
}}
