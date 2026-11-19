using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace ReSekai.Decompiler.Output;

public static partial class Decompilation
{
    /// <summary>CIL 局部的默认值在函数入口初始化一次，跳入标签时保留跳转前的赋值。</summary>
    private sealed class LabelAwareDeclarations : DeclareVariables, IAstTransform
    {
        void IAstTransform.Run(AstNode rootNode, TransformContext context)
        {
            if (!rootNode.Descendants.OfType<LabelStatement>().Any())
            {
                base.Run(rootNode, context);
                return;
            }

            var explicitDefaults = rootNode.Descendants.OfType<DefaultValueExpression>().ToHashSet(ReferenceEqualityComparer.Instance);
            base.Run(rootNode, context);
            foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToArray())
            {
                if (declaration.Variables.Count != 1
                    || declaration.Variables.First() is not { Initializer: DefaultValueExpression initial } local
                    || explicitDefaults.Contains(initial) || EntryLabel(declaration) is not { } label
                    || declaration.Type is ComposedType { HasRefSpecifier: true }) continue;
                var function = declaration.Ancestors.OfType<BlockStatement>().FirstOrDefault(block =>
                    block.Parent is EntityDeclaration or Accessor or LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement);
                if (function is null || !function.Descendants.OfType<GotoStatement>().Any(jump => jump.Label == label.Label)
                    || function.Descendants.OfType<VariableInitializer>().Count(other => other.Name == local.Name) != 1) continue;

                declaration.Remove();
                function.Statements.InsertBefore(function.Statements.FirstOrDefault(), declaration);
            }
        }

        private static LabelStatement? EntryLabel(Statement declaration)
        {
            for (var node = declaration.PrevSibling; node is not null; node = node.PrevSibling)
            {
                if (node is LabelStatement label) return label;
                if (node is Statement and not VariableDeclarationStatement) return null;
            }
            return null;
        }
    }
}
