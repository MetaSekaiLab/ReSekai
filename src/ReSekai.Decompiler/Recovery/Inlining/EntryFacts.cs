using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Recovery.Effects;

namespace ReSekai.Decompiler.Recovery.Inlining;

/// <summary>
/// 入口事实：程序点上成立、可以在该点重新求值的事实（前向必达数据流，汇合处取交集）。
/// 局部等于某个可重算的表达式（字段与元素读取、运算）；某个位置此刻存着某个可重算的值；路径上的分支条件；已解引用的局部非空；已初始化的类型。
/// 写入可能重叠的位置、调用与其他屏障使涉及内存的事实失效；局部被改写使提到它的事实失效；处理块入口没有事实。
/// 内联认回的调用等价于它的模板：模板只读（摘要中没有事件、没有对外可见的写入）时这次调用不是屏障。
/// 这些事实描述证明入口的状态：两侧共同的初始堆与路径条件，以及调用方局部的值。
/// </summary>
internal sealed class EntryFacts
{
    internal sealed class State
    {
        public Dictionary<Variable, ManagedValue> Vars { get; init; } = [];
        public Dictionary<ManagedValue, ManagedValue> Known { get; init; } = [];
        public Dictionary<ManagedValue, bool> Literals { get; init; } = [];
        public HashSet<Variable> NonNull { get; init; } = [];
        public HashSet<TypeDef> Initialized { get; init; } = [];
        public State Copy() => new() { Vars = new(Vars), Known = new(Known), Literals = new(Literals), NonNull = [.. NonNull], Initialized = [.. Initialized] };

        /// <summary>v 等于位置 e 此刻的内容：v 的定义就是 e，或 e 已知存着与 v 相同的值。</summary>
        private bool Holds(Variable variable, ManagedValue location) => Vars.TryGetValue(variable, out var defined) && Holds(defined, location);

        private bool Holds(ManagedValue defined, ManagedValue location) => (defined, location) switch
        {
            (_, ConversionValue converted) when defined is ConversionValue source && source.Kind == converted.Kind && source.Width == converted.Width
                => Holds(source.Operand, converted.Operand),
            (ConstantValue { Bits: 0 }, ConversionValue { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend } extension)
                => Holds(new ConstantValue(0, extension.Operand.Type, extension.Operand.Width), extension.Operand),
            (FieldValue a, FieldValue b) when a.Field.Equals(b.Field) && Same(a.Receiver, b.Receiver) => true,
            _ => defined.Equals(location) || Known.TryGetValue(location, out var content) && content.Equals(defined)
                || location is FieldValue field && Known.Any(pair => pair.Key is FieldValue written
                    && field.Field.Equals(written.Field) && field.Receiver.Equals(written.Receiver) && pair.Value.Equals(defined)),
        };

        /// <summary>两个接收者相等：一个是局部、另一个是它已知的定义时也相等（定义已代入其中一侧）。</summary>
        private bool Same(ManagedValue a, ManagedValue b) => a.Equals(b)
            || a is LocalValue x && Vars.TryGetValue(x.Variable, out var left) && left.Equals(b)
            || b is LocalValue y && Vars.TryGetValue(y.Variable, out var right) && right.Equals(a);

        private static bool IsLocation(ManagedValue? value) => value switch
        {
            FieldValue or StaticFieldValue or ArrayElementValue => true,
            ConversionValue { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend } conversion => IsLocation(conversion.Operand),
            _ => false,
        };

        public bool Narrow(State other)
        {
            var changed = false;
            // 各项按合流前的状态判断，与处理次序无关。
            var before = new State { Vars = new(Vars), Known = new(Known) };
            foreach (var (k, v) in Vars.ToArray())
            {
                if (other.Vars.TryGetValue(k, out var o) && o.Equals(v)) continue;
                // 两侧定义不同，但都等于同一位置的内容：保留为该位置的读取。
                var location = o is null ? null : new[] { o, v }.FirstOrDefault(l => IsLocation(l) && before.Holds(k, l) && other.Holds(k, l));
                if (location is not null)
                {
                    if (!v.Equals(location)) { Vars[k] = location; changed = true; }
                    continue;
                }
                Vars.Remove(k);
                changed = true;
            }
            foreach (var (k, v) in Known.ToArray())
                if (!other.Known.TryGetValue(k, out var o) || !o.Equals(v)) { Known.Remove(k); changed = true; }
            foreach (var (k, v) in Literals.ToArray())
                if (!other.Literals.TryGetValue(k, out var o) || o != v) { Literals.Remove(k); changed = true; }
            changed |= NonNull.RemoveWhere(v => !other.NonNull.Contains(v)) > 0;
            changed |= Initialized.RemoveWhere(t => !other.Initialized.Contains(t)) > 0;
            return changed;
        }
    }

