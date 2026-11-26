using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 同一对象对同一个类的检查已经成立（不成立即抛出）之后，支配范围内的同一检查必然成立：对象的类不变。
/// clang 合并公共子式时第二次检查可能只剩最后一步比较。
/// </summary>
internal static class RepeatedTypeChecks
{
    public static bool Run(IdiomState state)
    {
        var function = state.function;
        var checks = function.Blocks.SelectMany(b => b.Statements).OfType<Call>()
            .Where(c => c is { Op: { Kind: OpKind.TypeCheck, Operand: TypeRef }, Arguments.Length: 1, Defines: [{ }] }).ToDictionary(c => c.Defines[0]!);
        if (checks.Count == 0) return false;
        Variable? Object(Expr klass) => StripConversionsPass.StripConversions(klass is Use use ? state.defs.Raw(use.Variable) ?? klass : klass) is Load { Width: 64, Address: Use obj } ? obj.Variable : null;
        foreach (var block in function.Blocks)
            for (var index = 0; index < block.Statements.Count; index++)
            {
                if (block.Statements[index] is not Call { Op: { Kind: OpKind.ThrowIf }, Arguments: [Compare { Cond: Cond.Eq, Left: Use flag, Right: Const { Value: 0 } }] }
                    || !checks.TryGetValue(flag.Variable, out var check) || Object(check.Arguments[0]) is not { } obj) continue;
                var proven = new HashSet<Variable>();
                var changed = false;
                foreach (var later in function.Blocks)
                {
                    if (later != block && !Dominates(function, block, later)) continue;
                    for (var i = later == block ? index + 1 : 0; i < later.Statements.Count; i++)
                    {
                        var statement = later.Statements[i];
                        if (statement is Call { Defines: [{ } result] } again && checks.TryGetValue(result, out var known) && ReferenceEquals(known, again) && again != check
                            && Equals(again.Op!.Operand, check.Op!.Operand) && Object(again.Arguments[0])?.Equals(obj) == true)
                        {
                            later.Statements[i] = new Define(again.Address, result, Expr.C(1, result.Width));
                            proven.Add(result);
                            changed = true;
                            continue;
                        }
                        if (statement is Phi or Nop) continue;
                        var rewritten = ExprMap.MapOperands(statement, operand => ExprMap.Map(operand, e => e is Compare { Cond: Cond.Eq or Cond.Ne } compare && Same(compare)
                            ? Expr.C(compare.Cond == Cond.Eq ? 1u : 0u, 1) : null));
                        if (rewritten == statement) continue;
                        later.Statements[i] = ExprMap.MapOperands(rewritten, Fold.Simplify);
                        changed = true;
                    }
                }
                if (!changed) continue;
                if (proven.Count > 0)
                    foreach (var other in function.Blocks)
                        for (var i = 0; i < other.Statements.Count; i++)
                            if (other.Statements[i] is not Phi && ExprMap.Operands(other.Statements[i]).SelectMany(ExprMap.Walk).OfType<Use>().Any(u => proven.Contains(u.Variable)))
                                other.Statements[i] = ExprMap.MapOperands(other.Statements[i], operand => Fold.Simplify(ExprMap.Map(operand, e => e is Use use && proven.Contains(use.Variable) ? Expr.C(1, use.Width) : null)));
                // 恒不抛出的检查没有效果。
                foreach (var other in function.Blocks.Where(b => b.Handler is null))
                    for (var i = 0; i < other.Statements.Count; i++)
                        if (other.Statements[i] is Call { Op.Kind: OpKind.ThrowIf, Arguments: [Const { Value: 0 }] } never)
                            other.Statements[i] = new Nop(never.Address, "重复的类型检查");
                state.foldedRuntimeGuards = true;
                return true;

                bool Same(Compare compare)
                {
                    if (compare.Right is not Symbol { Kind: SymbolKind.Class } type || !Equals(type.Value, check.Op!.Operand)
                        || state.defs.Expand(compare.Left) is not Load { Width: 64 } item) return false;
                    // [[[obj] + typeHierarchy] + depth*8 - 8]
                    return TermsPass.Terms(item.Address).Any(t => t is Load { Width: 64, Address: Binary { Op: BinOp.Add, Left: Load { Width: 64, Address: Use holder }, Right: Const { Value: RuntimeLayout.ClassTypeHierarchy } } }
                        && holder.Variable.Equals(obj));
                }
            }
        return false;
    }

    private static bool Dominates(MachineFunction function, Block through, Block target)
    {
        var seen = new HashSet<Block> { through };
        var pending = new Stack<Block>([function.Blocks[0]]);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (current == target) return false;
            foreach (var next in current.Successors) pending.Push(next);
        }
        return true;
    }
}
