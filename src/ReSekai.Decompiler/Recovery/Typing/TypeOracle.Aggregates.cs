using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Typing;

/// <summary>字段的已证明位来源；Projection 相对于待组成的整体。</summary>
public sealed record ProjectedSym(ValueProjection Projection, Sym Value);

/// <summary>结构体表示计划：优先使用完整读取，否则由非零字段组成，未列出的字段由 initobj 初始化。</summary>
public sealed record AggregatePlan(Sym? Whole, IReadOnlyList<ProjectedSym> Fields)
{
    /// <summary>不为 null 时，值是这次整体读取（快照）改写 Fields 中的直接字段（<see cref="TypeOracle.UpdatedRead"/>）。</summary>
    public Sym? Snapshot { get; init; }
}

public sealed partial class TypeOracle
{
    /// <summary>错位复制拆开的引用仍须在每条入边上取同一个完整的引用。</summary>
    internal PhiSym? RejoinedReference(ConcatSym bits, TypeRef expected)
    {
        if (bits.Width != 64 || !ManagedTypes.IsReferenceType(expected)) return null;
        var parts = new List<Sym>(bits.Parts.Length);
        foreach (var part in bits.Parts)
            switch (SymTable.Resolve(part))
            {
                case PhiSym phi: parts.Add(phi); break;
                case SliceSym { Base: PhiSym whole, Low: var low, Width: var width }:
                    parts.Add(values.Syms.Projection(whole, low, width));
                    break;
                default: return null;
            }
        var phis = parts.Cast<PhiSym>().ToArray();
        var first = phis[0];
        if (phis.Any(p => p.Block != first.Block) || first.Sources.Length == 0) return null;
        var sources = new Sym[first.Sources.Length];
        for (var edge = 0; edge < sources.Length; edge++)
        {
            if (phis.Any(p => p.Sources[edge] is null)) return null;
            var whole = values.Syms.Concat(phis.Select(p => p.Sources[edge]!));
            if (!SymTable.IsZero(whole) && (TypeOf(whole) is not { } type || !types.IsAssignable(type, expected))) return null;
            sources[edge] = whole;
        }
        var joined = values.Syms.Phi(first.Block, (nameof(RejoinedReference), bits), bits.Width);
        for (var edge = 0; edge < sources.Length; edge++) values.Syms.SetSource(joined, edge, sources[edge]);
        Use(joined, expected);
        return joined;
    }

    /// <summary>没有自然结构体类型、按位解释的值；跨字段或非标准宽度的位片、相邻字段合并的读取没有自然类型，同样按位解释。</summary>
    public bool BitRepresentation(Sym value) => SymTable.Resolve(value) is ConcatSym or ConstSym
        || TypeOf(value) is null && (SymTable.Resolve(value) is SliceSym || SymTable.Resolve(value) is ReadSym merged && Parts(merged) is not null
            // 非标准宽度的运算结果不是 IL 标量（按位打包在寄存器里返回的小结构体），只能按位段取用。
            // 选择除外：两路整个值之间的选择（JoinedSelect）按期望的结构体类型选择两路，不拆成位段。
            || SymTable.Resolve(value) is OpSym { Width: not (1 or 8 or 16 or 32 or 64), Op.Kind: not OperatorKind.Select })
        || TypeOf(value) is { } scalar && types.Kind(scalar) is TypeKind.Int or TypeKind.Bool or TypeKind.Float or TypeKind.Double or TypeKind.Pointer;

