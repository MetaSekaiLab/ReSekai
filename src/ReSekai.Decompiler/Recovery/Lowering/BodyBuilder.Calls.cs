using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Recovery.Calls;
using ReSekai.Decompiler.Recovery.Control;
using ReSekai.Decompiler.Recovery.Memory;
using ReSekai.Decompiler.Recovery.Runtime;
using ReSekai.Decompiler.Recovery.Typing;
using ReSekai.Decompiler.Recovery.Values;

namespace ReSekai.Decompiler.Recovery.Lowering;

/// <summary>调用的下降：按机器签名重组实参、构造与内联证明、运行时操作。</summary>
internal sealed partial class BodyBuilder
{
    private sealed record DelegateChoice(PhiSym Phi, LocalValue Local, IReadOnlyList<DelegateTarget> Sources,
        IReadOnlyDictionary<BoundDelegateTarget, int> Methods);

    private readonly List<DelegateChoice> delegateChoices = [];
    private readonly Dictionary<Call, Sym> guardedReceivers = [];

    private Terminator? LowerCall(Block block, int index, Call call, IReadOnlyList<Sym> operands, List<ManagedStatement> output)
    {
        var arguments = operands.Skip(1).Take(call.Arguments.Length).ToArray();
        var shape = analysis.Environment.Shape(call);
        if (call.Op is { } op && shape.IsRuntime)
            return LowerRuntime(block, index, call, op, arguments, output);
        var signature = shape.Signature;
        if (signature is null)
        {
            output.Add(new ManagedHole(call.Address, "调用 目标或签名未知", false));
            holes.Add(new(call.Address, "调用 目标或签名未知"));
            return call.Kind is CallKind.Tail or CallKind.TailIndirect ? new Statement(new ManagedHole(call.Address, "调用 尾调用", true)) : null;
        }
        var contents = signature.Arguments.Select(v => values.ArgumentContent(call, v, arguments)).ToArray();
        var callee = oracle.Callee(call);
        if (callee is null)
        {
            holes.Add(new(call.Address, "调用 目标未知"));
            return call.Kind is CallKind.Tail or CallKind.TailIndirect ? new Statement(new ManagedHole(call.Address, "调用 目标未知", true))
                : AddHole(output, new Statement(new ManagedHole(call.Address, "调用 目标未知", false)));
        }
        // 分配之后的实例构造：合为 newobj（委托为委托构造），接收者不作为值出现。
        var construction = callee.Method.Name == ".ctor" && !callee.Method.IsStatic && contents.Length > 0
            && SymTable.Resolve(contents[0]) is ResultSym { Site.Op.Kind: OpKind.NewObject } allocated && constructions.ContainsKey(allocated) ? allocated : null;
        // newobj 必须指名分配类型自己的构造函数。调用的是基类构造时，原生代码内联了分配类型的构造函数：
        // 先写成新对象上的基类构造调用，由内联的逆以分配类型的构造函数为模板证明（InlineConstructions）。
        LocalValue? pendingReceiver = null;
        if (construction is not null && oracle.ClassOperand(construction.Site) is DefinitionRef allocatedType && callee.DeclaringType is DefinitionRef declaring
            && allocatedType.Definition != declaring.Definition)
        {
            pendingReceiver = Local(construction, allocatedType);
            pendingConstructions[pendingReceiver.Variable] = new(call.Address, allocatedType, callee);
            construction = null;
        }
        var managed = new List<ManagedValue>();
        ManagedValue? receiver = null;
        for (var i = 0; i < signature.Arguments.Length; i++)
        {
            var value = signature.Arguments[i];
            if (value.Index < 0 && construction is not null) continue;
            if (value.Index < 0 && pendingReceiver is not null) { receiver = pendingReceiver; continue; }
            var type = TypeOracle.ArgumentType(call, callee, value.Index);
            if (construction is not null && DelegateFacts.IsDelegate(callee.DeclaringType) && value.Index == 1 && managed.Count == 1)
            {
                if (oracle.DelegateTargetOf(contents[i], contents[Array.FindIndex(signature.Arguments, a => a.Index == 0)]) is { } target)
                {
                    var instance = managed[0];
                    if (target is not BoundDelegateTarget && instance is not (LocalValue or ArgumentValue or ConstantValue))
                    {
                        var saved = NewLocal(instance.Type);
                        output.Add(new ManagedAssignment(call.Address, saved, instance));
                        instance = saved;
                    }
                    ManagedValue Create(DelegateTarget binding) => binding switch
                    {
                        BoundDelegateTarget bound => new DelegateValue((DefinitionRef)callee.DeclaringType, instance, bound.Method, bound.Binding),
                        SelectedDelegateTarget selected => new ChoiceValue(View(selected.Condition, types.Boolean, call.Address),
                            Create(selected.True), Create(selected.False), callee.DeclaringType, 64),
                        MergedDelegateTarget merged => CreateMerged(merged),
                        _ => throw new NotSupportedException(binding.ToString()),
                    };
                    ManagedValue CreateMerged(MergedDelegateTarget merged)
                    {
                        var methods = BoundMethods(merged).Distinct().ToArray();
                        if (methods.Length == 1) return Create(methods[0]);
                        var ids = methods.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
                        var index = DelegateChoiceValue(merged, ids, call.Address);
                        var result = Create(methods[^1]);
                        for (var n = methods.Length - 2; n >= 0; n--)
                            result = new ChoiceValue(new ComparisonValue(Cond.Eq, index, new ConstantValue((ulong)n, types.Int32, 32), types.Boolean),
                                Create(methods[n]), result, callee.DeclaringType, 64);
                        return result;
                    }
                    var created = Create(target);
                    output.Add(new ManagedAssignment(call.Address, Local(construction, callee.DeclaringType), created));
                    return call.Kind == CallKind.Tail ? new Statement(new ManagedReturn(call.Address, null)) : null;
                }
            }
            var argument = value.Index < 0 && call.Op?.Kind == OpKind.CallDelegate && call.DelegateReceiver is not null
                ? View(operands[1 + call.Arguments.Length], type, call.Address)
                // 被吸收的判空要求接收者就是判过的值：换成默认值后 callvirt 必然抛出。
                : value.Index < 0 && absorbedChecks.Contains(contents[i]) ? Argument(call, value, type, contents[i], arguments, output)
                : UnusedValueReceiver(shape, callee, value, type, contents[i])
                    ?? UnusedReceiver(block, index, shape, callee, value, type, contents[i], contents)
                    ?? UnusedTailArgument(shape, callee, value, type, contents[i])
                    ?? UnusedScalar(shape, callee, value, type, contents[i]) ?? Unconstrained(shape, value, type, contents[i])
                    ?? UnusedStackArgument(call, callee, value, type, contents[i])
                    ?? UnusedCopyArgument(shape, callee, value, type, contents[i])
                    ?? (oracle.ArgumentCopy(shape, value, type, contents[i]) is { } whole ? View(whole, type, call.Address) : null)
                    ?? UnusedReference(shape, callee, value, type, contents[i], output)
                    ?? Argument(call, value, type, contents[i], arguments, output);
            if (value.Index < 0) receiver = argument;
            else managed.Add(argument);
        }
        if (construction is not null)
        {
            output.Add(new ManagedAssignment(call.Address, Local(construction, callee.DeclaringType), new NewObjectValue(callee, managed)));
            return call.Kind == CallKind.Tail ? new Statement(new ManagedReturn(call.Address, null)) : null;
        }
        // MethodInfo.methodPointer 指向已选中的实现。经指针调用该实现也可能是 base 调用。
        var dispatched = shape.IsDispatch || call.Op is null && call.Resolved is not null && call.Kind is CallKind.Indirect or CallKind.TailIndirect;
        var invocation = new ManagedInvocation(callee, receiver, managed, Virtual: receiver is not null
            && (dispatched || !callee.Method.IsOverridable && absorbedChecks.Contains(contents[0]))) { Constraint = TypeOracle.ConstraintOf(call) };
        LocalValue? result = null;
        var returnType = callee.Concrete(callee.Method.ReturnType);
        if (!ManagedTypes.IsVoid(returnType))
            result = signature.Return?.Transfer == Transfer.ResultBuffer
                ? Local(values.Syms.Out(call, MachineSignature.ReturnIndex, MethodEnvironment.ValueWidth(signature.Return)), returnType)
                : Local(values.Syms.Result(call, 0, signature.Return is { } ret ? MethodEnvironment.ValueWidth(ret) : Width(returnType)), returnType);
        var tail = call.Kind is CallKind.Tail or CallKind.TailIndirect;
        // 尾调用原样返回被调用者的结果寄存器；两者的返回类型不同（如按值返回的单字段结构体与其字段）时，返回值是同一段位在调用方返回类型下的值。
        if (tail && result is not null && oracle.ReturnType is { } own && !ManagedTypes.IsVoid(own) && !own.Equals(returnType)
            && !(ManagedTypes.IsReferenceType(own) && ManagedTypes.IsReferenceType(returnType))
            && signature.Return is { Transfer: Transfer.Value } returned && analysis.Signature.Return is { Transfer: Transfer.Value } caller
            && MethodEnvironment.ValueWidth(caller) <= MethodEnvironment.ValueWidth(returned))
        {
            output.Add(new ManagedCall(call.Address, invocation, result));
            var bits = values.Syms.Result(call, 0, MethodEnvironment.ValueWidth(returned));
            return new Statement(new ManagedReturn(call.Address, View(values.Syms.Slice(bits, 0, MethodEnvironment.ValueWidth(caller)), own, call.Address)));
        }
        if (tail)
        {
            // 尾调用即调用后返回：结果寄存器原样返回，调用方没有返回值时丢弃。
            var returnsValue = oracle.ReturnType is { } target && !ManagedTypes.IsVoid(target);
            if (returnsValue && result is null) return new Statement(new ManagedHole(call.Address, "调用 尾调用的被调方法没有返回值", true));
            output.Add(new ManagedCall(call.Address, invocation, returnsValue ? result : null) { Inlined = call.Inlined || shape.LocalCopy });
            return new Statement(new ManagedReturn(call.Address, returnsValue ? result : null));
        }
        var lowered = new ManagedCall(call.Address, invocation, result) { Inlined = call.Inlined || shape.LocalCopy };
        output.Add(lowered);
        // 提升的单元由调用写出的版本承接；其他结果缓冲区（不透明返回值、未提升的可寻址局部、堆上的位置）要把结果写到调用者给出的位置。
        if (result is not null && signature.Return is { Transfer: Transfer.ResultBuffer } buffer
            && call.ArgumentIndex(buffer.Pieces[0].Register) is var position && (buffer.Size < 0 || position >= 0 && !IsPromotedCell(arguments[position])))
        {
            if (position < 0 || oracle.Locate(arguments[position], buffer.Size < 0 ? -1 : buffer.Size * 8) is not { } home)
            {
                var reason = buffer.Size < 0 ? "调用 不透明返回值的位置未知" : $"调用 结果缓冲区的位置 {arguments[position]}";
                output.Add(new ManagedHole(call.Address, reason, false));
                holes.Add(new(call.Address, reason));
            }
            else if (LocationValue(home, call.Address, store: true) is var target)
                output.Add(target is LocalValue local ? new ManagedAssignment(call.Address, local, result) : new ManagedStore(call.Address, target, result));
        }
        if (call.DoesNotReturn && ManagedTypes.IsVoid(oracle.ReturnType))
            return new Statement(new ManagedReturn(call.Address, null));
        return null;
    }

