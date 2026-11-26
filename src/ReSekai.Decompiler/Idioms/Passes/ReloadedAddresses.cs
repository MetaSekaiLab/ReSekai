using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 合流的每个来源都由同一位置的读取按同一算式算出，且从各自的读取到合流之间没有内存写入：
/// 编译器在会写内存的那一侧重新读取，另一侧沿用先前的读取（部分冗余消除）。还原成合流处的一次读取。
/// </summary>
internal static class ReloadedAddresses
{
    public static bool Run(MachineFunction function, Func<int, Variable> fresh)
    {
        if (function.Range.ExceptionRegions.Length != 0) return false;
        Dictionary<Variable, Define>? definitions = null;
        Dictionary<Variable, Block>? owners = null;
        foreach (var block in function.Blocks)
        {
            if (block.IsLandingPad || block.Handler is not null || block.Predecessors.Count < 2) continue;
            foreach (var phi in block.Statements.OfType<Phi>().ToArray())
            {
                if (phi.Target.Width != 64 || phi.Sources.Length != block.Predecessors.Count || phi.Sources.Any(s => s.Version < 0)
                    || !UsedAsAddress(block, phi.Target)) continue;
                if (definitions is null)
                {
                    definitions = function.Blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target);
                    owners = function.Blocks.SelectMany(b => b.Statements.OfType<Define>().Select(d => (d.Target, Block: b))).ToDictionary(p => p.Target, p => p.Block);
                }
                var loads = new List<Define>();
                Expr? shape = null;
                foreach (var source in phi.Sources)
                {
                    var read = new List<Define>();
                    var value = Expand(new Use(source, 64), definitions, read, 0);
                    if (value is null || read.Distinct().ToArray() is not [var load] || shape is not null && shape != value) { shape = null; break; }
                    shape = value;
                    loads.Add(load);
                }
                if (shape is null || shape is Load || loads.Distinct().Count() < 2) continue;
                // 基址与下标支配每个前驱，因而也支配合流；读取到合流之间不得有可能改写该位置的写入。
                if (Enumerable.Range(0, loads.Count).Any(i => Clobbered(owners![loads[i].Target], loads[i], block.Predecessors[i], block))) continue;
                var reload = fresh(64);
                var position = block.Statements.TakeWhile(s => s is Phi).Count();
                block.Statements.Remove(phi);
                position--;
                var address = ExprMap.Walk(shape).OfType<Load>().First();
                block.Statements.Insert(position++, new Define(phi.Address, reload, address));
                block.Statements.Insert(position, new Define(phi.Address, phi.Target, ExprMap.Map(shape, e => e == address ? new Use(reload, 64) : null)));
                return true;
            }
        }
        return false;
    }

    /// <summary>合流后只作为内存地址（可加常量）使用。</summary>
    private static bool UsedAsAddress(Block block, Variable variable) => block.Statements.OfType<Store>().Any(s =>
        s.Target is Use u && u.Variable == variable || s.Target is Binary { Op: BinOp.Add, Left: Use l, Right: Const } && l.Variable == variable);

    /// <summary>加减乘、移位与扩展直接展开，读取展开成对应位置的读取表达式；出现其他定义时止于变量。</summary>
    private static Expr? Expand(Expr expression, Dictionary<Variable, Define> definitions, List<Define> loads, int depth)
    {
        if (depth >= 16) return null;
        switch (expression)
        {
            case Use use when definitions.TryGetValue(use.Variable, out var definition):
                if (definition.Value is Load { Width: 64 } load && use.Width == 64)
                {
                    if (!ExprMap.Walk(load.Address).All(e => e is Use or Const || e is Binary { Op: BinOp.Add })) return null;
                    loads.Add(definition);
                    return load;
                }
                return definition.Value is Binary { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Shl } or Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend or ConvKind.Truncate } or Use
                    ? use.Width == definition.Value.Width ? Expand(definition.Value, definitions, loads, depth + 1) : expression : expression;
            case Use or Const or Symbol:
                return expression;
            case Binary { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Shl } binary:
                return Expand(binary.Left, definitions, loads, depth + 1) is { } left && Expand(binary.Right, definitions, loads, depth + 1) is { } right
                    ? binary with { Left = left, Right = right } : null;
            case Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend or ConvKind.Truncate } conversion:
                return Expand(conversion.Operand, definitions, loads, depth + 1) is { } operand ? conversion with { Operand = operand } : null;
            default:
                return null;
        }
    }

    /// <summary>从读取到这条入边的所有路径上，可能有语句改写内存（栈帧自己的写入除外）。</summary>
    private static bool Clobbered(Block from, Define load, Block predecessor, Block join)
    {
        static bool Writes(Stmt statement) => statement switch
        {
            Store store => !FrameAddressed(store.Target),
            Call { Op.Kind: OpKind.NullCheck or OpKind.BoundsCheck } => false,
            Call or Opaque => true,
            _ => false,
        };
        static bool FrameAddressed(Expr target) => target is FrameAddress || target is Binary { Op: BinOp.Add, Left: FrameAddress, Right: Const };
        if (from.Statements.SkipWhile(s => s != load).Skip(1).Any(Writes)) return true;
        if (predecessor == from) return false;
        var pending = new Stack<Block>([predecessor]);
        var seen = new HashSet<Block> { from };
        while (pending.TryPop(out var block))
        {
            if (!seen.Add(block)) continue;
            if (block == join || block.Statements.Any(Writes) || block.Predecessors.Count == 0) return true;
            foreach (var parent in block.Predecessors) pending.Push(parent);
        }
        return false;
    }
}
