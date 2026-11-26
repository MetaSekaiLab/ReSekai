using ReSekai.Decompiler.Idioms;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Lifting;
using static ReSekai.Decompiler.Facts.UnregisteredMethods;

namespace ReSekai.Decompiler.Facts.Derived;

public static partial class UnregisteredInference
{
    private sealed class Observation(ulong caller, CodeRef reference)
    {
        public ulong Caller { get; } = caller;
        public CodeRef Reference { get; } = reference;
        public bool Failed { get; set; }
        public Dictionary<Storage, MethodRef[]> Symbols { get; } = [];
        public List<MethodRef> Positive { get; } = [];
    }

    /// <summary>only 只用于对少数目标重做推断（检查规则改动）；正式构建覆盖全部未注册函数。</summary>
    public static UnregisteredMethods Build(BaseFacts facts, Action<string>? log = null, Func<ulong, bool>? only = null)
    {
        return new Builder(facts, log).Run(only);
    }

    private sealed partial class Builder(BaseFacts facts, Action<string>? log)
    {
        private readonly ManagedAbi abi = new(facts.Layouts);
        private readonly Dictionary<MethodRef, AbiSignature?> signatures = [];
        private readonly Dictionary<ulong, HashSet<ulong>> guards = [];
        private readonly Dictionary<ulong, HashSet<ulong>> guardBranches = [];
        private readonly Dictionary<ulong, bool> addressProofs = [];
        private readonly List<string> analysisErrors = [];
        private AbiSignature? Signature(MethodRef method)
        {
            // 开放参数的相等性只包含名称和槽位，不包含调用者的约束；不能跨调用者复用分类。
            if (!IsClosed(method)) return ClassifyInstance(method, abi);
            if (!signatures.TryGetValue(method, out var result)) signatures[method] = result = ClassifyInstance(method, abi);
            return result;
        }