    private static IEnumerable<BoundDelegateTarget> BoundMethods(DelegateTarget target) => target switch
    {
        BoundDelegateTarget method => [method],
        SelectedDelegateTarget selected => BoundMethods(selected.True).Concat(BoundMethods(selected.False)),
        MergedDelegateTarget merged => merged.Sources.SelectMany(BoundMethods),
        _ => throw new NotSupportedException(target.ToString()),
    };

    private ManagedValue DelegateChoiceValue(DelegateTarget target, IReadOnlyDictionary<BoundDelegateTarget, int> methods, ulong address)
    {
        switch (target)
        {
            case BoundDelegateTarget bound:
                return new ConstantValue((ulong)methods[bound], types.Int32, 32);
            case SelectedDelegateTarget selected:
                return new ChoiceValue(View(selected.Condition, types.Boolean, address), DelegateChoiceValue(selected.True, methods, address),
                    DelegateChoiceValue(selected.False, methods, address), types.Int32, 32);
            case MergedDelegateTarget merged:
                var local = NewLocal(types.Int32);
                delegateChoices.Add(new DelegateChoice(merged.Phi, local, merged.Sources, methods));
                return local;
            default:
                throw new NotSupportedException(target.ToString());
        }
    }

    /// <summary>
    /// 被调方不读取的按值实参：原生代码不维护这些寄存器，内容与参数类型不兼容时，接收者取类型兼容的 this，其他取默认值。
    /// 这是推断：被调方的行为与该实参无关，任何类型正确的值都等价。
    /// </summary>
    private ManagedValue? Unconstrained(Calls.CallShape shape, MachineValue value, TypeRef type, Sym content)
        => oracle.Supply(shape, value, type, content) switch
        {
            ArgumentSupply.This => new ArgumentValue(-1, oracle.Parameter(-1)!, Width(oracle.Parameter(-1)!)),
            ArgumentSupply.Default => types.Kind(type) == TypeKind.Struct ? new ComposedValue(type, []) : new ConstantValue(0, type, Width(type)),
            _ => null,
        };