    /// <summary>
    /// 各段都是同一条件下的选择时，拼接就是按该条件在两路拼接之间选择（clang 把结构体值的条件选择拆到每一段上）；不成立时为 null。
    /// </summary>
    public Sym? JoinedSelect(ConcatSym bits)
    {
        Sym? condition = null;
        var (chosen, other) = (new List<Sym>(), new List<Sym>());
        foreach (var part in bits.Parts)
        {
            if (SymTable.Resolve(part) is UndefSym)
            {
                chosen.Add(part);
                other.Add(part);
                continue;
            }
            var (root, low) = SymTable.Resolve(part) is SliceSym slice ? (SymTable.Resolve(slice.Base), slice.Low) : (SymTable.Resolve(part), 0);
            if (root is not OpSym { Op.Kind: OperatorKind.Select, Args: [var test, var yes, var no] }
                || condition is not null && !ReferenceEquals(SymTable.Resolve(test), condition)) return null;
            condition = SymTable.Resolve(test);
            chosen.Add(values.Syms.Slice(yes, low, part.Width));
            other.Add(values.Syms.Slice(no, low, part.Width));
        }
        return condition is null ? null : values.Syms.Select(condition, values.Syms.Concat(chosen), values.Syms.Concat(other), bits.Width);
    }

    /// <summary>寄存器中的选择宽于整个结构体时，去掉两个来源中不属于结构体的高位。</summary>
    internal OpSym? NarrowStructChoice(Sym bits, DefinitionRef structure)
    {
        if (bits is not SliceSym { Low: 0, Base: OpSym { Op.Kind: OperatorKind.Select, Args: [var test, var yes, var no] }, Width: var width }
            || width != types.StorageBits(structure)) return null;
        var taken = values.Syms.Slice(yes, 0, width);
        var other = values.Syms.Slice(no, 0, width);
        bool Whole(Sym arm) => structure.Equals(TypeOf(arm)) || SymTable.Segments(arm).All(SymTable.IsZero);
        return Whole(taken) && Whole(other) ? values.Syms.Select(test, taken, other, width) as OpSym : null;
    }

    /// <summary>先证明整体读取，再按共享布局投影字段；只省略已知为零的位，不省略未知或未定义值。</summary>
    public AggregatePlan? Aggregate(Sym bits, DefinitionRef structure)
    {
        if (WholeRead(bits, structure) is { } whole) return new(whole, []);
        if (WholeCarrier(bits, structure) is { } carrier && !ReferenceEquals(carrier, bits)) return new(carrier, []);
        if (UpdatedRead(bits, structure) is var (snapshot, changed)) return new(null, changed) { Snapshot = snapshot };
        if (FieldValues(bits, structure) is not { } projected) return null;
        var fields = new List<ProjectedSym>();
        foreach (var (projection, part) in projected)
        {
            // 超过单个寄存器的零字段由多段零拼成，也由 initobj 提供。
            if (SymTable.Segments(part).All(SymTable.IsZero)) continue;
            // 一次宽读取的一段正好是内层结构体（q 寄存器复制跨过嵌套结构体）时，同样按叶子取。
            if (projection.Type is DefinitionRef nested && types.Kind(nested) == TypeKind.Struct && (BitRepresentation(part) || SymTable.Resolve(part) is SliceSym { Base: ReadSym }))
            {
                if (Aggregate(part, nested) is not { } inner) return null;
                if (inner.Whole is { } source) fields.Add(new(projection, source));
                else fields.AddRange(inner.Fields.Select(f => new ProjectedSym(f.Projection with
                {
                    Path = [.. projection.Path, .. f.Projection.Path],
                    Bit = projection.Bit + f.Projection.Bit,
                }, f.Value)));
            }
            else fields.Add(new(projection, part));
        }
        return new(null, fields);
    }

    /// <summary>
    /// clang 把结构体局部拆成叶子的 φ（SROA）后按叶子组成：有不可访问的字段或不可定型的宽 φ 时，还原同块的结构体 φ。
    /// 给出各字段对应的 φ 与片的低位；不成立时为 null。类型使用分析与下降共用这一判定。
    /// </summary>
    public IReadOnlyList<(ValueProjection Projection, PhiSym Phi, int Low)>? StructPhiLeaves(AggregatePlan plan)
    {
        if (plan.Fields.Count == 0) return null;
        var leaves = new List<(ValueProjection Projection, PhiSym Phi, int Low)>();
        foreach (var field in plan.Fields)
            switch (SymTable.Resolve(field.Value))
            {
                case PhiSym phi: leaves.Add((field.Projection, phi, 0)); break;
                case SliceSym { Base: var root, Low: var low } when SymTable.Resolve(root) is PhiSym phi: leaves.Add((field.Projection, phi, low)); break;
                default: return null;
            }
        return leaves.All(l => l.Phi.Block == leaves[0].Phi.Block)
            && (leaves.Any(l => l.Projection.Path.Any(p => !ManagedAccess.Field(types, method, p, p.DeclaringType)))
                || leaves.Any(l => l.Phi.Width > 64 && TypeOf(l.Phi) is null)) ? leaves : null;
    }

