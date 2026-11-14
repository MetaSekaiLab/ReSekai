using System.Reflection;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Lifting;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private readonly Dictionary<(ulong Entry, DefinitionRef Receiver), MethodRef?> localCopies = [];
    private readonly Dictionary<(ulong Entry, string Assembly), MethodRef?> localStaticGetters = [];
    private readonly Dictionary<(ulong Entry, string Assembly), MethodRef?> localStaticCopies = [];
    private MethodDecompiler? copyProof;
    private bool provingCopy;

    private IEnumerable<(CallSite Site, MethodRef Method)> FindLocalCopies(
        MachineFunction function, ValueModel values, TypeOracle oracle, MethodRef caller)
    {
        if (provingCopy) yield break;
        foreach (var call in function.Blocks.SelectMany(b => b.Statements).OfType<Call>())
        {
            if (call is not { Resolved: null, Op: null, Kind: CallKind.Direct, Target: Const target }
                || facts.Code.At(target.Value) is not { Region: CodeRegion.Managed, Size: <= 1024 } copy
                || copy.Start != target.Value || shapes.Of(call).Signature is not null) continue;
            var receiver = Array.IndexOf(call.Sources, Storage.Gpr(0));
            if (receiver < 0) continue;
            var input = values.Operands(call)[receiver + 1];
            if (copy.ExceptionRegions.Length != 0)
            {
                if (SymTable.IsZero(input) && LocalStaticCopy(copy, caller.Method.Owner.Assembly) is { } candidate
                    && ManagedAccess.Method(types, caller.Method.Owner, candidate, null)) yield return (call.Site, candidate);
                continue;
            }
            if (copy.Size <= 128 && SymTable.IsZero(input) && LocalStaticGetter(copy, caller.Method.Owner) is { } getter
                && ManagedAccess.Method(types, caller.Method.Owner, getter, null))
            {
                yield return (call.Site, getter);
                continue;
            }
            if (SymTable.Resolve(input) is LocalSym && IdenticalValueTypeThunk(copy, caller) is { } thunk)
            {
                yield return (call.Site, thunk);
                continue;
            }
            if (oracle.TypeOf(input) is not DefinitionRef type || !ManagedTypes.IsReferenceType(type)) continue;
            if (LocalCopy(copy, type) is { } method && ManagedAccess.Method(types, caller.Method.Owner, method, type))
                yield return (call.Site, method);
        }
    }

    /// <summary>无参静态方法的本地副本：正常路径和异常处理均须与唯一注册方法一致。</summary>
    private MethodRef? LocalStaticCopy(CodeFunction copy, string assembly)
    {
        var key = (copy.Start, assembly);
        if (localStaticCopies.TryGetValue(key, out var known)) return known;
        return localStaticCopies[key] = Find();

        MethodRef? Find()
        {
            if (copy.Size < 128 || copy.ExceptionRegions.Length > 12) return null;
            var calls = DirectCalls(copy);
            if (calls.Length < 2) return null;
            var candidates = facts.Model.Types.Where(t => t.Assembly == assembly && t.GenericParameters.Count == 0)
                .SelectMany(t => t.Methods).Where(m => m.IsStatic && m.GenericParameters.Count == 0 && m.Parameters.Count == 0
                    && (m.ImplAttributes & MethodImplAttributes.AggressiveInlining) != 0 && ManagedTypes.IsReferenceType(m.ReturnType)
                    && facts.Code.At(m.Address) is { Region: CodeRegion.Managed, ExceptionRegions.Length: > 0 } original && original.Start == m.Address
                    && facts.EntriesAt(m.Address).Any(e => e.Kind == EntryKind.Method && e.Method?.Method == m)
                    && original.Size <= copy.Size + 64 && copy.Size <= original.Size + 64 && calls.SequenceEqual(DirectCalls(original)))
                .Select(MethodRef.Open).Take(9).ToArray();
            if (candidates.Length is 0 or > 8) return null;
            copyProof ??= new MethodDecompiler(facts, types) { provingCopy = true };
            var matches = candidates.Where(method =>
                copyProof.RecoverInstance(method, method.Method.Address) is { Body: { Regions.Count: > 0 } a, Holes.Count: 0 }
                && copyProof.RecoverInstance(method, copy.Start) is { Body: { Regions.Count: > 0 } b, Holes.Count: 0 }
                && ManagedStructure.Same(NumberBlocks(a), NumberBlocks(b)) && SameNativeCleanup(copy, facts.Code.At(method.Method.Address)!)).ToArray();
            return matches is [var match] ? match : null;
        }

        ulong[] DirectCalls(CodeFunction function) => facts.Scan.Of(facts.Code.EntryIndex(function.Start)).ToArray()
            .Where(r => r.Kind == RefKind.Call && facts.EntriesAt(r.Target).Any(e => e.Kind is EntryKind.Method or EntryKind.GenericMethod))
            .Select(r => r.Target).ToArray();
    }

    private bool SameNativeCleanup(CodeFunction left, CodeFunction right)
    {
        // 托管区域已逐块比较；额外比对原生展开清理中可能被下降省略的调用、处理动作与落地路径。
        return CleanupShape(left).SequenceEqual(CleanupShape(right));

        IEnumerable<(RefKind Kind, ulong Target, string Action, int Landing)> CleanupShape(CodeFunction function)
        {
            var landings = function.ExceptionRegions.Select(e => e.LandingPad).Where(a => a != 0).Distinct().Order().ToArray();
            foreach (var reference in facts.Scan.Of(facts.Code.EntryIndex(function.Start)).ToArray()
                .Where(r => r.Kind is RefKind.Call or RefKind.TailCall && facts.Helpers.At(r.Target)?.Kind != HelperKind.InitMetadata))
            {
                var region = function.ExceptionRegions.FirstOrDefault(e => e.Start <= reference.Site && reference.Site < e.End);
                var landing = region is { LandingPad: > 0 } ? Array.IndexOf(landings, region.LandingPad) : -1;
                yield return (reference.Kind, reference.Target, string.Join(",", region?.Actions.Select(a => $"{a.Kind}:{a.Filter}:{a.TypeInfo:x}") ?? []), landing);
            }
        }
    }

    private MethodRef? LocalStaticGetter(CodeFunction copy, TypeDef caller)
    {
        var key = (copy.Start, caller.Assembly);
        if (localStaticGetters.TryGetValue(key, out var known)) return known;
        return localStaticGetters[key] = Find();

        MethodRef? Find()
        {
            // 读取 getter 的原生候选必须引用同一 metadata 槽。这样无需把副本与程序集里每个无参方法比较。
            var references = facts.Scan.Of(facts.Code.EntryIndex(copy.Start)).ToArray().Where(r => r.Kind == RefKind.Call).ToArray();
            if (references.Length != 2 || references.Count(r => facts.Helpers.At(r.Target)?.Kind == HelperKind.InitMetadata) != 1
                || references.Count(r => facts.Helpers.At(r.Target)?.Kind == HelperKind.ClassInit) != 1) return null;
            copyProof ??= new MethodDecompiler(facts, types) { provingCopy = true };
            var probe = new FunctionLifter(facts).Lift(copy.Start, null, [new ReturnPiece(0, new Read(Storage.Gpr(0), 32))], simplify: true, promoteFrameSlots: false);
            if (!probe.Succeeded || rewriter.Run(probe.Function).Errors.Count != 0) return null;
            var owners = probe.Function.Blocks.SelectMany(b => b.Statements).SelectMany(ExprMap.Operands).SelectMany(ExprMap.Walk)
                .OfType<Symbol>().Where(s => s.Kind == SymbolKind.StaticFields).Select(s => s.Value).OfType<DefinitionRef>().Distinct().ToArray();
            if (owners is not [var owner] || owner.Arguments.Count != 0 || owner.Definition.Assembly != caller.Assembly) return null;
            var matches = owner.Definition.Properties.Select(p => p.Getter).OfType<MethodDef>().Where(m => m.IsStatic && m.Parameters.Count == 0
                    && m.ReturnType.Equals(types.Int32) && m.GenericParameters.Count == 0 && m.Address != 0)
                .Select(MethodRef.Open).Where(Equivalent).ToArray();
            return matches is [var match] ? match : null;
        }

        bool Equivalent(MethodRef method)
        {
            var original = copyProof!.RecoverInstance(method, method.Method.Address);
            if (original is not { Body.Regions.Count: 0, Body.Blocks: var blocks, Holes.Count: 0 }
                || blocks.SelectMany(b => b.Statements).Where(s => s is not ManagedJump { Edge.Copies.Count: 0 }).ToArray() is not
                    [ManagedAssignment { Target: var local, Value: StaticFieldValue }, ManagedReturn { Value: LocalValue returned }]
                || local != returned) return false;
            return copyProof.RecoverInstance(method, copy.Start) is { Body: { Regions.Count: 0 } body, Holes.Count: 0 }
                && ManagedStructure.Same(NumberBlocks(original.Body), NumberBlocks(body));
        }
    }

    private MethodRef? IdenticalValueTypeThunk(CodeFunction copy, MethodRef caller)
    {
        if (ZeroingTail(copy) is not { } pattern) return null;
        var matching = caller.Method.Parameters.Select(p => caller.Concrete(p.Type)).OfType<DefinitionRef>()
            .Where(type => type.Definition.IsValueType && type.Arguments.Count == 0).Distinct()
            .SelectMany(type => type.Definition.Methods.Where(m => !m.IsStatic && m.Parameters.Count == 0 && m.GenericParameters.Count == 0)
                .Select(MethodRef.Open))
            .Where(method => facts.EntriesAt(method.Method.Address).Any(e => e.Kind == EntryKind.Method && Equals(e.Method, method))
                && facts.Code.At(method.Method.Address) is { Region: CodeRegion.Managed, ExceptionRegions.Length: 0 } original
                && original.Start == method.Method.Address && ZeroingTail(original) == pattern
                && ManagedAccess.Method(types, caller.Method.Owner, method, method.DeclaringType))
            .Distinct().ToArray();
        return matching is [var result] ? result : null;
    }

    private (ulong Target, int Registers)? ZeroingTail(CodeFunction function)
    {
        // 本地短入口与注册入口必须保留同一个 this、清零同一组寄存器并跳到同一目标。
        if (function.Size is < 8 or > 32 || function.Size % 4 != 0) return null;
        var end = function.End - 4;
        var count = 0;
        for (var pc = function.Start; pc < end; pc += 4)
        {
            var register = (uint)++count;
            if (facts.Code.Word(pc) != (0xAA1F03E0u | register)) return null; // mov xN, xzr
        }
        if (facts.Code.Word(end) is not { } jump || !A64.IsB(jump)) return null;
        var target = A64.Imm26Target(jump, end);
        return target != function.Start && facts.Code.IsEntry(target) && facts.Code.RegionOf(target) == CodeRegion.Managed ? (target, count) : null;
    }

    private MethodRef? LocalCopy(CodeFunction copy, DefinitionRef receiver)
    {
        var key = (copy.Start, receiver);
        if (localCopies.TryGetValue(key, out var known)) return known;
        MethodRef? match = null;
        for (TypeRef? current = receiver; current is DefinitionRef type; current = type.Definition.BaseType?.Substitute(type.Arguments, []))
        {
            if (type.Arguments.Count != 0) continue;
            foreach (var method in type.Definition.Methods)
            {
                if (method.IsStatic || method.IsVirtual || method.GenericParameters.Count != 0
                    || (method.ImplAttributes & MethodImplAttributes.AggressiveInlining) == 0
                    || types.Kind(method.ReturnType) != TypeKind.Void
                    || facts.Code.At(method.Address) is not { Region: CodeRegion.Managed, ExceptionRegions.Length: 0 } original
                    || original.Start != method.Address || original.Size > copy.Size + 64 || copy.Size > original.Size + 64) continue;
                var instance = MethodRef.Open(method);
                // 副本与原入口按同一签名恢复，比较完整控制流和语句，只忽略机器地址。
                copyProof ??= new MethodDecompiler(facts, types) { provingCopy = true };
                if (copyProof.RecoverInstance(instance, original.Start) is not { Body: { Regions.Count: 0 } a, Holes.Count: 0 }
                    || copyProof.RecoverInstance(instance, copy.Start) is not { Body: { Regions.Count: 0 } b, Holes.Count: 0 }
                    || !ManagedStructure.Same(NumberBlocks(a), NumberBlocks(b))) continue;
                if (match is not null) return localCopies[key] = null;
                match = instance;
            }
        }
        return localCopies[key] = match;
    }

    private static ManagedBody NumberBlocks(ManagedBody body)
    {
        var addresses = body.Blocks.Select((block, index) => (block.Address, index)).ToDictionary(p => p.Address, p => (ulong)p.index);
        return body with
        {
            Blocks = body.Blocks.Select(block => new ManagedBlock(addresses[block.Address], block.Statements
                .Select(statement => ManagedValues.MapEdges(statement with { Address = 0 }, edge => edge with { Target = addresses[edge.Target] }))
                .ToArray())).ToArray(),
            Regions = body.Regions.Select(region => region with
            {
                Try = region.Try.Select(address => addresses[address]).ToArray(),
                Handler = region.Handler.Select(address => addresses[address]).ToArray(),
                HandlerEntry = addresses[region.HandlerEntry],
            }).ToArray(),
        };
    }
}