    private ManagedValue? UnusedReceiver(Block block, int index, CallShape shape, MethodRef callee, MachineValue value, TypeRef type, Sym content, IReadOnlyList<Sym> arguments)
    {
        if (value.Index != -1 || !ManagedTypes.IsReferenceType(type) || shape.IsDispatch
            || shape.Call is not { Kind: CallKind.Direct, Target: Const target } call
            || oracle.Supply(shape, value, type, content) != ArgumentSupply.Default) return null;
        // 直接调用省略了 this 时，优先复用计算该调用实参时实际解引用的同类型对象。
        var receivers = arguments.Skip(1).Select(SymTable.Resolve).OfType<ReadSym>()
            .Select(read => oracle.Locate(read.Address, read.Width))
            .Where(location => location is { Kind: LocationKind.Instance, Base: not null })
            .Select(location => location!.Base!)
            .Where(owner => oracle.TypeOf(owner) is { } known && (known.Equals(type) || types.IsAssignable(known, type)))
            .Distinct().ToArray();
        if (receivers is [var argumentOwner] && templates.UnusedParameter(callee, target.Value, -1)) return View(argumentOwner, type, call.Address);
        if (guardedReceivers.TryGetValue(call, out var guarded)) return View(guarded, type, call.Address);
        // 未使用的 this 寄存器可以被 clang 丢弃；前驱的判空或非空分支可指名接收者。
        if (index != 0 || block.Predecessors is not [var previous] || !previous.NormalSuccessors.Contains(block)) return null;
        return previous.Statements.LastOrDefault(s => s is not (Ir.Jump or Nop)) switch
        {
            Call { Op.Kind: OpKind.NullCheck } check when values.Operands(check) is [_, var receiver]
                && oracle.TypeOf(receiver) is { } known && ManagedTypes.IsReferenceType(known)
                && (types.IsAssignable(type, known) || types.IsAssignable(known, type))
                && templates.UnusedParameter(callee, target.Value, -1) => View(receiver, type, call.Address),
            Ir.Branch branch when !callee.Method.IsOverridable && NonNullOnEdge(branch, block) is { } receiver
                && oracle.TypeOf(receiver) is { } known && (known.Equals(type) || types.IsAssignable(known, type))
                && templates.UnusedParameter(callee, target.Value, -1) => View(receiver, type, call.Address),
            _ => null,
        };
    }

