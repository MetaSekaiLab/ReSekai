using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private MethodRecoveryResult? RestoreCompareHashes(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "CompareHashes", IsStatic: true, Parameters.Count: 8,
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", FullName: "UnityEngine.Rendering.CoreUnsafeUtils" } owner }
            || method.MethodArguments is not [GenericParameterRef oldElement, GenericParameterRef oldGetter,
                GenericParameterRef newElement, GenericParameterRef newGetter]
            || !method.Method.ReturnType.Equals(types.Int32)
            || !ReferenceBodyMatches(entry, 972, "B00A394832B4EFEDA329FAA8E7DE7138B68938AB708AF7550B33A5434FB7CFC3")) return null;
        var pointer = new PointerRef(types.Int32);
        var counter = new ByRefRef(types.Int32);
        var signature = method.Method.Parameters.Select(p => method.Concrete(p.Type)).ToArray();
        if (signature is not [var oldLength, PointerRef { Element: var pointee } buffer, var newLength,
                var newBuffer, var adds, var removes, var addCount, var removeCount]
            || !oldLength.Equals(types.Int32) || !newLength.Equals(types.Int32) || !ManagedTypes.IsVoid(pointee)
            || !buffer.Equals(newBuffer) || !pointer.Equals(adds) || !pointer.Equals(removes)
            || !counter.Equals(addCount) || !counter.Equals(removeCount)) return null;

        var witness = facts.EntriesAt(entry).Where(e => e.Kind == EntryKind.GenericMethod && e.Method?.Method == method.Method)
            .Select(e => e.Method!).FirstOrDefault(m => m.MethodArguments is
                [DefinitionRef { Definition.FullName: "UnityEngine.Hash128" } hash, DefinitionRef getter, var newer, var newerGetter]
                && hash.Equals(newer) && getter.Equals(newerGetter) && getter.Definition.DeclaringType == owner
                && getter.Definition.SimpleName == "DefaultKeyGetter" && getter.Arguments is [var key] && key.Equals(hash));
        if (witness?.MethodArguments[0] is not DefinitionRef hashType) return null;
        var combine = owner.Methods.SingleOrDefault(m => m is { Name: "CombineHashes", IsStatic: true, GenericParameters.Count: 2, Parameters.Count: 3 });
        var equal = hashType.Definition.Methods.SingleOrDefault(m => m is { Name: "op_Equality", IsStatic: true, Parameters.Count: 2 });
        var less = hashType.Definition.Methods.SingleOrDefault(m => m is { Name: "op_LessThan", IsStatic: true, Parameters.Count: 2 });
        var create = types.CorlibDefinition("System.Activator")?.Methods.SingleOrDefault(m =>
            m is { Name: "CreateInstance", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 0 });
        var read = facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll" && t.FullName == "Unity.Collections.LowLevel.Unsafe.UnsafeUtility")
            ?.Methods.SingleOrDefault(m => m is { Name: "ReadArrayElement", IsStatic: true, GenericParameters.Count: 1, Parameters.Count: 2 });
        var oldGet = Getter(oldElement, oldGetter);
        var newGet = Getter(newElement, newGetter);
        if (combine is null || equal is null || less is null || create is null || read is null || oldGet is null || newGet is null
            || !ReferenceCall(entry + 0xac, m => m.Method == combine && m.MethodArguments.SequenceEqual(witness.MethodArguments.Take(2)))
            || !ReferenceCall(entry + 0xc4, m => m.Method == combine && m.MethodArguments.SequenceEqual(witness.MethodArguments.Skip(2)))
            || !ReferenceCall(entry + 0xd4, m => m.Method == equal) || !ReferenceCall(entry + 0x200, m => m.Method == equal)
            || !ReferenceCall(entry + 0x21c, m => m.Method == less) || !ReferenceCall(entry + 0x24c, m => m.Method == less)
            || !ReferenceCall(entry + 0x334, m => m.Method == less)) return null;

        var oldCountValue = new ArgumentValue(0, types.Int32, 32);
        var oldData = new ArgumentValue(1, buffer, 64);
        var newCountValue = new ArgumentValue(2, types.Int32, 32);
        var newData = new ArgumentValue(3, buffer, 64);
        var addIndices = new ArgumentValue(4, pointer, 64);
        var removeIndices = new ArgumentValue(5, pointer, 64);
        var added = new IndirectValue(new ArgumentValue(6, counter, 64), types.Int32, 32);
        var removed = new IndirectValue(new ArgumentValue(7, counter, 64), types.Int32, 32);
        var oldSelector = SortLocal(0, oldGetter);
        var newSelector = SortLocal(1, newGetter);
        var oldHash = SortLocal(2, hashType);
        var newHash = SortLocal(3, hashType);
        var same = SortLocal(4, types.Boolean);
        var oldIndex = SortLocal(5, types.Int32);
        var newIndex = SortLocal(6, types.Int32);
        var operations = SortLocal(7, types.Int32);
        var oldValue = SortLocal(8, oldElement);
        var newValue = SortLocal(9, newElement);
        var slot = SortLocal(10, types.Int32);
        var zero = SortConstant(0);
        var one = SortConstant(1);
        var four = new ConstantValue(4, types.Int64, 64);
        var combined = entry + 0x90;
        var start = entry + 0xdc;
        var test = entry + 0x140;
        var noOld = entry + 0x150;
        var noNew = entry + 0x158;
        var readBoth = entry + 0x198;
        var advance = entry + 0x2d8;
        var order = entry + 0x208;
        var testAddTail = entry + 0x298;
        var addTail = entry + 0x2ac;
        var testRemoveTail = entry + 0x160;
        var removeTail = entry + 0x170;
        var testAdd = entry + 0x2ec;
        var compareAdd = entry + 0x320;
        var add = entry + 0x33c;
        var testRemove = entry + 0x224;
        var compareRemove = entry + 0x238;
        var remove = entry + 0x254;
        var equalReturn = entry + 0xf0;
        var done = entry + 0xf4;

        var body = new ManagedBody(method, [new(entry, [Static(entry + 0x68, new(create, [], [oldGetter]), [], oldSelector),
                Static(entry + 0x78, new(create, [], [newGetter]), [], newSelector),
                new ManagedStore(entry + 0x84, added, zero), new ManagedStore(entry + 0x88, removed, zero),
                Branch(entry + 0x8c, SortCompare(Cond.Eq, oldCountValue, newCountValue), combined, start)]),
            new(combined, [new ManagedAssignment(combined, oldHash, new ComposedValue(hashType, [])),
                new ManagedAssignment(combined + 4, newHash, new ComposedValue(hashType, [])),
                Static(entry + 0xac, new(combine, [], [oldElement, oldGetter]), [oldCountValue, oldData, HashAddress(oldHash)], null),
                Static(entry + 0xc4, new(combine, [], [newElement, newGetter]), [newCountValue, newData, HashAddress(newHash)], null),
                Static(entry + 0xd4, new(equal, [], []), [oldHash, newHash], same), Branch(entry + 0xd8, same, equalReturn, start)]),
            new(equalReturn, [new ManagedReturn(equalReturn, zero)]),
            new(start, [new ManagedJump(start, new(test, [new(oldIndex, zero), new(newIndex, zero), new(operations, zero)]))]),
            new(test, [Branch(test, new BooleanValue(false, SortCompare(Cond.Slt, oldIndex, oldCountValue),
                SortCompare(Cond.Slt, newIndex, newCountValue), types.Boolean), noOld, done)]),
            new(noOld, [Branch(noOld, SortCompare(Cond.Eq, oldIndex, oldCountValue), testAddTail, noNew)]),
            new(noNew, [Branch(noNew, SortCompare(Cond.Eq, newIndex, newCountValue), testRemoveTail, readBoth)]),
            new(testAddTail, [Branch(testAddTail, SortCompare(Cond.Slt, newIndex, newCountValue), addTail, test)]),
            new(addTail, [.. AppendIndex(addTail, added, addIndices, newIndex), Jump(addTail + 16, testAddTail)]),
            new(testRemoveTail, [Branch(testRemoveTail, SortCompare(Cond.Slt, oldIndex, oldCountValue), removeTail, test)]),
            new(removeTail, [.. AppendIndex(removeTail, removed, removeIndices, oldIndex), Jump(removeTail + 16, testRemoveTail)]),
            new(readBoth, [Static(entry + 0x1a8, new(read, [], [newElement]), [newData, newIndex], newValue),
                Static(entry + 0x1b8, new(read, [], [oldElement]), [oldData, oldIndex], oldValue),
                Get(entry + 0x1c4, newGet, newSelector, newValue, newHash), Get(entry + 0x1e0, oldGet, oldSelector, oldValue, oldHash),
                Static(entry + 0x200, new(equal, [], []), [newHash, oldHash], same), Branch(entry + 0x204, same, advance, order)]),
            new(advance, [new ManagedJump(advance, new(test, [new(oldIndex, Inc(oldIndex)), new(newIndex, Inc(newIndex))]))]),
            new(order, [Static(entry + 0x21c, new(less, [], []), [newHash, oldHash], same), Branch(entry + 0x220, same, testAdd, testRemove)]),
            new(testAdd, [Branch(testAdd, SortCompare(Cond.Slt, newIndex, newCountValue), compareAdd, test)]),
            new(compareAdd, [Static(entry + 0x334, new(less, [], []), [newHash, oldHash], same), Branch(entry + 0x338, same, add, test)]),
            new(add, [.. AppendIndex(add, added, addIndices, newIndex),
                Static(entry + 0x36c, new(read, [], [newElement]), [newData, newIndex], newValue),
                Get(entry + 0x37c, newGet, newSelector, newValue, newHash), Jump(entry + 0x390, testAdd)]),
            new(testRemove, [Branch(testRemove, SortCompare(Cond.Slt, oldIndex, oldCountValue), compareRemove, test)]),
            new(compareRemove, [Static(entry + 0x24c, new(less, [], []), [oldHash, newHash], same), Branch(entry + 0x250, same, remove, test)]),
            new(remove, [.. AppendIndex(remove, removed, removeIndices, oldIndex), Jump(remove + 16, testRemove)]),
            new(done, [new ManagedReturn(done, operations)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12",
                "CoreUnsafeUtils.cs：保留两组开放泛型 getter 和原生有序差分分支", null)],
        };

        MethodRef? Getter(GenericParameterRef element, GenericParameterRef getter)
        {
            var constraint = getter.Parameter?.Constraints.OfType<DefinitionRef>().SingleOrDefault(c =>
                c.Definition.DeclaringType == owner && c.Definition.SimpleName == "IKeyGetter");
            if (constraint is null || method.Concrete(constraint) is not DefinitionRef { Arguments: [var item, var key] } iface
                || !item.Equals(element) || !key.Equals(hashType)) return null;
            var get = iface.Definition.Methods.SingleOrDefault(m => m is { Name: "Get", IsStatic: false, IsVirtual: true, Parameters.Count: 1 });
            if (get is null) return null;
            var reference = new MethodRef(get, iface.Arguments, []);
            return reference.Concrete(get.Parameters[0].Type) is ByRefRef byRef && byRef.Element.Equals(element)
                && reference.Concrete(get.ReturnType).Equals(hashType) ? reference : null;
        }
        ManagedValue HashAddress(LocalValue hash) => new ConversionValue(ConvKind.Bitcast, new AddressValue(hash), new PointerRef(hashType), 64);
        ManagedValue Inc(ManagedValue value) => SortArithmetic(BinOp.Add, value, one);
        ManagedStatement[] AppendIndex(ulong address, IndirectValue count, ArgumentValue indices, LocalValue index) =>
            [new ManagedAssignment(address, slot, count), new ManagedStore(address + 4, count, Inc(slot)),
                new ManagedStore(address + 8, new IndirectValue(new PointerOffsetValue(indices,
                    new BinaryValue(BinOp.Mul, new ConversionValue(ConvKind.SignExtend, slot, types.Int64, 64), four, types.Int64, 64)), types.Int32, 32), index),
                new ManagedAssignment(address + 12, index, Inc(index)), new ManagedAssignment(address + 16, operations, Inc(operations))];
        static ManagedBranch Branch(ulong address, ManagedValue condition, ulong yes, ulong no) => new(address, condition, new(yes, []), new(no, []));
        static ManagedJump Jump(ulong address, ulong target) => new(address, new(target, []));
        static ManagedCall Static(ulong address, MethodRef target, ManagedValue[] arguments, LocalValue? value) =>
            new(address, new(target, null, arguments, false), value);
        static ManagedCall Get(ulong address, MethodRef target, LocalValue receiver, LocalValue input, LocalValue value) => new(address,
            new ManagedInvocation(target, new AddressValue(receiver), [new AddressValue(input)], true) { Constraint = receiver.Type }, value);
    }
}
