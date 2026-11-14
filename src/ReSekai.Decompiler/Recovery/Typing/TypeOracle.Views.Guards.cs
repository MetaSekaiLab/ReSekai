using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Typing;

/// <summary>
/// 类型判断之后的视图。对象 o 通过类型判断之后，clang 仍可能经 o 本身访问判断类型的字段
/// （模式变量 o is T t 之后读 t.f，或几条判断路径的尾部合并后共用一次读取）。
/// 读写在 o 的静态类型上定位不到时，沿支配树由近及远取对 o 的判断：isinst&lt;T&gt;(o) 的结果（按它定位），
/// 或内联继承检查 TypeCheck&lt;T&gt;(o 的类) 成立的分支、类指针等于密封类 T 的分支、类指针不是密封类 T 就抛出的检查之后（位置带上视图类型 T，写出为 castclass）。
/// isinst 的结果按启发式使用：假定原生代码只在已知 o 是 T 的路径上按 T 的布局访问 o。
/// </summary>
public sealed partial class TypeOracle
{
    private Dominators? viewDominators;
    private Dictionary<Sym, List<Call>>? typeViews;

    /// <summary>Span.Slice 的两个无符号检查必须都支配组合后的引用。</summary>
    public bool SpanSliceChecked(Block block, Sym source, DefinitionRef span, Sym start, Sym length)
    {
        viewDominators ??= Dominators.Compute(values.Function);
        bool Same(Sym a, Sym b) => ReferenceEquals(SymTable.Resolve(a), SymTable.Resolve(b));
        bool SpanLength(Sym operand) => SymTable.Resolve(operand) is SliceSym { Base: var whole, Low: var low, Width: 32 }
            && Same(whole, source) && types.Project(span, low, 32) is { Path: [{ Field.Name: "_length" }] };
        var guards = values.Function.Blocks.Where(test => test.Statements.LastOrDefault() is Branch
            && test.NormalSuccessors.Count() == 2).Select(test => (test, branch: (Branch)test.Statements[^1]));
        var startChecked = false;
        var lengthChecked = false;
        foreach (var (test, branch) in guards)
        {
            if (values.Operands(branch) is not [OpSym { Op.Kind: OperatorKind.Compare, Op.Cond: var cond, Args: [var left, var right] }]) continue;
            var accepted = test.NormalSuccessors.SingleOrDefault(next => next.Predecessors.Count == 1 && viewDominators.Dominates(next.Index, block.Index));
            if (accepted is null || (accepted.Start == branch.Target ? cond != Cond.Uge : cond != Cond.Ult)) continue;
            startChecked |= SpanLength(left) && Same(right, start);
            lengthChecked |= Same(right, length) && SymTable.Resolve(left) is OpSym { Op.Binary: BinOp.Sub, Args: [var total, var offset] }
                && SpanLength(total) && Same(offset, start);
        }
        return startChecked && lengthChecked;
    }

    /// <summary>经类型判断的视图定位访问；没有判断或按判断类型也定位不到时为 null。</summary>
    public Location? ViewLocate(Sym address, int width, Stmt site)
    {
        var block = values.BlockOf(site);
        return ViewLocate(address, width, block, block.Statements.IndexOf(site));
    }

    /// <summary>访问若位于 block 的第 position 条语句之前，经类型判断的视图定位。</summary>
    public Location? ViewLocate(Sym address, int width, Block block, int position)
    {
        var (root, offset, index, _) = Decompose(address);
        if (index is not null) return null;
        root = SymTable.Resolve(root);
        if (RepeatedCast(root, block, position) is { } cast)
            return Locate(values.Syms.Binary(BinOp.Add, cast, values.Syms.Const(unchecked((ulong)offset), 64), 64), width);
        // 同一对象先被判断为基类、再判断为派生类时，后续读取可能仍经第一个 isinst 的结果。
        if (root is ResultSym { Site: { Op.Kind: OpKind.IsInst } baseCast }
            && values.Operands(baseCast) is [_, var original] && ClassOperand(baseCast) is { } baseType)
        {
            foreach (var (type, result) in Guards(SymTable.Resolve(original), block, position))
            {
                if (type.Equals(baseType) || !types.IsAssignable(type, baseType) || result is not ResultSym { Site: { } derivedCast }) continue;
                // 必须由成功分支证明原对象兼容派生类型，避免把可能为 null 的基类转换替换掉。
                var derivedBlock = values.BlockOf(derivedCast);
                if (derivedBlock.Predecessors is not [var predecessor]
                    || Checked(predecessor, derivedBlock, SymTable.Resolve(original)) is not { } checkedType || !checkedType.Equals(type)) continue;
                if (Locate(values.Syms.Binary(BinOp.Add, result, values.Syms.Const(unchecked((ulong)offset), 64), 64), width) is { } located) return located;
            }
        }
        foreach (var (type, result) in Guards(root, block, position))
        {
            if (result is not null)
            {
                if (Locate(values.Syms.Binary(BinOp.Add, result, values.Syms.Const(unchecked((ulong)offset), 64), 64), width) is { } viewed) return viewed;
                continue;
            }
            if (type is DefinitionRef reference && ManagedTypes.IsReferenceType(reference)
                && FieldAt(reference, (int)offset, width, false, false) is var (path, fieldType))
                return new(LocationKind.Instance, root, null, 0, null, path, fieldType) { View = reference };
        }
        return null;
    }

