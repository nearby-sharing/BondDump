using System.Buffers.Binary;
using System.CodeDom;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;

namespace BondDump;

// Inspired by https://medium.com/@canerten/linq-expression-trees-lambdas-to-codedom-conversion-8e434aa06380

public sealed class CodeDomExpressionVisitor
{
    public static CodeDomExpressionVisitor Instance => field ??= new();

    Scope _currentScope;
    public CodeDomExpressionVisitor()
    {
        _currentScope = new() { Parent = null };
    }

    public CodeMemberMethod Convert(LambdaExpression lambda, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(lambda.TailCall, false);

        _currentScope = new() { Parent = null };

        CodeMemberMethod method = new()
        {
            Name = name,
            ReturnType = new(lambda.ReturnType),
        };
        method.Parameters.AddRange([.. lambda.Parameters.Select(x => new CodeParameterDeclarationExpression(new CodeTypeReference(x.Type), x.Name))]);
        method.Statements.AddRange(Visit(lambda.Body) switch
        {
            CodeExpression expression => [new CodeMethodReturnStatement(expression)],
            var obj => obj.ToStatements()
        });
        return method;
    }

    CodeObject Visit(Expression exp) => exp switch
    {
        null => null!, // ToDo
        ConstantExpression constantExpression => VisitConstant(constantExpression),
        UnaryExpression unaryExpression => VisitUnary(unaryExpression),
        BinaryExpression binaryExpression => VisitBinary(binaryExpression),
        NewExpression newExpression => VisitNew(newExpression),
        IndexExpression indexExpression => VisitIndex(indexExpression),
        MethodCallExpression callExpression => VisitMethodCall(callExpression),
        InvocationExpression invocationExpression => VisitInvocation(invocationExpression),
        MemberExpression memberExpression => VisitMemberAccess(memberExpression),
        DefaultExpression defaultExpression => VisitDefault(defaultExpression),
        ParameterExpression parameterExpression => VisitParameter(parameterExpression),
        // Statments //
        GotoExpression gotoExpression => VisitGoTo(gotoExpression),
        ConditionalExpression conditionalExpression => VisitConditional(conditionalExpression),
        LoopExpression loopExpression => VisitLoop(loopExpression),
        TryExpression tryExpression => VisitTry(tryExpression),
        BlockExpression blockExpression => VisitBlock(blockExpression),
        _ => throw new NotImplementedException($"Expression of type {exp.GetType()} is not supported")
    };

    private static CodeExpression VisitConstant(ConstantExpression c)
    {
        if (c.Value == null)
        {
            return new CodePrimitiveExpression(null);
        }
        else if (c.Value.GetType().IsPrimitive || c.Value.GetType() == typeof(string))
        {
            return new CodePrimitiveExpression(c.Value);
        }
        else if (c.Value.GetType().IsEnum)
        {
            return new CodeFieldReferenceExpression(
                targetObject: new CodeTypeReferenceExpression(c.Value.GetType()),
                fieldName: Enum.GetName(c.Value.GetType(), c.Value)
            );
        }
        else
        {
            // CompactBinary{Writer, Reader} does not use the metadata
            if (c.Type.FullName == "Bond.Metadata")
                return new CodePrimitiveExpression(null);

            throw new NotImplementedException();
        }
    }

    private CodeObject VisitUnary(UnaryExpression unary)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(unary.Method, null);

