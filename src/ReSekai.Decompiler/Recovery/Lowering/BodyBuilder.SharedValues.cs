using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Lowering;

/// <summary>
/// 深层共享的计算：值是有向无环图，逐处展开成表达式树时大小按共享层数成倍增长。
/// 单个表达式展开后远超整个方法的不同值（值在表达式内部反复共享）时，把被多处使用的标量计算在它的定义处落成局部，其余方法仍按表达式展开。
/// </summary>
internal sealed partial class BodyBuilder
{
    private const long ExpandedLimit = 5_000;
    private const long SharedSize = 8;

    private readonly HashSet<Sym> sharedValues = [];
    private readonly Dictionary<Sym, Block> sharedOwners = [];
    private Dominators? sharedDominance;
    private Block? sharedBlock;

    private void PlanSharedValues()
    {
        if (regions.Count != 0) return;
        var sizes = new Dictionary<Sym, long>();
        var uses = new Dictionary<Sym, int>();
        var pendingPhis = new Stack<PhiSym>();
        long Visit(Sym sym)
        {
            sym = SymTable.Resolve(sym);
            uses[sym] = uses.GetValueOrDefault(sym) + 1;
            if (sizes.TryGetValue(sym, out var known)) return known;
            sizes[sym] = 1;
            if (sym is PhiSym merged) pendingPhis.Push(merged);
            var size = sym switch
            {
                OpSym op => op.Args.Sum(a => Math.Min(Visit(a), long.MaxValue / 64)),
                ConcatSym concat => concat.Parts.Sum(p => Math.Min(Visit(p), long.MaxValue / 64)),
                SliceSym slice => Visit(slice.Base),
                ReadSym read when !materialized.Contains(read) => Visit(read.Address),
                _ => 0,
            };
            return sizes[sym] = Math.Min(size + 1, long.MaxValue / 64);
        }
        foreach (var statement in order.SelectMany(b => b.Statements))
        {
            if (statement is Define or Phi or Nop) continue;
            foreach (var operand in values.Operands(statement)) Visit(operand);
        }
        // 合流在各入边上赋值，来源同样要展开。
        while (pendingPhis.TryPop(out var phi))
            foreach (var source in phi.Sources)
                if (source is not null) Visit(source);
        if (sizes.Count == 0 || sizes.Values.Max() is var largest && (largest <= ExpandedLimit || largest <= sizes.Count * 4L)) return;
        foreach (var (sym, count) in uses)
            if (count > 1 && sym is OpSym && sizes[sym] >= SharedSize) sharedValues.Add(sym);
        sharedDominance = Dominators.Compute(analysis.Function);
    }

    /// <summary>这条定义算出的值被多处使用：在这里落成局部。</summary>
    private void DefineShared(Block block, Define define, Sym value, List<ManagedStatement> output)
    {
        // 标量在向量寄存器里高位清零。
        var sym = SymTable.Resolve(value) is ConcatSym { Parts: [var low, .. var high] } && high.All(SymTable.IsZero) ? SymTable.Resolve(low) : SymTable.Resolve(value);
        if (!sharedValues.Contains(sym) || sharedOwners.ContainsKey(sym)
            || oracle.TypeOf(sym) is not { } type || types.Kind(type) is not (TypeKind.Int or TypeKind.Bool or TypeKind.Float or TypeKind.Double)) return;
        var content = View(sym, type, define.Address);
        output.Add(new ManagedAssignment(define.Address, Local(sym, type), content));
        sharedOwners[sym] = block;
    }

    /// <summary>落成局部的共享值在当前位置可用。</summary>
    private bool SharedVisible(Sym sym) => !sharedOwners.TryGetValue(sym, out var owner)
        || sharedBlock is { } at && sharedDominance!.Dominates(owner.Index, at.Index);
}
