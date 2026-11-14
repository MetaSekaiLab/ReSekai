using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

internal static class UnboxGuardsPass
{
    internal static bool UnboxGuards(this IdiomState state) => Run(state.function, state.defs);

    internal static bool Run(MachineFunction function, IdiomRewriter.Defs defs)
    {
        var changed = false;
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call { Op.Kind: OpKind.ThrowIf, Op.Operand: "System.InvalidCastException", Arguments: [Compare { Cond: Cond.Ne } compare] } check
                    || defs.Expand(compare.Left) is not Load { Width: 64, Address: Binary { Op: BinOp.Add, Left: Load { Width: 64, Address: var obj }, Right: Const { Value: RuntimeLayout.ClassElementClass } } }
                    || defs.Expand(compare.Right) is not Load { Width: 64, Address: Binary { Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class, Value: TypeRef type }, Right: Const { Value: RuntimeLayout.ClassElementClass } } })
                    continue;
                var following = block.Statements.Skip(i + 1).FirstOrDefault(s => !Transparent(s, allowStaticReads: true));
                Block? successor = null;
                var moveUnbox = false;
                if (following is null && block.NormalSuccessors.ToArray() is [var next]
                    && next.Predecessors.Count == 1 && !next.IsLandingPad && block.Handler == next.Handler)
                {
                    var allowStaticReads = block.Handler is null;
                    if (!block.Statements.Skip(i + 1).All(s => Transparent(s, allowStaticReads))) continue;
                    following = next.Statements.FirstOrDefault(s => !Transparent(s, allowStaticReads));
                    successor = next;
                    if (block.Handler is null && following is not Call)
                    {
                        // 先检查类型再读取字段，最后取拆箱后的地址。把 unbox 放回检查点，保留字段读取的异常顺序。
                        following = next.Statements.FirstOrDefault(s => !BeforeUnbox(s));
                        moveUnbox = true;
                    }
                }
                if (following is not Call { Op.Kind: OpKind.Unbox, Arguments: [var value, ..] } unbox
                    || !Equals(unbox.Op.Operand, type) || !Equals(defs.Expand(value), obj))
                    continue;
                Call? moved = null;
                if (moveUnbox)
                {
                    // 拆箱的参数可能引用后继块中的定义；只搬移已在检查点计算好的对象与类。
                    if (obj is not (Entry or Use) || unbox.Arguments is not [_, var klass]
                        || defs.Expand(klass) is not Symbol { Kind: SymbolKind.Class } classSymbol) continue;
                    moved = unbox with { Address = check.Address, Arguments = [obj, classSymbol] };
                }
                if (successor is not null && block.Handler is { } handler)
                {
                    // 抛出点移到拆箱后，处理体必须仍收到相同的入边值。
                    if (block.Statements.Take(i).Any(s => s is Call)
                        || successor.Statements.SkipWhile(s => !ReferenceEquals(s, unbox)).Skip(1).Any(s => !Transparent(s, allowStaticReads: false)))
                        continue;
                    var fromCheck = MachineFunction.PredecessorIndex(block, block.Successors.Count - 1);
                    var fromUnbox = MachineFunction.PredecessorIndex(successor, successor.Successors.Count - 1);
                    if (handler.Statements.OfType<Phi>().Any(phi => phi.Sources[fromCheck] != phi.Sources[fromUnbox])) continue;
                    MachineFunction.RemoveEdgeAt(block, block.Successors.Count - 1);
                }
                // unbox 在同一位置执行类型与 null 检查，原生 element_class 检查不另造托管字段访问。
                if (moved is not null)
                {
                    block.Statements[i] = moved;
                    successor!.Statements.Remove(unbox);
                }
                else block.Statements[i] = new Nop(check.Address, "拆箱类型检查");
                changed = true;
            }

        return changed;
    }

    private static bool BeforeUnbox(Stmt statement) => statement is Nop or Jump
        || statement is Define define && !ExprMap.Walk(define.Value).Any(part => part is Intrinsic or Unknown or Ir.Read
            or Binary { Op: BinOp.SDiv or BinOp.UDiv or BinOp.SRem or BinOp.URem });

    // beforefieldinit 静态字段读取可在拆箱之后执行；值的定义仍留在原位置。
    private static bool Transparent(Stmt statement, bool allowStaticReads) => statement is Nop or Jump
        || statement is Define define && ExprMap.Walk(define.Value).All(part => part switch
        {
            Load load => allowStaticReads && SplitPass.Split(load.Address) is (Symbol { Kind: SymbolKind.StaticFields, Value: DefinitionRef owner }, _)
                && (owner.Definition.Attributes & System.Reflection.TypeAttributes.BeforeFieldInit) != 0,
            Intrinsic or Unknown or Ir.Read or Binary { Op: BinOp.SDiv or BinOp.UDiv or BinOp.SRem or BinOp.URem } => false,
            _ => true,
        });
}
