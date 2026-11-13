using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private const string JoystickPackSource =
        "https://github.com/FeneraxStudios/Unity-Joystick-Pack/blob/master/Joystick%20Pack%20v2.1.unitypackage";

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
