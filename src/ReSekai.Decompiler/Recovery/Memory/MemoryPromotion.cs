using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;
using ReSekai.Decompiler.Recovery.Calls;

namespace ReSekai.Decompiler.Recovery.Memory;

/// <summary>局部内存的一个单元：重叠访问的并集。Escape 不为 null 时地址流向了无法确定访问范围的用途，单元不提升。</summary>
public sealed record Cell(int Id, LocalSpace Space, long Start, int Size, string? Escape)
{
    public DefinitionRef? TypedStorage { get; init; }
    public bool Promoted => Escape is null;
    public long End => Start + Size;
    public Storage Storage => Storage.Local(Id, Size * 8);
    public override string ToString() => $"cell{Id} {Space}[{Start:x},{End:x}){(Escape is null ? "" : " 逃逸：" + Escape)}";
}

/// <summary>局部内存的划分。</summary>
public sealed class MemoryLayout(IReadOnlyList<Cell> cells)
{
    public IReadOnlyList<Cell> Cells { get; } = cells;
    public Cell? Of(Storage storage) => storage.Bank == Bank.Local ? Cells[storage.Index] : null;
    public Cell? Find(LocalSpace space, long offset, int size) =>
        Cells.FirstOrDefault(c => c.Space == space && c.Start <= offset && offset + Math.Max(size, 1) <= c.End);
}

/// <summary>
/// 局部内存的提升：收集栈帧、结果缓冲区和实参副本上的全部访问——直接读写、按机器签名经指针实参的调用效果、
/// 栈传递的实参——把重叠的访问区间合并为单元。地址流向其他用途（存入内存、进入 φ、未知调用、动态下标）的单元保留为内存；
/// 其余单元的读写改为单元的部分读写，调用按效果读取或写出单元，然后对单元构建 SSA。
/// 栈槽复用时完整写入开始新的版本；溢出读回经 SSA 得到原值。
/// </summary>
internal sealed class MemoryPromotion
{
    private sealed record Access(LocalSpace Space, long Offset, int Size, string? Escape);

    /// <summary>调用经指针的一次效果：Argument 为指针在 Arguments 中的位置（栈实参为 -1）。</summary>
    private sealed record Effect(int Argument, int Value, int ValueWidth, int ValueBit, LocalSpace Space, long Offset, int Size, MemoryAccess Access);

    private readonly MachineFunction function;
    private readonly MachineSignature signature;
    private readonly Func<Call, CallShape> shapes;
    private readonly ManagedAbi abi;
    private readonly Dictionary<Variable, Expr> definitions = [];
    private readonly Dictionary<Storage, LocalSpace> pointers = [];
    private readonly Dictionary<long, LocalSpace> stackPointers = [];
    private readonly List<Access> accesses = [];
    private readonly Dictionary<long, (int Size, DefinitionRef Type)> typedFrame = [];

    private MemoryPromotion(MachineFunction function, MachineSignature signature, Func<Call, CallShape> shapes, ManagedAbi abi)
    {
        this.function = function;
        this.signature = signature;
        this.shapes = shapes;
        this.abi = abi;
        foreach (var value in signature.Arguments.Where(a => a.Transfer == Transfer.Copy && a.Pieces[0].Register.Bank == Bank.Gpr))
            pointers[value.Pieces[0].Register] = LocalSpace.Copy(value.Index);
        if (signature.Return is { Transfer: Transfer.ResultBuffer } result && result.Pieces[0].Register.Bank == Bank.Gpr)
            pointers[result.Pieces[0].Register] = LocalSpace.Result;
        // 实参副本的指针经栈槽传入：槽里的 64 位值就是副本地址，只要函数不改写这个槽。
        foreach (var value in signature.Arguments.Where(a => a.Transfer == Transfer.Copy && a.Pieces[0].Register.Bank == Bank.Stack))
        {
            var slot = (long)value.Pieces[0].Register.Index;
            if (!WritesFrame(slot, 8)) stackPointers[slot] = LocalSpace.Copy(value.Index);
        }
    }

    /// <summary>入口栈槽 [slot, slot+size) 是否被函数自己写入过。</summary>
    private bool WritesFrame(long slot, int size) => function.Blocks.SelectMany(b => b.Statements).OfType<Store>()
        .Any(s => s.Target is FrameAddress f && f.Offset < slot + size && slot < f.Offset + s.Value.Width / 8);

