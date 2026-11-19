namespace ReSekai.Decompiler.Facts;

public sealed record NativeMethodEntry(MethodRef Instance, ulong Address);

/// <summary>
/// 方法实例到原生入口的索引：非泛型方法取元数据中的地址，泛型实例取注册表中的入口；
/// 实例自身没有入口时取共享实现（引用类型的实参换成 System.Object，枚举换成同底层类型的共享枚举，IL2CPP 的共享规则）。
/// </summary>
public sealed class MethodEntries
{
    private readonly ManagedTypes types;
    private readonly Dictionary<(MethodRef Method, string Binding), ulong> addresses = [];
    private readonly Dictionary<MethodDef, NativeMethodEntry> definitions;

    public MethodEntries(ProgramFacts facts, ManagedTypes types)
    {
        this.types = types;
        foreach (var method in facts.Model.Methods.Where(m => m.Address != 0)) Add(new MethodRef(method, [], []), method.Address);
        // 调整桩、调用器和反向包装具有不同 ABI，不能用作方法本体的恢复入口。
        var implementations = facts.Registrations.Entries.Where(e => e.Kind is EntryKind.Method or EntryKind.GenericMethod
            && e.Method is not null && e.Address != 0).ToArray();
        foreach (var entry in implementations) Add(entry.Method!, entry.Address);
        var ordinary = facts.Model.Methods.Where(m => m.Address != 0).Select(m => new NativeMethodEntry(new(m, [], []), m.Address));
        var shared = implementations.Where(e => e.Kind == EntryKind.GenericMethod && e.Method!.Method.Address == 0)
            .Select(e => new NativeMethodEntry(e.Method!, e.Address));
        definitions = ordinary.Concat(shared).GroupBy(e => e.Instance.Method).ToDictionary(g => g.Key, SelectDefinition);
    }

    private void Add(MethodRef method, ulong address)
    {
        var key = (method, NativeBindings.Key(method));
        if (!addresses.TryGetValue(key, out var previous) || address < previous) addresses[key] = address;
    }

    /// <summary>写出定义的固定见证。与调用方顺序、请求范围无关；显式方法清单可另行指定入口。</summary>
    public NativeMethodEntry? ForDefinition(MethodDef method) => definitions.GetValueOrDefault(method);

    public IEnumerable<NativeMethodEntry> Definitions => definitions.Values.OrderBy(e => e.Instance.Method.Index);

