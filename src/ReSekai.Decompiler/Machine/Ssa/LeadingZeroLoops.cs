using ReSekai.Decompiler.Idioms;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Machine.Ssa;

using Storage = ReSekai.Decompiler.Ir.Storage;

/// <summary>
/// 前导零计数没有对应的 IL 指令，也没有各运行时通用的库方法：它是 clang 把逐位右移的计数循环换成的 clz。
/// 还原成那个循环：n = 0; while (v != 0) { v &gt;&gt;&gt;= 1; n++; }，前导零个数是位宽减 n。
/// </summary>
internal static class LeadingZeroLoops
{
    public static bool Run(MachineFunction function, Func<int, Variable> fresh)
    {
        // 拆块不维护按地址的异常区间。
        if (function.Range.ExceptionRegions.Length != 0) return false;
        var changed = false;
        for (var found = true; found;)
        {
            found = false;
            foreach (var block in function.Blocks)
            {
                if (block.Handler is not null || block.IsLandingPad) continue;
                var index = block.Statements.FindIndex(s => s is not Phi && ExprMap.Operands(s).SelectMany(ExprMap.Walk).Any(Count));
                if (index < 0) continue;
                Expand(function, block, index, fresh);
                found = changed = true;
                break;
            }
        }
        return changed;
    }

    private static bool Count(Expr expression) => expression is Unary { Op: UnOp.Clz, Width: 32 or 64 };

    private static void Expand(MachineFunction function, Block block, int index, Func<int, Variable> fresh)
    {
        var statement = block.Statements[index];
        var address = statement.Address;
        var count = (Unary)ExprMap.Operands(statement).SelectMany(ExprMap.Walk).First(Count);
        var width = count.Width;
        var (source, zero) = (fresh(width), fresh(width));
        var (value, digits, shifted, next) = (fresh(width), fresh(width), fresh(width), fresh(width));
        var label = Math.Max(NameSupply.JoinedEdgeBase, function.Blocks.Max(b => b.Start) + 1);
        Use Read(Variable variable) => new(variable, width);

        // 表达式是树，语句里同一个 clz 的各处出现一并换掉。
        var rest = ExprMap.MapOperands(statement, operand => ExprMap.Map(operand, e => e == count
            ? Fold.Simplify(new Binary(BinOp.Sub, Expr.C((ulong)width, width), Read(digits), width)) : null));
        var merge = new Block(label + 2) { End = block.End, Statements = [rest, .. block.Statements.Skip(index + 1)] };
        var header = new Block(label)
        {
            End = label + 1,
            Statements =
            [
                new Phi(address, value, [source, shifted]),
                new Phi(address, digits, [zero, next]),
                new Branch(address, new Compare(Cond.Ne, Read(value), Expr.C(0, width)), label + 1, merge.Start),
            ],
        };
        var body = new Block(label + 1)
        {
            End = label + 2,
            Statements =
            [
                new Define(address, shifted, new Binary(BinOp.LShr, Read(value), Expr.C(1, width), width)),
                new Define(address, next, new Binary(BinOp.Add, Read(digits), Expr.C(1, width), width)),
                new Jump(address, header.Start),
            ],
        };
        foreach (var successor in block.Successors)
        {
            successor.Predecessors[successor.Predecessors.IndexOf(block)] = merge;
            merge.Successors.Add(successor);
        }
        block.Successors.Clear();
        block.Successors.Add(header);
        header.Predecessors.AddRange([block, body]);
        header.Successors.AddRange([body, merge]);
        body.Predecessors.Add(header);
        body.Successors.Add(header);
        merge.Predecessors.Add(header);
        block.Statements =
        [
            .. block.Statements.Take(index),
            new Define(address, source, count.Operand),
            new Define(address, zero, Expr.C(0, width)),
            new Jump(address, header.Start),
        ];
        function.Blocks.AddRange([header, body, merge]);
        function.Renumber();
    }
}
