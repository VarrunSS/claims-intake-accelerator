using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClaimsIntake.Pipeline.Prompts;

/// <summary>
/// Puts untrusted text (the document, the loss description) into a prompt as JSON string data (AI-03). Inside a JSON string
/// a quote, a newline or a backslash is escaped, and <c>&lt;</c> and <c>&gt;</c> are written as the escapes backslash-u003C and backslash-u003E,
/// so the text can never contain a closing tag such as <c>&lt;/claim&gt;</c> that would end the "treat this as data" frame the
/// prompt relies on. The text is still readable to the model.
/// </summary>
public static class UntrustedText
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Serialises a value to compact JSON with no raw angle bracket in it.</summary>
    public static string Encode(object? value) =>
        JsonSerializer.Serialize(value, Options).Replace("<", "\\u003C", StringComparison.Ordinal).Replace(">", "\\u003E", StringComparison.Ordinal);
}

/// <summary>What a prompt-injection detector made of a piece of untrusted text.</summary>
/// <param name="Flagged">True when the text looks like an attack on the model.</param>
/// <param name="Reason">A short label for the handler, never a quote from the text.</param>
public sealed record PromptAttackResult(bool Flagged, string? Reason = null);

/// <summary>
/// The seam where a prompt-injection detector sits, for example Azure AI Content Safety Prompt Shields (document attacks) on
/// Azure. Locally it is a no-op. A flagged document is sent to senior review. Prompt Shields itself can only be proven on Azure
/// and has not been run (AI-03).
/// </summary>
public interface IPromptAttackDetector
{
    Task<PromptAttackResult> DetectAsync(string untrustedText, CancellationToken cancellationToken);
}

/// <summary>The local default: flags nothing.</summary>
public sealed class NoOpPromptAttackDetector : IPromptAttackDetector
{
    public Task<PromptAttackResult> DetectAsync(string untrustedText, CancellationToken cancellationToken) =>
        Task.FromResult(new PromptAttackResult(false));
}
