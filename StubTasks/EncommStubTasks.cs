// Stub MSBuild tasks used by Directory.Build.targets.
//
// These exist ONLY because Windows App SDK 1.5+ buildTransitive targets
// reference MSBuild tasks that ship only with Visual Studio's
// AppxPackage component. When building with the .NET SDK alone (no
// Visual Studio installed), these tasks fail to load.
//
// Encomm-AI-Browser does NOT produce an MSIX package
// (WindowsPackageType=None) and does NOT need PRI generation in Phase 1.
// We therefore install empty task stubs here that satisfy the UsingTask
// declarations in WinAppSDK targets so that the build can complete.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Microsoft.Build.AppxPackage
{
    public class RemovePayloadDuplicates : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        [Output] public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute() { Filtered = Inputs ?? Array.Empty<ITaskItem>(); return true; }
    }

    public class ExpandPayloadDirectories : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string[] TargetDirsToExclude { get; set; } = Array.Empty<string>();
        public string[] TargetFilesToExclude { get; set; } = Array.Empty<string>();
        [Output] public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { Expanded = Inputs ?? Array.Empty<ITaskItem>(); return true; }
    }

    public class GetDefaultResourceLanguage : Task
    {
        public ITaskItem[] Languages { get; set; } = Array.Empty<ITaskItem>();
        public string DefaultLanguage { get; set; } = "en-US";
        public ITaskItem[] SourceAppxManifest { get; set; } = Array.Empty<ITaskItem>();
        [Output] public string DefaultResourceLanguage { get; set; } = "en-US";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public override bool Execute() { DefaultResourceLanguage = string.IsNullOrEmpty(DefaultLanguage) ? "en-US" : DefaultLanguage; return true; }
    }

    public class GetPackageArchitecture : Task
    {
        public string Platform { get; set; } = "";
        public string PackageArchitecture { get; set; } = "x64";
        public string ProjectArchitecture { get; set; } = "x64";
        public string RecursiveProjectArchitecture { get; set; } = "x64";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public override bool Execute() { PackageArchitecture = string.IsNullOrEmpty(Platform) ? "x64" : Platform; ProjectArchitecture = PackageArchitecture; RecursiveProjectArchitecture = PackageArchitecture; return true; }
    }

    public class GetSdkFileFullPath : Task
    {
        public string File { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string FullFilePath { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string FileArchitecture { get; set; } = "";
        public string MSBuildExtensionsPath64Exists { get; set; } = "";
        public string RequireExeExtension { get; set; } = "";
        public string SDKIdentifier { get; set; } = "";
        public string SDKVersion { get; set; } = "";
        public string TargetPlatformIdentifier { get; set; } = "";
        public string TargetPlatformMinVersion { get; set; } = "";
        public string TargetPlatformSdkRootOverride { get; set; } = "";
        public string TargetPlatformVersion { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public override bool Execute() { FullFilePath = FileName; FullPath = FileName; return true; }
    }

    public class GetSdkPropertyValue : Task
    {
        public string PropertyName { get; set; } = "";
        public string PropertyValue { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string SDKIdentifier { get; set; } = "";
        public string SDKVersion { get; set; } = "";
        public string TargetPlatformIdentifier { get; set; } = "";
        public string TargetPlatformVersion { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public override bool Execute() { return true; }
    }

    public class RemoveRedundantXamlFilesFromSdkPayload : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { Output = Inputs; return true; }
    }

    public class ValidateConfiguration : Task
    {
        public string Configuration { get; set; } = "";
        public string Platform { get; set; } = "";
        public string ProjectFile { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public override bool Execute() { return true; }
    }
}

namespace Microsoft.Build.Packaging.Pri.Tasks
{
    public class ExpandPriContent : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string AdditionalMakepriExeParameters { get; set; } = "";
        public string ExcludeXamlFromLibraryLayoutsWhenXbfIsPresent { get; set; } = "";
        public string IntermediateDirectory { get; set; } = "";
        public string MakePriExeFullPath { get; set; } = "";
        public string MakePriExtensionPath { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        [Output] public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();
        [Output] public ITaskItem[] IntermediateFileWrites { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { Expanded = Inputs; IntermediateFileWrites = Array.Empty<ITaskItem>(); return true; }
    }

    public class CreatePriConfigXmlForSplitting : Task
    {
        public ITaskItem[] InputFiles { get; set; } = Array.Empty<ITaskItem>();
        public string OutputFile { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public override bool Execute() { return true; }
    }

    public class CreatePriConfigXmlForMainPackageFileMap : Task
    {
        public ITaskItem[] InputFiles { get; set; } = Array.Empty<ITaskItem>();
        public string OutputFile { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public override bool Execute() { return true; }
    }

    public class CreatePriConfigXmlForFullIndex : Task
    {
        public ITaskItem[] InputFiles { get; set; } = Array.Empty<ITaskItem>();
        public string OutputFile { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string AdditionalResourceResFiles { get; set; } = "";
        public string DefaultResourceLanguage { get; set; } = "";
        public string DefaultResourceQualifiers { get; set; } = "";
        public string EmbedFileResfilePath { get; set; } = "";
        public string IntermediateExtension { get; set; } = "";
        public string LayoutResfilesPath { get; set; } = "";
        public string PriConfigXmlDefaultSnippetPath { get; set; } = "";
        public string PriConfigXmlPath { get; set; } = "";
        public string PriInitialPath { get; set; } = "";
        public string PriResfilesPath { get; set; } = "";
        public string ResourcesResfilesPath { get; set; } = "";
        public string TargetPlatformIdentifier { get; set; } = "";
        public string TargetPlatformVersion { get; set; } = "";
        public override bool Execute() { return true; }
    }

    public class CreatePriFilesForPortableLibraries : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string AdditionalMakepriExeParameters { get; set; } = "";
        public string AppxBundleAutoResourcePackageQualifiers { get; set; } = "";
        public string ContentToIndex { get; set; } = "";
        public string DefaultResourceLanguage { get; set; } = "";
        public string DefaultResourceQualifiers { get; set; } = "";
        public string IntermediateDirectory { get; set; } = "";
        public string IntermediateExtension { get; set; } = "";
        public string MakePriExeFullPath { get; set; } = "";
        public string MakePriExtensionPath { get; set; } = "";
        public string SkipIntermediatePriGenerationForResourceFiles { get; set; } = "";
        public string TargetPlatformIdentifier { get; set; } = "";
        public string TargetPlatformVersion { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { return true; }
    }

    public class GenerateMainPriConfigurationFile : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string OutputFile { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public override bool Execute() { return true; }
    }

    public class GeneratePriConfigurationFiles : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string EmbedFileResfilePath { get; set; } = "";
        public string EmbedFiles { get; set; } = "";
        public string ExcludedLayoutResfilesPath { get; set; } = "";
        public string FilteredLayoutResfilesPath { get; set; } = "";
        public string IntermediateExtension { get; set; } = "";
        public string LayoutFiles { get; set; } = "";
        public string PriFiles { get; set; } = "";
        public string PriResfilesPath { get; set; } = "";
        public string PRIResourceFiles { get; set; } = "";
        public string ResourcesResfilesPath { get; set; } = "";
        public string UnfilteredLayoutResfilesPath { get; set; } = "";
        public string UnprocessedResourceFiles_OtherLanguages { get; set; } = "";
        public string AdditionalResourceResFiles { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { return true; }
    }

    public class GenerateProjectPriFile : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string OutputFile { get; set; } = "";
        public string OutputFileName { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string AdditionalMakepriExeParameters { get; set; } = "";
        public string AppxBundleAutoResourcePackageQualifiers { get; set; } = "";
        public string IndexFilesForQualifiersCollection { get; set; } = "";
        public string InsertReverseMap { get; set; } = "";
        public string IntermediateExtension { get; set; } = "";
        public string MakePriExeFullPath { get; set; } = "";
        public string MakePriExtensionPath { get; set; } = "";
        public string MultipleQualifiersPerDimensionFoundPath { get; set; } = "";
        public string PriConfigXmlPath { get; set; } = "";
        public string ProjectDirectory { get; set; } = "";
        public string ProjectPriIndexName { get; set; } = "";
        public string QualifiersPath { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { return true; }
    }

    public class RemoveDuplicatePriFiles : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { Output = Inputs; return true; }
    }

    public class UpdateMainPackageFileMap : Task
    {
        public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
        public string VsTelemetrySession { get; set; } = "";
        public string Condition { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Platform { get; set; } = "";
        [Output] public ITaskItem[] Output { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute() { return true; }
    }
}