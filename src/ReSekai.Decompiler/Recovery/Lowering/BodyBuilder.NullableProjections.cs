using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Lowering;

internal sealed partial class BodyBuilder
{
    private readonly Dictionary<ManagedValue, ulong> nullableProjections = new(ReferenceEqualityComparer.Instance);

    /// <summary>标量替换后只定义了 Nullable 的 HasValue 位。暂按零构造，随后证明未定义的有效载荷不可观察。</summary>
    private ManagedValue? PartialNullable(DefinitionRef structure, AggregatePlan plan, ulong address)
    {
        if (structure.Definition.FullName != "System.Nullable"
            || plan.Fields is not [var present, var payload]
            || present.Projection.Path is not [{ Field.Name: "hasValue" } flag]
            || payload.Projection.Path is not [{ Field.Name: "value" }]
            || SymTable.Resolve(payload.Value) is not UndefSym)
            return null;
        var condition = View(present.Value, types.Boolean, address);
        if (!condition.Type.Equals(types.Boolean) && types.Kind(condition.Type) == TypeKind.Int)
            condition = new ComparisonValue(Cond.Ne, condition, new ConstantValue(0, condition.Type, condition.Width), types.Boolean);
        if (!condition.Type.Equals(types.Boolean)
            || TryCompose(structure, [(new[] { flag }, condition)], address) is not { } composed) return null;
        nullableProjections.TryAdd(composed, address);
        return composed;
    }

    /// <summary>未定义的值只能经局部复制传播，传播后的值只允许读取 HasValue。观察到任何其他用法则保留恢复缺口。</summary>
    private ManagedBody VerifyNullableProjections(ManagedBody body)
    {
        if (nullableProjections.Count == 0) return body;
        var statements = body.Blocks.SelectMany(b => b.Statements).ToArray();
        var copies = statements.OfType<ManagedAssignment>().Select(a => new ManagedCopy(a.Target, a.Value))
            .Concat(statements.SelectMany(ManagedValues.Edges).SelectMany(e => e.Copies)).ToArray();
        var flows = copies.Where(c => c.Value is LocalValue).ToLookup(c => ((LocalValue)c.Value).Variable, c => c.Target.Variable);
        var affected = copies.Where(c => nullableProjections.ContainsKey(c.Value)).Select(c => c.Target.Variable).ToHashSet();
        var pending = new Queue<Variable>(affected);
        while (pending.TryDequeue(out var source))
            foreach (var target in flows[source])
                if (affected.Add(target)) pending.Enqueue(target);

        bool AffectedLocal(ManagedValue value) => value is LocalValue local && affected.Contains(local.Variable);
        bool Observed(ManagedValue value)
        {
            if (value is FieldValue { Field.Field: { Name: "hasValue", Owner.FullName: "System.Nullable" }, Receiver: var receiver }
                && (AffectedLocal(receiver) || receiver is AddressValue address && AffectedLocal(address.Target))) return false;
            return AffectedLocal(value) || nullableProjections.ContainsKey(value) || ManagedValues.Children(value).Any(Observed);
        }
        bool ObservedCopy(ManagedValue value) => nullableProjections.ContainsKey(value)
            ? ManagedValues.Children(value).Any(Observed) : value is not LocalValue && Observed(value);
        bool ObservedStatement(ManagedStatement statement) => statement switch
        {
            ManagedAssignment a => ObservedCopy(a.Value),
            ManagedStore s => ManagedValues.Walk(s.Target).Any(AffectedLocal) || Observed(s.Value),
            _ => ManagedValues.Of(statement).Any(Observed),
        };
        if (!statements.Any(ObservedStatement) && !copies.Any(c => ObservedCopy(c.Value))) return body;

        ManagedValue RestoreHole(ManagedValue value) => nullableProjections.TryGetValue(value, out var address)
            ? Hole(address, "值 Nullable 的未定义有效载荷可能被读取", value.Type)
            : ManagedValues.Map(value, RestoreHole);
        return ManagedValues.Map(body, RestoreHole);
    }
}
