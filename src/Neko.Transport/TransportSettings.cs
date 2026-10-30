using System.IO;
using System.Text.Json;

namespace Neko.Transport;

/// <summary>
/// Connector-specific protocol identifiers are local configuration, not
/// compiled code. Only a signed-in, user-authorized session is supported.
/// </summary>
internal sealed record TransportSettings
{
    public string ProfileDirectory { get; init; } = "";
    public string SiteOrigin { get; init; } = "";
    public string ApiPrefix { get; init; } = "";
    public string AssetPrefix { get; init; } = "";
    public string ProjectListingPath { get; init; } = "";
    public string ConversationHistoryPath { get; init; } = "";
    public string StreamStatusPath { get; init; } = "";
    public string SubmitPath { get; init; } = "";
    public string ScopePattern { get; init; } = "";
    public string ComposerSelector { get; init; } = "";
    public string SubmitExportName { get; init; } = "";
    public string ModelMarker { get; init; } = "";
    public string ModelCacheKey { get; init; } = "";
    public string FallbackMetadataKey { get; init; } = "";
    public string[] SubmitModuleFallbacks { get; init; } = [];
    public string ModelModuleFallback { get; init; } = "";
    public string CatalogModuleFallback { get; init; } = "";
    public string ParentField { get; init; } = "";
    public string ScopeField { get; init; } = "";
    public string ConversationField { get; init; } = "";
    public string[] ForwardHeaders { get; init; } = ["authorization"];

    public string SiteHost => new Uri(SiteOrigin).Host;

    public string Format(string template,string name,string value) =>
        template.Replace("{" + name + "}",Uri.EscapeDataString(value),
            StringComparison.Ordinal);

    public static TransportSettings Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if(root.TryGetProperty("transport",out var nested))root=nested;
        var settings=JsonSerializer.Deserialize<TransportSettings>(root.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive=true })
            ?? throw new InvalidOperationException("Transport settings unavailable.");
        if(string.IsNullOrWhiteSpace(settings.ProfileDirectory))
            throw new InvalidOperationException(
                "Configured user session directory is required.");
        var folder=Path.GetDirectoryName(Path.GetFullPath(path))!;
        var localProfile=Path.IsPathRooted(settings.ProfileDirectory)
            ?settings.ProfileDirectory:
             Path.Combine(folder,settings.ProfileDirectory);
        settings=settings with {ProfileDirectory=Path.GetFullPath(localProfile)};
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if(!Uri.TryCreate(SiteOrigin,UriKind.Absolute,out var uri) ||
           uri.Scheme!="https" || !string.IsNullOrEmpty(uri.Query) ||
           !string.IsNullOrEmpty(uri.Fragment) ||
           !string.IsNullOrEmpty(uri.UserInfo) ||
           uri.AbsolutePath!="/" ||
           !string.Equals(SiteOrigin,uri.GetLeftPart(UriPartial.Authority),
               StringComparison.Ordinal))
            throw new InvalidOperationException("Valid HTTPS service origin required.");
        if(string.IsNullOrWhiteSpace(ProfileDirectory) ||
           !Directory.Exists(ProfileDirectory))
            throw new InvalidOperationException("Configured user session unavailable.");
        var paths=new[] { ApiPrefix,AssetPrefix,ProjectListingPath,
            ConversationHistoryPath,StreamStatusPath,SubmitPath };
        if(paths.Any(x=>string.IsNullOrWhiteSpace(x) ||
            !x.StartsWith('/') || x.StartsWith("//",StringComparison.Ordinal) ||
            x.Contains("..") ||
            x.Contains('#') || x.Contains('\\')))
            throw new InvalidOperationException("Transport paths must be relative to the service origin.");
        if(string.IsNullOrWhiteSpace(ScopePattern) || ScopePattern.Length>200)
            throw new InvalidOperationException("Scope pattern is required.");
        try
        {
            _=System.Text.RegularExpressions.Regex.IsMatch("example",ScopePattern,
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(200));
        }
        catch(ArgumentException)
        {
            throw new InvalidOperationException("Invalid scope pattern.");
        }
        if(new[] { ComposerSelector,SubmitExportName,ModelMarker,ModelCacheKey,
                   FallbackMetadataKey,ParentField,ScopeField,ConversationField }
           .Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Incomplete transport identifiers.");
        if(ForwardHeaders is null || ForwardHeaders.Length>32 ||
           !ForwardHeaders.Contains("authorization",StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid forwarding headers.");
    }
}
