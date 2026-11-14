using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private MethodRecoveryResult? RestoreRenderRequests(MethodRef method, ulong entry)
    {
        if (method.Method is not { IsStatic: false, GenericParameters.Count: 1,
                Parameters: [{ Type: DefinitionRef context }, { Type: DefinitionRef camera }, _] }
            || method.MethodArguments is not [GenericParameterRef { IsMethod: true } request]
            || !method.Concrete(method.Method.Parameters[2].Type).Equals(request) || !ManagedTypes.IsVoid(method.Method.ReturnType)
            || context.Definition.FullName != "UnityEngine.Rendering.ScriptableRenderContext" || camera.Definition.FullName != "UnityEngine.Camera"
            || !UrpCode(entry, 1456, "CA365723F3B6C24CE7245DF0DA294164819D920EEEB4949626E477D43EF24ECB")) return null;
        DefinitionRef? Known(string fullName, string assembly = "UnityEngine.CoreModule.dll") =>
            facts.Model.Types.SingleOrDefault(t => t.Assembly == assembly && t.FullName == fullName) is { } type ? new(type, []) : null;
        if (Known("UnityEngine.Rendering.RenderPipeline.StandardRequest") is not { } standard
            || Known("UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest", method.Method.Owner.Assembly) is not { } single
            || Known("UnityEngine.RenderTexture") is not { } texture || Known("UnityEngine.Texture") is not { } baseTexture
            || Known("UnityEngine.RenderTextureDescriptor") is not { } descriptor || Known("UnityEngine.Object") is not { } unityObject
            || Known("UnityEngine.Graphics") is not { } graphics || Known("UnityEngine.Debug") is not { } debug
            || Known("UnityEngine.Rendering.TextureDimension") is not { } dimension || Known("UnityEngine.RenderTextureFormat") is not { } format
            || Known("UnityEngine.CubemapFace") is not { } cubeFace || types.Corlib("System.Type") is not DefinitionRef reflectionType
            || types.String is not DefinitionRef text || method.DeclaringType is not DefinitionRef owner) return null;

        var @void = types.Corlib("System.Void");
        var boolean = types.Boolean;
        var integer = types.Int32;
        var cameras = new ArrayRef(camera, 1);
        if (Field(standard, "destination") is not { } standardDest || !standardDest.Field.Type.Equals(texture)
            || Field(single, "destination") is not { } singleDest || !singleDest.Field.Type.Equals(texture)
            || Field(standard, "mipLevel") is not { } standardMip || !standardMip.Field.Type.Equals(integer)
            || Field(single, "mipLevel") is not { } singleMip || !singleMip.Field.Type.Equals(integer)
            || Field(standard, "slice") is not { } standardSlice || !standardSlice.Field.Type.Equals(integer)
            || Field(single, "slice") is not { } singleSlice || !singleSlice.Field.Type.Equals(integer)
            || Field(standard, "face") is not { } standardFace || !standardFace.Field.Type.Equals(cubeFace)
            || Field(single, "face") is not { } singleFace || !singleFace.Field.Type.Equals(cubeFace)
            || Method(camera, "get_targetTexture", texture) is not { } getTarget || Method(camera, "set_targetTexture", @void, texture) is not { } setTarget
            || Method(texture, "get_descriptor", descriptor) is not { } getDesc || Method(texture, "get_format", format) is not { } getFormat
            || Method(texture, "get_dimension", dimension) is not { } getDim || Method(texture, "get_width", integer) is not { } getWidth
            || Method(texture, "get_height", integer) is not { } getHeight
            || Method(descriptor, "set_colorFormat", @void, format) is not { } setFormat
            || Method(descriptor, "set_volumeDepth", @void, integer) is not { } setDepth
            || Method(descriptor, "get_msaaSamples", integer) is not { } getSamples || Method(descriptor, "set_msaaSamples", @void, integer) is not { } setSamples
            || Method(descriptor, "set_dimension", @void, dimension) is not { } setDim
            || Method(descriptor, "set_width", @void, integer) is not { } setWidth || Method(descriptor, "set_height", @void, integer) is not { } setHeight
            || Static(texture, "GetTemporary", texture, descriptor) is not { } getTemporary || Static(texture, "ReleaseTemporary", @void, texture) is not { } release
            || Static(unityObject, "op_Implicit", boolean, unityObject) is not { } exists
            || Method(owner, "Render", @void, context, cameras) is not { } render
            || Static(owner, "RenderSingleCameraInternal", @void, context, camera) is not { } renderSingle
            || Static(graphics, "CopyTexture", @void, baseTexture, integer, integer, baseTexture, integer, integer) is not { } copy
            || Static(graphics, "SetRenderTarget", @void, texture) is not { } setGraphicsTarget
            || Static(debug, "LogWarning", @void, types.Object) is not { } warn
            || Static(reflectionType, "GetTypeFromHandle", reflectionType, types.RuntimeTypeHandle) is not { } getType
            || Method(reflectionType, "get_FullName", text) is not { } fullName || Static(text, "Concat", text, text, text, text) is not { } concatenate
            || types.CorlibMethod("System.Math", "Pow", types.Double, types.Double) is not { } pow) return null;

        var self = new ArgumentValue(-1, owner, 64);
        var ctx = new ArgumentValue(0, context, types.ValueWidth(context));
        var cam = new ArgumentValue(1, camera, 64);
        var data = new ArgumentValue(2, request, types.ValueWidth(request));
        var boxed = Local(0, types.Object);
        var standardRequest = Local(1, standard);
        var singleRequest = Local(2, single);
        var destination = Local(3, texture);
        var mip = Local(4, integer);
        var slice = Local(5, integer);
        var face = Local(6, cubeFace);
        var original = Local(7, texture);
        var temporary = Local(8, texture);
        var desc = Local(9, descriptor);
        var secondDesc = Local(10, descriptor);
        var targetDimension = Local(11, dimension);
        var targetFormat = Local(12, format);
        var samples = Local(13, integer);
        var width = Local(14, integer);
        var height = Local(15, integer);
        var scale = Local(16, types.Double);
        var divisor = Local(17, integer);
        var haveTemporary = Local(18, boolean);
        var array = Local(19, cameras);
        var reflectedType = Local(20, reflectionType);
        var name = Local(21, text);
        var message = Local(22, text);
        var element = Local(23, integer);

        const int start = 0, hasSingle = 1, unsupported = 2, readStandard = 3, readSingle = 4, originalTarget = 5,
            emptyDescriptor = 6, updateDescriptor = 7, checkMip = 8, allocate = 9, targetCamera = 10,
            renderArray = 11, singleCamera = 12, haveCopy = 13, copyDimension = 14, cubeDimension = 15,
            sliceOnly = 16, cubeSlice = 17, copyTexture = 18, finish = 19, done = 20;
        ulong At(int id) => entry + (ulong)id * 4;
        ManagedBlock Block(int id, params ManagedStatement[] statements) => new(At(id), statements);
        ManagedCall Call(int id, MethodRef member, ManagedValue? receiver, LocalValue? result, params ManagedValue[] args) =>
            new(At(id), new(member, receiver, args, receiver is not null && (member.Method.IsVirtual || ManagedTypes.IsReferenceType(receiver.Type))), result);
        ManagedCall Set(int id, MethodRef setter, ManagedValue value) => Call(id, setter, new AddressValue(desc), null, value);
        ManagedJump Go(int id, int to) => new(At(id), new(At(to), []));
        ManagedAssignment Assign(int id, LocalValue local, ManagedValue value) => new(At(id), local, value);
        ManagedBranch Test(int id, ManagedValue value, int yes, int no) => new(At(id), value, new(At(yes), []), new(At(no), []));
        ManagedValue Null(TypeRef t) => new ConstantValue(0, t, 64);
        ManagedValue Int(int value) => new ConstantValue(unchecked((uint)value), integer, 32);
        ManagedValue Equal(ManagedValue lhs, ManagedValue rhs) => new ComparisonValue(Cond.Eq, lhs, rhs, boolean);
        ManagedValue Value(BinOp operation, ManagedValue lhs, ManagedValue rhs) => new BinaryValue(operation, lhs, rhs, integer, 32);
        ManagedValue Scale() => new ChoiceValue(new ComparisonValue(Cond.FEq, scale, new ConstantValue(0x7ff0000000000000, types.Double, 64), boolean), Int(int.MinValue),
            new ConversionValue(ConvKind.FloatToSInt, scale, integer, 32), integer, 32);
        ManagedValue Divide(ManagedValue value) => new BinaryValue(BinOp.SDiv, value, divisor, integer, 32);
        ManagedValue AtLeastOne(ManagedValue value) => new ChoiceValue(new ComparisonValue(Cond.Sgt, value, Int(1), boolean), value, Int(1), integer, 32);
        ManagedValue IsDimension(int number) => Equal(targetDimension, new ConstantValue((uint)number, dimension, 32));
        ManagedValue Either(ManagedValue a, ManagedValue b) => new BooleanValue(false, a, b, boolean);

        ManagedBlock[] blocks =
        [
            Block(start, Assign(start, boxed, new BoxValue(data, request, types.Object)),
                Assign(start, standardRequest, new IsInstValue(boxed, standard)), Assign(start, singleRequest, new IsInstValue(boxed, single)),
                Test(start, Equal(standardRequest, Null(standard)), hasSingle, readStandard)),
            Block(hasSingle, Test(hasSingle, Equal(singleRequest, Null(single)), unsupported, readSingle)),
            Block(unsupported, Call(unsupported, getType, null, reflectedType, new TypeHandleValue(request, types.RuntimeTypeHandle, 64)),
                Call(unsupported, fullName, reflectedType, name), Call(unsupported, concatenate, null, message,
                    new StringValue("The given RenderRequest type: ", text), name, new StringValue(", is either invalid or unsupported by the current pipeline", text)),
                Call(unsupported, warn, null, null, message), Go(unsupported, done)),
            Block(readStandard, Assign(readStandard, mip, Read(standardRequest, standardMip)), Assign(readStandard, destination, Read(standardRequest, standardDest)),
                Assign(readStandard, slice, Read(standardRequest, standardSlice)), Assign(readStandard, face, Read(standardRequest, standardFace)), Go(readStandard, originalTarget)),
            Block(readSingle, Assign(readSingle, mip, Read(singleRequest, singleMip)), Assign(readSingle, destination, Read(singleRequest, singleDest)),
                Assign(readSingle, slice, Read(singleRequest, singleSlice)), Assign(readSingle, face, Read(singleRequest, singleFace)), Go(readSingle, originalTarget)),
            Block(originalTarget, Call(originalTarget, getTarget, cam, original), Call(originalTarget, getDesc, destination, desc),
                Call(originalTarget, getDim, destination, targetDimension), Test(originalTarget, IsDimension(4), emptyDescriptor, updateDescriptor)),
            Block(emptyDescriptor, Assign(emptyDescriptor, desc, new ComposedValue(descriptor, [])), Go(emptyDescriptor, updateDescriptor)),
            Block(updateDescriptor, Call(updateDescriptor, getFormat, destination, targetFormat), Set(updateDescriptor, setFormat, targetFormat),
                Set(updateDescriptor, setDepth, Int(1)), Call(updateDescriptor, getDesc, destination, secondDesc),
                Call(updateDescriptor, getSamples, new AddressValue(secondDesc), samples), Set(updateDescriptor, setDim, new ConstantValue(2, dimension, 32)),
                Set(updateDescriptor, setSamples, samples), Call(updateDescriptor, getWidth, destination, width),
                Call(updateDescriptor, pow, null, scale, new ConstantValue(0x4000000000000000, types.Double, 64), new ConversionValue(ConvKind.SIntToFloat, mip, types.Double, 64)),
                Assign(updateDescriptor, divisor, Scale()), Assign(updateDescriptor, width, Divide(width)), Call(updateDescriptor, getHeight, destination, height),
                Call(updateDescriptor, pow, null, scale, new ConstantValue(0x4000000000000000, types.Double, 64), new ConversionValue(ConvKind.SIntToFloat, mip, types.Double, 64)),
                Assign(updateDescriptor, divisor, Scale()), Assign(updateDescriptor, height, Divide(height)),
                Set(updateDescriptor, setWidth, AtLeastOne(width)), Set(updateDescriptor, setHeight, AtLeastOne(height)),
                Call(updateDescriptor, getDim, destination, targetDimension), Assign(updateDescriptor, temporary, Null(texture)),
                Test(updateDescriptor, IsDimension(2), checkMip, allocate)),
            Block(checkMip, Test(checkMip, Equal(mip, Int(0)), targetCamera, allocate)),
            Block(allocate, Call(allocate, getTemporary, null, temporary, desc), Go(allocate, targetCamera)),
            Block(targetCamera, Call(targetCamera, exists, null, haveTemporary, temporary), Call(targetCamera, setTarget, cam, null,
                new ChoiceValue(haveTemporary, temporary, destination, texture, 64)), Test(targetCamera, Equal(standardRequest, Null(standard)), singleCamera, renderArray)),
            Block(renderArray, Assign(renderArray, array, new NewArrayValue(cameras, Int(1))),
                new ManagedStore(At(renderArray), new ArrayElementValue(array, Int(0), camera, 64), cam), Call(renderArray, render, self, null, ctx, array), Go(renderArray, haveCopy)),
            Block(singleCamera, Call(singleCamera, renderSingle, null, null, ctx, cam), Go(singleCamera, haveCopy)),
            Block(haveCopy, Call(haveCopy, exists, null, haveTemporary, temporary), Test(haveCopy, haveTemporary, copyDimension, finish)),
            Block(copyDimension, Call(copyDimension, getDim, destination, targetDimension), Test(copyDimension, Either(IsDimension(2), Either(IsDimension(3), IsDimension(5))), sliceOnly, cubeDimension)),
            Block(cubeDimension, Test(cubeDimension, Either(IsDimension(4), IsDimension(6)), cubeSlice, finish)),
            Block(sliceOnly, Assign(sliceOnly, element, slice), Go(sliceOnly, copyTexture)),
            Block(cubeSlice, Assign(cubeSlice, element, Value(BinOp.Add, new ConversionValue(ConvKind.Bitcast, face, integer, 32), Value(BinOp.Mul, slice, Int(6)))), Go(cubeSlice, copyTexture)),
            Block(copyTexture, Call(copyTexture, copy, null, null, temporary, Int(0), Int(0), destination, element, mip), Go(copyTexture, finish)),
            Block(finish, Call(finish, setTarget, cam, null, original), Call(finish, setGraphicsTarget, null, null, original),
                Call(finish, release, null, null, temporary), Go(finish, done)),
            Block(done, new ManagedReturn(At(done), null)),
        ];
        return UrpPackageResult(method, entry, new ManagedBody(method, blocks), "UniversalRenderPipeline.cs（APK 使用 Camera[] 和双参数 RenderSingleCameraInternal）");
    }

}