    public static RecoveryResult<MemoryLayout> Run(MachineFunction function, MachineSignature signature, Func<Call, CallShape> shapes,
        ManagedAbi abi, ICallModel calls, Storage stackPointer)
    {
        var promotion = new MemoryPromotion(function, signature, shapes, abi);
        promotion.TrimUnusedCallInputs();
        promotion.BindStackAddresses();
        if (Machine.Storage.FrameForwarding.Run(function, new CallMemoryEffects(shapes, abi, settled: true))) Propagate.Run(function, Dominators.Compute(function));
        promotion.FindTypedFrameCopies();
        promotion.SplitPairedLocalZeros();
        var layout = promotion.Partition();
        if (layout.Cells.Count == 0) return layout;
        if (signature.Return is { Transfer: Transfer.ResultBuffer, Size: > 0 } && layout.Cells.Any(c => c.Space == LocalSpace.Result && !c.Promoted))
            return RecoveryDiagnostics.Fail(function.Entry, "内存 结果缓冲区的地址逃逸：" + string.Join("；", layout.Cells
                .Where(c => c.Space == LocalSpace.Result && !c.Promoted).Select(c => $"+{c.Start:x} {c.Escape}")), rule: "Memory");
        layout = promotion.KeepThrowObservedCells(layout, calls, stackPointer);
        promotion.Rewrite(layout);
        var dominators = Dominators.Compute(function);
        SsaBuilder.Build(function, dominators, s => s.Bank == Bank.Local, calls, stackPointer);
        if (SsaCheck.Verify(function, dominators) is [var error, ..])
            return RecoveryDiagnostics.Fail(function.Entry, $"内存 单元 SSA：{error}", rule: "Memory");
        Propagate.Run(function, dominators);
        return layout;
    }

    /// <summary>经固定缓冲区游标寻址的值保留为一个有类型的栈局部，逐段复制仍属于同一存储。</summary>
    private void FindTypedFrameCopies()
    {
        var candidates = new Dictionary<long, HashSet<(int Size, DefinitionRef Type)>>();
        void Note(long offset, int size, DefinitionRef type)
        {
            if (!type.Definition.Fields.Any(f => f.CustomAttributes.Any(a =>
                    a.Constructor.Owner.FullName == "System.Runtime.CompilerServices.FixedBufferAttribute"))) return;
            if (!candidates.TryGetValue(offset, out var choices)) candidates[offset] = choices = [];
            choices.Add((size, type));
        }
        var blocks = function.Blocks;
        foreach (var block in blocks)
            foreach (var call in block.Statements.OfType<Call>())
            {
                var shape = shapes(call);
                if (shape.Signature is not { } signature || shape.Candidates is not [var method]) continue;
                foreach (var value in signature.Indirections)
                {
                    var index = call.ArgumentIndex(value.Pieces[0].Register);
                    if (index < 0 || call.Arguments[index] is not FrameAddress frame || value.Size <= 0) continue;
                    var type = value.Index == MachineSignature.ReturnIndex ? method.Concrete(method.Method.ReturnType)
                        : value.Index >= 0 ? method.Concrete(method.Method.Parameters[value.Index].Type) : null;
                    if (type is ByRefRef byRef) type = byRef.Element;
                    if (type is DefinitionRef definition && abi.Size(definition) == value.Size)
                        Note(frame.Offset, value.Size, definition);
                }
            }
        if (candidates.Count == 0) return;
        var definitions = blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        foreach (var group in blocks.SelectMany(b => b.Statements).OfType<Store>().GroupBy(s => s.Address))
        {
            var transfers = group.Select(s => s is { Target: FrameAddress target, Value: Use use }
                && definitions.GetValueOrDefault(use.Variable) is Load { Address: FrameAddress source } load
                && load.Width == s.Value.Width ? ((long Dest, long Source, int Size)?)(target.Offset, source.Offset, load.Width / 8) : null).ToArray();
            if (transfers.Length < 2 || transfers.Any(t => t is null)) continue;
            var ordered = transfers.Select(t => t!.Value).OrderBy(t => t.Source).ToArray();
            var first = ordered[0];
            if (!candidates.TryGetValue(first.Source, out var types) || types.Count != 1) continue;
            var (size, type) = types.Single();
            var end = first.Source;
            foreach (var transfer in ordered)
            {
                if (transfer.Source != end || transfer.Dest - first.Dest != transfer.Source - first.Source) break;
                end += transfer.Size;
            }
            if (end == first.Source + size) Note(first.Dest, size, type);
        }
        CollectAccesses();
        foreach (var (start, choices) in candidates)
        {
            if (choices.Count != 1) continue;
            var (size, type) = choices.Single();
            if (!accesses.Any(a => a.Space == LocalSpace.Frame && a.Escape is not null && a.Offset >= start && a.Offset < start + size)
                || accesses.Any(a => a.Space == LocalSpace.Frame && a.Size > 0 && a.Offset < start + size && start < a.Offset + a.Size
                    && (a.Offset < start || a.Offset + a.Size > start + size))) continue;
            if (candidates.Keys.Any(other => other != start && candidates[other].Any(c => other < start + size && start < other + c.Size))) continue;
            typedFrame[start] = (size, type);
        }
    }