    /// <summary>结构体 φ 在第 index 条入边上的整个值：各叶子 φ 在这条边上的来源按布局拼接，叶子之间的空位未定义。</summary>
    public Sym? StructPhiEdge(IReadOnlyList<(ValueProjection Projection, PhiSym Phi, int Low)> leaves, DefinitionRef structure, int index)
    {
        var total = types.Size(structure) * 8;
        var parts = new List<Sym>();
        var at = 0;
        foreach (var (projection, phi, low) in leaves.OrderBy(l => l.Projection.Bit))
        {
            if (phi.Sources[index] is not { } source || projection.Bit < at) return null;
            if (projection.Bit > at) parts.Add(values.Syms.Undef(projection.Bit - at));
            parts.Add(values.Syms.Slice(source, low, projection.Width));
            at = projection.Bit + projection.Width;
        }
        if (total <= 0 || at > total) return null;
        if (at < total) parts.Add(values.Syms.Undef(total - at));
        return values.Syms.Concat(parts);
    }

    /// <summary>
    /// 结构体 φ 在第 index 条入边上能整体取得的值：各叶子按布局取自同一个值时保留整体；
    /// 取自更宽的 φ 时须覆盖嵌套字段的全部叶子。各叶子来自同一内存位置时尝试整体读取。
    /// </summary>
    public Sym? StructPhiWhole(IReadOnlyList<(ValueProjection Projection, PhiSym Phi, int Low)> leaves, DefinitionRef structure, int index)
    {
        Sym? whole = null;
        int? offset = null;
        foreach (var (projection, phi, low) in leaves)
        {
            var part = phi.Sources[index] is { } piece ? SymTable.Resolve(values.Syms.Slice(piece, low, projection.Width)) : null;
            var (root, bit) = part switch
            {
                SliceSym slice => (SymTable.Resolve(slice.Base), slice.Low),
                PhiSym { Projection: { } nested } => (nested.Whole, nested.Low),
                { } entire when projection.Bit == 0 => (entire, 0),
                _ => (null, -1),
            };
            var start = bit - projection.Bit;
            if (root is null || start < 0 || offset is { } previous && previous != start
                || whole is not null && !ReferenceEquals(whole, root)) { whole = null; break; }
            whole = root;
            offset = start;
        }
        var width = types.StorageBits(structure);
        if (whole is not null && offset is { } at && width > 0)
        {
            if (at == 0 && width == whole.Width) return whole;
            if (at + width <= whole.Width && whole is PhiSym phi && types.Projections(structure).All(field =>
                leaves.Any(l => l.Projection.Bit == field.Bit && l.Projection.Width == field.Width)))
            {
                var nested = values.Syms.Projection(phi, at, width);
                if (ProjectedField(nested) is { } field && field.Type.Equals(structure)) return nested;
            }
        }
        if (StructPhiEdge(leaves, structure, index) is not { } edge) return null;
        if (WholeRead(edge, structure) is { } read) return read;
        // 每条边分别保留整值和条件，避免把同一次条件选择的各个机器字当成独立字段。
        if (edge is ConcatSym concat && JoinedSelect(concat) is OpSym { Args: [_, var yes, var no] } choice
            && StructureCarrier(yes, structure) && StructureCarrier(no, structure)) return choice;
        return WholeCarrier(edge, structure) ?? (StructureCarrier(edge, structure) ? edge : null);
    }

