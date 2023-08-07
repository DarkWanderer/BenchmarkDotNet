using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Helpers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Portability;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.Results;
using JetBrains.Annotations;

namespace BenchmarkDotNet.Toolchains.DotNetCli
{
    public class DotNetCliCommand
    {
        [PublicAPI] public string CliPath { get; }

        [PublicAPI] public string Arguments { get; }

        [PublicAPI] public GenerateResult GenerateResult { get; }

        [PublicAPI] public ILogger Logger { get; }

        [PublicAPI] public BuildPartition BuildPartition { get; }

        [PublicAPI] public IReadOnlyList<EnvironmentVariable> EnvironmentVariables { get; }

        [PublicAPI] public TimeSpan Timeout { get; }

        [PublicAPI] public bool LogOutput { get; }

        [Obsolete("Building with no dependencies is no longer supported.", false), EditorBrowsable(EditorBrowsableState.Never)]
        public bool RetryFailedBuildWithNoDeps => false;

        public DotNetCliCommand(string cliPath, string arguments, GenerateResult generateResult, ILogger logger,
            BuildPartition buildPartition, IReadOnlyList<EnvironmentVariable> environmentVariables, TimeSpan timeout, bool logOutput = false)
        {
            CliPath = cliPath ?? DotNetCliCommandExecutor.DefaultDotNetCliPath.Value;
            Arguments = arguments;
            GenerateResult = generateResult;
            Logger = logger;
            BuildPartition = buildPartition;
            EnvironmentVariables = environmentVariables;
            Timeout = timeout;
            LogOutput = logOutput || (buildPartition is not null && buildPartition.LogBuildOutput);
        }

        public DotNetCliCommand WithArguments(string arguments)
            => new (CliPath, arguments, GenerateResult, Logger, BuildPartition, EnvironmentVariables, Timeout, logOutput: LogOutput);

        public DotNetCliCommand WithCliPath(string cliPath)
            => new (cliPath, Arguments, GenerateResult, Logger, BuildPartition, EnvironmentVariables, Timeout, logOutput: LogOutput);

        [PublicAPI]
        public BuildResult RestoreThenBuild()
        {
            DotNetCliCommandExecutor.LogEnvVars(WithArguments(null));

            var packagesResult = AddPackages();
            if (!packagesResult.IsSuccess)
                return BuildResult.Failure(GenerateResult, packagesResult.AllInformation);

            // there is no way to do tell dotnet restore which configuration to use (https://github.com/NuGet/Home/issues/5119)
            // so when users go with custom build configuration, we must perform full build
            // which will internally restore for the right configuration
            if (BuildPartition.IsCustomBuildConfiguration)
                return Build().ToBuildResult(GenerateResult);

            var restoreResult = Restore();
            if (!restoreResult.IsSuccess)
                return BuildResult.Failure(GenerateResult, restoreResult.AllInformation);

            // On our CI, Integration tests take too much time, because each benchmark run rebuilds BenchmarkDotNet itself.
            // To reduce the total duration of the CI workflows, we build all the projects without dependencies
            if (XUnitHelper.ForceNoDependenciesForCore)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                return BuildNoRestoreNoDependencies().ToBuildResult(GenerateResult);
#pragma warning restore CS0618 // Type or member is obsolete
            }

            // We no longer retry with --no-dependencies, because it fails with --output set at the same time,
            // and the artifactsPaths.BinariesDirectoryPath is set before we try to build, so we cannot overwrite it.
            return BuildNoRestore().ToBuildResult(GenerateResult);
        }

        [PublicAPI]
        public BuildResult RestoreThenBuildThenPublish()
        {
            DotNetCliCommandExecutor.LogEnvVars(WithArguments(null));

            var packagesResult = AddPackages();
            if (!packagesResult.IsSuccess)
                return BuildResult.Failure(GenerateResult, packagesResult.AllInformation);

            // there is no way to do tell dotnet restore which configuration to use (https://github.com/NuGet/Home/issues/5119)
            // so when users go with custom build configuration, we must perform full publish
            // which will internally restore and build for the right configuration
            if (BuildPartition.IsCustomBuildConfiguration)
                return Publish().ToBuildResult(GenerateResult);

            var restoreResult = Restore();
            if (!restoreResult.IsSuccess)
                return BuildResult.Failure(GenerateResult, restoreResult.AllInformation);

            // We use the implicit build in the publish command. We stopped doing a separate build step because we set the --output.
            return PublishNoRestore().ToBuildResult(GenerateResult);
        }