    // 原生调用不读取 this，但 C# 调用会判空。复用支配调用点的非空值，
    // 在原读取位置保存字段，避免中间有其他调用时重新读取可能已经改变的字段。
    private void PlanGuardedReceivers()
    {
        foreach (var block in order)
        for (int i = 0; i < block.Statements.Count; i++)
        {
            if (block.Statements[i] is not Call { Kind: CallKind.Direct, Op: null, Target: Const target } call) continue;
            var shape = analysis.Environment.Shape(call);
            if (shape.Signature?.Arguments is not [{ Index: -1, Transfer: Transfer.Value, Pieces: [var piece] } slot, ..]
                || !shape.Unused.Contains(piece.Register) || shape.IsDispatch
                || oracle.Callee(call) is not { Method.IsOverridable: false, Method.IsStatic: false } callee
                || !ManagedTypes.IsReferenceType(callee.DeclaringType)) continue;
            var contents = values.ArgumentContent(call, slot, values.Operands(call).Skip(1).Take(call.Arguments.Length).ToArray());
            if (oracle.Supply(shape, slot, callee.DeclaringType, contents) != ArgumentSupply.Default
                || !templates.UnusedParameter(callee, target.Value, -1)
                || FindGuardedReceiver(block, i, callee.DeclaringType) is not { } receiver) continue;
            guardedReceivers.Add(call, receiver);
            if (receiver is ReadSym read) materialized.Add(read);
        }
    }

