using System.Text.Json;
using System.Net;
using FoodMarket;
using Telegram.Bot;

var options = File.Exists("appsettings.json")
    ? JsonSerializer.Deserialize<MarketOptions>(File.ReadAllText("appsettings.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MarketOptions()
    : new MarketOptions();
var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token)) token = options.BotToken;
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Set BotToken in appsettings.json or BOT_TOKEN in the environment.");
    Environment.ExitCode = 1;
    return;
}

using var store = new MarketStore(options);
var market = new Marketplace(store, options);
IFoodListingParser parser = new RuleBasedPersianFoodListingParser(options, store.Locations);
var proxyUrl = Environment.GetEnvironmentVariable("SOCKS5_PROXY_URL") ?? options.Socks5ProxyUrl;
using var handler = new SocketsHttpHandler();
if (!string.IsNullOrWhiteSpace(proxyUrl))
{
    var proxy = new Uri(proxyUrl);
    if (proxy.Scheme is not ("socks5" or "socks5h")) throw new InvalidOperationException("SOCKS5_PROXY_URL must start with socks5://");
    handler.Proxy = new WebProxy(proxy);
    handler.UseProxy = true;
}
// Telegram long polling may wait around 50 seconds before returning an empty batch.
using var httpClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
var app = new BotApp(new TelegramBotClient(new TelegramBotClientOptions(token), httpClient), store, market, parser, options);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await app.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception error)
{
    Console.Error.WriteLine(error.ToString().Replace(token, "[redacted]", StringComparison.Ordinal));
    Environment.ExitCode = 1;
}
