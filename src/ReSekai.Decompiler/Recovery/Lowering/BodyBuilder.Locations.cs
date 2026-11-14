using System.Reflection;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Recovery.Control;
using ReSekai.Decompiler.Recovery.Memory;
using ReSekai.Decompiler.Recovery.Runtime;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Lowering;

/// <summary>位置与局部：读取、字段链、可寻址局部与局部的分配。</summary>
internal sealed partial class BodyBuilder
{
    /// <summary>地址逃逸的单元对应可寻址局部，入口副本仍使用按值形参自己的存储。</summary>
    private ManagedValue CellLocal(LocalSym root)
    {
        if (root is { Offset: 0, Space: { Kind: LocalKind.Argument, Argument: var argument } }
            && oracle.Parameter(argument) is { } type)
            return new ArgumentValue(argument, type, Width(type));
        if (!locals.TryGetValue(root, out var local))
        {
            var cell = analysis.Memory.Find(root.Space, root.Offset, 1)!;
            locals[root] = local = NewLocal(oracle.CellType(cell)!);
        }
        return local;
    }

    private LocalValue NewLocal(TypeRef type) => new(new Variable(Storage.Temp(1000 + nextLocal++), 1), type, Width(type));

    private LocalValue Local(Sym sym, TypeRef type)
    {
        sym = SymTable.Resolve(sym);
        if (!locals.TryGetValue(sym, out var local)) locals[sym] = local = NewLocal(type);
        return local;
    }

    private int Width(TypeRef type) => types.ValueWidth(type);

    private Dictionary<Call, HashSet<TypeRef>>? caughtTypes;

    /// <summary>
    /// 每个 catch 的异常类型：处理块里对异常对象的类型判断（il2cpp_class_is_assignable_from）的目标类型；
    /// 判断通过后的字段读取按其中在该偏移处有字段的类型解释；没有判断的 catch 不在其中。
    /// </summary>
    private Dictionary<Call, HashSet<TypeRef>> CaughtTypes()
    {
        if (caughtTypes is not null) return caughtTypes;
        var found = new Dictionary<Call, HashSet<TypeRef>>();
        foreach (var call in analysis.Function.Blocks.SelectMany(b => b.Statements).OfType<Call>().Where(c => c.Op?.Kind == OpKind.IsAssignableFrom))
        {
            var operands = values.Operands(call);
            if (operands.Count < 3 || SymTable.Resolve(operands[1]) is not EntitySym { Symbol: { Kind: SymbolKind.Class, Value: TypeRef target } }
                || oracle.ClassOwner(operands[2]) is not { } owner || SymTable.Resolve(owner) is not ReadSym holder
                || oracle.Decompose(holder.Address) is not (ResultSym { Site: { Op.Kind: OpKind.BeginCatch } begin }, 0, null, _)) continue;
            if (!found.TryGetValue(begin, out var set)) found[begin] = set = [];
            set.Add(target);
        }
        return caughtTypes = found;
    }

    /// <summary>异常包装对象之上的托管异常对象的字段：包装里的指针是被捕获的对象，类型由 catch 的类型判断给出。</summary>
    private ManagedValue? CaughtFieldRead(ReadSym read, ulong address)
    {
        if (oracle.Decompose(read.Address) is not (ReadSym holder, var offset, null, _)
            || oracle.Decompose(holder.Address) is not (ResultSym { Site: { Op.Kind: OpKind.BeginCatch } begin }, 0, null, _)
            || !exceptions.TryGetValue(begin, out var exception) || !CaughtTypes().TryGetValue(begin, out var tested)) return null;
        // 被判断的类型中，只有一个在这个偏移处恰有一个字段时，读取按它解释。
        var matches = tested.OfType<DefinitionRef>()
            .Select(t => (Type: t, Field: oracle.FieldAt(t, (int)offset, read.Width, false, false)))
            .Where(m => m.Field is { Path.Count: 1 }).ToArray();
        if (matches.Length != 1) return null;
        var (caught, (path, fieldType)) = (matches[0].Type, matches[0].Field!.Value);
        return FieldAccess(new CheckedCastValue(exception, caught), path[0], fieldType, false, address);
    }

