using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Runtime;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Effects;

/// <summary>机器语句在托管语义下的效果；地址与类型事实由 TypeOracle 提供。</summary>
internal sealed class MachineEffects(ValueModel values, TypeOracle oracle)
{
    public (EffectSummary Effects, Sym? Root) Read(ReadSym read, IReadOnlySet<Sym> nonNull)
    {
        var location = oracle.Locate(read.Address, read.Width);
        var (root, offset, index, _) = oracle.Decompose(read.Address);
        root = SymTable.Resolve(root);
        var metadata = root is ResultSym { Site: Call { Op.Kind: OpKind.InterfaceInvokeData or OpKind.ResolveGenericVirtual } }
            || oracle.ClassOwner(root) is not null;
        var header = location is null && offset == 0 && index is null && read.Width == 64
            && oracle.TypeOf(root) is { } type && ManagedTypes.IsReferenceType(type);
        // 多维数组的 bounds 指针在对象里，和长度一样只经对象读取。
        var bounds = location is null && offset == Facts.RuntimeLayout.ArrayBounds && index is null && read.Width == 64
            && oracle.TypeOf(root) is ArrayRef { IsVector: false };
        // 维长与下界经 bounds 指针读取，按所属数组判空。
        if (location is { Kind: LocationKind.Dimension or LocationKind.LowerBound, Base: { } boundsOwner }) root = SymTable.Resolve(boundsOwner);
        var kind = metadata ? MemoryKind.Metadata : location?.Kind switch
        {
            LocationKind.Local => MemoryKind.Local,
            LocationKind.Instance or LocationKind.Length or LocationKind.StringLength or LocationKind.Dimension or LocationKind.LowerBound => MemoryKind.Instance,
            LocationKind.Element or LocationKind.MultiElement or LocationKind.StringChar => MemoryKind.Element,
            LocationKind.Static or LocationKind.ThreadStatic => MemoryKind.Static,
            LocationKind.Reference => MemoryKind.Reference,
            _ when header || bounds => MemoryKind.Instance,
            _ => MemoryKind.Unknown,
        };
        // beforefieldinit 类型的初始化可以在首次访问之前的任何时刻发生（ECMA-335 II.10.5.3.2），读取不构成时序约束。
        if (location is { Kind: LocationKind.Static, Owner: DefinitionRef owner } && (owner.Definition.Attributes & System.Reflection.TypeAttributes.BeforeFieldInit) != 0)
            return (new(EffectKind.Read), root);
        return (EffectSummary.Access(kind, nonNull: nonNull.Contains(root)), kind is MemoryKind.Local or MemoryKind.Metadata ? null : root);
    }

    public static EffectSummary Operation(Stmt statement) => statement switch
    {
        Define or Phi or Nop or Jump or Branch or Return or IndirectJump => EffectSummary.None,
        Store => new(EffectKind.Write, ExceptionEffect.Any),
        Call { Op: { } op } => RuntimeOps.Of(op.Kind)?.Behavior ?? EffectSummary.Unknown,
        _ => EffectSummary.Unknown,
    };

    public EffectSummary Statement(Stmt statement, IReadOnlySet<Sym> nonNull)
    {
        var effect = Operation(statement);
        foreach (var read in values.ReadsAt(statement)) effect |= Read(read, nonNull).Effects;
        return effect | Computation(values.Operands(statement));
    }

    private static EffectSummary Computation(IReadOnlyList<Sym> operands)
    {
        var effect = EffectSummary.None;
        var visited = new HashSet<Sym>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Sym>(operands);
        while (pending.TryPop(out var node))
        {
            RecoveryCancellation.Check();
            var value = SymTable.Resolve(node);
            if (!visited.Add(value)) continue;
            switch (value)
            {
                case UnknownSym or UndefSym:
                    effect |= EffectSummary.Unknown;
                    break;
                case OpSym op:
                    if (op.Op.Kind == OperatorKind.Binary && op.Op.Binary is BinOp.SDiv or BinOp.UDiv or BinOp.SRem or BinOp.URem)
                        effect |= new EffectSummary(EffectKind.None, ExceptionEffect.Other);
                    foreach (var arg in op.Args) pending.Push(arg);
                    break;
                case SliceSym slice:
                    pending.Push(slice.Base);
                    break;
                case ConcatSym concat:
                    foreach (var part in concat.Parts) pending.Push(part);
                    break;
            }
        }
        return effect;
    }
}
