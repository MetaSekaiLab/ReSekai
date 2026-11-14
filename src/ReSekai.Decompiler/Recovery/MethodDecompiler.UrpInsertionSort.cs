using System.Security.Cryptography;
using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Image;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private const string UrpSource = "https://packages.unity.com/com.unity.render-pipelines.universal/-/com.unity.render-pipelines.universal-14.0.12.tgz";

    /// <summary>完全共享入口的内层循环比较 T，向右挪动较大项，最后把暂存值放入空出的槽。</summary>
    private MethodRecoveryResult? RestoreUrpInsertionSort(MethodRef method, ulong entry)
    {
        if (method.Method is not { Name: "InsertionSort", IsStatic: true, GenericParameters.Count: 2,
                Owner: { Assembly: "Unity.RenderPipelines.Universal.Runtime.dll", Namespace: "UnityEngine.Rendering.Universal.UTess", Name: "ModuleHandle" },
                Parameters: [var array, var low, var high, var comparer] }
            || method.MethodArguments is not [GenericParameterRef { NativeShared: true } element, GenericParameterRef { NativeShared: true } comparerType]
            || !ManagedTypes.IsVoid(method.Method.ReturnType) || array.Type is not PointerRef { Element: var pointee } || !ManagedTypes.IsVoid(pointee)
            || !low.Type.Equals(types.Int32) || !high.Type.Equals(types.Int32) || !method.Concrete(comparer.Type).Equals(comparerType)
            || comparerType.Parameter?.Constraints.Select(method.Concrete).SingleOrDefault() is not DefinitionRef
                { Definition.FullName: "System.Collections.Generic.IComparer", Arguments: [var compared] } iface || !compared.Equals(element)
            || facts.Code.At(entry) is not { Size: 696, ExceptionRegions.Length: 0 } code || code.Start != entry
            || facts.Model.Image.Resolve(entry) is not { Kind: AddressKind.FileBacked, FileOffset: { } offset, AvailableBytes: >= 696 }
            || System.Convert.ToHexString(SHA256.HashData(facts.Model.Image.Bytes.Span.Slice((int)offset, 696)))
                != "5551CBAA8A1E0E8CDB3E1A7BF41332B717FD1A6C83DD6498BFBD44901AE06AE6") return null;

        var utility = facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll"
            && t.FullName == "Unity.Collections.LowLevel.Unsafe.UnsafeUtility");
        var readDefinition = utility?.Methods.SingleOrDefault(m => m.Name == "ReadArrayElement" && m.IsStatic
            && m.GenericParameters.Count == 1 && m.Parameters is [var pointer, var index] && pointer.Type.Equals(array.Type) && index.Type.Equals(types.Int32));
        var writeDefinition = utility?.Methods.SingleOrDefault(m => m.Name == "WriteArrayElement" && m.IsStatic
            && m.GenericParameters.Count == 1 && m.Parameters is [var pointer, var index, _] && pointer.Type.Equals(array.Type) && index.Type.Equals(types.Int32));
        var compareDefinition = iface.Definition.Methods.SingleOrDefault(m => m.Name == "Compare" && !m.IsStatic && m.Parameters.Count == 2);
        if (readDefinition is null || writeDefinition is null || compareDefinition is null) return null;
        var read = new MethodRef(readDefinition, [], [element]);
        var write = new MethodRef(writeDefinition, [], [element]);
        var compare = new MethodRef(compareDefinition, [element], []);
        if (!read.Concrete(readDefinition.ReturnType).Equals(element) || !ManagedTypes.IsVoid(writeDefinition.ReturnType)
            || !write.Concrete(writeDefinition.Parameters[2].Type).Equals(element) || !compareDefinition.ReturnType.Equals(types.Int32)
            || !compareDefinition.Parameters.All(p => compare.Concrete(p.Type).Equals(element))) return null;

        var buffer = new ArgumentValue(0, array.Type, 64);
        var lo = new ArgumentValue(1, types.Int32, 32);
        var hi = new ArgumentValue(2, types.Int32, 32);
        var comp = new ArgumentValue(3, comparerType, types.ValueWidth(comparerType));
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        LocalValue Local(int slot, TypeRef type) => new(new Variable(Storage.Temp(slot), 1), type, types.ValueWidth(type));
        var i = Local(0, types.Int32);
        var j = Local(1, types.Int32);
        var saved = Local(2, element);
        var item = Local(3, element);
        var order = Local(4, types.Int32);
        var outer = entry + 4;
        var take = entry + 8;
        var inner = entry + 12;
        var compareItems = entry + 16;
        var shift = entry + 20;
        var insert = entry + 24;
        var done = entry + 28;
        ManagedValue Next(ManagedValue index) => new BinaryValue(BinOp.Add, index, one, types.Int32, 32);
        ManagedBranch Test(ulong at, Cond condition, ManagedValue lhs, ManagedValue rhs, ulong yes, ulong no) =>
            new(at, new ComparisonValue(condition, lhs, rhs, types.Boolean), new(yes, []), new(no, []));
        ManagedJump Jump(ulong at, ulong target) => new(at, new(target, []));
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedAssignment(entry, i, lo), Jump(entry, outer)]),
            new(outer, [Test(outer, Cond.Slt, i, hi, take, done)]),
            new(take, [new ManagedAssignment(take, j, i), new ManagedCall(take, new(read, null, [buffer, Next(i)], false), saved), Jump(take, inner)]),
            new(inner, [Test(inner, Cond.Sge, j, lo, compareItems, insert)]),
            new(compareItems, [new ManagedCall(compareItems, new(read, null, [buffer, j], false), item),
                new ManagedCall(compareItems, new ManagedInvocation(compare, new AddressValue(comp), [saved, item], true) { Constraint = comparerType }, order),
                Test(compareItems, Cond.Slt, order, zero, shift, insert)]),
            new(shift, [new ManagedCall(shift, new(read, null, [buffer, j], false), item),
                new ManagedCall(shift, new(write, null, [buffer, Next(j), item], false), null),
                new ManagedAssignment(shift, j, new BinaryValue(BinOp.Sub, j, one, types.Int32, 32)), Jump(shift, inner)]),
            new(insert, [new ManagedCall(insert, new(write, null, [buffer, Next(j), saved], false), null),
                new ManagedAssignment(insert, i, Next(i)), Jump(insert, outer)]),
            new(done, [new ManagedReturn(done, null)]),
        ];
        return new(method, entry, new ManagedBody(method, blocks), [])
        {
            ReferenceSource = UrpSource,
            Rules = [new RuleTrace("source.reference", entry, RewriteStatus.Applied, "urp-14.0.12", "UTess.cs：完全共享泛型插入排序", null)],
        };
    }
}
