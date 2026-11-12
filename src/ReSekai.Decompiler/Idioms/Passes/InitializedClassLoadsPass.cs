using ReSekai.Decompiler.Ir;
namespace ReSekai.Decompiler.Idioms;

internal static class InitializedClassLoadsPass
{
    internal static bool InitializedClassLoads(this IdiomState state)
    {
        return ClassInitializationFacts.Run(state.function, constructorFacts: state.self?.Method.Name != ".cctor");
    }
}