        public UnregisteredMethods Run(Func<ulong, bool>? only)
        {
            var result = new UnregisteredMethods();
            // 标了 inline 的泛型方法没有 il2cpp 段属性，外提副本落在 .text，与运行时同段；只看托管代码调用、又不是已知 helper 的。
            var outlined = facts.Scan.All.Where(r => r.Kind is RefKind.Call or RefKind.TailCall && facts.Code.RegionOf(r.Target) == CodeRegion.Runtime
                    && facts.Code.RegionOf(r.Site) == CodeRegion.Managed && facts.Helpers.At(r.Target)?.Kind == HelperKind.Unknown)
                .Select(r => r.Target).ToHashSet();
            var functions = facts.Code.Functions.Where(f => (f.Region == CodeRegion.Managed || outlined.Contains(f.Start)) && facts.EntriesAt(f.Start).Length == 0
                && only?.Invoke(f.Start) != false).ToDictionary(f => f.Start);
            var sites = facts.Scan.All.Where(r => r.Kind is RefKind.Call or RefKind.TailCall && functions.ContainsKey(r.Target))
                .Select(r => new Observation(facts.Code.At(r.Site)!.Start, r)).ToArray();
            Observe(sites);
            // 整个注册集合参与歧义否决；不能先按 A 的定义过滤再宣布 B 唯一。
            var shapes = RegisteredShapes();
            var conflicts = new List<string>();
            var abiProblems = new List<string>();
            var contradictions = new List<string>();
            foreach (var group in sites.GroupBy(s => s.Reference.Target).OrderBy(g => g.Key))
            {
                var observations = group.ToArray();
                var positive = observations.SelectMany(s => s.Positive).Distinct().ToArray();
                var complete = positive.Where(IsComplete).ToArray();
                var definitions = positive.Select(m => m.Method).Distinct().ToArray();
                var definition = definitions.Length == 1 ? definitions[0] : null;
                var reason = "A/无见证";
                AbiSignature? signature = null;
                ulong[] matches = [];
                var adjustors = Adjustors(group.Key, observations);
                // IL2CPP 在调用方的翻译单元生成的 inline 副本：非泛型方法不传 MethodInfo，没有见证。
                // 机器码（含引用的数据）与唯一一个注册的非泛型定义同形时，它就是这个方法的副本。
                // 它的身份只由方法体决定，个别调用方分析不完整不影响结论。
                var copied = definitions.Length == 0 ? UniqueCopy(functions[group.Key], shapes) : null;
                var scheduled = definitions.Length == 0 && copied is null
                    ? ScheduledCopy(functions[group.Key], shapes, out matches) : null;
                copied ??= scheduled;
                if (copied is not null)
                {
                    definition = copied.Method;
                    complete = [copied];
                }
                if (definitions.Length > 1) reason = "A/不同定义冲突";
                else if (definition is not null && complete.Length == 0) reason = "A/不完整实例";
                else if (definition is not null)
                {
                    signature = Signature(complete[0])!;
                    // 参数占满寄存器的方法，MethodInfo 在栈上：没有寄存器位置可核对符号，身份由方法体决定。
                    var hidden = signature.MethodInfo?.Register;
                    MethodRef[] Symbols(Observation s) => hidden is { } register ? s.Symbols.GetValueOrDefault(register) ?? [] : [];
                    reason = observations.Any(s => Symbols(s).Any(m => m.Method != definition)) ? "A/不同定义冲突"
                        : copied is null && observations.Any(s => s.Failed) ? "A/分析不完整"
                        : complete.Any(m => Signature(m) is not { } sig || Shape(sig) != Shape(signature)) ? "ABI/正见证不吻合"
                        : "A/通过";
                    // 非单值符号也不能掩盖 ABI 冲突；它不给正见证，但有否决权。
                    if (reason == "A/通过" && observations.SelectMany(Symbols)
                        .Where(IsComplete).Any(m => Signature(m) is { } sig && Shape(sig) != Shape(signature))) reason = "ABI/候选槽位不吻合";
                    if (reason == "A/通过")
                    {
                        reason = scheduled is not null ? "接受" : Compare(functions[group.Key], definition, signature, shapes, out matches);
                        // 机器码唯一同形，只是线性扫描没配上某个地址的基址：改由完整的 SSA 体证明。
                        if (reason == "B/地址用途未证明" && copied is not null && SameCopy(functions[group.Key], copied, shapes, out matches)) reason = "接受";
                        // ABI 由本调用的完整实例分类；开放参数不替换成任意封闭见证。
                    }
                }
                if (reason == "A/不同定义冲突") conflicts.Add($"0x{group.Key:x}: {string.Join(';', positive.Select(m => m.ToString()))}");
                if (reason.StartsWith("ABI/", StringComparison.Ordinal)) abiProblems.Add($"0x{group.Key:x}: {reason}");
                if (definition is not null && adjustors.Any(e => e.Method!.Method != definition))
                {
                    reason = "注册调整器/定义矛盾";
                    contradictions.Add($"0x{group.Key:x}: A={definition.Index}, adjustors={string.Join(';', adjustors.Select(e => e.ToString()))}");
                }
                if (reason == "接受")
                    result.accepted.Add(group.Key, new(group.Key, definition!, signature!,
                        observations.SelectMany(s => s.Positive.Where(IsComplete).Select(m => new MethodWitness(s.Caller, s.Reference.Site, m))).ToArray(), matches, adjustors));
                result.decisions.Add(new(group.Key, reason, observations.Length, observations.Count(s => s.Positive.Count > 0), positive.Length, definition?.Index));
            }
            facts.Checks.Add("unregistered.definition-conflict", "Warning", functions.Count, conflicts, "不同定义的 MethodInfo 见证，拒绝身份推断");
            facts.Checks.Add("unregistered.abi", "Warning", functions.Count, abiProblems, "正见证或注册同形体 ABI 不吻合，拒绝身份推断");
            facts.Checks.Add("unregistered.analysis", "Warning", sites.Length, analysisErrors, "调用者提升/L2 不完整，相关目标不接受");
            facts.Checks.Add("unregistered.registration", "Error", functions.Count, contradictions, "A 与注册调整器精确跳转的定义矛盾");
            facts.Checks.Info("unregistered.definitions", result.All.Count, string.Join("；", result.Decisions.GroupBy(d => d.Reason).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
            log?.Invoke($"未注册定义：{result.All.Count}/{functions.Count}；原始调用点 {sites.Length}");
            return result;
        }

        private MethodRef? UniqueCopy(CodeFunction function, Dictionary<string, List<RegisteredShape>> shapes) =>
            shapes.TryGetValue(Fingerprint(function).Hash, out var bucket) && bucket.Select(m => m.Method).Distinct().ToArray() is [var only]
            && only.TypeArguments.Count + only.MethodArguments.Count == 0 && IsComplete(only) && Signature(only) is not null ? only : null;

        private CodeEntry[] Adjustors(ulong target, Observation[] sites) => sites.Select(s => s.Caller).Distinct()
            .Where(entry => facts.Code.At(entry) is { Size: 8 } && facts.Code.Word(entry) == 0x91004000
                && facts.Code.Word(entry + 4) is { } jump && A64.IsB(jump) && A64.Imm26Target(jump, entry + 4) == target)
            .SelectMany(facts.EntriesAt).Where(e => e.Method is not null && e.Kind is EntryKind.AdjustorThunk or EntryKind.GenericAdjustorThunk).ToArray();

        private void Observe(Observation[] sites)
        {
            var lifter = new FunctionLifter(facts);
            var rewriter = new IdiomRewriter(facts) { RecordFilter = _ => false };
            foreach (var group in sites.GroupBy(s => s.Caller))
            {
                var lifted = lifter.Lift(group.Key);
                var rewritten = lifted.Succeeded ? rewriter.Run(lifted.Function) : null;
                if (!lifted.Succeeded || rewritten!.Errors.Count > 0)
                {
                    foreach (var site in group) site.Failed = true;
                    analysisErrors.Add($"0x{group.Key:x}: {string.Join(';', lifted.Errors.Concat(rewritten?.Errors ?? []))}");
                    continue;
                }
                guards[group.Key] = rewritten.Guards;
                guardBranches[group.Key] = rewritten.MetadataGuardBranches;
                var statements = lifted.Function.Blocks.SelectMany(b => b.Statements).ToArray();
                var definitions = statements.OfType<Define>().Concat(lifted.Function.Dead.Select(d => d.Define))
                    .GroupBy(d => d.Target).ToDictionary(g => g.Key, g => g.First().Value);
                var phis = statements.OfType<Phi>().ToDictionary(p => p.Target);
                var calls = statements.OfType<Call>().ToLookup(c => c.Address);
                foreach (var site in group)
                {
                    // 公共尾部被复制到几条路径上时，同一个调用点有几份。
                    foreach (var call in calls[site.Reference.Site].Where(c => c.Target is Const target && target.Value == site.Reference.Target))
                    {
                        var sources = call.Arguments.Length - call.Sources.Length;
                        for (var i = sources; i < call.Arguments.Length; i++)
                        {
                            var storage = call.Sources[i - sources];
                            var symbols = Methods(call.Arguments[i], definitions, phis, []);
                            if (symbols.Length == 0) continue;
                            site.Symbols[storage] = [.. site.Symbols.GetValueOrDefault(storage, []).Union(symbols)];
                            if (Expand(call.Arguments[i], definitions, []) is Symbol { Kind: SymbolKind.Method, Value: MethodRef method }
                                && Signature(method)?.MethodInfo?.Register == storage) site.Positive.Add(method);
                        }
                    }
                }
            }
            log?.Invoke($"未注册定义 A 普查完成：{sites.Select(s => s.Caller).Distinct().Count()} 调用者");
        }

        private static Expr Expand(Expr value, Dictionary<Variable, Expr> definitions, HashSet<Variable> path) =>
            value is Use { Width: 64 } use && definitions.TryGetValue(use.Variable, out var next) && path.Add(use.Variable) ? Expand(next, definitions, path) : value;

        private static MethodRef[] Methods(Expr value, Dictionary<Variable, Expr> definitions, Dictionary<Variable, Phi> phis, HashSet<Variable> path)
        {
            if (value is Symbol { Kind: SymbolKind.Method, Value: MethodRef method }) return [method];
            if (value is Select select) return Methods(select.True, definitions, phis, new(path)).Concat(Methods(select.False, definitions, phis, new(path))).Distinct().ToArray();
            if (value is not Use { Width: 64 } use || !path.Add(use.Variable)) return [];
            if (definitions.TryGetValue(use.Variable, out var next)) return Methods(next, definitions, phis, path);
            return phis.TryGetValue(use.Variable, out var phi) ? phi.Sources.SelectMany(v => Methods(new Use(v, 64), definitions, phis, new(path))).Distinct().ToArray() : [];
        }
    }
}