    private void BindStackAddresses()
    {
        if (!function.Blocks.SelectMany(b => b.Statements).OfType<Call>().Any(c => shapes(c).Signature?.Indirections.Any(a => a.Pieces[0].Register.Bank == Bank.Stack) == true)) return;
        var frame = new Machine.Storage.FrameModel(function, new CallMemoryEffects(shapes, abi));
        var bound = new Dictionary<Store, (Block Block, HashSet<CallSite> Sites)>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call { StackPointer: FrameAddress sp } call || shapes(call).Signature is not { } callee) continue;
                var stackIndirections = callee.Indirections.Where(a => a.Transfer is Transfer.Reference or Transfer.Copy
                    && a.Pieces[0].Register.Bank == Bank.Stack && call.ArgumentIndex(a.Pieces[0].Register) < 0);
                var arguments = new List<(Storage Register, Expr Address)>();
                foreach (var value in stackIndirections)
                {
                    var piece = value.Pieces[0];
                    var at = sp.Offset + piece.Register.Index;
                    if (!frame.Escaped(at, 8) && frame.Reach(block, i, at, 8) is { Complete: true } reach
                        && reach.Stores.All(s => s.Value.Width == 64 && frame.Slot(s.Target) == at)
                        && reach.Stores.Select(s => s.Value).Distinct().ToArray() is [var address]
                        && (address is FrameAddress || value.Transfer == Transfer.Reference && address is
                            (Use { Width: 64 } or Binary { Op: BinOp.Add, Width: 64, Left: Use { Width: 64 }, Right: Const })))
                    {
                        arguments.Add((piece.Register, address));
                        foreach (var store in reach.Stores)
                        {
                            if (!bound.TryGetValue(store, out var used))
                                bound[store] = used = (function.Blocks.Single(b => b.Statements.Any(s => ReferenceEquals(s, store))), []);
                            used.Sites.Add(call.Site);
                        }
                    }
                }
                if (arguments.Count > 0)
                    block.Statements[i] = call with { Arguments = [.. call.Arguments, .. arguments.Select(a => a.Address)],
                        Sources = [.. call.Sources, .. arguments.Select(a => a.Register)] };
            }
        var removable = bound.Where(p => frame.UnobservedAfter(p.Value.Block, p.Key, call => p.Value.Sites.Contains(call.Site))).ToArray();
        foreach (var (store, use) in removable) use.Block.Statements.Remove(store);
    }

    /// <summary>只保留签名会读取的栈指针和按值传递的小结构体寄存器位。</summary>
    private void TrimUnusedCallInputs()
    {
        var inserts = function.Blocks.SelectMany(b => b.Statements).OfType<Define>()
            .Where(d => d.Value is Insert { Low: 0, Value: Use or Const }).ToDictionary(d => d.Target, d => (Insert)d.Value);
        var changed = false;
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
                if (block.Statements[i] is Call call && shapes(call).Signature is { } callee)
                {
                    var updated = call;
                    if (call.StackPointer is not null && callee.MethodInfo?.Register.Bank != Bank.Stack
                        && callee.Arguments.Concat(callee.Indirections).SelectMany(a => a.Pieces).All(p => p.Register.Bank != Bank.Stack))
                        updated = updated with { StackPointer = null };
                    Expr[]? arguments = null;
                    if (inserts.Count != 0)
                        foreach (var piece in callee.Arguments.Where(a => a.Transfer == Transfer.Value && abi.Kind(a.Type) == ValueKind.Struct)
                            .SelectMany(a => a.Pieces).Where(p => p.Register.Bank == Bank.Gpr && p.Width < 64))
                        {
                            var position = call.ArgumentIndex(piece.Register);
                            if (position < 0 || call.Arguments[position] is not Use use || !inserts.TryGetValue(use.Variable, out var insert)
                                || piece.Width > insert.Value.Width || use.Width <= piece.Width) continue;
                            var value = Fold.Convert(ConvKind.Truncate, insert.Value, piece.Width);
                            (arguments ??= call.Arguments.ToArray())[position] = Fold.Convert(ConvKind.ZeroExtend, value, use.Width);
                        }
                    if (arguments is not null) updated = updated with { Arguments = arguments };
                    if (ReferenceEquals(updated, call)) continue;
                    block.Statements[i] = updated;
                    changed = true;
                }
        if (changed) Propagate.RemoveDead(function);
    }

    // ---- 地址 ----

    /// <summary>局部地址：帧地址、副本或结果缓冲区指针的入口值，加常量。</summary>
    private (LocalSpace Space, long Offset)? Resolve(Expr expression, int depth = 0)
    {
        if (depth > 16) return null;
        return expression switch
        {
            FrameAddress f => (LocalSpace.Frame, f.Offset),
            StackAllocation a => (LocalSpace.Dynamic(a.Site, a.Element), 0),
            Entry { Storage: var s } when pointers.TryGetValue(s, out var space) => (space, 0),
            Load { Address: FrameAddress slot, Width: 64 } when stackPointers.TryGetValue(slot.Offset, out var space) => (space, 0),
            Use { Variable: { Version: 0 } v } when pointers.TryGetValue(v.Storage, out var space) => (space, 0),
            Use { Variable: var v, Width: 64 } when definitions.TryGetValue(v, out var value) => Resolve(value, depth + 1),
            Binary { Op: BinOp.Add, Right: Const c } b when Resolve(b.Left, depth + 1) is var (space, offset) => (space, offset + unchecked((long)c.Value)),
            Binary { Op: BinOp.Sub, Right: Const c } b when Resolve(b.Left, depth + 1) is var (space, offset) => (space, offset - unchecked((long)c.Value)),
            _ => null,
        };
    }

    // ---- 收集 ----

    private void CollectAccesses()
    {
        accesses.Clear();
        definitions.Clear();
        foreach (var block in function.Blocks)
            foreach (var statement in block.Statements)
                if (statement is Define define) definitions[define.Target] = define.Value;
        foreach (var block in function.Blocks)
            foreach (var statement in block.Statements)
                Collect(statement);
    }

    /// <summary>合并写零不能把两个分别经 ref 访问的局部绑成同一存储单元。</summary>
    private void SplitPairedLocalZeros()
    {
        var zeroes = function.Blocks.SelectMany(b => b.Statements.OfType<Store>()
            .Where(s => s is { Target: FrameAddress, Value: Const { Value: 0, Width: 64 } })
            .Select(s => (Block: b, Store: s, Offset: ((FrameAddress)s.Target).Offset))).ToArray();
        if (zeroes.Length == 0) return;
        CollectAccesses();
        var references = function.Blocks.SelectMany(b => b.Statements).OfType<Call>()
            .SelectMany(c => Effects(c).Where(e => e is { Space.Kind: LocalKind.Frame, Size: 4 }
                && shapes(c).Signature?.Arguments.Any(a => a.Index == e.Value && a.Transfer == Transfer.Reference) == true))
            .Select(e => e.Offset).ToHashSet();
        foreach (var group in zeroes.GroupBy(z => z.Offset))
        {
            var at = group.Key;
            if (!references.Contains(at) || !references.Contains(at + 4)) continue;
            var crossing = accesses.Where(a => a.Space == LocalSpace.Frame && a.Offset < at + 4 && a.Offset + a.Size > at + 4).ToArray();
            if (crossing.Length != group.Count() || crossing.Any(a => a.Offset != at || a.Size != 8 || a.Escape is not null)) continue;
            foreach (var (block, store, _) in group)
            {
                var position = block.Statements.IndexOf(store);
                block.Statements[position] = store with { Value = new Const(0, 32) };
                block.Statements.Insert(position + 1, new Store(store.Address, new FrameAddress(at + 4), new Const(0, 32)));
            }
        }
    }

    private MemoryLayout Partition()
    {
        CollectAccesses();
        foreach (var (start, storage) in typedFrame)
            Add(LocalSpace.Frame, start, storage.Size);
        var cells = new List<Cell>();
        foreach (var group in accesses.GroupBy(a => a.Space))
        {
            // 范围已知的对象（实参副本、结果缓冲区）的地址逃逸时，接收方可以访问整个对象：它是一个单元，不按已见访问切开。
            if (group.FirstOrDefault(a => a.Escape is not null) is { } escaped && ObjectSize(group.Key) is > 0 and var objectSize
                && group.All(a => a.Offset >= 0 && a.Offset + Math.Max(1, a.Size) <= objectSize))
            {
                cells.Add(Make(cells.Count, group.Key, 0, objectSize, escaped.Escape));
                continue;
            }
            var ordered = group.OrderBy(a => a.Offset).ToList();
            var start = ordered[0].Offset;
            var end = start + Math.Max(1, ordered[0].Size);
            string? escape = ordered[0].Escape;
            foreach (var access in ordered.Skip(1))
            {
                if (access.Offset < end)
                {
                    end = Math.Max(end, access.Offset + Math.Max(1, access.Size));
                    escape ??= access.Escape;
                    continue;
                }
                cells.Add(Make(cells.Count, group.Key, start, end, escape));
                (start, end, escape) = (access.Offset, access.Offset + Math.Max(1, access.Size), access.Escape);
            }
            cells.Add(Make(cells.Count, group.Key, start, end, escape));
        }
        return new MemoryLayout(cells.Select(cell => cell.Space == LocalSpace.Frame
            && typedFrame.TryGetValue(cell.Start, out var storage) && storage.Size == cell.Size
                ? cell with { TypedStorage = storage.Type } : cell).ToArray());
    }

    /// <summary>空间所代表对象的字节数；范围不由访问决定的空间（实参副本、结果缓冲区）才有。</summary>
    private int ObjectSize(LocalSpace space) => space.Kind == LocalKind.Argument
        ? signature.Arguments.FirstOrDefault(a => a.Index == space.Argument && a.Transfer == Transfer.Copy)?.Size ?? 0
        : space == LocalSpace.Result ? signature.Return?.Size ?? 0 : 0;

    private static Cell Make(int id, LocalSpace space, long start, long end, string? escape) =>
        new(id, space, start, checked((int)(end - start)), escape ?? (end - start > 4096 ? "单元超过 4096 字节" : null));

    private void Add(LocalSpace space, long offset, int size) => accesses.Add(new(space, offset, size, null));
    private void Escape(LocalSpace space, long offset, string reason) => accesses.Add(new(space, offset, 1, reason));

    private void Collect(Stmt statement)
    {
        switch (statement)
        {
            case Define define:
                if (Resolve(define.Value) is null) Walk(define.Value, "参与运算");
                break;
            case Phi phi:
                foreach (var source in phi.Sources)
                    if (source is not null && Resolve(new Use(source, 64)) is var (space, offset)) Escape(space, offset, "进入 φ");
                break;
            case Store store:
                if (Resolve(store.Target) is var (targetSpace, targetOffset)) Add(targetSpace, targetOffset, store.Value.Width / 8);
                else Walk(store.Target, "地址运算");
                Walk(store.Value, "存入内存");
                break;
            case Call { Op.Kind: OpKind.WriteBarrier or OpKind.MemoryBarrier }:
                // 写屏障只通知 GC，对应的写入是紧邻其前的 Store。
                break;
            case Return when signature.Return is { Transfer: Transfer.ResultBuffer }:
                // 间接返回只观察结果缓冲区；尾内存函数附带的目标指针返回值不属于托管返回签名。
                break;
            case Call call:
                {
                    Walk(call.Target, "调用目标");
                    var effects = Effects(call);
                    // 同一次调用可观察 ref 实参之间的别名；重叠区间必须保留共同存储。
                    var references = effects.Where(e => shapes(call).Signature?.Arguments.Any(a => a.Index == e.Value && a.Transfer == Transfer.Reference) == true).ToArray();
                    foreach (var effect in references)
                        if (references.Any(other => other.Argument != effect.Argument && other.Space == effect.Space
                            && other.Offset < effect.Offset + effect.Size && effect.Offset < other.Offset + other.Size))
                            Escape(effect.Space, effect.Offset, "调用的 ref 实参重叠");
                    foreach (var effect in effects)
                        if (effect.Size > 0) Add(effect.Space, effect.Offset, effect.Size);
                        else Escape(effect.Space, effect.Offset, "调用访问范围未知");
                    var covered = effects.Select(e => e.Argument).ToHashSet();
                    // 隐藏 MethodInfo 的寄存器只传方法指针，里面残留的旧值不是实参。
                    if (shapes(call).Signature?.MethodInfo is { } hidden && call.ArgumentIndex(hidden.Register) is >= 0 and var slot) covered.Add(slot);
                    // 签名已知时，输入只有签名占用的寄存器；其余寄存器里是调用前残留的值，被调方不读取，不是实参。
                    var live = shapes(call).Signature is { } known ? InputRegisters(known) : null;
                    for (var i = 0; i < call.Arguments.Length; i++)
                        if (!covered.Contains(i) && (live is null || i >= call.Sources.Length || live.Contains(call.Sources[i]))) Walk(call.Arguments[i], shapes(call) is { Signature: null, IsRuntime: false } ? "未知调用的实参" : "调用实参");
                    if (call.DelegateReceiver is { } receiver) Walk(receiver, "委托接收者");
                    break;
                }
            default:
                foreach (var operand in ExprMap.Operands(statement)) Walk(operand, "语句操作数");
                break;
        }
    }


    /// <summary>机器签名读取的输入寄存器：实参、隐藏的 MethodInfo 与结果缓冲区指针。</summary>
    private static HashSet<Storage> InputRegisters(MachineSignature signature)
    {
        var registers = signature.Arguments.SelectMany(a => a.Pieces).Select(p => p.Register).ToHashSet();
        if (signature.MethodInfo is { } info) registers.Add(info.Register);
        if (signature.Return is { Transfer: Transfer.ResultBuffer } result) registers.Add(result.Pieces[0].Register);
        return registers;
    }

    /// <summary>表达式中的读取是访问；其余位置出现的局部地址是逃逸。</summary>
    private void Walk(Expr expression, string reason)
    {
        if (expression is Load load)
        {
            if (Resolve(load.Address) is var (space, offset)) Add(space, offset, load.Width / 8);
            else Walk(load.Address, "地址运算");
            return;
        }
        if (expression is not Const && Resolve(expression) is var (local, at))
        {
            Escape(local, at, reason);
            return;
        }
        foreach (var child in ExprMap.Children(expression)) Walk(child, reason);
    }

    /// <summary>调用经指针实参与栈实参访问的局部内存。</summary>
    private List<Effect> Effects(Call call)
    {
        var effects = new List<Effect>();
        if (call.Op is { } op && shapes(call).IsRuntime)
        {
            foreach (var effect in Runtime.RuntimeOps.Of(op.Kind)?.PointerEffects?.Invoke(call, abi) ?? [])
                if (effect.Argument < call.Arguments.Length && Resolve(call.Arguments[effect.Argument]) is var (space, offset))
                    effects.Add(new(effect.Argument, effect.Value, effect.Size * 8, 0, space, offset, effect.Size, effect.Access));
            return effects;
        }
        if (shapes(call).Signature is not { } callee) return effects;
        foreach (var value in callee.Indirections)
        {
            var pointer = value.Pieces[0].Register;
            var position = call.ArgumentIndex(pointer);
            if (position < 0 || Resolve(call.Arguments[position]) is not var (space, offset)) continue;
            effects.Add(new(position, value.Index, value.Size * 8, 0, space, offset, value.Size, value.Access));
        }
        if (call.StackPointer is { } sp && Resolve(sp) is (LocalSpace { Kind: LocalKind.Frame }, var spOffset))
        {
            foreach (var value in callee.Arguments.Where(a => a.Transfer == Transfer.Value))
                foreach (var piece in value.Pieces.Where(p => p.Register.Bank == Bank.Stack))
                    effects.Add(new(-1, value.Index, MethodEnvironment.ValueWidth(value), piece.Offset * 8, LocalSpace.Frame,
                        spOffset + piece.Register.Index, piece.Width / 8, MemoryAccess.Read));
            // 栈上传递的隐藏 MethodInfo：共享泛型代码的调用由它指名确切实例。
            if (callee.MethodInfo is { Register.Bank: Bank.Stack } info)
                effects.Add(new(-1, MachineSignature.MethodInfoIndex, 64, 0, LocalSpace.Frame, spOffset + info.Register.Index, 8, MemoryAccess.Read));
        }
        return effects;
    }

    /// <summary>
    /// 调用经 ref 改写单元时抛出，处理块读到的是被调方留下的内容。SSA 的版本只有调用前后两个完整状态，表达不了这个中间状态；
    /// 这样的单元在机器和 IL 里都是同一块存储，留在内存里由可寻址局部承接。
    /// 试构建的剪枝 SSA 给出哪些落地块 φ 有表达不了的来源；部分写入在 SSA 里读取整个单元，它的活跃性偏宽，处理块是否读到旧内容按字节判断。
    /// </summary>
    private MemoryLayout KeepThrowObservedCells(MemoryLayout layout, ICallModel calls, Storage stackPointer)
    {
        var writers = new HashSet<(ulong Block, Storage Cell)>();
        foreach (var block in function.Blocks.Where(b => b.Handler is not null))
            foreach (var call in block.Statements.OfType<Call>())
                foreach (var effect in Effects(call).Where(e => e.Size > 0 && (e.Access & MemoryAccess.Write) != 0
                    && shapes(call).Signature?.Arguments.Any(a => a.Index == e.Value && a.Transfer == Transfer.Reference) == true))
                    foreach (var cell in layout.Cells.Where(c => c.Promoted && c.Space == effect.Space && c.Start < effect.Offset + effect.Size && effect.Offset < c.End))
                        writers.Add((block.Start, cell.Storage));
        if (writers.Count == 0) return layout;
        var live = LiveBytes(writers.Select(w => layout.Cells[w.Cell.Index]).Distinct().ToArray());
        var probe = function.Copy();
        var trial = new MemoryPromotion(probe, signature, shapes, abi);
        trial.Partition();
        trial.Rewrite(layout);
        SsaBuilder.Build(probe, Dominators.Compute(probe), s => s.Bank == Bank.Local, calls, stackPointer);
        var observed = new HashSet<int>();
        foreach (var landing in probe.Blocks.Where(b => b.IsLandingPad))
            foreach (var phi in landing.Statements.OfType<Phi>().Where(p => p.Target.Storage.Bank == Bank.Local))
                for (var i = 0; i < phi.Sources.Length; i++)
                    if (phi.Sources[i].Version < 0 && writers.Contains((landing.Predecessors[i].Start, phi.Target.Storage))
                        && live[landing.Start].Any(b => b.Cell == phi.Target.Storage.Index))
                        observed.Add(phi.Target.Storage.Index);
        return observed.Count == 0 ? layout
            : new MemoryLayout(layout.Cells.Select(c => observed.Contains(c.Id) ? c with { Escape = "异常路径观察 ref 调用写入的内容" } : c).ToList());
    }

    /// <summary>
    /// 各块入口按字节活跃的单元内容（后向数据流）：读取和调用的读效果产生活跃，写入只杀死写到的字节。
    /// 调用可能抛出，处理块的活跃集在每个调用处并入，调用之前的写入不会遮住调用抛出时的处理块。
    /// </summary>
    private Dictionary<ulong, HashSet<(int Cell, long Byte)>> LiveBytes(IReadOnlyList<Cell> tracked)
    {
        var entry = function.Blocks.ToDictionary(b => b.Start, _ => new HashSet<(int Cell, long Byte)>());
        void Mark(HashSet<(int Cell, long Byte)> live, LocalSpace space, long offset, int size, bool read)
        {
            foreach (var cell in tracked.Where(c => c.Space == space && c.Start < offset + size && offset < c.End))
                for (var at = Math.Max(cell.Start, offset); at < Math.Min(cell.End, offset + size); at++)
                    if (read) live.Add((cell.Id, at)); else live.Remove((cell.Id, at));
        }
        void Reads(HashSet<(int Cell, long Byte)> live, IEnumerable<Expr> operands)
        {
            foreach (var load in operands.SelectMany(ExprMap.Walk).OfType<Load>())
                if (Resolve(load.Address) is var (space, offset)) Mark(live, space, offset, load.Width / 8, true);
        }
        void Returned(HashSet<(int Cell, long Byte)> live)
        {
            foreach (var cell in tracked.Where(c => c.Space == LocalSpace.Result)) Mark(live, cell.Space, cell.Start, cell.Size, true);
        }
        var resultBuffer = signature.Return is { Transfer: Transfer.ResultBuffer };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var block in Enumerable.Reverse(function.Blocks))
            {
                var live = new HashSet<(int Cell, long Byte)>();
                foreach (var successor in block.Successors) live.UnionWith(entry[successor.Start]);
                foreach (var statement in Enumerable.Reverse(block.Statements))
                    switch (statement)
                    {
                        case Store store:
                            if (Resolve(store.Target) is var (space, offset)) Mark(live, space, offset, store.Value.Width / 8, false);
                            Reads(live, ExprMap.Operands(store));
                            break;
                        case Return when resultBuffer:
                            Returned(live);
                            break;
                        case Call call:
                            if (resultBuffer && call.Kind is CallKind.Tail or CallKind.TailIndirect) Returned(live);
                            var effects = Effects(call).Where(e => e.Size > 0).ToArray();
                            foreach (var effect in effects.Where(e => (e.Access & MemoryAccess.Write) != 0)) Mark(live, effect.Space, effect.Offset, effect.Size, false);
                            if (block.Handler is { } handler) live.UnionWith(entry[handler.Start]);
                            foreach (var effect in effects.Where(e => (e.Access & MemoryAccess.Read) != 0)) Mark(live, effect.Space, effect.Offset, effect.Size, true);
                            Reads(live, ExprMap.Operands(call));
                            break;
                        default:
                            Reads(live, ExprMap.Operands(statement));
                            break;
                    }
                // 活跃集只增不减，大小不变即不动点。
                if (live.Count != entry[block.Start].Count)
                {
                    entry[block.Start] = live;
                    changed = true;
                }
            }
        }
        return entry;
    }

    // ---- 改写 ----

    private void Rewrite(MemoryLayout layout)
    {
        Cell? Promoted(LocalSpace space, long offset, int size) => layout.Find(space, offset, size) is { Promoted: true } cell ? cell : null;
        Expr Loads(Expr expression) => ExprMap.Map(expression, e =>
        {
            if (e is not Load load || Resolve(load.Address) is not var (space, offset) || Promoted(space, offset, load.Width / 8) is not { } cell) return null;
            var whole = new Read(cell.Storage, cell.Size * 8);
            return offset == cell.Start && load.Width == cell.Size * 8 ? whole : new Extract(whole, (int)(offset - cell.Start) * 8, load.Width);
        });
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                var statement = block.Statements[i];
                if (statement is Store store && Resolve(store.Target) is var (space, offset) && Promoted(space, offset, store.Value.Width / 8) is { } cell)
                {
                    var value = Loads(store.Value);
                    var whole = new Read(cell.Storage, cell.Size * 8);
                    Expr content = offset == cell.Start && value.Width == cell.Size * 8 ? value : new Insert(whole, value, (int)(offset - cell.Start) * 8);
                    block.Statements[i] = new Assign(store.Address, cell.Storage, content);
                    continue;
                }
                statement = ExprMap.MapOperands(statement, Loads);
                if (statement is Call call)
                {
                    statement = WithEffects(call, Effects(call), layout);
                    // 间接返回取自调用方的结果缓冲区，尾调用本身的返回类型可以是 void。
                    // 先保留调用对单元的写入，再显式读取这些单元作为本方法的返回值。
                    if (call.Kind is CallKind.Tail or CallKind.TailIndirect && signature.Return is { Transfer: Transfer.ResultBuffer })
                    {
                        statement = ((Call)statement) with { Kind = call.Kind == CallKind.Tail ? CallKind.Direct : CallKind.Indirect };
                        block.Statements.Insert(i + 1, new Return(call.Address));
                    }
                }
                if (statement is Return ret && signature.Return is { Transfer: Transfer.ResultBuffer, Size: > 0 })
                    statement = ret with
                    {
                        Pieces = layout.Cells.Where(c => c.Space == LocalSpace.Result).OrderBy(c => c.Start)
                            .Select(c => new ReturnPiece((int)c.Start, new Read(c.Storage, c.Size * 8))).ToArray(),
                    };
                block.Statements[i] = statement;
            }
    }

    private static Call WithEffects(Call call, List<Effect> effects, MemoryLayout layout)
    {
        var arguments = call.Arguments.ToList();
        var sources = call.Sources.ToList();
        var results = call.Results.ToList();
        var defines = call.Defines.Length == call.Results.Length ? call.Defines.ToList() : Enumerable.Repeat<Variable>(null!, call.Results.Length).ToList();
        var memory = new List<CellAccess>();
        foreach (var effect in effects)
        {
            if (effect.Size <= 0) continue;
            foreach (var cell in layout.Cells.Where(c => c.Promoted && c.Space == effect.Space && c.Start < effect.Offset + effect.Size && effect.Offset < c.End))
            {
                var from = Math.Max(cell.Start, effect.Offset);
                var to = Math.Min(cell.End, effect.Offset + effect.Size);
                var access = new CellAccess(cell.Storage, effect.Value, effect.ValueWidth, effect.ValueBit + (int)(from - effect.Offset) * 8,
                    (int)(from - cell.Start) * 8, (int)(to - from) * 8, false);
                var whole = from == cell.Start && to == cell.End;
                if ((effect.Access & MemoryAccess.Read) != 0 || !whole)
                {
                    if (!sources.Contains(cell.Storage)) { sources.Add(cell.Storage); arguments.Add(new Read(cell.Storage, cell.Size * 8)); }
                    if ((effect.Access & MemoryAccess.Read) != 0) memory.Add(access);
                }
                if ((effect.Access & MemoryAccess.Write) != 0)
                {
                    if (!results.Contains(cell.Storage)) { results.Add(cell.Storage); defines.Add(null!); }
                    memory.Add(access with { Write = true });
                }
            }
        }
        if (memory.Count == 0) return call;
        return call with { Arguments = arguments.ToArray(), Sources = sources.ToArray(), Results = results.ToArray(), Defines = defines.ToArray(), Memory = memory.ToArray() };
    }
}
