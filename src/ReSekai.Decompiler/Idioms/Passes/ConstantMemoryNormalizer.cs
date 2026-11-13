using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

/// <summary>把定长复制与清零展开为访存；复制先读取全部源值，保留重叠区域的语义。</summary>
internal static class ConstantMemoryNormalizer
{
    public static bool Run(MachineFunction function, Func<Variable> fresh)
    {
        var changed = false;
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call
                    {
                        Op.Kind: OpKind.Import,
                        Arguments: [var start, var source, Const { Value: > 0 and <= 4096 } size]
                    } call) continue;
                var zero = call.Op.Operand is "memset" && source is Const { Value: 0 };
                var copy = call.Op.Operand is "memcpy" or "memmove";
                if (!zero && !copy) continue;
                Expr At(Expr root, int offset) => root is FrameAddress frame ? new FrameAddress(frame.Offset + offset)
                    : offset == 0 ? root : new Binary(BinOp.Add, root, Expr.C((ulong)offset, 64), 64);
                var writes = new List<Stmt>();
                var reads = new List<Stmt>();
                // 栈帧仍按小段提升；其他位置保留整段宽度，便于识别一次完整的结构体写入。
                var wholeZero = zero && start is not FrameAddress;
                if (wholeZero) writes.Add(new Store(call.Address, start, Expr.C(0, (int)size.Value * 8)));
                for (var offset = 0; !wholeZero && offset < (int)size.Value;)
                {
                    var bytes = Math.Min(8, (int)size.Value - offset);
                    if (bytes is not (1 or 2 or 4 or 8)) bytes = 1;
                    Expr value = Expr.C(0, bytes * 8);
                    if (copy)
                    {
                        var temp = fresh();
                        reads.Add(new Define(call.Address, temp, new Load(At(source, offset), bytes * 8)));
                        value = new Use(temp, bytes * 8);
                    }
                    writes.Add(new Store(call.Address, At(start, offset), value));
                    offset += bytes;
                }
                writes.InsertRange(0, reads);
                if (call.Defines.FirstOrDefault() is { } result) writes.Add(new Define(call.Address, result, start));
                // 内存函数返回目标指针；尾调用展开后仍须保留终结点，供结果缓冲区提升收集写入。
                if (call.Kind is CallKind.Tail or CallKind.TailIndirect)
                    writes.Add(new Return(call.Address) { Pieces = [new(0, start)] });
                block.Statements.RemoveAt(i);
                block.Statements.InsertRange(i, writes);
                i += writes.Count - 1;
                changed = true;
            }
        return changed;
    }
}
