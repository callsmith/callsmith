using System.Net;
using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="SequenceRunnerService"/>.
/// Uses NSubstitute mocks for all external collaborators.
/// </summary>
public sealed class SequenceRunnerServiceTests
{
    private readonly ICollectionService _collectionService = Substitute.For<ICollectionService>();
    private readonly IRequestAssemblyService _assemblyService = Substitute.For<IRequestAssemblyService>();
    private readonly ITransportRegistry _transportRegistry = Substitute.For<ITransportRegistry>();
    private readonly ITransport _transport = Substitute.For<ITransport>();
    private readonly IJsonPathService _jsonPathService = Substitute.For<IJsonPathService>();

    private static readonly EnvironmentModel GlobalEnv = new()
    {
        FilePath = string.Empty,
        EnvironmentId = Guid.NewGuid(),
        Name = "Global",
        Variables = [],
    };

    private SequenceRunnerService CreateSut() => new(
        _collectionService,
        _assemblyService,
        _transportRegistry,
        _jsonPathService,
        NullLogger<SequenceRunnerService>.Instance);

    private static CollectionRequest MakeRequest(string name = "Request") => new()
    {
        FilePath = $"/col/{name}.callsmith",
        Name = name,
        Method = HttpMethod.Get,
        Url = "https://example.com",
    };

    private static AssembledRequest MakeAssembled(string url = "https://example.com") => new()
    {
        RequestModel = new RequestModel
        {
            Method = HttpMethod.Get,
            Url = url,
            Headers = new Dictionary<string, string>(),
        },
        ResolvedUrl = url,
        VariableBindings = [],
        EffectiveAuth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        AutoAppliedHeaders = [],
    };

    private static ResponseModel MakeResponse(int statusCode = 200, string body = "{}") => new()
    {
        StatusCode = statusCode,
        ReasonPhrase = "OK",
        Body = body,
        BodyBytes = [],
        FinalUrl = "https://example.com",
        Headers = new Dictionary<string, string>(),
        Elapsed = TimeSpan.FromMilliseconds(50),
    };

    private static SequenceModel MakeSequence(params SequenceStep[] steps) => new()
    {
        SequenceId = Guid.NewGuid(),
        FilePath = "/col/sequences/test.seq.callsmith",
        Name = "Test Sequence",
        Steps = steps,
    };

    private static SequenceStep MakeStep(string name = "Step1") => new()
    {
        StepId = Guid.NewGuid(),
        RequestFilePath = $"/col/{name}.callsmith",
        RequestName = name,
    };

    // ─── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EmptySequence_ReturnsSuccessWithNoSteps()
    {
        var sut = CreateSut();
        var sequence = MakeSequence();

        var result = await sut.RunAsync(sequence, GlobalEnv, null, "/col");

        result.IsSuccess.Should().BeTrue();
        result.Steps.Should().BeEmpty();
        result.TotalElapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_SingleStep_ExecutesAndReturnsResult()
    {
        var step = MakeStep("Login");
        var seq = MakeSequence(step);
        var request = MakeRequest("Login");
        var response = MakeResponse(200, """{"token":"abc123"}""");

        _collectionService
            .LoadRequestAsync(step.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(request);
        _assemblyService
            .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, null, "/col",
                step.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeAssembled());
        _transportRegistry.Resolve(Arg.Any<RequestModel>()).Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>()).Returns(response);

        var sut = CreateSut();
        var result = await sut.RunAsync(seq, GlobalEnv, null, "/col");

        result.IsSuccess.Should().BeTrue();
        result.Steps.Should().HaveCount(1);
        result.Steps[0].IsSuccess.Should().BeTrue();
        result.Steps[0].RequestName.Should().Be("Login");
        result.Steps[0].Response.Should().Be(response);
    }

    [Fact]
    public async Task RunAsync_MultipleSteps_ExecutesAllOnSuccess()
    {
        var step1 = MakeStep("Step1");
        var step2 = MakeStep("Step2");
        var seq = MakeSequence(step1, step2);

        foreach (var step in new[] { step1, step2 })
        {
            _collectionService
                .LoadRequestAsync(step.RequestFilePath, Arg.Any<CancellationToken>())
                .Returns(MakeRequest(step.RequestName));
            _assemblyService
                .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, null, "/col",
                    step.RequestFilePath, Arg.Any<CancellationToken>())
                .Returns(MakeAssembled());
        }

        _transportRegistry.Resolve(Arg.Any<RequestModel>()).Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(MakeResponse());

        var sut = CreateSut();
        var result = await sut.RunAsync(seq, GlobalEnv, null, "/col");

