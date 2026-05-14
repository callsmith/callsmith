using System.Net.Http;
using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// Executes the steps of a <see cref="SequenceModel"/> in order, assembling and
/// sending each request, extracting response variables, and injecting them into a
/// mutable runtime environment so subsequent steps can reference them via
/// <c>{{variableName}}</c> substitution.
/// </summary>
public sealed class SequenceRunnerService : ISequenceRunnerService
{
    private readonly ICollectionService _collectionService;
    private readonly IRequestAssemblyService _requestAssemblyService;
    private readonly ITransportRegistry _transportRegistry;
    private readonly IJsonPathService _jsonPathService;
    private readonly ILogger<SequenceRunnerService> _logger;

    public SequenceRunnerService(
        ICollectionService collectionService,
        IRequestAssemblyService requestAssemblyService,
        ITransportRegistry transportRegistry,
        IJsonPathService jsonPathService,
        ILogger<SequenceRunnerService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(requestAssemblyService);
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(jsonPathService);
        ArgumentNullException.ThrowIfNull(logger);
        _collectionService = collectionService;
        _requestAssemblyService = requestAssemblyService;
        _transportRegistry = transportRegistry;
        _jsonPathService = jsonPathService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<SequenceRunResult> RunAsync(
        SequenceModel sequence,
        EnvironmentModel globalEnvironment,
        EnvironmentModel? activeEnvironment,
        string collectionRootPath,
        IProgress<SequenceStepResult>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(globalEnvironment);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Start with a mutable copy of the active environment so each step can inject
        // extracted variables into it without mutating the caller's environment.
        var runtimeEnv = activeEnvironment is not null
            ? activeEnvironment with { }
            : null;

        var stepResults = new List<SequenceStepResult>(sequence.Steps.Count);
        var overallSuccess = true;

        for (var i = 0; i < sequence.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = sequence.Steps[i];
            var result = await ExecuteStepAsync(
                step, i, globalEnvironment, runtimeEnv, collectionRootPath, ct)
                .ConfigureAwait(false);

            stepResults.Add(result);
            progress?.Report(result);

            if (!result.IsSuccess)
            {
                overallSuccess = false;
                _logger.LogWarning(
                    "Sequence '{Name}' stopped at step {Index} ({StepName}): {Error}",
                    sequence.Name, i, step.RequestName, result.Error);
                break;
            }

            // Inject extracted variables into the runtime environment so the next
            // step can consume them via {{variableName}} substitution.
            if (result.ExtractedVariables.Count > 0)
                runtimeEnv = InjectVariables(runtimeEnv, result.ExtractedVariables, globalEnvironment);
        }

        stopwatch.Stop();
        return new SequenceRunResult
        {
            Steps = stepResults,
            IsSuccess = overallSuccess,
            StartedAt = startedAt,
            TotalElapsed = stopwatch.Elapsed,
        };
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task<SequenceStepResult> ExecuteStepAsync(
        SequenceStep step,
        int index,
        EnvironmentModel globalEnvironment,
        EnvironmentModel? runtimeEnv,
        string collectionRootPath,
        CancellationToken ct)
    {
        CollectionRequest request;
        try
        {
            request = await _collectionService.LoadRequestAsync(step.RequestFilePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load request for step {Index}", index);
            return Failure(index, step.RequestName, $"Failed to load request: {ex.Message}");
        }

        AssembledRequest assembled;
        try
        {
            var input = BuildAssemblyInput(request);
            assembled = await _requestAssemblyService.AssembleAsync(
                input, globalEnvironment, runtimeEnv, collectionRootPath, step.RequestFilePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to assemble request for step {Index}", index);
            return Failure(index, step.RequestName, $"Failed to assemble request: {ex.Message}");
        }

        ResponseModel response;
        try
        {
            var transport = _transportRegistry.Resolve(assembled.RequestModel);
            response = await transport.SendAsync(assembled.RequestModel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(index, step.RequestName, "Step cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send request for step {Index}", index);
            return Failure(index, step.RequestName, $"Request failed: {ex.Message}");
        }

        var extracted = ExtractVariables(step.Extractions, response);

        return new SequenceStepResult
        {
            StepIndex = index,
            RequestName = step.RequestName,
            Response = response,
            ExtractedVariables = extracted,
            Error = null,
        };
    }

    private static RequestAssemblyInput BuildAssemblyInput(CollectionRequest request) => new()
    {
        Method = request.Method.Method,
        Url = request.Url,
        Headers = request.Headers
            .Where(h => h.IsEnabled)
            .Select(h => new KeyValuePair<string, string>(h.Key, h.Value)),
        PathParams = request.PathParams
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value)),
        QueryParams = request.QueryParams
            .Where(p => p.IsEnabled)
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value)),
        BodyType = request.BodyType,
        BodyText = request.Body,
        FormParams = request.FormParams,
        Auth = request.Auth,
    };

    private IReadOnlyDictionary<string, string> ExtractVariables(
        IReadOnlyList<VariableExtraction> extractions,
        ResponseModel response)
    {
        if (extractions.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(extractions.Count, StringComparer.Ordinal);

        foreach (var extraction in extractions)
        {
            if (string.IsNullOrWhiteSpace(extraction.VariableName) ||
                string.IsNullOrWhiteSpace(extraction.Expression))
                continue;

            try
            {
                var value = extraction.Source switch
                {
                    VariableExtractionSource.ResponseBody =>
                        ExtractFromBody(response.Body, extraction.Expression),
                    VariableExtractionSource.ResponseHeader =>
                        ExtractFromHeader(response.Headers, extraction.Expression),
                    _ => null,
                };

                if (value is not null)
                    result[extraction.VariableName] = value;
                else
                    _logger.LogDebug(
                        "Extraction '{Name}' produced no value (expression='{Expr}')",
                        extraction.VariableName, extraction.Expression);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Extraction '{Name}' failed (expression='{Expr}')",
                    extraction.VariableName, extraction.Expression);
            }
        }

        return result;
    }

    private string? ExtractFromBody(string? body, string jsonPathExpression)
    {
        if (string.IsNullOrEmpty(body)) return null;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            _logger.LogDebug("Response body is not valid JSON; cannot apply JSONPath.");
            return null;
        }

        var nodes = _jsonPathService.Query(root, jsonPathExpression);
        if (nodes.Count == 0) return null;

        var first = nodes[0];
        return first.ValueKind == JsonValueKind.String
            ? first.GetString()
            : first.GetRawText();
    }

