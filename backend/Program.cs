using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------
// Services
// ---------------------------------------------------------

builder.Services.AddCors(options =>
{
    options.AddPolicy("StreamlitLocal", policy =>
    {
        policy
            .WithOrigins("http://localhost:8501")
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddHttpClient("Gemini", client =>
{
    // Each provider request receives its own timeout.
    client.Timeout = Timeout.InfiniteTimeSpan;

    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "DocuMint-App/1.0"
    );
});

var app = builder.Build();

app.UseCors("StreamlitLocal");

string? apiKey =
    Environment.GetEnvironmentVariable("GEMINI_API_KEY");

// ---------------------------------------------------------
// Health endpoint
// ---------------------------------------------------------

app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "healthy",
        service = "DocuMint API"
    });
});

// ---------------------------------------------------------
// Helper methods
// ---------------------------------------------------------

static IResult CreateApiError(
    int statusCode,
    string code,
    string message,
    string requestId,
    bool retryable = false)
{
    return Results.Json(
        new ApiErrorResponse(
            Code: code,
            Message: message,
            RequestId: requestId,
            Retryable: retryable
        ),
        statusCode: statusCode
    );
}

static bool IsTransientStatusCode(
    HttpStatusCode statusCode)
{
    return statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;
}

static string GetSafeProviderMessage(
    HttpStatusCode statusCode)
{
    return statusCode switch
    {
        HttpStatusCode.BadRequest =>
            "The AI provider rejected the request format.",

        HttpStatusCode.Unauthorized =>
            "The AI provider rejected the API credentials.",

        HttpStatusCode.Forbidden =>
            "The API key does not have permission to use this model.",

        HttpStatusCode.NotFound =>
            "The selected AI model is unavailable.",

        HttpStatusCode.TooManyRequests =>
            "The AI service rate limit has been reached.",

        HttpStatusCode.InternalServerError =>
            "The AI provider encountered an internal error.",

        HttpStatusCode.BadGateway =>
            "The AI provider returned an invalid gateway response.",

        HttpStatusCode.ServiceUnavailable =>
            "The AI service is temporarily unavailable.",

        HttpStatusCode.GatewayTimeout =>
            "The AI service did not respond before the deadline.",

        _ =>
            "The AI provider returned an unexpected response."
    };
}

static async Task WaitBeforeRetryAsync(
    int attempt,
    CancellationToken cancellationToken)
{
    int exponentialDelayMilliseconds =
        1_000 * (int)Math.Pow(2, attempt - 1);

    int jitterMilliseconds =
        Random.Shared.Next(250, 900);

    await Task.Delay(
        exponentialDelayMilliseconds + jitterMilliseconds,
        cancellationToken
    );
}

static bool ContainsPossibleSecret(string code)
{
    string[] secretPatterns =
    {
        @"\b(api[_-]?key|client[_-]?secret|access[_-]?token|password)\s*[:=]\s*[""'][^""']+[""']",

        @"\bauthorization\s*[:=]\s*[""']bearer\s+[^""']+[""']",

        @"-----BEGIN\s+(RSA\s+|EC\s+|OPENSSH\s+)?PRIVATE\s+KEY-----"
    };

    return secretPatterns.Any(pattern =>
        Regex.IsMatch(
            code,
            pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant
        )
    );
}

static string CleanGeneratedJson(string generatedText)
{
    string cleaned = generatedText.Trim();

    if (cleaned.StartsWith(
        "```",
        StringComparison.Ordinal))
    {
        int firstLineBreak =
            cleaned.IndexOf('\n');

        if (firstLineBreak >= 0)
        {
            cleaned =
                cleaned[(firstLineBreak + 1)..];
        }
    }

    if (cleaned.EndsWith(
        "```",
        StringComparison.Ordinal))
    {
        int finalFenceIndex =
            cleaned.LastIndexOf(
                "```",
                StringComparison.Ordinal
            );

        if (finalFenceIndex >= 0)
        {
            cleaned =
                cleaned[..finalFenceIndex];
        }
    }

    return cleaned.Trim();
}

// ---------------------------------------------------------
// Documentation generation endpoint
// ---------------------------------------------------------