        result.IsSuccess.Should().BeTrue();
        result.Steps.Should().HaveCount(2);
    }

    // ─── Variable extraction ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithJsonPathExtraction_InjectsVariableForNextStep()
    {
        var step1 = new SequenceStep
        {
            StepId = Guid.NewGuid(),
            RequestFilePath = "/col/Login.callsmith",
            RequestName = "Login",
            Extractions =
            [
                new VariableExtraction
                {
                    VariableName = "token",
                    Source = VariableExtractionSource.ResponseBody,
                    Expression = "$.access_token",
                },
            ],
        };
        var step2 = MakeStep("Profile");
        var seq = MakeSequence(step1, step2);

        _collectionService
            .LoadRequestAsync(step1.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeRequest("Login"));
        _collectionService
            .LoadRequestAsync(step2.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeRequest("Profile"));

        // Both assembly calls return a no-op assembled request.
        _assemblyService
            .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, Arg.Any<EnvironmentModel?>(),
                "/col", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeAssembled());

        _transportRegistry.Resolve(Arg.Any<RequestModel>()).Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(MakeResponse(200, """{"access_token":"tok_abc"}"""));

        // JSONPath returns the extracted value.
        var tokenElement = System.Text.Json.JsonDocument.Parse(@"""tok_abc""").RootElement;
        _jsonPathService.Query(Arg.Any<System.Text.Json.JsonElement>(), "$.access_token")
            .Returns([tokenElement]);

        var sut = CreateSut();
        var result = await sut.RunAsync(seq, GlobalEnv, null, "/col");

        result.Steps[0].ExtractedVariables.Should().ContainKey("token")
            .WhoseValue.Should().Be("tok_abc");
    }

    // ─── Failure behaviour ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenRequestFileNotFound_StopsAndReturnsFailure()
    {
        var step = new SequenceStep
        {
            StepId = Guid.NewGuid(),
            RequestFilePath = "/nonexistent/Missing.callsmith",
            RequestName = "Missing",
        };
        var seq = MakeSequence(step);

        _collectionService
            .LoadRequestAsync(step.RequestFilePath, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("Not found", step.RequestFilePath));

        var sut = CreateSut();
        var result = await sut.RunAsync(seq, GlobalEnv, null, "/col");

        result.IsSuccess.Should().BeFalse();
        result.Steps.Should().HaveCount(1);
        result.Steps[0].IsSuccess.Should().BeFalse();
        result.Steps[0].Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenSendThrows_StopsAndReturnsFailure()
    {
        var step = MakeStep("Failing");
        var seq = MakeSequence(step);

        _collectionService
            .LoadRequestAsync(step.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeRequest("Failing"));
        _assemblyService
            .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, null, "/col",
                step.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeAssembled());
        _transportRegistry.Resolve(Arg.Any<RequestModel>()).Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = CreateSut();
        var result = await sut.RunAsync(seq, GlobalEnv, null, "/col");

        result.IsSuccess.Should().BeFalse();
        result.Steps[0].IsSuccess.Should().BeFalse();
        result.Steps[0].Error.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task RunAsync_SuccessfulStepFollowedByFailedStep_StopsAfterFailure()
    {
        var step1 = MakeStep("Ok");
        var step2 = MakeStep("Bad");
        var step3 = MakeStep("Never");
        var seq = MakeSequence(step1, step2, step3);

        _collectionService
            .LoadRequestAsync(step1.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeRequest("Ok"));
        _collectionService
            .LoadRequestAsync(step2.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeRequest("Bad"));

        _assemblyService
            .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, null, "/col",
                step1.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeAssembled());
        _assemblyService
            .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, null, "/col",
                step2.RequestFilePath, Arg.Any<CancellationToken>())
            .Returns(MakeAssembled());

        _transportRegistry.Resolve(Arg.Any<RequestModel>()).Returns(_transport);

        var callCount = 0;
        _transport
            .SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult(MakeResponse());
                throw new HttpRequestException("Bad step");
            });

        var sut = CreateSut();
        var result = await sut.RunAsync(seq, GlobalEnv, null, "/col");

        result.IsSuccess.Should().BeFalse();
        result.Steps.Should().HaveCount(2, because: "step 3 must not run after step 2 fails");
        result.Steps[0].IsSuccess.Should().BeTrue();
        result.Steps[1].IsSuccess.Should().BeFalse();
    }

    // ─── Progress reporting ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ReportsProgressAfterEachStep()
    {
        var step1 = MakeStep("A");
        var step2 = MakeStep("B");
        var seq = MakeSequence(step1, step2);

        foreach (var step in new[] { step1, step2 })
        {
            _collectionService
                .LoadRequestAsync(step.RequestFilePath, Arg.Any<CancellationToken>())
                .Returns(MakeRequest(step.RequestName));
            _assemblyService
                .AssembleAsync(Arg.Any<RequestAssemblyInput>(), GlobalEnv, null, "/col",
                    step.RequestFilePath, Arg.Any<CancellationToken>())
                .Returns(MakeAssembled());
        }

        _transportRegistry.Resolve(Arg.Any<RequestModel>()).Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(MakeResponse());

        var reported = new List<SequenceStepResult>();
        var progress = new Progress<SequenceStepResult>(r => reported.Add(r));

        var sut = CreateSut();
        await sut.RunAsync(seq, GlobalEnv, null, "/col", progress);

        // Allow progress callbacks to flush (Progress<T> raises on the sync-context).
        await Task.Delay(50);

        reported.Should().HaveCount(2);
        reported[0].RequestName.Should().Be("A");
        reported[1].RequestName.Should().Be("B");
    }
}
