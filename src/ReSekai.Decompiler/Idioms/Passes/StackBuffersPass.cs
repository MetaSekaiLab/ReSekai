using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// localloc：codegen 写成 p = size ? alloca(size) : NULL，随后 memset(p, 0, size)。
/// 栈上按 16 字节对齐的动态分配与同一大小的清零合成一次清零的栈分配。大小为零时的空指针臂并入（不访问任何元素）。
/// </summary>
internal static class StackBuffersPass
{
    internal static bool StackBuffers(IdiomState state)
    {
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
                if (block.Statements[i] is Define { Value: Binary { Op: BinOp.Sub, Left: var from, Right: var aligned } } define
                    && state.defs.Expand(aligned) is Binary { Op: BinOp.And, Left: Binary { Op: BinOp.Add, Left: var size, Right: Const { Value: 15 } },
                        Right: Const { Value: 0x1fffffff0 or 0xfffffffffffffff0 } }
                    && state.defs.Expand(from) is FrameAddress or Binary { Op: BinOp.Sub or BinOp.Add }
                    && ExprMap.Walk(state.defs.Expand(from)).OfType<FrameAddress>().Any()
                    && Name(state, block, i, define, size)) return true;
        return false;
    }

    private static bool Name(IdiomState state, Block block, int at, Define pointer, Expr size)
    {
        foreach (var clearing in state.function.Blocks)
            for (var i = 0; i < clearing.Statements.Count; i++)
            {
                if (clearing.Statements[i] is not Call { Op: { Kind: OpKind.Import, Operand: "memset" }, Arguments: [Use target, var zero, var length] } clear
                    || state.defs.Expand(zero) is not Const { Value: 0 } || state.defs.Expand(length) != size || Count(state, length) is not var (count, shift)) continue;
                if (target.Variable == pointer.Target)
                {
                    if (clearing != block || block.Statements.Skip(at + 1).Take(i - at - 1).Any(s => s is not (Define or Nop))) continue;
                    if (!CountAvailable(block, at, count)) continue;
                    block.Statements[i] = Done(clear, target);
                    block.Statements[at] = Allocate(pointer.Address, pointer.Target, count, shift);
                    EmptyArms(state, size);
                    return true;
                }
                var merge = clearing.Statements.OfType<Phi>().FirstOrDefault(p => p.Target == target.Variable);
                if (merge is null || clearing.Statements.Take(i).Any(s => s is not (Phi or Define or Nop))) continue;
                var other = Enumerable.Range(0, merge.Sources.Length).Where(s => merge.Sources[s] != pointer.Target).ToArray();
                if (other.Length != merge.Sources.Length - 1 || other.Any(s => state.defs.Expand(new Use(merge.Sources[s], 64)) is not Const { Value: 0 }
                    || !Empty(state, clearing.Predecessors[s], size))) continue;
                clearing.Statements[i] = Done(clear, target);
                clearing.Statements.Remove(merge);
                clearing.Statements.Insert(clearing.Statements.TakeWhile(s => s is Phi).Count(), Allocate(clear.Address, merge.Target, count, shift));
                EmptyArms(state, size);
                return true;
            }
        return false;
    }

    private static Call Allocate(ulong address, Variable target, Expr count, int shift) => new(address, CallKind.Direct, Expr.C(0, 64))
    {
        Op = new RuntimeOp(OpKind.StackBuffer, shift),
        Arguments = [count],
        Results = [target.Storage],
        Defines = [target],
    };

    // memset 返回目标指针。
    private static Stmt Done(Call clear, Expr target) => clear.Defines is [{ } result, ..] ? new Define(clear.Address, result, target) : new Nop(clear.Address, "栈分配清零");

    /// <summary>元素个数（32 位）与元素大小的对数：size = count &lt;&lt; shift。</summary>
    private static (Expr Count, int Shift)? Count(IdiomState state, Expr length)
    {
        var shift = 0;
        for (var depth = 0; depth < 8; depth++)
            switch (length)
            {
                case Binary { Op: BinOp.Shl, Left: var left, Right: Const { Value: <= 3 and var bits } } when shift == 0:
                    (length, shift) = (left, (int)bits);
                    break;
                case Ir.Convert { Kind: ConvKind.SignExtend, Operand: { Width: 32 } narrow } when !ExprMap.Walk(narrow).Any(e => e is Load):
                    return (narrow, shift);
                case Use use when state.RawDefinition(use) is { } definition:
                    length = definition;
                    break;
                default:
                    return null;
            }
        return null;
    }

    private static bool CountAvailable(Block block, int at, Expr count)
    {
        var used = ExprMap.Walk(count).OfType<Use>().Select(u => u.Variable).ToHashSet();
        return !block.Statements.Skip(at).Any(s => s is Define d && used.Contains(d.Target) || s is Call c && c.Defines.OfType<Variable>().Any(used.Contains));
    }

    /// <summary>大小为零的一侧对空指针的清零不访问内存。</summary>
    private static void EmptyArms(IdiomState state, Expr size)
    {
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
                if (block.Statements[i] is Call { Op: { Kind: OpKind.Import, Operand: "memset" }, Arguments: [var target, _, var length] } clear
                    && state.defs.Expand(target) is Const { Value: 0 } && state.defs.Expand(length) == size && Empty(state, block, size))
                    block.Statements[i] = Done(clear, Expr.C(0, 64));
    }

    /// <summary>沿单前驱链，到达 block 蕴含 size 为零。</summary>
    private static bool Empty(IdiomState state, Block block, Expr size)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (block.Predecessors is not [var previous]) return false;
            if (previous.Statements.LastOrDefault() is Branch branch && state.defs.Expand(branch.Condition) is Compare { Cond: Cond.Eq or Cond.Ne, Left: var tested, Right: Const { Value: 0 } } test
                && Core(tested) == Core(size) && branch.Target != branch.Fallthrough)
                return (test.Cond == Cond.Eq ? branch.Target : branch.Fallthrough) == block.Start;
            block = previous;
        }
        return false;
    }

    /// <summary>为零与否由哪个 32 位值决定：去掉扩展和不丢低 32 位的左移、截断。</summary>
    private static Expr Core(Expr value)
    {
        while (true)
            switch (value)
            {
                case Binary { Op: BinOp.Shl, Left: var left, Right: Const { Value: <= 32 } }: value = left; break;
                case Binary { Op: BinOp.And, Left: var left, Right: Const { Value: 0xffffffff } }: value = left; break;
                case Ir.Convert { Kind: ConvKind.SignExtend or ConvKind.ZeroExtend, Operand: { Width: 32 } narrow }: value = narrow; break;
                case Ir.Convert { Kind: ConvKind.Truncate, Width: 32, Operand: var wide }: value = wide; break;
                case Use { Width: 32 } use: return use with { Width = 64 };
                default: return value;
            }
    }
}
