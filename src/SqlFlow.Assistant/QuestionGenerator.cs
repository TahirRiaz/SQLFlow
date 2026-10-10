using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.Extensions.Logging;

namespace SqlFlow.Assistant;

/// <summary>The question-relevant content of one PowerBI report visual: its title, chart type, and the fields it
/// projects with their roles. Exactly the inputs <c>SubscriberReportVisualHash</c> hashes, so a visual whose hash
/// is unchanged never needs this generated again.</summary>
public sealed record VisualQuestionContext(
    string? Title, string VisualType, IReadOnlyList<VisualQuestionField> Fields);

/// <summary>One field a visual projects, in the shape a prompt needs: its role (the axis a chart is broken down
/// BY versus the value it plots), the table/entity it belongs to, and its column or measure name.</summary>
public sealed record VisualQuestionField(string Role, string TableName, string ColumnOrMeasure, bool IsMeasure);

/// <summary>
/// Turns one PowerBI report visual's title, chart type, and projected fields into 1-3 natural-language business
/// questions it answers, as text-to-query training material (POWERAI.md Section 6). This is a plain, stateless,
/// single-turn completion, deliberately not built on <see cref="IAssistantGateway"/>/<see cref="AnthropicGateway"/>:
/// that gateway exists for the interactive chat surfaces and carries an MCP tool loop, adaptive thinking,
/// streaming, and a per-user MCP bearer token, none of which this batch, unattended, sync-time job needs. It
/// reuses only <see cref="AnthropicOptions"/> (the same API key and model already configured for the assistant),
/// via the SDK's structured-output helper so the response is parsed straight into <see cref="Answer"/> rather than
/// hand-parsed JSON.
/// </summary>
public sealed class QuestionGenerator : IDisposable
{
    private readonly AnthropicClient _client;
    private readonly AnthropicOptions _options;
    private readonly ILogger<QuestionGenerator> _logger;

    public QuestionGenerator(AnthropicOptions options, ILogger<QuestionGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        _client = new AnthropicClient { ApiKey = options.ApiKey };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The model's answer: 1-3 questions this visual answers, in the order a person would find most
    /// natural to ask them.</summary>
    [SchemaClass("1 to 3 natural-language business questions a dashboard chart answers")]
    private sealed class Answer : StructuredOutputModel
    {
        [SchemaProperty("The questions (1 to 3), most natural phrasing first", MinItems = 1)]
        public List<string> Questions { get; set; } = [];
    }

    /// <summary>
    /// Asks the model for 1-3 questions this visual answers. Returns an empty list, rather than throwing, when
    /// the model's answer does not carry at least one usable question after one retry: a visual whose questions
    /// could not be generated must not block the rest of the sync, only be reported as a warning by the caller.
    /// </summary>
    public async Task<IReadOnlyList<string>> GenerateQuestionsAsync(VisualQuestionContext visual, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visual);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var message = await _client.Messages.Create<Answer>(
                    new MessageCreateParams
                    {
                        Model = _options.Model,
                        MaxTokens = 512,
                        System = "You read the description of one visual (chart) from a PowerBI business "
                            + "dashboard and write the business question(s) it answers, phrased exactly as a "
                            + "person would type them into a search box. Ground every question in the fields "
                            + "given: the field in the Category/Rows role is what the answer is broken down BY, "
                            + "and the field in the Y/Values role is what is being measured. Do not invent "
                            + "fields, tables, or numbers that are not given. Prefer the visual's own title when "
                            + "it already reads as a question. Write 1 question for a simple visual, up to 3 "
                            + "when the visual is broad enough to answer more than one real framing.",
                        Messages = [new MessageParam { Role = "user", Content = BuildPrompt(visual) }],
                    },
                    ct).ConfigureAwait(false);

                var answer = message.Content.Select(b => b.Parsed()).FirstOrDefault(a => a is not null);
                var questions = (answer?.Questions ?? [])
                    .Where(q => !string.IsNullOrWhiteSpace(q))
                    .Select(q => q.Trim())
                    .Take(3)
                    .ToList();

                if (questions.Count > 0)
                {
                    return questions;
                }

                _logger.LogWarning(
                    "Anthropic returned no usable questions for visual '{Title}' ({VisualType}) on attempt {Attempt}",
                    visual.Title, visual.VisualType, attempt + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Question generation failed for visual '{Title}' ({VisualType}) on attempt {Attempt}",
                    visual.Title, visual.VisualType, attempt + 1);
            }
        }

        return [];
    }

    private static string BuildPrompt(VisualQuestionContext visual)
    {
        var fields = visual.Fields.Count == 0
            ? "(no fields)"
            : string.Join('\n', visual.Fields.Select(f =>
                $"- {f.Role}: {f.TableName}.{f.ColumnOrMeasure}{(f.IsMeasure ? " (measure)" : "")}"));

        return $"""
            Chart type: {visual.VisualType}
            Title: {visual.Title ?? "(none)"}
            Fields:
            {fields}
            """;
    }
}