app.MapPost(
    "/api/v1/documentation/generate",
    async (
        [FromBody] CodeSubmissionRequest request,
        IHttpClientFactory httpClientFactory,
        CancellationToken requestAborted
    ) =>
{
    string requestId =
        Guid.NewGuid().ToString("N")[..8];

    var stopwatch =
        Stopwatch.StartNew();

    Console.WriteLine(
        $"\n📥 [{requestId}] Documentation request received."
    );

    string rawCode =
        request.RawCode?.Trim() ?? string.Empty;

    // -----------------------------------------------------
    // Input validation
    // -----------------------------------------------------

    if (string.IsNullOrWhiteSpace(rawCode))
    {
        return CreateApiError(
            StatusCodes.Status400BadRequest,
            "EMPTY_INPUT",
            "Paste C#/.NET controller code before generating documentation.",
            requestId
        );
    }

    const int maxInputCharacters = 20_000;

    if (rawCode.Length > maxInputCharacters)
    {
        return CreateApiError(
            StatusCodes.Status413PayloadTooLarge,
            "INPUT_TOO_LARGE",
            $"The submitted code exceeds the {maxInputCharacters:N0}-character limit.",
            requestId
        );
    }

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine(
            $"❌ [{requestId}] GEMINI_API_KEY is missing."
        );

        return CreateApiError(
            StatusCodes.Status500InternalServerError,
            "SERVER_CONFIGURATION_ERROR",
            "The AI service has not been configured correctly.",
            requestId
        );
    }

    if (ContainsPossibleSecret(rawCode))
    {
        Console.WriteLine(
            $"⚠️ [{requestId}] Possible credential detected."
        );

        return CreateApiError(
            StatusCodes.Status400BadRequest,
            "POSSIBLE_SECRET_DETECTED",
            "A possible password, token, API key, or private key was detected. Remove it before submitting the code.",
            requestId
        );
    }

    // -----------------------------------------------------
    // Prompt
    // -----------------------------------------------------

    const string systemPrompt = """
        You are DocuMint AI, a backend API documentation engine.

        Analyze the supplied C#/.NET controller code and identify all
        HTTP API endpoints explicitly supported by the source code.

        Requirements:
        - Extract only information supported by the submitted code.
        - Do not invent endpoints, routes, parameters, or business rules.
        - Include route, query, and body parameters when identifiable.
        - Use "unknown" when a parameter type cannot be determined.
        - Return an empty endpoints array if no API endpoint is present.
        - Keep descriptions concise and developer-friendly.
        - Generate syntactically reasonable curl examples.
        """;

    // -----------------------------------------------------
    // JSON Schema
    // -----------------------------------------------------

    using var schemaDocument =
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "endpoints": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "method": {
                        "type": "string",
                        "enum": [
                          "GET",
                          "POST",
                          "PUT",
                          "PATCH",
                          "DELETE"
                        ]
                      },
                      "path": {
                        "type": "string"
                      },
                      "description": {
                        "type": "string"
                      },
                      "parameters": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": {
                            "name": {
                              "type": "string"
                            },
                            "type": {
                              "type": "string",
                              "enum": [
                                "string",
                                "number",
                                "integer",
                                "boolean",
                                "object",
                                "array",
                                "unknown"
                              ]
                            },
                            "required": {
                              "type": "boolean"
                            },
                            "description": {
                              "type": "string"
                            }
                          },
                          "required": [
                            "name",
                            "type",
                            "required",
                            "description"
                          ]
                        }
                      },
                      "curl_example": {
                        "type": "string"
                      },
                      "warnings": {
                        "type": "array",
                        "items": {
                          "type": "string"
                        }
                      }
                    },
                    "required": [
                      "method",
                      "path",
                      "description",
                      "parameters",
                      "curl_example",
                      "warnings"
                    ]
                  }
                }
              },
              "required": [
                "endpoints"
              ]
            }
            """
        );

    JsonElement responseSchema =
        schemaDocument.RootElement.Clone();

    // -----------------------------------------------------
    // Gemini request payload
    // -----------------------------------------------------

    var geminiPayload = new
    {
        systemInstruction = new
        {
            parts = new[]
            {
                new
                {
                    text = systemPrompt
                }
            }
        },

        contents = new[]
        {
            new
            {
                role = "user",

                parts = new[]
                {
                    new
                    {
                        text =
                            "Generate API documentation for the following " +
                            "C#/.NET controller code:\n\n" +
                            rawCode
                    }
                }
            }
        },

        generationConfig = new
        {
            responseMimeType =
                "application/json",

            responseJsonSchema =
                responseSchema,

            temperature = 0.1,

            maxOutputTokens = 4096
        }
    };

    string jsonPayload =
        JsonSerializer.Serialize(geminiPayload);

    // These models were returned by your API-key model list.
    string[] models =
    {
        "gemini-3.1-flash-lite",
        "gemini-3.5-flash"
    };

    const int attemptsPerModel = 2;
    const int attemptTimeoutSeconds = 35;

    HttpStatusCode? lastStatusCode = null;

    string lastSafeMessage =
        "The AI service could not complete the request.";

    var client =
        httpClientFactory.CreateClient("Gemini");

    try
    {
        foreach (string model in models)
        {
            for (
                int attempt = 1;
                attempt <= attemptsPerModel;
                attempt++)
            {
                string url =
                    "https://generativelanguage.googleapis.com/" +
                    $"v1beta/models/{model}:generateContent";

                using var requestMessage =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        url
                    )
                    {
                        Content = new StringContent(
                            jsonPayload,
                            Encoding.UTF8,
                            "application/json"
                        )
                    };

                requestMessage.Headers.TryAddWithoutValidation(
                    "x-goog-api-key",
                    apiKey
                );

                using var attemptTimeout =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            requestAborted
                        );

                attemptTimeout.CancelAfter(
                    TimeSpan.FromSeconds(
                        attemptTimeoutSeconds
                    )
                );

                Console.WriteLine(
                    $"📡 [{requestId}] Model: {model}, attempt: {attempt}."
                );

                try
                {
                    using HttpResponseMessage response =
                        await client.SendAsync(
                            requestMessage,
                            attemptTimeout.Token
                        );

                    string providerResponse =
                        await response.Content
                            .ReadAsStringAsync(
                                attemptTimeout.Token
                            );

                    lastStatusCode =
                        response.StatusCode;

                    // Show the actual Gemini validation error
                    // without logging the user's submitted source code.
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine(
                            $"🔎 [{requestId}] Gemini error body:"
                        );

                        Console.WriteLine(providerResponse);
                    }

                    // -------------------------------------------------
                    // Successful response
                    // -------------------------------------------------

                    if (response.IsSuccessStatusCode)
                    {
                        using var providerDocument =
                            JsonDocument.Parse(
                                providerResponse
                            );

                        JsonElement providerRoot =
                            providerDocument.RootElement;

                        if (
                            providerRoot.TryGetProperty(
                                "promptFeedback",
                                out JsonElement promptFeedback
                            ) &&
                            promptFeedback.TryGetProperty(
                                "blockReason",
                                out JsonElement blockReason
                            )
                        )
                        {
                            string reason =
                                blockReason.GetString() ??
                                "UNKNOWN";

                            return CreateApiError(
                                StatusCodes
                                    .Status422UnprocessableEntity,
                                "AI_REQUEST_BLOCKED",
                                $"The AI provider blocked the request ({reason}). Remove sensitive or unsupported content and try again.",
                                requestId
                            );
                        }

                        if (
                            !providerRoot.TryGetProperty(
                                "candidates",
                                out JsonElement candidates
                            ) ||
                            candidates.ValueKind !=
                                JsonValueKind.Array ||
                            candidates.GetArrayLength() == 0
                        )
                        {
                            return CreateApiError(
                                StatusCodes.Status502BadGateway,
                                "EMPTY_AI_RESPONSE",
                                "The AI service returned no documentation result.",
                                requestId,
                                retryable: true
                            );
                        }

                        JsonElement candidate =
                            candidates[0];

                        if (
                            candidate.TryGetProperty(
                                "finishReason",
                                out JsonElement finishReasonElement
                            )
                        )
                        {
                            string finishReason =
                                finishReasonElement.GetString() ??
                                "UNKNOWN";

                            if (
                                finishReason is
                                    "SAFETY" or
                                    "BLOCKLIST" or
                                    "PROHIBITED_CONTENT" or
                                    "RECITATION"
                            )
                            {
                                return CreateApiError(
                                    StatusCodes
                                        .Status422UnprocessableEntity,
                                    "AI_RESPONSE_BLOCKED",
                                    $"The AI provider stopped the response ({finishReason}). Modify the input and try again.",
                                    requestId
                                );
                            }

                            if (finishReason == "MAX_TOKENS")
                            {
                                return CreateApiError(
                                    StatusCodes.Status502BadGateway,
                                    "AI_RESPONSE_INCOMPLETE",
                                    "The generated response reached the output limit.",
                                    requestId,
                                    retryable: true
                                );
                            }
                        }

                        if (
                            !candidate.TryGetProperty(
                                "content",
                                out JsonElement content
                            ) ||
                            !content.TryGetProperty(
                                "parts",
                                out JsonElement parts
                            ) ||
                            parts.ValueKind !=
                                JsonValueKind.Array ||
                            parts.GetArrayLength() == 0
                        )
                        {
                            return CreateApiError(
                                StatusCodes.Status502BadGateway,
                                "MALFORMED_AI_RESPONSE",
                                "The AI service returned an incomplete response.",
                                requestId,
                                retryable: true
                            );
                        }

                        string? generatedJson = null;

                        foreach (
                            JsonElement part in
                            parts.EnumerateArray()
                        )
                        {
                            if (
                                part.TryGetProperty(
                                    "text",
                                    out JsonElement textElement
                                )
                            )
                            {
                                generatedJson =
                                    textElement.GetString();

                                if (
                                    !string.IsNullOrWhiteSpace(
                                        generatedJson
                                    )
                                )
                                {
                                    break;
                                }
                            }
                        }

                        if (
                            string.IsNullOrWhiteSpace(
                                generatedJson
                            )
                        )
                        {
                            return CreateApiError(
                                StatusCodes.Status502BadGateway,
                                "EMPTY_AI_RESPONSE",
                                "The AI service returned an empty response.",
                                requestId,
                                retryable: true
                            );
                        }

                        string cleanedJson =
                            CleanGeneratedJson(
                                generatedJson
                            );

                        using var finalDocument =
                            JsonDocument.Parse(
                                cleanedJson
                            );

                        JsonElement finalRoot =
                            finalDocument.RootElement;

                        if (
                            !finalRoot.TryGetProperty(
                                "endpoints",
                                out JsonElement endpoints
                            ) ||
                            endpoints.ValueKind !=
                                JsonValueKind.Array
                        )
                        {
                            return CreateApiError(
                                StatusCodes.Status502BadGateway,
                                "INVALID_STRUCTURED_OUTPUT",
                                "The generated response did not contain a valid endpoint list.",
                                requestId,
                                retryable: true
                            );
                        }

                        stopwatch.Stop();

                        Console.WriteLine(
                            $"✅ [{requestId}] Success using {model} " +
                            $"in {stopwatch.ElapsedMilliseconds} ms."
                        );

                        return Results.Ok(new
                        {
                            endpoints =
                                endpoints.Clone(),

                            meta = new
                            {
                                requestId,
                                model,

                                durationMilliseconds =
                                    stopwatch.ElapsedMilliseconds
                            }
                        });
                    }

                    // -------------------------------------------------
                    // Gemini returned an error
                    // -------------------------------------------------

                    lastSafeMessage =
                        GetSafeProviderMessage(
                            response.StatusCode
                        );

                    Console.WriteLine(
                        $"⚠️ [{requestId}] {model} returned " +
                        $"{(int)response.StatusCode}."
                    );

                    bool transient =
                        IsTransientStatusCode(
                            response.StatusCode
                        );

                    if (!transient)
                    {
                        // Do not retry bad request, permission,
                        // authentication or unavailable-model errors.
                        break;
                    }

                    if (attempt < attemptsPerModel)
                    {
                        await WaitBeforeRetryAsync(
                            attempt,
                            requestAborted
                        );
                    }
                }
                catch (OperationCanceledException)
                    when (
                        !requestAborted
                            .IsCancellationRequested
                    )
                {
                    lastStatusCode =
                        HttpStatusCode.GatewayTimeout;

                    lastSafeMessage =
                        "The AI request exceeded the processing deadline.";

                    Console.WriteLine(
                        $"⏱️ [{requestId}] {model} timed out."
                    );

                    if (attempt < attemptsPerModel)
                    {
                        await WaitBeforeRetryAsync(
                            attempt,
                            requestAborted
                        );
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastStatusCode =
                        HttpStatusCode.BadGateway;

                    lastSafeMessage =
                        "The backend could not reach the AI service.";

                    Console.WriteLine(
                        $"🌐 [{requestId}] Provider network error: " +
                        ex.Message
                    );

                    if (attempt < attemptsPerModel)
                    {
                        await WaitBeforeRetryAsync(
                            attempt,
                            requestAborted
                        );
                    }
                }
            }

            Console.WriteLine(
                $"🔁 [{requestId}] Moving to the next model."
            );
        }

        // -------------------------------------------------
        // All models failed
        // -------------------------------------------------

        bool retryable =
            lastStatusCode.HasValue &&
            IsTransientStatusCode(
                lastStatusCode.Value
            );

        int finalStatusCode =
            lastStatusCode switch
            {
                HttpStatusCode.TooManyRequests =>
                    StatusCodes.Status429TooManyRequests,

                HttpStatusCode.ServiceUnavailable =>
                    StatusCodes.Status503ServiceUnavailable,

                HttpStatusCode.GatewayTimeout =>
                    StatusCodes.Status504GatewayTimeout,

                _ =>
                    StatusCodes.Status502BadGateway
            };

        string finalErrorCode =
            lastStatusCode switch
            {
                HttpStatusCode.TooManyRequests =>
                    "AI_RATE_LIMITED",

                HttpStatusCode.ServiceUnavailable =>
                    "AI_UNAVAILABLE",

                HttpStatusCode.GatewayTimeout =>
                    "AI_TIMEOUT",

                HttpStatusCode.NotFound =>
                    "MODEL_UNAVAILABLE",

                HttpStatusCode.Forbidden =>
                    "AI_PERMISSION_ERROR",

                HttpStatusCode.BadRequest =>
                    "AI_REQUEST_REJECTED",

                _ =>
                    "AI_PROVIDER_ERROR"
            };

        Console.WriteLine(
            $"❌ [{requestId}] All AI attempts failed."
        );

        return CreateApiError(
            finalStatusCode,
            finalErrorCode,
            lastSafeMessage,
            requestId,
            retryable
        );
    }
    catch (JsonException ex)
    {
        Console.WriteLine(
            $"❌ [{requestId}] JSON error: {ex.Message}"
        );

        return CreateApiError(
            StatusCodes.Status502BadGateway,
            "INVALID_STRUCTURED_OUTPUT",
            "The AI returned documentation in an invalid format. Please retry the request.",
            requestId,
            retryable: true
        );
    }
    catch (OperationCanceledException)
        when (requestAborted.IsCancellationRequested)
    {
        Console.WriteLine(
            $"ℹ️ [{requestId}] Client cancelled request."
        );

        return CreateApiError(
            499,
            "REQUEST_CANCELLED",
            "The request was cancelled.",
            requestId
        );
    }
    catch (Exception ex)
    {
        // Never log rawCode or apiKey.
        Console.WriteLine(
            $"❌ [{requestId}] Unexpected error: {ex.Message}"
        );

        return CreateApiError(
            StatusCodes.Status500InternalServerError,
            "UNEXPECTED_SERVER_ERROR",
            "DocuMint encountered an unexpected error. Please try again.",
            requestId,
            retryable: true
        );
    }
});

app.Run();

// ---------------------------------------------------------
// Request and response records
// ---------------------------------------------------------

public record CodeSubmissionRequest(
    string? RawCode
);

public record ApiErrorResponse(
    string Code,
    string Message,
    string RequestId,
    bool Retryable
);