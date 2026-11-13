using System.Reflection;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Inlining;

internal sealed partial class TemplateSummaries
{
    /// <summary>浮点计算构造函数的双路径摘要。各路径只初始化新值，可读 beforefieldinit 的只读静态字段。</summary>
    private readonly Dictionary<DefinitionRef, (MethodRef Method, Run Run, Term[][] Contents)[]> branchConstructors = [];

    private (MethodRef Method, Run Run, Term[][] Contents)[] BranchConstructors(DefinitionRef structure, Func<MethodRef, Program?> templates)
    {
        if (branchConstructors.TryGetValue(structure, out var known)) return known;
        var found = new List<(MethodRef, Run, Term[][])>();
        if ((structure.Definition.Attributes & TypeAttributes.BeforeFieldInit) == 0 && structure.Definition.Methods.Any(m => m.Name == ".cctor"))
            return branchConstructors[structure] = [];
        foreach (var definition in structure.Definition.Methods.Where(m => m.Name == ".ctor" && !m.IsStatic && m.GenericParameters.Count == 0))
        {
            var method = new MethodRef(definition, structure.Arguments, []);
            if (definition.Parameters.Count == 0 || definition.Parameters.Any(p => types.Kind(method.Concrete(p.Type)) != TypeKind.Struct)
                || evidence.CallFailure(method, structure, null) is not null || templates(method) is not { } program || program.Body.Regions.Count != 0
                || Execute(program, true) is not { Constructed: { } cell, Paths.Count: 2 } run || run.Terms.Overlays?.Invoke(structure) is not null) continue;
            var content = new List<Term[]>();
            foreach (var path in run.Paths)
            {
                if (path is not { Phi.Count: 1, End.Kind: StopKind.Return, Events.Count: 0 }
                    || path.State.Writes.Any(w => !run.Interpreter.Private(path.State, w.Location))
                    || run.Interpreter.ReadValue(path.State, cell.Location, structure, true) is not { Kind: TermKind.Tuple } tuple) break;
                content.Add(tuple.Args);
            }
            if (content.Count == 2 && ReferenceEquals(run.Terms.Not(run.Paths[0].Phi[0]), run.Paths[1].Phi[0]))
                found.Add((method, run, content.ToArray()));
        }
        return branchConstructors[structure] = found.ToArray();
    }

    public (MethodRef Constructor, Sym[] Arguments)? ProveBranchConstruction(DefinitionRef structure, OpSym test, Sym[] taken, Sym[] fallthrough,
        TypeOracle oracle, ValueModel values, int branchBlock, Func<MethodRef, Program?> templates)
    {
        foreach (var (method, run, contents) in BranchConstructors(structure, templates))
        {
            var slots = method.Method.Parameters.Select(p => types.Projections((DefinitionRef)method.Concrete(p.Type)).ToArray()).ToArray();
            var keys = slots.Select(s => s.Select(p => run.Terms.Path(p.Path).Id).ToArray()).ToArray();
            for (var i = 0; i < contents.Length; i++)
            {
                var binding = slots.Select(s => new Sym?[s.Length]).ToArray();
                bool Match(Term expected, Sym actual)
                {
                    actual = SymTable.Resolve(actual);
                    if (expected.Width != actual.Width) return false;
                    if (expected.IsConstant) return actual is ConstSym constant && constant.Value == expected.Bits;
                    if (expected.IsAtom(AtomKind.Formal) && expected.Key is TermTable.LeafKey { Root: int index, Path: var path })
                    {
                        var leaf = Array.IndexOf(keys[index], path);
                        if (leaf < 0 || !InputAvailable(actual, values, branchBlock)) return false;
                        if (binding[index][leaf] is { } bound) return ReferenceEquals(bound, actual);
                        binding[index][leaf] = actual;
                        return true;
                    }
                    if (expected is { Kind: TermKind.Read, Aux: 0, Location.Root: RootKind.Static })
                    {
                        var location = actual switch
                        {
                            ReadSym read => oracle.Locate(read.Address, read.Width),
                            SliceSym { Base: ReadSym read } slice => oracle.Part(read, slice.Low, slice.Width),
                            _ => null,
                        };
                        return location is { Kind: LocationKind.Static, Owner: DefinitionRef owner, Path: [var field, ..] }
                            && (owner.Definition.Attributes & TypeAttributes.BeforeFieldInit) != 0
                            && (field.Field.Attributes & FieldAttributes.InitOnly) != 0
                            && location.Path.SequenceEqual(TermTable.PathOf(expected.Location).Fields);
                    }
                    if (actual is not OpSym operation || expected.Kind != TermKind.Operation) return false;
                    return expected.Op switch
                    {
                        OpCode.Binary when operation.Op.Kind == OperatorKind.Binary && operation.Op.Binary == expected.BinOp
                            && expected.BinOp is BinOp.FAdd or BinOp.FMul or BinOp.FDiv =>
                            Match(expected.Args[0], operation.Args[0]) && Match(expected.Args[1], operation.Args[1]),
                        OpCode.Unary when operation.Op.Kind == OperatorKind.Unary && operation.Op.Unary == UnOp.FSqrt && expected.Aux == (int)UnOp.FSqrt =>
                            Match(expected.Args[0], operation.Args[0]),
                        _ => false,
                    };
                }
                bool MatchPath(Term[] path, Sym[] symbols) => path.Length == symbols.Length && path.Zip(symbols).All(p => Match(p.First, p.Second));
                if (!MatchPath(contents[i], taken) || !MatchPath(contents[1 - i], fallthrough)) continue;
                var condition = run.Paths[i].Phi[0];
                if (!condition.IsOp(OpCode.Compare) || condition.Cond != test.Op.Cond
                    || !Match(condition.Args[0], test.Args[0]) || !Match(condition.Args[1], test.Args[1])) continue;
                if (binding.Any(b => b.Length == 0 || b.Any(s => s is null))) continue;
                var arguments = new Sym[slots.Length];
                for (var p = 0; p < slots.Length; p++)
                {
                    var pieces = new List<Sym>();
                    var offset = 0;
                    foreach (var (projection, leaf) in slots[p].Zip(binding[p]))
                    {
                        if (projection.Bit < offset) break;
                        if (projection.Bit > offset) pieces.Add(values.Syms.Undef(projection.Bit - offset));
                        pieces.Add(leaf!);
                        offset = projection.Bit + projection.Width;
                    }
                    if (pieces.Count == 0 || offset != types.StorageBits(method.Concrete(method.Method.Parameters[p].Type))) break;
                    arguments[p] = values.Syms.Concat(pieces);
                }
                if (arguments.All(a => a is not null)) return (method, arguments);
            }
        }
        return null;
    }

    private static bool InputAvailable(Sym value, ValueModel values, int branchBlock) => value switch
    {
        ConstSym or ArgSym => true,
        ResultSym result => values.BlockOf(result.Site).Index < branchBlock,
        ReadSym read => values.BlockOf(read.Site).Index < branchBlock,
        PhiSym phi => phi.Block.Index <= branchBlock,
        SliceSym slice => InputAvailable(slice.Base, values, branchBlock),
        OpSym { Op: { Kind: OperatorKind.Binary, Binary: BinOp.FAdd or BinOp.FSub or BinOp.FMul or BinOp.FDiv }, Args: [var left, var right] } =>
            InputAvailable(left, values, branchBlock) && InputAvailable(right, values, branchBlock),
        _ => false,
    };
}
