using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Tests that change process-wide state, such as an environment variable. xUnit runs this collection on its own,
/// after the others, so no other test sees the change.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironment
{
    public const string Name = "Process environment";
}

/// <summary>
/// Tests that measure the whole process's allocations. xUnit runs this collection on its own, so what they measure is
/// their own work.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Measured
{
    public const string Name = "Measured";
}
