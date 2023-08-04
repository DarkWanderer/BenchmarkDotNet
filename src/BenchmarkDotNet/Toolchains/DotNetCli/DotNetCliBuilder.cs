using System;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.Results;
using JetBrains.Annotations;

namespace BenchmarkDotNet.Toolchains.DotNetCli
{
    [PublicAPI]
    public class DotNetCliBuilder : IBuilder
    {
        private string TargetFrameworkMoniker { get; }

        private string CustomDotNetCliPath { get; }
        private bool LogOutput { get; }
        private bool RetryFailedBuildWithOutputPath { get; }

        [PublicAPI]
        public DotNetCliBuilder(string targetFrameworkMoniker, string customDotNetCliPath = null, bool logOutput = false, bool retryFailedBuildWithOutputPath = true)
        {
            TargetFrameworkMoniker = targetFrameworkMoniker;
            CustomDotNetCliPath = customDotNetCliPath;
            LogOutput = logOutput;
            RetryFailedBuildWithOutputPath = retryFailedBuildWithOutputPath;
        }

        public BuildResult Build(GenerateResult generateResult, BuildPartition buildPartition, ILogger logger)
        {
            BuildResult buildResult = new DotNetCliCommand(
                    CustomDotNetCliPath,
                    string.Empty,
                    generateResult,
                    logger,
                    buildPartition,
                    Array.Empty<EnvironmentVariable>(),
                    buildPartition.Timeout,
                    logOutput: LogOutput,
                    retryFailedBuildWithOutputPath: RetryFailedBuildWithOutputPath)
                .RestoreThenBuild();
            if (buildResult.IsBuildSuccess &&
                buildPartition.RepresentativeBenchmarkCase.Job.Environment.LargeAddressAware)
            {
                LargeAddressAware.SetLargeAddressAware(generateResult.ArtifactsPaths.ExecutablePath);
            }
            return buildResult;
        }
    }
}