        var operand = (CodeExpression)Visit(unary.Operand);
        return unary.NodeType switch
        {
            ExpressionType.Convert => new CodeCastExpression(unary.Type, operand),
            ExpressionType.Increment => new CodeBinaryOperatorExpression(operand, CodeBinaryOperatorType.Add, new CodePrimitiveExpression(value: 1)),
            ExpressionType.PreIncrementAssign => new CodeBinaryOperatorExpression(operand, CodeBinaryOperatorType.Assign, new CodeBinaryOperatorExpression(operand, CodeBinaryOperatorType.Add, new CodePrimitiveExpression(value: 1))),
            ExpressionType.PreDecrementAssign => new CodeBinaryOperatorExpression(operand, CodeBinaryOperatorType.Assign, new CodeBinaryOperatorExpression(operand, CodeBinaryOperatorType.Subtract, new CodePrimitiveExpression(value: 1))),
            _ => throw new NotImplementedException($"Unary operation {unary.NodeType} not supported")
        };
    }

    private CodeBinaryOperatorExpression VisitBinary(BinaryExpression binary)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(binary.Conversion, null);

        binary = FixLoopConditionVisitor.Execute(binary);

        return new CodeBinaryOperatorExpression(
            (CodeExpression)Visit(binary.Left),
            BindOperant(binary.NodeType),
            (CodeExpression)Visit(binary.Right)
        );
    }

    private static CodeBinaryOperatorType BindOperant(ExpressionType type) => type switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => CodeBinaryOperatorType.Add,
        ExpressionType.And => CodeBinaryOperatorType.BitwiseAnd,
        ExpressionType.AndAlso => CodeBinaryOperatorType.BooleanAnd,
        ExpressionType.Or => CodeBinaryOperatorType.BitwiseOr,
        ExpressionType.OrElse => CodeBinaryOperatorType.BooleanOr,
        ExpressionType.Equal => CodeBinaryOperatorType.IdentityEquality,
        ExpressionType.NotEqual => CodeBinaryOperatorType.IdentityInequality,
        ExpressionType.GreaterThan => CodeBinaryOperatorType.GreaterThan,
        ExpressionType.GreaterThanOrEqual => CodeBinaryOperatorType.GreaterThanOrEqual,
        ExpressionType.LessThan => CodeBinaryOperatorType.LessThan,
        ExpressionType.LessThanOrEqual => CodeBinaryOperatorType.LessThanOrEqual,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => CodeBinaryOperatorType.Multiply,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => CodeBinaryOperatorType.Subtract,
        ExpressionType.Power or ExpressionType.Divide => CodeBinaryOperatorType.Divide,
        ExpressionType.Modulo => CodeBinaryOperatorType.Modulus,
        ExpressionType.Assign => CodeBinaryOperatorType.Assign,
        _ => throw new NotImplementedException($"Operator {type} is not implemented"),
    };

    private CodeObjectCreateExpression VisitNew(NewExpression newExpression)
    {
        var args = VisitExpressionList(newExpression.Arguments);
        return new CodeObjectCreateExpression(
            newExpression.Type,
            [.. args]
        );
    }

    private CodeIndexerExpression VisitIndex(IndexExpression index) => new(
        targetObject: (CodeExpression)Visit(index.Object),
        [.. index.Arguments.Select(Visit).Cast<CodeExpression>()]
    );

    private CodeMethodInvokeExpression VisitMethodCall(MethodCallExpression m)
    {
        CodeObject obj = Visit(m.Object);
        var args = Enumerable.Zip(VisitExpressionList(m.Arguments), m.Method.GetParameters())
            .Select((arg) => arg.Second switch
            {
                { IsIn: true } => new CodeDirectionExpression(FieldDirection.In, arg.First),
                { IsOut: true } => new CodeDirectionExpression(FieldDirection.Out, arg.First),
                // ToDo: Ref??
                _ => arg.First,
            });

        if (obj == null)
        {  //static method call
            return new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(m.Method.DeclaringType), m.Method.Name, [.. args]);
        }
        else
        {
            return new CodeMethodInvokeExpression((CodeExpression)obj, m.Method.Name, [.. args]);
        }
    }

    private CodeObject VisitInvocation(InvocationExpression invocation)
        => Visit(InlineLambdaVisitor.Execute(invocation));

    private CodeObject VisitMemberAccess(MemberExpression member)
    {
        var receiver = (CodeExpression)Visit(member.Expression);
        return member switch
        {
            { Member: FieldInfo field } => new CodeFieldReferenceExpression(receiver, field.Name),
            { Member: PropertyInfo property } => new CodePropertyReferenceExpression(receiver, property.Name),
            _ => throw new NotImplementedException()
        };
    }

    private static CodeObject VisitDefault(DefaultExpression expr)
    {
        if (expr.Type == typeof(void))
            return new CodeSnippetExpression();

        return new CodeDefaultValueExpression(new CodeTypeReference(expr.Type));
    }

    private CodeObject VisitGoTo(GotoExpression exp) => exp.Kind switch
    {
        GotoExpressionKind.Goto => new CodeGotoStatement(exp.Target.Name),
        GotoExpressionKind.Return => new CodeMethodReturnStatement((CodeExpression)Visit(exp.Value)),
        GotoExpressionKind.Break => new CodeSnippetExpression("break"),
        GotoExpressionKind.Continue => new CodeSnippetExpression("continue"),
        _ => throw new NotImplementedException(),
    };

    private CodeConditionStatement VisitConditional(ConditionalExpression c) => new(
        (CodeExpression)Visit(c.Test),
        Visit(c.IfTrue).ToStatements(),
        Visit(c.IfFalse).ToStatements()
    );

    private CodeIterationStatement VisitLoop(LoopExpression loop) => new(
        initStatement: new CodeSnippetStatement(),
        testExpression: new CodeSnippetExpression(),
        incrementStatement: new CodeSnippetStatement(),
        Visit(loop.Body).ToStatements()
    );

    private CodeTryCatchFinallyStatement VisitTry(TryExpression exp)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(exp.Handlers.Count, 0);
        ArgumentOutOfRangeException.ThrowIfNotEqual(exp.Fault, null);

        return new(
            tryStatements: Visit(exp.Body).ToStatements(),
            catchClauses: [],
            finallyStatements: Visit(exp.Finally).ToStatements()
        );
    }

    private CodeObject VisitBlock(BlockExpression block)
    {
        var oldScope = _currentScope;
        _currentScope = new() { Parent = oldScope, Variables = [.. block.Variables.Select(x => x.Name).WhereNotNull()] };
        try
        {
            List<CodeStatement> statements = [];
            foreach (var variable in block.Variables)
            {
                statements.Add(new CodeVariableDeclarationStatement(new CodeTypeReference(variable.Type), _currentScope.Mangle(variable.Name)));
            }
            foreach (var expr in block.Expressions)
            {
                statements.AddRange(Visit(expr).ToStatements());
            }
            return new CodeConditionStatement(new CodePrimitiveExpression(value: true), [.. statements]);
        }
        finally
        {
            _currentScope = oldScope;
        }
    }

    private ReadOnlyCollection<CodeExpression> VisitExpressionList(ReadOnlyCollection<Expression> original)
        => [.. original.Select(Visit).Cast<CodeExpression>()];

    private CodeArgumentReferenceExpression VisitParameter(ParameterExpression p)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p.IsByRef, false);

        // ToDo: arg vs var
        return new CodeArgumentReferenceExpression(_currentScope.Mangle(p.Name));
    }

    sealed record Scope
    {
        public required Scope? Parent { get; init; }
        public HashSet<string> Variables { get; init; } = [];

        public string? Mangle(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            if (Variables.Contains(name))
                return $"_{HexHashcode}_{MangleCore(name)}";

            if (Parent is { } parent)
                return parent.Mangle(name);

            return name;
        }

        private string HexHashcode
        {
            get
            {
                if (field is { } result)
                    return result;

                Span<byte> buffer = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(buffer, GetHashCode());
                return field = System.Convert.ToHexStringLower(buffer);
            }
        }

        private static string MangleCore(string value) => string.Create(length: value.Length, state: value, static (dest, src) =>
        {
            var src2 = src.AsSpan();
            for (int i = 0; i < src2.Length; i++)
            {
                ref readonly var a = ref src2[i];
                ref var b = ref dest[i];

                if (char.IsLetterOrDigit(a))
                    b = a;
                else
                    b = '_';
            }
        });
    }
}

