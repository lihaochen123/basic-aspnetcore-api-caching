using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace BasicWeatherCacheApp.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private readonly HttpClient _client;
    private readonly IDatabase _redis;
    private readonly ILogger<WeatherForecastController> _logger;
    private readonly TimeSpan _slidingTtl = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maxTtl = TimeSpan.FromSeconds(30);

    public WeatherForecastController(
        HttpClient client,
        IConnectionMultiplexer muxer,
        ILogger<WeatherForecastController> logger)
    {
        _client = client;
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("weatherCachingApp", "1.0"));
        _redis = muxer.GetDatabase();
        _logger = logger;
    }


    [HttpGet(Name = "GetWeatherForecast")]
    public async Task<ForecastResult?> Get([FromQuery] double latitude, [FromQuery] double longitude)
    {
        var watch = Stopwatch.StartNew();
        var keyName = $"forecast:{latitude},{longitude}";
        var creationTimeKey = $"{keyName}:creationTime";

        _logger.LogDebug("Fetching weather forecast for {Latitude}, {Longitude}", latitude, longitude);

        string? json = await _redis.StringGetAsync(keyName);
        string? creationTimeStr = await _redis.StringGetAsync(creationTimeKey);

        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(creationTimeStr) ||
            (DateTime.TryParse(creationTimeStr, null, DateTimeStyles.AdjustToUniversal, out var creationTime) && DateTime.UtcNow - creationTime > _maxTtl))
        {
            json = await GetForecast(latitude, longitude);
            var setTask = _redis.StringSetAsync(keyName, json);
            var expireTask = _redis.KeyExpireAsync(keyName, _slidingTtl);
            var setCreationTimeTask = _redis.StringSetAsync(creationTimeKey, DateTime.UtcNow.ToString("o"));
            await Task.WhenAll(setTask, expireTask, setCreationTimeTask);
            _logger.LogDebug("New data cached with key {KeyName} and creation time {CreationTimeKey}", keyName, creationTimeKey);
        }
        else
        {
            // Reset the TTL each time the key is accessed if within max TTL
            var previousTtl = await _redis.KeyTimeToLiveAsync(keyName);
            await _redis.KeyExpireAsync(keyName, _slidingTtl);
            
            // Logging stuff (irrelevant to the logic)
            _logger.LogDebug("Cache hit. Resetting TTL for key {KeyName}. Previous TTL was {previousTTL}", keyName, previousTtl.ToString());
            if (DateTime.TryParse(creationTimeStr, null, DateTimeStyles.AdjustToUniversal, out var creationTime2))
            {
                var timeDifference = DateTime.UtcNow - creationTime2;
                _logger.LogDebug("Total time elapsed between Creation Date and Now: {timeDifference}", timeDifference);
            }
            else
            {
                _logger.LogDebug("Failed to parse creation time string: {CreationTimeStr}", creationTimeStr);
            }
        }


        if (json == null) return null;
        var forecast =
            JsonSerializer.Deserialize<IEnumerable<WeatherForecast>>(json);
        watch.Stop();
        var result = new ForecastResult(forecast, watch.ElapsedMilliseconds);

        _logger.LogDebug("Weather forecast fetched in {ElapsedMilliseconds} ms", watch.ElapsedMilliseconds);

        return result;
    }

    private async Task<string?> GetForecast(double latitude, double longitude)
    {
        var pointsRequestQuery = $"https://api.weather.gov/points/{latitude},{longitude}"; //get the URI
        var result = await _client.GetFromJsonAsync<JsonObject>(pointsRequestQuery);
        if (result == null) return null;
        var gridX = result["properties"]?["gridX"]?.ToString();
        var gridY = result["properties"]?["gridY"]?.ToString();
        var gridId = result["Properties"]?["gridId"]?.ToString();
        var forecastRequestQuery = $"https://api.weather.gov/gridpoints/{gridId}/{gridX},{gridY}/forecast";
        var forecastResult = await _client.GetFromJsonAsync<JsonObject>(forecastRequestQuery);
        var periodsJson = forecastResult?["properties"]?["periods"]?.ToJsonString();
        return periodsJson;
    }
}