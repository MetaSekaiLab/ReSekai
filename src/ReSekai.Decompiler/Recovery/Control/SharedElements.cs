using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Managed;
using ReSekai.Decompiler.Model;
using ReSekai.Decompiler.Recovery.Inlining;

namespace ReSekai.Decompiler.Recovery.Control;

/// <summary>
/// 原生代码算一次元素地址（缓冲区首址加下标乘元素大小），之后各字段都经它读写。下降把地址代入每个访问，同一个地址写出几十遍。
/// 反复用到的元素地址放回一个指针局部：在支配全部访问处赋值，地址由只赋值一次的局部、参数和常量算出，不读内存、不会抛出，提前求值和复用都不改变语义。
/// async 和迭代器的 MoveNext 不处理：反编译器按固定形态识别它们。内联证明用的方法体不经过这一步。
/// </summary>
internal static class SharedElements
{
    /// <summary>访问达到这么多次才提取；更少时地址留在访问处更好读。</summary>
    private const int MinimumUses = 6;

    public static ManagedBody Run(ManagedBody body, ManagedTypes types, out int extracted)
    {
        extracted = 0;
        if (body.Method.Method.Name == "MoveNext") return body;
        var statements = body.Blocks.SelectMany(b => b.Statements).ToArray();
        var definitions = new Dictionary<Variable, int>();
        void Define(LocalValue? local) { if (local is not null) definitions[local.Variable] = definitions.GetValueOrDefault(local.Variable) + 1; }
        var addressed = new HashSet<Variable>();
        var written = new HashSet<int>();
        var uses = new Dictionary<IndirectValue, int>();
        foreach (var statement in statements)
        {
            Define((statement as ManagedAssignment)?.Target);
            Define((statement as ManagedCall)?.Result);
            foreach (var copy in ManagedValues.Edges(statement).SelectMany(e => e.Copies)) Define(copy.Target);
            if (statement is ManagedStore { Target: ArgumentValue argument }) written.Add(argument.Index);
            foreach (var value in ManagedValues.Of(statement).Concat(ManagedValues.CopiedValues(statement)).SelectMany(ManagedValues.Walk))
                switch (value)
                {
                    case AddressValue { Target: LocalValue local }: addressed.Add(local.Variable); break;
                    case AddressValue { Target: ArgumentValue taken }: written.Add(taken.Index); break;
                    case IndirectValue { Address: PointerOffsetValue { Type: PointerRef } } element: uses[element] = uses.GetValueOrDefault(element) + 1; break;
                }
        }
        foreach (var region in body.Regions) Define(region.Exception);
        bool Fixed(ManagedValue value) => ManagedValues.Walk(value).All(v => v switch
        {
            LocalValue local => !addressed.Contains(local.Variable) && definitions.GetValueOrDefault(local.Variable) == 1,
            ArgumentValue argument => !written.Contains(argument.Index) && argument.Type is not ByRefRef,
            ConstantValue or SizeOfValue or PointerOffsetValue or UnaryValue => true,
            ConversionValue conversion => conversion.Operand.Type is not ByRefRef,
            BinaryValue { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.And or BinOp.Or or BinOp.Xor or BinOp.Shl or BinOp.LShr or BinOp.AShr } => true,
            _ => false,
        });
        var shared = uses.Where(u => u.Value >= MinimumUses && types.Kind(u.Key.Type) == TypeKind.Struct
            && ManagedValues.Walk(u.Key.Address).Any(v => v is SizeOfValue) && Fixed(u.Key.Address)).Select(u => u.Key).ToArray();
        if (shared.Length == 0) return body;

        var program = Program.Compile(body, types);
        var next = EnumeratorForms.NextLocal(body);
        var order = new Dictionary<ulong, int>();
        int Depth(ulong block)
        {
            var depth = 0;
            for (ulong? at = block; program.Dominator(at.Value) is { } up; at = up) depth++;
            return order[block] = depth;
        }
        ulong Meet(ulong a, ulong b)
        {
            while (a != b)
            {
                var (da, db) = (order.TryGetValue(a, out var x) ? x : Depth(a), order.TryGetValue(b, out var y) ? y : Depth(b));
                if (da >= db) a = program.Dominator(a) ?? a;
                if (db >= da && a != b) b = program.Dominator(b) ?? b;
            }
            return a;
        }
        static bool Mentions(ManagedStatement statement, ManagedValue value) =>
            ManagedValues.Of(statement).Concat(ManagedValues.CopiedValues(statement)).SelectMany(ManagedValues.Walk).Any(value.Equals);
        var handlers = body.Regions.SelectMany(r => r.Handler.Append(r.HandlerEntry)).ToHashSet();
        var blocks = body.Blocks.ToDictionary(b => b.Address, b => b.Statements.ToList());
        foreach (var element in shared)
        {
            var holding = blocks.Where(b => b.Value.Any(s => Mentions(s, element))).Select(b => b.Key).ToArray();
            // 处理块里的访问与受保护的块之间没有普通的支配关系。
            if (holding.Length == 0 || holding.Any(handlers.Contains)) continue;
            var home = holding.Aggregate(Meet);
            if (body.Regions.Any(r => r.Try.Contains(home) && holding.Any(b => !r.Try.Contains(b)))) continue;
            var leaves = ManagedValues.Walk(element.Address).OfType<LocalValue>().Select(l => l.Variable).ToHashSet();
            var list = blocks[home];
            var at = list.FindIndex(s => Mentions(s, element));
            if (at < 0) at = list.Count - 1;
            // 叶子的赋值要在这之前：它支配每个访问，只可能在 home 之后的块里时不提取。
            var settled = leaves.All(leaf =>
            {
                var definition = blocks.Where(b => b.Value.Any(s => Program.Definitions(s).Contains(leaf))).Select(b => b.Key).ToArray();
                if (definition is not [var block]) return false;
                if (block == home) return list.FindIndex(s => Program.Definitions(s).Contains(leaf)) < at;
                for (ulong? up = home; up is { } current; up = program.Dominator(current))
                    if (current == block) return true;
                return false;
            });
            if (!settled) continue;
            var type = new PointerRef(element.Type);
            var pointer = new LocalValue(new Variable(Storage.Temp(next++), 1), type, 64);
            list.Insert(at, new ManagedAssignment(list[at].Address, pointer, element.Address.Type.Equals(type) ? element.Address : new ConversionValue(ConvKind.Bitcast, element.Address, type, 64)));
            var replacement = new IndirectValue(pointer, element.Type, element.Width);
            ManagedValue Replace(ManagedValue value) => value.Equals(element) ? replacement : ManagedValues.Map(value, Replace);
            foreach (var block in holding)
            {
                var target = blocks[block];
                for (var i = 0; i < target.Count; i++)
                    if (!(block == home && i == at) && Mentions(target[i], element)) target[i] = ManagedValues.Map(target[i], Replace, Replace);
            }
            extracted++;
        }
        return extracted == 0 ? body : body with { Blocks = body.Blocks.Select(b => b with { Statements = blocks[b.Address] }).ToArray() };
    }
}