    private ManagedValue? Read(ReadSym read, ulong address)
    {
        if (oracle.Decompose(read.Address) is (ResultSym { Site: { Op.Kind: OpKind.BeginCatch } begin }, 0, null, _) && exceptions.TryGetValue(begin, out var exception))
            return exception;
        if (CaughtFieldRead(read, address) is { } caught) return caught;
        if ((oracle.Locate(read.Address, read.Width) ?? oracle.ViewLocate(read.Address, read.Width, read.Site)) is not { } location)
        {
            if (oracle.BooleanAddress(read.Address, read.Width) is var (condition, yes, no))
                return new ChoiceValue(View(condition, types.Boolean, address), LocationValue(yes, address),
                    LocationValue(no, address), yes.Type, Width(yes.Type));
            if (oracle.NarrowRead(read) is not { } part) return Hole(address, $"内存 读取位置 {read.Address}", null);
            var value = LocationValue(part.Leaf, address);
            if (part.FieldBit != 0)
                value = new BinaryValue(BinOp.LShr, value, new ConstantValue((ulong)part.FieldBit, types.Int32, 32), value.Type, Width(value.Type));
            return new ConversionValue(ConvKind.Truncate, value, types.Integer(read.Width, true), read.Width);
        }
        return LocationValue(location, address);
    }

    /// <summary>
    /// 位置的托管值。写入目标（store）中值类型的根与中间字段以地址给出：stfld 需要对象引用或托管地址。
    /// </summary>
    private ManagedValue LocationValue(Location location, ulong address, bool store = false)
    {
        var path = location.Path;
        switch (location.Kind)
        {
            case LocationKind.Argument:
                return View(location.Base!, location.Type, address);
            case LocationKind.Static or LocationKind.ThreadStatic:
                {
                    var type = FieldType(path[0], path.Count == 1 ? location.Type : null);
                    var root = StaticFieldAccess(path[0], type, store && path.Count == 1, address);
                    return path.Count == 1 ? root : Nest(store ? new AddressValue(root) : root, path.Skip(1).ToArray(), location.Type, store, address);
                }
            case LocationKind.Instance:
                {
                    var receiver = View(location.Base!, location.Through ?? oracle.TypeOf(location.Base!), address);
                    if (location.View is { } viewed) receiver = new CheckedCastValue(receiver, viewed);
                    return Nest(receiver, path, location.Type, store, address);
                }
            case LocationKind.Reference:
                {
                    var pointer = View(location.Base!, location.Through ?? oracle.TypeOf(location.Base!), address);
                    if (location.FixedBuffer)
                    {
                        var first = new AddressValue(Nest(pointer, path, location.Type, store: true, address));
                        pointer = new ConversionValue(ConvKind.Bitcast, first, new PointerRef(location.Type), 64);
                        return new IndirectValue(new PointerOffsetValue(pointer, View(location.ByteOffset!, types.Int64, address)),
                            location.Type, Width(location.Type));
                    }
                    if (location.ByteOffset is { } offset)
                        pointer = new PointerOffsetValue(pointer, View(offset, types.Int64, address));
                    if (location is { Strided: true, Index: { } stride })
                        pointer = new PointerOffsetValue(pointer, new BinaryValue(BinOp.Mul, View(stride, types.Int64, address),
                            new ConversionValue(ConvKind.ZeroExtend, new SizeOfValue(location.Element ?? location.Type, types.Int32), types.Int64, 64), types.Int64, 64));
                    // 无类型指针上元素的字段：先按元素类型解引用，再进入字段。
                    if (location is { Strided: true, Element: { } element } && path.Count > 0)
                    {
                        ManagedValue item = new IndirectValue(pointer, element, Width(element));
                        return Nest(store ? new AddressValue(item) : item, path, location.Type, store, address);
                    }
                    if (path.Count != 0) return Nest(pointer, path, location.Type, store, address);
                    if (pointer.Type is PointerRef { Element: var pointee } && !pointee.Equals(location.Type) && !ManagedTypes.IsVoid(location.Type))
                        pointer = new ConversionValue(ConvKind.Bitcast, pointer, new PointerRef(location.Type), 64);
                    return new IndirectValue(pointer, location.Type, Width(location.Type));
                }
            case LocationKind.Element:
                {
                    var array = View(location.Base!, location.Through ?? oracle.TypeOf(location.Base!), address);
                    var index = location.Index is { } i ? ElementIndex(i, address) : new ConstantValue((ulong)location.ConstantIndex, types.Int32, 32);
                    var element = array.Type is ArrayRef a ? a.Element : location.Type;
                    ManagedValue value = new ArrayElementValue(array, index, element, Width(element));
                    return path.Count == 0 ? value : Nest(store ? new AddressValue(value) : value, path, location.Type, store, address);
                }
            case LocationKind.MultiElement:
                {
                    var array = View(location.Base!, location.Through ?? oracle.TypeOf(location.Base!), address);
                    var indices = location.Indices!.Select(i => View(i, types.Int32, address)).ToArray();
                    var element = array.Type is ArrayRef a ? a.Element : location.Type;
                    ManagedValue value = new MultiArrayElementValue(array, indices, element, Width(element));
                    return path.Count == 0 ? value : Nest(store ? new AddressValue(value) : value, path, location.Type, store, address);
                }
            case LocationKind.SpanElement:
                return SpanElement(location, address, store);
            case LocationKind.Dimension or LocationKind.LowerBound:
                return new ArrayBoundValue(View(location.Base!, location.Through ?? oracle.TypeOf(location.Base!), address), (int)location.ConstantIndex,
                    location.Kind == LocationKind.LowerBound, types.Int32);
            case LocationKind.Length:
                return new ArrayLengthValue(View(location.Base!, location.Through ?? oracle.TypeOf(location.Base!), address), types.Int32, 32);
            case LocationKind.StringLength:
                return new StringLengthValue(View(location.Base!, types.String, address), types.Int32);
            case LocationKind.Local:
                {
                    var local = CellLocal((LocalSym)location.Base!);
                    return path.Count == 0 ? store && local is ArgumentValue ? new IndirectValue(new AddressValue(local), location.Type, Width(location.Type)) : local
                        : Nest(store ? new AddressValue(local) : local, path, location.Type, store, address);
                }
        }
        return Hole(address, $"内存 位置 {location.Kind} 尚未接入", location.Type);
    }

