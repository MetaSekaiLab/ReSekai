using ReSekai.Decompiler.Idioms;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Lifting;
using static ReSekai.Decompiler.Facts.UnregisteredMethods;

namespace ReSekai.Decompiler.Facts.Derived;

public static partial class UnregisteredInference
{
    private sealed partial class Builder
    {
        private readonly Dictionary<string, List<RegisteredShape>> scheduledCandidates = [];
        private readonly Dictionary<ulong, CopyBlock[]?> normalizedCopies = [];
        private sealed record CopyBlock(int[] Successors, int[] Predecessors, Stmt[] Statements);

        // 后半段的已识别调用序列只用于收窄候选；身份由完整的 SSA 体复核。
        private string? ScheduledKey(CodeFunction function)
        {
            if (function.Size is < 32 or > 512 || function.ExceptionRegions.Length != 0) return null;
            var calls = new List<string>();
            foreach (var reference in facts.Scan.Of(facts.Code.EntryIndex(function.Start)))
                if (reference.Kind is RefKind.Call or RefKind.TailCall && facts.Helpers.At(reference.Target)?.Kind != HelperKind.InitMetadata)
                    calls.Add($"{reference.Kind}:{reference.Target:x}");
            return calls.Count >= 3 ? $"{function.Size}:{string.Join(",", calls)}" : null;
        }

        private MethodRef? ScheduledCopy(CodeFunction function, Dictionary<string, List<RegisteredShape>> shapes, out ulong[] matches)
        {
            matches = [];
            if (ScheduledKey(function) is not { } key) return null;
            if (scheduledCandidates.Count == 0)
                foreach (var bucket in shapes.Values)
                    foreach (var registered in bucket.GroupBy(r => r.Address))
                        if (ScheduledKey(facts.Code.At(registered.Key)!) is { } candidateKey)
                        {
                            if (!scheduledCandidates.TryGetValue(candidateKey, out var list)) scheduledCandidates[candidateKey] = list = [];
                            list.AddRange(registered);
                        }
            if (!scheduledCandidates.TryGetValue(key, out var candidates) || candidates.Count > 24
                || NormalizedCopy(function.Start) is not { } body) return null;
            var equivalent = candidates.Where(c => NormalizedCopy(c.Address) is { } other && SameBody(body, other)).ToArray();
            if (equivalent.Select(c => c.Method).Distinct().ToArray() is not [var only]
                || only.TypeArguments.Count + only.MethodArguments.Count != 0 || !IsComplete(only)
                || Signature(only) is not { MethodInfo: not null }) return null;
            matches = equivalent.Select(c => c.Address).Distinct().Order().ToArray();
            return only;
        }

        private CopyBlock[]? NormalizedCopy(ulong entry)
        {
            if (normalizedCopies.TryGetValue(entry, out var known)) return known;
            return normalizedCopies[entry] = Normalize();
            CopyBlock[]? Normalize()
            {
                var lifted = new FunctionLifter(facts) { InlinesClosures = true }.Lift(entry, null, null, simplify: true, promoteFrameSlots: false);
                if (!lifted.Succeeded || lifted.Function.UnresolvedIndirectJumps != 0) return null;
                var function = lifted.Function;
                var result = new IdiomRewriter(facts) { RecordFilter = _ => false, RecoverUnusedInputs = true }.Run(function);
                if (result.Errors.Count != 0 || function.Blocks.Count > 16 || function.Blocks.Any(b => b.Handler is not null)) return null;
                var blocks = function.Blocks.ToDictionary(b => b.Start, b => b.Index);
                var output = new List<CopyBlock>();
                foreach (var block in function.Blocks)
                {
                    var statements = new List<Stmt>();
                    foreach (var statement in block.Statements)
                    {
                        switch (statement)
                        {
                            case Nop:
                                break;
                            case Phi phi:
                                statements.Add(phi);
                                break;
                            case Define define when Supported(define.Value):
                                statements.Add(define);
                                break;
                            case Store store when store.Target is FrameAddress && Supported(store.Value):
                                statements.Add(store);
                                break;
                            case Branch branch when Supported(branch.Condition):
                                statements.Add(branch with { Target = (ulong)blocks[branch.Target], Fallthrough = (ulong)blocks[branch.Fallthrough] });
                                break;
                            case Jump:
                                break;
                            case Call call when Supported(call.Target)
                                && call.Arguments.All(a => Supported(a) || a is Unknown { Reason: var reason } && reason.EndsWith(" 被调用破坏", StringComparison.Ordinal))
                                && (call.StackPointer is null || Supported(call.StackPointer))
                                && call.Memory.Length == 0 && call.Resolved is null && call.DelegateReceiver is null
                                && call.Kind is CallKind.Direct or CallKind.Tail
                                && call.Op is null or { Kind: OpKind.RunClassConstructor, Operand: DefinitionRef }:
                                statements.Add(call);
                                break;
                            default: return null;
                        }
                    }
                    output.Add(new(block.Successors.Select(b => b.Index).ToArray(), block.Predecessors.Select(b => b.Index).ToArray(), statements.ToArray()));
                }
                return output.ToArray();
            }
        }

        private static bool SameBody(CopyBlock[] left, CopyBlock[] right) => left.Length == right.Length
            && left.Zip(right).All(p => p.First.Successors.SequenceEqual(p.Second.Successors)
                && p.First.Predecessors.SequenceEqual(p.Second.Predecessors)
                && p.First.Statements.Length == p.Second.Statements.Length
                && p.First.Statements.Zip(p.Second.Statements).All(s => SameStatement(s.First, s.Second)));

        private static bool SameStatement(Stmt left, Stmt right) => (left, right) switch
        {
            (Phi a, Phi b) => a.Target == b.Target && a.Sources.SequenceEqual(b.Sources),
            (Define a, Define b) => a.Target == b.Target && a.Value == b.Value,
            (Store a, Store b) => a.Target == b.Target && a.Value == b.Value,
            (Branch a, Branch b) => a.Condition == b.Condition && a.Target == b.Target && a.Fallthrough == b.Fallthrough,
            (Call a, Call b) => a.Kind == b.Kind && a.Target == b.Target && a.Op == b.Op && a.StackPointer == b.StackPointer
                && a.DoesNotReturn == b.DoesNotReturn && a.Inlined == b.Inlined
                && a.Arguments.SequenceEqual(b.Arguments) && a.Sources.SequenceEqual(b.Sources)
                && a.Defines.SequenceEqual(b.Defines) && a.Results.SequenceEqual(b.Results),
            _ => false,
        };

        private static bool Supported(Expr expression) => ExprMap.Walk(expression).All(e => e is Const or Use or FrameAddress
            or Binary or Ir.Compare or Ir.Convert or Extract or Load
            || e is Symbol { Kind: SymbolKind.Class, Value: DefinitionRef });
    }
}
