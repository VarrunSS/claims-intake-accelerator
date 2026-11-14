using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ClaimsIntake.Pipeline.Prompts;

/// <summary>A versioned prompt: its text and the SHA-256 of that text, recorded with every evaluation run.</summary>
/// <param name="Kind"><c>extraction</c> or <c>assessment</c>.</param>
/// <param name="Version">For example <c>v1</c>.</param>
/// <param name="Text">The prompt sent to the model.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="Text"/>.</param>
public sealed record PromptVersion(string Kind, string Version, string Text, string Sha256)
{
    /// <summary>The first 12 hex characters of the hash, enough to tell versions apart in a table.</summary>
    public string ShortHash => Sha256[..12];

    public static PromptVersion Create(string kind, string version, string rawText)
    {
        var text = Normalise(rawText);
        return new PromptVersion(kind, version, text, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    /// <summary>Line endings to LF, trailing whitespace trimmed and any byte order mark dropped, so a checkout with CRLF hashes the same.</summary>
    public static string Normalise(string text) => text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}

/// <summary>The prompt files in <c>prompts/</c>, embedded in this assembly so the Function and the evaluation read the same text.</summary>
public static class PromptLibrary
{
    public const string Extraction = "extraction";
    public const string Assessment = "assessment";
    public const string DefaultVersion = "v1";

    private static readonly string[] Kinds = [Extraction, Assessment];

    public static PromptVersion Get(string kind, string? version = null)
    {
        version = string.IsNullOrWhiteSpace(version) ? DefaultVersion : version.Trim();
        if (!Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unknown prompt kind \"{kind}\".", nameof(kind));
        }

        var resource = typeof(PromptLibrary).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => Normalised(n) == $"prompts/{kind}/{version}.md"); // MSBuild may leave a backslash in the name.
        using var stream = (resource is null ? null : typeof(PromptLibrary).Assembly.GetManifestResourceStream(resource))
            ?? throw new ArgumentException($"There is no {kind} prompt version \"{version}\". Available: {string.Join(", ", Versions(kind))}.", nameof(version));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return PromptVersion.Create(kind, version, reader.ReadToEnd());
    }

    public static IReadOnlyList<string> Versions(string kind) =>
    [
        .. typeof(PromptLibrary).Assembly.GetManifestResourceNames()
            .Select(Normalised)
            .Where(n => n.StartsWith($"prompts/{kind}/", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .Order(StringComparer.Ordinal),
    ];

    private static string Normalised(string name) => name.Replace('\\', '/');
}
