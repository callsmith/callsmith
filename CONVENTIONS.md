# Coding Conventions — Callsmith

> AI agents and contributors must follow these conventions in all code they write
> for this project. Consistency matters more than personal preference.

---

## 1. General

- **Language version:** C# 13 (latest features are welcome where they improve clarity)
- **Target framework:** net10.0
- **Nullable reference types:** enabled in all projects (`<Nullable>enable</Nullable>`)
- **Implicit usings:** enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- **File-scoped namespaces:** always use file-scoped namespace declarations
- **No emojis** — not in code, identifiers, comments, log messages, or exception messages

```csharp
// ✅ Correct
namespace Callsmith.Core.Services;

public class HttpTransport : ITransport { }

// ❌ Wrong
namespace Callsmith.Core.Services
{
    public class HttpTransport : ITransport { }
}
```

---

## 2. Naming

| Element | Convention | Example |
|---|---|---|
| Classes, interfaces, enums | PascalCase | `HttpTransport`, `ITransport` |
| Methods | PascalCase | `SendAsync`, `GetCollectionById` |
| Properties | PascalCase | `StatusCode`, `RequestUrl` |
| Fields (private) | `_camelCase` | `_httpClient`, `_logger` |
| Constants | PascalCase | `DefaultTimeoutSeconds` |
| Local variables | camelCase | `requestModel`, `elapsed` |
| Parameters | camelCase | `requestModel`, `ct` |
| Async methods | Suffix with `Async` | `SendAsync`, `LoadCollectionAsync` |
| Interfaces | Prefix with `I` | `ITransport`, `ICollectionService` |
| Test classes | Suffix with `Tests` | `HttpTransportTests` |
| Test methods | `MethodName_Scenario_ExpectedResult` | `SendAsync_WhenTimeout_ThrowsOperationCanceledException` |

---

## 3. File Organization

- One type (class, interface, enum, record) per file
- Filename matches the type name exactly: `HttpTransport.cs` for `class HttpTransport`
- Folders reflect namespaces: `Core/Transports/Http/HttpTransport.cs` → `Callsmith.Core.Transports.Http`

### Member ordering within a class

1. Fields (private)
2. Constructor(s)
3. Properties (public, then internal/protected)
4. Public methods
5. Private/protected methods

---

## 4. Async and Cancellation

Every method that does I/O **must** be async and accept a cancellation token:

```csharp
// ✅ Correct
public async Task<ResponseModel> SendAsync(
    RequestModel request,
    CancellationToken ct = default)
{
    var response = await _httpClient.SendAsync(message, ct);
    // ...
}

// ❌ Wrong — blocking, no cancellation
public ResponseModel Send(RequestModel request)
{
    return _httpClient.SendAsync(message).Result;
}
```

- Use `ct` as the parameter name for `CancellationToken` (short, consistent)
- Always pass `ct` through to every inner async call
- Never use `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`

---

## 5. Null Handling

- Nullable reference types are enabled — the compiler is your first line of defence
- Use `ArgumentNullException.ThrowIfNull()` at the top of public methods
- Use null-coalescing and null-conditional operators (`??`, `?.`) for concise null checks
- Avoid `!` (null-forgiving operator) unless genuinely necessary, and always add a
  comment explaining why

```csharp
// ✅
public void Process(RequestModel request)
{
    ArgumentNullException.ThrowIfNull(request);
    var url = request.Url ?? throw new InvalidOperationException("URL must be set.");
}
```

---

## 6. Error Handling

- Use specific exception types — prefer `InvalidOperationException`, `ArgumentException`,
  `HttpRequestException` over bare `Exception`
- Do not swallow exceptions silently:

```csharp
// ❌ Never do this
try { ... }
catch (Exception) { }

// ✅ At minimum, log and rethrow
try { ... }
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to send request to {Url}", request.Url);
    throw;
}
```

- Use `OperationCanceledException` for cancellation — do not catch it unless you
  specifically need to handle cancellation differently

---

## 7. Dependency Injection

- Constructor injection only — no property injection, no service locator
- All dependencies are declared as interfaces, not concrete types
- Keep constructors small — if a constructor has more than 4–5 parameters,
  consider whether the class is doing too much

```csharp
// ✅
public class CollectionService(
    ICollectionRepository repository,
    ILogger<CollectionService> logger) : ICollectionService
{
    // Use primary constructor syntax (C# 12+)
}
```

---

## 8. ViewModels (Desktop project only)

- Use `[ObservableProperty]` for bindable properties (generates the boilerplate)
- Use `[RelayCommand]` for commands
- Use `WeakReferenceMessenger` from CommunityToolkit.Mvvm for cross-ViewModel
  communication — do not hold direct references between ViewModels
- Do not call EF Core, HttpClient, or any Data project type directly from a ViewModel

```csharp
// ✅
public partial class RequestViewModel : ObservableObject
{
    private readonly ITransport _transport;

    [ObservableProperty]
    private string _requestUrl = string.Empty;

    [ObservableProperty]
    private ResponseModel? _response;

    public RequestViewModel(ITransport transport) => _transport = transport;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken ct)
    {
        Response = await _transport.SendAsync(BuildRequest(), ct);
    }
}
```

---

## 9. Tests

- All tests live in the `tests/` folder
- Use `xUnit` with `[Fact]` and `[Theory]`
- Use `FluentAssertions` for assertions (`result.Should().Be(...)`)
- Use `NSubstitute` for mocking (`Substitute.For<IHttpEngine>()`)
- Arrange/Act/Assert structure with blank lines between sections
- Test method names: `MethodName_Scenario_ExpectedResult`

```csharp
[Fact]
public async Task SendAsync_WhenRequestIsNull_ThrowsArgumentNullException()
{
    // Arrange
    var transport = new HttpTransport(_httpClient, _logger);

    // Act
    var act = () => transport.SendAsync(null!, CancellationToken.None);

    // Assert
    await act.Should().ThrowAsync<ArgumentNullException>();
}
```

---

## 10. Comments and Documentation

- **Prefer self-documenting code over comments.** If you feel the urge to write
  an inline comment, first ask whether a better name would make the comment
  unnecessary. A well-named method or variable is always preferable to an
  explanation of a poorly named one.
- Inline comments are acceptable only when explaining **why** something is done,
  not **what** it does — the code itself should make the "what" obvious.
- Public types and public methods should have XML doc comments.
- Do not leave TODO comments in committed code — use TODO.md instead.

```csharp
// ❌ Comment explaining what the code does — rename instead
var x = 86400; // seconds in a day

// ✅ Descriptive name eliminates the need for the comment
var secondsPerDay = 86400;

// ✅ Comment explaining WHY — this is appropriate
// HttpClient reuse is intentional: instantiating per-request causes socket exhaustion.
private readonly HttpClient _httpClient;

/// <summary>
/// Sends an HTTP request and returns the full response including headers,
/// body, status code, and elapsed time.
/// </summary>
/// <param name="request">The request to send.</param>
/// <param name="ct">Cancellation token.</param>
public Task<HttpResponseModel> SendAsync(HttpRequestModel request, CancellationToken ct = default);
```
