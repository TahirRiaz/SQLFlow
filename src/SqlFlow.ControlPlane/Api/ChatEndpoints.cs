using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Api;

/// <summary>What the chat feature can do under the current deployment, so the GUI shows exactly
/// the affordances that work: the feature switch itself, whether questions may carry images, and
/// whether voice recordings can be transcribed.</summary>
public sealed record ChatCapabilitiesDto(
    bool Enabled, string Provider, bool Images, bool Transcription, int MaxImages, long MaxImageBytes);

/// <summary>One chat conversation in the owner's list (newest activity first).</summary>
public sealed record ChatConversationDto(Guid Id, string Title, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>One tool call an answer made (in call order), for the transcript's tool-activity display.</summary>
public sealed record ChatToolCallDto(string Name, string Status);

/// <summary>One message of a conversation transcript: the DTO twin of <see cref="CatalogChatMessage"/>.</summary>
public sealed record ChatMessageDto(
    long Id, int Ordinal, string Role, string Text, IReadOnlyList<string> Images,
    IReadOnlyList<ChatToolCallDto> ToolCalls, DateTime CreatedUtc);

/// <summary>What a purge removed: every conversation of the caller and every message in them.</summary>
public sealed record ChatConversationsPurgedDto(int Conversations, int Messages);

/// <summary>A rename request for a conversation.</summary>
public sealed record RenameChatConversationRequest(string Title);

/// <summary>One question for the assistant: into an existing conversation, or a new one when
/// <see cref="ConversationId"/> is null. Images are <c>data:&lt;mime&gt;;base64,...</c> URIs.</summary>
public sealed record ChatAskRequest(Guid? ConversationId, string? Question, IReadOnlyList<string>? Images);

/// <summary>The stream's first event: which conversation the answer belongs to (echoed for an
/// existing conversation, minted for a new one).</summary>
public sealed record ChatStreamConversationDto(Guid Id, string Title, long UserMessageId, int UserMessageOrdinal);

/// <summary>One streamed fragment of the answer text (a delta, not the accumulated text).</summary>
public sealed record ChatStreamDeltaDto(string Text);

/// <summary>The stream's terminal event: the persisted assistant message.</summary>
public sealed record ChatStreamDoneDto(long MessageId, int Ordinal, string Text, IReadOnlyList<ChatToolCallDto> ToolCalls);

/// <summary>A mid-stream failure, delivered as an event because the response status is already
/// committed once streaming begins.</summary>
public sealed record ChatStreamErrorDto(string Message);

/// <summary>The transcription of one recorded voice message.</summary>
public sealed record ChatTranscriptionDto(string Text);

/// <summary>
/// The GUI chat assistant: the same SqlFlow.Assistant core the Slack bot runs, streamed over SSE
/// with conversations persisted in the catalog (the durable transcript the provider-side state is
/// only a cache of). Every agent run forwards the calling user's own bearer to the SQLFlow MCP
/// server, so the assistant's tool access is exactly the caller's access, and conversations are
/// strictly per-user. The whole surface stays mapped when the feature is disabled: the handlers
/// answer with a clear problem and <c>/chat/capabilities</c> reports the switch, so the GUI can
/// explain instead of erroring.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>A question's text cap: far beyond anything typed, small enough that a runaway
    /// client cannot park megabytes in the transcript.</summary>
    private const int MaxQuestionLength = 32_000;

    private const int MaxTitleLength = 200;

    /// <summary>How many characters of the first question become the derived conversation title.</summary>
    private const int DerivedTitleLength = 80;

    /// <summary>The most conversations one listing returns; older ones age out of view (they are
    /// kept, a rename or new message surfaces them again).</summary>
    private const int MaxListedConversations = 200;

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var chat = group.MapGroup("/chat").WithTags("Chat");
        chat.MapGet("/capabilities", GetCapabilities).WithName("GetChatCapabilities");
        chat.MapGet("/conversations", ListConversationsAsync).WithName("ListChatConversations");
        chat.MapPut("/conversations/{id:guid}", RenameConversationAsync).WithName("RenameChatConversation");
        chat.MapDelete("/conversations/{id:guid}", DeleteConversationAsync).WithName("DeleteChatConversation");
        chat.MapDelete("/conversations", DeleteAllConversationsAsync).WithName("DeleteAllChatConversations");
        chat.MapGet("/conversations/{id:guid}/messages", ListMessagesAsync).WithName("ListChatMessages");
        chat.MapPost("/ask", AskAsync).WithName("AskChatAssistant");
        chat.MapPost("/transcribe", TranscribeAsync).WithName("TranscribeChatAudio");
        return group;
    }

    private static Ok<ChatCapabilitiesDto> GetCapabilities(IOptions<ControlPlaneOptions> options, HttpContext http)
    {
        var assistant = options.Value.Assistant;
        var transcription = http.RequestServices.GetService<TranscriptionGateway>()?.Enabled == true;
        return TypedResults.Ok(new ChatCapabilitiesDto(
            assistant.Enabled,
            assistant.Provider.ToString(),
            assistant.Enabled && assistant.MaxImages > 0,
            assistant.Enabled && transcription,
            assistant.MaxImages,
            assistant.MaxImageBytes));
    }

    private static async Task<Results<Ok<IReadOnlyList<ChatConversationDto>>, ProblemHttpResult>> ListConversationsAsync(
        CatalogDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!TryGetUserId(http, out var userId, out var noUser))
        {
            return noUser;
        }

        IReadOnlyList<ChatConversationDto> conversations = await db.ChatConversations.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedUtc)
            .Take(MaxListedConversations)
            .Select(c => new ChatConversationDto(c.Id, c.Title, c.CreatedUtc, c.UpdatedUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(conversations);
    }

    private static async Task<Results<Ok<ChatConversationDto>, ProblemHttpResult>> RenameConversationAsync(
        Guid id, RenameChatConversationRequest request, CatalogDbContext db, TimeProvider clock, HttpContext http,
        CancellationToken ct)
    {
        if (!TryGetUserId(http, out var userId, out var noUser))
        {
            return noUser;
        }

        var title = request?.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid title", detail: $"A title must be 1 to {MaxTitleLength} characters.");
        }

        // The pooled context reads no-tracking by default; this mutation must be tracked to persist.
        var conversation = await db.ChatConversations.AsTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct).ConfigureAwait(false);
        if (conversation is null)
        {
            return NotFoundConversation();
        }

        conversation.Title = title;
        conversation.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new ChatConversationDto(
            conversation.Id, conversation.Title, conversation.CreatedUtc, conversation.UpdatedUtc));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteConversationAsync(
        Guid id, CatalogDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!TryGetUserId(http, out var userId, out var noUser))
        {
            return noUser;
        }

        var deleted = await db.ChatConversations
            .Where(c => c.Id == id && c.UserId == userId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted == 0)
        {
            return NotFoundConversation();
        }

        await db.ChatMessages.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Empties the caller's whole chat history in one call: every conversation they own and every
    /// message in it. Only the caller's own rows are touched, and the count of each comes back so
    /// the GUI can say what it removed. Deleting one conversation at a time is the common case; this
    /// is the escape hatch for a rail that has grown to hundreds of entries.
    /// </summary>
    private static async Task<Results<Ok<ChatConversationsPurgedDto>, ProblemHttpResult>> DeleteAllConversationsAsync(
        CatalogDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!TryGetUserId(http, out var userId, out var noUser))
        {
            return noUser;
        }

        // Messages first, keyed off the caller's conversations, so a failure between the two
        // statements can only leave empty conversations behind, never orphaned transcripts.
        var messages = await db.ChatMessages
            .Where(m => db.ChatConversations.Any(c => c.Id == m.ConversationId && c.UserId == userId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var conversations = await db.ChatConversations
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new ChatConversationsPurgedDto(conversations, messages));
    }

    private static async Task<Results<Ok<IReadOnlyList<ChatMessageDto>>, ProblemHttpResult>> ListMessagesAsync(
        Guid id, CatalogDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!TryGetUserId(http, out var userId, out var noUser))
        {
            return noUser;
        }

        var owned = await db.ChatConversations.AsNoTracking()
            .AnyAsync(c => c.Id == id && c.UserId == userId, ct).ConfigureAwait(false);
        if (!owned)
        {
            return NotFoundConversation();
        }

        var rows = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);
        IReadOnlyList<ChatMessageDto> messages = rows.Select(ToDto).ToList();
        return TypedResults.Ok(messages);
    }

    /// <summary>
    /// One question, answered as Server-Sent Events: a <c>conversation</c> event first (which
    /// conversation the turn landed in, minted when the request named none), then <c>tool</c> and
    /// <c>delta</c> events as the model works, and a terminal <c>done</c> carrying the persisted
    /// assistant message. A failure after streaming begins arrives as an <c>error</c> event, since
    /// the status code is already committed. The user message is persisted before the model runs,
    /// so nothing typed is ever lost; a question the client aborts mid-answer persists the partial
    /// answer, exactly as ChatGPT-style UIs keep a stopped reply.
    /// </summary>
    private static async Task<IResult> AskAsync(
        ChatAskRequest request, CatalogDbContext db, TimeProvider clock, HttpContext http,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!TryGetUserId(http, out var userId, out var noUser))
        {
            return noUser;
        }

        var gateway = http.RequestServices.GetService<IAssistantGateway>();
        var options = http.RequestServices.GetRequiredService<IOptions<ControlPlaneOptions>>().Value.Assistant;
        if (gateway is null || !options.Enabled)
        {
            return NotConfigured();
        }

        if (request is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Empty request", detail: "The request body must carry a question.");
        }

        var question = request.Question?.Trim() ?? "";
        var images = request?.Images ?? [];
        if (question.Length > MaxQuestionLength)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Question too long", detail: $"A question may be at most {MaxQuestionLength} characters.");
        }
        if (question.Length == 0 && images.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Empty question", detail: "Type a question, or attach an image to ask about.");
        }
        if (ValidateImages(images, options) is { } imageProblem)
        {
            return imageProblem;
        }
        // An image with no words is still a question ("what is this error?"): give the model a prompt.
        if (question.Length == 0)
        {
            question = "Look at the attached image and help me understand or resolve what it shows.";
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        CatalogChatConversation? conversation;
        if (request!.ConversationId is { } conversationId)
        {
            // Tracked deliberately (the pooled context reads no-tracking by default): the turn
            // bumps UpdatedUtc on this row, both when the user message lands and when the answer does.
            conversation = await db.ChatConversations.AsTracking()
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct).ConfigureAwait(false);
            if (conversation is null)
            {
                return NotFoundConversation();
            }
        }
        else
        {
            conversation = new CatalogChatConversation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = DeriveTitle(question),
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            };
            db.ChatConversations.Add(conversation);
        }

        // The prior transcript (for the model's context rebuild) and the next ordinal come from the
        // persisted messages; the new question is persisted BEFORE the model runs, so nothing typed
        // is lost to a model failure.
        var prior = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.Ordinal)
            .Select(m => new { m.Ordinal, m.Role, m.Text })
            .ToListAsync(ct).ConfigureAwait(false);
        var priorTurns = prior
            .Select(m => new ConversationTurn(m.Role == ChatMessageRoles.Assistant, m.Text))
            .ToList();

        var userMessage = new CatalogChatMessage
        {
            ConversationId = conversation.Id,
            Ordinal = (prior.Count > 0 ? prior[^1].Ordinal : 0) + 1,
            Role = ChatMessageRoles.User,
            Text = question,
            ImagesJson = images.Count > 0 ? JsonSerializer.Serialize(images) : null,
            CreatedUtc = nowUtc,
        };
        db.ChatMessages.Add(userMessage);
        conversation.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Tell buffering reverse proxies (nginx) to pass frames through as they are written.
        response.Headers["X-Accel-Buffering"] = "no";

        var serializer = json.Value.SerializerOptions;
        await WriteSseAsync(response, "conversation", JsonSerializer.Serialize(new ChatStreamConversationDto(
            conversation.Id, conversation.Title, userMessage.Id, userMessage.Ordinal), serializer), ct)
            .ConfigureAwait(false);

        // The caller's own bearer becomes the MCP Authorization for this run: the assistant can do
        // exactly what this user can do against the control plane, nothing more.
        var bearer = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        var mcpBearer = bearer.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? bearer[scheme.Length..] : bearer;

        var logger = loggerFactory.CreateLogger("SqlFlow.ControlPlane.Api.ChatEndpoints");
        var toolCalls = new List<ChatToolCallDto>();
        var streamedText = new System.Text.StringBuilder();
        string? completedText = null;
        try
        {
            var assistantRequest = new AssistantRequest
            {
                ConversationKey = conversation.Id.ToString("N"),
                PriorTurns = priorTurns,
                Question = question,
                ImageDataUris = images,
                McpBearer = mcpBearer,
            };
            await foreach (var evt in gateway.StreamAsync(assistantRequest, ct).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AssistantEvent.TextDelta delta:
                        streamedText.Append(delta.Text);
                        await WriteSseAsync(response, "delta",
                            JsonSerializer.Serialize(new ChatStreamDeltaDto(delta.Text), serializer), ct)
                            .ConfigureAwait(false);
                        break;
                    case AssistantEvent.ToolCall tool:
                    {
                        var dto = RecordToolCall(toolCalls, tool);
                        await WriteSseAsync(response, "tool", JsonSerializer.Serialize(dto, serializer), ct)
                            .ConfigureAwait(false);
                        break;
                    }
                    case AssistantEvent.Completed completed:
                        completedText = completed.Text;
                        break;
                }
            }

            if (completedText is null)
            {
                throw new InvalidOperationException("The assistant stream ended without a completed answer.");
            }

            var saved = await PersistAssistantMessageAsync(
                db, clock, conversation, userMessage.Ordinal, completedText, toolCalls, ct).ConfigureAwait(false);
            await WriteSseAsync(response, "done", JsonSerializer.Serialize(
                new ChatStreamDoneDto(saved.Id, saved.Ordinal, completedText, toolCalls), serializer), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user stopped the answer (or closed the tab). Keep what was already produced, so a
            // long answer stopped near the end is not thrown away; nothing is written to the dead
            // connection, and the next open of the conversation shows the partial answer.
            if (streamedText.Length > 0)
            {
                await PersistAssistantMessageAsync(
                    db, clock, conversation, userMessage.Ordinal, streamedText.ToString(), toolCalls,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Every failure of a started answer ends here, whatever the provider threw. The status
            // code is long since committed, so an escaping exception would just drop the connection:
            // the browser would see a stream that stopped after the conversation event and render an
            // answer that never came, with nothing in the log to explain it. An error event says what
            // happened, and the partial text survives exactly as a user-stopped answer does.
            logger.LogError(ex, "Chat answer failed for conversation {ConversationId}", conversation.Id);
            if (streamedText.Length > 0)
            {
                await PersistAssistantMessageAsync(
                    db, clock, conversation, userMessage.Ordinal, streamedText.ToString(), toolCalls,
                    CancellationToken.None).ConfigureAwait(false);
            }

            // A cancellation that is not the caller's is the run's own timeout ceiling firing.
            var detail = ex is OperationCanceledException
                ? $"The assistant did not finish within {options.RunTimeoutSeconds} seconds."
                : ex.Message;
            await WriteSseAsync(response, "error", JsonSerializer.Serialize(
                new ChatStreamErrorDto(detail), serializer), ct).ConfigureAwait(false);
        }

        return TypedResults.Empty;
    }

    private static async Task<Results<Ok<ChatTranscriptionDto>, ProblemHttpResult>> TranscribeAsync(
        HttpContext http, string? fileName, CancellationToken ct)
    {
        if (!TryGetUserId(http, out _, out var noUser))
        {
            return noUser;
        }

        var transcription = http.RequestServices.GetService<TranscriptionGateway>();
        if (transcription is null || !transcription.Enabled)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Transcription is not configured",
                detail: "Set ControlPlane:Assistant:Foundry:TranscriptionDeploymentName (AzureFoundry) or "
                    + "ControlPlane:Assistant:OpenAI:TranscriptionModel (OpenAI) to enable voice input.");
        }

        // The recording arrives as the raw request body (audio/webm etc.), bounded before it is
        // buffered: the providers reject anything above 25 MB anyway.
        if (http.Request.ContentLength is { } declared && declared > TranscriptionGateway.MaxAudioBytes)
        {
            return TooLargeRecording();
        }

        using var buffer = new MemoryStream();
        await CopyBoundedAsync(http.Request.Body, buffer, TranscriptionGateway.MaxAudioBytes + 1, ct).ConfigureAwait(false);
        if (buffer.Length > TranscriptionGateway.MaxAudioBytes)
        {
            return TooLargeRecording();
        }
        if (buffer.Length == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Empty recording", detail: "The request body carried no audio.");
        }
        buffer.Position = 0;

        var contentType = string.IsNullOrWhiteSpace(http.Request.ContentType) ? "audio/webm" : http.Request.ContentType;
        var name = string.IsNullOrWhiteSpace(fileName) ? "recording.webm" : Path.GetFileName(fileName);
        try
        {
            var text = await transcription.TranscribeAsync(buffer, name, contentType, ct).ConfigureAwait(false);
            return TypedResults.Ok(new ChatTranscriptionDto(text));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Transcription failed", detail: ex.Message);
        }
    }

    /// <summary>Appends or upgrades the tool-call record for one streamed transition: a Started
    /// event appends, a Completed/Failed upgrades the newest still-running entry of that tool (tool
    /// runs never interleave the same name concurrently in practice; if one ever did, the newest
    /// entry is the one still open).</summary>
    private static ChatToolCallDto RecordToolCall(List<ChatToolCallDto> toolCalls, AssistantEvent.ToolCall tool)
    {
        var status = tool.Status.ToString().ToLowerInvariant();
        if (tool.Status == AssistantToolCallStatus.Started)
        {
            var started = new ChatToolCallDto(tool.Name, status);
            toolCalls.Add(started);
            return started;
        }

        for (var i = toolCalls.Count - 1; i >= 0; i--)
        {
            if (toolCalls[i].Name == tool.Name && toolCalls[i].Status == "started")
            {
                toolCalls[i] = new ChatToolCallDto(tool.Name, status);
                return toolCalls[i];
            }
        }
        var unmatched = new ChatToolCallDto(tool.Name, status);
        toolCalls.Add(unmatched);
        return unmatched;
    }

    private static async Task<CatalogChatMessage> PersistAssistantMessageAsync(
        CatalogDbContext db, TimeProvider clock, CatalogChatConversation conversation, int userOrdinal,
        string text, IReadOnlyList<ChatToolCallDto> toolCalls, CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var message = new CatalogChatMessage
        {
            ConversationId = conversation.Id,
            Ordinal = userOrdinal + 1,
            Role = ChatMessageRoles.Assistant,
            Text = text,
            ToolCallsJson = toolCalls.Count > 0 ? JsonSerializer.Serialize(toolCalls) : null,
            CreatedUtc = nowUtc,
        };
        db.ChatMessages.Add(message);
        conversation.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return message;
    }

    private static ProblemHttpResult? ValidateImages(IReadOnlyList<string> images, AssistantChatOptions options)
    {
        if (images.Count == 0)
        {
            return null;
        }
        if (options.MaxImages <= 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Images are disabled", detail: "This deployment does not accept image attachments.");
        }
        if (images.Count > options.MaxImages)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Too many images", detail: $"A question may carry at most {options.MaxImages} images.");
        }
        foreach (var image in images)
        {
            if (!AnthropicGateway.TryParseDataUri(image, out var mediaType, out var base64)
                || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid image", detail: "Each image must be a data:<image mime>;base64,... URI.");
            }
            // Estimated decoded size: 3 bytes per 4 base64 characters.
            if ((long)base64.Length * 3 / 4 > options.MaxImageBytes)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Image too large", detail: $"Each image may be at most {options.MaxImageBytes} bytes.");
            }
        }
        return null;
    }

    /// <summary>The conversation title derived from the first question: its first line's opening
    /// words, whitespace-collapsed.</summary>
    private static string DeriveTitle(string question)
    {
        var collapsed = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return "New chat";
        }
        return collapsed.Length <= DerivedTitleLength ? collapsed : collapsed[..DerivedTitleLength].TrimEnd() + "...";
    }

    private static ChatMessageDto ToDto(CatalogChatMessage message)
    {
        var images = message.ImagesJson is { Length: > 0 } imagesJson
            ? JsonSerializer.Deserialize<List<string>>(imagesJson) ?? []
            : [];
        var toolCalls = message.ToolCallsJson is { Length: > 0 } toolsJson
            ? JsonSerializer.Deserialize<List<ChatToolCallDto>>(toolsJson) ?? []
            : [];
        return new ChatMessageDto(
            message.Id, message.Ordinal, message.Role, message.Text, images, toolCalls, message.CreatedUtc);
    }

    /// <summary>Reads the request body into <paramref name="destination"/>, stopping once
    /// <paramref name="limit"/> bytes have been copied (the caller then rejects the oversized body
    /// without buffering the rest).</summary>
    private static async Task CopyBoundedAsync(Stream source, MemoryStream destination, long limit, CancellationToken ct)
    {
        var rented = new byte[64 * 1024];
        while (destination.Length < limit)
        {
            var read = await source.ReadAsync(rented.AsMemory(0, (int)Math.Min(rented.Length, limit - destination.Length)), ct)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            destination.Write(rented, 0, read);
        }
    }

    /// <summary>The caller's catalog user id from the <c>uid</c> claim. A bootstrap-secret session
    /// has no user behind it, so it cannot own conversations.</summary>
    private static bool TryGetUserId(HttpContext http, out Guid userId, out ProblemHttpResult problem)
    {
        if (Guid.TryParse(http.User.FindFirst("uid")?.Value, out userId))
        {
            problem = null!;
            return true;
        }
        problem = TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: "No user account",
            detail: "Chat conversations belong to a user account; this session is not backed by one.");
        return false;
    }

    private static ProblemHttpResult TooLargeRecording()
        => TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Recording too large",
            detail: $"A recording may be at most {TranscriptionGateway.MaxAudioBytes} bytes.");

    private static ProblemHttpResult NotFoundConversation()
        => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
            detail: "No conversation with that id belongs to you.");

    private static ProblemHttpResult NotConfigured()
        => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "The chat assistant is not configured",
            detail: "Set ControlPlane:Assistant:Enabled=true with a provider, a model, and the SQLFlow MCP server URL.");

    private static async Task WriteSseAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