sealed class InlineLambdaVisitor(Dictionary<ParameterExpression, Expression> lookup) : ExpressionVisitor
{
    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (lookup.TryGetValue(node, out var replacement))
            return replacement;
        return node;
    }

    public static Expression Execute(InvocationExpression invocation)
    {
        if (invocation.Expression is not LambdaExpression lambda)
            throw new NotImplementedException($"Can only inline lambdas");

        foreach (var arg in invocation.Arguments)
            if (arg is not (ConstantExpression or ParameterExpression))
                throw new NotImplementedException("Can only inline primitive args");

        var args = invocation.Arguments;
        var parameters = lambda.Parameters;

        var lookup = Enumerable.Zip(parameters, args).ToDictionary();

        InlineLambdaVisitor visitor = new(lookup);
        return visitor.Visit(lambda.Body);
    }
}

sealed class FixLoopConditionVisitor : ExpressionVisitor
{
    public static BinaryExpression Execute(BinaryExpression expression)
    {
        if (expression is not
            {
                Left: UnaryExpression { Operand: var left, NodeType: ExpressionType.PostDecrementAssign },
                NodeType: ExpressionType.GreaterThan,
                Right: var right
            })
            return expression;

        return Expression.GreaterThanOrEqual(Expression.PreDecrementAssign(left), right);
    }
}
