using System.Security.Cryptography;
using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Image;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private const string JoystickPackSource =
        "https://github.com/FeneraxStudios/Unity-Joystick-Pack/blob/master/Joystick%20Pack%20v2.1.unitypackage";

    private bool ReferenceBodyMatches(ulong entry, int size, string sha256, string? exceptionsSha256 = null)
    {
        if (facts.Code.At(entry) is not { } code || code.Start != entry || code.Size != (ulong)size
            || facts.Model.Image.Resolve(entry) is not { Kind: AddressKind.FileBacked, FileOffset: { } offset } region
            || region.AvailableBytes < (ulong)size
            || System.Convert.ToHexString(SHA256.HashData(facts.Model.Image.Bytes.Span.Slice((int)offset, size))) != sha256) return false;
        if (exceptionsSha256 is null) return code.ExceptionRegions.Length == 0;
        var handlers = code.ExceptionRegions.Select(row => FormattableString.Invariant(
            $"{row.Start - entry:x}/{row.End - entry:x}/{(row.LandingPad == 0 ? 0 : row.LandingPad - entry):x}/{row.ActionOffset:x}/{string.Join(',', row.Actions.Select(a => FormattableString.Invariant($"{a.Filter}:{a.NextOffset}:{a.TypeInfo:x}:{a.Kind}")))}"));
        return System.Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', handlers)))) == exceptionsSha256;
    }

    /// <summary>栈副本排序后逆序比较相邻项，保留原输入、空数组和 null 的行为。</summary>
    private MethodRecoveryResult? RestoreHaveDuplicates(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "HaveDuplicates", IsStatic: true, Parameters: [var parameter],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "CoreUnsafeUtils" } owner }
            || parameter.Type is not ArrayRef { IsVector: true, Element: var element } array || !element.Equals(types.Int32)
            || !method.Method.ReturnType.Equals(types.Boolean)
            || !ReferenceBodyMatches(entry, 360, "2C72CE7DB1717BCE890E1FDDDF4DD72456C02A2EB6632FB9EF6C61CE738D5066")) return null;
        MethodRef? CallAt(ulong offset, string name) => facts.Code.Word(entry + offset) is { } word && A64.IsBl(word)
            ? facts.EntriesAt(A64.Imm26Target(word, entry + offset)).Where(e => e.Kind == EntryKind.GenericMethod)
                .Select(e => e.Method).OfType<MethodRef>().SingleOrDefault(m => m.Method.Name == name && m.Method.Owner == owner
                    && m.MethodArguments is [var item] && item.Equals(types.Int32))
            : null;
        var copy = CallAt(0xcc, "CopyTo");
        var sort = CallAt(0xdc, "QuickSort");
        var pointer = new PointerRef(types.Int32);
        if (copy?.Method.Parameters is not [var source, var destination, var limit] || sort?.Method.Parameters is not [var size, var items]
            || !copy.Concrete(source.Type).Equals(array) || copy.Concrete(destination.Type) is not PointerRef { Element: var copyItem }
            || sort.Concrete(items.Type) is not PointerRef { Element: var sortItem } || !ManagedTypes.IsVoid(copyItem) || !ManagedTypes.IsVoid(sortItem)
            || !copy.Concrete(limit.Type).Equals(types.Int32) || !sort.Concrete(size.Type).Equals(types.Int32)) return null;
        var input = new ArgumentValue(0, array, 64);
        var length = new LocalValue(new Variable(Storage.Temp(0), 1), types.Int32, 32);
        var stack = new LocalValue(new Variable(Storage.Temp(1), 1), pointer, 64);
        var i = new LocalValue(new Variable(Storage.Temp(2), 1), types.Int32, 32);
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var four = new ConstantValue(4, types.Int64, 64);
        ManagedValue Previous(ManagedValue index) => new BinaryValue(BinOp.Sub, index, one, types.Int32, 32);
        ManagedValue Item(ManagedValue index) => new IndirectValue(new PointerOffsetValue(stack,
            new BinaryValue(BinOp.Mul, new ConversionValue(ConvKind.SignExtend, index, types.Int64, 64), four, types.Int64, 64)), types.Int32, 32);
        var test = entry + 0xf8;
        var pair = entry + 0x100;
        var found = entry + 0x130;
        var absent = entry + 0x13c;
        var body = new ManagedBody(method, [new(entry, [
                new ManagedAssignment(entry + 0x74, length, new ArrayLengthValue(input, types.Int32, 32)),
                new ManagedAssignment(entry + 0x94, stack, new ZeroedStackBufferValue(length, pointer, 4)),
                new ManagedCall(entry + 0xcc, new(copy, null, [input, stack, length], false), null),
                new ManagedCall(entry + 0xdc, new(sort, null, [length, stack], false), null),
                new ManagedJump(entry + 0xe0, new(test, [new(i, Previous(length))]))]),
            new(test, [new ManagedBranch(test, new ComparisonValue(Cond.Sgt, i, zero, types.Boolean), new(pair, []), new(absent, []))]),
            new(pair, [new ManagedBranch(pair, new ComparisonValue(Cond.Eq, Item(i), Item(Previous(i)), types.Boolean),
                new(found, []), new(test, [new(i, Previous(i))]))]),
            new(found, [new ManagedReturn(found, new ConstantValue(1, types.Boolean, 8))]),
            new(absent, [new ManagedReturn(absent, new ConstantValue(0, types.Boolean, 8))])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "CoreUnsafeUtils.cs：栈副本排序并比较相邻值", null)],
        };
    }

    /// <summary>Clear 与第一次 Add 在原生合并了版本更新；之后按纹理的宽、高推进三维砖块坐标。</summary>
    private MethodRecoveryResult? RestoreProbeSourceLocations(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "GetSourceLocations", IsStatic: false, Parameters: [var count, var chunkSize, var data],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "ProbeReferenceVolume" } owner }
            || !count.Type.Equals(types.Int32) || !chunkSize.Type.Equals(types.Int32)
            || data.Type is not DefinitionRef { Definition: { Name: "DataLocation", IsValueType: true, DeclaringType.Name: "ProbeBrickPool" } } texture
            || method.Method.ReturnType is not DefinitionRef { Definition.FullName: "System.Collections.Generic.List",
                Arguments: [DefinitionRef { Definition: { Name: "BrickChunkAlloc", IsValueType: true, DeclaringType.Name: "ProbeBrickPool" } } chunk] } list
            || chunk.Definition.DeclaringType != texture.Definition.DeclaringType || types.Size(chunk) != 12
            || !ReferenceBodyMatches(entry, 388, "3FFD1FA2868F746ACEF6BCA786FB12386FA4ABC44F5339C911C0989EE573E902")) return null;
        FieldDef? Field(DefinitionRef holder, string name, int offset, TypeRef type) =>
            facts.Layouts.Of(holder)?.Fields.SingleOrDefault(f => f.Field.Name == name && f.Offset == offset && f.Type.Equals(type))?.Field;
        var listField = Field(new(owner, []), "m_TmpSrcChunks", 0x58, list);
        var xField = Field(chunk, "x", 0x10, types.Int32);
        var yField = Field(chunk, "y", 0x14, types.Int32);
        var zField = Field(chunk, "z", 0x18, types.Int32);
        var width = Field(texture, "width", 0x50, types.Int32);
        var height = Field(texture, "height", 0x54, types.Int32);
        var add = list.Definition.Methods.SingleOrDefault(m => m is { Name: "Add", IsStatic: false, Parameters.Count: 1 });
        var clear = list.Definition.Methods.SingleOrDefault(m => m is { Name: "Clear", IsStatic: false, Parameters.Count: 0 });
        if (listField is null || xField is null || yField is null || zField is null || width is null || height is null || add is null || clear is null) return null;
        var addRef = new MethodRef(add, list.Arguments, []);
        var clearRef = new MethodRef(clear, list.Arguments, []);
        if (!addRef.Concrete(add.Parameters[0].Type).Equals(chunk) || !ManagedTypes.IsVoid(add.ReturnType) || !ManagedTypes.IsVoid(clear.ReturnType)) return null;
        foreach (var offset in new ulong[] { 0xa8, 0x158 })
        {
            var call = entry + offset;
            if (facts.Code.Word(call) is not { } word || !A64.IsBl(word)
                || !facts.EntriesAt(A64.Imm26Target(word, call)).Any(e => e.Method is { Method.Name: "AddWithResize" } target
                    && target.Method.Owner == list.Definition && target.TypeArguments.SequenceEqual(list.Arguments))) return null;
        }

        var self = new ArgumentValue(-1, method.DeclaringType, 64);
        var dataValue = new ArgumentValue(2, texture, 0);
        var chunks = new FieldValue(self, new(listField, method.DeclaringType), list, 64);
        var position = new LocalValue(new Variable(Storage.Temp(0), 1), chunk, 0);
        var j = new LocalValue(new Variable(Storage.Temp(1), 1), types.Int32, 32);
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var four = new ConstantValue(4, types.Int32, 32);
        ManagedValue Add(ManagedValue a, ManagedValue b) => new BinaryValue(BinOp.Add, a, b, types.Int32, 32);
        ManagedValue Less(ManagedValue a, ManagedValue b) => new ComparisonValue(Cond.Slt, a, b, types.Boolean);
        FieldValue Read(FieldDef field) => new(position, new(field, chunk), types.Int32, 32);
        FieldValue Write(FieldDef field) => new(new AddressValue(position), new(field, chunk), types.Int32, 32);
        FieldValue Dimension(FieldDef field) => new(dataValue, new(field, texture), types.Int32, 32);
        var test = entry + 0xac;
        var stepX = entry + 0xcc;
        var stepY = entry + 0xdc;
        var stepZ = entry + 0xe8;
        var append = entry + 0xf8;
        var done = entry + 0x164;
        var body = new ManagedBody(method, [new(entry, [
                new ManagedAssignment(entry, position, new ComposedValue(chunk, [])),
                new ManagedCall(entry + 0x50, new(clearRef, chunks, [], true), null),
                new ManagedCall(entry + 0x50, new(addRef, chunks, [position], true), null),
                new ManagedJump(entry + 0x50, new(test, [new(j, one)]))]),
            new(test, [new ManagedBranch(test, Less(j, new ArgumentValue(0, types.Int32, 32)), new(stepX, []), new(done, []))]),
            new(stepX, [new ManagedStore(stepX, Write(xField), Add(Read(xField), new BinaryValue(BinOp.Mul,
                    new ArgumentValue(1, types.Int32, 32), four, types.Int32, 32))),
                new ManagedBranch(stepX, Less(Read(xField), Dimension(width)), new(append, []), new(stepY, []))]),
            new(stepY, [new ManagedStore(stepY, Write(xField), zero), new ManagedStore(stepY, Write(yField), Add(Read(yField), four)),
                new ManagedBranch(stepY, Less(Read(yField), Dimension(height)), new(append, []), new(stepZ, []))]),
            new(stepZ, [new ManagedStore(stepZ, Write(yField), zero), new ManagedStore(stepZ, Write(zField), Add(Read(zField), four)),
                new ManagedJump(stepZ, new(append, []))]),
            new(append, [new ManagedCall(append, new(addRef, chunks, [position], true), null),
                new ManagedJump(append, new(test, [new(j, Add(j, one))]))]),
            new(done, [new ManagedReturn(done, chunks)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "ProbeReferenceVolume.cs：清空并填充三维砖块坐标列表", null)],
        };
    }

    /// <summary>已知实例内联了元素读取；按开放签名恢复元素和 getter，保留 T 的实际类型。</summary>
    private MethodRecoveryResult? RestoreCombineHashes(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "CombineHashes", IsStatic: true, Parameters: [var count, var hashes, var output],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "CoreUnsafeUtils" } }
            || method.MethodArguments is not [GenericParameterRef element, GenericParameterRef getter]
            || method.Concrete(output.Type) is not PointerRef { Element: DefinitionRef { Definition.FullName: "UnityEngine.Hash128" } hash } destination
            || !method.Concrete(count.Type).Equals(types.Int32) || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || method.Concrete(hashes.Type) is not PointerRef { Element: var pointee } buffer || !ManagedTypes.IsVoid(pointee)
            || !ReferenceBodyMatches(entry, 160, "4D51A7FE4FF37F68B4C0F7F9C6E837F887B170F27B85CEDF6F0AB837F271652E")) return null;
        var key = getter.Parameter?.Constraints.OfType<DefinitionRef>().SingleOrDefault(c =>
            c.Definition.DeclaringType == method.Method.Owner && c.Definition.SimpleName == "IKeyGetter");
        if (key is null || method.Concrete(key) is not DefinitionRef { Arguments: [var item, var keyType] } iface
            || !item.Equals(element) || !keyType.Equals(hash)) return null;
        var get = iface.Definition.Methods.SingleOrDefault(m => m is { Name: "Get", IsStatic: false, IsVirtual: true, Parameters.Count: 1 });
        var read = facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll" && t.FullName == "Unity.Collections.LowLevel.Unsafe.UnsafeUtility")
            ?.Methods.SingleOrDefault(m => m is { Name: "ReadArrayElement", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 2 });
        var append = facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll" && t.FullName == "UnityEngine.HashUtilities")
            ?.Methods.SingleOrDefault(m => m is { Name: "AppendHash", IsStatic: true, Parameters.Count: 2 }
                && m.Parameters.All(p => p.Type is ByRefRef reference && reference.Element.Equals(hash)));
        var create = types.CorlibDefinition("System.Activator")?.Methods.SingleOrDefault(m =>
            m is { Name: "CreateInstance", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 0 });
        if (get is null || read is null || append is null || create is null
            || facts.Code.Word(entry + 0x80) is not { } appendWord || !A64.IsBl(appendWord)
            || !facts.EntriesAt(A64.Imm26Target(appendWord, entry + 0x80)).Any(e => e.Method?.Method == append)) return null;
        var getRef = new MethodRef(get, iface.Arguments, []);
        var readRef = new MethodRef(read, [], [element]);
        var createRef = new MethodRef(create, [], [getter]);
        if (getRef.Concrete(get.Parameters[0].Type) is not ByRefRef valueRef || !valueRef.Element.Equals(element)
            || !getRef.Concrete(get.ReturnType).Equals(hash) || !readRef.Concrete(read.ReturnType).Equals(element)
            || !readRef.Concrete(read.Parameters[0].Type).Equals(buffer) || !readRef.Concrete(read.Parameters[1].Type).Equals(types.Int32)
            || !createRef.Concrete(create.ReturnType).Equals(getter) || !ManagedTypes.IsVoid(append.ReturnType)) return null;

        var index = new LocalValue(new Variable(Storage.Temp(0), 1), types.Int32, 32);
        var converter = new LocalValue(new Variable(Storage.Temp(1), 1), getter, types.ValueWidth(getter));
        var input = new LocalValue(new Variable(Storage.Temp(2), 1), element, types.ValueWidth(element));
        var value = new LocalValue(new Variable(Storage.Temp(3), 1), hash, 0);
        var destinationValue = new IndirectValue(new ArgumentValue(2, destination, 64), hash);
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var test = entry + 0x4c;
        var step = entry + 0x54;
        var done = entry + 0x8c;
        var body = new ManagedBody(method, [new(entry, [new ManagedCall(entry + 0x40, new(createRef, null, [], false), converter),
                new ManagedJump(entry + 0x48, new(test, [new(index, zero)]))]),
            new(test, [new ManagedBranch(test, new ComparisonValue(Cond.Slt, index, new ArgumentValue(0, types.Int32, 32), types.Boolean),
                new(step, []), new(done, []))]),
            new(step, [new ManagedCall(entry + 0x58, new(readRef, null, [new ArgumentValue(1, buffer, 64), index], false), input),
                new ManagedCall(entry + 0x6c, new ManagedInvocation(getRef, new AddressValue(converter), [new AddressValue(input)], true) { Constraint = getter }, value),
                new ManagedCall(entry + 0x80, new(new(append, [], []), null, [new AddressValue(value), new AddressValue(destinationValue)], false), null),
                new ManagedJump(entry + 0x88, new(test, [new(index, new BinaryValue(BinOp.Add, index, one, types.Int32, 32))]))]),
            new(done, [new ManagedReturn(entry + 0x9c, null)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "CoreUnsafeUtils.cs：以 TGetter 逐元素取哈希，再累计到 outHash", null)],
        };
    }

    /// <summary>CopyTo 的 int 实例用 4 字节步进写出整个 T；开放签名通过 UnsafeUtility 保留实际元素大小。</summary>
    private MethodRecoveryResult? RestoreArrayCopyTo(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "CopyTo", IsStatic: true, Parameters: [var list, var dest, var count],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "CoreUnsafeUtils" } }
            || method.MethodArguments is not [GenericParameterRef { NativeScalar: { } witness } element] || !witness.Equals(types.Int32)
            || method.Concrete(list.Type) is not ArrayRef { IsVector: true, Element: var item } array || !item.Equals(element)
            || method.Concrete(dest.Type) is not PointerRef { Element: var pointee } pointer || !ManagedTypes.IsVoid(pointee)
            || !method.Concrete(count.Type).Equals(types.Int32) || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !ReferenceBodyMatches(entry, 116, "E8F712C041F87DB844CDED9985F2A1596E43345F4CED152F75E791D8E05F5D5C")) return null;
        var utility = facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll"
            && t.FullName == "Unity.Collections.LowLevel.Unsafe.UnsafeUtility");
        var write = utility?.Methods.SingleOrDefault(m => m is { Name: "WriteArrayElement", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 3 });
        if (write is null) return null;
        var writeRef = new MethodRef(write, [], [element]);
        if (!writeRef.Concrete(write.Parameters[0].Type).Equals(pointer) || !writeRef.Concrete(write.Parameters[1].Type).Equals(types.Int32)
            || !writeRef.Concrete(write.Parameters[2].Type).Equals(element) || !ManagedTypes.IsVoid(write.ReturnType)) return null;

        var source = new ArgumentValue(0, array, 64);
        var destination = new ArgumentValue(1, pointer, 64);
        var requested = new ArgumentValue(2, types.Int32, 32);
        var length = new LocalValue(new Variable(Storage.Temp(0), 1), types.Int32, 32);
        var limit = new LocalValue(new Variable(Storage.Temp(1), 1), types.Int32, 32);
        var index = new LocalValue(new Variable(Storage.Temp(2), 1), types.Int32, 32);
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var min = new ChoiceValue(new ComparisonValue(Cond.Sgt, length, requested, types.Boolean), requested, length, types.Int32, 32);
        var test = entry + 0x34;
        var copy = entry + 0x4c;
        var done = entry + 0x60;
        var body = new ManagedBody(method, [new(entry, [new ManagedAssignment(entry + 0x28, length, new ArrayLengthValue(source, types.Int32, 32)),
                new ManagedAssignment(entry + 0x30, limit, min), new ManagedJump(entry + 0x30, new(test, [new(index, zero)]))]),
            new(test, [new ManagedBranch(entry + 0x38, new ComparisonValue(Cond.Slt, index, limit, types.Boolean), new(copy, []), new(done, []))]),
            new(copy, [new ManagedCall(entry + 0x58, new(writeRef, null, [destination, index, new ArrayElementValue(source, index, element, 32)], false), null),
                new ManagedJump(entry + 0x5c, new(test, [new(index, new BinaryValue(BinOp.Add, index, one, types.Int32, 32))]))]),
            new(done, [new ManagedReturn(entry + 0x68, null)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "CoreUnsafeUtils.cs：按元素实际大小复制 min(count, Length) 个数组元素", null)],
        };
    }

    /// <summary>List.Add 的快路在调用方内联，保留边界检查、版本更新、容量扩展和元组参数。</summary>
    private MethodRecoveryResult? RestoreRenderGraphRegisterFrameAllocation(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "RegisterFrameAllocation", IsStatic: false, Parameters: [var hash, var value],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Experimental.Rendering.RenderGraphModule",
                    Name: "RenderGraphResourcePool`1" } owner }
            || method.TypeArguments is not [GenericParameterRef { IsReferenceConstrained: true } resourceType]
            || !method.Concrete(hash.Type).Equals(types.Int32) || !method.Concrete(value.Type).Equals(resourceType)
            || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !ReferenceBodyMatches(entry, 180, "38D907F4CFB86CBCE80EE6135734C837FEBF5DE204F130582AD1CF989ED1A4C4")) return null;

        var field = owner.Fields.SingleOrDefault(f => f.Name == "m_FrameAllocatedResources" && !f.IsStatic);
        if (field is null || method.DeclaringType is not DefinitionRef declaring
            || facts.Layouts.Of(declaring)?.Field(field)?.Offset != 0x20
            || method.Concrete(field.Type) is not DefinitionRef { Definition.FullName: "System.Collections.Generic.List",
                Arguments: [DefinitionRef { Definition.FullName: "System.ValueTuple", Arguments: [var key, var resource] } tuple] } list
            || !key.Equals(types.Int32) || !resource.Equals(resourceType)) return null;
        var constructor = tuple.Definition.Methods.SingleOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 2);
        var add = list.Definition.Methods.SingleOrDefault(m => m.Name == "Add" && !m.IsStatic && m.Parameters.Count == 1);
        if (constructor is null || add is null) return null;
        var tupleCtor = new MethodRef(constructor, tuple.Arguments, []);
        var listAdd = new MethodRef(add, list.Arguments, []);
        if (!tupleCtor.Concrete(constructor.Parameters[0].Type).Equals(types.Int32)
            || !tupleCtor.Concrete(constructor.Parameters[1].Type).Equals(resourceType)
            || !listAdd.Concrete(add.Parameters[0].Type).Equals(tuple) || !ManagedTypes.IsVoid(add.ReturnType)) return null;
        bool Calls(ulong address, Func<MethodRef, bool> predicate) => facts.Code.Word(address) is { } word && A64.IsBl(word)
            && facts.EntriesAt(A64.Imm26Target(word, address)).Any(e => e.Kind == EntryKind.GenericMethod && e.Method is { } target && predicate(target));
        bool TupleShape(TypeRef type) => type is DefinitionRef d && d.Definition == tuple.Definition
            && d.Arguments is [var first, var second] && first.Equals(types.Int32) && ManagedTypes.IsReferenceType(second);
        if (!Calls(entry + 0x30, m => m.Method == constructor && TupleShape(m.DeclaringType))
            || !Calls(entry + 0x9c, m => m.Method.Name == "AddWithResize" && m.Method.Owner == list.Definition
                && m.TypeArguments is [var element] && TupleShape(element))) return null;

        var self = new ArgumentValue(-1, method.DeclaringType, 64);
        var index = new ArgumentValue(0, types.Int32, 32);
        var payload = new ArgumentValue(1, resourceType, 64);
        var pair = new LocalValue(new Variable(Storage.Temp(0), 1), tuple, 0);
        var storage = new FieldValue(self, new(field, method.DeclaringType), list, 64);
        var skip = new ComparisonValue(Cond.Eq, index, new ConstantValue(uint.MaxValue, types.Int32, 32), types.Boolean);
        var addBlock = entry + 0x14;
        var done = entry + 0xa0;
        var body = new ManagedBody(method, [new(entry, [new ManagedBranch(entry + 0x10, skip, new(done, []), new(addBlock, []))]),
            new(addBlock, [new ManagedAssignment(entry + 0x18, pair, new ComposedValue(tuple, [])),
                new ManagedCall(entry + 0x30, new(tupleCtor, new AddressValue(pair), [index, payload], false), null),
                new ManagedCall(entry + 0x3c, new(listAdd, storage, [pair], true), null), new ManagedJump(entry + 0x88, new(done, []))]),
            new(done, [new ManagedReturn(entry + 0xac, null)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "RenderGraphResourcePool.cs：有效 hash 的资源加入帧分配列表", null)],
        };
    }

    /// <summary>两个 as 结果依次虚调用 value，最后按同一 T 分派 Interp。任一接收者不兼容即触发判空。</summary>
    private MethodRecoveryResult? RestoreVolumeParameterInterp(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "Interp", IsStatic: false, IsVirtual: true, Parameters: [var from, var to, var t],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "VolumeParameter`1",
                    BaseType: DefinitionRef { Definition.Name: "VolumeParameter" } baseType } owner }
            || method.TypeArguments is not [GenericParameterRef { NativeScalar: null, NativeAggregate: null } type]
            || !method.Concrete(from.Type).Equals(baseType) || !method.Concrete(to.Type).Equals(baseType)
            || !method.Concrete(t.Type).Equals(types.Single) || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !facts.EntriesAt(entry).Any(e => e.Kind == EntryKind.GenericMethod && e.Method?.Method == method.Method)
            || !ReferenceBodyMatches(entry, 400, "32092340B770C73444E0D083666949E51CD0990ABD5BDA7A6CD9BD6EC2FCEC5F")) return null;

        var getter = owner.Properties.SingleOrDefault(p => p.Name == "value")?.Getter;
        var typed = owner.Methods.SingleOrDefault(m => m.Name == "Interp" && m != method.Method && m.IsVirtual && !m.IsStatic
            && m.Parameters is [var left, var right, var blend] && method.Concrete(left.Type).Equals(type)
            && method.Concrete(right.Type).Equals(type) && method.Concrete(blend.Type).Equals(types.Single) && ManagedTypes.IsVoid(m.ReturnType));
        if (getter is not { IsVirtual: true, IsStatic: false, Parameters.Count: 0 }
            || !method.Concrete(getter.ReturnType).Equals(type) || RuntimeLayout.VtableMethodPointer(getter.Slot) != 0x218
            || typed is null || RuntimeLayout.VtableMethodPointer(typed.Slot) != 0x238) return null;

        var parameterType = method.DeclaringType;
        var first = new LocalValue(new Variable(Storage.Temp(0), 1), type, 64);
        var second = new LocalValue(new Variable(Storage.Temp(1), 1), type, 64);
        var get = new MethodRef(getter, method.TypeArguments, []);
        ManagedInvocation Read(int index) => new(get, new IsInstValue(new ArgumentValue(index, baseType, 64), parameterType), [], true);
        var interpolate = new ManagedInvocation(new MethodRef(typed, method.TypeArguments, []), new ArgumentValue(-1, parameterType, 64),
            [first, second, new ArgumentValue(2, types.Single, 32)], true);
        var body = new ManagedBody(method, [new(entry, [new ManagedCall(entry + 0xc4, Read(0), first),
            new ManagedCall(entry + 0x158, Read(1), second), new ManagedCall(entry + 0x188, interpolate, null), new ManagedReturn(entry + 0x188, null)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "VolumeParameter.cs：按 T 转型后读取两个虚属性，再调用虚插值", null)],
        };
    }

    /// <summary>Joystick Pack 2.1 的 getter 递归读取自身。Android Clang 12.0.8 优化后只剩 ret。</summary>
    private MethodRecoveryResult? RestoreReferenceSource(MethodRecoveryResult result)
    {
        var method = result.Method;
        if (method.Method is not { Name: "get_AxisOptions", IsStatic: false, Parameters.Count: 0,
                Owner: { Assembly: "Assembly-CSharp.dll", Namespace: "", Name: "Joystick", BaseType: DefinitionRef
                    { Definition: { Namespace: "UnityEngine", Name: "MonoBehaviour" } } } owner,
                ReturnType: DefinitionRef { Definition: { Namespace: "", Name: "AxisOptions", IsEnum: true } } returns }
            || result.Entry != method.Method.Address || facts.Code.At(result.Entry) is not { Size: 4 } code || code.Start != result.Entry
            || facts.Code.Word(result.Entry) != 0xd65f03c0
            || owner.Properties.SingleOrDefault(p => p.Name == "AxisOptions" && p.Getter == method.Method)?.Setter is not { } setter
            || Template(MethodRef.Open(setter))?.Body is not { } setterBody
            || setterBody.Blocks.SelectMany(b => b.Statements).ToArray() is not
                [ManagedStore { Target: FieldValue { Field.Field: { Name: "axisOptions", Owner: var fieldOwner, Type: var fieldType },
                    Receiver: ArgumentValue { Index: -1 } }, Value: ArgumentValue { Index: 0 } }, ManagedReturn { Value: null }]
            || fieldOwner != owner || !fieldType.Equals(returns)) return null;

        var self = new ArgumentValue(-1, method.DeclaringType, 64);
        var value = new LocalValue(new Variable(Storage.Temp(0), 1), returns, 32);
        var body = new ManagedBody(method, [new(result.Entry, [new ManagedCall(result.Entry, new(method, self, [], false), value), new ManagedReturn(result.Entry, value)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = JoystickPackSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", result.Entry, RewriteStatus.Applied, "joystick-pack-2.1", "原始 getter 递归读取自身", null)],
        };
    }
}
