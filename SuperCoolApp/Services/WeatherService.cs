using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SuperCoolApp.Services
{
    public record WeatherForecast
    {
        // existing
        public string? Timezone { get; init; }
        public DateTime[]? HourlyTime { get; init; }
        public double[]? HourlyTemperature2m { get; init; }
        public double? CurrentTemperature { get; init; }
        public double? CurrentWindSpeed10m { get; init; }
        public double? CurrentPrecipitation { get; init; }
        public double? CurrentRain { get; init; }
        public double? CurrentShowers { get; init; }
        public double? CurrentSnowfall { get; init; }
        public JsonElement? Raw { get; init; }

        // NEW: daily
        public DateTime[]? DailyTime { get; init; }                // local dates (timezone=auto)
        public int[]?      DailyWeatherCode { get; init; }         // WMO codes
        public double[]?   DailyPrecipitationSum { get; init; }    // mm
        public double[]?   DailySnowfallSum { get; init; }         // cm
    }


    public class WeatherService
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public WeatherService(HttpClient? http = null, string baseUrl = "https://api.open-meteo.com/v1/forecast")
        {
            _http = http ?? new HttpClient();
            _baseUrl = baseUrl?.TrimEnd('/') ?? "https://api.open-meteo.com/v1/forecast";
        }

        public async Task<WeatherForecast?> GetForecastAsync(
            double latitude,
            double longitude,
            int forecastDays = 3,
            CancellationToken ct = default)
        {
            // Request a small set of hourly variables + explicit "current" variables (new API)
            var hourly = "temperature_2m,precipitation,precipitation_probability,wind_speed_10m";
            var current = "temperature_2m,precipitation,rain,showers,snowfall,wind_speed_10m";
            var daily = "weather_code,precipitation_sum,snowfall_sum";
            var url =
                $"{_baseUrl}?latitude={latitude}&longitude={longitude}" +
                $"&hourly={hourly}" +
                $"&current={current}" +
                $"&daily={daily}" +
                $"&forecast_days={forecastDays}" +
                $"&timezone=auto";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var forecast = new WeatherForecast { Raw = root };

            if (root.TryGetProperty("timezone", out var tz))
                forecast = forecast with { Timezone = tz.GetString() };

            // ---- current (new API) ----
            // Example shape:
            // "current": {
            //   "time": "2025-10-27T14:00",
            //   "temperature_2m": 13.4,
            //   "wind_speed_10m": 21.6,
            //   "precipitation": 0.0, "rain": 0.0, "showers": 0.0, "snowfall": 0.0
            // }
            if (root.TryGetProperty("current", out var cur))
            {
                double? GetDouble(string name)
                    => cur.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

                forecast = forecast with
                {
                    CurrentTemperature = GetDouble("temperature_2m"),
                    CurrentWindSpeed10m = GetDouble("wind_speed_10m"),
                    CurrentPrecipitation = GetDouble("precipitation"),
                    CurrentRain = GetDouble("rain"),
                    CurrentShowers = GetDouble("showers"),
                    CurrentSnowfall = GetDouble("snowfall")
                };
            }

            // ---- hourly arrays ----
            if (root.TryGetProperty("hourly", out var hourlyEl))
            {
                DateTime[]? times = null;
                double[]? temps = null;

                if (hourlyEl.TryGetProperty("time", out var timeEl) && timeEl.ValueKind == JsonValueKind.Array)
                {
                    var n = timeEl.GetArrayLength();
                    times = new DateTime[n];
                    for (int i = 0; i < n; i++)
                    {
                        // Handle either ISO strings or unix seconds
                        if (timeEl[i].ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(timeEl[i].GetString(), out var dt))
                        {
                            times[i] = dt;
                        }
                        else if (timeEl[i].ValueKind == JsonValueKind.Number)
                        {
                            times[i] = DateTime.UnixEpoch.AddSeconds(timeEl[i].GetInt64());
                        }
                    }
                }

                if (hourlyEl.TryGetProperty("temperature_2m", out var tempEl) && tempEl.ValueKind == JsonValueKind.Array)
                {
                    var n = tempEl.GetArrayLength();
                    temps = new double[n];
                    for (int i = 0; i < n; i++) temps[i] = tempEl[i].GetDouble();
                }

                if (root.TryGetProperty("daily", out var dailyEl))
                {
                    DateTime[]? dailyTime = null;
                    int[]? weatherCodes = null;
                    double[]? precip = null;
                    double[]? snow = null;

                    if (dailyEl.TryGetProperty("time", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                    {
                        dailyTime = tEl.EnumerateArray()
                            .Select(x => DateTime.TryParse(x.GetString(), out var dt)
                                ? dt : DateTime.MinValue)
                            .ToArray();
                    }

                    if (dailyEl.TryGetProperty("weather_code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Array)
                        weatherCodes = codeEl.EnumerateArray().Select(x => x.GetInt32()).ToArray();

                    if (dailyEl.TryGetProperty("precipitation_sum", out var prEl) && prEl.ValueKind == JsonValueKind.Array)
                        precip = prEl.EnumerateArray().Select(x => x.GetDouble()).ToArray();

                    if (dailyEl.TryGetProperty("snowfall_sum", out var snEl) && snEl.ValueKind == JsonValueKind.Array)
                        snow = snEl.EnumerateArray().Select(x => x.GetDouble()).ToArray();

                    forecast = forecast with
                    {
                        DailyTime = dailyTime,
                        DailyWeatherCode = weatherCodes,
                        DailyPrecipitationSum = precip,
                        DailySnowfallSum = snow
                    };
                }
                
                
                forecast = forecast with { HourlyTime = times, HourlyTemperature2m = temps };
            }

            return forecast;
        }
    }
}
