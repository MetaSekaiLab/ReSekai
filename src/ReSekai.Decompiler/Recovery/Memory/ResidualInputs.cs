using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Recovery.Calls;

namespace ReSekai.Decompiler.Recovery.Memory;

/// <summary>
/// 被调方不读的实参，调用方可以不设置，寄存器里留着前一次调用的返回值。
/// 来源中有类型上不可能成为这个实参的返回值时，整个来源都是残留，不能当作实参，也不该为它保留 φ。
/// </summary>
internal sealed class ResidualInputs
{
    private readonly Dictionary<Variable, Expr> definitions = [];
    private readonly Dictionary<Variable, Phi> phis = [];
    private readonly Dictionary<Variable, Call> results = [];
    private readonly Func<Call, CallShape> shapes;

    public ResidualInputs(MachineFunction function, Func<Call, CallShape> shapes)
    {
        this.shapes = shapes;
        foreach (var statement in function.Blocks.SelectMany(b => b.Statements))
            switch (statement)
            {
                case Define define: definitions[define.Target] = define.Value; break;
                case Phi phi: phis[phi.Target] = phi; break;
                case Call call:
                    foreach (var result in call.Defines.OfType<Variable>()) results[result] = call;
                    break;
            }
    }

    public bool Is(Expr argument, TypeRef expected)
    {
        if (!ManagedTypes.IsReferenceType(expected)) return false;
        var seen = new HashSet<Variable>();
        var pending = new Stack<Expr>([argument]);
        while (pending.TryPop(out var value))
        {
            // 栈地址不是类的实例，它是前一次复制留在寄存器里的目标地址。
            if (value is FrameAddress) return true;
            if (value is not Use use || !seen.Add(use.Variable)) continue;
            if (results.GetValueOrDefault(use.Variable) is { Op: { Kind: OpKind.Import, Operand: "memcpy" or "memmove" or "memset" }, Arguments: [var copied, ..] })
            {
                pending.Push(copied);
                continue;
            }
            if (definitions.TryGetValue(use.Variable, out var source)) pending.Push(source);
            else if (phis.TryGetValue(use.Variable, out var phi)) foreach (var input in phi.Sources) pending.Push(new Use(input, input.Width));
            else if (results.TryGetValue(use.Variable, out var call) && shapes(call).Signature is { } signature && !call.DoesNotReturn
                && Impossible(call, use.Variable, signature, expected)) return true;
        }
        return false;
    }

    private static bool Impossible(Call call, Variable variable, MachineSignature signature, TypeRef expected)
    {
        var index = Array.IndexOf(call.Defines, variable);
        if (index < 0 || index >= call.Results.Length) return false;
        if (signature.Return is not { Transfer: Transfer.Value } result || result.Pieces.All(p => p.Register != call.Results[index])) return true;
        if (!ManagedTypes.IsReferenceType(result.Type)) return result.Type is not GenericParameterRef;
        return Class(result.Type) is { } from && Class(expected) is { } to && !Derives(from, to) && !Derives(to, from);
    }

    private static TypeDef? Class(TypeRef type) => type is DefinitionRef { Definition: { IsInterface: false, IsValueType: false } definition }
        && definition.FullName is not ("System.Object" or "System.MulticastDelegate" or "System.Delegate") ? definition : null;

    private static bool Derives(TypeDef type, TypeDef ancestor)
    {
        for (var current = type; current is not null; current = (current.BaseType as DefinitionRef)?.Definition)
            if (current == ancestor) return true;
        return false;
    }
}
