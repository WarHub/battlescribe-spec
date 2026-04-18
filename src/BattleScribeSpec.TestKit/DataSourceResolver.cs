using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BattleScribeSpec;

public sealed class DataSourceResolver
{
    private static readonly Regex ShaRegex = new("^[0-9a-fA-F]{40}$", RegexOptions.Compiled);
    private static readonly object GitLock = new();
    private readonly string _cacheDir;

    public DataSourceResolver(string? cacheDir = null)
    {
        _cacheDir = cacheDir
            ?? Environment.GetEnvironmentVariable("BSSPEC_DATASOURCE_CACHE_DIR")
            ?? FindDefaultCacheDir();
    }

    /// <summary>
    /// Default cache goes into .testdata/datasource-cache under the repo root.
    /// Falls back to ~/.battlescribe-spec/datasource-cache if repo root can't be found.
    /// </summary>
    private static string FindDefaultCacheDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx"))
                || Directory.Exists(Path.Combine(dir, ".testdata")))
            {
                return Path.Combine(dir, ".testdata", "datasource-cache");
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".battlescribe-spec",
            "datasource-cache");
    }

    /// <summary>
    /// Pre-resolve all datasources used by the given specs. Call this once
    /// before parallel execution so that git clones happen sequentially
    /// and parallel threads only hit cache hits.
    /// </summary>
    public void WarmCache(IEnumerable<SpecFile> specs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            if (spec.Setup.DataSource is { Length: > 0 } uri && seen.Add(uri))
                Resolve(uri);
        }
    }

    public string Resolve(string dataSourceUri) => Resolve(DataSourceUri.Parse(dataSourceUri));

    public string Resolve(DataSourceUri uri) => uri.Provider switch
    {
        "github" => ResolveGithub(uri),
        "local" => ResolveLocal(uri),
        _ => throw new NotSupportedException($"Unsupported data source provider: {uri.Provider}")
    };

    public string? FindGameSystem(string resolvedDir, string gameSystemName) =>
        FindByName(resolvedDir, "*.gst", gameSystemName);

    public string? FindCatalogue(string resolvedDir, string catalogueName) =>
        FindByName(resolvedDir, "*.cat", catalogueName);

    private string ResolveLocal(DataSourceUri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Repo))
            throw new DirectoryNotFoundException("Local data source path is empty.");

        if (!Directory.Exists(uri.Repo))
            throw new DirectoryNotFoundException($"Local data source directory not found: {uri.Repo}");

        return uri.Repo;
    }

    private string ResolveGithub(DataSourceUri uri)
    {
        var cachePath = Path.Combine(
            [_cacheDir, .. uri.CacheKey.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);

        if (IsPopulatedCache(cachePath))
            return cachePath;

        // Serialize git clones to prevent races when parallel threads
        // resolve the same datasource (safety net — primary mechanism
        // is WarmCache called before parallel execution)
        lock (GitLock)
        {
            if (IsPopulatedCache(cachePath))
                return cachePath;

            // Clean up partial/corrupt directories before cloning
            try
            {
                if (Directory.Exists(cachePath))
                    Directory.Delete(cachePath, recursive: true);
            }
            catch (IOException)
            {
                if (IsPopulatedCache(cachePath))
                    return cachePath;
            }

            var parent = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            var repoUrl = $"https://github.com/{uri.Org}/{uri.Repo}.git";
            if (uri.Ref is not null && ShaRegex.IsMatch(uri.Ref))
            {
                RunGit(["clone", repoUrl, cachePath]);
                RunGit(["-C", cachePath, "checkout", uri.Ref]);
            }
            else if (!string.IsNullOrWhiteSpace(uri.Ref))
            {
                RunGit(["clone", "--depth", "1", "--branch", uri.Ref, repoUrl, cachePath]);
            }
            else
            {
                RunGit(["clone", "--depth", "1", repoUrl, cachePath]);
            }

            return cachePath;
        }
    }

    /// <summary>
    /// A cache directory is "populated" if it exists and has content beyond
    /// just the .git directory (which indicates a partial/interrupted clone).
    /// </summary>
    private static bool IsPopulatedCache(string path) =>
        Directory.Exists(path) &&
        Directory.EnumerateFileSystemEntries(path)
            .Any(e => !Path.GetFileName(e).Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static string? FindByName(string resolvedDir, string pattern, string name)
    {
        if (!Directory.Exists(resolvedDir))
            throw new DirectoryNotFoundException($"Resolved directory not found: {resolvedDir}");

        return Directory
            .EnumerateFiles(resolvedDir, pattern, SearchOption.AllDirectories)
            .FirstOrDefault(file => Path.GetFileName(file).Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void RunGit(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdOut = stdOutTask.GetAwaiter().GetResult();
        var stdErr = stdErrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git command failed ({process.ExitCode}):\nSTDERR: {stdErr}\nSTDOUT: {stdOut}");
    }
}
