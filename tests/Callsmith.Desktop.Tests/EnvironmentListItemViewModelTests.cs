using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class EnvironmentListItemViewModelTests
{
    [Fact]
    public void MoveVariable_MovesItemAndMarksEnvironmentDirty()
    {
        var vm = CreateEnvironmentVm("mock-var");
        vm.IsDirty.Should().BeFalse();

        var first = vm.Variables[0];

        vm.MoveVariable(first, 1);

        vm.Variables[1].Should().BeSameAs(first);
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void MoveVariable_WhenTargetIsOutOfRange_DoesNothing()
    {
        var vm = CreateEnvironmentVm("mock-var");
        var snapshot = vm.Variables.Select(v => v.Name).ToArray();

        vm.MoveVariable(vm.Variables[0], 99);

        vm.Variables.Select(v => v.Name).Should().Equal(snapshot);
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void MockDataFieldChange_UpdatesReferencedStaticPreviewToNewGeneratorType()
    {
        var vm = CreateEnvironmentVm("mock-var");
        SeedDynamicPreviewCaches(vm, "mock-var", "Internet", "Email");

        var mockVar = vm.Variables.Single(v => v.IsMockData);
        var testVar = vm.Variables.Single(v => v.Name == "test");

        var oldPreview = testVar.PreviewValue;
        oldPreview.Should().NotBeNull();
        oldPreview!.Should().Contain("@");

        mockVar.MockDataField = "Username";

        var updatedPreview = testVar.PreviewValue;
        updatedPreview.Should().NotBeNull();
        updatedPreview!.Should().NotContain("@");
    }

    [Fact]
    public void MockDataFieldChange_WithWhitespaceInVariableName_StillUpdatesReferencedStaticPreview()
    {
        var vm = CreateEnvironmentVm("mock-var ");
        SeedDynamicPreviewCaches(vm, "mock-var", "Internet", "Email");

        var mockVar = vm.Variables.Single(v => v.IsMockData);
        var testVar = vm.Variables.Single(v => v.Name == "test");

        var oldPreview = testVar.PreviewValue;
        oldPreview.Should().NotBeNull();
        oldPreview!.Should().Contain("@");

        mockVar.MockDataField = "Username";

        var updatedPreview = testVar.PreviewValue;
        updatedPreview.Should().NotBeNull();
        updatedPreview!.Should().NotContain("@");
    }

    private static EnvironmentListItemViewModel CreateEnvironmentVm(string mockVarName)
    {
        var model = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\env\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = mockVarName,
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.MockData,
                    MockDataCategory = "Internet",
                    MockDataField = "Email",
                },
                new EnvironmentVariable
                {
                    Name = "test",
                    Value = "{{mock-var}}",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        return new EnvironmentListItemViewModel(
            model,
            onRenameCommit: (_, _, _) => Task.CompletedTask,
            onDeleteRequest: (_, _) => Task.CompletedTask);
    }

    private static void SeedDynamicPreviewCaches(
        EnvironmentListItemViewModel vm,
        string key,
        string category,
        string field)
    {
        var entry = MockDataCatalog.All.Single(e => e.Category == category && e.Field == field);

        vm.SetDynamicPreviewValues(
            dynVars: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [key] = MockDataCatalog.Generate(category, field),
            },
            generators: new Dictionary<string, MockDataEntry>(StringComparer.Ordinal)
            {
                [key] = entry,
            });
    }
}