    // 保留引用共享入口的优先级。类型参数没有 struct 约束时，优先使用保留类型参数自身 ABI 的完全共享入口。
    internal static NativeMethodEntry SelectDefinition(IEnumerable<NativeMethodEntry> candidates)
    {
        var entries = candidates.ToArray();
        int? sharedReturnSlot = SharedEnumReturnSlot(entries);
        return entries.OrderBy(e => e.Instance.TypeArguments.Concat(e.Instance.MethodArguments)
                .Any(a => !ManagedTypes.IsReferenceType(a) || FieldLayouts.IsFullyShared(a)))
            .ThenBy(e => CopiesNativeArrayIntoView(e.Instance.Method) && !e.Instance.TypeArguments.Any(FieldLayouts.IsFullyShared)
                || SortsGenericPointerElements(e.Instance.Method) && !e.Instance.MethodArguments.All(FieldLayouts.IsFullyShared))
            .ThenBy(e => sharedReturnSlot is int slot && (e.Instance.MethodArguments.ElementAtOrDefault(slot) is not { } argument
                || !FieldLayouts.IsFullyShared(argument)))
            .ThenBy(e => e.Instance.Method.Owner.GenericParameters.Concat(e.Instance.Method.GenericParameters)
                .Any(p => (p.Attributes & System.Reflection.GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                || e.Instance.TypeArguments.Concat(e.Instance.MethodArguments).Any(a => !FieldLayouts.IsFullyShared(a) && !ManagedTypes.IsReferenceType(a)))
            .ThenBy(e => e.Address).ThenBy(e => NativeBindings.Key(e.Instance), StringComparer.Ordinal).First();
    }

    /// <summary>同一个 T 既按值传入又按值返回时，完全共享入口保留跨枚举宽度的值复制。</summary>
    private static int? SharedEnumReturnSlot(NativeMethodEntry[] entries)
    {
        var method = entries[0].Instance.Method;
        if (method.ReturnType is not GenericParameterRef { IsMethod: true } result
            || !method.Parameters.Any(p => p.Type.Equals(result))) return null;
        var arguments = entries.Select(e => e.Instance.MethodArguments.ElementAtOrDefault(result.Position)).OfType<DefinitionRef>();
        return arguments.Where(a => a.Definition.IsEnum && a.Definition.Size > 0)
            .Select(a => a.Definition.Size).Distinct().Skip(1).Any() ? result.Position : null;
    }

    /// <summary>从 NativeArray&lt;T&gt; 建立值类型视图时，完整共享实现保留 sizeof(T) 的来源。</summary>
    private static bool CopiesNativeArrayIntoView(MethodDef method) => method is
        {
            Name: ".ctor", Owner: { IsValueType: true, GenericParameters.Count: 1 },
            Parameters: [_, _, _]
        } && method.Parameters[0].Type is DefinitionRef
        {
            Definition.FullName: "Unity.Collections.NativeArray",
            Arguments: [GenericParameterRef { IsMethod: false, Position: 0 }]
        } && method.Parameters.Skip(1).All(p => ManagedTypes.PrimitiveName(p.Type) == "Int32");

    /// <summary>按泛型比较器操作无类型指针时，共享实现保留元素布局和比较器分派。</summary>
    private static bool SortsGenericPointerElements(MethodDef method) => method is
    {
        IsStatic: true, Parameters: [{ Type: PointerRef { Element: var pointed } }, _, _, { Type: GenericParameterRef { IsMethod: true, Position: 1 } }],
        GenericParameters: [_, var comparer]
    } && ManagedTypes.IsVoid(pointed)
        && comparer.Constraints.Any(c => c is DefinitionRef { Definition.FullName: "System.Collections.Generic.IComparer",
            Arguments: [GenericParameterRef { IsMethod: true, Position: 0 }] });

    /// <summary>方法实例的原生入口；没有时为 0。</summary>
    public ulong Of(MethodRef method) => Lookup(method) is var address and not 0 ? address : Witnessed(method) is { } witnessed ? Lookup(witnessed) : 0;

    private ulong Lookup(MethodRef method) =>
        addresses.TryGetValue((method, NativeBindings.Key(method)), out var address)
        || Shared(method) is { } shared && addresses.TryGetValue((shared, NativeBindings.Key(shared)), out address) ? address : 0;

    /// <summary>
    /// 实参里绑定了原生布局的泛型参数换成布局类型：调用方由某个实例的原生代码恢复时，内联进来的被调方法也是这个实例的代码。
    /// 只用于查入口；恢复仍按带绑定的托管身份进行。
    /// </summary>
    private static MethodRef? Witnessed(MethodRef method)
    {
        var witnessed = method with { TypeArguments = method.TypeArguments.Select(Layout).ToArray(), MethodArguments = method.MethodArguments.Select(Layout).ToArray() };
        return NativeBindings.Key(witnessed) == NativeBindings.Key(method) ? null : witnessed;
        static TypeRef Layout(TypeRef type) => type switch
        {
            GenericParameterRef { Binding.Layout: { } layout } => layout,
            DefinitionRef { Arguments.Count: > 0 } generic => generic with { Arguments = generic.Arguments.Select(Layout).ToArray() },
            ArrayRef array => array with { Element = Layout(array.Element) },
            _ => type,
        };
    }

    private MethodRef? Shared(MethodRef method)
    {
        var shared = method with { TypeArguments = method.TypeArguments.Select(types.SharedGenericArgument).ToArray(), MethodArguments = method.MethodArguments.Select(types.SharedGenericArgument).ToArray() };
        return shared.Equals(method) ? null : shared;
    }
}
