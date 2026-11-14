using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

/// <summary>把类型合流后的分配与无参构造移入必经分支，保留对象值的合流。</summary>
internal static class ClassPhiAllocations
{
    public static bool Run(MachineFunction function, Func<ulong, bool> constructor, Func<Variable> fresh)
    {
        var definitions = function.Blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        (Variable Variable, Expr Value)? ClassValue(Variable variable)
        {
            var seen = new HashSet<Variable>();
            while (seen.Add(variable) && definitions.TryGetValue(variable, out var value))
            {
                if (Classes(value)) return (variable, value);
                if (value is not Use copy) break;
                variable = copy.Variable;
            }
            return null;
        }
        var changed = false;
        foreach (var block in function.Blocks.ToArray())
        {
            if (block.IsLandingPad || block.Statements.OfType<Call>().FirstOrDefault(c => c.Op is { Kind: OpKind.NewObject, Operand: null }) is not Call
                {
                    Kind: CallKind.Direct, Op: { Kind: OpKind.NewObject, Operand: null },
                    Arguments: [{ Width: 64 } pointer], Defines: [var created]
                } allocation) continue;
            var following = block.Statements.Skip(block.Statements.IndexOf(allocation) + 1).FirstOrDefault(s => s is not Nop);
            if (following is not Call
                {
                    Kind: CallKind.Direct, Op: null, Target: Const target, Defines.Length: 0,
                    Arguments: [Use receiver, Const { Value: 0 }]
                } ctor
                || receiver.Variable != created || !constructor(target.Value)) continue;
            if (pointer is Use selectorUse && ClassValue(selectorUse.Variable) is (var selector, Select choice)
                && SplitChoice(function, block, allocation, ctor, receiver, created, selector, choice, fresh))
                return true;
            if (pointer is Select inlined && Classes(inlined)
                && SplitChoice(function, block, allocation, ctor, receiver, created, null, inlined, fresh)) return true;
            if (block.Statements.First(s => s is not (Phi or Nop)) != allocation
                || pointer is not Use use || block.Statements.OfType<Phi>().FirstOrDefault(p => p.Target == use.Variable) is not { } phi
                || phi.Sources.Length != block.Predecessors.Count || block.Predecessors.Count < 2
                || block.Predecessors.Any(p => p == block || p.Handler != block.Handler
                    || p.NormalSuccessors.Count() != 1 || !p.NormalSuccessors.Contains(block))) continue;
            var classes = phi.Sources.Select(ClassValue).ToArray();
            if (classes.Any(c => c is null)) continue;
            var sources = new Variable[classes.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                var parent = block.Predecessors[i];
                sources[i] = fresh();
                var at = parent.Statements.Count - (parent.Statements.LastOrDefault() is Jump ? 1 : 0);
                var selected = classes[i]!.Value.Value;
                parent.Statements.Insert(at, allocation.Duplicate() with
                {
                    Defines = [sources[i]],
                    Op = new(OpKind.NewObject, (selected as Symbol)?.Value),
                    Arguments = [selected is Symbol ? selected : new Use(phi.Sources[i], 64)]
                });
                parent.Statements.Insert(at + 1, ctor.Duplicate() with { Arguments = [new Use(sources[i], receiver.Width), ctor.Arguments[1]] });
            }
            block.Statements.Remove(allocation);
            block.Statements.Remove(ctor);
            block.Statements.Insert(block.Statements.TakeWhile(s => s is Phi).Count(), new Phi(allocation.Address, created, sources));
            changed = true;
        }
        return changed;
    }

    private static bool SplitChoice(MachineFunction function, Block block, Call allocation, Call ctor, Use receiver,
        Variable created, Variable? selector, Select choice, Func<Variable> fresh)
    {
        var allocationIndex = block.Statements.IndexOf(allocation);
        var constructorIndex = block.Statements.IndexOf(ctor);
        if (block.Statements.Skip(allocationIndex + 1).Take(constructorIndex - allocationIndex - 1).Any(s => s is not Nop)
            || block.Handler is not null || function.Range.ExceptionRegions.Any(r => r.LandingPad != 0
            && (r.Start <= allocation.Address && allocation.Address < r.End || r.Start <= ctor.Address && ctor.Address < r.End)))
            return false;
        var selectorIndex = selector is null ? allocationIndex : block.Statements.FindIndex(s => s is Define d && d.Target == selector);
        if (selectorIndex < 0 || selector is not null && selectorIndex >= allocationIndex
            || ContainsLoad(choice.True) || ContainsLoad(choice.False))
            return false;

        var label = Math.Max(NameSupply.JoinedEdgeBase, function.Blocks.Max(b => b.Start) + 1);
        Block NewBlock()
        {
            var added = new Block(label) { End = label + 1 };
            label++;
            function.Blocks.Add(added);
            return added;
        }
        static void Link(Block from, Block to)
        {
            from.Successors.Add(to);
            to.Predecessors.Add(from);
        }

        var merge = NewBlock();
        merge.End = block.End;
        merge.Statements = [.. block.Statements.Skip(constructorIndex + 1)];
        foreach (var successor in block.Successors)
        {
            successor.Predecessors[successor.Predecessors.IndexOf(block)] = merge;
            merge.Successors.Add(successor);
        }
        // 选择的条件在选择器的原位置求值；保留选择器定义供区域之外的使用。
        if (selector is not null)
        {
            var stable = fresh();
            block.Statements.Insert(selectorIndex, new Define(block.Statements[selectorIndex].Address, stable, choice.Condition));
            choice = choice with { Condition = new Use(stable, 1) };
            block.Statements[selectorIndex + 1] = ((Define)block.Statements[selectorIndex + 1]) with { Value = choice };
            allocationIndex++;
        }
        block.Statements = [.. block.Statements.Take(allocationIndex)];
        block.Successors.Clear();

        var results = new List<Variable>();
        void Build(Block arm, Expr expression)
        {
            if (expression is Select select)
            {
                var yes = NewBlock();
                var no = NewBlock();
                arm.Statements.Add(new Branch(allocation.Address, select.Condition, yes.Start, no.Start));
                Link(arm, yes);
                Link(arm, no);
                Build(yes, select.True);
                Build(no, select.False);
                return;
            }
            var selected = (Symbol)expression;
            var instance = fresh();
            arm.Statements.Add(allocation.Duplicate() with
            {
                Defines = [instance], Op = new(OpKind.NewObject, selected.Value), Arguments = [selected],
            });
            arm.Statements.Add(ctor.Duplicate() with { Arguments = [new Use(instance, receiver.Width), ctor.Arguments[1]] });
            arm.Statements.Add(new Jump(ctor.Address, merge.Start));
            Link(arm, merge);
            results.Add(instance);
        }
        Build(block, choice);
        merge.Statements.Insert(0, new Phi(allocation.Address, created, results.ToArray()));
        function.Renumber();
        return true;
    }

    private static bool Classes(Expr expression) => expression switch
    {
        Symbol { Kind: SymbolKind.Class, Value: DefinitionRef { Definition.IsValueType: false } } => true,
        Select select => !ExprMap.Walk(select.Condition).Any(e => e is Ir.Read or Intrinsic or Unknown)
            && Classes(select.True) && Classes(select.False),
        _ => false,
    };

    private static bool ContainsLoad(Expr expression) => ExprMap.Walk(expression).Any(e => e is Load);
}
