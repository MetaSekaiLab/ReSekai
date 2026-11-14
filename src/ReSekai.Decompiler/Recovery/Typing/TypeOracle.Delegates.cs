using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Typing;

public abstract record DelegateTarget;
public sealed record BoundDelegateTarget(MethodRef Method, DelegateBinding Binding) : DelegateTarget;
public sealed record SelectedDelegateTarget(Sym Condition, DelegateTarget True, DelegateTarget False) : DelegateTarget;
public sealed record MergedDelegateTarget(PhiSym Phi, IReadOnlyList<DelegateTarget> Sources) : DelegateTarget;

public sealed partial class TypeOracle
{
    /// <summary>委托绑定的目标及选择条件；每个分支都必须有直接方法或虚表槽的证据。</summary>
    public DelegateTarget? DelegateTargetOf(Sym pointer, Sym receiver) => DelegateTargetOf(pointer, receiver, []);

    private DelegateTarget? DelegateTargetOf(Sym pointer, Sym receiver, HashSet<PhiSym> seen)
    {
        pointer = SymTable.Resolve(pointer);
        if (pointer is EntitySym { Symbol: { Kind: SymbolKind.Method, Value: MethodRef method } })
            // 接口方法没有自己的代码，委托指针只能来自接口分发，等价于对接收者的虚绑定。
            return new BoundDelegateTarget(method, method.Method.IsStatic ? DelegateBinding.Static
                : method.DeclaringType is DefinitionRef { Definition.IsInterface: true } ? DelegateBinding.VirtualInstance : DelegateBinding.ClosedInstance);
        if (pointer is OpSym { Op.Kind: OperatorKind.Select, Args: [var condition, var yes, var no] }
            && DelegateTargetOf(yes, receiver, seen) is { } taken && DelegateTargetOf(no, receiver, seen) is { } fallthrough)
            return new SelectedDelegateTarget(condition, taken, fallthrough);
        if (pointer is PhiSym { Projection: null, Width: 64 } phi && seen.Add(phi))
        {
            var targets = phi.Sources.Select((source, i) => source is null ? null : DelegateTargetOf(EdgeSource(phi, i), receiver, seen)).ToArray();
            seen.Remove(phi);
            if (targets.Length > 0 && targets.All(Direct)) return new MergedDelegateTarget(phi, targets!);
        }
        return VirtualMethod(pointer, receiver) is { } dispatched ? new BoundDelegateTarget(dispatched, DelegateBinding.VirtualInstance) : null;
    }

    private static bool Direct(DelegateTarget? target) => target switch
    {
        BoundDelegateTarget { Binding: not DelegateBinding.VirtualInstance } => true,
        SelectedDelegateTarget selected => Direct(selected.True) && Direct(selected.False),
        MergedDelegateTarget merged => merged.Sources.All(Direct),
        _ => false,
    };
}