    /// <summary>同一对象再次按目标深度查继承表时，使用支配该位置的同类型转换结果。</summary>
    private Sym? RepeatedCast(Sym root, Block block, int position)
    {
        if (root is not OpSym { Op.Kind: OperatorKind.Select, Args: [OpSym { Op.Kind: OperatorKind.Compare, Op.Cond: Cond.Eq,
                Args: [ReadSym entry, EntitySym { Symbol: { Kind: SymbolKind.Class, Value: TypeRef target } } entity] }, var selected, ConstSym { Value: 0 }] }
            || entry.Address is not OpSym { Op.Kind: OperatorKind.Binary, Op.Binary: BinOp.Add, Args: [ReadSym hierarchy, ConcatSym { Parts: [ConstSym { Width: 3, Value: 0 }, var index] }] }
            || Decompose(hierarchy.Address) is not (ReadSym klass, RuntimeLayout.ClassTypeHierarchy, null, _)
            || ClassOwner(klass) is not { } obj) return null;
        var depth = values.Syms.Read(entry.Site, int.MinValue, values.Syms.Binary(BinOp.Add, entity,
            values.Syms.Const(RuntimeLayout.ClassTypeHierarchyDepth, 64), 64), 8);
        var shiftedDepth = values.Syms.Binary(BinOp.Add, values.Syms.ZeroExtend(depth, 61), values.Syms.Const(unchecked(0UL - 1), 61), 61);
        if (!SameDepth(index, shiftedDepth)) return null;
        if (!ReferenceEquals(SymTable.Resolve(selected), obj) && (SymTable.Resolve(selected) is not ResultSym { Site: { Op.Kind: OpKind.IsInst } earlier }
            || values.Operands(earlier) is not [_, var original] || !ReferenceEquals(SymTable.Resolve(original), obj)
            || ClassOperand(earlier) is not { } baseType || !types.IsAssignable(target, baseType))) return null;
        return Guards(obj, block, position).FirstOrDefault(g => g.Type.Equals(target) && g.Result is not null).Result;

        static bool SameDepth(Sym a, Sym b) => (a, b) switch
        {
            (ReadSym x, ReadSym y) => x.Width == y.Width && ReferenceEquals(x.Address, y.Address),
            (OpSym x, OpSym y) => x.Op.Equals(y.Op) && x.Width == y.Width && x.Args.Length == y.Args.Length && x.Args.Zip(y.Args).All(p => SameDepth(p.First, p.Second)),
            (ConcatSym x, ConcatSym y) => x.Parts.Length == y.Parts.Length && x.Parts.Zip(y.Parts).All(p => SameDepth(p.First, p.Second)),
            _ => ReferenceEquals(a, b),
        };
    }

