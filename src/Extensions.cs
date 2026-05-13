using System.CodeDom;
using System.CodeDom.Compiler;

namespace BondDump;

internal static class Extensions
{
    extension(CodeObject obj)
    {
        public CodeStatement ToStatement() => obj switch
        {
            CodeStatement stmt => stmt,
            CodeSnippetExpression { Value: null or "" } => new CodeSnippetStatement(),
            CodeBinaryOperatorExpression { Operator: CodeBinaryOperatorType.Assign } assignExpr => new CodeAssignStatement(assignExpr.Left, assignExpr.Right),
            CodeExpression expr => new CodeExpressionStatement(expr),
            _ => throw new InvalidCastException()
        };

        public CodeStatement[] ToStatements() => obj.ToStatement() switch
        {
            CodeConditionStatement { Condition: CodePrimitiveExpression { Value: true } } block => [.. block.TrueStatements.Cast<CodeStatement>()
                .Where(x => x is not CodeSnippetStatement { Value: null or "" })
            ],
            CodeSnippetStatement { Value: null or "" } => [],
            var stmt => [stmt]
        };
    }

    extension(CodeDomProvider provider)
    {
        public string Compile(CodeCompileUnit unit, CodeGeneratorOptions? options = null)
        {
            using StringWriter writer = new();
            using IndentedTextWriter indentedWriter = new(writer);
            provider.GenerateCodeFromCompileUnit(unit, indentedWriter, options ?? new());
            return writer.ToString();
        }
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
}
