using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private MethodRecoveryResult? RestoreProbeScenario(MethodRecoveryResult result)
    {
        var method = result.Method;
        var entry = result.Entry;
        if (method.Method is not { Name: "ResolvePerScenarioCellData", IsStatic: false, Parameters: [var input, var optional, var state],
                Owner: { Assembly: "Unity.RenderPipelines.Core.Runtime.dll", FullName: "UnityEngine.Rendering.ProbeVolumeAsset" } owner }
            || input.Type is not DefinitionRef { Definition.FullName: "UnityEngine.TextAsset" } asset || !asset.Equals(optional.Type)
            || !state.Type.Equals(types.Int32) || !method.Method.ReturnType.Equals(types.Boolean)
            || !ReferenceBodyMatches(entry, 1272, "46B92FDB9DF291EA500461B5F2A04A40F7B9824D51BD08FA325E89B7BC898932")) return null;
        var cells = owner.Fields.SingleOrDefault(f => f is { Name: "cells", IsStatic: false });
        var counts = owner.Fields.SingleOrDefault(f => f is { Name: "cellCounts", IsStatic: false });
        var totals = owner.Fields.SingleOrDefault(f => f is { Name: "totalCellCounts", IsStatic: false });
        var chunkSize = owner.Fields.SingleOrDefault(f => f is { Name: "chunkSizeInBricks", IsStatic: false });
        if (cells?.Type is not ArrayRef { IsVector: true, Element: DefinitionRef cell } cellArray
            || counts?.Type is not ArrayRef { IsVector: true, Element: DefinitionRef cellCount } countArray
            || !cellCount.Equals(totals?.Type) || !types.Int32.Equals(chunkSize?.Type)) return null;
        var chunks = cellCount.Definition.Fields.SingleOrDefault(f => f is { Name: "chunksCount", IsStatic: false });
        var scenario0 = cell.Definition.Fields.SingleOrDefault(f => f is { Name: "scenario0", IsStatic: false });
        var scenario1 = cell.Definition.Fields.SingleOrDefault(f => f is { Name: "scenario1", IsStatic: false });
        if (scenario0?.Type is not DefinitionRef scenario || !scenario.Equals(scenario1?.Type)
            || !types.Int32.Equals(chunks?.Type)) return null;

        var align = owner.Methods.SingleOrDefault(m => m is { Name: "AlignUp16", IsStatic: true, Parameters.Count: 1 });
        var split = owner.Methods.SingleOrDefault(m => m is { Name: "GetSubArray", IsStatic: false, GenericParameters.Count: 1, Parameters.Count: 3 });
        var getData = asset.Definition.Methods.SingleOrDefault(m => m is { Name: "GetData", IsStatic: false, GenericParameters.Count: 1, Parameters.Count: 0 });
        var objectType = asset.Definition.BaseType as DefinitionRef;
        var equal = objectType?.Definition.Methods.SingleOrDefault(m => m is { Name: "op_Equality", IsStatic: true, Parameters.Count: 2 });
        var alive = objectType?.Definition.Methods.SingleOrDefault(m => m is { Name: "op_Implicit", IsStatic: true, Parameters.Count: 1 });
        if (align is null || split is null || getData is null || equal is null || alive is null) return null;
        var bytes = new MethodRef(getData, [], [types.Byte]);
        if (bytes.Concrete(getData.ReturnType) is not DefinitionRef { Definition.FullName: "Unity.Collections.NativeArray", Arguments: [var item] } nativeBytes
            || !item.Equals(types.Byte)) return null;
        var native = nativeBytes.Definition;
        var length = native.Methods.SingleOrDefault(m => m is { Name: "get_Length", IsStatic: false, Parameters.Count: 0 });
        var isCreated = native.Methods.SingleOrDefault(m => m is { Name: "get_IsCreated", IsStatic: false, Parameters.Count: 0 });
        var sub = native.Methods.SingleOrDefault(m => m is { Name: "GetSubArray", IsStatic: false, Parameters.Count: 2 });
        if (length is null || isCreated is null || sub is null || objectType is null
            || !ReferenceCall(entry + 0xd4, m => m.Method == equal) || !ReferenceCall(entry + 0x1e0, m => m.Method == alive)
            || !ReferenceCall(entry + 0x100, m => m.Equals(bytes)) || !ReferenceCall(entry + 0x1fc, m => m.Equals(bytes))) return null;

        string[] properties = ["shL0L1RxData", "shL1GL1RyData", "shL1BL1RzData", "shL2Data_0", "shL2Data_1", "shL2Data_2", "shL2Data_3"];
        int[] splitOffsets = [0x13c, 0x164, 0x188, 0x228, 0x248, 0x268, 0x288];
        int[] sliceOffsets = [0x33c, 0x354, 0x36c, 0x388, 0x3a4, 0x3c0, 0x3dc];
        var section = new LocalValue[properties.Length];
        var splitCalls = new MethodRef[properties.Length];
        var sliceCalls = new MethodRef[properties.Length];
        var writeCalls = new MethodRef[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            var element = i == 0 ? types.UInt16 : types.Byte;
            var array = new DefinitionRef(native, [element]);
            var setter = scenario.Definition.Properties.SingleOrDefault(p => p.Name == properties[i])?.Setter;
            var splitCall = new MethodRef(split, [], [element]);
            var sliceCall = new MethodRef(sub, [element], []);
            if (setter is not { IsStatic: false, Parameters: [var value] } || !value.Type.Equals(array)
                || !splitCall.Concrete(split.ReturnType).Equals(array) || !sliceCall.Concrete(sub.ReturnType).Equals(array)
                || !ReferenceCall(entry + (ulong)splitOffsets[i], m => m.Equals(splitCall))
                || !ReferenceCall(entry + (ulong)sliceOffsets[i], m => m.Equals(sliceCall))) return null;
            section[i] = SortLocal(14 + i, array);
            splitCalls[i] = splitCall;
            sliceCalls[i] = sliceCall;
            writeCalls[i] = new(setter, [], []);
        }

        var self = new ArgumentValue(-1, method.DeclaringType, 64);
        var assetArg = new ArgumentValue(0, asset, 64);
        var optionalArg = new ArgumentValue(1, asset, 64);
        var stateArg = new ArgumentValue(2, types.Int32, 32);
        var condition = SortLocal(0, types.Boolean);
        var chunkWidth = SortLocal(1, types.Int32);
        var totalWidth = SortLocal(2, types.Int32);
        var payload = SortLocal(3, nativeBytes);
        var optionalData = SortLocal(4, nativeBytes);
        var offset = SortLocal(5, types.Int32);
        var aligned = SortLocal(6, types.Int32);
        var hasOptional = SortLocal(7, types.Boolean);
        var index = SortLocal(8, types.Int32);
        var start = SortLocal(9, types.Int32);
        var count = SortLocal(10, types.Int32);
        var sliceOffset = SortLocal(11, types.Int32);
        var sliceSize = SortLocal(12, types.Int32);
        var data = SortLocal(13, scenario);
        var zero = SortConstant(0);
        var one = SortConstant(1);
        var falseValue = new ConstantValue(0, types.Boolean, 1);
        var trueValue = new ConstantValue(1, types.Boolean, 1);
        var cellList = Field(self, cells!, method.DeclaringType);
        var countList = Field(self, counts!, method.DeclaringType);
        var totalChunks = Field(Field(self, totals!, method.DeclaringType), chunks!, cellCount);
        var cellAtIndex = new ArrayElementValue(cellList, index, cell, 64);
        var countAtIndex = new ArrayElementValue(countList, index, cellCount, 0);
        var primary = entry + 0xe8;
        var chooseOptional = entry + 0x1d8;
        var readOptional = entry + 0x1f4;
        var splitOptional = entry + 0x208;
        var optionalLength = entry + 0x294;
        var init = entry + 0x2c8;
        var test = entry + 0x2ec;
        var cellBody = entry + 0x2f8;
        var optionalSlices = entry + 0x378;
        var chooseState = entry + 0x400;
        var writeZero = entry + 0x46c;
        var writeOther = entry + 0x42c;
        var advance = entry + 0x4b0;
        var done = entry + 0x4d0;
        var fail = entry + 0xdc;
        var body = new ManagedBody(method, [new(entry, [Call(entry + 0xd4, new(equal, [], []), null,
                    [assetArg, new ConstantValue(0, objectType, 64)], condition), Branch(entry + 0xd8, condition, fail, primary)]),
            new(primary, [new ManagedAssignment(primary, chunkWidth, Mul(Field(self, chunkSize!, method.DeclaringType), SortConstant(256))),
                new ManagedAssignment(primary + 4, totalWidth, Mul(totalChunks, chunkWidth)), Call(entry + 0x100, bytes, assetArg, [], payload),
                new ManagedAssignment(primary + 8, offset, zero), .. Enumerable.Range(0, 3).Select(i => Split(i, payload)),
                .. AlignPayload(entry + 0x18c, payload), Branch(entry + 0x1c4, SortCompare(Cond.Eq, aligned, offset), chooseOptional, fail)]),
            new(chooseOptional, [Call(entry + 0x1e0, new(alive, [], []), null, [optionalArg], condition),
                new ManagedAssignment(chooseOptional, optionalData, new ComposedValue(nativeBytes, [])),
                Branch(entry + 0x1ec, condition, readOptional, splitOptional)]),
            new(readOptional, [Call(entry + 0x1fc, bytes, optionalArg, [], optionalData), Jump(readOptional, splitOptional)]),
            new(splitOptional, [Call(splitOptional, new(isCreated, [types.Byte], []), new AddressValue(optionalData), [], hasOptional),
                new ManagedAssignment(splitOptional, offset, zero), .. Enumerable.Range(3, 4).Select(i => Split(i, optionalData)),
                Branch(entry + 0x290, hasOptional, optionalLength, init)]),
            new(optionalLength, [.. AlignPayload(optionalLength, optionalData),
                Branch(entry + 0x2c4, SortCompare(Cond.Eq, aligned, offset), init, fail)]),
            new(init, [new ManagedJump(init, new(test, [new(index, zero), new(start, zero)]))]),
            new(test, [Branch(test, SortCompare(Cond.Slt, index, new ArrayLengthValue(cellList, types.Int32, 32)), cellBody, done)]),
            new(cellBody, [new ManagedAssignment(cellBody, count, Field(countAtIndex, chunks!, cellCount)),
                new ManagedAssignment(cellBody + 4, data, new ComposedValue(scenario, [])),
                new ManagedAssignment(cellBody + 8, sliceOffset, Mul(start, chunkWidth)),
                new ManagedAssignment(cellBody + 12, sliceSize, Mul(count, chunkWidth)),
                .. Enumerable.Range(0, 3).SelectMany(Slice), Branch(entry + 0x374, hasOptional, optionalSlices, chooseState)]),
            new(optionalSlices, [.. Enumerable.Range(3, 4).SelectMany(Slice), Jump(optionalSlices, chooseState)]),
            new(chooseState, [Branch(entry + 0x424, SortCompare(Cond.Eq, stateArg, zero), writeZero, writeOther)]),
            new(writeZero, [new ManagedStore(writeZero, Field(cellAtIndex, scenario0, cell), data), Jump(writeZero, advance)]),
            new(writeOther, [new ManagedStore(writeOther, Field(cellAtIndex, scenario1!, cell), data), Jump(writeOther, advance)]),
            new(advance, [new ManagedJump(advance, new(test, [new(index, Add(index, one)), new(start, Add(start, count))]))]),
            new(done, [new ManagedReturn(done, trueValue)]), new(fail, [new ManagedReturn(fail, falseValue)])]);
        return result with
        {
            Body = body, Partial = null, Holes = [], ReferenceSource = SrpCoreSource,
            Rules = [.. result.Rules, new RuleTrace("source.reference", entry, RewriteStatus.Applied, "srp-core-14.0.12",
                "ProbeVolumeAsset.cs：按场景写回七组 NativeArray 切片，保留对齐与可选数据检查", null)],
        };

        ManagedValue Add(ManagedValue a, ManagedValue b) => SortArithmetic(BinOp.Add, a, b);
        ManagedValue Mul(ManagedValue a, ManagedValue b) => SortArithmetic(BinOp.Mul, a, b);
        FieldValue Field(ManagedValue receiver, FieldDef field, TypeRef declaring) =>
            new(receiver, new(field, declaring), field.Type, types.ValueWidth(field.Type));
        ManagedCall Split(int i, LocalValue value) => Call(entry + (ulong)splitOffsets[i], splitCalls[i], self,
            [value, totalWidth, new AddressValue(offset)], section[i]);
        ManagedStatement[] Slice(int i)
        {
            var at = entry + (ulong)sliceOffsets[i];
            var sliced = SortLocal(21 + i, section[i].Type);
            return [Call(at, sliceCalls[i], new AddressValue(section[i]), [sliceOffset, sliceSize], sliced),
                Call(at, writeCalls[i], new AddressValue(data), [sliced], null)];
        }
        ManagedStatement[] AlignPayload(ulong at, LocalValue value) =>
            [Call(at, new(length, [types.Byte], []), new AddressValue(value), [], aligned),
                Call(at, new(align, [], []), null, [aligned], aligned)];
        static ManagedBranch Branch(ulong at, ManagedValue condition, ulong yes, ulong no) => new(at, condition, new(yes, []), new(no, []));
        static ManagedJump Jump(ulong at, ulong target) => new(at, new(target, []));
        static ManagedCall Call(ulong at, MethodRef target, ManagedValue? receiver, ManagedValue[] arguments, LocalValue? value) =>
            new(at, new(target, receiver, arguments, false), value);
    }
}
