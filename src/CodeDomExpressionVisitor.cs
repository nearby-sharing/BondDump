using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;
using static BondDump.SyntaxUtils;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace BondDump;

// Inspired by https://medium.com/@canerten/linq-expression-trees-lambdas-to-codedom-conversion-8e434aa06380

public sealed class CodeDomExpressionVisitor
{
    public static CodeDomExpressionVisitor Instance => field ??= new();

    string _baseName = "";
    ClassDeclarationSyntax _typeDeclaration = null!;
    Scope _currentScope;
    public CodeDomExpressionVisitor()
    {
        _currentScope = new() { Parent = null };
    }

    public MethodDeclarationSyntax Convert(LambdaExpression lambda, ClassDeclarationSyntax declaringType, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(lambda.TailCall, false);

        _baseName = name;
        _typeDeclaration = declaringType;
        _currentScope = new() { Parent = null };

        return MethodDeclaration(Type(lambda.ReturnType), name)
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters([.. lambda.Parameters
                .Select(x =>
                    Parameter(Identifier(x.Name))
                        .WithType(Type(x.Type))
                )
            ])
            .WithBody(Visit(lambda.Body).ToStatements());
    }

    CSharpSyntaxNode Visit(Expression exp) => exp switch
    {
        null => null!, // ToDo
        ConstantExpression constantExpression => VisitConstant(constantExpression),
        UnaryExpression unaryExpression => VisitUnary(unaryExpression),
        BinaryExpression binaryExpression => VisitBinary(binaryExpression),
        NewExpression newExpression => VisitNew(newExpression),
        IndexExpression indexExpression => VisitIndex(indexExpression),
        MethodCallExpression callExpression => VisitMethodCall(callExpression),
        InvocationExpression invocationExpression => VisitInvocation(invocationExpression),
        InvokeRecursiveExpression invokeRecursiveExpression => VisitRecursiveInvocation(invokeRecursiveExpression),
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

    private static ExpressionSyntax VisitConstant(ConstantExpression c)
    {
        if (c.Value == null)
        {
            return LiteralExpression(SyntaxKind.NullLiteralExpression);
        }
        else if (c.Value.GetType().IsPrimitive || c.Value.GetType() == typeof(string))
        {
            return LiteralExpression(c.Value);
        }
        else if (c.Value.GetType().IsEnum)
        {
            return MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                Type(c.Value.GetType()),
                IdentifierName(Enum.GetName(c.Value.GetType(), c.Value))
            );
        }
        else
        {
            // CompactBinary{Writer, Reader} does not use the metadata
            if (c.Type.FullName == "Bond.Metadata")
                return LiteralExpression(SyntaxKind.NullLiteralExpression);

            throw new NotImplementedException();
        }
    }

    private ExpressionSyntax VisitUnary(UnaryExpression unary)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(unary.Method, null);

