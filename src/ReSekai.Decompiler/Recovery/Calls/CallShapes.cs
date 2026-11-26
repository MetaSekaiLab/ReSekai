using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery.Calls;

/// <summary>调用点的形状：可能的托管目标及其机器签名。运行时操作没有托管签名。</summary>
public sealed record CallShape(Call Call, IReadOnlyList<MethodRef> Candidates, MachineSignature? Signature)
{
    /// <summary>被调方机器码不读取的输入寄存器：原生代码对这些位置的 IL 实参没有约束。</summary>
    public IReadOnlySet<Storage> Unused { get; init; } = new HashSet<Storage>();
    /// <summary>原生调用的是这个方法的本地副本，执行对照按方法体透明执行。</summary>
    public bool LocalCopy { get; init; }
    /// <summary>经运行时分派的托管调用：按托管签名传参，以 callvirt 调用。</summary>
    public bool IsDispatch => Call.Op?.Kind is OpKind.CallVirtual or OpKind.CallInterface or OpKind.CallDelegate or OpKind.CallGenericVirtual or OpKind.InvokeVirtualGeneric or OpKind.InvokeConstrainedGeneric or OpKind.CallConstrained;
    public bool IsRuntime => Call.Op is not null && !IsDispatch
        && !(Call.Op.Kind is OpKind.CallMethodPointer or OpKind.InvokeGeneric && Candidates.Count == 1);
}

/// <summary>
/// 按事实解析调用目标。直接调用的地址可能被多个方法共用（相同代码折叠），候选全部保留，由类型约束选择；
/// 候选的机器签名不一致时签名为 null。
/// </summary>
public sealed class CallShapes(ProgramFacts facts, ManagedAbi abi, Func<ulong, Storage, bool> unused)
{
    public ProgramFacts Facts => facts;
    private readonly Dictionary<ulong, (MethodRef[] Candidates, MachineSignature? Signature)> direct = [];

    public CallShape Of(Call call)
    {
        if (call.Op is { Kind: OpKind.InvokeGeneric or OpKind.InvokeVirtualGeneric, Operand: MethodRef invoked })
            return new(call, [invoked], abi.Invoker(invoked));
        if (call.Op is { Kind: OpKind.InvokeConstrainedGeneric, Operand: ValueTuple<TypeRef, MethodRef> constrained })
            return new(call, [constrained.Item2], abi.ConstrainedInvoker(constrained.Item1, constrained.Item2));
        if (call.Op is { Kind: OpKind.CallConstrained, Operand: ValueTuple<TypeRef, MethodRef> direct })
            return new(call, [direct.Item2], abi.ConstrainedMachine(direct.Item1, direct.Item2));
        if (call.Resolved is { } resolved)
            return new(call, [resolved], abi.Machine(resolved, out _))
            {
                LocalCopy = call is { Op: null, Kind: CallKind.Direct, Target: Const address } && address.Value != resolved.Method.Address,
            };
        if (call.Op is { Operand: MethodRef method } && call.Op.Kind is OpKind.CallGenericVirtual or OpKind.InvokeGeneric or OpKind.CallMethodPointer)
            return new(call, [method], abi.Machine(method, out _));
        if (call is { Op: null, Kind: CallKind.Direct or CallKind.Tail, Target: Const target })
        {
            var (candidates, signature) = Direct(target.Value);
            var named = signature is null ? Named(call) : null;
            if (named is var (instance, namedSignature)) (candidates, signature) = ([instance], namedSignature);
            else if (signature is not null) signature = Concrete(call, candidates, signature);
            var ignored = signature?.Arguments.SelectMany(a => a.Pieces).Select(p => p.Register)
                .Where(r => r.Bank is Bank.Gpr or Bank.Vec && unused(target.Value, r)).ToHashSet() ?? [];
            return new(call, candidates, signature) { Unused = ignored, LocalCopy = named is not null };
        }
        return new(call, [], null);
    }

