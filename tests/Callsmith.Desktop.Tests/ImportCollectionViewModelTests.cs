using Callsmith.Core.Abstractions;
using Callsmith.Core.Import;
using Callsmith.Desktop.ViewModels;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="ImportCollectionViewModel"/>:
/// import-type defaults, non-empty folder warning, successful import, and parse-error handling.
/// </summary>
public sealed class ImportCollectionViewModelTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ImportCollectionViewModel BuildSut(
        ICollectionImportService? importService = null)
    {
        var svc = importService ?? Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml", ".yml", ".json"]);
        return new ImportCollectionViewModel(svc);
    }

    // ─── Constructor / defaults ───────────────────────────────────────────────

    [Fact]
    public void Constructor_DefaultsSelectedImportTypeToPostman()
    {
        var sut = BuildSut();
        sut.SelectedImportType.Name.Should().Be("Postman");
        sut.SelectedImportType.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ImportTypeOptions_HasThreeEntries()
    {
        var sut = BuildSut();
        sut.ImportTypeOptions.Should().HaveCount(3);
    }

    [Fact]
    public void Constructor_InsomniaAndPostmanAreEnabled()
    {
        var sut = BuildSut();
        sut.ImportTypeOptions.Where(o => o.IsEnabled)
            .Select(o => o.Name)
            .Should()
            .BeEquivalentTo(["Insomnia", "Postman"]);
    }

    [Fact]
    public void Constructor_InitialStateHasNoErrors()
    {
        var sut = BuildSut();
        sut.ErrorMessage.Should().BeEmpty();
        sut.IsNonEmptyFolderWarningVisible.Should().BeFalse();
        sut.IsImporting.Should().BeFalse();
        sut.IsConfirmed.Should().BeFalse();
    }

    // ─── Import-type selection ────────────────────────────────────────────────

    [Fact]
    public void SelectingPostman_KeepsPostman()
    {
        var sut = BuildSut();
        var postman = sut.ImportTypeOptions.First(o => o.Name == "Postman");

        sut.SelectedImportType = postman;

        sut.SelectedImportType.Name.Should().Be("Postman");
    }

    [Fact]
    public void SelectingDisabledImportType_RevertsToFirstEnabled()
    {
        var sut = BuildSut();
        var hoppscotch = sut.ImportTypeOptions.First(o => o.Name == "Hoppscotch");

        sut.SelectedImportType = hoppscotch;

        sut.SelectedImportType.Name.Should().Be("Postman");
    }

    [Fact]
    public void SelectingInsomnia_KeepsInsomnia()
    {
        var sut = BuildSut();
        var insomnia = sut.ImportTypeOptions.First(o => o.Name == "Insomnia");

        sut.SelectedImportType = insomnia;

        sut.SelectedImportType.Name.Should().Be("Insomnia");
    }

    // ─── CanImport ────────────────────────────────────────────────────────────

    [Fact]
    public void ImportCommand_IsDisabled_WhenFilePathIsEmpty()
    {
        var sut = BuildSut();
        sut.FolderPath = @"/some/folder";
        sut.FilePath = string.Empty;

        sut.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ImportCommand_IsDisabled_WhenFolderPathIsEmpty()
    {
        var sut = BuildSut();
        sut.FilePath = @"/some/file.yaml";
        sut.FolderPath = string.Empty;

        sut.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ImportCommand_IsEnabled_WhenBothPathsAreSet()
    {
        var sut = BuildSut();
        sut.FilePath = @"/some/file.yaml";
        sut.FolderPath = @"/some/folder";

        sut.ImportCommand.CanExecute(null).Should().BeTrue();
    }

    // ─── Non-empty folder warning ─────────────────────────────────────────────

    [Fact]
    public async Task ImportCommand_WhenFolderIsNonEmpty_ShowsWarning()
    {
        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            // Put a file in the temp directory to make it non-empty.
            File.WriteAllText(Path.Combine(tempDir.FullName, "existing.txt"), "data");

            var sut = BuildSut();
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            sut.IsNonEmptyFolderWarningVisible.Should().BeTrue();
            sut.IsConfirmed.Should().BeFalse();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportCommand_WhenFolderIsEmpty_DoesNotShowWarning()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test",
               RootRequests = [],
               RootFolders = [],
               ItemOrder = [],
               Environments = [],
               GlobalDynamicVars = [],
           });

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            sut.IsNonEmptyFolderWarningVisible.Should().BeFalse();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CancelWarningCommand_HidesWarning()
    {
        var sut = BuildSut();
        sut.IsNonEmptyFolderWarningVisible = true;

        sut.CancelWarningCommand.Execute(null);

        sut.IsNonEmptyFolderWarningVisible.Should().BeFalse();
    }

    // ─── Successful import ────────────────────────────────────────────────────

    [Fact]
    public async Task ImportCommand_OnSuccess_SetsIsConfirmedAndResultFolderPath()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test",
               RootRequests = [],
               RootFolders = [],
               ItemOrder = [],
               Environments = [],
               GlobalDynamicVars = [],
           });

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            sut.IsConfirmed.Should().BeTrue();
            sut.ResultFolderPath.Should().Be(tempDir.FullName);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportCommand_OnSuccess_RaisesCloseRequested()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test",
               RootRequests = [],
               RootFolders = [],
               ItemOrder = [],
               Environments = [],
               GlobalDynamicVars = [],
           });

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            var closeRaised = 0;
            sut.CloseRequested += (_, _) => Interlocked.Exchange(ref closeRaised, 1);

            await sut.ImportCommand.ExecuteAsync(null);

            (Volatile.Read(ref closeRaised) == 1).Should().BeTrue();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProceedAnywayCommand_OnSuccess_SetsIsConfirmed()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test",
               RootRequests = [],
               RootFolders = [],
               ItemOrder = [],
               Environments = [],
               GlobalDynamicVars = [],
           });

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            // Make folder non-empty first to simulate the warning scenario.
            File.WriteAllText(Path.Combine(tempDir.FullName, "existing.txt"), "data");

            var sut = new ImportCollectionViewModel(svc);
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            // First call: triggers warning.
            await sut.ImportCommand.ExecuteAsync(null);
            sut.IsNonEmptyFolderWarningVisible.Should().BeTrue();

            // Now proceed anyway.
            await sut.ProceedAnywayCommand.ExecuteAsync(null);

            sut.IsConfirmed.Should().BeTrue();
            sut.IsNonEmptyFolderWarningVisible.Should().BeFalse();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ─── Parse failure (no-op) ────────────────────────────────────────────────

    [Fact]
    public async Task ImportCommand_WhenImportThrows_SetsErrorMessageAndDoesNotConfirm()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .ThrowsAsync(new InvalidOperationException("Bad format"));

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            sut.IsConfirmed.Should().BeFalse();
            sut.ErrorMessage.Should().Contain("Bad format");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportCommand_WhenImportThrows_DoesNotRaiseCloseRequested()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .ThrowsAsync(new InvalidOperationException("Bad format"));

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePath = @"/fake/file.yaml";
            sut.FolderPath = tempDir.FullName;

            var closeRaised = 0;
            sut.CloseRequested += (_, _) => Interlocked.Exchange(ref closeRaised, 1);

            await sut.ImportCommand.ExecuteAsync(null);

            (Volatile.Read(ref closeRaised) == 1).Should().BeFalse();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ─── Cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public void CancelCommand_SetsIsConfirmedFalseAndRaisesCloseRequested()
    {
        var sut = BuildSut();
        var closeRaised = false;
        sut.CloseRequested += (_, _) => closeRaised = true;

        sut.CancelCommand.Execute(null);

        sut.IsConfirmed.Should().BeFalse();
        closeRaised.Should().BeTrue();
    }

    // ─── HasCurrentCollectionOption ───────────────────────────────────────────

    [Fact]
    public void HasCurrentCollectionOption_IsFalse_WhenNoCollectionPath()
    {
        var sut = BuildSut();
        sut.HasCurrentCollectionOption.Should().BeFalse();
    }

    [Fact]
    public void HasCurrentCollectionOption_IsTrue_WhenCollectionPathProvided()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        var sut = new ImportCollectionViewModel(svc, "/some/collection");
        sut.HasCurrentCollectionOption.Should().BeTrue();
    }

    [Fact]
    public void IsImportIntoCurrentCollection_DefaultsFalse()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        var sut = new ImportCollectionViewModel(svc, "/some/collection");
        sut.IsImportIntoCurrentCollection.Should().BeFalse();
    }

    [Fact]
    public void IsImportIntoCurrentCollection_CannotBeSetTrue_WhenNoCollection()
    {
        var sut = BuildSut();
        sut.IsImportIntoCurrentCollection = true;
        sut.IsImportIntoCurrentCollection.Should().BeFalse();
    }

    // ─── CanImport in import-into-current mode ────────────────────────────────

    [Fact]
    public void ImportCommand_IsEnabled_WhenImportIntoCurrent_AndFilePathSet()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        var sut = new ImportCollectionViewModel(svc, "/col");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = "/some/file.yaml";

        sut.ImportCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ImportCommand_IsDisabled_WhenImportIntoCurrent_AndNoFilePath()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        var sut = new ImportCollectionViewModel(svc, "/col");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = string.Empty;

        sut.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    // ─── Import-into-current: success ─────────────────────────────────────────

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_CallsImportIntoCollectionAsync()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test", RootRequests = [], RootFolders = [],
               ItemOrder = [], Environments = [], GlobalDynamicVars = [],
           });

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = "/fake/file.yaml";

        await sut.ImportCommand.ExecuteAsync(null);

        await svc.Received(1).ImportIntoCollectionAsync(
            "/fake/file.yaml",
            "/my/collection",
            "/my/collection",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_WithSubFolder_PassesCorrectAbsolutePath()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test", RootRequests = [], RootFolders = [],
               ItemOrder = [], Environments = [], GlobalDynamicVars = [],
           });

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = "/fake/file.yaml";
        sut.SubFolderPath = "Orders/Internal";

        await sut.ImportCommand.ExecuteAsync(null);

        var expectedTarget = Path.Combine("/my/collection", "Orders/Internal");
        await svc.Received(1).ImportIntoCollectionAsync(
            "/fake/file.yaml",
            "/my/collection",
            expectedTarget,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_SetsImportedIntoCurrentCollectionAndIsConfirmed()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test", RootRequests = [], RootFolders = [],
               ItemOrder = [], Environments = [], GlobalDynamicVars = [],
           });

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = "/fake/file.yaml";

        await sut.ImportCommand.ExecuteAsync(null);

        sut.IsConfirmed.Should().BeTrue();
        sut.ImportedIntoCurrentCollection.Should().BeTrue();
        sut.ResultFolderPath.Should().Be("/my/collection");
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_DoesNotShowNonEmptyFolderWarning()
    {
        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_into_col_test_");
        try
        {
            // Folder is non-empty — warning must NOT appear in import-into-current mode.
            File.WriteAllText(Path.Combine(tempDir.FullName, "existing.txt"), "data");

            var svc = Substitute.For<ICollectionImportService>();
            svc.SupportedFileExtensions.Returns([".yaml"]);
            svc.ImportIntoCollectionAsync(
                    Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(new ImportedCollection
               {
                   Name = "Test", RootRequests = [], RootFolders = [],
                   ItemOrder = [], Environments = [], GlobalDynamicVars = [],
               });

            var sut = new ImportCollectionViewModel(svc, tempDir.FullName);
            sut.IsImportIntoCurrentCollection = true;
            sut.FilePath = "/fake/file.yaml";

            await sut.ImportCommand.ExecuteAsync(null);

            sut.IsNonEmptyFolderWarningVisible.Should().BeFalse();
            sut.IsConfirmed.Should().BeTrue();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ─── Import-into-current: invalid sub-folder path ─────────────────────────

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_RejectsRootedSubFolderPath()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = "/fake/file.yaml";
        sut.SubFolderPath = "/absolute/path";

        await sut.ImportCommand.ExecuteAsync(null);

        sut.IsConfirmed.Should().BeFalse();
        sut.ErrorMessage.Should().NotBeEmpty();
        await svc.DidNotReceive().ImportIntoCollectionAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_RejectsDoubleDotSegments()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePath = "/fake/file.yaml";
        sut.SubFolderPath = "valid/../../../etc/passwd";

        await sut.ImportCommand.ExecuteAsync(null);

        sut.IsConfirmed.Should().BeFalse();
        sut.ErrorMessage.Should().NotBeEmpty();
        await svc.DidNotReceive().ImportIntoCollectionAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
