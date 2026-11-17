using ICSharpCode.Decompiler.CSharp.Syntax;
using ReSekai.Decompiler.Output;

static class TerminalValueChecks
{
    public static void Run()
    {
        foreach (bool intervening in new[] { false, true })
        {
            var body = new BlockStatement();
            body.Statements.Add(new VariableDeclarationStatement(new SimpleType("T"), "holder", new DefaultValueExpression(new SimpleType("T"))));
            body.Statements.Add(new VariableDeclarationStatement(new ComposedType { BaseType = new SimpleType("T"), HasRefSpecifier = true },
                "selected", new DirectionExpression(FieldDirection.Ref, new IdentifierExpression("holder"))));
            var binding = new ExpressionStatement(new AssignmentExpression(new IdentifierExpression("selected"),
                new DirectionExpression(FieldDirection.Ref, new IdentifierExpression("input"))));
            var branch = new BlockStatement();
            branch.Statements.Add(binding);
            body.Statements.Add(new IfElseStatement(new IdentifierExpression("condition"), branch));
            if (intervening) body.Statements.Add(new ExpressionStatement(new InvocationExpression(new IdentifierExpression("MutateInput"))));
            body.Statements.Add(new ReturnStatement(new IdentifierExpression("selected")));
            Apply(body);
            bool retainsReference = body.Descendants.OfType<DirectionExpression>().Any();
            if (retainsReference != intervening) throw new Exception("引用返回转换越过求值或未消除末尾引用");
            if (!intervening && branch.Statements.Single() is not ReturnStatement { Expression: IdentifierExpression { Identifier: "input" } })
                throw new Exception("引用返回值不正确");
        }
        foreach (bool intervening in new[] { false, true })
        {
            var body = new BlockStatement();
            body.Statements.Add(new VariableDeclarationStatement(new SimpleType("Exception"), "error"));
            body.Statements.Add(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression("error"),
                new ObjectCreateExpression(new SimpleType("Exception"), new PrimitiveExpression("message")))));
            if (intervening) body.Statements.Add(new ExpressionStatement(new InvocationExpression(new IdentifierExpression("Observe"), new IdentifierExpression("error"))));
            body.Statements.Add(new ThrowStatement(new IdentifierExpression("error")));
            Apply(body);
            bool inline = body.Statements.OfType<ThrowStatement>().Single().Expression is ObjectCreateExpression;
            if (inline == intervening) throw new Exception("异常临时变量转换越过用途或未合并相邻抛出");
        }
    }

    static void Apply(BlockStatement body)
    {
        var tree = new SyntaxTree();
        tree.Members.Add(new MethodDeclaration { Name = "Sample", ReturnType = new SimpleType("T"), Body = body });
        new Decompilation.TerminalValues().Run(tree, null!);
    }
}
