using System.Reflection;
using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    private readonly Dictionary<(ulong Entry, DefinitionRef Receiver), MethodRef?> localCopies = [];
    private MethodDecompiler? copyProof;
    private bool provingCopy;

    private IEnumerable<(CallSite Site, MethodRef Method)> FindLocalCopies(
        MachineFunction function, ValueModel values, TypeOracle oracle, MethodRef caller)
    {
        if (provingCopy) yield break;
        foreach (var call in function.Blocks.SelectMany(b => b.Statements).OfType<Call>())
        {
            if (call is not { Resolved: null, Op: null, Kind: CallKind.Direct, Target: Const target }
                || facts.Code.At(target.Value) is not { Region: CodeRegion.Managed, ExceptionRegions.Length: 0, Size: <= 1024 } copy
                || copy.Start != target.Value || shapes.Of(call).Signature is not null) continue;
            var receiver = Array.IndexOf(call.Sources, Storage.Gpr(0));
            if (receiver < 0) continue;
            var input = values.Operands(call)[receiver + 1];
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
        };
    }
}
