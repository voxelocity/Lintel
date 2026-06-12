using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lintel.Services;

internal static class Http
{
    public static readonly HttpClient Client = Make();
    private static HttpClient Make()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.Add("User-Agent", "Lintel/1.1 (+https://github.com/voxelocity/Lintel)");
        return c;
    }
}

// ---- weather (wttr.in, no API key) ----

public sealed class WeatherInfo
{
    public int TempC, FeelsC;
    public string Desc = "";
    public string Location = "";
    public int Humidity;
    public double WindKph;
    public List<(string day, int min, int max, string desc)> Forecast = new();
    public bool Ok;
}

public static class Weather
{
    public static Task<WeatherInfo> GetAsync(string location) => Task.Run(async () =>
    {
        var w = new WeatherInfo();
        try
        {
            string loc = Uri.EscapeDataString(location?.Trim() ?? "");
            string json = await Http.Client.GetStringAsync($"https://wttr.in/{loc}?format=j1");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cur = root.GetProperty("current_condition")[0];
            w.TempC = int.Parse(cur.GetProperty("temp_C").GetString()!);
            w.FeelsC = int.Parse(cur.GetProperty("FeelsLikeC").GetString()!);
            w.Desc = cur.GetProperty("weatherDesc")[0].GetProperty("value").GetString() ?? "";
            w.Humidity = int.Parse(cur.GetProperty("humidity").GetString()!);
            w.WindKph = double.Parse(cur.GetProperty("windspeedKmph").GetString()!);
            if (root.TryGetProperty("nearest_area", out var areas) && areas.GetArrayLength() > 0)
                w.Location = areas[0].GetProperty("areaName")[0].GetProperty("value").GetString() ?? "";
            foreach (var day in root.GetProperty("weather").EnumerateArray())
            {
                var date = day.GetProperty("date").GetString() ?? "";
                string dow = DateTime.TryParse(date, out var dt) ? dt.ToString("ddd") : date;
                w.Forecast.Add((dow,
                    int.Parse(day.GetProperty("mintempC").GetString()!),
                    int.Parse(day.GetProperty("maxtempC").GetString()!),
                    day.GetProperty("hourly")[4].GetProperty("weatherDesc")[0].GetProperty("value").GetString() ?? ""));
            }
            w.Ok = true;
        }
        catch { }
        return w;
    });
}

// ---- stocks & crypto (Yahoo Finance chart endpoint, no key) ----

public sealed record Quote(string Symbol, double Price, double ChangePct, bool Ok);

public static class Stocks
{
    public static Task<List<Quote>> GetAsync(IEnumerable<string> symbols) => Task.Run(async () =>
    {
        var list = new List<Quote>();
        foreach (var raw in symbols)
        {
            var sym = raw.Trim();
            if (sym.Length == 0) continue;
            try
            {
                string json = await Http.Client.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(sym)}?interval=1d&range=2d");
                using var doc = JsonDocument.Parse(json);
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                double price = meta.GetProperty("regularMarketPrice").GetDouble();
                double prev = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble()
                            : meta.TryGetProperty("previousClose", out var p2) ? p2.GetDouble() : price;
                double pct = prev > 0 ? (price - prev) / prev * 100 : 0;
                list.Add(new Quote(sym.ToUpperInvariant(), price, pct, true));
            }
            catch { list.Add(new Quote(sym.ToUpperInvariant(), 0, 0, false)); }
        }
        return list;
    });
}