    private Sym? FindGuardedReceiver(Block use, int index, TypeRef type)
    {
        bool Compatible(Sym candidate) => candidate is ArgSym or ReadSym
            && oracle.TypeOf(candidate) is { } known && (known.Equals(type) || types.IsAssignable(known, type));
        var block = use;
        var dominance = sharedDominance ??= Machine.Ssa.Dominators.Compute(analysis.Function);
        while (true)
        {
            // 能经异常边到达使用点时，没有证据说明此前的判空成功。
            if (block == use || block.Handler is null || block.NormalSuccessors.Any(s => s.Predecessors.Count == 1 && dominance.Dominates(s.Index, use.Index)))
                for (var i = index - 1; i >= 0; i--)
                    if (block.Statements[i] is Call { Op.Kind: OpKind.NullCheck } check && values.Operands(check) is [_, var checkedValue]
                        && SymTable.Resolve(checkedValue) is var value && Compatible(value)) return value;
            if (block.Index == 0) return null;
            if (block.Predecessors is [var from] && from.Statements.LastOrDefault() is Ir.Branch branch
                && NonNullOnEdge(branch, block) is { } nonNull && SymTable.Resolve(nonNull) is var edgeValue && Compatible(edgeValue)) return edgeValue;
            block = analysis.Function.Blocks[dominance.Idom[block.Index]];
            index = block.Statements.Count;
        }
    }

    private Sym? NonNullOnEdge(Ir.Branch branch, Block successor)
    {
        if (branch.Target == branch.Fallthrough || values.Operands(branch) is not
            [OpSym { Op: { Kind: OperatorKind.Compare, Cond: Cond.Eq or Cond.Ne } comparison, Args: [var left, var right] }])
            return null;
        if (successor.Start != (comparison.Cond == Cond.Ne ? branch.Target : branch.Fallthrough)) return null;
        return (left, right) switch
        {
            (ConstSym { Value: 0 }, var receiver) => receiver,
            (var receiver, ConstSym { Value: 0 }) => receiver,
            _ => null,
        };
    }

    /// <summary>直接调用同类型值方法时，未使用的 this 可以留在被覆盖的寄存器中。</summary>
    private ManagedValue? UnusedValueReceiver(CallShape shape, MethodRef callee, MachineValue value, TypeRef type, Sym content)
    {
        if (shape.Call is not { Kind: CallKind.Direct, Op: null, Target: Const target }
            || value is not { Index: -1, Transfer: Transfer.Reference, Pieces: [var piece] }
            || type is not ByRefRef || oracle.Parameter(-1)?.Equals(type) != true || !shape.Unused.Contains(piece.Register)
            || oracle.TypeOf(content)?.Equals(type) == true || !templates.UnusedParameter(callee, target.Value, -1)) return null;
        return new ArgumentValue(-1, type, Width(type));
    }

    private ManagedValue? UnusedScalar(CallShape shape, MethodRef callee, MachineValue value, TypeRef type, Sym content)
    {
        if (shape.Call is not { Kind: CallKind.Direct, Target: Const target } || shape.IsDispatch
            || value is not { Transfer: Transfer.Value, Index: >= 0, Pieces: [var piece] })
            return null;
        var scalar = piece.Register.Bank == Bank.Gpr && piece.Width <= 32 && types.Kind(type) is TypeKind.Int or TypeKind.Bool
            || piece.Register.Bank == Bank.Vec && piece.Width is 32 or 64 && types.Kind(type) is TypeKind.Float or TypeKind.Double;
        if (!scalar) return null;
        while (SymTable.Resolve(content) is SliceSym { Low: 0 } slice)
            content = slice.Base;
        if (!OmittedRegister(SymTable.Resolve(content)) || !templates.UnusedParameter(callee, target.Value, value.Index)) return null;
        var parameter = MatchingParameter(callee, value.Index, type);
        return parameter >= 0 ? new ArgumentValue(parameter, type, Width(type))
            : shape.Unused.Contains(piece.Register) ? new ConstantValue(0, type, Width(type)) : null;
    }

    private static bool OmittedRegister(Sym source) => source is UndefSym || source is UnknownSym { Reason: var reason }
        && (reason.EndsWith("被调用破坏", StringComparison.Ordinal) || reason == "原生被调方未用的输入");

    /// <summary>尾调用可以省去不读取的按值形参。被上一次调用破坏的槽可由同名同类型的入口形参补齐。</summary>
    private ManagedValue? UnusedTailArgument(CallShape shape, MethodRef callee, MachineValue value, TypeRef type, Sym content)
    {
        if (shape.Call is not { Kind: CallKind.Tail, Op: null, Target: Const target }
            || value is not { Index: >= 0, Transfer: Transfer.Value, Pieces.Length: > 0 }
            || !value.Pieces.Any(p => shape.Unused.Contains(p.Register)) || !SymTable.Segments(content).All(Clobbered)) return null;
        var index = MatchingParameter(callee, value.Index, type);
        return index >= 0 && templates.UnusedParameter(callee, target.Value, value.Index) ? new ArgumentValue(index, type, Width(type)) : null;

        bool Clobbered(Sym segment)
        {
            while (SymTable.Resolve(segment) is SliceSym { Low: 0 } slice)
                segment = slice.Base;
            if (OmittedRegister(SymTable.Resolve(segment))) return true;
            // 未清理的机器结果中，签名不返回的寄存器只有调用破坏留下的值。
            return SymTable.Resolve(segment) is ResultSym { Site: var producer, Index: > 0 } result
                && analysis.Environment.Shape(producer) is { IsRuntime: false, Signature: { } signature }
                && (signature.Return?.Pieces.All(p => p.Register != producer.Results[result.Index - 1]) ?? true);
        }
    }