    /// <summary>由近及远支配这个位置的、对 root 的类型判断：类型与 isinst 的结果（继承检查没有结果）。</summary>
    private IEnumerable<(TypeRef Type, Sym? Result)> Guards(Sym root, Block block, int position)
    {
        typeViews ??= values.Function.Blocks.SelectMany(b => b.Statements).OfType<Call>()
            .Where(c => c.Op?.Kind == OpKind.IsInst && values.Operands(c).Count > 1 && ClassOperand(c) is not null)
            .GroupBy(c => SymTable.Resolve(values.Operands(c)[1])).ToDictionary(g => g.Key, g => g.ToList());
        viewDominators ??= Dominators.Compute(values.Function);
        var casts = typeViews.GetValueOrDefault(root) ?? [];
        var blocks = values.Function.Blocks;
        for (var current = block; ;)
        {
            var limit = current == block ? position : current.Statements.Count;
            foreach (var call in casts.Where(c => values.BlockOf(c) == current && current.Statements.IndexOf(c) < limit)
                .OrderByDescending(c => current.Statements.IndexOf(c)))
                yield return (ClassOperand(call)!, values.Syms.Result(call, 0, 64));
            // 类指针与密封类不同时抛出（as 之后访问成员的空引用检查）：之后 o 就是这个类。
            for (var i = Math.Min(limit, current.Statements.Count) - 1; i >= 0; i--)
                if (current.Statements[i] is Call { Op.Kind: OpKind.ThrowIf } guard
                    && values.Operands(guard).Skip(1).FirstOrDefault() is { } condition
                    && SymTable.Resolve(condition) is OpSym { Op: { Kind: OperatorKind.Compare, Cond: Cond.Ne }, Args: [var left, var right] }
                    && ClassIdentity(left, right) is var (owner, exact) && ReferenceEquals(SymTable.Resolve(owner), root))
                    yield return (exact, null);
            if (current.Predecessors is [var predecessor] && Checked(predecessor, current, root) is { } tested)
                yield return (tested, null);
            var parent = viewDominators.Idom[current.Index];
            if (parent < 0 || parent == current.Index) yield break;
            current = blocks[parent];
        }
    }

    /// <summary>
    /// 地址里的选择在 block 处已由支配它的分支决定条件时取决定的一路（clang 把按条件取对象或空的选择外提到分支之前）。
    /// 条件值是 SSA 值：支配 block 的块只有一个前驱、经条件分支的一侧进入时，到 block 的每条路径上条件都取这一侧的真假。
    /// </summary>
    public Sym Decided(Sym address, Block block)
    {
        address = SymTable.Resolve(address);
        switch (address)
        {
            case OpSym { Op.Kind: OperatorKind.Select, Args: [var condition, var first, var second] } when Known(condition, block) is { } known:
                return Decided(known ? first : second, block);
            case OpSym { Op: { Kind: OperatorKind.Binary } op, Args: [var left, var right] } binary when op.Binary is BinOp.Add or BinOp.Sub:
                {
                    var (l, r) = (Decided(left, block), Decided(right, block));
                    return ReferenceEquals(l, SymTable.Resolve(left)) && ReferenceEquals(r, SymTable.Resolve(right)) ? binary : values.Syms.Binary(op.Binary, l, r, binary.Width);
                }
            default:
                return address;
        }
    }

    /// <summary>由支配 block 的条件分支决定的 condition 真假；沿支配树找不到时为 null。</summary>
    private bool? Known(Sym condition, Block block)
    {
        viewDominators ??= Dominators.Compute(values.Function);
        for (var current = block; ;)
        {
            if (current.Predecessors is [var predecessor] && predecessor.Statements.LastOrDefault() is Branch branch && branch.Target != branch.Fallthrough
                && Implied(values.Operands(branch)[0], branch.Target == current.Start, condition) is { } known)
                return known;
            var parent = viewDominators.Idom[current.Index];
            if (parent < 0 || parent == current.Index) return null;
            current = values.Function.Blocks[parent];
        }
    }

    /// <summary>predecessor 以继承检查 TypeCheck&lt;T&gt;(root 的类) 的结果或 root 的类指针与密封类 T 的比较分支、successor 是检查成立的一侧时为 T。</summary>
    private TypeRef? Checked(Block predecessor, Block successor, Sym root)
    {
        if (predecessor.Statements.LastOrDefault() is not Branch branch || branch.Target == branch.Fallthrough
            || values.Operands(branch) is not [OpSym { Op.Kind: OperatorKind.Compare, Op.Cond: Cond.Eq or Cond.Ne, Args: [var left, var right] } compare]) return null;
        // 类指针等于密封类的一侧：o 就是这个类。
        if (ClassIdentity(left, right) is var (owner, exact) && ReferenceEquals(SymTable.Resolve(owner), root))
            return successor.Start == (compare.Op.Cond == Cond.Eq ? branch.Target : branch.Fallthrough) ? exact : null;
        if (right is not ConstSym { Value: 0 } || SymTable.Resolve(left) is not ResultSym { Site: { Op.Kind: OpKind.TypeCheck } check }
            || values.Operands(check) is not [_, var klass] || ClassOwner(klass) is not { } checkedOwner
            || !ReferenceEquals(SymTable.Resolve(checkedOwner), root) || check.Op.Operand is not TypeRef type) return null;
        var passed = compare.Op.Cond == Cond.Ne ? branch.Target : branch.Fallthrough;
        return successor.Start == passed ? type : null;
    }
}
