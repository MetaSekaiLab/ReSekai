using System.Security.Cryptography;
using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Image;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private MethodRecoveryResult? RestoreUrpPackageMethod(MethodRef method, ulong entry)
    {
        if (method.Method.Owner.Assembly != "Unity.RenderPipelines.Universal.Runtime.dll") return null;
        return (method.Method.Owner.FullName, method.Method.Name) switch
        {
            ("UnityEngine.Rendering.Universal.LibTessDotNet.Tess", "TopLeftRegion") => RestoreTopLeftRegion(method, entry),
            ("UnityEngine.Rendering.Universal.LibTessDotNet.Tess", "WalkDirtyRegions") => RestoreWalkDirtyRegions(method, entry),
            ("UnityEngine.Rendering.Universal.ScriptableRenderer", "GetSubPassAttachmentIndicesCount") => RestoreSubPassCount(method, entry),
            ("UnityEngine.Rendering.Universal.PolyNode", "GetNextSiblingUp") => RestoreNextSibling(method, entry),
            ("UnityEngine.Rendering.Universal.Clipper", "ProcessIntersections") => RestoreProcessIntersections(method, entry),
            ("UnityEngine.Rendering.Universal.Fixed2", "get_Item" or "set_Item") => RestoreFixed2Item(method, entry),
            ("UnityEngine.Rendering.Universal.LightCookieManager.ShaderBitArray", "ToString") => RestoreShaderBitsString(method, entry),
            ("UnityEngine.Rendering.Universal.UniversalRenderPipeline", "CreateRenderTextureDescriptor") => RestoreCameraDescriptor(method, entry),
            ("UnityEngine.Rendering.Universal.UniversalRenderPipeline", "ProcessRenderRequests") => RestoreRenderRequests(method, entry),
            ("UnityEngine.Rendering.Universal.LibTessDotNet.MeshUtils.Vertex", "Reset") => RestoreVertexReset(method, entry),
            ("UnityEngine.Rendering.Universal.ShadowUtility.<>c", "<GenerateShadowMesh>b__9_2") => RestoreShadowColor(method, entry),
            ("UnityEngine.Rendering.Universal.ScriptableRenderer", "GetFirstAllocatedRTHandle") => RestoreFirstAllocatedHandle(method, entry),
            ("Sekai.Rendering.AreaPostProcessRenderPass", "AllocBloomSamplingHandles") => RestoreBloomSamplingHandles(method, entry),
            ("UnityEngine.Rendering.Universal.BuddyAllocator", "TryAllocate") => RestoreBuddyAllocation(method, entry),
            ("UnityEngine.Rendering.Universal.Internal.AdditionalLightsShadowCasterPass", "AtlasLayout") => RestoreAtlasLayout(method, entry),
            _ => null,
        };
    }

    private bool UrpCode(ulong entry, int size, string sha256, bool registered = true) =>
        (!registered || facts.Code.At(entry) is { Region: CodeRegion.Managed } code && code.Start == entry && code.Size == (ulong)size)
        && facts.Model.Image.Resolve(entry) is { Kind: AddressKind.FileBacked, FileOffset: { } offset, AvailableBytes: var available }
        && available >= (ulong)size && System.Convert.ToHexString(SHA256.HashData(facts.Model.Image.Bytes.Span.Slice((int)offset, size))) == sha256;

    private MethodRecoveryResult? RestoreFirstAllocatedHandle(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: true, GenericParameters.Count: 0,
                Parameters: [{ Type: DefinitionRef pass }], ReturnType: DefinitionRef handle }
            || pass.Definition.FullName != "UnityEngine.Rendering.Universal.ScriptableRenderPass"
            || handle.Definition.FullName != "UnityEngine.Rendering.RTHandle"
            || !UrpCode(entry, 220, "27AAC74811BA06190E8D46C592F39932B51A9A15BCD6BEA63194DF5D030F50D5")) return null;
        var handles = new ArrayRef(handle, 1);
        var texture = UrpType("UnityEngine.RenderTexture");
        var unityObject = UrpType("UnityEngine.Object");
        if (texture is null || unityObject is null || Method(pass, "get_colorAttachmentHandles", handles) is not { } getHandles
            || Method(handle, "get_rt", texture) is not { } getTexture
            || Static(unityObject, "op_Inequality", types.Boolean, unityObject, unityObject) is not { } notEqual) return null;

        var input = new ArgumentValue(0, pass, 64);
        var index = Local(0, types.Int32);
        var array = Local(1, handles);
        var renderTexture = Local(2, texture);
        var exists = Local(3, types.Boolean);
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var missing = new ConstantValue(0, unityObject, 64);
        var loop = entry + 4;
        var test = entry + 8;
        var next = entry + 12;
        var found = entry + 16;
        var fallback = entry + 20;
        ManagedCall LoadHandles(ulong at) => new(at, new(getHandles, input, [], false), array);
        ArrayElementValue Element(ManagedValue i) => new(array, i, handle, 64);
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedAssignment(entry, index, zero), LoadHandles(entry), new ManagedJump(entry, new(loop, []))]),
            new(loop, [Branch(loop, Cond.Slt, index, new ArrayLengthValue(array, types.Int32, 32), test, fallback)]),
            new(test, [new ManagedCall(test, new(getTexture, Element(index), [], false), renderTexture),
                new ManagedCall(test, new(notEqual, null, [renderTexture, missing], false), exists),
                new ManagedBranch(test, exists, new(found, []), new(next, []))]),
            new(next, [LoadHandles(next), new ManagedAssignment(next, index, new BinaryValue(BinOp.Add, index, one, types.Int32, 32)),
                new ManagedJump(next, new(loop, []))]),
            new(found, [LoadHandles(found), new ManagedReturn(found, Element(index))]),
            new(fallback, [new ManagedReturn(fallback, Element(zero))]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "NativeRenderPass.cs");
    }

    private MethodRecoveryResult? RestoreBloomSamplingHandles(MethodRef method, ulong entry)
    {
        if (method.DeclaringType is not DefinitionRef pass || method.Method is not { IsStatic: false, GenericParameters.Count: 0,
                Parameters: [{ Type: DefinitionRef descriptor }, var reallocate] }
            || descriptor.Definition.FullName != "UnityEngine.RenderTextureDescriptor" || !reallocate.Type.Equals(types.Boolean)
            || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !UrpCode(entry, 552, "97817426FF988A429C1EA30CF90DAE083F295F4C54C4C23F498AB45E9E6CB0EE")) return null;
        var @void = method.Method.ReturnType;
        var str = types.Corlib("System.String");
        var integer = types.Int32;
        var boolean = types.Boolean;
        if (Field(pass, "bloomSamplingHandles") is not { Field.Type: ArrayRef { IsVector: true, Element: DefinitionRef handle } handles } arrayField
            || handle.Definition.FullName != "UnityEngine.Rendering.RTHandle"
            || Field(pass, "bloomSamplingTargetHandleNames") is not { Field.Type: ArrayRef { IsVector: true } names } namesField || !names.Element.Equals(str)
            || UrpType("UnityEngine.FilterMode") is not { } filter || UrpType("UnityEngine.TextureWrapMode") is not { } wrap
            || UrpType("UnityEngine.Rendering.RTHandles") is not { } rtHandles
            || UrpType("UnityEngine.Rendering.Universal.RenderingUtils") is not { } renderingUtils
            || Method(descriptor, "get_width", integer) is not { } getWidth || Method(descriptor, "get_height", integer) is not { } getHeight
            || Method(descriptor, "set_width", @void, integer) is not { } setWidth || Method(descriptor, "set_height", @void, integer) is not { } setHeight
            || Method(descriptor, "set_depthBufferBits", @void, integer) is not { } setDepth
            || Static(rtHandles, "Alloc", handle, new ByRefRef(descriptor), filter, wrap, boolean, integer, types.Single, str) is not { } allocate
            || Static(renderingUtils, "ReAllocateIfNeeded", boolean, new ByRefRef(handle), new ByRefRef(descriptor), filter, wrap,
                boolean, integer, types.Single, str) is not { } resize
            || types.Corlib("System.NullReferenceException") is not DefinitionRef nullReference
            || Method(nullReference, ".ctor", @void) is not { } nullConstructor) return null;

        var receiver = new ArgumentValue(-1, pass, 64);
        var source = new ArgumentValue(0, descriptor, types.ValueWidth(descriptor));
        var resizeRequested = new ArgumentValue(1, boolean, 8);
        var array = Local(0, handles);
        var desc = Local(1, descriptor);
        var index = Local(2, integer);
        var width = Local(3, integer);
        var height = Local(4, integer);
        var name = Local(5, str);
        var created = Local(6, handle);
        var field = Read(receiver, arrayField);
        var missing = new ConstantValue(0, handles, 64);
        var initialize = entry + 4;
        var prepare = entry + 8;
        var loop = entry + 12;
        var checkHeight = entry + 16;
        var choose = entry + 20;
        var realloc = entry + 24;
        var alloc = entry + 28;
        var next = entry + 32;
        var done = entry + 36;
        ConstantValue I(int value) => new((ulong)value, integer, 32);
        ManagedJump Go(ulong at, ulong target) => new(at, new(target, []));
        ManagedCall Descriptor(ulong at, MethodRef call, LocalValue? result, params ManagedValue[] values) => new(at, new(call, new AddressValue(desc), values, false), result);
        ManagedValue Half(LocalValue size) => new BinaryValue(BinOp.AShr, size, I(1), integer, 32);
        var element = new ArrayElementValue(array, index, handle, 64);
        var lookup = new ArrayElementValue(Read(receiver, namesField), index, str, 64);
        ManagedValue[] allocationArguments = [new AddressValue(desc), new ConstantValue(1, filter, 32), new ConstantValue(1, wrap, 32),
            new ConstantValue(0, boolean, 8), I(1), new ConstantValue(0, types.Single, 32), name];
        ManagedBlock[] blocks =
        [
            new(entry, [Branch(entry, Cond.Eq, field, missing, initialize, prepare)]),
            new(initialize, [new ManagedStore(initialize, field, new NewArrayValue(handles, I(8))), Go(initialize, prepare)]),
            new(prepare, [new ManagedAssignment(prepare, desc, source), Descriptor(prepare, setDepth, null, I(0)),
                new ManagedAssignment(prepare, index, I(0)), Go(prepare, loop)]),
            new(loop, [Descriptor(loop, getWidth, width), Branch(loop, Cond.Slt, width, I(4), done, checkHeight)]),
            new(checkHeight, [Descriptor(checkHeight, getHeight, height), Branch(checkHeight, Cond.Slt, height, I(4), done, choose)]),
            new(choose, [Descriptor(choose, setWidth, null, Half(width)), Descriptor(choose, setHeight, null, Half(height)),
                new ManagedAssignment(choose, array, field), new ManagedBranch(choose, resizeRequested, new(realloc, []), new(alloc, []))]),
            new(realloc, [new ManagedCheck(realloc, new ComparisonValue(Cond.Eq, array, missing, boolean), nullConstructor),
                new ManagedAssignment(realloc, name, lookup), new ManagedCall(realloc, new(resize, null, [new AddressValue(element), .. allocationArguments], false), null),
                Go(realloc, next)]),
            new(alloc, [new ManagedAssignment(alloc, name, lookup), new ManagedCall(alloc, new(allocate, null, allocationArguments, false), created),
                new ManagedStore(alloc, element, created), Go(alloc, next)]),
            new(next, [new ManagedAssignment(next, index, new BinaryValue(BinOp.Add, index, I(1), integer, 32)),
                Branch(next, Cond.Slt, index, I(8), loop, done)]),
            new(done, [new ManagedReturn(done, null)]),
        ];
        return new(method, entry, new ManagedBody(method, blocks), [])
        {
            Rules = [new RuleTrace("native.verified", entry, RewriteStatus.Applied, "sekai-urp", "Bloom descriptor copy, bounded size loop, and RTHandle allocation", null)],
        };
    }

    private DefinitionRef? UrpType(string fullName) =>
        facts.Model.Types.SingleOrDefault(t => t.FullName == fullName) is { } type ? new(type, []) : null;

    private MethodRecoveryResult? RestoreProcessIntersections(MethodRef method, ulong entry)
    {
        var @void = types.Corlib("System.Void");
        var boolean = types.Boolean;
        if (method.DeclaringType is not DefinitionRef owner || method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters: [var topY] }
            || !topY.Type.Equals(types.Int64) || !method.Method.ReturnType.Equals(boolean)
            || !UrpCode(entry, 400, "4F831272D1DB33CAEE68A7A345B8D53DF4FF3FDA399F64C998C35AA5A701E6CD")
            || !UrpCode(0x44469bc, 40, "E6D2337E791FF321E71F261DB27FBB568145A79DF7653C8B955E68D5EAE645F7", registered: false)) return null;
        if (HierarchyField(owner, "m_ActiveEdges") is not { } activeEdges || Field(owner, "m_SortedEdges") is not { } sortedEdges
            || Field(owner, "m_IntersectList") is not { Field.Type: DefinitionRef list } intersections
            || Method(list, "get_Count", types.Int32) is not { } getCount || Method(list, "Clear", @void) is not { } clear
            || Method(owner, "BuildIntersectList", @void, types.Int64) is not { } build
            || Method(owner, "FixupIntersectionOrder", boolean) is not { } reorder
            || Method(owner, "ProcessIntersectList", @void) is not { } process
            || UrpType("UnityEngine.Rendering.Universal.ClipperException") is not { } exception
            || Method(exception, ".ctor", @void, types.Corlib("System.String")) is not { } constructor) return null;
        var self = new ArgumentValue(-1, owner, 64);
        var input = new ArgumentValue(0, types.Int64, 64);
        var count = Local(0, types.Int32);
        var ordered = Local(1, boolean);
        var caught = Local(2, types.Object);
        var body = entry + 4;
        var test = entry + 8;
        var fix = entry + 12;
        var apply = entry + 16;
        var catchEntry = entry + 20;
        var reset = entry + 24;
        var done = entry + 28;
        var failed = entry + 32;
        ConstantValue I(int value) => new((ulong)value, types.Int32, 32);
        ManagedJump Go(ulong at, ulong target) => new(at, new(target, []));
        ManagedStore ClearSorted(ulong at) => new(at, Read(self, sortedEdges), new ConstantValue(0, sortedEdges.Field.Type, 64));
        ManagedBlock[] blocks =
        [
            new(entry, [Branch(entry, Cond.Eq, Read(self, activeEdges), new ConstantValue(0, activeEdges.Field.Type, 64), done, body)]),
            new(body, [new ManagedCall(body, new(build, self, [input], false), null),
                new ManagedCall(body, new(getCount, Read(self, intersections), [], true), count),
                Branch(body, Cond.Eq, count, I(0), done, test)]),
            new(test, [Branch(test, Cond.Eq, count, I(1), apply, fix)]),
            new(fix, [new ManagedCall(fix, new(reorder, self, [], false), ordered),
                new ManagedBranch(fix, ordered, new(apply, []), new(failed, []))]),
            new(apply, [new ManagedCall(apply, new(process, self, [], false), null), Go(apply, reset)]),
            new(catchEntry, [ClearSorted(catchEntry), new ManagedCall(catchEntry, new(clear, Read(self, intersections), [], true), null),
                new ManagedThrow(catchEntry, new NewObjectValue(constructor, [new StringValue("ProcessIntersections error", types.Corlib("System.String"))]))]),
            new(reset, [ClearSorted(reset), Go(reset, done)]),
            new(done, [new ManagedReturn(done, new ConstantValue(1, boolean, 8))]),
            new(failed, [new ManagedReturn(failed, new ConstantValue(0, boolean, 8))]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks)
        {
            Regions = [new([body, test, fix, apply], catchEntry, [catchEntry], caught, ManagedRegionKind.Catch) { CatchType = types.Object }],
        }, "Clipper.cs");
    }

    private static FieldRef? HierarchyField(DefinitionRef owner, string name)
    {
        for (DefinitionRef? current = owner; current is not null; current = current.Definition.BaseType?.Substitute(current.Arguments, []) as DefinitionRef)
            if (Field(current, name) is { } field) return field;
        return null;
    }

    private MethodRecoveryResult? RestoreAtlasLayout(MethodRef method, ulong entry)
    {
        var integer = types.Int32;
        var @void = types.Corlib("System.Void");
        if (method.DeclaringType is not DefinitionRef pass || method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters.Count: 3 }
            || !method.Method.Parameters.All(p => p.Type.Equals(integer)) || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !UrpCode(entry, 724, "9ED83CA70036023BEB0A31B3B9FF69113B043B84D8280BB844628B26E346F90C")) return null;
        if (Field(pass, "m_UnusedAtlasSquareAreas") is not { Field.Type: DefinitionRef { Arguments: [DefinitionRef rect] } list } areas
            || rect.Definition.FullName != "UnityEngine.RectInt" || list.Definition.FullName != "System.Collections.Generic.List"
            || Field(pass, "m_SortedShadowResolutionRequests") is not { Field.Type: ArrayRef { IsVector: true, Element: DefinitionRef request } requests } sorted
            || Field(request, "requestedResolution") is not { } resolutionField || Field(request, "softShadow") is not { } soft
            || Field(request, "offsetX") is not { } offsetX || Field(request, "offsetY") is not { } offsetY
            || Field(request, "allocatedResolution") is not { } allocated
            || Method(rect, ".ctor", @void, integer, integer, integer, integer) is not { } rectConstructor
            || Method(rect, "get_x", integer) is not { } getX || Method(rect, "get_y", integer) is not { } getY
            || Method(rect, "get_width", integer) is not { } getWidth || Method(rect, "get_height", integer) is not { } getHeight
            || Method(list, "Clear", @void) is not { } clear || Method(list, "Add", @void, rect) is not { } add
            || Method(list, "get_Count", integer) is not { } getCount || Method(list, "get_Item", rect, integer) is not { } getArea
            || Method(list, "RemoveAt", @void, integer) is not { } remove || Method(list, "Insert", @void, integer, rect) is not { } insert)
            return null;

        var self = new ArgumentValue(-1, pass, 64);
        var atlas = new ArgumentValue(0, integer, 32);
        var total = new ArgumentValue(1, integer, 32);
        var estimate = new ArgumentValue(2, integer, 32);
        var scale = Local(0, integer);
        var work = Local(1, list);
        var slice = Local(2, integer);
        var areaIndex = Local(3, integer);
        var count = Local(4, integer);
        var resolution = Local(5, integer);
        var minimum = Local(6, integer);
        var area = Local(7, rect);
        var width = Local(8, integer);
        var height = Local(9, integer);
        var x = Local(10, integer);
        var y = Local(11, integer);
        var right = Local(12, integer);
        var bottom = Local(13, integer);
        var newX = Local(14, integer);
        var newY = Local(15, integer);
        var inserted = Local(16, integer);
        var remaining = Local(17, integer);
        var retry = entry + 4;
        var checkSlice = entry + 8;
        var prepare = entry + 12;
        var search = entry + 16;
        var candidate = entry + 20;
        var nextArea = entry + 24;
        var found = entry + 28;
        var split = entry + 32;
        var advanceX = entry + 36;
        var advanceY = entry + 40;
        var addArea = entry + 44;
        var nextSlice = entry + 48;
        var scaleDown = entry + 52;
        var done = entry + 56;
        ConstantValue I(int value) => new((ulong)value, integer, 32);
        ManagedValue Math(BinOp op, ManagedValue left, ManagedValue rightValue) => new BinaryValue(op, left, rightValue, integer, 32);
        ManagedAssignment Set(ulong at, LocalValue variable, ManagedValue value) => new(at, variable, value);
        ManagedJump Go(ulong at, ulong target) => new(at, new(target, []));
        ManagedCall On(ulong at, MethodRef call, ManagedValue receiver, LocalValue? output, params ManagedValue[] arguments) =>
            new(at, new(call, receiver, arguments, ManagedTypes.IsReferenceType(receiver.Type)), output);
        ManagedValue AreaList() => Read(self, areas);
        ManagedValue Rectangle(ManagedValue px, ManagedValue py, ManagedValue w, ManagedValue h) => new NewObjectValue(rectConstructor, [px, py, w, h]);
        var item = new ArrayElementValue(Read(self, sorted), slice, requests.Element, types.ValueWidth(request));
        ManagedValue ItemField(FieldRef field) => Read(new AddressValue(item), field);
        ManagedBlock[] blocks =
        [
            new(entry, [Set(entry, scale, estimate), Go(entry, retry)]),
            new(retry, [Set(retry, work, AreaList()), On(retry, clear, work, null), On(retry, add, work, null, Rectangle(I(0), I(0), atlas, atlas)),
                Set(retry, slice, I(0)), Go(retry, checkSlice)]),
            new(checkSlice, [Branch(checkSlice, Cond.Slt, slice, total, prepare, done)]),
            new(prepare, [Set(prepare, resolution, Math(BinOp.SDiv, ItemField(resolutionField), scale)),
                Set(prepare, minimum, new ChoiceValue(ItemField(soft), I(16), I(8), integer, 32)),
                Set(prepare, areaIndex, I(0)), Branch(prepare, Cond.Slt, resolution, minimum, done, search)]),
            new(search, [Set(search, work, AreaList()), On(search, getCount, work, count),
                Branch(search, Cond.Slt, areaIndex, count, candidate, scaleDown)]),
            new(candidate, [On(candidate, getArea, work, area, areaIndex), On(candidate, getWidth, new AddressValue(area), width),
                Branch(candidate, Cond.Sge, width, resolution, found, nextArea)]),
            new(nextArea, [Set(nextArea, areaIndex, Math(BinOp.Add, areaIndex, I(1))), Go(nextArea, search)]),
            new(found, [On(found, getX, new AddressValue(area), x), On(found, getY, new AddressValue(area), y),
                On(found, getHeight, new AddressValue(area), height),
                new ManagedStore(found, ItemField(offsetX), x), new ManagedStore(found, ItemField(offsetY), y),
                new ManagedStore(found, ItemField(allocated), resolution), On(found, remove, AreaList(), null, areaIndex),
                Set(found, remaining, Math(BinOp.Sub, Math(BinOp.Sub, total, slice), I(1))),
                Set(found, inserted, I(0)), Set(found, newX, x), Set(found, newY, y),
                Set(found, right, Math(BinOp.Add, x, width)), Set(found, bottom, Math(BinOp.Add, y, height)), Go(found, split)]),
            new(split, [Branch(split, Cond.Slt, inserted, remaining, advanceX, nextSlice)]),
            new(advanceX, [Set(advanceX, newX, Math(BinOp.Add, newX, resolution)),
                Branch(advanceX, Cond.Sle, Math(BinOp.Add, newX, resolution), right, addArea, advanceY)]),
            new(advanceY, [Set(advanceY, newX, x), Set(advanceY, newY, Math(BinOp.Add, newY, resolution)),
                Branch(advanceY, Cond.Sle, Math(BinOp.Add, newY, resolution), bottom, addArea, nextSlice)]),
            new(addArea, [On(addArea, insert, AreaList(), null, Math(BinOp.Add, areaIndex, inserted), Rectangle(newX, newY, resolution, resolution)),
                Set(addArea, inserted, Math(BinOp.Add, inserted, I(1))), Go(addArea, split)]),
            new(nextSlice, [Set(nextSlice, slice, Math(BinOp.Add, slice, I(1))), Go(nextSlice, checkSlice)]),
            new(scaleDown, [Set(scaleDown, scale, Math(BinOp.Shl, scale, I(1))), Go(scaleDown, retry)]),
            new(done, [new ManagedReturn(done, null)]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "AdditionalLightsShadowCasterPass.cs");
    }

    private MethodRecoveryResult? RestoreWalkDirtyRegions(MethodRef method, ulong entry)
    {
        if (method.DeclaringType is not DefinitionRef owner || method.Method is not { IsStatic: false, GenericParameters.Count: 0,
                Parameters: [{ Type: DefinitionRef region }] }
            || region.Definition.FullName != "UnityEngine.Rendering.Universal.LibTessDotNet.Tess.ActiveRegion"
            || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !UrpCode(entry, 560, "2C689D31462D3A46E0CBBBE586FE3CBF87543F493F10E66D39626B86BDA61960")) return null;
        var @void = method.Method.ReturnType;
        var boolean = types.Boolean;
        if (Field(region, "_eUp") is not { Field.Type: DefinitionRef edge } upperEdge
            || Field(region, "_dirty") is not { } dirty || Field(region, "_fixUpperEdge") is not { } fix
            || Field(edge, "_Sym") is not { } sym || Field(edge, "_Org") is not { Field.Type: DefinitionRef vertex } org
            || Field(owner, "_event") is not { } currentEvent || Field(owner, "_mesh") is not { Field.Type: DefinitionRef mesh } meshField
            || Method(owner, "RegionAbove", region, region) is not { } above || Method(owner, "RegionBelow", region, region) is not { } below
            || Method(owner, "CheckForLeftSplice", boolean, region) is not { } left || Method(owner, "CheckForRightSplice", boolean, region) is not { } right
            || Method(owner, "CheckForIntersect", boolean, region) is not { } intersect || Method(owner, "DeleteRegion", @void, region) is not { } deleteRegion
            || Method(mesh, "Delete", @void, edge) is not { } deleteEdge
            || UrpType("UnityEngine.Rendering.Universal.LibTessDotNet.Geom") is not { } geom
            || Static(geom, "AddWinding", @void, edge, edge) is not { } addWinding) return null;
        var self = new ArgumentValue(-1, owner, 64);
        var input = new ArgumentValue(0, region, 64);
        var up = Local(0, region);
        var lo = Local(1, region);
        var eUp = Local(2, edge);
        var eLo = Local(3, edge);
        var check = Local(4, boolean);
        var dstUp = Local(5, vertex);
        var dstLo = Local(6, vertex);
        var @event = Local(7, vertex);
        var missing = new ConstantValue(0, region, 64);
        var clean = new ConstantValue(0, boolean, 8);
        var loop = entry + 4;
        var lower = entry + 8;
        var checkUpper = entry + 12;
        var upper = entry + 16;
        var upperDirty = entry + 20;
        var takeEdges = entry + 24;
        var splice = entry + 28;
        var fixLower = entry + 32;
        var deleteLower = entry + 36;
        var fixUpper = entry + 40;
        var deleteUpper = entry + 44;
        var compareOrigins = entry + 48;
        var compareDestinations = entry + 52;
        var checkFixUp = entry + 56;
        var checkFixLo = entry + 60;
        var compareEventUp = entry + 64;
        var compareEventLo = entry + 68;
        var intersection = entry + 72;
        var spliceRight = entry + 76;
        var equalOrigins = entry + 80;
        var equalDestinations = entry + 84;
        var degenerate = entry + 88;
        var done = entry + 92;
        ManagedJump Go(ulong at, ulong target) => new(at, new(target, []));
        ManagedBranch If(ulong at, ManagedValue value, ulong yes, ulong no) => new(at, value, new(yes, []), new(no, []));
        ManagedCall On(ulong at, MethodRef call, ManagedValue? receiver, LocalValue? output, params ManagedValue[] arguments) =>
            new(at, new(call, receiver, arguments, false), output);
        ManagedCall RemoveEdge(ulong at, ManagedValue target) => new(at, new(deleteEdge, Read(self, meshField), [target], true), null);
        FieldValue Destination(ManagedValue value) => Read(Read(value, sym), org);
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedAssignment(entry, up, input), On(entry, below, self, lo, input), Go(entry, loop)]),
            new(loop, [If(loop, Read(lo, dirty), lower, checkUpper)]),
            new(lower, [new ManagedAssignment(lower, up, lo), On(lower, below, self, lo, lo), Go(lower, loop)]),
            new(checkUpper, [If(checkUpper, Read(up, dirty), takeEdges, upper)]),
            new(upper, [new ManagedAssignment(upper, lo, up), On(upper, above, self, up, up),
                Branch(upper, Cond.Eq, up, missing, done, upperDirty)]),
            new(upperDirty, [If(upperDirty, Read(up, dirty), takeEdges, done)]),
            new(takeEdges, [new ManagedAssignment(takeEdges, eUp, Read(up, upperEdge)), new ManagedStore(takeEdges, Read(up, dirty), clean),
                new ManagedAssignment(takeEdges, eLo, Read(lo, upperEdge)),
                Branch(takeEdges, Cond.Ne, Destination(eUp), Destination(eLo), splice, compareOrigins)]),
            new(splice, [On(splice, left, self, check, up), If(splice, check, fixLower, compareOrigins)]),
            new(fixLower, [If(fixLower, Read(lo, fix), deleteLower, fixUpper)]),
            new(deleteLower, [On(deleteLower, deleteRegion, self, null, lo), RemoveEdge(deleteLower, eLo),
                On(deleteLower, below, self, lo, up), new ManagedAssignment(deleteLower, eLo, Read(lo, upperEdge)), Go(deleteLower, compareOrigins)]),
            new(fixUpper, [If(fixUpper, Read(up, fix), deleteUpper, compareOrigins)]),
            new(deleteUpper, [On(deleteUpper, deleteRegion, self, null, up), RemoveEdge(deleteUpper, eUp),
                On(deleteUpper, above, self, up, lo), new ManagedAssignment(deleteUpper, eUp, Read(up, upperEdge)), Go(deleteUpper, compareOrigins)]),
            new(compareOrigins, [Branch(compareOrigins, Cond.Ne, Read(eUp, org), Read(eLo, org), compareDestinations, equalOrigins)]),
            new(compareDestinations, [new ManagedAssignment(compareDestinations, dstUp, Destination(eUp)), new ManagedAssignment(compareDestinations, dstLo, Destination(eLo)),
                Branch(compareDestinations, Cond.Ne, dstUp, dstLo, checkFixUp, spliceRight)]),
            new(checkFixUp, [If(checkFixUp, Read(up, fix), spliceRight, checkFixLo)]),
            new(checkFixLo, [If(checkFixLo, Read(lo, fix), spliceRight, compareEventUp)]),
            new(compareEventUp, [new ManagedAssignment(compareEventUp, @event, Read(self, currentEvent)),
                Branch(compareEventUp, Cond.Eq, dstUp, @event, intersection, compareEventLo)]),
            new(compareEventLo, [Branch(compareEventLo, Cond.Eq, dstLo, @event, intersection, spliceRight)]),
            new(intersection, [On(intersection, intersect, self, check, up), If(intersection, check, done, equalOrigins)]),
            new(spliceRight, [On(spliceRight, right, self, null, up), Go(spliceRight, equalOrigins)]),
            new(equalOrigins, [Branch(equalOrigins, Cond.Eq, Read(eUp, org), Read(eLo, org), equalDestinations, loop)]),
            new(equalDestinations, [Branch(equalDestinations, Cond.Eq, Destination(eUp), Destination(eLo), degenerate, loop)]),
            new(degenerate, [On(degenerate, addWinding, null, null, eLo, eUp), On(degenerate, deleteRegion, self, null, up),
                RemoveEdge(degenerate, eUp), On(degenerate, above, self, up, lo), Go(degenerate, loop)]),
            new(done, [new ManagedReturn(done, null)]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "Sweep.cs");
    }

    private MethodRecoveryResult? RestoreBuddyAllocation(MethodRef method, ulong entry)
    {
        if (method.DeclaringType is not DefinitionRef owner || method.Method is not { IsStatic: false, GenericParameters.Count: 0,
                Parameters: [var requested, { Type: ByRefRef { Element: DefinitionRef allocation } }] }
            || !requested.Type.Equals(types.Int32) || !method.Method.ReturnType.Equals(types.Boolean)
            || allocation.Definition.FullName != "UnityEngine.Rendering.Universal.BuddyAllocation"
            || !UrpCode(entry, 548, "4EBF86BDABCF41083B53A16BD85DFE978DF811F30725BD490DA14D23DC13B198")) return null;
        var integer = types.Int32;
        var unsigned = types.Integer(64, false);
        var boolean = types.Boolean;
        var @void = types.Corlib("System.Void");
        if (UrpType("Unity.Collections.NativeArray") is not { } native
            || UrpType("UnityEngine.Rendering.Universal.BuddyAllocator.Header") is not { } headerType
            || UrpType("Unity.Mathematics.math") is not { } math || Static(math, "tzcnt", integer, unsigned) is not { } trailingZeros
            || Field(headerType, "branchingOrder") is not { } orderField
            || Field(allocation, "level") is not { } levelField || Field(allocation, "index") is not { } indexField) return null;
        var intArray = new DefinitionRef(native.Definition, [integer]);
        var ulongArray = new DefinitionRef(native.Definition, [unsigned]);
        var byrefHeader = new ByRefRef(headerType);
        if (Method(owner, "get_header", byrefHeader) is not { } getHeader || Method(owner, "get_freeMaskCounts", intArray) is not { } getCounts
            || Method(owner, "FreeMaskIndices", intArray, integer) is not { } getIndices || Method(owner, "FreeMasks", ulongArray, integer) is not { } getMasks
            || Method(intArray, "get_Item", integer, integer) is not { } getInt || Method(intArray, "set_Item", @void, integer, integer) is not { } setInt
            || Method(ulongArray, "get_Item", unsigned, integer) is not { } getBits || Method(ulongArray, "set_Item", @void, integer, unsigned) is not { } setBits)
            return null;

        var self = new ArgumentValue(-1, new ByRefRef(owner), 64);
        var wanted = new ArgumentValue(0, integer, 32);
        var output = new ArgumentValue(1, new ByRefRef(allocation), 64);
        var counts = Local(0, intArray);
        var level = Local(1, integer);
        var count = Local(2, integer);
        var indices = Local(3, intArray);
        var masks = Local(4, ulongArray);
        var maskIndex = Local(5, integer);
        var freeMask = Local(6, unsigned);
        var bitIndex = Local(7, integer);
        var dataIndex = Local(8, integer);
        var header = Local(9, byrefHeader);
        var order = Local(10, integer);
        var search = entry + 4;
        var hasFree = entry + 8;
        var previous = entry + 12;
        var split = entry + 16;
        var retain = entry + 20;
        var data = entry + 24;
        var iterate = entry + 28;
        var ascend = entry + 32;
        var register = entry + 36;
        var mark = entry + 40;
        var success = entry + 44;
        var failure = entry + 48;
        ConstantValue I(int value) => new((ulong)value, integer, 32);
        ConstantValue U(ulong value) => new(value, unsigned, 64);
        ManagedValue Int(BinOp op, ManagedValue left, ManagedValue right) => new BinaryValue(op, left, right, integer, 32);
        ManagedValue Bits(BinOp op, ManagedValue left, ManagedValue right) => new BinaryValue(op, left, right, unsigned, 64);
        ManagedAssignment Assign(ulong at, LocalValue variable, ManagedValue value) => new(at, variable, value);
        ManagedJump Go(ulong at, ulong target) => new(at, new(target, []));
        ManagedCall On(ulong at, MethodRef call, ManagedValue? receiver, LocalValue? result, params ManagedValue[] arguments) =>
            new(at, new(call, receiver, arguments, false), result);
        ManagedCall At(ulong at, MethodRef call, LocalValue array, LocalValue? result, params ManagedValue[] arguments) =>
            On(at, call, new AddressValue(array), result, arguments);
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedStore(entry, new IndirectValue(output, allocation, types.ValueWidth(allocation)), new ComposedValue(allocation, [])),
                Assign(entry, level, wanted), On(entry, getCounts, self, counts), Go(entry, search)]),
            new(search, [Branch(search, Cond.Slt, level, I(0), failure, hasFree)]),
            new(hasFree, [At(hasFree, getInt, counts, count, level), Branch(hasFree, Cond.Sgt, count, I(0), split, previous)]),
            new(previous, [Assign(previous, level, Int(BinOp.Sub, level, I(1))), Go(previous, search)]),
            new(split, [On(split, getIndices, self, indices, level), At(split, getInt, counts, count, level),
                Assign(split, count, Int(BinOp.Sub, count, I(1))), At(split, setInt, counts, null, level, count),
                At(split, getInt, indices, maskIndex, count), On(split, getMasks, self, masks, level),
                At(split, getBits, masks, freeMask, maskIndex), On(split, trailingZeros, null, bitIndex, freeMask),
                Assign(split, freeMask, Bits(BinOp.Xor, freeMask, Bits(BinOp.Shl, U(1), bitIndex))),
                At(split, setBits, masks, null, maskIndex, freeMask), Branch(split, Cond.Ne, freeMask, U(0), retain, data)]),
            new(retain, [At(retain, getInt, counts, count, level), At(retain, setInt, counts, null, level, Int(BinOp.Add, count, I(1))),
                At(retain, setInt, indices, null, count, maskIndex), Go(retain, data)]),
            new(data, [Assign(data, dataIndex, Int(BinOp.Add, Int(BinOp.Shl, maskIndex, I(6)), bitIndex)), Go(data, iterate)]),
            new(iterate, [Branch(iterate, Cond.Slt, level, wanted, ascend, success)]),
            new(ascend, [Assign(ascend, level, Int(BinOp.Add, level, I(1))), On(ascend, getHeader, self, header),
                Assign(ascend, dataIndex, Int(BinOp.Shl, dataIndex, Read(header, orderField))),
                Assign(ascend, maskIndex, Int(BinOp.AShr, dataIndex, I(6))), On(ascend, getMasks, self, masks, level),
                At(ascend, getBits, masks, freeMask, maskIndex), Branch(ascend, Cond.Eq, freeMask, U(0), register, mark)]),
            new(register, [On(register, getIndices, self, indices, level), At(register, getInt, counts, count, level),
                At(register, setInt, counts, null, level, Int(BinOp.Add, count, I(1))), At(register, setInt, indices, null, count, maskIndex),
                Go(register, mark)]),
            new(mark, [Assign(mark, bitIndex, Int(BinOp.And, dataIndex, I(63))), On(mark, getHeader, self, header),
                Assign(mark, order, Read(header, orderField)), Assign(mark, freeMask, Bits(BinOp.Or, freeMask,
                    Bits(BinOp.Shl, Bits(BinOp.Sub, Bits(BinOp.Shl, U(1), Int(BinOp.Shl, I(1), order)), U(2)), bitIndex))),
                At(mark, setBits, masks, null, maskIndex, freeMask), Go(mark, iterate)]),
            new(success, [new ManagedStore(success, Read(output, levelField), level), new ManagedStore(success, Read(output, indexField), dataIndex),
                new ManagedReturn(success, new ConstantValue(1, boolean, 8))]),
            new(failure, [new ManagedReturn(failure, new ConstantValue(0, boolean, 8))]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "BuddyAllocator.cs");
    }

    private MethodRecoveryResult? RestoreTopLeftRegion(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters: [var parameter], ReturnType: DefinitionRef region }
            || !parameter.Type.Equals(region) || region.Definition.FullName != "UnityEngine.Rendering.Universal.LibTessDotNet.Tess.ActiveRegion"
            || !UrpCode(entry, 176, "A9B3E82C47400F1E6E9FCC410CBE48D4F7243F16EE14D665F031A49CA590A91D")) return null;
        var owner = (DefinitionRef)method.DeclaringType;
        if (Field(region, "_eUp") is not { Field.Type: DefinitionRef edge } upper
            || Field(region, "_fixUpperEdge") is not { } fix || !fix.Field.Type.Equals(types.Boolean)
            || Field(edge, "_Org") is not { } origin || Field(edge, "_Sym") is not { } symmetric || Field(edge, "_Lnext") is not { } next
            || Field(owner, "_mesh") is not { Field.Type: DefinitionRef mesh } meshField
            || Method(owner, "RegionAbove", region, region) is not { } above || Method(owner, "RegionBelow", region, region) is not { } below
            || Method(mesh, "Connect", edge, edge, edge) is not { } connect
            || Method(owner, "FixUpperEdge", types.Corlib("System.Void"), region, edge) is not { } fixUpper) return null;

        var self = new ArgumentValue(-1, owner, 64);
        var input = new ArgumentValue(0, region, 64);
        var current = Local(0, region);
        var firstOrigin = Local(1, origin.Field.Type);
        var meshLocal = Local(2, mesh);
        var lower = Local(3, region);
        var replacement = Local(4, edge);
        var loop = entry + 4;
        var test = entry + 8;
        var replace = entry + 12;
        var done = entry + 16;
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedAssignment(entry, firstOrigin, Read(Read(input, upper), origin)),
                new ManagedAssignment(entry, current, input), new ManagedJump(entry, new(loop, []))]),
            new(loop, [new ManagedCall(loop, new(above, self, [current], false), current),
                Branch(loop, Cond.Eq, Read(Read(current, upper), origin), firstOrigin, loop, test)]),
            new(test, [new ManagedBranch(test, Read(current, fix), new(replace, []), new(done, []))]),
            new(replace, [new ManagedAssignment(replace, meshLocal, Read(self, meshField)),
                new ManagedCall(replace, new(below, self, [current], false), lower),
                new ManagedCall(replace, new(connect, meshLocal, [Read(Read(lower, upper), symmetric), Read(Read(current, upper), next)], false), replacement),
                new ManagedCall(replace, new(fixUpper, self, [current, replacement], false), null),
                new ManagedCall(replace, new(above, self, [current], false), current), new ManagedJump(replace, new(done, []))]),
            new(done, [new ManagedReturn(done, current)]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "Sweep.cs");
    }

    private MethodRecoveryResult? RestoreVertexReset(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters.Count: 0 } || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || !UrpCode(entry, 172, "D888A546F146230EFB52FC94ACCFB441C59C265878AF79AF0371EF5B193D0872")
            || method.DeclaringType is not DefinitionRef vertex || Field(vertex, "_coords") is not { Field.Type: DefinitionRef coords } coordinates
            || coords.Definition.Fields.SingleOrDefault(f => f.Name == "Zero" && f.IsStatic && f.Type.Equals(coords)) is not { } zero) return null;
        var names = new[] { "_next", "_prev", "_anEdge", "_data", "_pqHandle", "_n", "_s", "_t" };
        var fields = names.Select(name => Field(vertex, name)).ToArray();
        if (fields.Any(f => f is null) || !fields.Take(4).All(f => ManagedTypes.IsReferenceType(f!.Field.Type))) return null;
        var receiver = new ArgumentValue(-1, vertex, 64);
        var storedCoords = Local(0, coords);
        var statements = new List<ManagedStatement>();
        for (var i = 0; i < 6; i++)
        {
            if (i == 3) statements.Add(new ManagedAssignment(entry, storedCoords,
                new StaticFieldValue(new FieldRef(zero, coords), coords, types.ValueWidth(coords))));
            var field = fields[i]!;
            var target = Read(receiver, field);
            ManagedValue value = types.Kind(target.Type) == TypeKind.Struct ? new ComposedValue(target.Type, [])
                : new ConstantValue(0, target.Type, target.Width);
            statements.Add(new ManagedStore(entry, target, value));
        }
        statements.Add(new ManagedStore(entry, Read(receiver, coordinates), storedCoords));
        foreach (var field in fields.Skip(6).OfType<FieldRef>())
        {
            var target = Read(receiver, field);
            statements.Add(new ManagedStore(entry, target, new ConstantValue(0, target.Type, target.Width)));
        }
        statements.Add(new ManagedReturn(entry, null));
        return UrpPackageResult(method, entry, new ManagedBody(method, [new(entry, statements)]), "MeshUtils.cs");
    }

    private MethodRecoveryResult? RestoreShadowColor(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters: [{ Type: DefinitionRef vertex }], ReturnType: DefinitionRef color }
            || !UrpCode(entry, 276, "9A217E2EE75D82FF9598AE385474459DBD0E27CFBA410218A88F42DB0CBF57CD")
            || vertex.Definition.FullName != "UnityEngine.Rendering.Universal.LibTessDotNet.ContourVertex"
            || color.Definition.FullName != "UnityEngine.Color" || Field(vertex, "Data") is not { } data || !data.Field.Type.Equals(types.Object)
            || Method(color, ".ctor", types.Corlib("System.Void"), types.Single, types.Single, types.Single, types.Single) is not { } constructor) return null;
        var channels = new[] { "r", "g", "b", "a" }.Select(name => Field(color, name)).ToArray();
        if (channels.Any(c => c is null || !c.Field.Type.Equals(types.Single))) return null;
        var argument = new ArgumentValue(0, vertex, types.ValueWidth(vertex));
        var boxed = Read(argument, data);
        var values = channels.OfType<FieldRef>().Select(channel => (ManagedValue)Read(new UnboxValue(boxed, color, types.ValueWidth(color)), channel)).ToArray();
        var body = new ManagedBody(method, [new(entry, [new ManagedReturn(entry, new NewObjectValue(constructor, values))])]);
        return UrpPackageResult(method, entry, body, "ShadowUtility.cs");
    }

    private MethodRecoveryResult? RestoreSubPassCount(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: true, GenericParameters.Count: 0, Parameters: [{ Type: DefinitionRef pass }] }
            || pass.Definition.FullName != "UnityEngine.Rendering.Universal.ScriptableRenderPass" || !method.Method.ReturnType.Equals(types.UInt32)
            || !UrpCode(entry, 396, "9D8AD6FD0446435231FEB16BFECC319620CA83A98EF6A9E15C99AC3D2743ADC2")
            || Field(pass, "m_ColorAttachmentIndices") is not { Field.Type: DefinitionRef array } indices
            || array is not { Definition.FullName: "Unity.Collections.NativeArray", Arguments: [var element] } || !element.Equals(types.Int32)
            || array.Definition.Methods.SingleOrDefault(m => m.Name == "GetEnumerator" && !m.IsStatic && m.Parameters.Count == 0
                && m.ReturnType.Substitute(array.Arguments, []) is DefinitionRef { Definition.Name: "Enumerator" }) is not { } get
            || get.ReturnType.Substitute(array.Arguments, []) is not DefinitionRef enumerator
            || Method(enumerator, "MoveNext", types.Boolean) is not { } move
            || Method(enumerator, "get_Current", types.Int32) is not { } current
            || Method(enumerator, "Dispose", types.Corlib("System.Void")) is not { } dispose) return null;

        var collection = new ArgumentValue(0, pass, 64);
        var iterator = Local(0, enumerator);
        var count = Local(1, types.UInt32);
        var item = Local(2, types.Int32);
        var hasItem = Local(3, types.Boolean);
        var loop = entry + 4;
        var test = entry + 8;
        var countItem = entry + 12;
        var cleanup = entry + 16;
        var done = entry + 20;
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedAssignment(entry, count, new ConstantValue(0, types.UInt32, 32)),
                new ManagedCall(entry, new(new MethodRef(get, array.Arguments, []), new AddressValue(Read(collection, indices)), [], false), iterator),
                new ManagedJump(entry, new(loop, []))]),
            new(loop, [new ManagedCall(loop, new(move, new AddressValue(iterator), [], false), hasItem),
                new ManagedBranch(loop, hasItem, new(test, []), new(done, []))]),
            new(test, [new ManagedCall(test, new(current, new AddressValue(iterator), [], false), item),
                Branch(test, Cond.Sge, item, new ConstantValue(0, types.Int32, 32), countItem, loop)]),
            new(countItem, [new ManagedAssignment(countItem, count, new BinaryValue(BinOp.Add, count, new ConstantValue(1, types.UInt32, 32), types.UInt32, 32)),
                new ManagedJump(countItem, new(loop, []))]),
            new(cleanup, [new ManagedCall(cleanup, new(dispose, new AddressValue(iterator), [], false), null), new ManagedEndFault(cleanup)]),
            new(done, [new ManagedReturn(done, count)]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks)
        {
            Regions = [new([loop, test, countItem], cleanup, [cleanup], null, ManagedRegionKind.Finally)],
        }, "NativeRenderPass.cs");
    }

    private MethodRecoveryResult? RestoreNextSibling(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters.Count: 0 }
            || method.DeclaringType is not DefinitionRef node || !method.Method.ReturnType.Equals(node)
            || !UrpCode(entry, 160, "122B5295E2CD00596AEA86E100F3585CB738D0E3896E9B41915619274690AF24")
            || Field(node, "m_Parent") is not { } parent || !parent.Field.Type.Equals(node)
            || Field(node, "m_Index") is not { } index || !index.Field.Type.Equals(types.Int32)
            || Field(node, "m_Childs") is not { Field.Type: DefinitionRef list } children
            || list is not { Definition.FullName: "System.Collections.Generic.List", Arguments: [var element] } || !element.Equals(node)
            || Method(list, "get_Count", types.Int32) is not { } count || Method(list, "get_Item", node, types.Int32) is not { } get) return null;

        var self = new ArgumentValue(-1, node, 64);
        var current = Local(0, node);
        var currentParent = Local(1, node);
        var siblings = Local(2, list);
        var siblingCount = Local(3, types.Int32);
        var sibling = Local(4, node);
        var missing = new ConstantValue(0, node, 64);
        var one = new ConstantValue(1, types.Int32, 32);
        var loop = entry + 4;
        var test = entry + 8;
        var climb = entry + 12;
        var next = entry + 16;
        var end = entry + 20;
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedAssignment(entry, current, self), new ManagedJump(entry, new(loop, []))]),
            new(loop, [new ManagedAssignment(loop, currentParent, Read(current, parent)), Branch(loop, Cond.Eq, currentParent, missing, end, test)]),
            new(test, [new ManagedAssignment(test, siblings, Read(currentParent, children)), new ManagedCall(test, new(count, siblings, [], true), siblingCount),
                Branch(test, Cond.Eq, Read(current, index), new BinaryValue(BinOp.Sub, siblingCount, one, types.Int32, 32), climb, next)]),
            new(climb, [new ManagedAssignment(climb, current, currentParent), new ManagedJump(climb, new(loop, []))]),
            new(next, [new ManagedCall(next, new(get, siblings, [new BinaryValue(BinOp.Add, Read(current, index), one, types.Int32, 32)], true), sibling),
                new ManagedReturn(next, sibling)]),
            new(end, [new ManagedReturn(end, missing)]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "Clipper.cs");
    }

    private MethodRecoveryResult? RestoreFixed2Item(MethodRef method, ulong entry)
    {
        var write = method.Method.Name == "set_Item";
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 0 }
            || method.DeclaringType is not DefinitionRef { Arguments: [GenericParameterRef { NativeScalar: { } witness } element] } owner
            || !witness.Equals(types.Single) || method.Method.Parameters.Count != (write ? 2 : 1)
            || !method.Method.Parameters[0].Type.Equals(types.Int32)
            || !UrpCode(entry, 8, write ? "DD236A28DE4FC8375C3F1BF4E9571E910681F9B36F8B7D7572D0206A30D27D72"
                : "E6B2F8BAECABFA1C3026CEE830E37A116B60F754AF651BE87607E30B6A665FC5")
            || Field(owner, "item1") is not { } first || !first.Field.Type.Substitute(owner.Arguments, []).Equals(element)
            || Field(owner, "item2") is not { } second || !second.Field.Type.Substitute(owner.Arguments, []).Equals(element)
            || (write ? !ManagedTypes.IsVoid(method.Method.ReturnType) || !method.Concrete(method.Method.Parameters[1].Type).Equals(element)
                : !method.Concrete(method.Method.ReturnType).Equals(element))) return null;

        var self = new ArgumentValue(-1, new ByRefRef(owner), 64);
        var index = new ArgumentValue(0, types.Int32, 32);
        var width = types.ValueWidth(element);
        var start = new ConversionValue(ConvKind.Bitcast, new AddressValue(Read(self, first)), new PointerRef(element), 64);
        var delta = new BinaryValue(BinOp.Mul, new ConversionValue(ConvKind.SignExtend, index, types.Int64, 64),
            new ConversionValue(ConvKind.ZeroExtend, new SizeOfValue(element, types.Int32), types.Int64, 64), types.Int64, 64);
        var item = new IndirectValue(new PointerOffsetValue(start, delta), element, width);
        ManagedStatement[] statements = write ? [new ManagedStore(entry, item, new ArgumentValue(1, element, width)), new ManagedReturn(entry, null)]
            : [new ManagedReturn(entry, item)];
        return UrpPackageResult(method, entry, new ManagedBody(method, [new(entry, statements)]), "Fixed2.cs");
    }

    private MethodRecoveryResult? RestoreShaderBitsString(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 0, Parameters.Count: 0 }
            || !method.Method.ReturnType.Equals(types.String) || method.DeclaringType is not DefinitionRef owner
            || !UrpCode(entry, 364, "EAFF258E9A04CC43D47C6CE243828E22DFED20ED29CD722D3DA78DE95FAF3AC3")
            || Method(owner, "get_bitCapacity", types.Int32) is not { } capacity
            || Method(owner, "get_Item", types.Boolean, types.Int32) is not { } get
            || types.CorlibMethod("System.Math", "Min", types.Int32, types.Int32) is not { } min
            || types.Corlib("System.Text.Encoding") is not DefinitionRef encoding
            || types.CorlibMethod("System.Text.Encoding", "get_UTF8") is not { } utf8
            || types.String is not DefinitionRef text) return null;

        var unsigned = types.Corlib("System.Byte");
        var signed = new PointerRef(types.Corlib("System.SByte"));
        if (Method(text, ".ctor", types.Corlib("System.Void"), signed, types.Int32, types.Int32, encoding) is not { } construct) return null;
        var self = new ArgumentValue(-1, new ByRefRef(owner), 64);
        var length = Local(0, types.Int32);
        var bytes = Local(1, new PointerRef(unsigned));
        var index = Local(2, types.Int32);
        var set = Local(3, types.Boolean);
        var codec = Local(4, encoding);
        var zero = new ConstantValue(0, types.Int32, 32);
        var one = new ConstantValue(1, types.Int32, 32);
        var loop = entry + 4;
        var item = entry + 8;
        var done = entry + 12;
        var offset = new ConversionValue(ConvKind.SignExtend, index, types.Int64, 64);
        var place = new IndirectValue(new PointerOffsetValue(bytes, offset), unsigned, 8);
        var digit = new ChoiceValue(set, new ConstantValue('1', unsigned, 8), new ConstantValue('0', unsigned, 8), unsigned, 8);
        ManagedBlock[] blocks =
        [
            new(entry, [new ManagedCall(entry, new(capacity, self, [], false), length),
                new ManagedCall(entry, new(min, null, [length, new ConstantValue(4096, types.Int32, 32)], false), length),
                new ManagedAssignment(entry, bytes, new StackBytesValue(unsigned, length)),
                new ManagedAssignment(entry, index, zero), new ManagedJump(entry, new(loop, []))]),
            new(loop, [Branch(loop, Cond.Slt, index, length, item, done)]),
            new(item, [new ManagedCall(item, new(get, self, [index], false), set), new ManagedStore(item, place, digit),
                new ManagedAssignment(item, index, new BinaryValue(BinOp.Add, index, one, types.Int32, 32)), new ManagedJump(item, new(loop, []))]),
            new(done, [new ManagedCall(done, new(utf8, null, [], false), codec),
                new ManagedReturn(done, new NewObjectValue(construct, [new ConversionValue(ConvKind.Bitcast, bytes, signed, 64), zero, length, codec]))]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "LightCookieManager.cs");
    }

    private MethodRecoveryResult UrpPackageResult(MethodRef method, ulong entry, ManagedBody body, string file) => new(method, entry, body, [])
    {
        ReferenceSource = UrpSource,
        Rules = [new RuleTrace("source.reference", entry, RewriteStatus.Applied, "urp-14.0.12", file, null)],
    };

    private MethodRecoveryResult? RestoreCameraDescriptor(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: true, GenericParameters.Count: 0, ReturnType: DefinitionRef descriptor,
                Parameters: [{ Type: DefinitionRef camera }, var scale, var hdr, var precision, var samples, var alpha, var opaque] }
            || descriptor.Definition.FullName != "UnityEngine.RenderTextureDescriptor" || camera.Definition.FullName != "UnityEngine.Camera"
            || !scale.Type.Equals(types.Single) || !hdr.Type.Equals(types.Boolean) || !samples.Type.Equals(types.Int32)
            || !alpha.Type.Equals(types.Boolean) || !opaque.Type.Equals(types.Boolean)
            || precision.Type is not DefinitionRef { Definition.FullName: "UnityEngine.Rendering.Universal.HDRColorBufferPrecision" }
            || !UrpCode(entry, 740, "8D1371823D056BAAB8712C6501EB572ECBE0569AF2D83FC971254819DE6529CB")) return null;

        DefinitionRef? UnityType(string name) => facts.Model.Types.SingleOrDefault(t => t.Assembly == "UnityEngine.CoreModule.dll"
            && t.FullName == "UnityEngine." + name) is { } t ? new(t, []) : null;
        if (UnityType("RenderTexture") is not { } texture || UnityType("Object") is not { } unityObject
            || UnityType("CameraType") is not { } cameraType || UnityType("ColorSpace") is not { } colorSpace
            || UnityType("Experimental.Rendering.GraphicsFormat") is not { } format || UnityType("Experimental.Rendering.DefaultFormat") is not { } defaultFormat
            || UnityType("QualitySettings") is not { } quality || UnityType("SystemInfo") is not { } system) return null;

        var @void = types.Corlib("System.Void");
        var boolean = types.Boolean;
        var integer = types.Int32;
        if (Method(camera, "get_pixelWidth", integer) is not { } width || Method(camera, "get_pixelHeight", integer) is not { } height
            || Method(camera, "get_targetTexture", texture) is not { } target || Method(camera, "get_cameraType", cameraType) is not { } kind
            || Method(camera, "get_allowDynamicResolution", boolean) is not { } dynamic
            || Static(unityObject, "op_Equality", boolean, unityObject, unityObject) is not { } equal
            || Method(texture, "get_descriptor", descriptor) is not { } getDescriptor
            || Method(descriptor, ".ctor", @void, integer, integer) is not { } ctor
            || Method(descriptor, "set_width", @void, integer) is not { } setWidth || Method(descriptor, "set_height", @void, integer) is not { } setHeight
            || Method(descriptor, "set_msaaSamples", @void, integer) is not { } setSamples
            || Method(descriptor, "set_depthBufferBits", @void, integer) is not { } setDepth
            || Method(descriptor, "set_graphicsFormat", @void, format) is not { } setFormat
            || Method(descriptor, "set_sRGB", @void, boolean) is not { } setSrgb
            || Method(descriptor, "set_enableRandomWrite", @void, boolean) is not { } setRandomWrite
            || Method(descriptor, "set_bindMS", @void, boolean) is not { } setBindMs
            || Method(descriptor, "set_useDynamicScale", @void, boolean) is not { } setDynamic
            || Static((DefinitionRef)method.DeclaringType, "MakeRenderTextureGraphicsFormat", format, boolean, precision.Type, boolean) is not { } makeFormat
            || Static(quality, "get_activeColorSpace", colorSpace) is not { } activeColor
            || Static(system, "GetGraphicsFormat", format, defaultFormat) is not { } getFormat
            || Static(system, "GetRenderTextureSupportedMSAASampleCount", integer, descriptor) is not { } supported
            || Static(system, "get_supportsStoreAndResolveAction", boolean) is not { } resolve) return null;

        var cam = new ArgumentValue(0, camera, 64);
        var factor = new ArgumentValue(1, scale.Type, 32);
        var useHdr = new ArgumentValue(2, boolean, 8);
        var hdrPrecision = new ArgumentValue(3, precision.Type, 32);
        var msaa = new ArgumentValue(4, integer, 32);
        var needsAlpha = new ArgumentValue(5, boolean, 8);
        var originalWidth = Local(0, integer);
        var originalHeight = Local(1, integer);
        var scaledWidth = Local(2, integer);
        var scaledHeight = Local(3, integer);
        var renderTarget = Local(4, texture);
        var missing = Local(5, boolean);
        var desc = Local(6, descriptor);
        var pixelsWidth = Local(7, integer);
        var pixelsHeight = Local(8, integer);
        var graphicsFormat = Local(9, format);
        var color = Local(10, colorSpace);
        var cameraKind = Local(11, cameraType);
        var dynamicScale = Local(12, boolean);
        var sampleCount = Local(13, integer);
        var canResolve = Local(14, boolean);
        var product = Local(15, types.Single);
        var productY = Local(16, types.Single);
        var one = new ConstantValue(1, integer, 32);
        var off = new ConstantValue(0, boolean, 8);
        var newTarget = entry + 4;
        var reuseTarget = entry + 8;
        var testHdr = entry + 12;
        var formatLdr = entry + 16;
        var finalize = entry + 20;
        var forceOne = entry + 24;
        var done = entry + 28;

        ManagedCall Call(ulong at, MethodRef member, ManagedValue? receiver, LocalValue? result, params ManagedValue[] args) =>
            new(at, new(member, receiver, args, receiver?.Type.Equals(camera) == true || receiver?.Type.Equals(texture) == true), result);
        ManagedCall Set(ulong at, MethodRef setter, ManagedValue value) => Call(at, setter, new AddressValue(desc), null, value);
        ManagedJump Go(ulong at, ulong next) => new(at, new(next, []));
        ManagedValue Scale(ManagedValue size) => new BinaryValue(BinOp.FMul, new ConversionValue(ConvKind.SIntToFloat, size, types.Single, 32), factor, types.Single, 32);
        ManagedValue PixelCount(ManagedValue value) => new ChoiceValue(
            new ComparisonValue(Cond.FEq, value, new ConstantValue(0x7f800000, types.Single, 32), boolean),
            new ConstantValue(0x80000000, integer, 32), new ConversionValue(ConvKind.FloatToSInt, value, integer, 32), integer, 32);
        ManagedValue AtLeastOne(ManagedValue value) => new ChoiceValue(new ComparisonValue(Cond.Sgt, value, one, boolean), value, one, integer, 32);

        ManagedBlock[] blocks =
        [
            new(entry, [Call(entry, width, cam, originalWidth), new ManagedAssignment(entry, product, Scale(originalWidth)),
                new ManagedAssignment(entry, scaledWidth, PixelCount(product)), Call(entry, height, cam, originalHeight),
                new ManagedAssignment(entry, productY, Scale(originalHeight)), new ManagedAssignment(entry, scaledHeight, PixelCount(productY)),
                Call(entry, target, cam, renderTarget), Call(entry, equal, null, missing, renderTarget, new ConstantValue(0, unityObject, 64)),
                new ManagedBranch(entry, missing, new(newTarget, []), new(reuseTarget, []))]),
            new(newTarget, [Call(newTarget, width, cam, pixelsWidth), Call(newTarget, height, cam, pixelsHeight),
                new ManagedAssignment(newTarget, desc, new NewObjectValue(ctor, [pixelsWidth, pixelsHeight])),
                Set(newTarget, setWidth, scaledWidth), Set(newTarget, setHeight, scaledHeight), Call(newTarget, makeFormat, null, graphicsFormat, useHdr, hdrPrecision, needsAlpha),
                Set(newTarget, setFormat, graphicsFormat), Set(newTarget, setDepth, new ConstantValue(32, integer, 32)), Set(newTarget, setSamples, msaa),
                Call(newTarget, activeColor, null, color), Set(newTarget, setSrgb, new ComparisonValue(Cond.Eq, color, new ConstantValue(1, colorSpace, 32), boolean)), Go(newTarget, finalize)]),
            new(reuseTarget, [Call(reuseTarget, target, cam, renderTarget), Call(reuseTarget, getDescriptor, renderTarget, desc),
                Set(reuseTarget, setWidth, scaledWidth), Set(reuseTarget, setHeight, scaledHeight), Set(reuseTarget, setSamples, msaa),
                Call(reuseTarget, kind, cam, cameraKind), Branch(reuseTarget, Cond.Eq, cameraKind, new ConstantValue(2, cameraType, 32), testHdr, finalize)]),
            new(testHdr, [new ManagedBranch(testHdr, useHdr, new(finalize, []), new(formatLdr, []))]),
            new(formatLdr, [Call(formatLdr, getFormat, null, graphicsFormat, new ConstantValue(0, defaultFormat, 32)), Set(formatLdr, setFormat, graphicsFormat), Go(formatLdr, finalize)]),
            new(finalize, [Set(finalize, setWidth, AtLeastOne(scaledWidth)), Set(finalize, setHeight, AtLeastOne(scaledHeight)),
                Set(finalize, setRandomWrite, off), Set(finalize, setBindMs, off), Call(finalize, dynamic, cam, dynamicScale), Set(finalize, setDynamic, dynamicScale),
                Call(finalize, supported, null, sampleCount, desc), Set(finalize, setSamples, sampleCount), Call(finalize, resolve, null, canResolve),
                new ManagedBranch(finalize, canResolve, new(done, []), new(forceOne, []))]),
            new(forceOne, [Set(forceOne, setSamples, one), Go(forceOne, done)]),
            new(done, [new ManagedReturn(done, desc)]),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "UniversalRenderPipelineCore.cs（APK 使用 renderScale 并夹紧尺寸）");
    }

    private static MethodRef? Static(DefinitionRef owner, string name, TypeRef returned, params TypeRef[] parameters) => owner.Definition.Methods.SingleOrDefault(m =>
        m.Name == name && m.IsStatic && m.GenericParameters.Count == 0 && m.ReturnType.Equals(returned)
        && m.Parameters.Select(p => p.Type).SequenceEqual(parameters)) is { } method ? new(method, [], []) : null;

    private static FieldRef? Field(DefinitionRef owner, string name) => owner.Definition.Fields.SingleOrDefault(f => f.Name == name && !f.IsStatic) is { } field
        ? new(field, owner) : null;

    private static MethodRef? Method(DefinitionRef owner, string name, TypeRef returned, params TypeRef[] parameters) => owner.Definition.Methods.SingleOrDefault(m =>
        m.Name == name && !m.IsStatic && m.GenericParameters.Count == 0 && m.ReturnType.Substitute(owner.Arguments, []).Equals(returned)
        && m.Parameters.Select(p => p.Type.Substitute(owner.Arguments, [])).SequenceEqual(parameters)) is { } method
        ? new(method, owner.Arguments, []) : null;

    private LocalValue Local(int slot, TypeRef type) => new(new Variable(Storage.Temp(slot), 1), type, types.ValueWidth(type));

    private FieldValue Read(ManagedValue receiver, FieldRef field)
    {
        var type = field.Field.Type.Substitute(((DefinitionRef)field.DeclaringType).Arguments, []);
        return new(receiver, field, type, types.ValueWidth(type));
    }

    private ManagedBranch Branch(ulong at, Cond test, ManagedValue lhs, ManagedValue rhs, ulong yes, ulong no) =>
        new(at, new ComparisonValue(test, lhs, rhs, types.Boolean), new(yes, []), new(no, []));
}