    /// <summary>直接转发时，原生代码可省略被调方不读取的栈实参。用已证明未读的同名形参补齐调用。</summary>
    private ManagedValue? UnusedStackArgument(Call call, MethodRef callee, MachineValue value, TypeRef type, Sym content)
    {
        if (call is not { Kind: CallKind.Direct, Target: Const target } || value is not { Index: >= 0, Transfer: Transfer.Value, Pieces.Length: > 0 }
            || value.Pieces.Any(p => p.Register.Bank != Bank.Stack) || SymTable.Resolve(content) is not UndefSym) return null;
        var index = MatchingParameter(callee, value.Index, type);
        return index >= 0 && templates.UnusedParameter(callee, target.Value, value.Index) ? new ArgumentValue(index, type, Width(type)) : null;
    }

    private int MatchingParameter(MethodRef callee, int index, TypeRef type)
    {
        var name = callee.Method.Parameters[index].Name;
        var parameters = analysis.Method.Method.Parameters;
        return Enumerable.Range(0, parameters.Count).FirstOrDefault(i => parameters[i].Name == name && oracle.Parameter(i)?.Equals(type) == true, -1);
    }

    /// <summary>编译器可省略被调方从不读取的值类型副本。</summary>
    private ManagedValue? UnusedCopyArgument(CallShape shape, MethodRef callee, MachineValue value, TypeRef type, Sym content)
    {
        if (shape.Call is not { Kind: CallKind.Direct, Op: null, Target: Const target }
            || value is not { Index: >= 0, Transfer: Transfer.Copy, Pieces: [var piece] } || types.Kind(type) != TypeKind.Struct
            || SymTable.Resolve(content) is not (UndefSym or UnknownSym) || !shape.Unused.Contains(piece.Register)
            || !templates.UnusedParameter(callee, target.Value, value.Index)) return null;
        return new ComposedValue(type, []);
    }

    /// <summary>尾转发省略了不读写的 ref bool，原寄存器仍保存调用方的隐藏 MethodInfo。</summary>
    private ManagedValue? UnusedReference(CallShape shape, MethodRef callee, MachineValue value, TypeRef type, Sym content, List<ManagedStatement> output)
    {
        if (shape.Call is not { Kind: CallKind.Direct or CallKind.Tail, Target: Const target } call
            || value is not { Index: >= 0, Transfer: Transfer.Reference, Pieces: [var piece] }
            || type is not ByRefRef { Element: var element } || !shape.Unused.Contains(piece.Register)) return null;
        var source = SymTable.Resolve(content);
        var omitted = call.Kind == CallKind.Direct && !shape.IsDispatch && OmittedRegister(source);
        var forwarded = call.Kind == CallKind.Tail && element.Equals(types.Boolean)
            && analysis.Signature.MethodInfo?.Register == piece.Register
            && source is EntitySym { Symbol: { Kind: SymbolKind.Method, Value: MethodRef hidden } } && hidden.Equals(analysis.Method);
        if ((!omitted && !forwarded) || !templates.UnusedParameter(callee, target.Value, value.Index)) return null;

        // 同一类型的 ref 实参已在当前签名中，直接传递原存储。
        var parameters = analysis.Method.Method.Parameters.Select((_, index) => index)
            .Where(index => oracle.Parameter(index)?.Equals(type) == true).ToArray();
        if (omitted && parameters is [var parameter]) return new ArgumentValue(parameter, type, Width(type));

        var local = NewLocal(element);
        var initial = types.Kind(element) == TypeKind.Struct ? (ManagedValue)new ComposedValue(element, [])
            : new ConstantValue(0, element, Width(element));
        output.Add(new ManagedAssignment(call.Address, local, initial));
        return new AddressValue(local);
    }

