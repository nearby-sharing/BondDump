using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace BondDump;

internal sealed class InvokeRecursiveExpression(Expression lambdaIndex, ReadOnlyCollection<Expression> arguments) : Expression
{
    public Expression LambdaIndex { get; } = lambdaIndex;
    public ReadOnlyCollection<Expression> Arguments { get; } = arguments;

    public override ExpressionType NodeType => ExpressionType.Invoke;
    public override Type Type => typeof(object);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        return new InvokeRecursiveExpression(
            visitor.Visit(LambdaIndex),
            visitor.Visit(Arguments)
        );
    }
}
