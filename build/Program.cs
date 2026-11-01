#nullable enable

using System;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
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
    public string? PackageVersion { get; }
    public bool SkipDuplicate { get; }
    public DirectoryPath Artifacts { get; }
    public string? NuGetSource { get; }
    public string? NuGetApiKey { get; }

    public string[] SourceProjects { get; } =
    [
        "src/Pondhawk.Logging/Pondhawk.Logging.csproj",
        "src/Pondhawk.Logging.Watch/Pondhawk.Logging.Watch.csproj",
        "src/Pondhawk.Logging.Console/Pondhawk.Logging.Console.csproj",
        "src/Pondhawk.Logging.CloudWatch/Pondhawk.Logging.CloudWatch.csproj",
        "src/Pondhawk.Logging.Journald/Pondhawk.Logging.Journald.csproj",
        "src/Pondhawk.Logging.AspNetCore/Pondhawk.Logging.AspNetCore.csproj",
        "src/Pondhawk.Logging.Migration/Pondhawk.Logging.Migration.csproj"
    ];

    public BuildContext(ICakeContext context) : base(context)
    {
        Configuration = context.Argument("configuration", "Release");
        Solution = context.Argument("solution", "pondhawk-logging.slnx");
        PackageVersion = context.Argument<string?>("package-version", null);
        SkipDuplicate = context.Argument("skip-duplicate", true);
        Artifacts = context.Argument("artifacts", "artifacts");
        NuGetSource = context.Argument<string?>("nuget-source", null)
            ?? context.Environment.GetEnvironmentVariable("NUGET_SOURCE");
        NuGetApiKey = context.Argument<string?>("nuget-api-key", null)
            ?? context.Environment.GetEnvironmentVariable("NUGET_API_KEY");
    }

    private const string VersionFile = "src/Directory.Build.props";

    private static readonly Regex VersionPattern =
        new(@"<VersionPrefix>(?<version>\d+\.\d+\.\d+)</VersionPrefix>", RegexOptions.Compiled);

    /// <summary>The last released version, from <see cref="VersionFile"/>.</summary>
    public string ReadVersion()
    {
        var match = VersionPattern.Match(System.IO.File.ReadAllText(VersionFile));
        return match.Success
            ? match.Groups["version"].Value
            : throw new System.InvalidOperationException(
                $"No <VersionPrefix>MAJOR.MINOR.PATCH</VersionPrefix> in {VersionFile}.");
    }

    /// <summary>The version a Pack writes. A plain local Pack must never look released.</summary>
    public string GetPackVersion() => PackageVersion ?? $"{ReadVersion()}-local";

    /// <summary>The next patch after the last released version. Does not change the file.</summary>
    public string NextPatch()
    {
        var parts = ReadVersion().Split('.').Select(int.Parse).ToArray();
        return $"{parts[0]}.{parts[1]}.{parts[2] + 1}";
    }

    /// <summary>Applies a semantic bump, rewrites <see cref="VersionFile"/>, and returns the new version.</summary>
    public string BumpVersion(string part)
    {
        var parts = ReadVersion().Split('.').Select(int.Parse).ToArray();
        var next = part switch
        {
            "major" => $"{parts[0] + 1}.0.0",
            "minor" => $"{parts[0]}.{parts[1] + 1}.0",
            "patch" => $"{parts[0]}.{parts[1]}.{parts[2] + 1}",
            _ => throw new System.InvalidOperationException(
                $"Unknown bump '{part}' (expected none, major, minor or patch)."),
        };
        var text = System.IO.File.ReadAllText(VersionFile);
        System.IO.File.WriteAllText(VersionFile, VersionPattern.Replace(text, $"<VersionPrefix>{next}</VersionPrefix>"));
        return next;
    }
}

/// <summary>
/// Resolves a version and writes version=X.Y.Z to $GITHUB_OUTPUT under Actions.
/// --bump=patch rewrites the version file; --next=true --suffix=ci.42 leaves it unchanged.
/// </summary>
[TaskName("Version")]
public sealed class VersionTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var bump = context.Argument("bump", "none");
        var suffix = context.Argument("suffix", string.Empty);
        var version = bump != "none"
            ? context.BumpVersion(bump)
            : context.Argument("next", false) ? context.NextPatch() : context.ReadVersion();
        if (suffix.Length > 0)
            version = $"{version}-{suffix}";

        context.Log.Write(Verbosity.Normal, LogLevel.Information, "Version: {0}", version);
        var output = context.Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (!string.IsNullOrEmpty(output))
            System.IO.File.AppendAllText(output, $"version={version}\n");
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

// An application may reference Pondhawk.Logging from NuGet, vendor its source, or drop it, without breaking:
// so no other package may reference it, as a project or as a package. Checked here as well as by
// each test project, because an unused reference leaves no trace in the assembly but still becomes a NuGet
// dependency.
[TaskName("CheckReferences")]
public sealed class CheckReferencesTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var offenders = context.SourceProjects
            .Where(project => !project.EndsWith("/Pondhawk.Logging.csproj", StringComparison.Ordinal))
            .SelectMany(project => XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
                .Where(include => include.Replace('\\', '/').EndsWith("/Pondhawk.Logging.csproj", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(include, "Pondhawk.Logging", StringComparison.OrdinalIgnoreCase))
                .Select(include => $"{project} references {include}"))
            .ToList();

        if (offenders.Count > 0)
            throw new InvalidOperationException(
                "No package may reference Pondhawk.Logging (see CLAUDE.md):" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(RestoreTask))]
[IsDependentOn(typeof(CheckReferencesTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetBuild(context.Solution, new DotNetBuildSettings
        {
            Configuration = context.Configuration,
            NoRestore = true
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
            NoRestore = true
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
            var version = context.GetPackVersion();
            context.Log.Write(Verbosity.Normal, LogLevel.Information,
                "Packing {0} with version {1}", project, version);
            context.DotNetPack(project, new DotNetPackSettings
            {
                Configuration = context.Configuration,
                NoBuild = true,
                NoRestore = true,
                OutputDirectory = context.Artifacts.FullPath,
                // Global properties, so they follow project references. That is correct here: every
                // package in this repository has one version.
                MSBuildSettings = new DotNetMSBuildSettings()
                    .WithProperty("PackageVersion", version)
                    .WithProperty("Version", version)
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
            _ => context.GetPackVersion(),
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
                        $"is packed as {expected}. Check the version properties in the Pack task.");
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
                SkipDuplicate = context.SkipDuplicate
            });
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PushTask))]
public sealed class DefaultTask : FrostingTask<BuildContext>;