        public DotNetCliCommandResult AddPackages()
        {
            var executionTime = new TimeSpan(0);
            var stdOutput = new StringBuilder();
            foreach (var cmd in GetAddPackagesCommands(BuildPartition))
            {
                var result = DotNetCliCommandExecutor.Execute(WithArguments(cmd));
                if (!result.IsSuccess) return result;
                executionTime += result.ExecutionTime;
                stdOutput.Append(result.StandardOutput);
            }
            return DotNetCliCommandResult.Success(executionTime, stdOutput.ToString());
        }

        public DotNetCliCommandResult Restore()
            => DotNetCliCommandExecutor.Execute(WithArguments(
                GetRestoreCommand(GenerateResult.ArtifactsPaths, BuildPartition, Arguments, "restore")));

        public DotNetCliCommandResult Build()
            => DotNetCliCommandExecutor.Execute(WithArguments(
                GetBuildCommand(GenerateResult.ArtifactsPaths, BuildPartition, Arguments, "build")));

        public DotNetCliCommandResult BuildNoRestore()
            => DotNetCliCommandExecutor.Execute(WithArguments(
                GetBuildCommand(GenerateResult.ArtifactsPaths, BuildPartition, $"{Arguments} --no-restore", "build-no-restore")));

        [Obsolete("Building with no dependencies is no longer supported, and will probably fail.", false), EditorBrowsable(EditorBrowsableState.Never)]
        public DotNetCliCommandResult BuildNoRestoreNoDependencies()
            => DotNetCliCommandExecutor.Execute(WithArguments(
                GetBuildCommand(GenerateResult.ArtifactsPaths, BuildPartition, $"{Arguments} --no-restore --no-dependencies", "build-no-restore-no-deps", excludeOutput: true)));

        public DotNetCliCommandResult Publish()
            => DotNetCliCommandExecutor.Execute(WithArguments(
                GetPublishCommand(GenerateResult.ArtifactsPaths, BuildPartition, Arguments, "publish")));

        // PublishNoBuildAndNoRestore was removed because we set --output in the build step. We use the implicit build included in the publish command.
        public DotNetCliCommandResult PublishNoRestore()
            => DotNetCliCommandExecutor.Execute(WithArguments(
                GetPublishCommand(GenerateResult.ArtifactsPaths, BuildPartition, $"{Arguments} --no-restore", "publish-no-restore")));

        internal static IEnumerable<string> GetAddPackagesCommands(BuildPartition buildPartition)
            => GetNuGetAddPackageCommands(buildPartition.RepresentativeBenchmarkCase, buildPartition.Resolver);

        internal static string GetRestoreCommand(ArtifactsPaths artifactsPaths, BuildPartition buildPartition, string extraArguments = null, string binLogSuffix = null)
            => new StringBuilder()
                .AppendArgument("restore")
                .AppendArgument(string.IsNullOrEmpty(artifactsPaths.PackagesDirectoryName) ? string.Empty : $"--packages \"{artifactsPaths.PackagesDirectoryName}\"")
                .AppendArgument(GetCustomMsBuildArguments(buildPartition.RepresentativeBenchmarkCase, buildPartition.Resolver))
                .AppendArgument(extraArguments)
                .AppendArgument(GetMandatoryMsBuildSettings(buildPartition.BuildConfiguration))
                .AppendArgument(GetMsBuildBinLogArgument(buildPartition, binLogSuffix))
                .ToString();

        internal static string GetBuildCommand(ArtifactsPaths artifactsPaths, BuildPartition buildPartition, string extraArguments = null, string binLogSuffix = null, bool excludeOutput = false)
            => new StringBuilder()
                .AppendArgument($"build -c {buildPartition.BuildConfiguration}") // we don't need to specify TFM, our auto-generated project contains always single one
                .AppendArgument(GetCustomMsBuildArguments(buildPartition.RepresentativeBenchmarkCase, buildPartition.Resolver))
                .AppendArgument(extraArguments)
                .AppendArgument(GetMandatoryMsBuildSettings(buildPartition.BuildConfiguration))
                .AppendArgument(string.IsNullOrEmpty(artifactsPaths.PackagesDirectoryName) ? string.Empty : $"/p:NuGetPackageRoot=\"{artifactsPaths.PackagesDirectoryName}\"")
                .AppendArgument(GetMsBuildBinLogArgument(buildPartition, binLogSuffix))
                // Fix #1377 (see comments in #1773).
                // We force the project to output binaries to a new directory.
                // Specifying --output and --no-dependencies breaks the build (because the previous build was not done using the custom output path),
                // so we don't include it if we're building no-deps (only supported for integration tests).
                .AppendArgument(excludeOutput ? string.Empty : $"--output \"{artifactsPaths.BinariesDirectoryPath}\"")
                .AppendArgument(excludeOutput ? string.Empty : $"/p:OutputPath=\"{artifactsPaths.BinariesDirectoryPath}\"")
                .AppendArgument(excludeOutput ? string.Empty : $"/p:IntermediateOutputPath=\"{artifactsPaths.IntermediateDirectoryPath}\"\\")
                .ToString();

