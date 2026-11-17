using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace ReSekai.Decompiler.Output;

public static partial class Decompilation
{
    /// <summary>
    /// 特性的类型是别的程序集的内部类型时源码访问不到（CS0122）。这类特性只是构建工具留下的标记，
    /// 去掉它不影响方法体。元数据里的特性不变，只改源码。
    /// </summary>
    private sealed class InaccessibleAttributes : IAstTransform
    {
        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var attribute in rootNode.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.Attribute>().ToArray())
            {
                if (attribute.Type.Annotation<TypeResolveResult>()?.Type.GetDefinition() is not { } type
                    || ReferenceEquals(type.ParentModule, context.TypeSystem.MainModule) || type.Accessibility == Accessibility.Public) continue;
                if (type.Accessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal
                    && type.ParentModule?.InternalsVisibleTo(context.TypeSystem.MainModule) == true) continue;
                var section = (AttributeSection)attribute.Parent!;
                attribute.Remove();
                if (!section.Attributes.Any()) section.Remove();
            }
        }
    }

    /// <summary>
    /// 接口实现上的元组元素名称存在 InterfaceImpl 的特性里，IL2CPP 的元数据没有保留，方法参数上的名称还在。
    /// C# 要求两处一致（CS8141），按方法签名把名称补回基类型列表。
    /// </summary>
    private sealed class InterfaceTupleNames : IAstTransform
    {
        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var type in rootNode.Descendants.OfType<TypeDeclaration>())
            {
                var declarations = type.BaseTypes.Concat(type.Members.OfType<MethodDeclaration>().Select(m => m.PrivateImplementationType))
                    .Concat(type.Members.OfType<PropertyDeclaration>().Select(p => p.PrivateImplementationType));
                var unnamed = declarations.OfType<AstType>().SelectMany(b => b.DescendantsAndSelf.OfType<TupleAstType>()).Where(t => t.Elements.All(e => string.IsNullOrEmpty(e.Name))).ToArray();
                if (unnamed.Length == 0) continue;
                var named = type.Members.OfType<MethodDeclaration>().SelectMany(m => m.Parameters.Select(p => p.Type).Append(m.ReturnType))
                    .Concat(type.Members.OfType<PropertyDeclaration>().Select(p => p.ReturnType))
                    .OfType<AstType>().SelectMany(t => t.DescendantsAndSelf.OfType<TupleAstType>()).Where(t => t.Elements.Any(e => !string.IsNullOrEmpty(e.Name))).ToArray();
                foreach (var tuple in unnamed)
                {
                    var shape = tuple.Elements.Select(e => e.Type.ToString()).ToArray();
                    if (named.FirstOrDefault(t => t.Elements.Select(e => e.Type.ToString()).SequenceEqual(shape)) is not { } source) continue;
                    foreach (var (to, from) in tuple.Elements.Zip(source.Elements)) to.Name = from.Name;
                }
            }
        }
    }

    // 被裁掉 Property 表记录的显式访问器仍需按 C# 属性写出。
    private sealed class ExplicitAccessors : IAstTransform
    {
        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var method in rootNode.Descendants.OfType<MethodDeclaration>().Where(m => m.PrivateImplementationType is not null && m.Name.StartsWith("get_", StringComparison.Ordinal) && m.Parameters.Count == 0).ToArray())
            {
                var property = new PropertyDeclaration { Name = method.Name[4..], ReturnType = method.ReturnType.Clone(), PrivateImplementationType = method.PrivateImplementationType!.Clone(), Getter = new Accessor { Body = (BlockStatement?)method.Body?.Clone() } };
                method.ReplaceWith(property);
            }
        }
    }

    // 原版 Unity 库包含裁剪引用中不存在的同名类型，限定外部声明避免重新绑定时歧义。
    private sealed class ExternalTypeNames : IAstTransform
    {
        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var syntax in rootNode.Descendants.OfType<SimpleType>().ToArray())
            {
                if (syntax.Annotation<TypeResolveResult>()?.Type is not { } type || type.GetDefinition() is null || type.Namespace.Length == 0) continue;
                if (type.Namespace == syntax.GetParent<NamespaceDeclaration>()?.Name) continue;
                var qualified = QualifiedTypes.ConvertType(type);
                foreach (var member in qualified.DescendantsAndSelf.OfType<MemberType>().Where(m => !m.IsDoubleColon && m.Target is SimpleType).ToArray())
                {
                    var first = (SimpleType)member.Target;
                    member.Target = new MemberType(new SimpleType("global"), first.Identifier!) { IsDoubleColon = true };
                }
                syntax.ReplaceWith(qualified);
            }
        }
    }
}