    /// <summary>
    /// 一个 IL 实参。按值传递时为内容的视图；按副本传递时为被指单元在调用前的内容；
    /// ref 实参指向已提升的单元时成为局部：调用前写入原内容，调用写出的值即该局部，再取其地址。
    /// </summary>
    private ManagedValue Argument(Call call, MachineValue value, TypeRef type, Sym content, IReadOnlyList<Sym> arguments, List<ManagedStatement> output)
    {
        switch (value.Transfer)
        {
            case Transfer.Value:
                return View(content, type, call.Address);
            case Transfer.Copy:
                {
                    var stored = values.CellContent(call, value, arguments, write: false);
                    if (stored is not null) return View(stored, type, call.Address);
                    return SymTable.Resolve(content) is var p && oracle.Locate(p, value.Size < 0 ? -1 : value.Size * 8) is { } location
                        ? LocationValue(location, call.Address) : Hole(call.Address, "调用 副本实参的内容未知", type);
                }
            case Transfer.Reference:
                {
                    var element = type is ByRefRef byRef ? byRef.Element : type;
                    // 不透明值没有固定位宽，只按位置定位。
                    var elementBits = ManagedAbi.SharedValue(element) ? -1 : types.Kind(element) == TypeKind.Struct ? types.Size(element) * 8 : Math.Max(8, Width(element));
                    if (SymTable.Resolve(content) is LocalSym escaped && analysis.Memory.Find(escaped.Space, escaped.Offset, 1) is { Promoted: false })
                        return oracle.Locate(escaped, elementBits) is { } escapedLocation
                            ? new AddressValue(LocationValue(escapedLocation, call.Address)) : Hole(call.Address, $"调用 ref 实参指向的可寻址局部 {escaped}", type);
                    if (SymTable.Resolve(content) is LocalSym)
                    {
                        var local = NewLocal(element);
                        if (values.CellContent(call, value, arguments, write: false) is { } before)
                            output.Add(new ManagedAssignment(call.Address, local, View(before, element, call.Address)));
                        if (call.Memory.Any(m => m.Write && m.Value == value.Index))
                            locals[values.Syms.Out(call, value.Index, value.Size * 8)] = local;
                        return new AddressValue(local);
                    }
                    if (oracle.TypeOf(content) is ByRefRef or PointerRef) return View(content, type, call.Address);
                    return oracle.Locate(content, value.Size < 0 ? -1 : Math.Max(8, value.Size * 8)) is { } target
                        ? new AddressValue(LocationValue(target, call.Address)) : Hole(call.Address, $"调用 ref 实参的位置 {content}", type);
                }
            default:
                return Hole(call.Address, $"调用 实参传递方式 {value.Transfer}", type);
        }
    }

    private bool IsPromotedCell(Sym pointer) =>
        SymTable.Resolve(pointer) is LocalSym local && analysis.Memory.Find(local.Space, local.Offset, 1) is { Promoted: true };

    private Terminator? LowerRuntime(Block block, int index, Call call, RuntimeOp op, IReadOnlyList<Sym> arguments, List<ManagedStatement> output)
    {
        var tail = call.Kind is CallKind.Tail or CallKind.TailIndirect;
        var context = new RuntimeContext(this, block, index, call, op, arguments, output);
        if (RuntimeOps.Of(op.Kind)?.Lower is { } lower && lower(context, out var terminal))
        {
            if (terminal is not null) return new Statement(terminal);
            if (!tail) return null;
            // 运行时尾调用与普通尾调用相同：结果寄存器按当前方法的返回类型解释。
            var returned = oracle.ReturnType is { } type && !ManagedTypes.IsVoid(type) ? View(context.Result, type, call.Address) : null;
            return new Statement(new ManagedReturn(call.Address, returned));
        }
        holes.Add(new(call.Address, $"运行时 {op.Kind}"));
        if (tail) return new Statement(new ManagedHole(call.Address, $"运行时 {op}", true));
        output.Add(new ManagedHole(call.Address, $"运行时 {op}", false));
        return null;
    }