        internal static string GetPublishCommand(ArtifactsPaths artifactsPaths, BuildPartition buildPartition, string extraArguments = null, string binLogSuffix = null)
            => new StringBuilder()
                .AppendArgument($"publish -c {buildPartition.BuildConfiguration}") // we don't need to specify TFM, our auto-generated project contains always single one
                .AppendArgument(GetCustomMsBuildArguments(buildPartition.RepresentativeBenchmarkCase, buildPartition.Resolver))
                .AppendArgument(extraArguments)
                .AppendArgument(GetMandatoryMsBuildSettings(buildPartition.BuildConfiguration))
                .AppendArgument(string.IsNullOrEmpty(artifactsPaths.PackagesDirectoryName) ? string.Empty : $"/p:NuGetPackageRoot=\"{artifactsPaths.PackagesDirectoryName}\"")
                .AppendArgument(GetMsBuildBinLogArgument(buildPartition, binLogSuffix))
                .AppendArgument($"--output \"{artifactsPaths.BinariesDirectoryPath}\"")
                .AppendArgument($"/p:OutputPath=\"{artifactsPaths.BinariesDirectoryPath}\"")
                .AppendArgument($"/p:IntermediateOutputPath=\"{artifactsPaths.IntermediateDirectoryPath}\"\\")
                .ToString();

        private static string GetMsBuildBinLogArgument(BuildPartition buildPartition, string suffix)
        {
            if (!buildPartition.GenerateMSBuildBinLog || string.IsNullOrEmpty(suffix))
                return string.Empty;

            return $"-bl:{buildPartition.ProgramName}-{suffix}.binlog";
        }

        private static string GetCustomMsBuildArguments(BenchmarkCase benchmarkCase, IResolver resolver)
        {
            if (!benchmarkCase.Job.HasValue(InfrastructureMode.ArgumentsCharacteristic))
                return null;

            var msBuildArguments = benchmarkCase.Job.ResolveValue(InfrastructureMode.ArgumentsCharacteristic, resolver).OfType<MsBuildArgument>();

            return string.Join(" ", msBuildArguments.Select(arg => arg.TextRepresentation));
        }

        private static IEnumerable<string> GetNuGetAddPackageCommands(BenchmarkCase benchmarkCase, IResolver resolver)
        {
            if (!benchmarkCase.Job.HasValue(InfrastructureMode.NuGetReferencesCharacteristic))
                return Enumerable.Empty<string>();

            var nuGetRefs = benchmarkCase.Job.ResolveValue(InfrastructureMode.NuGetReferencesCharacteristic, resolver);

            return nuGetRefs.Select(BuildAddPackageCommand);
        }

        private static string GetMandatoryMsBuildSettings(string buildConfiguration)
        {
            // we use these settings to make sure that MSBuild does the job and simply quits without spawning any long living processes
            // we want to avoid "file in use" and "zombie processes" issues
            const string NoMsBuildZombieProcesses = "/p:UseSharedCompilation=false /p:BuildInParallel=false /m:1 /p:Deterministic=true";
            const string EnforceOptimizations = "/p:Optimize=true";

            if (string.Equals(buildConfiguration, RuntimeInformation.DebugConfigurationName, StringComparison.OrdinalIgnoreCase))
            {
                return NoMsBuildZombieProcesses;
            }

            return $"{NoMsBuildZombieProcesses} {EnforceOptimizations}";
        }

        private static string BuildAddPackageCommand(NuGetReference reference)
        {
            var commandBuilder = new StringBuilder();
            commandBuilder.AppendArgument("add package");
            commandBuilder.AppendArgument(reference.PackageName);
            if (!string.IsNullOrWhiteSpace(reference.PackageVersion))
            {
                commandBuilder.AppendArgument("-v");
                commandBuilder.AppendArgument(reference.PackageVersion);
            }
            if (reference.PackageSource != null)
            {
                commandBuilder.AppendArgument("-s");
                commandBuilder.AppendArgument(reference.PackageSource);
            }
            if (reference.Prerelease)
            {
                commandBuilder.AppendArgument("--prerelease");
            }
            return commandBuilder.ToString();
        }
    }
}
