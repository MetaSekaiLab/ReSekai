using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private MethodRecoveryResult? RestorePerSceneSerialization(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "UnityEngine.ISerializationCallbackReceiver.OnBeforeSerialize", IsStatic: false, Parameters.Count: 0,
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", FullName: "UnityEngine.Rendering.ProbeVolumePerSceneData" } owner }
            || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !ReferenceBodyMatches(entry, 692, "89E5B07A70A93CBF44D7F15E0F2679BB7755DA1ED485BAD8FF1041985457B675",
                "CC223A305BD4D94BEEC85032310968A751D5017D6F1F458D45DAA3CB4570C330")) return null;
        var serialized = owner.Fields.SingleOrDefault(f => f is { Name: "serializedScenarios", IsStatic: false });
        var scenarios = owner.Fields.SingleOrDefault(f => f is { Name: "scenarios", IsStatic: false });
        if (serialized?.Type is not DefinitionRef { Definition.FullName: "System.Collections.Generic.List", Arguments: [DefinitionRef item] } list
            || scenarios?.Type is not DefinitionRef { Definition.FullName: "System.Collections.Generic.Dictionary", Arguments: [var keyType, var valueType] } dictionary
            || item.Definition.DeclaringType != owner || item.Definition.SimpleName != "SerializablePerScenarioDataItem") return null;
        var key = item.Definition.Fields.SingleOrDefault(f => f is { Name: "scenario", IsStatic: false });
        var value = item.Definition.Fields.SingleOrDefault(f => f is { Name: "data", IsStatic: false });
        var clear = list.Definition.Methods.SingleOrDefault(m => m is { Name: "Clear", IsStatic: false, Parameters.Count: 0 });
        var add = list.Definition.Methods.SingleOrDefault(m => m is { Name: "Add", IsStatic: false, Parameters.Count: 1 });
        var resize = list.Definition.Methods.SingleOrDefault(m => m is { Name: "AddWithResize", IsStatic: false, Parameters.Count: 1 });
        var getEnumerator = dictionary.Definition.Methods.SingleOrDefault(m => m is { Name: "GetEnumerator", IsStatic: false, Parameters.Count: 0 });
        if (key is null || value is null || clear is null || add is null || resize is null || getEnumerator is null
            || !key.Type.Equals(keyType) || !value.Type.Equals(valueType)) return null;
        var enumerate = new MethodRef(getEnumerator, dictionary.Arguments, []);
        if (enumerate.Concrete(getEnumerator.ReturnType) is not DefinitionRef iterator
            || !ReferenceCall(entry + 0x100, m => MatchesSharedReference(enumerate, m))
            || !ReferenceCall(entry + 0x204, m => m.Method == resize && m.TypeArguments.SequenceEqual(list.Arguments))) return null;
        var moveNext = iterator.Definition.Methods.SingleOrDefault(m => m is { Name: "MoveNext", IsStatic: false, Parameters.Count: 0 });
        var getCurrent = iterator.Definition.Methods.SingleOrDefault(m => m is { Name: "get_Current", IsStatic: false, Parameters.Count: 0 });
        var dispose = iterator.Definition.Methods.SingleOrDefault(m => m is { Name: "Dispose", IsStatic: false, Parameters.Count: 0 });
        if (moveNext is null || getCurrent is null || dispose is null) return null;
        var move = new MethodRef(moveNext, iterator.Arguments, []);
        var current = new MethodRef(getCurrent, iterator.Arguments, []);
        var close = new MethodRef(dispose, iterator.Arguments, []);
        if (current.Concrete(getCurrent.ReturnType) is not DefinitionRef pair
            || !ReferenceCall(entry + 0x138, m => MatchesSharedReference(move, m))
            || !ReferenceCall(entry + 0x214, m => MatchesSharedReference(close, m))
            || !ReferenceCall(entry + 0x274, m => MatchesSharedReference(close, m))) return null;
        var getKey = pair.Definition.Methods.SingleOrDefault(m => m is { Name: "get_Key", IsStatic: false, Parameters.Count: 0 });
        var getValue = pair.Definition.Methods.SingleOrDefault(m => m is { Name: "get_Value", IsStatic: false, Parameters.Count: 0 });
        if (getKey is null || getValue is null) return null;
        var pairKey = new MethodRef(getKey, pair.Arguments, []);
        var pairValue = new MethodRef(getValue, pair.Arguments, []);
        if (!pairKey.Concrete(getKey.ReturnType).Equals(keyType) || !pairValue.Concrete(getValue.ReturnType).Equals(valueType)) return null;

        var self = new ArgumentValue(-1, method.DeclaringType, 64);
        var destination = new FieldValue(self, new(serialized, method.DeclaringType), list, 64);
        var source = new FieldValue(self, new(scenarios, method.DeclaringType), dictionary, 64);
        var enumerator = SortLocal(0, iterator);
        var more = SortLocal(1, types.Boolean);
        var pairValueLocal = SortLocal(2, pair);
        var sceneKey = SortLocal(3, keyType);
        var sceneData = SortLocal(4, valueType);
        var record = SortLocal(5, item);
        var test = entry + 0x130;
        var append = entry + 0x140;
        var cleanup = entry + 0x270;
        var done = entry + 0x218;
        var body = new ManagedBody(method, [new(entry, [Call(entry + 0xb8, new(clear, list.Arguments, []), destination, [], null, true),
                Call(entry + 0x100, enumerate, source, [], enumerator, true), new ManagedJump(entry + 0x104, new(test, []))]),
            new(test, [Call(entry + 0x138, move, new AddressValue(enumerator), [], more),
                new ManagedBranch(entry + 0x13c, more, new(append, []), new(done, []))]),
            new(append, [Call(append, current, new AddressValue(enumerator), [], pairValueLocal),
                Call(append + 4, pairKey, new AddressValue(pairValueLocal), [], sceneKey),
                Call(append + 8, pairValue, new AddressValue(pairValueLocal), [], sceneData),
                new ManagedAssignment(append + 12, record, new ComposedValue(item, [])),
                new ManagedStore(append + 16, new FieldValue(new AddressValue(record), new(key, item), keyType, 64), sceneKey),
                new ManagedStore(append + 20, new FieldValue(new AddressValue(record), new(value, item), valueType, types.ValueWidth(valueType)), sceneData),
                Call(entry + 0x204, new(add, list.Arguments, []), destination, [record], null, true),
                new ManagedJump(entry + 0x208, new(test, []))]),
            new(cleanup, [Call(entry + 0x274, close, new AddressValue(enumerator), [], null), new ManagedEndFault(entry + 0x278)]),
            new(done, [new ManagedReturn(done, null)])])
        {
            Regions = [new([test, append], cleanup, [cleanup], null, ManagedRegionKind.Finally)],
        };
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12",
                "ProbeVolumePerSceneData.cs：保留序列化条目复制及枚举器异常清理", null)],
        };

        static ManagedCall Call(ulong at, MethodRef target, ManagedValue receiver, ManagedValue[] arguments, LocalValue? value, bool virtualCall = false) =>
            new(at, new(target, receiver, arguments, virtualCall), value);
    }

    private bool MatchesSharedReference(MethodRef expected, MethodRef actual) => expected.Method == actual.Method
        && expected.TypeArguments.Count == actual.TypeArguments.Count && expected.MethodArguments.Count == actual.MethodArguments.Count
        && expected.TypeArguments.Zip(actual.TypeArguments).Concat(expected.MethodArguments.Zip(actual.MethodArguments))
            .All(pair => pair.First.Equals(pair.Second) || pair.Second.Equals(types.Object) && ManagedTypes.IsReferenceType(pair.First));

    private MethodRecoveryResult? RestoreSceneSerialization(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "OnBeforeSerialize", IsStatic: false, Parameters.Count: 0,
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", FullName: "UnityEngine.Rendering.ProbeVolumeSceneData" } owner }
            || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !ReferenceBodyMatches(entry, 3028, "B3AB1B3DA76819C1A0CF1DA6410D777E98DD5EC07634D1B3FA534CAEE5A739FE",
                "3D084BF403844682C2DDB0775A867373816D91E5EABEC9D851D43D5BB8B97913")) return null;

        var self = new ArgumentValue(-1, method.DeclaringType, 64);
        var blocks = new List<ManagedBlock>();
        var regions = new List<ManagedRegion>();
        var done = entry + 0x934;
        string[] names = ["sceneBounds", "hasProbeVolumes", "sceneBakingSettings", "sceneProfiles", "serializedBounds",
            "serializedHasVolumes", "serializedProfiles", "serializedBakeSettings", "serializedBakingSets", "bakingSets"];
        var fieldValues = names.Select(name => owner.Fields.SingleOrDefault(f => f.Name == name && !f.IsStatic)).ToArray();
        if (fieldValues.Any(f => f?.Type is not DefinitionRef d || !ManagedTypes.IsReferenceType(d))) return null;
        var fields = fieldValues.Select(f => new FieldValue(self, new(f!, method.DeclaringType), f!.Type, 64)).ToArray();
        var begin = entry + 0x290;
        ManagedValue? absent = null;
        foreach (var field in fields.Take(9))
        {
            var isNull = SortCompare(Cond.Eq, field, new ConstantValue(0, field.Type, 64));
            absent = absent is null ? isNull : new BooleanValue(false, absent, isNull, types.Boolean);
        }
        blocks.Add(new(entry, [new ManagedBranch(entry + 0x240, absent!, new(done, []), new(begin, []))]));
        var clearing = new List<ManagedStatement>();
        var clearOffsets = new ulong[] { 0x290, 0x2bc, 0x2e8, 0x314, 0x340 };
        for (var i = 0; i < clearOffsets.Length; i++)
        {
            var list = (DefinitionRef)fields[4 + i].Type;
            if (ListMethod(list, "Clear", 0) is not { } clear) return null;
            clearing.Add(Invoke(entry + clearOffsets[i], clear, fields[4 + i], [], null, true));
        }
        ulong[] starts = [0x36c, 0x4b0, 0x5c8, 0x754, 0x880, 0x934];
        clearing.Add(Jump(begin, entry + starts[0]));
        blocks.Add(new(begin, clearing));

        if (!AddMapLoop(0, 4, "bounds", 0x384, 0x3c0, 0x3f0, 0x480, 0x498, 0xb24)
            || !AddMapLoop(1, 5, "hasProbeVolumes", 0x4c8, 0x4fc, 0x528, 0x598, 0x5b0, 0xac0)
            || !AddMapLoop(2, 7, "settings", 0x5e0, 0x620, 0x650, 0x724, 0x73c, 0xa5c)
            || !AddMapLoop(3, 6, "profile", 0x76c, 0x7a8, 0x7d4, 0x84c, 0x864, 0x9f8)
            || !AddSetLoop()) return null;
        blocks.Add(new(done, [new ManagedReturn(done, null)]));
        return result with
        {
            Body = new ManagedBody(method, blocks) { Regions = regions }, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12",
                "ProbeVolumeSceneData.cs：按原顺序序列化各字典与烘焙集，保留五个枚举器清理区域", null)],
        };

        bool AddMapLoop(int number, int target, string fieldName, ulong openAt, ulong moveAt, ulong readAt, ulong addAt, ulong closeAt, ulong faultAt)
        {
            var startAt = starts[number];
            var source = fields[number];
            var destination = fields[target];
            if (source.Type is not DefinitionRef { Definition.FullName: "System.Collections.Generic.Dictionary", Arguments: [var key, var value] } dictionary
                || destination.Type is not DefinitionRef { Arguments: [DefinitionRef item] } list
                || item.Definition.DeclaringType != owner
                || InstanceMethod(dictionary, "get_Keys", 0) is not { } getKeys
                || getKeys.Concrete(getKeys.Method.ReturnType) is not DefinitionRef keysType
                || InstanceMethod(dictionary, "get_Item", 1) is not { } read || !read.Concrete(read.Method.ReturnType).Equals(value)
                || !ReferenceCall(entry + startAt, m => MatchesSharedReference(getKeys, m))
                || !ReferenceCall(entry + readAt, m => MatchesSharedReference(read, m))) return false;
            var keyField = item.Definition.Fields.SingleOrDefault(f => f.Name == "sceneGUID" && !f.IsStatic && f.Type.Equals(key));
            var valueField = item.Definition.Fields.SingleOrDefault(f => f.Name == fieldName && !f.IsStatic && f.Type.Equals(value));
            if (keyField is null || valueField is null) return false;
            var keys = SortLocal(number * 7, keysType);
            var valueLocal = SortLocal(number * 7 + 1, value);
            var record = SortLocal(number * 7 + 2, item);
            return AddLoop(number, keysType, keys, list, destination, key,
                [Invoke(entry + startAt, getKeys, source, [], keys, true)], openAt, moveAt, addAt, closeAt, faultAt, keyLocal =>
                [Invoke(entry + readAt, read, source, [keyLocal], valueLocal, true),
                    new ManagedAssignment(entry + readAt, record, new ComposedValue(item, [])),
                    Store(entry + readAt, record, item, keyField, keyLocal), Store(entry + readAt, record, item, valueField, valueLocal)], record);
        }

        bool AddSetLoop()
        {
            var source = fields[9];
            var destination = fields[8];
            return source.Type is DefinitionRef { Arguments: [var set] } list && list.Equals(destination.Type)
                && AddLoop(4, list, source, list, destination, set, [], 0x880, 0x89c, 0x900, 0x918, 0xb8c, _ => [], null);
        }

        bool AddLoop(int number, DefinitionRef collection, ManagedValue receiver, DefinitionRef list, ManagedValue destination,
            TypeRef element, ManagedStatement[] initial, ulong openAt, ulong moveAt, ulong addAt, ulong closeAt, ulong faultAt,
            Func<LocalValue, ManagedStatement[]> buildItem, LocalValue? record)
        {
            if (InstanceMethod(collection, "GetEnumerator", 0) is not { } open
                || open.Concrete(open.Method.ReturnType) is not DefinitionRef iterator
                || InstanceMethod(iterator, "MoveNext", 0) is not { } move
                || InstanceMethod(iterator, "get_Current", 0) is not { } current
                || InstanceMethod(iterator, "Dispose", 0) is not { } close
                || ListMethod(list, "Add", 1) is not { } add || ListMethod(list, "AddWithResize", 1) is not { } resize
                || !current.Concrete(current.Method.ReturnType).Equals(element)
                || !add.Concrete(add.Method.Parameters[0].Type).Equals((TypeRef?)record?.Type ?? element)
                || !ReferenceCall(entry + openAt, m => MatchesSharedReference(open, m))
                || !ReferenceCall(entry + moveAt, m => MatchesSharedReference(move, m))
                || !ReferenceCall(entry + addAt, m => MatchesSharedReference(resize, m))
                || !ReferenceCall(entry + closeAt, m => MatchesSharedReference(close, m))
                || !ReferenceCall(entry + faultAt, m => MatchesSharedReference(close, m))) return false;
            var start = entry + starts[number];
            var test = entry + moveAt;
            var body = test + 8;
            var cleanup = entry + faultAt;
            var next = entry + starts[number + 1];
            var enumerator = SortLocal(number * 7 + 3, iterator);
            var more = SortLocal(number * 7 + 4, types.Boolean);
            var item = SortLocal(number * 7 + 5, element);
            blocks.Add(new(start, [.. initial, Invoke(entry + openAt, open, receiver, [], enumerator, true), Jump(start, test)]));
            blocks.Add(new(test, [Invoke(test, move, new AddressValue(enumerator), [], more),
                new ManagedBranch(test + 4, more, new(body, []), new(next, []))]));
            blocks.Add(new(body, [Invoke(body, current, new AddressValue(enumerator), [], item), .. buildItem(item),
                Invoke(entry + addAt, add, destination, [record ?? item], null, true), Jump(entry + addAt, test)]));
            blocks.Add(new(cleanup, [Invoke(cleanup, close, new AddressValue(enumerator), [], null), new ManagedEndFault(cleanup + 4)]));
            regions.Add(new([test, body], cleanup, [cleanup], null, ManagedRegionKind.Finally));
            return true;
        }

        ManagedStore Store(ulong at, LocalValue target, TypeRef type, FieldDef field, ManagedValue value) =>
            new(at, new FieldValue(new AddressValue(target), new(field, type), field.Type, types.ValueWidth(field.Type)), value);
        static MethodRef? InstanceMethod(DefinitionRef owner, string name, int arity) =>
            owner.Definition.Methods.SingleOrDefault(m => m.Name == name && !m.IsStatic && m.Parameters.Count == arity) is { } definition
                ? new(definition, owner.Arguments, []) : null;
        static MethodRef? ListMethod(DefinitionRef owner, string name, int arity) =>
            owner.Definition.FullName == "System.Collections.Generic.List" ? InstanceMethod(owner, name, arity) : null;
        static ManagedCall Invoke(ulong at, MethodRef target, ManagedValue receiver, ManagedValue[] args, LocalValue? value, bool virtualCall = false) =>
            new(at, new(target, receiver, args, virtualCall), value);
        static ManagedJump Jump(ulong at, ulong target) => new(at, new(target, []));
    }
}
