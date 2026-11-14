using System.Text;
using System.Text.Json;
using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Prompts;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Pipeline.Fields;

/// <summary>
/// Reads claim fields with a chat model on Azure AI Foundry or Azure OpenAI, through
/// <see cref="IChatClient"/> and a JSON schema, so the reply is structured. Authentication is by
/// <c>DefaultAzureCredential</c> (set up in <see cref="ServiceCollectionExtensions"/>).
/// </summary>
/// <remarks>
/// ADR 0004 chose Microsoft Agent Framework for the extraction agent. This single extraction call needs no
/// tools or threads, so it uses <see cref="IChatClient"/>, the layer Agent Framework itself sits on. The
/// agent and its tools arrive in Milestone 3 behind the same <see cref="IClaimExtractor"/>.
///
/// The model is not trusted. The document is data inside delimiters, the model has no tools, and its answer
/// is checked: a value that cannot be found in the document gets its confidence capped at 0.5 and no source
/// span, and malformed or empty replies fail loudly rather than become an empty claim.
/// Compiled and unit-tested with a fake chat client only; never run against a real model.
/// </remarks>
public sealed class FoundryLlmClaimExtractor(IChatClient chatClient, PromptVersion? prompt = null) : IClaimExtractor
{
    /// <summary>The longest document text sent to the model, in characters.</summary>
    public const int MaxDocumentCharacters = 60_000;

    /// <summary>The most confidence we allow for a value that cannot be found in the document.</summary>
    public const double UngroundedConfidenceCap = 0.5;

    /// <summary>The prompt that shipped in Milestone 2 (<c>prompts/extraction/v1.md</c>). A run can pick another version.</summary>
    internal static string SystemPrompt => PromptLibrary.Get(PromptLibrary.Extraction).Text;

    private readonly PromptVersion _prompt = prompt ?? PromptLibrary.Get(PromptLibrary.Extraction);

    private static readonly JsonElement Schema = BuildSchema();

    public async Task<IReadOnlyList<ExtractedField>> ExtractAsync(string documentId, DocumentText text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(text);

        var options = new ChatOptions
        {
            Temperature = 0,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "claim_fields", "Fields read from a claim document."),
        };

        ChatResponse response;
        try
        {
            response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, _prompt.Text), new ChatMessage(ChatRole.User, BuildUserMessage(text))],
                options,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service error can echo the prompt, so only the type is kept.
            throw new ClaimExtractionException($"The model call failed ({ex.GetType().Name}).", ex);
        }

        return Parse(documentId, text, response.Text);
    }

    internal static string BuildUserMessage(DocumentText text)
    {
        // The document is untrusted. Each page is a JSON string, so the text cannot contain a closing </document> tag (AI-03).
        // The cut is made on the text before it is encoded, so the JSON stays valid.
        var pages = new List<Dictionary<string, object>>();
        var remaining = MaxDocumentCharacters;
        var cut = false;
        for (var i = 0; i < text.Pages.Count; i++)
        {
            var page = text.Pages[i];
            if (page.Length > remaining)
            {
                page = page[..Math.Max(remaining, 0)];
                cut = true;
            }

            remaining -= page.Length;
            pages.Add(new Dictionary<string, object> { ["page"] = i + 1, ["text"] = page });
            if (cut)
            {
                break;
            }
        }

        var body = UntrustedText.Encode(new Dictionary<string, object> { ["pages"] = pages, ["text_cut"] = cut });
        return $"<document>\n{body}\n</document>";
    }

    internal static IReadOnlyList<ExtractedField> Parse(string documentId, DocumentText text, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            ModelRunMetrics.Malformed();
            throw new ClaimExtractionException("The model returned an empty reply.");
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(StripCodeFence(json));
        }
        catch (JsonException ex)
        {
            ModelRunMetrics.Malformed();
            throw new ClaimExtractionException("The model returned malformed JSON.", ex);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                ModelRunMetrics.SchemaViolation();
                throw new ClaimExtractionException("The model reply was not a JSON object.");
            }

            if (FieldNames.All.Any(name => !parsed.RootElement.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object))
            {
                ModelRunMetrics.SchemaViolation(); // The reply is JSON but not the shape the schema asks for.
            }

            return [.. FieldNames.All.Select(name => ReadField(documentId, text, name, parsed.RootElement))];
        }
    }

    private static ExtractedField ReadField(string documentId, DocumentText text, string name, JsonElement root)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return FieldNames.NotFound(name); // Counted once per reply in Parse; a missing field is still "not found", not an error.
        }

        var value = StringOrNull(element, "value")?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return FieldNames.NotFound(name);
        }

        var quote = StringOrNull(element, "quote")?.Trim();
        var confidence = ReadConfidence(element);

        var span = Locate(documentId, text, value, quote);
        if (span is null)
        {
            confidence = Math.Min(confidence, UngroundedConfidenceCap);
        }

        return new ExtractedField(name, value, Math.Round(confidence, 2), span);
    }

    private static double ReadConfidence(JsonElement element)
    {
        if (!element.TryGetProperty("confidence", out var c) || c.ValueKind != JsonValueKind.Number || !c.TryGetDouble(out var value)
            || double.IsNaN(value))
        {
            return 0.0; // No usable confidence: treat it as none rather than assume the model was sure.
        }

        return Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Finds where the value sits in the document, preferring the model's quote to pick the right occurrence.</summary>
    private static SourceSpan? Locate(string documentId, DocumentText text, string value, string? quote)
    {
        if (!string.IsNullOrEmpty(quote))
        {
            for (var p = 0; p < text.Pages.Count; p++)
            {
                var at = text.Pages[p].IndexOf(quote, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                var inside = quote.IndexOf(value, StringComparison.Ordinal);
                return inside >= 0
                    ? new SourceSpan(documentId, p + 1, at + inside, value.Length)
                    : new SourceSpan(documentId, p + 1, at, quote.Length);
            }
        }

        for (var p = 0; p < text.Pages.Count; p++)
        {
            var at = text.Pages[p].IndexOf(value, StringComparison.Ordinal);
            if (at >= 0)
            {
                return new SourceSpan(documentId, p + 1, at, value.Length);
            }
        }

        return null;
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string StripCodeFence(string json)
    {
        var trimmed = json.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline ? trimmed[(firstNewline + 1)..lastFence] : trimmed;
    }

    private static JsonElement BuildSchema()
    {
        const string field =
            """{"type":"object","additionalProperties":false,"required":["value","quote","confidence"],"properties":{"value":{"type":["string","null"]},"quote":{"type":["string","null"]},"confidence":{"type":"number"}}}""";
        var properties = string.Join(',', FieldNames.All.Select(n => $"\"{n}\":{field}"));
        var required = string.Join(',', FieldNames.All.Select(n => $"\"{n}\""));
        var json = "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[" + required + "],\"properties\":{" + properties + "}}";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
