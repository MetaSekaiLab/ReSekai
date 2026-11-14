using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private const string PartitionNative = "6132383A21F41C7F1DAA60D7A7F6836028888E8FAD3FA39278082CA95E5963C2";

    private MethodRecoveryResult? RestoreGenericSort(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { IsStatic: true, Parameters: [var data, var lower, var upper],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", Namespace: "UnityEngine.Rendering", Name: "CoreUnsafeUtils" } owner }
            || method.MethodArguments is not [GenericParameterRef element, GenericParameterRef key, GenericParameterRef getter]
            || method.Concrete(data.Type) is not PointerRef { Element: var pointee } buffer || !ManagedTypes.IsVoid(pointee)
            || !lower.Type.Equals(types.Int32) || !upper.Type.Equals(types.Int32)) return null;
        var isSort = method.Method.Name == "QuickSort" && ManagedTypes.IsVoid(method.Method.ReturnType)
            && ReferenceBodyMatches(entry, 152, "F9B75E98469BC1DC0614B3556D375D94DFE16ED4CBD849C4944EBA0E4D9BF17B");
        var isPartition = method.Method.Name == "Partition" && method.Method.ReturnType.Equals(types.Int32)
            && ReferenceBodyMatches(entry, 300, PartitionNative);
        if (!isSort && !isPartition) return null;

        var witness = facts.EntriesAt(entry).Where(e => e.Kind == EntryKind.GenericMethod && e.Method?.Method == method.Method)
            .Select(e => e.Method!).FirstOrDefault(m => m.MethodArguments is [var a, var b, DefinitionRef g]
                && a.Equals(types.Int32) && b.Equals(types.Int32) && g.Definition.DeclaringType == owner
                && g.Definition.SimpleName == "DefaultKeyGetter" && g.Arguments is [var k] && k.Equals(types.Int32));
        if (witness is null) return null;
        var body = isSort ? GenericQuickSort(method, witness, buffer, entry)
            : GenericPartition(method, witness, buffer, element, key, getter, entry);
        return body is null ? null : result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12",
                "CoreUnsafeUtils.cs：保留排序、分区的开放类型参数和受约束调用", null)],
        };
    }

    private ManagedBody? GenericQuickSort(MethodRef method, MethodRef witness, PointerRef buffer, ulong entry)
    {
        var target = method.Method.Owner.Methods.SingleOrDefault(m => m is { Name: "Partition", IsStatic: true, GenericParameters.Count: 3, Parameters.Count: 3 });
        if (target is null || !target.ReturnType.Equals(types.Int32)
            || !ReferenceCall(entry + 0x48, m => m.Method == target && m.MethodArguments.SequenceEqual(witness.MethodArguments))
            || !ReferenceCall(entry + 0x6c, m => m.Equals(witness))) return null;

        var address = new ArgumentValue(0, buffer, 64);
        var right = new ArgumentValue(2, types.Int32, 32);
        var left = SortLocal(0, types.Int32);
        var pivot = SortLocal(1, types.Int32);
        var one = SortConstant(1);
        var test = entry + 0x2c;
        var split = entry + 0x34;
        var recurse = entry + 0x58;
        var advance = entry + 0x70;
        var done = entry + 0x88;
        return new(method, [new(entry, [new ManagedJump(entry, new(test, [new(left, new ArgumentValue(1, types.Int32, 32))]))]),
            new(test, [new ManagedBranch(test, SortCompare(Cond.Slt, left, right), new(split, []), new(done, []))]),
            new(split, [new ManagedCall(entry + 0x48, new(new(target, [], method.MethodArguments), null, [address, left, right], false), pivot),
                new ManagedBranch(entry + 0x54, SortCompare(Cond.Sge, pivot, one), new(recurse, []), new(advance, []))]),
            new(recurse, [new ManagedCall(entry + 0x6c, new(method, null, [address, left, pivot], false), null), new ManagedJump(recurse, new(advance, []))]),
            new(advance, [new ManagedJump(advance, new(test, [new(left, SortArithmetic(BinOp.Add, pivot, one))]))]),
            new(done, [new ManagedReturn(done, null)])]);
    }

    private ManagedBody? GenericPartition(MethodRef method, MethodRef witness, PointerRef buffer,
        GenericParameterRef element, GenericParameterRef key, GenericParameterRef getter, ulong entry)
    {
        var getType = getter.Parameter?.Constraints.OfType<DefinitionRef>().SingleOrDefault(c =>
            c.Definition.DeclaringType == method.Method.Owner && c.Definition.SimpleName == "IKeyGetter");
        var compareType = key.Parameter?.Constraints.OfType<DefinitionRef>().SingleOrDefault(c => c.Definition.FullName == "System.IComparable");
        if (getType is null || method.Concrete(getType) is not DefinitionRef { Arguments: [var item, var keyResult] } getInterface
            || compareType is null || method.Concrete(compareType) is not DefinitionRef { Arguments: [var comparisonKey] } compareInterface
            || !item.Equals(element) || !keyResult.Equals(key) || !comparisonKey.Equals(key)) return null;
        var get = getInterface.Definition.Methods.SingleOrDefault(m => m is { Name: "Get", IsVirtual: true, Parameters.Count: 1 });
        var compare = compareInterface.Definition.Methods.SingleOrDefault(m => m is { Name: "CompareTo", IsVirtual: true, Parameters.Count: 1 });
        var utility = facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll" && t.FullName == "Unity.Collections.LowLevel.Unsafe.UnsafeUtility");
        var read = utility?.Methods.SingleOrDefault(m => m is { Name: "ReadArrayElement", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 2 });
        var write = utility?.Methods.SingleOrDefault(m => m is { Name: "WriteArrayElement", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 3 });
        if (get is null || compare is null || read is null || write is null
            || !ReferenceCall(entry + 0x58, m => m.Method.Name.EndsWith("Get", StringComparison.Ordinal) && m.DeclaringType.Equals(witness.MethodArguments[2]))
            || !ReferenceCall(entry + 0xa0, m => m.Method.Name == "CompareTo" && m.DeclaringType.Equals(types.Int32))) return null;

        var address = new ArgumentValue(0, buffer, 64);
        var left = SortLocal(0, types.Int32);
        var right = SortLocal(1, types.Int32);
        var selector = SortLocal(2, getter);
        var pivotValue = SortLocal(3, element);
        var pivot = SortLocal(4, key);
        var leftValue = SortLocal(5, element);
        var rightValue = SortLocal(6, element);
        var currentKey = SortLocal(7, key);
        var comparison = SortLocal(8, types.Int32);
        var one = SortConstant(1);
        var zero = SortConstant(0);
        var scanLeft = entry + 0x6c;
        var scanRight = entry + 0xb0;
        var test = entry + 0xf0;
        var swap = entry + 0xf8;
        var done = entry + 0x110;
        ManagedCall Read(ulong at, ManagedValue index, LocalValue value) =>
            new(at, new(new(read, [], [element]), null, [address, index], false), value);
        ManagedCall Get(ulong at, LocalValue value, LocalValue result) => new(at,
            new ManagedInvocation(new(get, getInterface.Arguments, []), new AddressValue(selector), [new AddressValue(value)], true) { Constraint = getter }, result);
        ManagedCall Compare(ulong at) => new(at,
            new ManagedInvocation(new(compare, compareInterface.Arguments, []), new AddressValue(currentKey), [pivot], true) { Constraint = key }, comparison);
        ManagedCall Write(ulong at, LocalValue index, LocalValue value) => new(at, new(new(write, [], [element]), null, [address, index, value], false), null);
        return new(method, [new(entry, [
                new ManagedAssignment(entry, selector, new ComposedValue(getter, [])),
                Read(entry + 0x44, new ArgumentValue(1, types.Int32, 32), pivotValue), Get(entry + 0x58, pivotValue, pivot),
                new ManagedJump(entry + 0x64, new(scanLeft, [new(left, SortArithmetic(BinOp.Sub, new ArgumentValue(1, types.Int32, 32), one)),
                    new(right, SortArithmetic(BinOp.Add, new ArgumentValue(2, types.Int32, 32), one))]))]),
            new(scanLeft, [new ManagedAssignment(scanLeft, left, SortArithmetic(BinOp.Add, left, one)),
                Read(scanLeft, left, leftValue), Get(entry + 0x88, leftValue, currentKey), Compare(entry + 0xa0),
                new ManagedBranch(entry + 0xa4, SortCompare(Cond.Slt, comparison, zero), new(scanLeft, []), new(scanRight, []))]),
            new(scanRight, [new ManagedAssignment(scanRight, right, SortArithmetic(BinOp.Sub, right, one)),
                Read(scanRight, right, rightValue), Get(entry + 0xcc, rightValue, currentKey), Compare(entry + 0xe4),
                new ManagedBranch(entry + 0xec, SortCompare(Cond.Sgt, comparison, zero), new(scanRight, []), new(test, []))]),
            new(test, [new ManagedBranch(test, SortCompare(Cond.Slt, left, right), new(swap, []), new(done, []))]),
            new(swap, [Write(entry + 0x100, right, leftValue), Write(entry + 0x108, left, rightValue),
                new ManagedJump(entry + 0x10c, new(scanLeft, []))]),
            new(done, [new ManagedReturn(done, right)])]);
    }

    private bool ReferenceCall(ulong address, Func<MethodRef, bool> match) =>
        facts.Code.Word(address) is { } word && A64.IsBl(word)
        && facts.EntriesAt(A64.Imm26Target(word, address)).Any(e => e.Method is { } target && match(target));

    private LocalValue SortLocal(int index, TypeRef type) => new(new Variable(Storage.Temp(index), 1), type, types.ValueWidth(type));
    private ConstantValue SortConstant(ulong bits) => new(bits, types.Int32, 32);
    private BinaryValue SortArithmetic(BinOp op, ManagedValue left, ManagedValue right) => new(op, left, right, types.Int32, 32);
    private ComparisonValue SortCompare(Cond condition, ManagedValue left, ManagedValue right) => new(condition, left, right, types.Boolean);
}
