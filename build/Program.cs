#nullable enable

using System;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Common.Tools.DotNet.Pack;
using Cake.Common.Tools.DotNet.Restore;
using Cake.Common.Tools.DotNet.Test;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;

return new CakeHost()
    .UseContext<BuildContext>()
    .Run(args);

public class BuildContext : FrostingContext
{
    public new string Configuration { get; }
    public string Solution { get; }
    public string BuildNumber { get; }
    public string? VersionSuffix { get; }
    public DirectoryPath Artifacts { get; }
    public string? NuGetSource { get; }
    public string? NuGetApiKey { get; }

    public string[] SourceProjects { get; } =
    [
        "src/Pondhawk.Logging/Pondhawk.Logging.csproj",
        "src/Pondhawk.Logging.Watch/Pondhawk.Logging.Watch.csproj",
        "src/Pondhawk.Logging.Console/Pondhawk.Logging.Console.csproj"
    ];

    public BuildContext(ICakeContext context) : base(context)
    {
        Configuration = context.Argument("configuration", "Release");
        Solution = context.Argument("solution", "pondhawk-logging.slnx");
        BuildNumber = context.Argument("build-number",
            context.Environment.GetEnvironmentVariable("BUILD_NUMBER") ?? "0");
        VersionSuffix = context.Argument<string?>("version-suffix", null)
            ?? context.Environment.GetEnvironmentVariable("VERSION_SUFFIX");
        if (BuildNumber == "0" && VersionSuffix is null)
            VersionSuffix = "local";
        Artifacts = context.Argument("artifacts", "artifacts");
        NuGetSource = context.Argument<string?>("nuget-source", null)
            ?? context.Environment.GetEnvironmentVariable("NUGET_SOURCE");
        NuGetApiKey = context.Argument<string?>("nuget-api-key", null)
            ?? context.Environment.GetEnvironmentVariable("NUGET_API_KEY");
    }

    /// <summary>
    /// The version inputs every dotnet invocation gets. Only the build number and suffix travel on the
    /// command line; each project derives its own version from its own version.json (see
    /// src/Directory.Build.props), so a project reference is never evaluated at the referencing project's
    /// version.
    /// </summary>
    public DotNetMSBuildSettings VersionProperties()
    {
        var settings = new DotNetMSBuildSettings().WithProperty("BuildNumber", BuildNumber);

        if (VersionSuffix is not null)
            settings = settings.WithProperty("VersionSuffix", VersionSuffix);

        return settings;
    }

    public string GetProjectVersion(string projectPath)
    {
        var projectDir = System.IO.Path.GetDirectoryName(projectPath)!;
        var versionFile = System.IO.Path.Combine(projectDir, "version.json");
        var json = System.IO.File.ReadAllText(versionFile);
        var doc = JsonDocument.Parse(json);
        var majorMinor = doc.RootElement.GetProperty("version").GetString()
            ?? throw new System.InvalidOperationException($"Missing 'version' in {versionFile}");
        var version = $"{majorMinor}.{BuildNumber}";
        if (VersionSuffix is not null)
            version = $"{version}-{VersionSuffix}";
        return version;
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetClean(context.Solution, new DotNetCleanSettings
        {
            Configuration = context.Configuration
        });

        context.CleanDirectory(context.Artifacts);
    }
}

[TaskName("Restore")]
[IsDependentOn(typeof(CleanTask))]
public sealed class RestoreTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetRestore(context.Solution, new DotNetRestoreSettings());
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(RestoreTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetBuild(context.Solution, new DotNetBuildSettings
        {
            Configuration = context.Configuration,
            NoRestore = true,
            MSBuildSettings = context.VersionProperties()
        });
    }
}

[TaskName("Test")]
[IsDependentOn(typeof(BuildTask))]
public sealed class TestTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetTest(context.Solution, new DotNetTestSettings
        {
            Configuration = context.Configuration,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = context.VersionProperties()
        });
    }
}

[TaskName("Pack")]
[IsDependentOn(typeof(TestTask))]
public sealed class PackTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        foreach (var project in context.SourceProjects)
        {
            var version = context.GetProjectVersion(project);
            context.Log.Write(Verbosity.Normal, LogLevel.Information,
                "Packing {0} with version {1}", project, version);
            context.DotNetPack(project, new DotNetPackSettings
            {
                Configuration = context.Configuration,
                NoBuild = true,
                NoRestore = true,
                OutputDirectory = context.Artifacts.FullPath,
                // Deliberately NOT Version/PackageVersion: those are global properties and would follow
                // the project reference into Pondhawk.Logging, stamping this package's version as the
                // dependency's. The projects derive their own versions from version.json instead.
                MSBuildSettings = context.VersionProperties()
            });
        }

        VerifyInternalDependencyVersions(context);
    }

    /// <summary>
    /// Reads back each packed nuspec and fails the build if a dependency on one of our own packages names
    /// a version other than the one being packed alongside it. That mismatch is invisible in the build log
    /// and in the package itself — it only surfaces as an unresolvable restore on a consumer's machine, so
    /// it is worth catching here rather than on NuGet.org, where a version cannot be replaced.
    /// </summary>
    private static void VerifyInternalDependencyVersions(BuildContext context)
    {
        var ours = context.SourceProjects.ToDictionary(
            project => System.IO.Path.GetFileNameWithoutExtension(project),
            context.GetProjectVersion,
            StringComparer.Ordinal);

        foreach (var (id, version) in ours)
        {
            var package = System.IO.Path.Combine(context.Artifacts.FullPath, $"{id}.{version}.nupkg");

            using var archive = ZipFile.OpenRead(package);
            var entry = archive.GetEntry($"{id}.nuspec")
                ?? throw new System.InvalidOperationException($"No nuspec found in {package}.");

            using var stream = entry.Open();
            var nuspec = XDocument.Load(stream);

            foreach (var dependency in nuspec.Descendants().Where(e => e.Name.LocalName == "dependency"))
            {
                var dependencyId = dependency.Attribute("id")?.Value;
                if (dependencyId is null || !ours.TryGetValue(dependencyId, out var expected))
                    continue;

                var declared = dependency.Attribute("version")?.Value;
                if (!string.Equals(declared, expected, StringComparison.Ordinal))
                {
                    throw new System.InvalidOperationException(
                        $"{id} {version} declares a dependency on {dependencyId} {declared}, but {dependencyId} " +
                        $"is packed as {expected}. A version has leaked into the project reference as a global " +
                        "MSBuild property — see src/Directory.Build.props.");
                }
            }
        }

        context.Log.Write(Verbosity.Normal, LogLevel.Information,
            "Verified internal dependency versions across {0} packages.", ours.Count);
    }
}

[TaskName("Push")]
[IsDependentOn(typeof(PackTask))]
public sealed class PushTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context)
    {
        if (string.IsNullOrEmpty(context.NuGetSource) || string.IsNullOrEmpty(context.NuGetApiKey))
        {
            context.Log.Write(Verbosity.Normal, LogLevel.Warning,
                "Skipping Push — nuget-source or nuget-api-key not configured.");
            return false;
        }

        return true;
    }

    public override void Run(BuildContext context)
    {
        var packages = context.GetFiles($"{context.Artifacts.FullPath}/*.nupkg");

        foreach (var package in packages)
        {
            context.DotNetNuGetPush(package.FullPath, new DotNetNuGetPushSettings
            {
                Source = context.NuGetSource,
                ApiKey = context.NuGetApiKey,
                SkipDuplicate = true
            });
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PushTask))]
public sealed class DefaultTask : FrostingTask<BuildContext>;
