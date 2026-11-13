using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Lowering;

internal sealed partial class BodyBuilder
{
    /// <summary>将由纯计算分支写入的结构体位与构造函数的两条路径比较。</summary>
    private ManagedValue? BranchConstruction(Sym bits, DefinitionRef structure, AggregatePlan plan, ulong address)
    {
        if (currentBlock is null || currentOutput is null || regions.Count != 0
            || !plan.Fields.Any(f => f.Projection.Path.Any(p => !fields.Accessible(p, p.DeclaringType)))) return null;
        var projections = types.Projections(structure).ToArray();
        if (projections.Length == 0 || projections.Any(p => !p.Fits(bits.Width) || types.Kind(p.Type) != TypeKind.Float)) return null;
        var parts = projections.Select(p => SymTable.Resolve(values.Syms.Slice(bits, p.Bit, p.Width))).ToArray();
        var merged = parts.OfType<PhiSym>().ToArray();
        if (merged.Length == 0) return null;
        var join = merged[0].Block;
        if (join.Predecessors is not [var first, var second] || merged.Any(p => p.Block != join) || !AfterJoin(currentBlock, join)) return null;
        var (head, firstArm) = BranchArm(first, join);
        var (other, secondArm) = BranchArm(second, join);
        if (head is null || head != other || firstArm == secondArm || head.Statements.LastOrDefault() is not Ir.Branch branch
            || values.Operands(branch) is not [OpSym { Op.Kind: OperatorKind.Compare, Args: [OpSym { Op: { Kind: OperatorKind.Unary, Unary: UnOp.FSqrt } }, ConstSym] } condition]) return null;
        Sym[]? Edge(int index)
        {
            var selected = new Sym[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] is not PhiSym phi) selected[i] = parts[i];
                else if (phi.Sources[index] is { } source) selected[i] = source;
                else return null;
            }
            return selected;
        }
        var taken = firstArm.Start == branch.Target ? 0 : secondArm.Start == branch.Target ? 1 : -1;
        if (taken < 0 || Edge(taken) is not { } yes || Edge(1 - taken) is not { } no) return null;
        if (templates.ProveBranchConstruction(structure, condition, yes, no, oracle, values, head.Index) is not { } proof) return null;
        var (constructor, arguments) = proof;
        return new NewObjectValue(constructor, arguments.Select((a, i) => View(a, constructor.Concrete(constructor.Method.Parameters[i].Type), address)).ToArray())
            { Transparent = true };
    }

    private static bool AfterJoin(Block use, Block join)
    {
        while (use != join && use.Predecessors is [var from] && from.Successors is [var next] && next == use && from.Index < use.Index)
            use = from;
        return use == join;
    }

    private static (Block? Head, Block Arm) BranchArm(Block block, Block next)
    {
        while (block.Predecessors is [var from] && block.Successors is [var only] && only == next
            && from.Index < block.Index && block.Statements.All(s => s is Define or Nop or Ir.Jump))
        {
            if (from.Successors.Count == 2) return (from, block);
            next = block;
            block = from;
        }
        return (null, block);
    }
}
