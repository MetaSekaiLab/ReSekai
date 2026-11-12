using Mono.Cecil;
using Mono.Cecil.Rocks;

namespace ReSekai.Decompiler.Output;

public static partial class SourceReferences
{
    // Await 使用 UnsafeOnCompleted 时，普通接口入口可能被裁掉；编辑器的接口仍要求两者。
    private static void AddAwaiterMembers(IReadOnlyList<ModuleDefinition> modules, HashSet<ModuleDefinition> changed)
    {
        foreach (var module in modules)
        foreach (var type in module.GetTypes())
        {
            if (type.IsInterface || !type.Interfaces.Any(i => i.InterfaceType.FullName is "System.Runtime.CompilerServices.INotifyCompletion" or "System.Runtime.CompilerServices.ICriticalNotifyCompletion")
                || type.Methods.Any(m => m.Name == "OnCompleted")
                || type.Methods.FirstOrDefault(m => m.Name == "UnsafeOnCompleted" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Action") is not { } source) continue;
            var method = new MethodDefinition("OnCompleted", source.Attributes, source.ReturnType);
            method.Parameters.Add(new ParameterDefinition("continuation", ParameterAttributes.None, source.Parameters[0].ParameterType));
            Throwing(method);
            type.Methods.Add(method);
            changed.Add(module);
        }
    }

    // 元数据丢失 unmanaged 标记，指针声明及其泛型实参仍能证明约束。
    private static void AddUnmanagedConstraints(IReadOnlyList<ModuleDefinition> modules, HashSet<ModuleDefinition> changed)
    {
        var types = modules.SelectMany(m => m.GetTypes()).ToArray();
        var signatures = types.SelectMany(t => t.Fields.Select(f => f.FieldType)
            .Concat(t.Methods.SelectMany(m => m.Parameters.Select(p => p.ParameterType).Append(m.ReturnType)))
            .Concat(t.Interfaces.Select(i => i.InterfaceType)).Concat(t.BaseType is null ? [] : new[] { t.BaseType })).ToArray();
        HashSet<GenericParameter> required = [];
        var instances = new List<GenericInstanceType>();
        void Visit(TypeReference type)
        {
            if (type is PointerType { ElementType: GenericParameter parameter }) required.Add(parameter);
            if (type is GenericInstanceType instance)
            {
                instances.Add(instance);
                foreach (var argument in instance.GenericArguments) Visit(argument);
            }
            else if (type is TypeSpecification specification) Visit(specification.ElementType);
        }
        foreach (var signature in signatures) Visit(signature);
        bool added;
        do
        {
            added = false;
            foreach (var instance in instances)
            {
                var definition = instance.Resolve();
                for (int i = 0; i < definition.GenericParameters.Count; i++)
                    if (required.Contains(definition.GenericParameters[i]) && instance.GenericArguments[i] is GenericParameter argument) added |= required.Add(argument);
            }
        } while (added);
        foreach (var parameter in required)
        {
            if (parameter.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute")) continue;
            var module = parameter.Module;
            var attribute = new TypeReference("System.Runtime.CompilerServices", "IsUnmanagedAttribute", module, module.TypeSystem.CoreLibrary);
            parameter.CustomAttributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true }));
            parameter.Attributes |= GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint;
            changed.Add(module);
        }
    }
}
