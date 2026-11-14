using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ReSekai.Decompiler.Output;

/// <summary>语句与边的 CIL 写法：每种语句一处；边上的复制能排序时写成顺序赋值，否则先读出全部值再写入目标局部。</summary>
internal sealed partial class CilBodyWriter
{
    private ulong statementAddress;

    /// <summary>元素下标：int32 原样，64 位下标转为 native int（ECMA-335 III.4.7）。</summary>
    private void ElementIndex(ManagedValue index)
    {
        Value(index);
        if (index.Width == 64) il.Emit(OpCodes.Conv_I);
    }

    private void Statement(ManagedStatement statement)
    {
        statementAddress = statement.Address;
        switch (statement)
        {
            case ManagedAssignment a: Value(a.Value); il.Emit(OpCodes.Stloc, Local(a.Target)); break;
            case ManagedStore { Target: IndirectValue i, Value: ComposedValue { Leaves.Count: 0 } }:
                Value(i.Address);
                var initialization = Instruction.Create(OpCodes.Initobj, TypeReference(i.Type));
                il.Append(initialization); ExplicitInitializations.Add(initialization); break;
            case ManagedStore { Target: IndirectValue i } s:
                Value(i.Address); Value(s.Value); IndirectAlignment(i); il.Emit(OpCodes.Stobj, TypeReference(i.Type)); break;
            // 结构体字段清零写成 ldflda; initobj，和 C# 编译器一样；反编译器认状态机清理被提升的局部时只认这个写法。
            case ManagedStore { Target: FieldValue { Accessor: null } f, Value: ComposedValue { Leaves.Count: 0 } }:
                Value(f.Receiver); il.Emit(OpCodes.Ldflda, FieldReference(f.Field)); il.Emit(OpCodes.Initobj, TypeReference(f.Type)); break;
            case ManagedStore { Target: FieldValue f } s:
                // 值类型的 setter 接收原存储的地址，才能把写入保留在该字段中。
                if (f.Accessor is { Method.Method.Owner.IsValueType: true } && f.Receiver.Type is not ByRefRef)
                    Address(f.Receiver);
                else Value(f.Receiver);
                Value(s.Value);
                if (f.Accessor is { } setter) AccessorCall(setter); else il.Emit(OpCodes.Stfld, FieldReference(f.Field)); break;
            case ManagedStore { Target: StaticFieldValue f } s:
                Value(s.Value);
                if (f.Accessor is { } staticSetter) AccessorCall(staticSetter); else il.Emit(OpCodes.Stsfld, FieldReference(f.Field)); break;
            case ManagedStore { Target: ArrayElementValue element } s:
                Value(element.Array); ElementIndex(element.Index); Value(s.Value);
                il.Emit(OpCodes.Stelem_Any, TypeReference(element.Type)); break;
            case ManagedStore { Target: MultiArrayElementValue element } s:
                Value(element.Array); foreach (var index in element.Indices) Value(index); Value(s.Value);
                il.Emit(OpCodes.Call, ArrayMethod((ArrayRef)element.Array.Type, "Set")); break;
            case ManagedCall c:
                var call = Invocation(c.Invocation);
                if (c.Inlined) InlineCalls.Add(call, c.Invocation.Method);
                if (c.Result is { } result) il.Emit(OpCodes.Stloc, Local(result));
                else if (!ManagedTypes.IsVoid(c.Invocation.Method.Method.ReturnType)) il.Emit(OpCodes.Pop);
                break;
            case ManagedReturn r:
                if (r.Value is { } returned) Value(returned);
                ReturnFromBlock(); break;
            case ManagedJump j: Edge(j.Edge); break;
            case ManagedBranch b:
                var taken = Instruction.Create(OpCodes.Nop);
                Value(b.Condition); il.Emit(OpCodes.Brtrue, taken);
                Edge(b.Fallthrough);
                il.Append(taken); Edge(b.Taken); break;
            case ManagedSwitch w:
                {
                    // 越界不可达（原生代码在前驱中检查了上界）；落空时走第 0 项，保持每条路径都有出边。
                    var cases = w.Cases.Select(_ => Instruction.Create(OpCodes.Nop)).ToArray();
                    Value(w.Index); il.Emit(OpCodes.Switch, cases);
                    Edge(w.Cases[0]);
                    for (var i = 0; i < cases.Length; i++) { il.Append(cases[i]); Edge(w.Cases[i]); }
                    break;
                }
            case ManagedThrow t: Value(t.Exception); il.Emit(OpCodes.Throw); break;
            case ManagedRethrow: il.Emit(OpCodes.Rethrow); break;
            case ManagedEndFault: il.Emit(OpCodes.Endfinally); break;
            case ManagedAwait awaited:
                Value(awaited.Task);
                var helpers = Awaits();
                if (awaited.Result is { } awaitedResult)
                {
                    il.Emit(OpCodes.Call, Generic(helpers.Of, awaited.Task.Type, awaitedResult.Type));
                    il.Emit(OpCodes.Stloc, Local(awaitedResult));
                }
                else il.Emit(OpCodes.Call, Generic(helpers.Do, awaited.Task.Type));
                break;
            case ManagedHole hole:
                il.Emit(OpCodes.Ldc_I8, unchecked((long)hole.Address));
                if (hole.Terminal) { il.Emit(OpCodes.Call, Unrecovered().Fail); il.Emit(OpCodes.Throw); }
                else il.Emit(OpCodes.Call, Unrecovered().At);
                break;
            case ManagedCheck check:
                var pass = Instruction.Create(OpCodes.Nop);
                Value(check.Condition); il.Emit(OpCodes.Brfalse, pass);
                il.Emit(OpCodes.Newobj, MethodReference(check.Exception)); il.Emit(OpCodes.Throw);
                il.Append(pass); break;
            default: throw new NotSupportedException(statement.ToString());
        }
    }

