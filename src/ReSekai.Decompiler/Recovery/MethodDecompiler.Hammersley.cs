using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private const string SrpCoreSource = "https://packages.unity.com/com.unity.render-pipelines.core/-/com.unity.render-pipelines.core-14.0.12.tgz";

    /// <summary>SRP Core 14.0.12 的四组序列。原生依次清零各缓冲、按数组实际长度复制 float，再逐个上传。</summary>
    private MethodRecoveryResult? RestoreHammersley(MethodRef method, ulong entry)
    {
        if (method.Method is not { Name: "Initialize", IsStatic: true, Parameters.Count: 0,
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "Hammersley" } owner }
            || !ManagedTypes.IsVoid(method.Method.ReturnType) || entry != method.Method.Address
            || !ReferenceBodyMatches(entry, 848, "6E89A08B4B91333FA459C74091F183E9289764B842A986A69A46FA2130F3121B")) return null;

        // 方法指令绑定了四个栈对象和每个数组的 static_fields 偏移；再按调用点核对 metadata 里的实例。
        (int Count, ulong Call)[] groups = [(16, 0x2e0), (32, 0x2f0), (64, 0x300), (256, 0x30c)];
        var arrays = new List<StaticFieldValue>();
        var buffers = new List<(LocalValue Local, FieldDef Fixed, FieldDef First, MethodRef Update)>();
        foreach (var (count, callOffset) in groups)
        {
            var field = owner.Fields.SingleOrDefault(f => f.Name == $"k_Hammersley2dSeq{count}" && f.IsStatic);
            var name = $"Hammersley2dSeq{count}";
            var definition = owner.NestedTypes.SingleOrDefault(t => t.Name == name && t.IsValueType);
            if (field?.Type is not ArrayRef array || array.Rank != 1 || !array.Element.Equals(types.Single)
                || field.Offset != buffers.Count * 8 || definition is null || definition.Size != count * 16
                || definition.Fields is not [{ Offset: 0, IsStatic: false, Type: DefinitionRef fixedType } fixedField]
                || !fixedType.Definition.IsValueType || fixedType.Definition.Size != definition.Size
                || fixedType.Definition.Fields is not [{ Offset: 0, IsStatic: false } first] || !first.Type.Equals(types.Single)) return null;
            var type = new DefinitionRef(definition, []);
            var callAddress = entry + callOffset;
            if (facts.Code.Word(callAddress) is not { } call || !A64.IsBl(call)) return null;
            var callee = A64.Imm26Target(call, callAddress);
            var matches = facts.EntriesAt(callee).Where(e => e.Kind == EntryKind.GenericMethod).Select(e => e.Method).OfType<MethodRef>()
                .Where(m => m.Method is { Name: "UpdateData", IsStatic: true, Owner: { Name: "ConstantBuffer" } target, Parameters: [var parameter] }
                    && target.Namespace == owner.Namespace && target.Assembly == owner.Assembly && ManagedTypes.IsVoid(m.Method.ReturnType)
                    && m.MethodArguments is [var argument] && argument.Equals(type) && m.Concrete(parameter.Type) is ByRefRef reference
                    && reference.Element.Equals(type)).Distinct().ToArray();
            if (matches is not [var update]) return null;
            arrays.Add(new StaticFieldValue(new FieldRef(field, method.DeclaringType), array, 64));
            buffers.Add((new LocalValue(new Variable(Storage.Temp(buffers.Count), 1), type, 0), fixedField, first, update));
        }

        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var index = new LocalValue(new Variable(Storage.Temp(buffers.Count), 1), types.Int32, 32);
        var blocks = new List<ManagedBlock>();
        var firstLoop = entry + 4;
        var initial = buffers.Select(b => (ManagedStatement)new ManagedAssignment(entry, b.Local, new ComposedValue(b.Local.Type, []))).ToList();
        initial.Add(new ManagedJump(entry, new(firstLoop, [new(index, zero)])));
        blocks.Add(new(entry, initial));
        for (var i = 0; i < buffers.Count; i++)
        {
            var (local, fixedField, first, _) = buffers[i];
            var test = firstLoop + (ulong)i * 8;
            var body = test + 4;
            var next = test + 8;
            var array = arrays[i];
            var length = new ArrayLengthValue(array, types.Int32, 32);
            blocks.Add(new(test, [new ManagedBranch(test, new ComparisonValue(Cond.Slt, index, length, types.Boolean), new(body, []), new(next, [new(index, zero)]))]));
            var storage = new FieldValue(local, new(fixedField, local.Type), fixedField.Type, local.Width);
            var start = new FieldValue(storage, new(first, fixedField.Type), types.Single, 32);
            var pointer = new ConversionValue(ConvKind.Bitcast, new AddressValue(start), new PointerRef(types.Single), 64);
            var offset = new BinaryValue(BinOp.Mul, new ConversionValue(ConvKind.SignExtend, index, types.Int64, 64),
                new ConstantValue(4, types.Int64, 64), types.Int64, 64);
            var destination = new IndirectValue(new PointerOffsetValue(pointer, offset), types.Single, 32);
            var load = new ArrayElementValue(array, index, types.Single, 32);
            blocks.Add(new(body, [new ManagedStore(body, destination, load),
                new ManagedJump(body, new(test, [new(index, new BinaryValue(BinOp.Add, index, one, types.Int32, 32))]))]));
        }
        var upload = firstLoop + (ulong)buffers.Count * 8;
        blocks.Add(new(upload, [.. buffers.Select(b => (ManagedStatement)new ManagedCall(upload, new(b.Update, null, [new AddressValue(b.Local)], false), null)), new ManagedReturn(upload, null)]));
        return new(method, entry, new ManagedBody(method, blocks), [])
        {
            ReferenceSource = SrpCoreSource,
            Rules = [new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12", "Hammersley.cs：四组定长缓冲清零、按数组长度复制、上传", null)],
        };
    }
}
