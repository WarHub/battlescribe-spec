namespace BattleScribeSpec;

/// <summary>
/// Parsed data source URI identifying game system data.
/// Format: {provider}:{org}/{repo}[@{ref}]
/// </summary>
public sealed record DataSourceUri(
    string Provider,
    string Org,
    string Repo,
    string? Ref)
{
    /// <summary>
    /// Original URI string.
    /// </summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Cache directory path for this data source.
    /// </summary>
    public string CacheKey => Ref is not null
        ? $"{Provider}/{Org}/{Repo}/{Ref}"
        : $"{Provider}/{Org}/{Repo}/latest";

    public override string ToString() => Raw;

    /// <summary>
    /// Parse a data source URI string.
    /// </summary>
    public static DataSourceUri Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new FormatException("Data source URI cannot be empty.");

        var separatorIndex = uri.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == uri.Length - 1)
            throw new FormatException("Data source URI must include provider and path.");

        var provider = uri[..separatorIndex];
        var path = uri[(separatorIndex + 1)..];

        return provider switch
        {
            "github" => ParseGithub(uri, provider, path),
            "local" => ParseLocal(uri, provider, path),
            _ => throw new FormatException($"Unsupported data source provider: {provider}")
        };
    }

    /// <summary>
    /// Try to parse a data source URI string.
    /// </summary>
    public static bool TryParse(string uri, out DataSourceUri? result)
    {
        try
        {
            result = Parse(uri);
            return true;
        }
        catch (FormatException)
        {
            result = null;
            return false;
        }
    }

    private static DataSourceUri ParseGithub(string uri, string provider, string path)
    {
        var slashIndex = path.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == path.Length - 1)
            throw new FormatException("GitHub data source URI must be in the format github:{org}/{repo}[@{ref}].");

        var org = path[..slashIndex];
        var repoAndRef = path[(slashIndex + 1)..];

        var atIndex = repoAndRef.IndexOf('@');
        string repo;
        string? refName = null;

        if (atIndex >= 0)
        {
            if (atIndex == 0 || atIndex == repoAndRef.Length - 1)
                throw new FormatException("GitHub data source URI has an invalid ref segment.");

            repo = repoAndRef[..atIndex];
            refName = repoAndRef[(atIndex + 1)..];
        }
        else
        {
            repo = repoAndRef;
        }

        if (string.IsNullOrWhiteSpace(repo))
            throw new FormatException("GitHub data source URI must include a repository name.");

        return new DataSourceUri(provider, org, repo, refName) { Raw = uri };
    }

    private static DataSourceUri ParseLocal(string uri, string provider, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FormatException("Local data source URI must include a path.");

        return new DataSourceUri(provider, "", path, null) { Raw = uri };
    }
}