    private readonly Program program;
    private readonly Func<ManagedCall, bool>? readOnly;
    private readonly Func<ManagedCall, (FieldRef Field, ulong Bits, int Width)[]>? constantWrites;
    private readonly Func<DefinitionRef, IEnumerable<ValueProjection>>? leaves;
    private readonly Dictionary<ulong, State> entries = [];

    /// <summary>不动点迭代处理的块数（诊断）。</summary>
    public int Visits { get; }

    public EntryFacts(Program program, Func<ManagedCall, bool>? readOnly = null,
        Func<ManagedCall, (FieldRef Field, ulong Bits, int Width)[]>? constantWrites = null, Func<DefinitionRef, IEnumerable<ValueProjection>>? leaves = null)
    {
        this.leaves = leaves;
        this.program = program;
        this.readOnly = readOnly;
        this.constantWrites = constantWrites;
        var body = program.Body;
        var handlers = body.Regions.Select(r => r.HandlerEntry).Where(program.Blocks.ContainsKey).ToHashSet();
        var roots = new List<ulong> { body.Blocks[0].Address };
        roots.AddRange(handlers.Where(h => h != body.Blocks[0].Address));
        var singleIncoming = body.Blocks.SelectMany(b => b.Statements.TakeLast(1)).SelectMany(Outgoing)
            .GroupBy(o => o.Edge.Target).Where(g => g.Count() == 1).Select(g => g.Key).Except(roots).ToHashSet();
        var rank = ReversePostorder(roots);
        // 按逆后序取块、队列去重：前驱大多先于后继处理，合流处的取交次数少。
        var pending = new SortedSet<(int Rank, ulong Block)>();
        void Enqueue(ulong block) => pending.Add((rank.GetValueOrDefault(block, int.MaxValue), block));
        var start = new State();
        if (body.Method.DeclaringType is DefinitionRef owner) start.Initialized.Add(owner.Definition);
        entries[body.Blocks[0].Address] = start;
        foreach (var handler in roots.Skip(1)) entries[handler] = new State();
        foreach (var root in roots) Enqueue(root);
        var limit = 64 * program.Blocks.Count + 1000;
        var visits = 0;
        while (pending.Count > 0 && visits++ < limit)
        {
            var next = pending.Min;
            pending.Remove(next);
            var address = next.Block;
            var facts = entries[address].Copy();
            var statements = program.Blocks[address].Statements;
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];
                if (i == statements.Count - 1)
                    foreach (var (edge, literal) in Outgoing(statement))
                    {
                        if (handlers.Contains(edge.Target)) continue;
                        var arriving = facts.Copy();
                        if (literal is var (condition, value) && Evaluable(condition)) arriving.Literals[condition] = value;
                        Copies(edge, arriving);
                        // 唯一入边重新计算后直接替换旧状态。旧迭代的表达式可能已经被更简洁的等价定义取代。
                        if (singleIncoming.Contains(edge.Target) || !entries.TryGetValue(edge.Target, out var known))
                        {
                            entries[edge.Target] = arriving;
                            Enqueue(edge.Target);
                        }
                        else if (known.Narrow(arriving)) Enqueue(edge.Target);
                    }
                else Apply(statement, facts);
            }
        }
        // 没有到达不动点时，已有的事实不一定在所有路径上成立：全部作废。
        Visits = visits;
        if (pending.Count > 0)
            foreach (var address in entries.Keys.ToArray()) entries[address] = new State();
    }

    /// <summary>从各根出发的逆后序编号。</summary>
    private Dictionary<ulong, int> ReversePostorder(IReadOnlyList<ulong> roots)
    {
        var post = new List<ulong>();
        var visited = new HashSet<ulong>();
        foreach (var root in roots)
        {
            if (!visited.Add(root)) continue;
            var stack = new Stack<(ulong Block, IEnumerator<ulong> Next)>();
            stack.Push((root, Successors(root).GetEnumerator()));
            while (stack.Count > 0)
            {
                var (block, successors) = stack.Peek();
                if (successors.MoveNext())
                {
                    var target = successors.Current;
                    if (program.Blocks.ContainsKey(target) && visited.Add(target)) stack.Push((target, Successors(target).GetEnumerator()));
                    continue;
                }
                stack.Pop();
                post.Add(block);
            }
        }
        var rank = new Dictionary<ulong, int>();
        for (var i = 0; i < post.Count; i++) rank[post[post.Count - 1 - i]] = i;
        return rank;

        IEnumerable<ulong> Successors(ulong block) => program.Blocks[block].Statements is [.., var last] ? Outgoing(last).Select(o => o.Edge.Target) : [];
    }

    private static IEnumerable<(ManagedEdge Edge, (ManagedValue, bool)? Literal)> Outgoing(ManagedStatement statement) => statement switch
    {
        ManagedBranch b => [(b.Taken, (b.Condition, true)), (b.Fallthrough, (b.Condition, false))],
        _ => ManagedValues.Edges(statement).Select(e => (e, ((ManagedValue, bool)?)null)),
    };

    /// <summary>第 index 条语句之前的事实。</summary>
    public State At(ulong block, int index)
    {
        if (!entries.TryGetValue(block, out var entry)) return new State();
        var facts = entry.Copy();
        foreach (var statement in program.Blocks[block].Statements.Take(index)) Apply(statement, facts);
        return facts;
    }

    // ---- 转移 ----

    /// <summary>可在程序点重新求值、没有副作用的表达式（读取视为可重算：之前已求值过而未抛出）。</summary>
    public static bool Evaluable(ManagedValue value) => value switch
    {
        LocalValue or ArgumentValue or ConstantValue or StringValue or TypeHandleValue or SizeOfValue or FieldHandleValue or MethodHandleValue => true,
        // 经已证明访问器的读取就是字段读取（访问器的证明正是这一点）。
        FieldValue f => Evaluable(f.Receiver),
        StaticFieldValue => true,
        ArrayElementValue e => Evaluable(e.Array) && Evaluable(e.Index),
        ArrayLengthValue a => Evaluable(a.Array),
        StringLengthValue s => Evaluable(s.String),
        AddressValue a => Evaluable(a.Target),
        IndirectValue i => Evaluable(i.Address),
        BinaryValue { Op: not (BinOp.SDiv or BinOp.UDiv or BinOp.SRem or BinOp.URem) } b => Evaluable(b.Left) && Evaluable(b.Right),
        UnaryValue u => Evaluable(u.Operand),
        ConversionValue c => Evaluable(c.Operand),
        ComparisonValue c => Evaluable(c.Left) && Evaluable(c.Right),
        BooleanValue b => Evaluable(b.Left) && Evaluable(b.Right),
        NegatedValue n => Evaluable(n.Operand),
        ChoiceValue c => Evaluable(c.Condition) && Evaluable(c.True) && Evaluable(c.False),
        IsInstValue i => Evaluable(i.Operand),
        _ => false,
    };

    private static bool Mentions(ManagedValue value, Variable variable) =>
        ManagedValues.Walk(value).Any(v => v is LocalValue l && l.Variable.Equals(variable));

    /// <summary>表达式中读内存（随写入与屏障变化）的子值；长度不变。</summary>
    private IEnumerable<ManagedValue> MemoryReads(ManagedValue value) => ManagedValues.Walk(value).Where(v => v switch
    {
        FieldValue f => !LocalRooted(f.Receiver),
        StaticFieldValue or ArrayElementValue or IndirectValue => true,
        _ => false,
    });

    private bool LocalRooted(ManagedValue value) => value switch
    {
        LocalValue local => !program.AddressTaken.Contains(local.Variable) && local.Type is not (ByRefRef or PointerRef) && !ManagedTypes.IsReferenceType(local.Type),
        FieldValue f => !ManagedTypes.IsReferenceType(f.Receiver.Type) && f.Receiver.Type is not (ByRefRef or PointerRef) && LocalRooted(f.Receiver),
        _ => false,
    };

    private bool ReadsMemory(ManagedValue value) => MemoryReads(value).Any();

    private static bool Clobbers(ManagedValue target, ManagedValue read) =>
        MemoryFootprint.MayAlias(MemoryFootprint.Of(target), MemoryFootprint.Of(read));

    private void Kill(State facts, Func<ManagedValue, bool> stale)
    {
        foreach (var (k, v) in facts.Vars.ToArray()) if (stale(v)) facts.Vars.Remove(k);
        foreach (var (k, v) in facts.Known.ToArray()) if (stale(k) || stale(v)) facts.Known.Remove(k);
        foreach (var k in facts.Literals.Keys.ToArray()) if (stale(k)) facts.Literals.Remove(k);
    }

    private void Redefine(State facts, Variable variable)
    {
        facts.Vars.Remove(variable);
        facts.NonNull.Remove(variable);
        Kill(facts, v => Mentions(v, variable));
    }

    private void Barrier(State facts) => Kill(facts, ReadsMemory);

    /// <summary>语句执行之后：解引用过的局部非空，访问过的静态字段的类型已初始化。</summary>
    private static void Dereferenced(ManagedStatement statement, State facts)
    {
        foreach (var value in ManagedValues.Of(statement).SelectMany(ManagedValues.Walk))
        {
            var receiver = value switch
            {
                FieldValue f when ManagedTypes.IsReferenceType(f.Receiver.Type) => f.Receiver,
                ArrayElementValue e => e.Array,
                ArrayLengthValue a => a.Array,
                StringLengthValue s => s.String,
                _ => null,
            };
            if (receiver is LocalValue local) facts.NonNull.Add(local.Variable);
            if (value is StaticFieldValue s2 && s2.Field.DeclaringType is DefinitionRef owner) facts.Initialized.Add(owner.Definition);
        }
        if (statement is ManagedCall { Invocation: { Virtual: true, Receiver: LocalValue called } }) facts.NonNull.Add(called.Variable);
        if (statement is ManagedCall { Invocation: { Receiver: null, Method: var method } } && method.DeclaringType is DefinitionRef type) facts.Initialized.Add(type.Definition);
    }

    private static bool HasEvent(ManagedValue value) =>
        ManagedValues.Walk(value).Any(v => v is StaticCallValue or NewObjectValue { Transparent: false } or NewObjectValue { Transparent: true });

    private void Apply(ManagedStatement statement, State facts)
    {
        switch (statement)
        {
            case ManagedAssignment a:
                {
                    if (HasEvent(a.Value)) Barrier(facts);
                    Dereferenced(statement, facts);
                    var variable = a.Target.Variable;
                    var value = Propagate(a.Value, facts);
                    Redefine(facts, variable);
                    if (program.AddressTaken.Contains(variable))
                    {
                        CopiedStruct(a, facts);
                        break;
                    }
                    if (Evaluable(value) && !Mentions(value, variable)) facts.Vars[variable] = value;
                    if (a.Value is NewObjectValue or NewArrayValue or BoxValue or DelegateValue or StringValue) facts.NonNull.Add(variable);
                    break;
                }
            case ManagedStore s:
                {
                    if (HasEvent(s.Value)) Barrier(facts);
                    Dereferenced(statement, facts);
                    if (s.Target is LocalValue || s.Target is FieldValue f0 && LocalRooted(f0))
                    {
                        // 结构体局部的字段：局部被改写。
                        var root = ManagedValues.Walk(s.Target).OfType<LocalValue>().LastOrDefault();
                        if (root is not null) Redefine(facts, root.Variable);
                        break;
                    }
                    Kill(facts, v => MemoryReads(v).Any(read => Clobbers(s.Target, read)));
                    foreach (var key in facts.Known.Keys.Where(k => Clobbers(s.Target, k)).ToArray()) facts.Known.Remove(key);
                    if (Evaluable(s.Target) && Evaluable(s.Value) && s.Target is not IndirectValue) facts.Known[OfLocal(s.Target) ? Plain(s.Target) : s.Target] = s.Value;
                    break;
                }
            case ManagedCall call:
                {
                    if (!(call.Inlined && readOnly?.Invoke(call) == true)) Barrier(facts);
                    Dereferenced(statement, facts);
                    if (call.Result is { } result) Redefine(facts, result.Variable);
                    // 取址交出的局部可能被改写。
                    foreach (var local in ManagedValues.Of(call.Invocation).SelectMany(ManagedValues.Walk).OfType<AddressValue>().Select(a => a.Target).OfType<LocalValue>())
                        Redefine(facts, local.Variable);
                    if (call is { Inlined: true, Invocation.Receiver: LocalValue or ArgumentValue } && constantWrites is not null)
                        foreach (var (field, bits, width) in constantWrites(call))
                        {
                            var type = field.Field.Type.Substitute(((DefinitionRef)field.DeclaringType).Arguments, []);
                            facts.Known[new FieldValue(call.Invocation.Receiver!, field, type, width)] = new ConstantValue(bits, type, width);
                        }
                    break;
                }
            default:
                Dereferenced(statement, facts);
                break;
        }
    }

    private void Copies(ManagedEdge edge, State facts)
    {
        var values = edge.Copies.Select(c => Propagate(c.Value, facts)).ToArray();
        // 取址的结构体局部的字段存着正要合流的值：合流之后它存着合流的局部（循环里逐轮推进的状态）。
        var held = facts.Known.Where(k => OfLocal(k.Key)).Select(k => (k.Key, Value: Plain(k.Value))).ToArray();
        var carried = edge.Copies.Select((c, i) => (Target: c.Target, Values: new[] { Plain(c.Value), Plain(values[i]) })).ToArray();
        foreach (var copy in edge.Copies) Redefine(facts, copy.Target.Variable);
        foreach (var (key, value) in held)
            if (carried.FirstOrDefault(c => c.Values.Contains(value) && !program.AddressTaken.Contains(c.Target.Variable)) is { Target: { } joined }
                && !edge.Copies.Any(c => Mentions(key, c.Target.Variable))) facts.Known[key] = joined;
        for (var i = 0; i < values.Length; i++)
        {
            var target = edge.Copies[i].Target.Variable;
            if (!program.AddressTaken.Contains(target) && Evaluable(values[i]) && edge.Copies.All(c => !Mentions(values[i], c.Target.Variable)))
                facts.Vars[target] = values[i];
        }
    }

    /// <summary>取址的结构体局部整体复制自另一个局部：各叶子字段存着来源的对应字段。</summary>
    private void CopiedStruct(ManagedAssignment assignment, State facts)
    {
        if (leaves is null || assignment.Value is not LocalValue source || program.AddressTaken.Contains(source.Variable)
            || assignment.Target.Type is not DefinitionRef structure || !structure.Equals(source.Type) || !structure.Definition.IsValueType) return;
        var target = new AddressValue(assignment.Target);
        foreach (var leaf in leaves(structure))
        {
            ManagedValue Nested(ManagedValue receiver) => leaf.Path.Select((field, depth) => (Field: field, Last: depth == leaf.Path.Count - 1))
                .Aggregate(receiver, (value, step) => new FieldValue(value, step.Field, StorageType(step.Field), step.Last ? leaf.Width : 0));
            facts.Known[Nested(target)] = Nested(source);
        }
    }

    private static TypeRef StorageType(FieldRef field) => field.DeclaringType is DefinitionRef owner ? field.Field.Type.Substitute(owner.Arguments, []) : field.Field.Type;

    /// <summary>取址的局部里的字段（经地址访问）。</summary>
    private static bool OfLocal(ManagedValue location) => location is FieldValue field && (field.Receiver is AddressValue { Target: LocalValue } || OfLocal(field.Receiver));

    /// <summary>去掉访问方式的标注，只留位置。</summary>
    private static ManagedValue Plain(ManagedValue value) => value is FieldValue field ? new FieldValue(Plain(field.Receiver), field.Field, field.Type, field.Width)
        : ManagedValues.Map(value, Plain);

    /// <summary>
    /// 复制传播：值里读到的局部换成它已知的定义（常量、实参、其他局部或小表达式），事实就不再随这个局部的重新赋值失效。
    /// 代入后的表达式此刻与原值相等；其中的内存读取照常由写入与屏障失效。
    /// </summary>
    private ManagedValue Propagate(ManagedValue value, State facts)
    {
        ManagedValue Visit(ManagedValue v) => v is LocalValue local && facts.Vars.TryGetValue(local.Variable, out var known)
            && (known is ConstantValue or ArgumentValue or StringValue or LocalValue || ManagedValues.Walk(known).Count() <= 6)
            ? known : ManagedValues.Map(v, Visit);
        return Visit(value);
    }
}
