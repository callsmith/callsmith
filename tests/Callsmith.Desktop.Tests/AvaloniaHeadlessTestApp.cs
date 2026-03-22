using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(Callsmith.Desktop.Tests.AvaloniaHeadlessTestApp))]

namespace Callsmith.Desktop.Tests;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Callsmith.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}