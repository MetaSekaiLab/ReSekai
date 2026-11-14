using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Typing;

public sealed partial class TypeOracle
{
    private Location? FixedBufferLocation(Sym root, long offset, Sym? index, int scale, int width)
    {
        // 固定缓冲区没有边界检查。按元数据选取缓冲区，保留原访问的全部字节偏移。
        if (width is not (8 or 16 or 32 or 64) || TypeOf(root) is not ByRefRef { Element: DefinitionRef owner }
            || !ManagedAccess.Type(types, method.Method.Owner, owner) || types.Layouts.Of(owner) is not { } layout) return null;
        foreach (var slot in layout.Fields)
        {
            var first = slot.Offset - RuntimeLayout.ObjectHeaderSize;
            var size = width / 8;
            if (slot.Type is not DefinitionRef { Definition: { Fields: [var firstElement] } } buffer || firstElement.Offset != 0
                || firstElement.IsStatic || !ManagedAccess.Field(types, method, new(slot.Field, owner), owner)
                || slot.Field.CustomAttributes.SingleOrDefault(a =>
                    a.Constructor.Owner.FullName == "System.Runtime.CompilerServices.FixedBufferAttribute") is not
                    { Arguments: [{ Value: TypeRef element }, { Value: int count and > 0 }] }
                || !firstElement.Type.Equals(element) || !types.Unmanaged(element) || types.StorageBits(element) != width
                || types.Size(buffer) != (long)count * size) continue;
            var inside = offset - first;
            // 强度削减可把字段起点留在循环偏移的初值里（例如 0x30 + φ，φ 从 -16 起）。
            var seed = index is PhiSym phi && scale == 1 && ConstantCursorSeed(phi) is { } start ? start : 0;
            if (inside + seed < 0 || inside + seed >= types.Size(buffer) || inside % size != 0) continue;
            var displacement = index is null ? values.Syms.Const(0, 64) : values.Syms.ZeroExtend(index, 64);
            if (index is not null && scale != 1) displacement = values.Syms.Binary(BinOp.Mul, displacement, values.Syms.Const((ulong)scale, 64), 64);
            // 循环可按字节递增；对多字节元素，动态部分也必须按同样的对齐访问。
            if (scale % size != 0 && LowZeros(displacement) < System.Numerics.BitOperations.TrailingZeroCount((uint)size)) continue;
            displacement = values.Syms.Binary(BinOp.Add, displacement, values.Syms.Const((ulong)(offset - first), 64), 64);
            return new Location(LocationKind.Reference, root, null, 0, null, [new(slot.Field, owner), new(firstElement, buffer)], element)
                { FixedBuffer = true, ByteOffset = displacement };
        }
        return null;
    }

    private long? ConstantCursorSeed(PhiSym phi)
    {
        var edges = phi.Sources.Where(s => s is not null).Select(s => AddressTerms(s!)).ToArray();
        if (edges.Length != phi.Sources.Length || edges.Any(e => e.Index is not null)
            || !edges.Any(e => ReferenceEquals(e.Root, phi))) return null;
        return edges.Where(e => !ReferenceEquals(e.Root, phi)).Select(e => e.Root).Distinct().ToArray() is [ConstSym seed]
            ? seed.Signed : null;
    }

    private readonly Dictionary<PhiSym, (Sym Root, long Offset, PhiSym Index)?> fixedBufferCursors = [];

    /// <summary>固定缓冲区游标可把不变的托管基址留在循环外，循环只递推其字节偏移。</summary>
    private (Sym Root, long Offset, PhiSym Index)? FixedBufferCursor(PhiSym phi)
    {
        if (fixedBufferCursors.TryGetValue(phi, out var known)) return known;
        fixedBufferCursors[phi] = null;
        if (phi.Width != 64 || phi.Projection is not null || phi.Sources.Any(s => s is null)) return null;
        var edges = phi.Sources.Select(s => AddressTerms(s!)).ToArray();
        var bases = edges.Where(e => !ReferenceEquals(e.Root, phi)).Distinct().ToArray();
        if (bases is not [(ArgSym { Index: -1 } or LocalSym, var offset, var initial, var scale)]
            || edges.Any(e => ReferenceEquals(e.Root, phi) && e.Index is not null)) return null;
        var root = bases[0].Root;
        if (edges.All(e => !ReferenceEquals(e.Root, phi))
            || root is LocalSym local && memory.Find(local.Space, local.Offset, 1) is not { Promoted: false, TypedStorage: not null }
            || root is ArgSym && !new[] { 8, 16, 32, 64 }.Any(w =>
                FixedBufferLocation(root, offset, values.Syms.Const(0, 64), 1, w) is not null)) return null;
        var seed = initial is null ? values.Syms.Const(0, 64) : values.Syms.Binary(BinOp.Mul,
            values.Syms.ZeroExtend(initial, 64), values.Syms.Const((ulong)scale, 64), 64);
        var counter = values.Syms.Phi(phi.Block, (phi.Origin, nameof(FixedBufferCursor)), 64);
        for (var i = 0; i < edges.Length; i++)
        {
            var (origin, step, _, _) = edges[i];
            values.Syms.SetSource(counter, i, ReferenceEquals(origin, phi)
                ? values.Syms.Binary(BinOp.Add, counter, values.Syms.Const(unchecked((ulong)step), 64), 64)
                : seed);
        }
        return fixedBufferCursors[phi] = (root, offset, counter);
    }
}