        var operand = Visit(unary.Operand).ToExpression();
        return unary.NodeType switch
        {
            ExpressionType.Convert => CastExpression(Type(unary.Type), operand),
            ExpressionType.Increment => BinaryExpression(SyntaxKind.AddExpression, operand, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1))),
            ExpressionType.PreIncrementAssign => PrefixUnaryExpression(SyntaxKind.PreIncrementExpression, operand),
            ExpressionType.PreDecrementAssign => PrefixUnaryExpression(SyntaxKind.PreDecrementExpression, operand),
            ExpressionType.PostIncrementAssign => PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, operand),
            ExpressionType.PostDecrementAssign => PostfixUnaryExpression(SyntaxKind.PostDecrementExpression, operand),
            _ => throw new NotImplementedException($"Unary operation {unary.NodeType} not supported")
        };
    }

    private ExpressionSyntax VisitBinary(BinaryExpression binary)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(binary.Conversion, null);

        return BinaryExpressionEx(
            BindOperant(binary.NodeType),
            Visit(binary.Left).ToExpression(),
            Visit(binary.Right).ToExpression()
        );
    }

    private static SyntaxKind BindOperant(ExpressionType type) => type switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => SyntaxKind.AddExpression,
        ExpressionType.And => SyntaxKind.BitwiseAndExpression,
        ExpressionType.AndAlso => SyntaxKind.LogicalAndExpression,
        ExpressionType.Or => SyntaxKind.BitwiseOrExpression,
        ExpressionType.OrElse => SyntaxKind.LogicalOrExpression,
        ExpressionType.Equal => SyntaxKind.EqualsExpression,
        ExpressionType.NotEqual => SyntaxKind.NotEqualsExpression,
        ExpressionType.GreaterThan => SyntaxKind.GreaterThanExpression,
        ExpressionType.GreaterThanOrEqual => SyntaxKind.GreaterThanOrEqualExpression,
        ExpressionType.LessThan => SyntaxKind.LessThanExpression,
        ExpressionType.LessThanOrEqual => SyntaxKind.LessThanOrEqualExpression,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => SyntaxKind.MultiplyExpression,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => SyntaxKind.SubtractExpression,
        ExpressionType.Power or ExpressionType.Divide => SyntaxKind.DivideExpression,
        ExpressionType.Modulo => SyntaxKind.ModuloExpression,
        ExpressionType.Assign => SyntaxKind.SimpleAssignmentExpression,
        _ => throw new NotImplementedException($"Operator {type} is not implemented"),
    };

    private ObjectCreationExpressionSyntax VisitNew(NewExpression newExpression)
    {
        var args = VisitExpressionList(newExpression.Arguments);
        return ObjectCreationExpression(
            Type(newExpression.Type),
            ArgumentList(SeparatedList(args.Select(Argument))),
            initializer: null
        );
    }

    private ElementAccessExpressionSyntax VisitIndex(IndexExpression index) => ElementAccessExpression(
        expression: Visit(index.Object).ToExpression(),
        BracketedArgumentList(SeparatedList(index.Arguments.Select(Visit).Cast<ExpressionSyntax>().Select(Argument)))
    );

    private InvocationExpressionSyntax VisitMethodCall(MethodCallExpression m)
    {
        CSharpSyntaxNode obj = Visit(m.Object);
        var args = Enumerable.Zip(VisitExpressionList(m.Arguments), m.Method.GetParameters())
            .Select((arg) => arg.Second switch
            {
                { IsIn: true } => Argument(nameColon: null, Token(SyntaxKind.InKeyword), arg.First),
                { IsOut: true } => Argument(nameColon: null, Token(SyntaxKind.OutKeyword), arg.First),
                // ToDo: Ref??
                _ => Argument(arg.First),
            });

        if (obj == null)
        {
            //static method call
            return InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Type(m.Method.DeclaringType), IdentifierName(m.Method.Name)),
                ArgumentList(SeparatedList(args))
            );
        }
        else
        {
            return InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, obj.ToExpression(), IdentifierName(m.Method.Name)),
                ArgumentList(SeparatedList(args))
            );
        }
    }

    private CSharpSyntaxNode VisitInvocation(InvocationExpression invocation)
        => Visit(InlineLambdaVisitor.Execute(invocation));

    private InvocationExpressionSyntax VisitRecursiveInvocation(InvokeRecursiveExpression invocation)
    {
        if (invocation.LambdaIndex is not ConstantExpression { Value: int index })
            throw new InvalidOperationException("Recursive calls must use a constant index");

        return InvocationExpression(
            IdentifierName(index switch
            {
                0 => _baseName,
                _ => $"{_baseName}{index}"
            }),
            ArgumentList(SeparatedList(invocation.Arguments.Select(Visit).Cast<ExpressionSyntax>().Select(Argument)))
        );
    }

    private MemberAccessExpressionSyntax VisitMemberAccess(MemberExpression member)
    {
        var receiver = Visit(member.Expression).ToExpression();
        return member switch
        {
            { Member: FieldInfo field } => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName(field.Name)),
            { Member: PropertyInfo property } => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName(property.Name)),
            _ => throw new NotImplementedException()
        };
    }

    private static CSharpSyntaxNode VisitDefault(DefaultExpression expr)
    {
        if (expr.Type == typeof(void))
            return Block();

        return DefaultExpression(Type(expr.Type));
    }

    private StatementSyntax VisitGoTo(GotoExpression exp) => exp.Kind switch
    {
        GotoExpressionKind.Goto => GotoStatement(SyntaxKind.GotoStatement, IdentifierName(exp.Target.Name)),
        GotoExpressionKind.Return => ReturnStatement(Visit(exp.Value).ToExpression()),
        GotoExpressionKind.Break => BreakStatement(),
        GotoExpressionKind.Continue => ContinueStatement(),
        _ => throw new NotImplementedException(),
    };

    private IfStatementSyntax VisitConditional(ConditionalExpression c) => IfStatement(
        Visit(c.Test).ToExpression(),
        Visit(c.IfTrue).ToStatements(),
        ElseClause(Visit(c.IfFalse).ToStatements())
    );

    private WhileStatementSyntax VisitLoop(LoopExpression loop) => WhileStatement(
        condition: LiteralExpression(SyntaxKind.TrueLiteralExpression),
        Visit(loop.Body).ToStatements()
    );

    private TryStatementSyntax VisitTry(TryExpression exp)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(exp.Handlers.Count, 0);
        ArgumentOutOfRangeException.ThrowIfNotEqual(exp.Fault, null);

        return TryStatement(
            Visit(exp.Body).ToStatements(),
            List<CatchClauseSyntax>(),
            FinallyClause(Visit(exp.Finally).ToStatements())
        );
    }

    private BlockSyntax VisitBlock(BlockExpression block)
    {
        var oldScope = _currentScope;
        _currentScope = new() { Parent = oldScope, Variables = [.. block.Variables.Select(x => x.Name).WhereNotNull()] };
        try
        {
            List<StatementSyntax> statements = [];
            foreach (var variable in block.Variables)
            {
                statements.Add(LocalDeclarationStatement(VariableDeclaration(
                    Type(variable.Type),
                    SeparatedList([VariableDeclarator(_currentScope.Mangle(variable.Name))])
                )));
            }
            foreach (var expr in block.Expressions)
            {
                statements.AddRange(Visit(expr).ToStatements().Statements);
            }
            return Block(statements);
        }
        finally
        {
            _currentScope = oldScope;
        }
    }

    private IEnumerable<ExpressionSyntax> VisitExpressionList(ReadOnlyCollection<Expression> original)
        => original.Select(Visit).Cast<ExpressionSyntax>();

    private IdentifierNameSyntax VisitParameter(ParameterExpression p)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p.IsByRef, false);

        return IdentifierName(_currentScope.Mangle(p.Name));
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
    protected override Expression VisitLambda<T>(Expression<T> node)
        => throw new NotSupportedException("Cannot invoke lambda inside a lambda");

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

        // ToDo: Ignoring this check may change behavior!!
        //foreach (var arg in invocation.Arguments)
        //    if (arg is not (ConstantExpression or ParameterExpression))
        //        throw new NotImplementedException("Can only inline primitive args");

        var args = invocation.Arguments;
        var parameters = lambda.Parameters;

        var lookup = Enumerable.Zip(parameters, args).ToDictionary();

        InlineLambdaVisitor visitor = new(lookup);
        return visitor.Visit(lambda.Body);
    }
}
