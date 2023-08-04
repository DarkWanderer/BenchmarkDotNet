using BenchmarkDotNet.Portability;
using System;
using System.Linq;

namespace BenchmarkDotNet.Helpers;

internal static class XUnitHelper
{
    public static Lazy<bool> IsIntegrationTest =
        new (() => AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "BenchmarkDotNet.IntegrationTests"));

    public static bool ForceNoDependenciesForCore => IsIntegrationTest.Value && RuntimeInformation.IsNetCore;
}