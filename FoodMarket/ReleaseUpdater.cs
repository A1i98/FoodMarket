using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoodMarket;

public static class ReleaseUpdater
{
    private const string AssetName = "FoodMarket-win-x64.zip";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static string UpdateDirectory => Path.Combine(AppContext.BaseDirectory, ".foodmarket-update");
    private static string Marker => Path.Combine(UpdateDirectory, "pending");

    public static Uri ApiUrl(string releasesUrl)
    {
        if (!Uri.TryCreate(releasesUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/?$", RegexOptions.IgnoreCase) ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("ReleasesUrl must be a GitHub repository releases URL.", nameof(releasesUrl));
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return new Uri($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest");
    }

    public static bool IsNewer(string current, string candidate)
    {
        var currentVersion = Parse(current);
        var nextVersion = Parse(candidate.TrimStart('v'));
        if (nextVersion.Prerelease) return false;
        return nextVersion.Core > currentVersion.Core ||
            nextVersion.Core == currentVersion.Core && currentVersion.Prerelease && !nextVersion.Prerelease;
    }

    private static (Version Core, bool Prerelease) Parse(string value)
    {
        var match = Regex.Match(value.Split('+')[0], @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-([0-9A-Za-z.-]+))?$");
        if (!match.Success) throw new FormatException($"Invalid release version: {value}");
        return (Version.Parse($"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}"), match.Groups[4].Success);
    }

    public static string? Checksum(string manifest, string filename)
    {
        foreach (var line in manifest.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})\s+\*?([^/\\]+)$");
            if (match.Success && match.Groups[2].Value == filename) return match.Groups[1].Value;
        }
        return null;
    }

    public static Task WaitForUpdateAsync()
    {
        // An already-loaded EXE locks the target on Windows. The batch launcher waits
        // before starting it; a direct Task Scheduler launch must exit instead.
        if (OperatingSystem.IsWindows() && File.Exists(Marker))
            throw new IOException("Update pending: run via run-foodmarket.cmd so the EXE stays unlocked until replacement finishes.");
        return Task.CompletedTask;
    }

    public static async Task RunAsync(MarketOptions options, string? proxyUrl, CancellationTokenSource stop, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            !string.Equals(Path.GetFileName(Environment.ProcessPath), "FoodMarket.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Auto-update requires the published Windows x64 FoodMarket.exe; skipping.");
            return;
        }
        using var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxyUrl)) { handler.Proxy = new WebProxy(proxyUrl); handler.UseProxy = true; }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FoodMarket", "1.0"));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await StageLatestAsync(client, options.ReleasesUrl, ct))
                {
                    Console.WriteLine("Update staged; stopping for Task Scheduler to restart the bot.");
                    await stop.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Console.Error.WriteLine($"Update check failed: {ex.Message}"); }
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private static async Task<bool> StageLatestAsync(HttpClient client, string releasesUrl, CancellationToken ct)
    {
        var apiUrl = ApiUrl(releasesUrl);
        using var response = await client.GetAsync(apiUrl, ct);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var tag = release.RootElement.GetProperty("tag_name").GetString() ?? throw new FormatException("Missing release tag.");
        var current = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        if (!IsNewer(current, tag)) return false;
        var assets = release.RootElement.GetProperty("assets").EnumerateArray();
        var urls = assets.ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("browser_download_url").GetString()!);
        if (!urls.TryGetValue(AssetName, out var zipUrl) || !urls.TryGetValue("SHA256SUMS.txt", out var sumsUrl))
            throw new FormatException("Release is missing the Windows package or checksums.");
        var archive = await DownloadAsync(client, zipUrl, releasesUrl, tag, AssetName, ct);
        var manifest = await DownloadAsync(client, sumsUrl, releasesUrl, tag, "SHA256SUMS.txt", ct);
        var expected = Checksum(System.Text.Encoding.UTF8.GetString(manifest), AssetName)
            ?? throw new FormatException("Windows package checksum not found.");
        if (!SHA256.HashData(archive).AsSpan().SequenceEqual(Convert.FromHexString(expected)))
            throw new InvalidDataException("Windows package checksum mismatch.");

        Directory.CreateDirectory(UpdateDirectory);
        var staged = Path.Combine(UpdateDirectory, "FoodMarket-" + Guid.NewGuid().ToString("N") + ".exe");
        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        {
            var entries = zip.Entries.Where(e => e.FullName == "FoodMarket.exe").ToArray();
            if (entries.Length != 1 || entries[0].Length > 150_000_000)
                throw new InvalidDataException("Release archive has no valid FoodMarket.exe.");
            await using var source = entries[0].Open();
            await using var destination = File.Create(staged);
            await source.CopyToAsync(destination, ct);
        }
        try
        {
            // This staged executable runs the helper while the original process exits.
            File.WriteAllText(Marker, tag);
            var start = new ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(staged);
            start.ArgumentList.Add(AppContext.BaseDirectory);
            using var helper = Process.Start(start) ?? throw new IOException("Unable to launch update helper.");
            Console.WriteLine($"Updating FoodMarket from {current} to {tag} (helper PID {helper.Id}).");
            return true;
        }
        catch
        {
            File.Delete(Marker);
            File.Delete(staged);
            throw;
        }
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string url, string releasesUrl, string tag, string filename, CancellationToken ct)
    {
        var repo = ApiUrl(releasesUrl).AbsolutePath.Split('/');
        var expected = $"https://github.com/{repo[2]}/{repo[3]}/releases/download/{Uri.EscapeDataString(tag)}/{filename}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.AbsoluteUri.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release asset URL does not match the configured repository.");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 150_000_000) throw new InvalidDataException("Release asset is too large.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 150_000_000) throw new InvalidDataException("Release asset is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static async Task<int> ApplyAsync(string pid, string stagedExe, string installDirectory)
    {
        var updateDirectory = Path.Combine(Path.GetFullPath(installDirectory), ".foodmarket-update");
        if (!OperatingSystem.IsWindows() || !int.TryParse(pid, out var oldPid) || oldPid <= 0 ||
            !Path.GetDirectoryName(Path.GetFullPath(stagedExe))!.Equals(updateDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(stagedExe).StartsWith("FoodMarket-", StringComparison.OrdinalIgnoreCase)) return 1;
        var target = Path.Combine(installDirectory, "FoodMarket.exe");
        var backup = Path.Combine(updateDirectory, "FoodMarket.previous.exe");
        var next = Path.Combine(updateDirectory, "FoodMarket.next.exe");
        try
        {
            try { using var old = Process.GetProcessById(oldPid); await old.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token); }
            catch (ArgumentException) { /* The old process already exited. */ }
            // The helper's own EXE is locked. Copy it to an unlocked file first,
            // then atomically replace the old EXE and keep the previous version.
            File.Copy(stagedExe, next, true);
            File.Replace(next, target, backup, ignoreMetadataErrors: true);
            Console.WriteLine("FoodMarket executable updated; waiting for the supervisor to restart it.");
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(updateDirectory);
            File.AppendAllText(Path.Combine(updateDirectory, "update.log"), $"{DateTime.UtcNow:O}: {ex}\n");
            return 1;
        }
        finally
        {
            if (File.Exists(next)) File.Delete(next);
            File.Delete(Path.Combine(updateDirectory, "pending"));
        }
    }
}
