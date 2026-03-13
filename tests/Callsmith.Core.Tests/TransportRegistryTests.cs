using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Core.Tests;

public sealed class TransportRegistryTests
{
    // ---------------------------------------------------------------------------
    // Register
    // ---------------------------------------------------------------------------

    [Fact]
    public void Register_WhenTransportIsNull_ThrowsArgumentNullException()
    {
        var registry = new TransportRegistry();

        var act = () => registry.Register(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_RegistersTransportForAllDeclaredSchemes()
    {
        var registry = new TransportRegistry();
        var transport = MakeTransport("http", "https");

        registry.Register(transport);

        registry.Resolve(Request("http://example.com")).Should().BeSameAs(transport);
        registry.Resolve(Request("https://example.com")).Should().BeSameAs(transport);
    }

    // ---------------------------------------------------------------------------
    // Resolve
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var registry = new TransportRegistry();

        var act = () => registry.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_WhenNoTransportRegistered_ThrowsInvalidOperationException()
    {
        var registry = new TransportRegistry();
        var request = Request("https://example.com");

        var act = () => registry.Resolve(request);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*https*");
    }

    [Fact]
    public void Resolve_WhenUrlIsNotAbsolute_ThrowsInvalidOperationException()
    {
        var registry = new TransportRegistry();
        var request = Request("not-a-valid-url");

        var act = () => registry.Resolve(request);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolve_ReturnsCorrectTransportForScheme()
    {
        var registry = new TransportRegistry();
        var httpTransport = MakeTransport("http");
        var wsTransport = MakeTransport("ws");

        registry.Register(httpTransport);
        registry.Register(wsTransport);

        registry.Resolve(Request("http://example.com")).Should().BeSameAs(httpTransport);
        registry.Resolve(Request("ws://example.com")).Should().BeSameAs(wsTransport);
    }

    [Fact]
    public void Resolve_SchemeMatchingIsCaseInsensitive()
    {
        var registry = new TransportRegistry();
        var transport = MakeTransport("HTTPS");

        registry.Register(transport);

        registry.Resolve(Request("https://example.com")).Should().BeSameAs(transport);
    }

    [Fact]
    public void Register_OverwritesPreviousTransportForSameScheme()
    {
        var registry = new TransportRegistry();
        var first = MakeTransport("http");
        var second = MakeTransport("http");

        registry.Register(first);
        registry.Register(second);

        registry.Resolve(Request("http://example.com")).Should().BeSameAs(second);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static RequestModel Request(string url) =>
        new() { Method = HttpMethod.Get, Url = url };

    private static ITransport MakeTransport(params string[] schemes)
    {
        var transport = Substitute.For<ITransport>();
        transport.SupportedSchemes.Returns(schemes);
        return transport;
    }
}