    /// <summary>运行时操作下降的上下文。</summary>
    private sealed class RuntimeContext(BodyBuilder owner, Block block, int index, Call call, RuntimeOp op, IReadOnlyList<Sym> arguments, List<ManagedStatement> output) : IRuntimeLowering
    {
        public ManagedTypes Types => owner.types;
        public Call Call => call;
        public RuntimeOp Op => op;
        public TypeRef? ClassOperand => owner.oracle.ClassOperand(call);
        public IReadOnlyList<Sym> Arguments { get; } = arguments.Take(call.OriginalArgumentCount).ToArray();
        public Sym Result => owner.values.Syms.Result(call, 0, RuntimeOps.Of(op.Kind)?.ResultWidths?.Invoke(call) is [var width] ? width : 64);
        public Sym ResultAt(int result) => owner.values.Syms.Result(call, result + 1, RuntimeOps.Of(op.Kind)!.ResultWidths!(call)[result]);
        public ManagedValue Input(int index)
        {
            var input = RuntimeOps.Of(op.Kind)!.Inputs!(call, Arguments, owner.oracle)[index];
            if (input.Kind == RuntimeInputKind.Value)
                return owner.View(input.Width > 0 ? owner.values.Syms.Slice(input.Value, input.Low, input.Width) : input.Value, input.Type, call.Address);
            if (input.Kind == RuntimeInputKind.Pointee && owner.values.CellContent(call, input.Pointee, arguments, false) is { } content)
                return owner.View(input.Width > 0 ? owner.values.Syms.Slice(content, input.Low, input.Width) : content, input.Type, call.Address);
            return input.Type is { } type && (owner.oracle.Locate(input.Value, owner.types.StorageBits(type)) ?? owner.oracle.TypedPointerAddress(input.Value, type)) is { } location
                ? owner.LocationValue(location, call.Address) : owner.Hole(call.Address, "运行时 输入位置未知", input.Type);
        }
        public void Assign(int index, ManagedValue value)
        {
            var input = RuntimeOps.Of(op.Kind)!.Inputs!(call, Arguments, owner.oracle)[index];
            if (input.Type is not { } type || (owner.oracle.Locate(input.Value, owner.types.StorageBits(type)) ?? owner.oracle.TypedPointerAddress(input.Value, type)) is not { } location)
            {
                output.Add(new ManagedHole(call.Address, "运行时 输出位置未知", false));
                owner.holes.Add(new(call.Address, "运行时 输出位置未知"));
                return;
            }
            var target = owner.LocationValue(location, call.Address, store: true);
            output.Add(target is LocalValue local ? new ManagedAssignment(call.Address, local, value) : new ManagedStore(call.Address, target, value));
        }
        public TypeRef? TypeOf(Sym sym) => owner.oracle.TypeOf(sym);
        public LocalValue Local(Sym sym, TypeRef type) => owner.Local(sym, type);
        public LocalValue Temporary(TypeRef type) => owner.NewLocal(type);
        public void Emit(ManagedStatement statement) => output.Add(statement);
        public void AwaitConstruction()
        {
            owner.constructions[(ResultSym)Result] = call;
            if (ReSekai.Decompiler.Recovery.Effects.ConstructionPrelude.Find(owner.analysis, owner.oracle, owner.effects, call) is { } prelude)
            {
                var local = owner.Local(Result, prelude.Type);
                owner.pendingConstructions[local.Variable] = new(prelude.Address, prelude.Type, prelude.Base);
            }
        }
        public MethodRef? ExceptionConstructor(string fullName) => owner.Constructor(fullName);
        public ManagedValue Hole(string reason, TypeRef? type) => owner.Hole(call.Address, reason, type);

        public bool InFault => owner.regions.Any(r => !r.IsCatch && r.Handler.Contains(block));

        public Sym? ClassOwner(Sym klass) => owner.oracle.ClassOwner(klass);

        public bool InitializesNext(TypeRef type) => owner.effects.Initializes(block, index + 1, type);

        public bool InCatchOf(Sym exception) => owner.regions.Any(r => r.IsCatch && r.Handler.Contains(block)
            && SymTable.Resolve(exception) is ReadSym read && owner.oracle.Decompose(read.Address) is (ResultSym { Site: var begin }, 0, null, _) && ReferenceEquals(begin, r.BeginCatch));

        public bool KnownNonNull(Sym target) => owner.effects.KnownNonNull(block, index, target);

        public bool AbsorbBounds(Sym position, Sym length) => owner.effects.BoundsAbsorbed(block, index + 1, position, length);

        public bool Absorb(Sym target)
        {
            return owner.effects.Absorbed(block, index + 1, target, owner.absorbedChecks);
        }

        public ManagedValue Field(ManagedValue receiver, FieldRef field) => owner.FieldAccess(receiver, field, owner.FieldType(field, null), true, call.Address);
    }

    private MethodRef? Constructor(string exception) => oracle.ExceptionType(exception) is { } type
        ? type.Definition.Methods.FirstOrDefault(m => m.Name == ".ctor" && !m.IsStatic && m.Parameters.Count == 0) is { } ctor ? new MethodRef(ctor, [], []) : null
        : null;
}
