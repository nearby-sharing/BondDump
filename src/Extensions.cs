using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace BondDump;

internal static class Extensions
{
    extension(CSharpSyntaxNode obj)
    {
        public StatementSyntax ToStatement() => obj switch
        {
            StatementSyntax stmt => stmt,
            ExpressionSyntax expr => ExpressionStatement(expr),
            _ => throw new InvalidCastException()
        };

        public BlockSyntax ToStatements() => obj.ToStatement() switch
        {
            BlockSyntax block => block,
            var stmt => Block(List([stmt])),
        };
    }

    extension<T>(IEnumerable<T?> source)
    {
        public IEnumerable<T> WhereNotNull()
        {
            foreach (var item in source)
                if (item is not null)
                    yield return item;
        }
    }

    public static string Join<T>(this IEnumerable<T> values, char separator)
        => string.Join(separator, values);
}
