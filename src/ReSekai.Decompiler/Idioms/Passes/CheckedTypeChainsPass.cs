using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Ssa;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 抛出形式的内联继承检查：先比层级深度，再比该层的类，不成立即抛出。castclass 抛 InvalidCast；
/// (x as T).M() 的 isinst 失败得到 null，随后的访问抛空引用，clang 把两者合成同样的检查链。两次抛出必须是同一种异常。
/// </summary>
internal static class CheckedTypeChainsPass
{
    internal static bool CheckedTypeChains(this IdiomState state)
    {
        var changed = false;
        foreach (var head in state.function.Blocks)
            for (var index = 0; index < head.Statements.Count; index++)
            {
                if (head.Statements[index] is not Call { Op.Kind: OpKind.ThrowIf, Op.Operand: "System.InvalidCastException" or "System.NullReferenceException", Arguments: [Compare { Cond: Cond.Ult } depth] } first || StripConversionsPass.StripConversions(state.defs.Expand(depth.Left)) is not Load { Width: 8, Address: Binary { Op: BinOp.Add, Right: Const { Value: RuntimeLayout.ClassTypeHierarchyDepth } } objectDepth } || StripConversionsPass.StripConversions(state.defs.Expand(depth.Right)) is not Load { Width: 8, Address: Binary { Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class } type, Right: Const { Value: RuntimeLayout.ClassTypeHierarchyDepth } } })
                    continue;
                var klass = StripConversionsPass.StripConversions(state.RawDefinition(StripConversionsPass.StripConversions(depth.Left)) ?? depth.Left);
                var objectClass = klass is Load { Address: Binary { Left: var basis } } ? basis : objectDepth.Left;
                var hierarchy = new Load(Fold.Simplify(new Binary(BinOp.Add, objectDepth.Left, Expr.C(RuntimeLayout.ClassTypeHierarchy, 64), 64)), 64);
                foreach (var (block, statement) in ReSekai.Decompiler.Idioms.AllocationPairing.Following(head, index))
                {
                    if (statement is Define or Nop)
                        continue;
                    if (statement is not Call { Op.Kind: OpKind.ThrowIf, Arguments: [Compare { Cond: Cond.Ne } compare] } second || !Equals(second.Op!.Operand, first.Op!.Operand))
                        break;
                    var expanded = state.defs.Expand(compare);
                    if (expanded is not Compare test || test.Right != type || test.Left is not Load { Width: 64 } item || !TermsPass.Terms(item.Address).Contains(hierarchy))
                        break;
                    var result = state.Fresh();
                    head.Statements.Insert(index, new Call(first.Address, CallKind.Direct, Expr.C(0, 64)) { Op = new RuntimeOp(OpKind.TypeCheck, type.Value), Arguments = [objectClass], Results = [result.Storage], Defines = [result], });
                    head.Statements[++index] = first with
                    {
                        Arguments = [new Compare(Cond.Eq, new Use(result, 64), Expr.C(0, 64))]
                    };
                    block.Statements[block.Statements.IndexOf(second)] = new Nop(second.Address, "TypeCheck");
                    state.Result.Hit(Template.InlineTypeCheck);
                    changed = true;
                    break;
                }
            }

        return changed;
    }

    // 类型检查成功蕴含对象的继承深度足够；对象的类在后续显式转换时仍是同一个。
    internal static bool RepeatedTypeDepthChecks(this IdiomState state)
    {
        var function = state.function;
        var checks = function.Blocks.SelectMany(b => b.Statements).OfType<Call>()
            .Where(c => c is { Op.Kind: OpKind.TypeCheck, Arguments.Length: 1, Defines.Length: 1 })
            .ToDictionary(c => c.Defines[0]);
        if (checks.Count == 0) return false;
        var dominators = Dominators.Compute(function);
        Expr Register(Expr expression) => state.defs.Expand(expression, v => state.defs.Raw(v) is not Load);
        Expr Value(Expr expression) => expression is Use use ? state.defs.Raw(use.Variable) ?? expression : expression;
        var changed = false;
        foreach (var test in function.Blocks)
        {
            if (test.Statements.LastOrDefault() is not Branch branch
                || state.defs.Expand(branch.Condition) is not Compare { Cond: Cond.Eq or Cond.Ne, Left: Use flag, Right: Const { Value: 0 } } result
                || !checks.TryGetValue(flag.Variable, out var check)
                || Value(Register(check.Arguments[0])) is not Load { Width: 64, Address: Use } klass) continue;
            var address = result.Cond == Cond.Eq ? branch.Fallthrough : branch.Target;
            var success = test.NormalSuccessors.FirstOrDefault(b => b.Start == address);
            if (success is null || success.IsLandingPad || success.Predecessors is not [var source] || source != test) continue;
            foreach (var block in function.Blocks.Where(b => dominators.Dominates(success.Index, b.Index)))
                for (var i = 0; i < block.Statements.Count; i++)
                {
                    if (block.Statements[i] is not Call { Op.Kind: OpKind.ThrowIf, Op.Operand: "System.InvalidCastException", Arguments: [Compare { Cond: Cond.Ult } depth] } guard
                        || Value(StripConversionsPass.StripConversions(Register(depth.Left))) is not Load { Width: 8, Address: Binary { Op: BinOp.Add, Right: Const { Value: RuntimeLayout.ClassTypeHierarchyDepth } } actual }
                        || Value(Register(actual.Left)) != klass
                        || StripConversionsPass.StripConversions(state.defs.Expand(depth.Right)) is not Load { Width: 8, Address: Binary { Op: BinOp.Add,
                            Left: Symbol { Kind: SymbolKind.Class, Value: var type }, Right: Const { Value: RuntimeLayout.ClassTypeHierarchyDepth } } }
                        || !Equals(type, check.Op!.Operand)) continue;
                    block.Statements[i] = guard with { Arguments = [Expr.C(0, 1)] };
                    changed = true;
                }
        }
        return changed;
    }
}