    /// <summary>用托管引用重建 Span 的裸起点与元素偏移，保留机器代码已有的边界检查。</summary>
    private ManagedValue SpanElement(Location location, ulong address, bool store)
    {
        var spanType = (DefinitionRef)location.Owner!;
        var element = spanType.Arguments[0];
        var referenceType = new ByRefRef(element);
        var getReference = types.CorlibDefinition("System.Runtime.InteropServices.MemoryMarshal")?.Methods
            .Where(m => m.Name == "GetReference" && m.IsStatic && (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                && m.GenericParameters.Count == 1 && m.Parameters.Count == 1)
            .Select(m => new MethodRef(m, [], [element]))
            .SingleOrDefault(m => m.Concrete(m.Method.Parameters[0].Type).Equals(spanType)
                && m.Concrete(m.Method.ReturnType).Equals(referenceType));
        var add = types.CorlibDefinition("System.Runtime.CompilerServices.Unsafe")?.Methods
            .Where(m => m.Name == "Add" && m.IsStatic && (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                && m.GenericParameters.Count == 1 && m.Parameters.Count == 2)
            .Select(m => new MethodRef(m, [], [element]))
            .SingleOrDefault(m => m.Concrete(m.Method.Parameters[0].Type).Equals(referenceType)
                && m.Concrete(m.Method.Parameters[1].Type).Equals(types.IntPtr) && m.Concrete(m.Method.ReturnType).Equals(referenceType));
        if (getReference is null || add is null || currentOutput is null || sharedEdge)
            return Hole(address, "内存 Span 起点或步进不可写出", location.Type);
        var span = View(location.Base!, spanType, address);
        var reference = NewLocal(referenceType);
        currentOutput.Add(new ManagedCall(address, new ManagedInvocation(getReference, null, [span], false), reference));
        if (location.Index is { } index && !SymTable.IsZero(index))
        {
            var offset = new ConversionValue(ConvKind.Bitcast, View(index, types.Int64, address), types.IntPtr, 64);
            var indexed = NewLocal(referenceType);
            currentOutput.Add(new ManagedCall(address, new ManagedInvocation(add, null, [reference, offset], false), indexed));
            reference = indexed;
        }
        ManagedValue value = new IndirectValue(reference, element, Width(element));
        return location.Path.Count == 0 ? value : Nest(store ? new AddressValue(value) : value, location.Path, location.Type, store, address);
    }

    /// <summary>
    /// 元素下标（ECMA-335 III.4.7）：下标是 int32 或 native int，int32 按符号扩展参与寻址，界外访问抛出 IndexOutOfRangeException。
    /// 按比例寻址时丢弃的高位、32 位下标的符号或零扩展都只影响界外访问，下标取其来源；仍为 64 位时按 int64 写出，由写出转为 native int。
    /// </summary>
    private ManagedValue ElementIndex(Sym index, ulong address)
    {
        index = TypeOracle.ArrayIndex(index);
        return index.Width == 32 ? View(index, types.Int32, address) : View(index, types.Int64, address);
    }

    private TypeRef FieldType(FieldRef field, TypeRef? leaf) => leaf ?? field.Field.Type.Substitute(((DefinitionRef)field.DeclaringType).Arguments, []);

    /// <summary>
    /// 字段链：写入目标中值类型的中间字段以地址给出；每一级按可访问性取字段或被内联的访问器。
    /// 写入经过的不可访问中间字段没有访问器可以代替（取不到它的地址）：记为待证，由内联的逆证明覆盖；和调用方同一程序集时按原生代码直接取址，见 <see cref="FieldValue.Unfolded"/>。
    /// </summary>
    private ManagedValue Nest(ManagedValue receiver, IReadOnlyList<FieldRef> path, TypeRef leaf, bool store, ulong address)
    {
        var value = receiver;
        for (var i = 0; i < path.Count; i++)
        {
            var last = i == path.Count - 1;
            var type = FieldType(path[i], last ? leaf : null);
            if (store && !last && !fields.Accessible(path[i], value.Type))
            {
                var reason = $"内存 写入经过不可访问的值类型字段 {path[i].Field.Name}";
                if (path[i].Field.Owner.Assembly != analysis.Method.Method.Owner.Assembly
                    || Inlining.InlineProof.SharedArgument(analysis.Method, path[i].DeclaringType))
                {
                    // 只能来自被内联的方法（如构建器写自己的字段）：待内联的逆证明覆盖，证明不成立时记为洞。
                    value = new FieldValue(value, path[i], type, Width(type)) { Pending = new(address, reason, true) };
                    if (types.Kind(type) == TypeKind.Struct) value = new AddressValue(value);
                    continue;
                }
                unfolded.Add(new ReSekai.Decompiler.Diagnostics.RuleTrace(Inlining.InlineProof.UnfoldedRule, address, ReSekai.Decompiler.Diagnostics.RewriteStatus.Applied, "same-assembly-path", reason, null));
                value = new FieldValue(value, path[i], type, Width(type)) { Unfolded = true };
                if (types.Kind(type) == TypeKind.Struct) value = new AddressValue(value);
                continue;
            }
            value = (store ? null : OverlappingPieces(value, path[i], type, address)) ?? FieldAccess(value, path[i], type, store && last, address);
            if (store && !last && types.Kind(type) == TypeKind.Struct) value = new AddressValue(value);
        }
        return value;
    }

    /// <summary>
    /// 显式布局中不可访问的整数字段，其位同时由可访问的整数字段连续覆盖（联合体式的结构）时，读取它等于按小端把这些字段拼起来。
    /// </summary>
    private ManagedValue? OverlappingPieces(ManagedValue receiver, FieldRef field, TypeRef type, ulong address)
    {
        if (fields.Accessible(field, receiver.Type) || types.Kind(type) != TypeKind.Int || !ExplicitLayout(field, out var owner)) return null;
        var slots = types.Fields(owner).ToArray();
        if (slots.FirstOrDefault(f => f.Path[0].Equals(field)) is not { } self) return null;
        var bit = self.Bit;
        ManagedValue? packed = null;
        foreach (var piece in slots.Where(f => !f.Path[0].Equals(field) && self.Contains(f.Bit, f.Width) && fields.Accessible(f.Path[0], receiver.Type)
            && types.Kind(f.Type) is TypeKind.Int or TypeKind.Bool).OrderBy(f => f.Bit))
        {
            if (piece.Bit != bit) return null;
            var part = Resize(FieldAccess(receiver, piece.Path[0], piece.Type, false, address), type);
            if (bit > self.Bit) part = new BinaryValue(BinOp.Shl, part, new ConstantValue((ulong)(bit - self.Bit), types.Int32, 32), type, Width(type));
            packed = packed is null ? part : new BinaryValue(BinOp.Or, packed, part, type, Width(type));
            bit += piece.Width;
        }
        return bit == self.Bit + self.Width ? packed : null;
    }

    private static bool ExplicitLayout(FieldRef field, out DefinitionRef owner)
    {
        owner = (DefinitionRef)field.DeclaringType;
        return (owner.Definition.Attributes & System.Reflection.TypeAttributes.LayoutMask) == System.Reflection.TypeAttributes.ExplicitLayout;
    }
}
