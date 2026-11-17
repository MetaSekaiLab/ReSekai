using System.Reflection;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using ReSekai.Decompiler.Model;
using ReSekai.Decompiler.Output;

namespace ReSekai;

sealed class SerializedFieldNames(ProgramModel model) : IDecompileTransformProvider, IAstTransform
{
    readonly Dictionary<string, string> names = Create(model);

    public string Name(FieldDef field) => names.GetValueOrDefault(field.Owner.FullName + "." + field.Name, field.Name);
    public bool Contains(TypeDef type) => type.Fields.Any(f => names.ContainsKey(type.FullName + "." + f.Name));

    static bool Serialized(FieldDef field) => (field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.InitOnly | (FieldAttributes)128)) == 0
        && ((field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public
            || field.CustomAttributes.HasAttribute("UnityEngine.SerializeField") || field.CustomAttributes.HasAttribute("UnityEngine.SerializeReference"));

    static Dictionary<string, string> Create(ProgramModel model)
    {
        Dictionary<string, string> result = [];
        foreach (var type in model.Types)
        {
            HashSet<string> inherited = [];
            for (var parent = type.BaseType as DefinitionRef; parent is not null; parent = parent.Definition.BaseType as DefinitionRef)
                foreach (var field in parent.Definition.Fields.Where(Serialized)) inherited.Add(field.Name);
            foreach (var field in type.Fields.Where(f => Serialized(f) && inherited.Contains(f.Name)))
                result[type.FullName + "." + field.Name] = "__resekai_" + field.Name + "_" + field.Index;
        }
        return result;
    }

    public void Register(CSharpDecompiler decompiler, DecompilePosition position)
    {
        if (position == DecompilePosition.AfterEvents) decompiler.AstTransforms.Add(this);
    }

    public void Run(AstNode rootNode, TransformContext context)
    {
        string? Name(AstNode node) => node.GetSymbol() is IField { DeclaringTypeDefinition: { } owner } field
            ? names.GetValueOrDefault(owner.FullName + "." + field.Name) : null;
        foreach (var declaration in rootNode.Descendants.OfType<FieldDeclaration>())
            foreach (var variable in declaration.Variables)
                if ((Name(variable) ?? Name(declaration)) is { } name)
                {
                    variable.Name = name;
                    declaration.Modifiers &= ~Modifiers.New;
                }
        foreach (var expression in rootNode.Descendants.OfType<Expression>())
        {
            if (Name(expression) is not { } name) continue;
            if (expression is IdentifierExpression identifier) identifier.Identifier = name;
            else if (expression is MemberReferenceExpression member) member.MemberName = name;
        }
    }
}