    private (MethodRef[], MachineSignature?) Direct(ulong address)
    {
        if (direct.TryGetValue(address, out var known)) return known;
        var candidates = facts.EntriesAt(address)
            .Where(e => e.Kind is EntryKind.Method or EntryKind.GenericMethod && e.Method is not null)
            .Select(e => e.Method!).Distinct().ToArray();
        if (candidates.Length == 0 && facts.AdjustorTargets.At(address) is { } adjusted) candidates = [adjusted.Method];
        // 未登记在方法指针表中的方法：事实层由调用证据推断出定义，实例取自见证。
        if (candidates.Length == 0 && facts.Unregistered.At(address) is { } inferred)
            candidates = inferred.Witnesses.Select(w => w.Instance).Distinct().DefaultIfEmpty(new MethodRef(inferred.Definition, [], [])).ToArray();
        var signatures = candidates.Select(c => abi.Machine(c, out _)).ToArray();
        var signature = signatures.Length > 0 && signatures.All(s => s is not null && SameShape(s, signatures[0]!)) ? signatures[0] : null;
        return direct[address] = (candidates, signature);
    }

    /// <summary>
    /// 共享泛型代码的调用传入确切实例的 MethodInfo：某个实参是方法实体，且该方法签名的 MethodInfo 正落在这个实参的寄存器上时，
    /// 它就是被调用的方法（共享实例未登记在入口表中时由此得到签名）。
    /// </summary>
    private (MethodRef, MachineSignature)? Named(Call call)
    {
        for (var i = 0; i < call.Arguments.Length && i < call.Sources.Length; i++)
            if (call.Arguments[i] is Symbol { Kind: SymbolKind.Method, Value: MethodRef method }
                && abi.Machine(method, out _) is { MethodInfo: { } info } signature && info.Register == call.Sources[i])
                return (method, signature);
        return null;
    }

    /// <summary>
    /// 完全共享的实现按指针收发每个 T。隐藏 MethodInfo 给出确切实例时，实例的实参类型决定指针的含义：
    /// 引用类型就是对象引用本身，大小已知的值类型是已知大小的副本。
    /// </summary>
    private MachineSignature Concrete(Call call, MethodRef[] candidates, MachineSignature signature)
    {
        if (signature.MethodInfo is not { } info || !signature.Indirections.Any(v => v.Size < 0)) return signature;
        var position = call.ArgumentIndex(info.Register);
        if (position < 0 || call.Arguments[position] is not Symbol { Kind: SymbolKind.Method, Value: MethodRef instance }
            || candidates.Length == 0 || candidates.Any(c => c.Method != instance.Method)) return signature;
        MachineValue Refined(MachineValue value)
        {
            if (value.Size >= 0 || value.Transfer == Transfer.Value) return value;
            var type = value.Index == MachineSignature.ReturnIndex ? instance.Concrete(instance.Method.ReturnType)
                : value.Index == -1 ? new ByRefRef(instance.DeclaringType) : instance.Concrete(instance.Method.Parameters[value.Index].Type);
            // 类型参数本身的含义取决于实例，仍是共享的。
            if (type is GenericParameterRef || type is ByRefRef { Element: GenericParameterRef }) return value;
            if (value.Transfer == Transfer.Copy && ManagedTypes.IsReferenceType(type))
                return value with { Type = type, Transfer = Transfer.Value, Size = 8, Access = MemoryAccess.None };
            var stored = type is ByRefRef byRef ? byRef.Element : type;
            return !ManagedAbi.SharedValue(stored) && abi.Size(stored) is > 0 and var size ? value with { Type = type, Size = size } : value;
        }
        return signature with { Arguments = signature.Arguments.Select(Refined).ToArray(), Return = signature.Return is { } result ? Refined(result) : null };
    }

    /// <summary>直接调用目标的各候选及其机器签名（检查用）。</summary>
    public IEnumerable<(MethodRef Method, MachineSignature? Signature, string? Problem)> Candidates(ulong address) =>
        Direct(address).Item1.Select(c => (c, abi.Machine(c, out var problem), problem));

    /// <summary>两个签名占用相同的机器位置并有相同的内存效果。</summary>
    private static bool SameShape(MachineSignature a, MachineSignature b) =>
        a.Arguments.Length == b.Arguments.Length && a.Arguments.Zip(b.Arguments).All(p => Same(p.First, p.Second))
        && (a.Return is null) == (b.Return is null) && (a.Return is null || Same(a.Return, b.Return!))
        && a.MethodInfo == b.MethodInfo;

    private static bool Same(MachineValue a, MachineValue b) => a.Index == b.Index && a.Transfer == b.Transfer && a.Size == b.Size
        && a.Access == b.Access && a.Pieces.AsSpan().SequenceEqual(b.Pieces);
}
