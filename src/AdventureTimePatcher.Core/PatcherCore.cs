using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AdventureTimePatcher.Core;

public sealed record RepoRef(string Owner, string Name, string Branch)
{
    public string CommitApiUrl => $"https://api.github.com/repos/{Owner}/{Name}/commits/{Uri.EscapeDataString(Branch)}";
    public string ZipUrl => $"https://github.com/{Owner}/{Name}/archive/refs/heads/{Uri.EscapeDataString(Branch)}.zip";
}

public sealed record InstallMarker(string Owner, string Repo, string Branch, string Sha, DateTimeOffset InstalledAtUtc);

public sealed record UpdateStatus(string Owner, string Repo, string Branch, string LatestSha, string InstalledSha, bool UpdateAvailable, string Status, DateTimeOffset CheckedAtUtc, string Error);

public sealed record CheckResult(RepoRef Repo, string MqRoot, string LatestSha, string InstalledSha, bool UpdateAvailable);

public sealed record UpdateResult(RepoRef Repo, string MqRoot, string LatestSha, bool AlreadyCurrent, IReadOnlyList<string> CopiedFiles, string? BackupDir);

public sealed record AdventureTimeBuild(string Version, string BuildTag)
{
    public string Display => string.IsNullOrWhiteSpace(BuildTag) ? Version : $"{Version} build {BuildTag}";
}

public sealed class LocalBuildNewerException : Exception
{
    public AdventureTimeBuild LocalBuild { get; }
    public AdventureTimeBuild RemoteBuild { get; }

    public LocalBuildNewerException(AdventureTimeBuild localBuild, AdventureTimeBuild remoteBuild)
        : base($"Installed AdventureTime appears newer than GitHub. Installed: {localBuild.Display}; GitHub: {remoteBuild.Display}.")
    {
        LocalBuild = localBuild;
        RemoteBuild = remoteBuild;
    }
}

public sealed class PatcherService
{
    public const string MarkerFileName = "adventuretime_install.json";
    public const string StatusFileName = "adventuretime_update_status.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string ShortSha(string sha) => string.IsNullOrEmpty(sha) ? "" : (sha.Length <= 12 ? sha : sha[..12]);

    public async Task<CheckResult> CheckAsync(string mqRoot, RepoRef repo, CancellationToken ct = default)
    {
        mqRoot = ValidateMqRoot(mqRoot);
        using var http = NewHttpClient();
        var latestSha = await GetLatestCommitShaAsync(http, repo, ct).ConfigureAwait(false);
        var marker = ReadMarker(mqRoot);
        var installedSha = marker?.Sha ?? string.Empty;
        var available = installedSha != latestSha;
        WriteUpdateStatus(mqRoot, repo, latestSha, installedSha, available, available ? "update_available" : "up_to_date");
        return new CheckResult(repo, mqRoot, latestSha, installedSha, available);
    }