    private bool StructureCarrier(Sym value, DefinitionRef structure)
    {
        value = SymTable.Resolve(value);
        if (WholeCarrier(value, structure) is not null) return true;
        if (value is not ConcatSym concat || StructPhiLeaves(new AggregatePlan(null, types.Projections(structure)
                .Select(p => new ProjectedSym(p, values.Syms.Slice(concat, p.Bit, p.Width))).ToArray())) is not { } leaves)
            return false;
        return Enumerable.Range(0, leaves[0].Phi.Sources.Length).All(j =>
            StructPhiEdge(leaves, structure, j) is { } incoming && WholeCarrier(incoming, structure) is not null);
    }

    /// <summary>全部有效字段对应同一结构体值的原位切片；寄存器填充无需作为托管值读取。</summary>
    private Sym? WholeCarrier(Sym value, DefinitionRef structure)
    {
        if (structure.Equals(TypeOf(value))) return value;
        if (value is not ConcatSym concat || value.Width != types.StorageBits(structure)) return null;
        Sym? whole = null;
        var fields = types.Projections(structure).ToArray();
        if (fields.Length == 0 || fields.Any(field => !field.Fits(value.Width))) return null;
        foreach (var field in fields)
        {
            if (values.Syms.Slice(value, field.Bit, field.Width) is not SliceSym slice
                || slice.Low != field.Bit || !structure.Equals(TypeOf(slice.Base))
                || whole is not null && !ReferenceEquals(whole, slice.Base)) return null;
            whole = slice.Base;
        }
        var at = 0;
        foreach (var part in concat.Parts)
        {
            if (part is not UndefSym && (part is not SliceSym slice || slice.Low != at || !ReferenceEquals(slice.Base, whole))) return null;
            at += part.Width;
        }
        return whole;
    }

    /// <summary>结构体 φ 的每条入边都能整体取得：值与写入都整体进行，不需要逐个访问叶子。</summary>
    public IReadOnlyList<(ValueProjection Projection, PhiSym Phi, int Low)>? WholeStructPhi(AggregatePlan plan, DefinitionRef structure) =>
        StructPhiLeaves(plan) is { } leaves && Enumerable.Range(0, leaves[0].Phi.Sources.Length).All(j => StructPhiWhole(leaves, structure, j) is not null) ? leaves : null;

    /// <summary>直接字段的位来源，供值组成与逐字段写入共用。</summary>
    public IReadOnlyList<ProjectedSym>? FieldValues(Sym bits, DefinitionRef structure)
    {
        if (types.Kind(structure) != TypeKind.Struct || types.Layouts.Of(structure) is null) return null;
        var fields = types.Fields(structure).ToArray();
        return fields.Any(p => !p.Fits(bits.Width)) ? null
            : fields.Select(p => new ProjectedSym(p, values.Syms.Slice(bits, p.Bit, p.Width))).ToArray();
    }

    /// <summary>整体位写入可分解的非零叶子；整体读取、快照改写与各入边都能整体取得的结构体 φ 仍保留为整体复制。</summary>
    public IReadOnlyList<ValueProjection>? FieldStores(Sym bits, TypeRef type)
    {
        if (type is not DefinitionRef structure || types.Kind(type) != TypeKind.Struct || !BitRepresentation(bits)
            || types.StorageBits(type) > bits.Width || Aggregate(bits, structure) is { } plan && (plan.Whole is not null || plan.Snapshot is not null || WholeStructPhi(plan, structure) is not null)) return null;
        var leaves = types.Projections(structure).ToArray();
        // 布局未知的叶子（共享泛型字段）没有位范围，整体不能按位分解。
        if (leaves.Any(p => !p.Fits(bits.Width))) return null;
        return leaves.Where(p => !SymTable.Segments(values.Syms.Slice(bits, p.Bit, p.Width)).All(SymTable.IsZero)).ToArray();
    }
}
