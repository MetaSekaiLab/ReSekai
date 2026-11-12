using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Inlining;

namespace ReSekai.Decompiler.Recovery.Lowering;

/// <summary>机器下降产物。待证构造和局部分配边界随方法体交给模板阶段。</summary>
internal sealed record LoweredBody(ManagedBody? Body, IReadOnlyList<RecoveryHole> Holes,
    IReadOnlyDictionary<Variable, PendingConstruction> Constructions, int NextLocal)
{
    /// <summary>按原生代码直接写出、没有折回内联的同程序集访问。</summary>
    public IReadOnlyList<ReSekai.Decompiler.Diagnostics.RuleTrace> Unfolded { get; init; } = [];
}
