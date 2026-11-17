using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace ReSekai.Decompiler.Output;

public static partial class Decompilation
{
    internal sealed class TerminalValues : IAstTransform
    {
        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var method in rootNode.Descendants.OfType<MethodDeclaration>())
            {
                if (method.Body is null) continue;
                foreach (var declaration in method.Body.Descendants.OfType<VariableDeclarationStatement>().ToArray())
                {
                    if (declaration.Variables.Count != 1) continue;
                    var variable = declaration.Variables.Single();
                    var uses = method.Body.Descendants.OfType<IdentifierExpression>().Where(i => i.Identifier == variable.Name).ToArray();
                    if (declaration.Type is ComposedType { HasRefSpecifier: true }
                        && variable.Initializer is DirectionExpression { Expression: IdentifierExpression holder }
                        && declaration.Parent is BlockStatement block
                        && block.Statements.OfType<VariableDeclarationStatement>().Any(d => d.Variables.Any(v => v.Name == holder.Identifier && v.Initializer is DefaultValueExpression))
                        && method.Body.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == holder.Identifier) == 1)
                    {
                        var returns = uses.Where(i => i.Parent is ReturnStatement).ToArray();
                        var bindings = uses.Where(i => i.Parent is AssignmentExpression a && a.Left == i && Reference(a.Right)
                            && a.Parent is ExpressionStatement).ToArray();
                        if (returns.Length != 1 || bindings.Length == 0 || returns.Length + bindings.Length != uses.Length) continue;
                        var exit = (ReturnStatement)returns[0].Parent!;
                        if (!bindings.All(i => Tail((Statement)i.Parent!.Parent!, exit))) continue;
                        foreach (var binding in bindings)
                        {
                            var assignment = (AssignmentExpression)binding.Parent!;
                            assignment.Parent!.ReplaceWith(new ReturnStatement(Value(assignment.Right)));
                        }
                        exit.Expression = holder.Clone();
                        declaration.Remove();
                    }
                    else if (variable.Initializer is null && uses.Length > 0)
                    {
                        var assignments = uses.Where(i => i.Parent is AssignmentExpression { Right: ObjectCreateExpression } a && a.Left == i
                            && a.Parent is ExpressionStatement statement && statement.NextSibling is ThrowStatement { Expression: IdentifierExpression thrown }
                            && thrown.Identifier == variable.Name).ToArray();
                        if (assignments.Length * 2 != uses.Length || uses.Except(assignments).Any(i => i.Parent is not ThrowStatement)) continue;
                        foreach (var use in assignments)
                        {
                            var assignment = (AssignmentExpression)use.Parent!;
                            var statement = (ExpressionStatement)assignment.Parent!;
                            ((ThrowStatement)statement.NextSibling!).Expression = assignment.Right.Clone();
                            statement.Remove();
                        }
                        declaration.Remove();
                    }
                }
            }
        }

        static bool Reference(Expression expression) => expression is DirectionExpression { FieldDirection: FieldDirection.Ref }
            || expression is ConditionalExpression c && Reference(c.TrueExpression) && Reference(c.FalseExpression);

        static Expression Value(Expression expression) => expression switch
        {
            DirectionExpression d => Value(d.Expression),
            ConditionalExpression c => new ConditionalExpression(c.Condition.Clone(), Value(c.TrueExpression), Value(c.FalseExpression)),
            _ => expression.Clone()
        };

        // 引用选择之后只允许退出分支并返回，不提前越过其他求值。
        static bool Tail(Statement statement, ReturnStatement exit)
        {
            while (statement.Parent is BlockStatement block)
            {
                var after = block.Statements.SkipWhile(s => s != statement).Skip(1).ToArray();
                if (after.Length == 1 && after[0] == exit) return true;
                if (after.Length != 0 || block.Parent is not IfElseStatement branch) return false;
                statement = branch;
            }
            return false;
        }
    }
}
