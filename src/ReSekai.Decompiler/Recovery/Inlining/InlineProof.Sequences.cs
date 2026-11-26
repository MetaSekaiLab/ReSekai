using System.Reflection;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery.Inlining;

internal sealed partial class InlineProof
{
    private const int MaxSequence = 3;
    private const int MaxSequences = 96;

    private readonly Dictionary<MethodRef, MethodRef[]> sequences = [];
    private readonly Dictionary<(FieldRef, TypeRef?, int), IReadOnlyList<MethodRef>> sequenceCandidates = [];

    /// <summary>
    /// 一次写入把字段加了 k（2 ≤ k ≤ MaxSequence）：同一对象上相邻的 k 个各加一的内联被编译器合并成一次写入，单个模板都对不上。
    /// </summary>
    private static int MergedSteps(ManagedBody body, (ulong Block, int Index, ManagedValue Access) anchor)
    {
        if (anchor.Access is not FieldValue { Pending.Write: true } access) return 0;
        var statements = body.Blocks.First(b => b.Address == anchor.Block).Statements;
        if (statements[anchor.Index] is not ManagedStore { Target: FieldValue target, Value: BinaryValue { Op: BinOp.Add, Right: ConstantValue { Bits: >= 2 and <= MaxSequence and var steps }, Left: var read } }
            || !ReferenceEquals(target, access)) return 0;
        var source = read is LocalValue local
            ? body.Blocks.SelectMany(b => b.Statements).OfType<ManagedAssignment>().Where(a => a.Target.Variable.Equals(local.Variable)).Select(a => a.Value).ToArray() is [var only] ? only : null
            : read;
        return source is FieldValue previous && previous.Field.Equals(access.Field) ? (int)steps : 0;
    }

    /// <summary>k 个方法在同一对象上依次调用，写成一个合成方法作模板；证成后再展开成各次调用。</summary>
    private IReadOnlyList<MethodRef> Sequences(FieldRef field, TypeRef? receiver, int steps)
    {
        if (sequenceCandidates.TryGetValue((field, receiver, steps), out var known)) return known;
        var single = Candidates(field, receiver).Where(c => !c.Method.Method.IsStatic && ManagedTypes.IsVoid(c.Method.Method.ReturnType)
                && c.Method.DeclaringType.Equals(field.DeclaringType) && !c.Method.Method.IsOverridable
                && evidence.CallFailure(c.Method, receiver, null) is null && Steps(c.Template, field)).Select(c => c.Method).ToList();
        var found = new List<MethodRef>();
        void Extend(MethodRef[] prefix)
        {
            if (prefix.Length == steps) { found.Add(Sequence(prefix)); return; }
            foreach (var next in single)
            {
                if (found.Count >= MaxSequences) return;
                Extend([.. prefix, next]);
            }
        }
        Extend([]);
        return sequenceCandidates[(field, receiver, steps)] = found;
    }

    private static bool Steps(Program template, FieldRef field) => template.Body.Blocks.SelectMany(b => b.Statements).Any(s => s is ManagedStore
        { Target: FieldValue { Receiver: ArgumentValue { Index: -1 } } target, Value: BinaryValue { Op: BinOp.Add, Right: ConstantValue { Bits: 1 } } } && target.Field.Field == field.Field);

    private MethodRef Sequence(MethodRef[] parts)
    {
        if (sequences.FirstOrDefault(s => s.Value.SequenceEqual(parts)) is { Key: { } known }) return known;
        var first = parts[0];
        var definition = new MethodDef
        {
            Index = -1 - sequences.Count, Owner = first.Method.Owner, Name = string.Join("+", parts.Select(p => p.Method.Name)), Token = 0,
            Attributes = MethodAttributes.Public | MethodAttributes.HideBySig, ImplAttributes = MethodImplAttributes.IL, Slot = -1,
            ReturnType = first.Method.ReturnType,
            Parameters = parts.SelectMany(p => p.Method.Parameters.Select(a => a with { Type = p.Concrete(a.Type), HasDefault = false, DefaultValue = null })).ToArray(),
        };
        var method = new MethodRef(definition, first.TypeArguments, []);
        var self = new ArgumentValue(-1, first.DeclaringType, 64);
        var statements = new List<ManagedStatement>();
        var at = 0;
        foreach (var part in parts)
        {
            var arguments = part.Method.Parameters.Select(p => (ManagedValue)new ArgumentValue(at++, part.Concrete(p.Type), types.ValueWidth(part.Concrete(p.Type)))).ToArray();
            statements.Add(new ManagedCall(0, new ManagedInvocation(part, self, arguments, true), null) { Inlined = true });
        }
        statements.Add(new ManagedReturn(0, null));
        sequences[method] = parts;
        programs[method] = Program.Compile(new ManagedBody(method, [new ManagedBlock(0, statements)]), types);
        return method;
    }

    /// <summary>证成的合成调用展开成各次调用；后面的调用的实参在第一次调用之前求值。</summary>
    private ManagedBody ExpandSequences(ManagedBody body)
    {
        if (!body.Blocks.SelectMany(b => b.Statements).Any(s => s is ManagedCall call && sequences.ContainsKey(call.Invocation.Method))) return body;
        return body with
        {
            Blocks = body.Blocks.Select(block => block with
            {
                Statements = block.Statements.SelectMany(statement =>
                {
                    if (statement is not ManagedCall call || !sequences.TryGetValue(call.Invocation.Method, out var parts)) return [statement];
                    var output = new List<ManagedStatement>();
                    ManagedValue Held(ManagedValue value)
                    {
                        if (value is ConstantValue or ArgumentValue or StringValue) return value;
                        var local = NewLocal(value.Type);
                        output.Add(new ManagedAssignment(call.Address, local, value));
                        return local;
                    }
                    var receiver = call.Invocation.Receiver is LocalValue simple ? simple : Held(call.Invocation.Receiver!);
                    var arguments = call.Invocation.Arguments.Select(Held).ToArray();
                    var at = 0;
                    foreach (var part in parts)
                    {
                        output.Add(call with { Invocation = call.Invocation with { Method = part, Receiver = receiver, Arguments = arguments[at..(at + part.Method.Parameters.Count)] } });
                        at += part.Method.Parameters.Count;
                    }
                    return output;
                }).ToArray(),
            }).ToArray(),
        };
    }
}