    public async Task<UpdateResult> UpdateAsync(string mqRoot, RepoRef repo, IProgress<string>? progress = null, CancellationToken ct = default, bool allowLocalNewer = false)
    {
        mqRoot = ValidateMqRoot(mqRoot);
        using var http = NewHttpClient();
        progress?.Report("Checking GitHub...");
        var latestSha = await GetLatestCommitShaAsync(http, repo, ct).ConfigureAwait(false);
        var marker = ReadMarker(mqRoot);

        if (marker?.Sha == latestSha)
        {
            WriteUpdateStatus(mqRoot, repo, latestSha, marker?.Sha, false, "up_to_date");
            return new UpdateResult(repo, mqRoot, latestSha, true, Array.Empty<string>(), null);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdventureTimePatcher_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var zipPath = Path.Combine(tempRoot, "source.zip");
            var extractPath = Path.Combine(tempRoot, "source");
            progress?.Report("Downloading AdventureTime...");
            await DownloadFileAsync(http, repo.ZipUrl, zipPath, ct).ConfigureAwait(false);
            progress?.Report("Extracting package...");
            ZipFile.ExtractToDirectory(zipPath, extractPath);

            var sourceRoot = FindSourceRoot(extractPath);
            var luaDir = Path.Combine(mqRoot, "lua", "adventuretime");
            var localInit = Path.Combine(luaDir, "init.lua");
            var remoteInit = Path.Combine(sourceRoot, "init.lua");
            var localBuild = ReadAdventureTimeBuild(localInit);
            var remoteBuild = ReadAdventureTimeBuild(remoteInit);
            if (!allowLocalNewer && IsLocalBuildNewer(localBuild, remoteBuild))
            {
                WriteUpdateStatus(mqRoot, repo, latestSha, marker?.Sha, true, "local_newer");
                throw new LocalBuildNewerException(localBuild, remoteBuild);
            }
            var configDir = Path.Combine(mqRoot, "config");
            Directory.CreateDirectory(luaDir);
            Directory.CreateDirectory(configDir);

            var backupDir = Path.Combine(configDir, "AdventureTimePatcher_backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var copied = new List<string>();

            CopyOverwriteWithBackup(remoteInit, Path.Combine(luaDir, "init.lua"), backupDir, "lua/adventuretime/init.lua", copied);
            CopyOverwriteWithBackup(Path.Combine(sourceRoot, "README.md"), Path.Combine(luaDir, "README.md"), backupDir, "lua/adventuretime/README.md", copied);

            var targetsSrc = Path.Combine(sourceRoot, "AdventureTime_targets.ini");
            if (File.Exists(targetsSrc))
            {
                var targetsDst = Path.Combine(luaDir, "AdventureTime_targets.ini");
                if (File.Exists(targetsDst))
                {
                    File.Copy(targetsSrc, Path.Combine(luaDir, "AdventureTime_targets.ini.example"), overwrite: true);
                    copied.Add("lua/adventuretime/AdventureTime_targets.ini.example");
                }
                else
                {
                    File.Copy(targetsSrc, targetsDst);
                    copied.Add("lua/adventuretime/AdventureTime_targets.ini");
                }
            }

            WriteMarker(mqRoot, new InstallMarker(repo.Owner, repo.Name, repo.Branch, latestSha, DateTimeOffset.UtcNow));
            WriteUpdateStatus(mqRoot, repo, latestSha, latestSha, false, "up_to_date");
            PruneBackups(Path.Combine(configDir, "AdventureTimePatcher_backup"), keep: 5);
            progress?.Report("Update complete.");
            return new UpdateResult(repo, mqRoot, latestSha, false, copied, Directory.Exists(backupDir) ? backupDir : null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public InstallMarker? ReadMarker(string mqRoot)
    {
        var path = Path.Combine(mqRoot, "config", MarkerFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }


    public static AdventureTimeBuild ReadAdventureTimeBuild(string path)
    {
        if (!File.Exists(path)) return new AdventureTimeBuild(string.Empty, string.Empty);
        var text = File.ReadAllText(path);
        var version = MatchLuaString(text, "VERSION");
        var build = MatchLuaString(text, "BUILD_TAG");
        return new AdventureTimeBuild(version, build);
    }

    public static bool IsLocalBuildNewer(AdventureTimeBuild localBuild, AdventureTimeBuild remoteBuild)
    {
        var l = ParseBuildVersion(localBuild.BuildTag.Length > 0 ? localBuild.BuildTag : localBuild.Version);
        var r = ParseBuildVersion(remoteBuild.BuildTag.Length > 0 ? remoteBuild.BuildTag : remoteBuild.Version);
        if (l is null || r is null) return false;
        return l.CompareTo(r) > 0;
    }

    private static string MatchLuaString(string text, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, $@"(?:local\s+)?{name}\s*=\s*['\""]([^'\""\r\n]+)['\""]");
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    private static Version? ParseBuildVersion(string text)
    {
        text = (text ?? string.Empty).Trim().TrimStart('v', 'V');
        if (text.Length == 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+){0,3}");
        if (!m.Success) return null;
        var parts = m.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (parts.Count < 2) parts.Add(0);
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }
    private static string ValidateMqRoot(string mqRoot)
    {
        var full = Path.GetFullPath(mqRoot);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"MQ root not found: {full}");
        return full;
    }

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AdventureTimePatcher", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static async Task<string> GetLatestCommitShaAsync(HttpClient http, RepoRef repo, CancellationToken ct)
    {
        using var res = await http.GetAsync(repo.CommitApiUrl, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var sha = doc.RootElement.GetProperty("sha").GetString();
        if (string.IsNullOrWhiteSpace(sha)) throw new InvalidOperationException("GitHub response did not contain a commit sha.");
        return sha;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destination, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private static string FindSourceRoot(string extractPath)
    {
        var candidates = Directory.GetDirectories(extractPath, "*", SearchOption.TopDirectoryOnly).Concat(new[] { extractPath });
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "init.lua"))) return dir;
        }
        throw new FileNotFoundException("Downloaded source did not contain init.lua at the repository root.");
    }

    private static void CopyOverwriteWithBackup(string source, string destination, string backupDir, string displayPath, List<string> copied)
    {
        if (!File.Exists(source)) throw new FileNotFoundException($"Required source file missing: {source}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination))
        {
            Directory.CreateDirectory(backupDir);
            File.Copy(destination, Path.Combine(backupDir, Path.GetFileName(destination)), overwrite: true);
        }

        File.Copy(source, destination, overwrite: true);
        copied.Add(displayPath);
    }

    private static void WriteMarker(string mqRoot, InstallMarker marker)
    {
        var configDir = Path.Combine(mqRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, MarkerFileName), JsonSerializer.Serialize(marker, JsonOptions));
    }

    private static void WriteUpdateStatus(string mqRoot, RepoRef repo, string latestSha, string? installedSha, bool updateAvailable, string status)
    {
        var configDir = Path.Combine(mqRoot, "config");
        Directory.CreateDirectory(configDir);
        var payload = new UpdateStatus(repo.Owner, repo.Name, repo.Branch, latestSha, installedSha ?? string.Empty, updateAvailable, status, DateTimeOffset.UtcNow, string.Empty);
        File.WriteAllText(Path.Combine(configDir, StatusFileName), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static void PruneBackups(string backupRoot, int keep)
    {
        if (!Directory.Exists(backupRoot)) return;
        var dirs = Directory.GetDirectories(backupRoot)
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Skip(keep);
        foreach (var dir in dirs)
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }
}
