using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace BondDump;

internal static class SyntaxUtils
{
    static SyntaxGenerator SyntaxGenerator { get; } = SyntaxGenerator.GetGenerator(new AdhocWorkspace(), LanguageNames.CSharp);

    public static ExpressionSyntax LiteralExpression(object? value)
        => (ExpressionSyntax)SyntaxGenerator.LiteralExpression(value);

    public static TypeSyntax Type(Type type)
    {
        return ParseTypeName(TypeName(type));

        static string TypeName(Type type)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(type.IsArray, false);
            ArgumentOutOfRangeException.ThrowIfNotEqual(type.IsGenericParameter, false);

            string baseType = StripGenericSuffix(type.Name);
            Type currentType = type;
            while (currentType.IsNested)
            {
                currentType = type.DeclaringType!;
                baseType = StripGenericSuffix(currentType.Name) + "." + baseType;
            }

            if (!string.IsNullOrEmpty(type.Namespace))
                baseType = type.Namespace + "." + baseType;

            if (type.IsGenericType)
            {
                baseType += $"<{type.GetGenericArguments().Select(TypeName).Join(',')}>";
            }

            return baseType;
        }

        static string StripGenericSuffix(string name)
        {
            var index = name.IndexOf('`');
            if (index < 0)
                return name;

            return name[..index];
        }
    }

    public static ExpressionSyntax BinaryExpressionEx(SyntaxKind op, ExpressionSyntax left, ExpressionSyntax right)
    {
        if (op == SyntaxKind.SimpleAssignmentExpression)
            return AssignmentExpression(op, left, right);

        return BinaryExpression(op, left, right);
    }
}
