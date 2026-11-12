
namespace ReSekai.Decompiler.Facts.Derived;

public sealed record DerivedFacts
{
    public UnregisteredMethods Unregistered { get; init; } = UnregisteredMethods.Empty;
    public AdjustorTargets AdjustorTargets { get; init; } = AdjustorTargets.Empty;
}
