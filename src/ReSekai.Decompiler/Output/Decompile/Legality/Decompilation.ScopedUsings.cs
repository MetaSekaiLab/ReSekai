using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace ReSekai.Decompiler.Output;

public static partial class Decompilation
{
    /// <summary>goto 不能跳过同一个块里的 using 声明（CS8648、CS8649）：块里有 goto 时写成带大括号的 using 语句，资源的作用域不变。</summary>
    private sealed class ScopedUsings : IAstTransform
    {
        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var resource in rootNode.Descendants.OfType<UsingStatement>().Where(u => u.IsEnhanced).ToArray())
                if (resource.Parent is BlockStatement block && block.DescendantNodes(n => n is not (LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement))
                        .OfType<GotoStatement>().Any())
                    resource.IsEnhanced = false;
        }
    }
}
