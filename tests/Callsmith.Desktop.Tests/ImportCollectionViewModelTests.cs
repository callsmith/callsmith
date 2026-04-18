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
    public void Constructor_InitialStateHasNoErrors()
    {
        var sut = BuildSut();
        sut.ErrorMessage.Should().BeEmpty();
        sut.IsNonEmptyFolderWarningVisible.Should().BeFalse();
        sut.IsImporting.Should().BeFalse();
        sut.IsConfirmed.Should().BeFalse();
    }

    // ─── CanImport ────────────────────────────────────────────────────────────

    [Fact]
    public void ImportCommand_IsDisabled_WhenFilePathIsEmpty()
    {
        var sut = BuildSut();
        sut.FolderPath = @"/some/folder";
        sut.FilePaths = [];

        sut.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ImportCommand_IsDisabled_WhenFolderPathIsEmpty()
    {
        var sut = BuildSut();
        sut.FilePaths = ["/some/file.yaml"];
        sut.FolderPath = string.Empty;

        sut.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ImportCommand_IsEnabled_WhenBothPathsAreSet()
    {
        var sut = BuildSut();
        sut.FilePaths = ["/some/file.yaml"];
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
            sut.FilePaths = ["/fake/file.yaml"];
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
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
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
            sut.FilePaths = ["/fake/file.yaml"];
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
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
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
            sut.FilePaths = ["/fake/file.yaml"];
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
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
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
            sut.FilePaths = ["/fake/file.yaml"];
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
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
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
            sut.FilePaths = ["/fake/file.yaml"];
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
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .ThrowsAsync(new InvalidOperationException("Bad format"));

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePaths = ["/fake/file.yaml"];
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
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .ThrowsAsync(new InvalidOperationException("Bad format"));

        var tempDir = Directory.CreateTempSubdirectory("callsmith_import_test_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePaths = ["/fake/file.yaml"];
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
        sut.FilePaths = ["/some/file.yaml"];

        sut.ImportCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ImportCommand_IsDisabled_WhenImportIntoCurrent_AndNoFilePath()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        var sut = new ImportCollectionViewModel(svc, "/col");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = [];

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
                Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test", RootRequests = [], RootFolders = [],
               ItemOrder = [], Environments = [], GlobalDynamicVars = [],
           });

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/fake/file.yaml"];

        await sut.ImportCommand.ExecuteAsync(null);

        await svc.Received(1).ImportIntoCollectionAsync(
            "/fake/file.yaml",
            "/my/collection",
            "/my/collection",
            Arg.Any<CollectionImportOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_WithSubFolder_PassesCorrectAbsolutePath()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test", RootRequests = [], RootFolders = [],
               ItemOrder = [], Environments = [], GlobalDynamicVars = [],
           });

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/fake/file.yaml"];
        sut.SubFolderPath = "Orders/Internal";

        await sut.ImportCommand.ExecuteAsync(null);

        var expectedTarget = Path.GetFullPath(Path.Combine("/my/collection", "Orders/Internal"));
        await svc.Received(1).ImportIntoCollectionAsync(
            "/fake/file.yaml",
            "/my/collection",
            expectedTarget,
            Arg.Any<CollectionImportOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_SetsImportedIntoCurrentCollectionAndIsConfirmed()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(new ImportedCollection
           {
               Name = "Test", RootRequests = [], RootFolders = [],
               ItemOrder = [], Environments = [], GlobalDynamicVars = [],
           });

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/fake/file.yaml"];

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
                    Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
               .Returns(new ImportedCollection
               {
                   Name = "Test", RootRequests = [], RootFolders = [],
                   ItemOrder = [], Environments = [], GlobalDynamicVars = [],
               });

            var sut = new ImportCollectionViewModel(svc, tempDir.FullName);
            sut.IsImportIntoCurrentCollection = true;
            sut.FilePaths = ["/fake/file.yaml"];

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
        sut.FilePaths = ["/fake/file.yaml"];
        sut.SubFolderPath = "/absolute/path";

        await sut.ImportCommand.ExecuteAsync(null);

        sut.IsConfirmed.Should().BeFalse();
        sut.ErrorMessage.Should().NotBeEmpty();
        await svc.DidNotReceive().ImportIntoCollectionAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_RejectsDoubleDotSegments()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/fake/file.yaml"];
        sut.SubFolderPath = "valid/../../../etc/passwd";

        await sut.ImportCommand.ExecuteAsync(null);

        sut.IsConfirmed.Should().BeFalse();
        sut.ErrorMessage.Should().NotBeEmpty();
        await svc.DidNotReceive().ImportIntoCollectionAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
    }

    // ─── Multi-file: FilePath display property ────────────────────────────────

    [Fact]
    public void FilePath_IsEmpty_WhenNoFilesSelected()
    {
        var sut = BuildSut();
        sut.FilePath.Should().BeEmpty();
    }

    [Fact]
    public void FilePath_ShowsPath_WhenSingleFileSelected()
    {
        var sut = BuildSut();
        sut.FilePaths = ["/some/file.yaml"];
        sut.FilePath.Should().Be("/some/file.yaml");
    }

    [Fact]
    public void FilePath_ShowsCount_WhenMultipleFilesSelected()
    {
        var sut = BuildSut();
        sut.FilePaths = ["/a.yaml", "/b.json", "/c.yaml"];
        sut.FilePath.Should().Be("3 files selected");
    }

    // ─── Multi-file: new-collection mode ─────────────────────────────────────

    [Fact]
    public async Task ImportCommand_MultiFile_NewCollectionMode_FirstFileCreatesCollection()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml", ".json"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());
        svc.ImportIntoCollectionAsync(Arg.Any<string>(), Arg.Any<string>(),
               Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var tempDir = Directory.CreateTempSubdirectory("callsmith_multi_import_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePaths = ["/first.yaml", "/second.json"];
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            // First file → ImportToFolderAsync
            await svc.Received(1).ImportToFolderAsync("/first.yaml", tempDir.FullName, Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportCommand_MultiFile_NewCollectionMode_SubsequentFilesMergedIn()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml", ".json"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());
        svc.ImportIntoCollectionAsync(Arg.Any<string>(), Arg.Any<string>(),
               Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var tempDir = Directory.CreateTempSubdirectory("callsmith_multi_import_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePaths = ["/first.yaml", "/second.json", "/third.yaml"];
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            // Second and third files → ImportIntoCollectionAsync
            await svc.Received(1).ImportIntoCollectionAsync(
                "/second.json", tempDir.FullName, tempDir.FullName, Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
            await svc.Received(1).ImportIntoCollectionAsync(
                "/third.yaml", tempDir.FullName, tempDir.FullName, Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
            // ImportToFolderAsync called exactly once (for the first file only)
            await svc.Received(1).ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportCommand_MultiFile_NewCollectionMode_SingleFile_DoesNotCallMerge()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var tempDir = Directory.CreateTempSubdirectory("callsmith_single_import_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePaths = ["/only.yaml"];
            sut.FolderPath = tempDir.FullName;

            await sut.ImportCommand.ExecuteAsync(null);

            await svc.Received(1).ImportToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
            await svc.DidNotReceive().ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ─── Multi-file: import-into-current mode ────────────────────────────────

    [Fact]
    public async Task ImportCommand_MultiFile_InImportIntoCurrentMode_AllFilesAreMerged()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml", ".json"]);
        svc.ImportIntoCollectionAsync(Arg.Any<string>(), Arg.Any<string>(),
               Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/alpha.yaml", "/beta.json"];

        await sut.ImportCommand.ExecuteAsync(null);

        await svc.Received(1).ImportIntoCollectionAsync(
            "/alpha.yaml", "/my/collection", "/my/collection", Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        await svc.Received(1).ImportIntoCollectionAsync(
            "/beta.json", "/my/collection", "/my/collection", Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        await svc.DidNotReceive().ImportToFolderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        sut.IsConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task ImportCommand_MultiFile_InImportIntoCurrentMode_UsesSubFolderForAllFiles()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(Arg.Any<string>(), Arg.Any<string>(),
               Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var sut = new ImportCollectionViewModel(svc, "/col");
        sut.IsImportIntoCurrentCollection = true;
        sut.SubFolderPath = "Orders";
        sut.FilePaths = ["/a.yaml", "/b.yaml"];

        await sut.ImportCommand.ExecuteAsync(null);

        var expectedTarget = Path.GetFullPath(Path.Combine("/col", "Orders"));
        await svc.Received(1).ImportIntoCollectionAsync(
            "/a.yaml", "/col", expectedTarget, Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
        await svc.Received(1).ImportIntoCollectionAsync(
            "/b.yaml", "/col", expectedTarget, Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>());
    }

    // ─── Advanced options defaults ────────────────────────────────────────────

    [Fact]
    public void AdvancedOptions_DefaultMergeStrategy_IsSkip()
    {
        var sut = BuildSut();
        sut.SelectedMergeStrategy.Should().Be(ImportMergeStrategy.Skip);
        sut.IsMergeStrategySkip.Should().BeTrue();
        sut.IsMergeStrategyTakeBoth.Should().BeFalse();
        sut.IsMergeStrategyReplace.Should().BeFalse();
    }

    [Fact]
    public void AdvancedOptions_DefaultBaseUrlVariableName_IsBaseUrl()
    {
        var sut = BuildSut();
        sut.BaseUrlVariableName.Should().Be("baseUrl");
    }

    [Fact]
    public void AdvancedOptions_IsAdvancedOptionsExpanded_DefaultIsFalse()
    {
        var sut = BuildSut();
        sut.IsAdvancedOptionsExpanded.Should().BeFalse();
    }

    [Fact]
    public void AdvancedOptions_ToggleAdvancedOptionsCommand_TogglesExpansion()
    {
        var sut = BuildSut();
        sut.ToggleAdvancedOptionsCommand.Execute(null);
        sut.IsAdvancedOptionsExpanded.Should().BeTrue();
        sut.ToggleAdvancedOptionsCommand.Execute(null);
        sut.IsAdvancedOptionsExpanded.Should().BeFalse();
    }

    [Fact]
    public void AdvancedOptions_SettingMergeStrategyUpdatesHelperProperties()
    {
        var sut = BuildSut();
        sut.SelectedMergeStrategy = ImportMergeStrategy.Replace;
        sut.IsMergeStrategySkip.Should().BeFalse();
        sut.IsMergeStrategyTakeBoth.Should().BeFalse();
        sut.IsMergeStrategyReplace.Should().BeTrue();

        sut.IsMergeStrategyTakeBoth = true;
        sut.SelectedMergeStrategy.Should().Be(ImportMergeStrategy.TakeBoth);
        sut.IsMergeStrategyTakeBoth.Should().BeTrue();
        sut.IsMergeStrategySkip.Should().BeFalse();
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_PassesMergeStrategyInOptions()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/fake/file.yaml"];
        sut.SelectedMergeStrategy = ImportMergeStrategy.Replace;

        await sut.ImportCommand.ExecuteAsync(null);

        await svc.Received(1).ImportIntoCollectionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<CollectionImportOptions?>(o => o != null && o.MergeStrategy == ImportMergeStrategy.Replace),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_InImportIntoCurrentMode_PassesBaseUrlVariableNameInOptions()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportIntoCollectionAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var sut = new ImportCollectionViewModel(svc, "/my/collection");
        sut.IsImportIntoCurrentCollection = true;
        sut.FilePaths = ["/fake/file.yaml"];
        sut.BaseUrlVariableName = "apiRoot";

        await sut.ImportCommand.ExecuteAsync(null);

        await svc.Received(1).ImportIntoCollectionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<CollectionImportOptions?>(o => o != null && o.BaseUrlVariableName == "apiRoot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_NewCollectionMode_PassesBaseUrlVariableNameInOptions()
    {
        var svc = Substitute.For<ICollectionImportService>();
        svc.SupportedFileExtensions.Returns([".yaml"]);
        svc.ImportToFolderAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CollectionImportOptions?>(), Arg.Any<CancellationToken>())
           .Returns(EmptyCollection());

        var tempDir = Directory.CreateTempSubdirectory("callsmith_baseurl_new_");
        try
        {
            var sut = new ImportCollectionViewModel(svc);
            sut.FilePaths = ["/fake/file.yaml"];
            sut.FolderPath = tempDir.FullName;
            sut.BaseUrlVariableName = "apiHost";

            await sut.ImportCommand.ExecuteAsync(null);

            await svc.Received(1).ImportToFolderAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<CollectionImportOptions?>(o => o != null && o.BaseUrlVariableName == "apiHost"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ImportedCollection EmptyCollection() =>
        new() { Name = "Test", RootRequests = [], RootFolders = [], ItemOrder = [], Environments = [], GlobalDynamicVars = [] };
}