    /// <summary>骨架的占位体以 ldnull; throw 结尾，前面可能有基类构造调用，那部分要留着才能编译。</summary>
    internal void MarkUnrecovered(MethodDefinition method, ulong address, bool timedOut)
    {
        if (method.Body?.Instructions is not { Count: >= 2 } instructions || instructions[^1].OpCode != OpCodes.Throw || instructions[^2].OpCode != OpCodes.Ldnull) return;
        destination = method;
        var il = method.Body.GetILProcessor();
        var location = il.Create(OpCodes.Ldc_I8, unchecked((long)address));
        il.Replace(instructions[^2], location);
        var marker = Unrecovered();
        il.InsertAfter(location, il.Create(OpCodes.Call, timedOut ? marker.TimeOut : marker.Fail));
    }

    /// <summary>洞标记只携带原生地址，完整原因留在 recovery.jsonl，避免诊断文本撑爆 #US 字符串堆。</summary>
    private (MethodReference At, MethodReference Fail, MethodDefinition Value, MethodReference TimeOut) Unrecovered()
    {
        var module = destination.Module;
        if (unrecovered.TryGetValue(module, out var known)) return known;
        var type = new TypeDefinition("ReSekai.Decompiler", "Unrecovered", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, TypeReference(types.Object));
        module.Types.Add(type);
        MethodDefinition Method(string name, TypeReference returns)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, returns);
            method.Parameters.Add(new ParameterDefinition("address", ParameterAttributes.None, TypeReference(types.UInt64)));
            type.Methods.Add(method);
            return method;
        }
        var exception = TypeReference(types.Corlib("System.Exception"));
        var fail = Method("Fail", exception);
        var timeOut = Method("TimeOut", exception);
        ExceptionFactory(fail, "System.InvalidOperationException", "Unrecovered IL2CPP code at 0x");
        ExceptionFactory(timeOut, "System.TimeoutException", "IL2CPP recovery timed out at 0x");

        var value = Method("Value", TypeReference(types.Object));
        var parameter = new GenericParameter("T", value);
        value.GenericParameters.Add(parameter);
        value.ReturnType = parameter;
        var at = Method("At", TypeReference(types.Corlib("System.Void")));
        foreach (var marker in new[] { at, value })
        {
            var processor = marker.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Call, fail);
            processor.Emit(OpCodes.Throw);
        }
        var created = (at, fail, value, timeOut);
        unrecovered.Add(module, created);
        return created;

        void ExceptionFactory(MethodDefinition method, string exceptionType, string prefix)
        {
            var text = TypeReference(types.String);
            var format = new MethodReference("ToString", text, TypeReference(types.UInt64)) { HasThis = true };
            format.Parameters.Add(new ParameterDefinition(text));
            var concat = new MethodReference("Concat", text, text);
            concat.Parameters.Add(new ParameterDefinition(text));
            concat.Parameters.Add(new ParameterDefinition(text));
            var ctor = new MethodReference(".ctor", TypeReference(types.Corlib("System.Void")), TypeReference(types.Corlib(exceptionType))) { HasThis = true };
            ctor.Parameters.Add(new ParameterDefinition(text));
            var processor = method.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldstr, prefix);
            processor.Emit(OpCodes.Ldarga, method.Parameters[0]);
            processor.Emit(OpCodes.Ldstr, "x16");
            processor.Emit(OpCodes.Call, format);
            processor.Emit(OpCodes.Call, concat);
            processor.Emit(OpCodes.Newobj, ctor);
            processor.Emit(OpCodes.Ret);
        }
    }

    private GenericInstanceMethod Generic(MethodDefinition method, params TypeRef[] arguments)
    {
        var instance = new GenericInstanceMethod(method);
        foreach (var argument in arguments) instance.GenericArguments.Add(TypeReference(argument));
        return instance;
    }

    /// <summary>
    /// async 提升（源码视图提升）的标记：每个模块一个 ReSekai.Decompiler.Await 静态类，C# 输出时改写成 await / return
    /// （Of&lt;TTask,TResult&gt;、Do&lt;TTask&gt; 是 await，Result、Completed 是 async 方法的 return；void 完成使用非泛型重载）。方法体只抛出。
    /// </summary>
    private (MethodDefinition Of, MethodDefinition Do, MethodDefinition Result, MethodDefinition Completed, MethodDefinition CompletedVoid) Awaits()
    {
        var module = destination.Module;
        if (awaits.TryGetValue(module, out var known)) return known;
        var type = new TypeDefinition("ReSekai.Decompiler", "Await", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, TypeReference(types.Object));
        module.Types.Add(type);
        MethodDefinition Method(string name, string[] parameters, Func<GenericParameter[], TypeReference> returns, Func<GenericParameter[], TypeReference[]> arguments)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, TypeReference(types.Corlib("System.Void")));
            var generic = parameters.Select(p => new GenericParameter(p, method)).ToArray();
            foreach (var parameter in generic) method.GenericParameters.Add(parameter);
            method.ReturnType = returns(generic);
            foreach (var argument in arguments(generic)) method.Parameters.Add(new ParameterDefinition(argument));
            var processor = method.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldnull); processor.Emit(OpCodes.Throw);
            type.Methods.Add(method);
            return method;
        }
        var created = (Method("Of", ["TTask", "TResult"], g => g[1], g => [g[0]]), Method("Do", ["TTask"], _ => TypeReference(types.Corlib("System.Void")), g => [g[0]]),
            Method("Result", ["TTask", "TResult"], g => g[0], g => [g[1]]), Method("Completed", ["TTask"], g => g[0], _ => []),
            Method("Completed", [], _ => TypeReference(types.Corlib("System.Void")), _ => []));
        awaits.Add(module, created);
        return created;
    }

    private void Edge(ManagedEdge edge)
    {
        if (Sequential(edge.Copies) is { } order)
            foreach (var copy in order)
            {
                Value(copy.Value);
                il.Emit(OpCodes.Stloc, Local(copy.Target));
            }
        else
        {
            foreach (var copy in edge.Copies) Value(copy.Value);
            for (var i = edge.Copies.Count - 1; i >= 0; i--) il.Emit(OpCodes.Stloc, Local(edge.Copies[i].Target));
        }
        il.Emit(LeavesRegion(edge.Target) ? OpCodes.Leave : OpCodes.Br, labels[edge.Target]);
    }

    /// <summary>
    /// 边上的复制是并行赋值。值不抛出、只读局部和常量时，可以排成顺序赋值：先写没有其他复制还要读的目标。有环时没有这样的次序。
    /// 取过地址的局部可能经引用读到，值里没有引用读取就不受影响。
    /// </summary>
    private static List<ManagedCopy>? Sequential(IReadOnlyList<ManagedCopy> copies)
    {
        if (copies.Count == 1) return [copies[0]];
        if (copies.Any(c => ManagedValues.Walk(c.Value).Any(v => v is not (LocalValue or ArgumentValue or ConstantValue or BinaryValue { Op: not (ReSekai.Decompiler.Ir.BinOp.SDiv or ReSekai.Decompiler.Ir.BinOp.UDiv or ReSekai.Decompiler.Ir.BinOp.SRem or ReSekai.Decompiler.Ir.BinOp.URem) } or ConversionValue)))) return null;
        var reads = copies.Select(c => ManagedValues.Walk(c.Value).OfType<LocalValue>().Select(l => l.Variable).ToHashSet()).ToArray();
        var pending = Enumerable.Range(0, copies.Count).ToList();
        var order = new List<ManagedCopy>(copies.Count);
        while (pending.Count > 0)
        {
            var next = pending.FindIndex(i => pending.All(j => j == i || !reads[j].Contains(copies[i].Target.Variable)));
            if (next < 0) return null;
            order.Add(copies[pending[next]]);
            pending.RemoveAt(next);
        }
        return order;
    }

    private Instruction Invocation(ManagedInvocation invocation)
    {
        // 虚调用的接收者是类型参数的值时走受约束调用（ECMA-335 III.2.1），CIL 才合法，C# 也不必转换。值在局部或参数里才有地址可取。
        // 非虚调用不改：受约束调用要判空，原生的这类调用不判空。
        var through = ThroughConstraint(invocation);
        var explicitImplementation = ExplicitValueTypeImplementation(invocation);
        var parameter = through?.Parameter
            ?? (invocation is { Virtual: true, Constraint: null, Receiver: { Type: GenericParameterRef open } and (LocalValue or ArgumentValue) } ? open : null);
        if (parameter is not null && invocation.Receiver!.Type is GenericParameterRef) Address(invocation.Receiver);
        else if (invocation.Receiver is { } receiver) Value(receiver);
        for (var i = 0; i < invocation.Arguments.Count; i++)
        {
            var argument = invocation.Arguments[i];
            Value(argument);
            // 类型参数的值传给引用类型的形参要装箱（引用类型的实例上是空操作），CIL 才合法。
            if (argument.Type is GenericParameterRef { IsReferenceConstrained: false } argumentType && i < invocation.Method.Method.Parameters.Count
                && invocation.Method.Concrete(invocation.Method.Method.Parameters[i].Type) is var formal && formal is not (GenericParameterRef or ByRefRef or PointerRef)
                && ManagedTypes.IsReferenceType(formal))
                il.Emit(OpCodes.Box, TypeReference(argumentType));
        }
        if (((invocation.Constraint ?? parameter) ?? explicitImplementation?.Type) is { } constraint) il.Emit(OpCodes.Constrained, TypeReference(constraint));
        var call = Instruction.Create(invocation.Virtual || through is not null || explicitImplementation is not null ? OpCodes.Callvirt : OpCodes.Call,
            MethodReference(through?.Method ?? explicitImplementation?.Method ?? OnReceiverInstance(invocation)));
        il.Append(call);
        return call;
    }

    /// <summary>结构体的显式接口实现只能经接口从外部调用；保留原地址与同一个 vtable 实现，生成 constrained. 调用。</summary>
    private static (DefinitionRef Type, MethodRef Method)? ExplicitValueTypeImplementation(ManagedInvocation invocation)
    {
        var method = invocation.Method.Method;
        if (invocation is not { Virtual: false, Constraint: null, Receiver.Type: ByRefRef { Element: DefinitionRef type } }
            || !type.Definition.IsValueType || type.Definition != method.Owner || !method.IsVirtual || !method.Name.Contains('.')) return null;
        foreach (var (@interface, offset) in method.Owner.InterfaceOffsets)
        {
            if (@interface.Substitute(type.Arguments, []) is not DefinitionRef implemented) continue;
            foreach (var member in implemented.Definition.Methods)
            {
                var slot = offset + member.Slot;
                if (member.Slot >= 0 && slot >= 0 && slot < method.Owner.VTable.Count && method.Owner.VTable[slot]?.Method == method
                    && method.Name.EndsWith("." + member.Name, StringComparison.Ordinal))
                    return (type, new MethodRef(member, implemented.Arguments, invocation.Method.MethodArguments));
            }
        }
        return null;
    }

    /// <summary>
    /// 泛型定义上的方法体来自值类型实例时，原生直接调用实参类型自己的方法（如 Start&lt;TStateMachine&gt; 里状态机的 MoveNext）。
    /// 接收者是类型参数的存储，被调方法按 vtable 实现了约束里的接口方法时，写成对接口方法的受约束调用：值类型自己实现了该方法时，
    /// 以存储地址直接调用，不装箱也不判空（ECMA-335 III.2.1），和原生相同；泛型定义也不再引用某个实例的具体类型。
    /// 约束是泛型接口时只接受和实例的接口完全相同的写法，其余保持原样。
    /// </summary>
    private static (GenericParameterRef Parameter, MethodRef Method)? ThroughConstraint(ManagedInvocation invocation)
    {
        if (invocation is not { Virtual: false, Constraint: null, Receiver: { } receiver }) return null;
        var parameter = receiver switch
        {
            { Type: ByRefRef { Element: GenericParameterRef open } } => open,
            LocalValue or ArgumentValue when receiver.Type is GenericParameterRef open => open,
            _ => null,
        };
        var method = invocation.Method.Method;
        if (parameter?.Parameter is null || method.IsStatic || !method.Owner.IsValueType) return null;
        var owner = method.Owner;
        for (var slot = 0; slot < owner.VTable.Count; slot++)
        {
            if (owner.VTable[slot]?.Method != method) continue;
            foreach (var (implemented, offset) in owner.InterfaceOffsets)
            {
                if (implemented is not DefinitionRef itf || slot < offset) continue;
                if (itf.Definition.Methods.FirstOrDefault(m => m.Slot == slot - offset && !m.IsStatic) is not { } declared) continue;
                var constraint = parameter.Parameter.Constraints.OfType<DefinitionRef>().FirstOrDefault(c => c.Definition == itf.Definition && c.Arguments.SequenceEqual(itf.Arguments));
                if (constraint is not null) return (parameter, new MethodRef(declared, constraint.Arguments, []));
            }
        }
        return null;
    }

    /// <summary>
    /// 泛型定义上的方法体来自某个实例，原生调用目标是实例上的方法（如 Action&lt;Int32Enum, int, bool&gt;.Invoke、Func&lt;Task&lt;具体类型&gt;&gt;.Invoke）。
    /// 接收者的静态类型是同一泛型定义的开放实例时（类型参数可以嵌在实参里），调用写在接收者的实例上：对象是同一个，
    /// 而共享占位类型在 C# 里写不出来，具体实例又会让泛型定义引用别的程序集。
    /// </summary>
    private static MethodRef OnReceiverInstance(ManagedInvocation invocation) =>
        invocation.Receiver?.Type is DefinitionRef { Arguments: var open } receiver && receiver.Definition == invocation.Method.Method.Owner
        && open.Any(MentionsParameter) && !open.SequenceEqual(invocation.Method.TypeArguments)
            ? invocation.Method with { TypeArguments = open } : invocation.Method;

    private static bool MentionsParameter(TypeRef type) => type switch
    {
        GenericParameterRef => true,
        DefinitionRef definition => definition.Arguments.Any(MentionsParameter),
        ArrayRef array => MentionsParameter(array.Element),
        PointerRef pointer => MentionsParameter(pointer.Element),
        ByRefRef reference => MentionsParameter(reference.Element),
        _ => false,
    };
}
