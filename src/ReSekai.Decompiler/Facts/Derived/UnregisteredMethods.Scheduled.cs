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
        private readonly Dictionary<(ulong, string), CopyBlock[]?> normalizedCopies = [];
        private sealed record CopyBlock(int[] Successors, int[] Predecessors, Stmt[] Statements);

        // 已识别的调用序列只用于收窄候选；身份由完整的 SSA 体复核。
        private string? ScheduledKey(CodeFunction function)
        {
            if (function.Size is < 32 or > 1024 || function.ExceptionRegions.Length != 0) return null;
            var calls = new List<string>();
            foreach (var reference in facts.Scan.Of(facts.Code.EntryIndex(function.Start)))
                if (reference.Kind is RefKind.Call or RefKind.TailCall && facts.Helpers.At(reference.Target)?.Kind is not (HelperKind.InitMetadata or HelperKind.ClassInit))
                    calls.Add($"{reference.Target:x}");
            return calls.Count >= 1 ? string.Join(",", calls) : null;
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
            if (!scheduledCandidates.TryGetValue(key, out var all)) return null;
            // 调度与寄存器分配只让长度有小的出入。
            var candidates = all.Where(c => Math.Abs((long)facts.Code.At(c.Address)!.Size - (long)function.Size) <= 64).ToArray();
            if (candidates.Length is 0 or > 24) return null;
            var equivalent = candidates.Where(c => SameCopy(function, c)).ToArray();
            if (equivalent.Select(c => c.Method).Distinct().ToArray() is not [var only]
                || only.TypeArguments.Count + only.MethodArguments.Count != 0 || !IsComplete(only)
                || Signature(only) is null) return null;
            matches = equivalent.Select(c => c.Address).Distinct().Order().ToArray();
            return only;
        }

        /// <summary>函数与同形的注册体逐语句相同（守卫、调度与基址的算法归一之后）。</summary>
        private bool SameCopy(CodeFunction function, MethodRef method, Dictionary<string, List<RegisteredShape>> shapes, out ulong[] matches)
        {
            matches = shapes[Fingerprint(function).Hash].Where(c => c.Method == method && SameCopy(function, c)).Select(c => c.Address).Distinct().Order().ToArray();
            return matches.Length > 0;
        }

        /// <summary>候选的签名决定返回值在哪些寄存器里，两边都按它读取返回值再比较。</summary>
        private bool SameCopy(CodeFunction function, RegisteredShape candidate) => Signature(candidate.Method) is { } signature
            && NormalizedCopy(function.Start, signature) is { } body && NormalizedCopy(candidate.Address, signature) is { } other && SameBody(body, other);

        private CopyBlock[]? NormalizedCopy(ulong entry, AbiSignature signature)
        {
            var key = (entry, Shape(signature));
            if (normalizedCopies.TryGetValue(key, out var known)) return known;
            return normalizedCopies[key] = Normalize();
            CopyBlock[]? Normalize()
            {
                var lifted = new FunctionLifter(facts) { InlinesClosures = true }.Lift(entry, null,
                    signature.Results.Select((r, i) => new ReturnPiece(i * 16, new Read(r, r.Width))).ToArray(), simplify: true, promoteFrameSlots: false);
                if (!lifted.Succeeded || lifted.Function.UnresolvedIndirectJumps != 0) return null;
                var function = lifted.Function;
                var result = new IdiomRewriter(facts) { RecordFilter = _ => false, RecoverUnusedInputs = true, AssumedSignature = signature }.Run(function);
                if (result.Errors.Count != 0 || function.Blocks.Count > 32 || function.Blocks.Any(b => b.Handler is not null)) return null;
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
                            case Store store when Supported(store.Target) && Supported(store.Value):
                                statements.Add(store);
                                break;
                            case Branch branch when Supported(branch.Condition):
                                statements.Add(branch with { Target = (ulong)blocks[branch.Target], Fallthrough = (ulong)blocks[branch.Fallthrough] });
                                break;
                            case Jump:
                                break;
                            case Return { Value: null, Buffer: null } ret when ret.Pieces.All(p => Supported(p.Value)):
                                statements.Add(ret);
                                break;
                            case Call call when Supported(call.Target)
                                && call.Arguments.All(a => Supported(a) || a is Unknown { Reason: var reason } && reason.EndsWith(" 被调用破坏", StringComparison.Ordinal))
                                && (call.StackPointer is null || Supported(call.StackPointer))
                                && call.Memory.Length == 0 && call.Resolved is null && call.DelegateReceiver is null
                                && call.Kind is CallKind.Direct or CallKind.Tail:
                                statements.Add(WithoutUnusedMethodInfo(call));
                                break;
                            default:
                                return null;
                        }
                    }
                    output.Add(new(block.Successors.Select(b => b.Index).ToArray(), block.Predecessors.Select(b => b.Index).ToArray(), statements.ToArray()));
                }
                return output.ToArray();
            }
        }

        /// <summary>非泛型方法的代码不取决于隐藏 MethodInfo；codegen 有时传零，有时不设置，两种写法是同一次调用。</summary>
        private Call WithoutUnusedMethodInfo(Call call)
        {
            if (call is not { Op: null, Target: Const target }) return call;
            var methods = facts.EntriesAt(target.Value).Where(e => e.Kind is EntryKind.Method or EntryKind.GenericMethod).Select(e => e.Method).ToArray();
            if (methods.Length == 0 || methods.Any(m => m is null || m.TypeArguments.Count + m.MethodArguments.Count != 0)
                || methods.Select(m => Signature(m!)?.MethodInfo?.Register).Distinct().ToArray() is not [{ } hidden]) return call;
            var index = call.ArgumentIndex(hidden);
            if (index < 0 || call.Arguments[index] is Symbol) return call;
            return call with { Arguments = call.Arguments.Select((a, i) => i == index ? Expr.C(0, 64) : a).ToArray() };
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
            (Return a, Return b) => a.Pieces.SequenceEqual(b.Pieces),
            (Call a, Call b) => a.Kind == b.Kind && a.Target == b.Target && a.Op == b.Op && a.StackPointer == b.StackPointer
                && a.DoesNotReturn == b.DoesNotReturn && a.Inlined == b.Inlined
                && a.Arguments.SequenceEqual(b.Arguments) && a.Sources.SequenceEqual(b.Sources)
                && a.Defines.SequenceEqual(b.Defines) && a.Results.SequenceEqual(b.Results),
            _ => false,
        };

        private static bool Supported(Expr expression) => ExprMap.Walk(expression).All(e => e is Const or Use or FrameAddress
            or Binary or Unary or Ir.Compare or Ir.Convert or Extract or Insert or Load or Select or Logical or Not or Intrinsic
            || e is Symbol);
    }
}
