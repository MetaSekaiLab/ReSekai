using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

/// <summary>分配专用的直线链证明；不扩大判空、类初始化等其他 Following 使用者的吸收范围。</summary>
internal static class AllocationPairing
{
    internal static IEnumerable<(Block Block, Stmt Statement)> Following(Block block, int index) =>
        StraightLine.Following(block, index, StraightLinePolicy.Allocation);

    internal static bool Uses(Stmt statement, Variable created) =>
        statement is Phi phi && phi.Sources.Contains(created)
        || ExprMap.Operands(statement).SelectMany(ExprMap.Walk).Any(e => e is Use u && u.Variable == created);

    /// <summary>构造函数的栈实参槽只存已经求值的内容，不引入内存读取。</summary>
    private static bool OutgoingArgumentStore(Store store, Call constructor, MachineSignature signature)
    {
        if (store.Target is not FrameAddress target || constructor.StackPointer is not FrameAddress { Offset: < 0 } stack
            || target.Offset >= 0 || ExprMap.Walk(store.Value).Any(e => e is Load or Intrinsic or Unknown)) return false;
        bool Matches(AbiPiece piece) => piece.Register.Bank == Bank.Stack && stack.Offset + piece.Register.Index == target.Offset
            && piece.Width == store.Value.Width && target.Offset + piece.Width / 8 <= 0;
        return signature.MethodInfo is { } info && Matches(info) || signature.Arguments.SelectMany(a => a.Pieces).Any(Matches);
    }

    internal static string? MotionProblem(CodeFunction range, Block source, Call allocation, Block destination,
        Call constructor, IEnumerable<Stmt> middle, Variable created, MachineSignature? signature)
    {
        var first = range.ExceptionRegions.FirstOrDefault(r => r.Start <= allocation.Address && allocation.Address < r.End);
        var last = range.ExceptionRegions.FirstOrDefault(r => r.Start <= constructor.Address && constructor.Address < r.End);
        foreach (var statement in middle)
        {
            if (Uses(statement, created)) return "构造之前分配结果已被使用";
            if (statement is Nop || statement is Define d && !ExprMap.Walk(d.Value).Any(e => e is Load or Intrinsic or Unknown)) continue;
            // NewObject 已完成相同类型的布局初始化；重复 Class::Init 不运行静态构造。
            if (statement is Call { Op.Kind: OpKind.InitClass } init && allocation.Op is { Kind: OpKind.NewObject, Operand: { } type }
                && Equals(init.Op.Operand, type)) continue;
            if (statement is Store store && signature is not null && source.Handler is null && destination.Handler is null
                && (first?.LandingPad ?? 0) == 0 && (last?.LandingPad ?? 0) == 0 && OutgoingArgumentStore(store, constructor, signature)) continue;
            return "分配与构造之间有可观察操作，不能移动分配";
        }
        // newobj 同时承载分配（含 OOM / 类初始化）和构造的异常。两端必须有同一异常去向，
        // 相同落地点但不同 LSDA action 也不能合并；中间没有可观察操作才允许移动分配时点。
        if (source.Handler != destination.Handler || (first?.LandingPad ?? 0) != (last?.LandingPad ?? 0)
            || (first?.ActionOffset ?? 0) != (last?.ActionOffset ?? 0))
            return "分配与构造的异常去向不同，不能合并为 newobj";
        return null;
    }
}