    private static string? ExtractFromHeader(
        IReadOnlyDictionary<string, string>? headers, string headerName)
    {
        if (headers is null) return null;
        return headers.TryGetValue(headerName, out var value) ? value : null;
    }

    /// <summary>
    /// Returns a copy of <paramref name="current"/> (or a fresh empty environment when
    /// <paramref name="current"/> is null) with the given extracted variables added or
    /// updated as static variables.
    /// </summary>
    private static EnvironmentModel InjectVariables(
        EnvironmentModel? current,
        IReadOnlyDictionary<string, string> newVars,
        EnvironmentModel globalEnvironment)
    {
        var baseEnv = current ?? new EnvironmentModel
        {
            FilePath = string.Empty,
            EnvironmentId = Guid.NewGuid(),
            Name = "_sequence_runtime",
            Variables = [],
        };

        // Keep existing variables, replacing any that share a name with an extracted value.
        var existing = baseEnv.Variables
            .Where(v => !newVars.ContainsKey(v.Name))
            .ToList();

        var injected = newVars
            .Select(kv => new EnvironmentVariable
            {
                Name = kv.Key,
                Value = kv.Value,
                VariableType = EnvironmentVariable.VariableTypes.Static,
            })
            .ToList();

        return baseEnv with { Variables = [..existing, ..injected] };
    }

    private static SequenceStepResult Failure(int index, string requestName, string error) =>
        new()
        {
            StepIndex = index,
            RequestName = requestName,
            Response = null,
            ExtractedVariables = new Dictionary<string, string>(),
            Error = error,
        };
}
